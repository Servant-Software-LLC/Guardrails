using System.Text.Json;
using Guardrails.Core.Journal;

namespace Guardrails.Cli.Commands;

/// <summary>
/// Issue #704 — records that a <c>guardrails run</c> ENDED, for the owner its load claimed
/// (<see cref="RunJournal.LoadOrCreateForRun"/>).
/// <para>
/// <see cref="RecordEnd"/> is called right after the run-finished event, so the record lands as soon as the run's
/// verdict is settled — not after a log-server drain or a worktree sweep the verdict never waited on, during which a
/// killed process would read EXITED WITHOUT FINISHING and a live one RUNNING. The instance is also held by a
/// <c>using</c> declaration, so the early returns that never reach that event (a failed plan preflight, a declined drift
/// prompt, the MAX_PATH preflight) record their end on the way out; a second call is a no-op. None of this happens on
/// the ways out that do not unwind — a killed process, a hard crash, a machine that reboots under the run — and that
/// difference is the signal <c>guardrails status</c> reads.
/// </para>
/// <para>
/// Best-effort, like <see cref="RunJournal.RecordDelivery"/>: a journal fault never changes a run's verdict. Never
/// silent: a write that fails prints one warning line saying what <c>status</c> will get wrong.
/// </para>
/// </summary>
internal sealed class RunOwnership : IDisposable
{
    private readonly RunJournal _journal;
    private readonly RunOwner? _owner;
    private readonly TextWriter _output;
    private bool _ended;

    /// <param name="journal">The run's own journal instance.</param>
    /// <param name="owner">The owner its load claimed; null when the run could not read its identity (nothing to end).</param>
    /// <param name="output">Where a failed end record is reported.</param>
    public RunOwnership(RunJournal journal, RunOwner? owner, TextWriter output)
    {
        _journal = journal;
        _owner = owner;
        _output = output;
    }

    /// <summary>Record that this run ended — once; later calls do nothing.</summary>
    public void RecordEnd()
    {
        if (_ended || _owner is not { } owner)
        {
            return;
        }

        _ended = true;
        try
        {
            _journal.RecordOwnerFinished(owner, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _output.WriteLine(
                $"WARNING: could not record this run's end in run.json ({ex.GetType().Name}); once this process exits, "
                + "`guardrails status` will report the run as EXITED WITHOUT FINISHING (issue #704).");
        }
    }

    /// <summary>The backstop for every way out of the run that never reached the run-finished event.</summary>
    public void Dispose() => RecordEnd();
}
