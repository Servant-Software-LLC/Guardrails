using System.CommandLine;
using Guardrails.Core.Breakdown;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails lock [folder] [--check] [--diff]</c> — record or compare the breakdown
/// manifest (SSOT §10). Default: capture the authored files and write
/// <c>&lt;folder&gt;/guardrails.lock</c> (the BASE a later <c>/plan-breakdown</c> regeneration
/// diffs against). <c>--check</c> reports drift via exit code (0 clean, 1 drifted/missing).
/// <c>--diff</c> prints the per-file classification (human edits/additions/deletions). Defaults
/// to the current directory when the folder is omitted. This is a pure content snapshot — it
/// does not load or validate the plan; run <c>guardrails validate</c> for that.
/// </summary>
public static class LockCommand
{
    public static Command Create()
    {
        var folderArgument = FolderArgument.Create();

        var checkOption = new Option<bool>("--check")
        {
            Description = "Report whether the folder matches guardrails.lock (exit 0 clean, 1 drifted/missing); writes nothing."
        };

        var diffOption = new Option<bool>("--diff")
        {
            Description = "Print the per-file classification (edited/added/missing) against guardrails.lock; writes nothing."
        };

        var command = new Command("lock", "Record or compare a plan folder's breakdown manifest (guardrails.lock).");
        command.Add(folderArgument);
        command.Add(checkOption);
        command.Add(diffOption);

        command.SetAction(parseResult =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument));
            bool check = parseResult.GetValue(checkOption);
            bool diff = parseResult.GetValue(diffOption);
            return Execute(folder, check, diff);
        });

        return command;
    }

    private static int Execute(string folder, bool check, bool diff)
    {
        if (!Directory.Exists(folder))
        {
            Console.WriteLine($"Plan folder does not exist: {folder}");
            return ExitCodes.HarnessError;
        }

        if (check)
        {
            return Check(folder);
        }

        if (diff)
        {
            return Diff(folder);
        }

        return Write(folder);
    }

    private static int Write(string folder)
    {
        BreakdownManifest manifest = BreakdownManifest.Capture(folder);
        manifest.Write(folder);
        Console.WriteLine($"Wrote {BreakdownManifest.LockFilePath(folder)} ({manifest.Files.Count} file(s))");
        return ExitCodes.Success;
    }

    /// <summary>
    /// <c>--check</c>: a boolean gate. Missing lock or any drift → one actionable line and
    /// exit 1; otherwise exit 0.
    /// </summary>
    private static int Check(string folder)
    {
        BreakdownManifest? baseManifest = BreakdownManifest.Read(folder);
        if (baseManifest is null)
        {
            Console.WriteLine($"{BreakdownManifest.FileName} missing — run: guardrails lock {QuoteIfNeeded(folder)}");
            return ExitCodes.HarnessError;
        }

        BreakdownDiff diff = BreakdownDiff.Compute(baseManifest, BreakdownManifest.Capture(folder));
        if (!diff.HasDrift)
        {
            return ExitCodes.Success;
        }

        int changed = diff.Edited.Count() + diff.Added.Count() + diff.Missing.Count();
        Console.WriteLine(
            $"{BreakdownManifest.FileName} is stale — {changed} change(s) since last lock; run: guardrails lock {QuoteIfNeeded(folder)}");
        return ExitCodes.HarnessError;
    }

    /// <summary>
    /// <c>--diff</c>: a report. Prints one line per changed file (EDITED/ADDED/MISSING) and
    /// exits 0; a missing lock is the one error (exit 1) since there is no BASE to diff against.
    /// </summary>
    private static int Diff(string folder)
    {
        BreakdownManifest? baseManifest = BreakdownManifest.Read(folder);
        if (baseManifest is null)
        {
            Console.WriteLine($"{BreakdownManifest.FileName} missing — run: guardrails lock {QuoteIfNeeded(folder)}");
            return ExitCodes.HarnessError;
        }

        BreakdownDiff diff = BreakdownDiff.Compute(baseManifest, BreakdownManifest.Capture(folder));
        if (!diff.HasDrift)
        {
            Console.WriteLine("No changes since last lock.");
            return ExitCodes.Success;
        }

        foreach (string path in diff.Edited)
        {
            Console.WriteLine($"EDITED   {path}");
        }
        foreach (string path in diff.Added)
        {
            Console.WriteLine($"ADDED    {path}");
        }
        foreach (string path in diff.Missing)
        {
            Console.WriteLine($"MISSING  {path}");
        }

        return ExitCodes.Success;
    }

    private static string QuoteIfNeeded(string path) =>
        path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
}
