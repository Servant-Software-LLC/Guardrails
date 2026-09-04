namespace Guardrails.Integration.Tests;

/// <summary>
/// Serializes the two classes in this assembly that are still coupled to the PROCESS-GLOBAL telemetry
/// environment variables — one because it mutates them, one because it reads them.
///
/// <para><b>This is the residual, not the fix.</b> The measured six-test failure on
/// <c>solution-wide-test (windows-latest)</c> was repaired at its mechanism:
/// <c>TelemetryCorpusStore.Append</c> no longer reads <c>GUARDRAILS_TELEMETRY</c> at write time, so a
/// store's behavior follows its constructor and no concurrent test can silently turn another's writes into
/// no-ops. That is why <c>TelemetryReportPhase1Tests</c> and <c>TelemetryCommandWiringTests</c> — the six
/// tests that actually failed — are deliberately NOT members here: they construct their own stores and are
/// now provably immune. Adding them would re-hide exactly the class of defect #566's concurrent profile
/// exists to expose.</para>
///
/// <para><b>What is left, and why it cannot be injected away.</b>
/// <c>RunEndTelemetryIngestTests</c> drives the real <c>run</c> verb through
/// <c>CommandFactory.BuildRootCommand</c>, and run-end ingest resolves both
/// <c>GUARDRAILS_TELEMETRY_CORPUS_ROOT</c> (where) and <c>GUARDRAILS_TELEMETRY</c> (whether) from the
/// environment at its own composition point. There is no seam into that path short of threading a switch
/// through the whole <c>run</c> command, so those tests genuinely must mutate process state — and while
/// one holds the opt-out at <c>off</c>, <c>TelemetryCommandTests</c>' ingest tests (which exercise the
/// production default, i.e. the environment, on purpose) would read it and write nothing.</para>
///
/// <para><b>If you add a test class that asserts on corpus contents AND lets the opt-out come from the
/// environment, add it here.</b> A class that injects its own collection decision, or constructs its own
/// store, does not belong — and should not be added just to be safe, because membership costs the
/// concurrency coverage that catches the next defect of this shape.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryEnvironmentCollection
{
    /// <summary>The collection name. Referenced by the environment-coupled telemetry classes.</summary>
    public const string Name = "telemetry-environment";
}
