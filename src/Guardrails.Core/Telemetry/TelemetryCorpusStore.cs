using System.Text.Json;

namespace Guardrails.Core.Telemetry;

/// <summary>
/// The append-only local telemetry corpus (charter §9, <c>model-evidence-and-graduation</c>): one
/// JSONL file per UTC month, under a corpus root the CALLER supplies. This type never resolves the
/// default <c>~/.guardrails/telemetry/</c> location itself — that belongs to the CLI (task 10), so the
/// same store can be pointed at a throwaway directory in tests, at a bench's sandboxed root (charter §7),
/// or at the real home-scoped corpus in production without three different code paths.
///
/// <para><b>STUB (#535, task 01).</b> Every member throws <see cref="NotImplementedException"/>.
/// <c>02-implement-corpus-store</c> fills the behaviour; <c>TelemetryCorpusStoreTests</c> — authored
/// alongside this stub — is the specification.</para>
/// </summary>
public sealed class TelemetryCorpusStore
{
    /// <summary>
    /// The opt-out switch (charter §9's collection-default decision): <c>GUARDRAILS_TELEMETRY=off</c>
    /// disables collection entirely (no files written, not even an empty corpus root); any other value,
    /// or unset, means collection stays ON. This is the SINGLE definition for the whole plan — the CLI
    /// verb (task 10) and run-end ingest (task 13) both honour this by calling into the store rather than
    /// re-reading the environment themselves, so a machine cannot end up opted out of one path and not
    /// the other.
    /// </summary>
    public const string OptOutEnvVar = "GUARDRAILS_TELEMETRY";

    /// <summary>
    /// The wire-format options every row is written and read with — camelCase to match the field names
    /// charter §9 already spells (<c>schemaVersion</c>, <c>taskId</c>, …), the same convention
    /// <c>Guardrails.Core.Journal.JournalJson</c> uses for <c>run.json</c>. Internal (not private) so the
    /// implementation, the tests, and later the ETL/report tasks share exactly one definition rather than
    /// each inventing their own and drifting.
    /// </summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>The corpus root this store was constructed with, verbatim.</summary>
    public string CorpusRoot { get; }

    /// <summary>
    /// <paramref name="corpusRoot"/> is the ONLY place this store will ever read or write. It is never
    /// resolved, defaulted, or created here in the constructor — resolving the real
    /// <c>~/.guardrails/telemetry/</c> home path is the CLI task's job, not this store's.
    /// </summary>
    public TelemetryCorpusStore(string corpusRoot)
    {
        CorpusRoot = corpusRoot;
    }

    /// <summary>
    /// Appends <paramref name="row"/> as one JSON line to the file for its <see cref="TelemetryRow.StartedAt"/>'s
    /// UTC year and month, unless collection is disabled (<see cref="OptOutEnvVar"/> — writes nothing at
    /// all, no file created) or a row with the same <c>(runId, taskId, attempt)</c> triple is already on
    /// disk (idempotent no-op — re-ingesting a plan must be safe by construction). Never rewrites an
    /// existing line.
    /// </summary>
    public void Append(TelemetryRow row) => throw new NotImplementedException();

    /// <summary>Removes every row under the corpus root. Safe to call on an empty or not-yet-created corpus.</summary>
    public void Purge() => throw new NotImplementedException();
}
