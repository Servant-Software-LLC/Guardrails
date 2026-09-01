using Guardrails.Cli;
using Guardrails.Core.Journal;

namespace Guardrails.Integration.Tests.Journal;

/// <summary>
/// Plan 30 §3.4's machine/concurrency/version profile is one hop of three:
/// <c>RunEnvironmentProbe -&gt; RunJournal -&gt; state/run.json</c>. <see cref="RunEnvironmentTests"/>
/// (<c>Guardrails.Core.Tests</c>) proves the probe returns the right record and would keep passing even
/// if nothing ever persisted it — this test is the third hop, proving the record the probe produces
/// actually survives to the file a real run writes.
/// <para>
/// <b>Why this is red, and why that reason differs from the Core suite.</b> The Core tests are red
/// because <see cref="RunEnvironmentProbe.Probe"/> throws. This one is red because nothing stamps the
/// environment onto the journal at all yet — <c>18-record-the-run-environment</c> adds the recorder and
/// the call site on <c>RunCommand</c>. So on this tree a real run completes normally, <c>state/run.json</c>
/// is written, and its <c>environment</c> key is absent. <see cref="JournalDocument.Environment"/> already
/// exists (<c>03-extend-the-journal-record-shape</c>), so this compiles clean against the tree as it
/// stands; only the recording behaviour is missing.
/// </para>
/// <para>
/// Modeled on <see cref="RunEndTelemetryIngestTests"/>: drives a REAL <c>guardrails run</c> through
/// <see cref="CommandFactory.BuildRootCommand"/> — the actual composition root <c>Program.cs</c> builds —
/// then reads the journal back OFF DISK with <see cref="JournalReader.Read"/>. Never against a
/// <see cref="RunJournal"/> instance, a <see cref="JournalDocument"/>, or a <see cref="RunEnvironment"/>
/// the test itself builds: any of those would pass while <c>state/run.json</c> on disk carried nothing,
/// which is precisely the "silently lost" failure (named by <c>18-record-the-run-environment</c>'s own
/// prompt) this test exists to catch. Corpus isolation is already in force via the
/// <see cref="TelemetryCorpusIsolation"/> module initializer, so this never touches
/// <c>~/.guardrails/telemetry/</c>.
/// </para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class RunEnvironmentJournalTests
{
    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    [Fact]
    public async Task AfterARealRun_RunJsonCarriesANonNullEnvironmentHost()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-a");

        (int exit, _) = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        // Baseline, not the point: already true on this tree today.
        Assert.Equal(ExitCodes.Success, exit);

        // Load-bearing: read back OFF DISK, never from an in-memory RunJournal/JournalDocument this test
        // built itself.
        JournalDocument doc = JournalReader.Read(RunJournal.PathFor(plan.PlanDir));

        Assert.NotNull(doc.Environment);
        Assert.False(string.IsNullOrEmpty(doc.Environment!.Host));
    }
}
