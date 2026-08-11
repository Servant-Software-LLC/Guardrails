using System.Text.Json;
using Guardrails.Core.Journal;
using Guardrails.Core.State;

namespace Guardrails.Cli;

/// <summary>
/// Persists the OPTIONAL top-level <c>run.json</c> sections the two CLI-driven plan phases own —
/// <c>planPreflights</c> (<see cref="PlanPreflightPhase"/>), <c>planGuardrails</c>
/// (<see cref="PlanGuardrailPhase"/>) and the shared <c>halt</c> record (issue #432).
/// <para>
/// These are written STRAIGHT to disk rather than through a <see cref="RunJournal"/> mutator because the
/// journal type exposes none for them and the phases run OUTSIDE the Scheduler's journal lifetime: the
/// pre-DAG phase runs before the Scheduler loads its own journal, and the terminal phase after it has
/// finished with it. Each update re-reads the document currently on disk and replaces only the named
/// field(s), leaving everything the Scheduler wrote untouched, then writes atomically.
/// </para>
/// </summary>
internal static class PlanPhaseJournalWriter
{
    /// <summary>
    /// Apply <paramref name="update"/> to the journal document currently on disk for
    /// <paramref name="planDirectory"/> and persist the result atomically.
    /// </summary>
    public static void Update(string planDirectory, Func<JournalDocument, JournalDocument> update)
    {
        string path = RunJournal.PathFor(planDirectory);
        JournalDocument document = JournalReader.Read(path);
        string json = JsonSerializer.Serialize(update(document), JournalJson.Options);
        AtomicFile.WriteAllText(path, json);
    }
}
