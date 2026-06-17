using System.CommandLine;
using Guardrails.Core.Breakdown;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails lock [folder] [--check] [--diff]</c> — record or compare the breakdown
/// manifest (SSOT §11). Default: capture the authored files and write
/// <c>&lt;folder&gt;/guardrails.baseline</c> (the BASE a later <c>/plan-breakdown</c> regeneration
/// diffs against). <c>--check</c> reports drift via exit code (0 clean, 2 drifted/missing baseline,
/// 1 corrupt baseline). <c>--diff</c> prints the per-file classification (human edits/additions/
/// deletions). Defaults to the current directory when the folder is omitted. This is a pure
/// content snapshot — it does not load or validate the plan; run <c>guardrails validate</c> for
/// that. (The command verb stays <c>lock</c> — it WRITES the baseline; only the file it produces
/// was renamed from <c>guardrails.lock</c>, see issue #10.)
/// </summary>
public static class LockCommand
{
    /// <summary>
    /// Exit code returned by <c>--check</c> when the folder has drifted from
    /// <c>guardrails.baseline</c> OR the baseline is missing — the "re-baseline" signal (SSOT §7:
    /// exit 2 = "the operation completed but an actionable condition was found"). Distinct from
    /// <see cref="ExitCodes.HarnessError"/> (1), which a genuine failure (missing folder, corrupt
    /// baseline) returns, so CI can tell "re-run guardrails lock" apart from "the tool failed".
    /// Mirrors <c>graph --check</c>'s stale signal: deliberately NOT added to the shared
    /// <see cref="ExitCodes"/> class — it shares the numeric value of
    /// <see cref="ExitCodes.TaskFailed"/> (2) by design (both are the §7 "actionable condition
    /// found" code) but is a baseline-specific meaning, so it lives here next to its only caller.
    /// </summary>
    private const int DriftExitCode = 2;

    public static Command Create(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        var checkOption = new Option<bool>("--check")
        {
            Description = "Report whether the folder matches guardrails.baseline (exit 0 clean, 2 drifted/missing, 1 corrupt); writes nothing."
        };

        var diffOption = new Option<bool>("--diff")
        {
            Description = "Print the per-file classification (edited/added/missing) against guardrails.baseline; writes nothing."
        };

        var command = new Command("lock", "Record or compare a plan folder's breakdown manifest (guardrails.baseline).");
        command.Add(folderArgument);
        command.Add(checkOption);
        command.Add(diffOption);

        command.SetAction(parseResult =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out);
            bool check = parseResult.GetValue(checkOption);
            bool diff = parseResult.GetValue(diffOption);
            return Execute(folder, check, diff, io);
        });

        return command;
    }

    private static int Execute(string folder, bool check, bool diff, IConsoleIo io)
    {
        TextWriter output = io.Out;

        if (!Directory.Exists(folder))
        {
            output.WriteLine($"Plan folder does not exist: {folder}");
            return ExitCodes.HarnessError;
        }

        if (check)
        {
            return Check(folder, output);
        }

        if (diff)
        {
            return Diff(folder, output);
        }

        return Write(folder, output);
    }

    private static int Write(string folder, TextWriter output)
    {
        BreakdownManifest manifest = BreakdownManifest.Capture(folder);
        manifest.Write(folder);
        output.WriteLine($"Wrote {BreakdownManifest.BaselineFilePath(folder)} ({manifest.Files.Count} file(s))");

        // A committed baseline is a path→SHA-256 manifest; its high-entropy hashes trip generic
        // secret scanners (issue #67). DETECT whether the repo's ggshield config already excludes
        // baseline files and, if not, PRINT a copy-pasteable suggestion. This is advisory only — it
        // never edits the user's scanner config and never affects the exit code.
        GitGuardianConfig.SuggestBaselineExclusion(folder, output);
        return ExitCodes.Success;
    }

    /// <summary>
    /// <c>--check</c>: a boolean gate. A missing baseline or any drift → one actionable line and
    /// exit <see cref="DriftExitCode"/> (2, the "re-baseline" signal). A corrupt baseline (present
    /// but unparseable) → exit 1, a genuine error. Clean → exit 0.
    /// </summary>
    private static int Check(string folder, TextWriter output)
    {
        BreakdownManifest? baseManifest = ReadBase(folder, output, out int errorCode);
        if (baseManifest is null)
        {
            return errorCode;
        }

        BreakdownDiff diff = BreakdownDiff.Compute(baseManifest, BreakdownManifest.Capture(folder));
        if (!diff.HasDrift)
        {
            return ExitCodes.Success;
        }

        int changed = diff.Edited.Count() + diff.Added.Count() + diff.Missing.Count();
        output.WriteLine(
            $"{BreakdownManifest.FileName} is stale — {changed} change(s) since last lock; run: guardrails lock {QuoteIfNeeded(folder)}");
        return DriftExitCode;
    }

    /// <summary>
    /// <c>--diff</c>: a report. Prints one line per changed file (EDITED/ADDED/MISSING) and exits
    /// 0 (printing the report IS the success, drift or not). A missing baseline → exit
    /// <see cref="DriftExitCode"/> (2, "run guardrails lock first" — there is no BASE to diff
    /// against); a corrupt baseline → exit 1.
    /// </summary>
    private static int Diff(string folder, TextWriter output)
    {
        BreakdownManifest? baseManifest = ReadBase(folder, output, out int errorCode);
        if (baseManifest is null)
        {
            return errorCode;
        }

        BreakdownDiff diff = BreakdownDiff.Compute(baseManifest, BreakdownManifest.Capture(folder));
        if (!diff.HasDrift)
        {
            output.WriteLine("No changes since last baseline.");
            return ExitCodes.Success;
        }

        foreach (string path in diff.Edited)
        {
            output.WriteLine($"EDITED   {path}");
        }
        foreach (string path in diff.Added)
        {
            output.WriteLine($"ADDED    {path}");
        }
        foreach (string path in diff.Missing)
        {
            output.WriteLine($"MISSING  {path}");
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// Load the BASE manifest, distinguishing the two failure modes <c>--check</c> and
    /// <c>--diff</c> share: a missing baseline is the actionable "re-baseline" signal
    /// (<see cref="DriftExitCode"/>, 2), while a present-but-corrupt baseline is a genuine error
    /// (<see cref="ExitCodes.HarnessError"/>, 1). On success returns the manifest and sets
    /// <paramref name="errorCode"/> to <see cref="ExitCodes.Success"/>; on failure returns null
    /// and sets the code the caller should return.
    /// </summary>
    private static BreakdownManifest? ReadBase(string folder, TextWriter output, out int errorCode)
    {
        BreakdownManifest? baseManifest = BreakdownManifest.Read(folder);
        if (baseManifest is not null)
        {
            errorCode = ExitCodes.Success;
            return baseManifest;
        }

        if (File.Exists(BreakdownManifest.BaselineFilePath(folder)))
        {
            output.WriteLine(
                $"{BreakdownManifest.FileName} is corrupt (could not be parsed) — run: guardrails lock {QuoteIfNeeded(folder)}");
            errorCode = ExitCodes.HarnessError;
            return null;
        }

        output.WriteLine($"{BreakdownManifest.FileName} missing — run: guardrails lock {QuoteIfNeeded(folder)}");
        errorCode = DriftExitCode;
        return null;
    }

    private static string QuoteIfNeeded(string path) =>
        path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
}
