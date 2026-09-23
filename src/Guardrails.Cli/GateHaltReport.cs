using Guardrails.Core.Execution;
using Guardrails.Core.Journal;

namespace Guardrails.Cli;

/// <summary>
/// The console block for a run that stopped at a plan-scoped gate, rendered from the <c>halt</c> record the gate
/// just wrote to <c>state/run.json</c> (#762, SSOT §7). Before this, a failed plan preflight printed one generic
/// sentence and a pointer to <c>run.json</c>: the check's name, its reason and the captured-output directory were
/// all computed and journaled, and none of it reached the one surface CI keeps. The headline was also cut short of
/// the <c>: &lt;check-name&gt;</c> it carries in the record.
/// <para>
/// Shape, in order: the record's own headline; one block per failed check (its name, then its FULL reason,
/// indented); the <c>logDir</c> when one was captured; and the <c>run.json</c> pointer LAST, so the file is where
/// the operator goes for more, not the only place the cause exists. A reason longer than the harness's output-tail
/// cap (<see cref="OutputTail"/>, the same cut <c>feedback.md</c> uses) is shown as its tail, with a marker saying
/// so. Lines are plain text, not Spectre markup: every caller runs before any live region exists or after it has
/// closed.
/// </para>
/// </summary>
public static class GateHaltReport
{
    /// <summary>Write the block for <paramref name="halt"/>; <paramref name="journalPath"/> is <c>state/run.json</c>.</summary>
    public static void Write(RunHalt halt, string journalPath, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine(halt.Headline);

        if (halt.FailedChecks.Count > 0)
        {
            output.WriteLine();
            foreach (FailedGuardrail check in halt.FailedChecks)
            {
                WriteCheck(check, output);
            }
        }

        output.WriteLine();
        if (!string.IsNullOrEmpty(halt.LogDir))
        {
            output.WriteLine($"  Logs:  {halt.LogDir}");
        }

        output.WriteLine($"  State: {journalPath} (\"{SectionFor(halt.Kind)}\")");
    }

    /// <summary>
    /// Read the <c>halt</c> record at <paramref name="journalPath"/> and write its block. Returns false — having
    /// written nothing — when there is no readable halt, so the caller can print its own fallback pointer.
    /// </summary>
    public static bool TryWriteFromJournal(string journalPath, TextWriter output)
    {
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

        Write(halt, journalPath, output);
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
