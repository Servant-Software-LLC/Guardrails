using System.Text.Json;
using Guardrails.Core.Journal;

namespace Guardrails.Cli.Commands;

/// <summary>
/// Issue #704 — <c>guardrails run</c>'s claim on its journal. It names THIS process as the run's owner when the
/// run starts, and records that the run ENDED when disposed.
/// <para>
/// Held by a <c>using</c> declaration in <c>RunCommand.RunAsync</c>, so the end is recorded on every way out of a
/// run that unwinds — a green finish, a halt, an early return, cancellation, a fault the harness surfaces — and on
/// none of the ways that do not: a killed process, a hard crash, a machine that reboots under the run. That
/// difference is the whole signal <c>guardrails status</c> reads.
/// </para>
/// <para>
/// Both writes are best-effort, like <see cref="RunJournal.RecordDelivery"/>'s: a journal fault must never turn a
/// run's verdict into a harness error. A lost write costs a less certain status line, never a wrong run.
/// </para>
/// </summary>
internal sealed class RunOwnership : IDisposable
{
    private readonly RunJournal _journal;
    private readonly RunOwner _owner;

    private RunOwnership(RunJournal journal, RunOwner owner)
    {
        _journal = journal;
        _owner = owner;
    }

    /// <summary>
    /// Claim <paramref name="journal"/> for this process. Call it immediately after
    /// <see cref="RunJournal.LoadOrCreate"/> and before the Scheduler's own, later load, which carries the claim
    /// forward from disk — the same ordering rule as <see cref="RunJournal.RecordEnvironment"/>.
    /// </summary>
    public static RunOwnership Claim(RunJournal journal)
    {
        RunOwner owner = RunLiveness.OwnerForThisProcess();
        BestEffort(() => journal.RecordOwner(owner));
        return new RunOwnership(journal, owner);
    }

    /// <summary>Record that this process reached the end of its run — the run's last journal write.</summary>
    public void Dispose() => BestEffort(() => _journal.RecordOwnerFinished(_owner, DateTimeOffset.UtcNow));

    private static void BestEffort(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The run's outcome stands; `status` reads a less certain line (see the type remarks).
        }
    }
}
