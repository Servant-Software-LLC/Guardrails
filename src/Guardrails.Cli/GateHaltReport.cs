using Guardrails.Core.Execution;
using Guardrails.Core.Journal;

namespace Guardrails.Cli;

/// <summary>
/// The console block for a run that stopped at a plan-scoped gate, rendered from the <c>halt</c> record the gate
/// just wrote to <c>state/run.json</c> (#762, SSOT §8). Before this, a failed plan preflight printed one generic
/// sentence and a pointer to <c>run.json</c>: the check's name, its reason and the captured-output directory were
/// all computed and journaled, and none of it reached the one surface CI keeps. The headline was also cut short of
/// the <c>: &lt;check-name&gt;</c> it carries in the record.
/// <para>
/// Shape, in order: a lead line (the record's own headline, or the caller's); one block per failed check (its
/// name, then its FULL reason, indented); the absolute captured-output directory when one was captured; and the
/// absolute <c>run.json</c> pointer LAST, so the file is where the operator goes for more, not the only place the
/// cause exists. A reason longer than the harness's output-tail cap (<see cref="OutputTail"/>, the same cut
/// <c>feedback.md</c> uses) is shown as its tail, with a marker saying so. Lines are plain text, not Spectre markup:
/// every caller runs before any live region exists or after it has closed.
/// </para>
/// </summary>
public static class GateHaltReport
{
    /// <summary>
    /// Read the <c>halt</c> record of the plan at <paramref name="planDirectory"/> and write its block. Returns
    /// false, having written nothing, when there is no usable record — none on disk, an unreadable journal, or a
    /// hand-edited one whose headline or checks are missing — so the caller can print its own fallback.
    /// </summary>
    /// <param name="leadLine">
    /// Printed in place of the record's headline. <c>revalidate</c> passes its own, because the recorded headline
    /// says "halting before scheduling any task", which is untrue of a verb that schedules nothing.
    /// </param>
    /// <param name="checksAlreadyPrinted">
    /// True when the source already printed every finding itself (see
    /// <see cref="PlanPreflightPhase.HaltHasOwnConsoleReport"/>): the block is then reduced to the lead line, if
    /// one was given, and the trailing pointers, so no finding is printed twice.
    /// </param>
    public static bool TryWriteFromJournal(
        string planDirectory, TextWriter output, string? leadLine = null, Func<RunHalt, bool>? checksAlreadyPrinted = null)
    {
        string journalPath = RunJournal.PathFor(planDirectory);
        RunHalt? halt;
        try
        {
            halt = JournalReader.Read(journalPath).Halt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }

        if (halt is null)
        {
            return false;
        }

        bool pointersOnly = checksAlreadyPrinted?.Invoke(halt) ?? false;
        return Write(halt, planDirectory, output, leadLine, pointersOnly);
    }

    /// <summary>
    /// Write the block for <paramref name="halt"/>. Returns false, having written nothing, when the record has
    /// nothing usable to show: no lead line (neither <paramref name="leadLine"/> nor a recorded headline) or, unless
    /// <paramref name="pointersOnly"/>, no failed check with a name. A hand-edited journal can carry
    /// <c>"failedChecks": null</c> or a null element despite the model's non-null types; those are skipped, never
    /// dereferenced.
    /// </summary>
    public static bool Write(RunHalt halt, string planDirectory, TextWriter output, string? leadLine = null, bool pointersOnly = false)
    {
        string? lead = string.IsNullOrWhiteSpace(leadLine) ? halt.Headline : leadLine;
        List<FailedGuardrail> checks = (halt.FailedChecks ?? [])
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Name))
            .ToList();

        if (!pointersOnly && (string.IsNullOrWhiteSpace(lead) || checks.Count == 0))
        {
            return false;
        }

        if (!pointersOnly || !string.IsNullOrWhiteSpace(leadLine))
        {
            output.WriteLine();
            output.WriteLine(lead);
        }

        if (!pointersOnly)
        {
            output.WriteLine();
            foreach (FailedGuardrail check in checks)
            {
                WriteCheck(check, output);
            }
        }

        output.WriteLine();
        if (!string.IsNullOrEmpty(halt.LogDir))
        {
            output.WriteLine($"  Logs:  {Path.GetFullPath(Path.Combine(planDirectory, halt.LogDir))}");
        }

        output.WriteLine($"  State: {RunJournal.PathFor(planDirectory)} (\"{SectionFor(halt.Kind)}\")");
        return true;
    }

    private static void WriteCheck(FailedGuardrail check, TextWriter output)
    {
        output.WriteLine($"  FAILED: {check.Name}");

        if (string.IsNullOrWhiteSpace(check.Reason))
        {
            output.WriteLine("    (no reason recorded)");
            return;
        }

        (string reason, bool truncated) = OutputTail.Take(check.Reason);
        if (truncated)
        {
            // The tail is kept, so what was dropped is the START; say so before the text rather than after it.
            output.WriteLine(
                $"    [earlier output omitted: showing the last {OutputTail.MaxLines} lines / {OutputTail.MaxChars} characters; "
                + "the full reason is in run.json]");
        }

        foreach (string line in reason.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            output.WriteLine(line.Length == 0 ? string.Empty : $"    {line.TrimEnd()}");
        }
    }

    /// <summary>The <c>run.json</c> section holding the per-check detail for each gate kind (SSOT §7).</summary>
    private static string SectionFor(RunHaltKind kind) => kind switch
    {
        RunHaltKind.PlanPreflightFailed => "planPreflights",
        RunHaltKind.PlanGuardrailFailed => "planGuardrails",
        _ => "waves"
    };
}
