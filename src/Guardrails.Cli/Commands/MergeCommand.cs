using System.CommandLine;
using Guardrails.Core.Breakdown;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails merge [folder] --remote &lt;dir&gt; [--apply]</c> — the identity-aware
/// regeneration merge (SSOT §11.3). <paramref>folder</paramref> is the current plan folder
/// (LOCAL, carrying <c>guardrails.lock</c> = BASE); <c>--remote</c> is a freshly generated
/// candidate (REMOTE) the skill staged from the changed plan. The merge preserves human
/// guardrail edits and re-derives everything else, matching tasks by <c>stableId</c>.
///
/// Default (dry run) reports the resolutions and writes nothing. <c>--apply</c> materializes the
/// merge in place and re-locks — but only when there are no conflicts. Exit codes follow §7:
/// <c>0</c> clean (dry run with no conflicts, or applied); <c>2</c> the actionable "human must
/// act" signal (conflicts to resolve, or a missing lock to establish first); <c>1</c> a genuine
/// error (missing folder/remote, corrupt lock, or an invalid plan on either side).
/// </summary>
public static class MergeCommand
{
    /// <summary>
    /// Exit code for an actionable "a human must act" outcome — unresolved conflicts, or a
    /// missing BASE lock. Shares the numeric value of <see cref="ExitCodes.TaskFailed"/> (2, the
    /// §7 "actionable condition found" code) but carries a merge-specific meaning, so it lives
    /// here next to its only caller (mirroring <c>lock</c>/<c>graph --check</c>).
    /// </summary>
    private const int ActionNeededExitCode = 2;

    public static Command Create(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        var remoteOption = new Option<string>("--remote")
        {
            Description = "Path to the freshly generated candidate breakdown (REMOTE) to merge in.",
            Required = true
        };

        var applyOption = new Option<bool>("--apply")
        {
            Description = "Apply the merge in place and re-lock (only when there are no conflicts); otherwise dry-run report."
        };

        var command = new Command("merge",
            "Merge a freshly regenerated breakdown into the current folder, preserving human guardrail edits.");
        command.Add(folderArgument);
        command.Add(remoteOption);
        command.Add(applyOption);

        command.SetAction(parseResult =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out);
            string remote = parseResult.GetValue(remoteOption)!;
            bool apply = parseResult.GetValue(applyOption);
            return Execute(folder, remote, apply, io);
        });

        return command;
    }

    private static int Execute(string folder, string remote, bool apply, IConsoleIo io)
    {
        TextWriter output = io.Out;

        if (!Directory.Exists(folder))
        {
            output.WriteLine($"Plan folder does not exist: {folder}");
            return ExitCodes.HarnessError;
        }

        if (!Directory.Exists(remote))
        {
            output.WriteLine($"Remote (candidate) folder does not exist: {remote}");
            return ExitCodes.HarnessError;
        }

        // BASE: a missing lock is the actionable "lock first" signal (2); a corrupt one is an error (1).
        BreakdownManifest? baseManifest = BreakdownManifest.Read(folder);
        if (baseManifest is null)
        {
            if (File.Exists(BreakdownManifest.LockFilePath(folder)))
            {
                output.WriteLine(
                    $"{BreakdownManifest.FileName} is corrupt (could not be parsed) — run: guardrails lock {QuoteIfNeeded(folder)}");
                return ExitCodes.HarnessError;
            }

            output.WriteLine(
                $"{BreakdownManifest.FileName} missing — run 'guardrails lock {QuoteIfNeeded(folder)}' first to adopt the current folder as BASE.");
            return ActionNeededExitCode;
        }

        // Both sides must load + validate (this is also where a duplicate stableId / GR2010 surfaces).
        if (!TryLoad(folder, "current", output, out PlanProbe.Result localProbe))
        {
            return ExitCodes.HarnessError;
        }
        if (!TryLoad(remote, "remote", output, out PlanProbe.Result remoteProbe))
        {
            return ExitCodes.HarnessError;
        }

        MergePlan plan = BreakdownMerge.Compute(
            baseManifest,
            localProbe.Plan!, BreakdownManifest.Capture(folder),
            remoteProbe.Plan!, BreakdownManifest.Capture(remote));

        Report(plan, output);

        if (plan.HasConflicts)
        {
            output.WriteLine(
                $"Blocked: {plan.Conflicts.Count()} conflict(s) must be resolved by a human before applying.");
            return ActionNeededExitCode;
        }

        if (!apply)
        {
            output.WriteLine("Dry run: no conflicts. Re-run with --apply to merge.");
            return ExitCodes.Success;
        }

        BreakdownMerge.Apply(plan, folder, remote);
        output.WriteLine(
            $"Applied: {plan.PreservedCount} human guardrail(s) preserved, {plan.DroppedCount} dropped; re-locked {BreakdownManifest.LockFilePath(folder)}.");
        return ExitCodes.Success;
    }

    private static bool TryLoad(string folder, string label, TextWriter output, out PlanProbe.Result probe)
    {
        probe = PlanProbe.LoadAndValidate(folder);
        if (probe.Plan is null || probe.HasErrors)
        {
            output.WriteLine($"The {label} plan at {folder} is not valid:");
            PlanProbe.PrintDiagnostics(probe.Diagnostics, output);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Print the human-facing decisions: conflicts and preserved/dropped guardrails (the
    /// non-trivial outcomes) plus warnings and a one-line summary. <c>TakeRemote</c> is the
    /// quiet default and is summarized as a count rather than line-by-line.
    /// </summary>
    private static void Report(MergePlan plan, TextWriter output)
    {
        foreach (GuardrailMergeItem item in plan.Items)
        {
            string? label = item.Action switch
            {
                GuardrailMergeAction.Conflict => "CONFLICT",
                GuardrailMergeAction.KeepLocal => "KEEP",
                GuardrailMergeAction.Drop => "DROP",
                _ => null // TakeRemote: summarized below, not listed.
            };

            if (label is not null)
            {
                output.WriteLine($"{label,-9} {item.TaskIdentity}/{item.GuardrailFile} — {item.Reason}");
            }
        }

        foreach (string warning in plan.Warnings)
        {
            output.WriteLine($"warning: {warning}");
        }

        int takeRemote = plan.Items.Count(i => i.Action == GuardrailMergeAction.TakeRemote);
        output.WriteLine(
            $"Merge: {plan.PreservedCount} preserved, {plan.DroppedCount} dropped, {plan.Conflicts.Count()} conflict(s), {takeRemote} from regeneration ({plan.Items.Count} guardrails total).");
    }

    private static string QuoteIfNeeded(string path) =>
        path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
}
