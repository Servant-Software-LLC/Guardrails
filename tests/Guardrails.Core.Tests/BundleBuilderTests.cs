using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Guardrails.Core.Bundle;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests;

/// <summary>
/// #799 row 3: the bundle built end to end over a plan folder on disk (SSOT §17.3–§17.7, §17.10). Every machine fact is
/// injected; assertions are on entries, MANIFEST rows and bytes, never on elapsed time.
/// </summary>
public sealed class BundleBuilderTests : IDisposable
{
    private readonly BundlePlanFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static readonly string[] FullClassFiles =
    [
        "tasks/02-second/attempt-1/transcript.md",
        "tasks/02-second/attempt-1/claude-stream.jsonl",
        "tasks/02-second/attempt-1/composed-prompt.md",
        "tasks/02-second/attempt-1/prior-attempt.patch",
        "gateway/sessions/proj-a/session-1.jsonl",
    ];

    private void StandardRun()
    {
        _fixture.WriteJournal(BundlePlanFixture.Journal());
        _fixture.PromptAttempt("02-second", 1, $"token {BundlePlanFixture.KnownToken} here");
        _fixture.Log("events.jsonl", "{\"kind\":\"task-started\",\"task\":\"02-second\"}\n");
        _fixture.Log("claude-config/projects/proj-a/session-1.jsonl", $"{{\"text\":\"{BundlePlanFixture.KnownToken}\"}}\n");
        _fixture.Log("claude-config/.claude.json", "{\"userID\":\"private-config\"}\n");
        _fixture.Log("claude-config/shell-snapshots/snap.sh", "export PRIVATE=1\n");
        _fixture.Log("index.html", "<html></html>\n");
    }

    // ------------------------------------------------------------------ content default vs --lean (§17.3, §17.9)

    [Fact]
    public void ADefaultBundleIncludesTheFullClassRedactedUnderTheWarning()
    {
        StandardRun();
        BundleOutcome outcome = _fixture.Build();

        foreach (string path in FullClassFiles)
        {
            Assert.True(outcome.Has(path), $"{path} missing from a default (full) bundle");
        }

        Assert.All(outcome.Entries, entry =>
            Assert.DoesNotContain(BundlePlanFixture.KnownToken, Encoding.UTF8.GetString(entry.Value), StringComparison.Ordinal));
        Assert.Contains("[REDACTED:QWEN_TOKEN#1]", outcome.Text("tasks/02-second/attempt-1/transcript.md"), StringComparison.Ordinal);
        Assert.StartsWith(BundleBuilder.FullBundleWarning, outcome.Text("SUMMARY.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void LeanWithholdsEveryFullClassFileAndNamesEachInTheManifest()
    {
        StandardRun();
        BundleOutcome outcome = _fixture.Build(new BundleOptions { Lean = true });

        foreach (string path in FullClassFiles)
        {
            Assert.False(outcome.Has(path), $"{path} shipped in a --lean bundle");
            BundleManifestRow row = outcome.Row(path);
            Assert.Equal(("withheld", "lean"), (row.Status, row.Reason));
        }

        Assert.True(outcome.Has("tasks/02-second/attempt-1/feedback.md"));
        Assert.Equal(FullClassFiles.Length, outcome.LeanWithheld.Count);
        Assert.StartsWith(BundleBuilder.LeanNote(FullClassFiles.Length), outcome.Text("SUMMARY.md"), StringComparison.Ordinal);
        Assert.DoesNotContain("WARNING: this bundle includes", outcome.Text("SUMMARY.md"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScriptActionOutputIsIncludedRedactedInBothFullAndLean(bool lean)
    {
        StandardRun();
        _fixture.Log("01-first/attempt-1/action-stdout.log", $"building...\nexport QWEN_TOKEN={BundlePlanFixture.KnownToken}\ndone\n");
        _fixture.Log("01-first/attempt-1/action-stderr.log", "warning: ghp_AbCdEfGhIjKlMnOpQrStUv1234 is deprecated\n");

        BundleOutcome outcome = _fixture.Build(new BundleOptions { Lean = lean });

        string stdout = outcome.Text("tasks/01-first/attempt-1/action-stdout.log")!;
        string stderr = outcome.Text("tasks/01-first/attempt-1/action-stderr.log")!;
        Assert.Contains("building...", stdout, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:QWEN_TOKEN#1]", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(BundlePlanFixture.KnownToken, stdout, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:github-token]", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(outcome.Manifest, r => r.Source.EndsWith("/action-stdout.log", StringComparison.Ordinal) && r.Status == "listed-only");
    }

    [Fact]
    public void ScriptActionOutputIsTailCappedInTheLogClass()
    {
        StandardRun();
        var big = new StringBuilder();
        while (big.Length < BundleCatalog.LogCap + 10_000)
        {
            big.Append("line of script output\n");
        }

        _fixture.Log("01-first/attempt-1/action-stdout.log", big.ToString());
        BundleOutcome outcome = _fixture.Build();

        Assert.Equal(("tail", "tail-window"), Row(outcome, "tasks/01-first/attempt-1/action-stdout.log"));
        Assert.True(outcome.Entries.Single(e => e.Key == "tasks/01-first/attempt-1/action-stdout.log").Value.Length <= BundleCatalog.LogCap);
    }

    [Fact]
    public void ScriptActionOutputIsRemovedUnderWithoutAgentText()
    {
        StandardRun();
        _fixture.Log("01-first/attempt-1/action-stdout.log", "script echoed free text canary marker\n");
        _fixture.Log("01-first/attempt-1/action-stderr.log", "free text canary marker on stderr\n");

        BundleOutcome outcome = _fixture.Build(new BundleOptions { WithoutAgentText = true });

        Assert.False(outcome.Has("tasks/01-first/attempt-1/action-stdout.log"));
        Assert.False(outcome.Has("tasks/01-first/attempt-1/action-stderr.log"));
        Assert.Equal(("withheld", "agent-text"), Row(outcome, "tasks/01-first/attempt-1/action-stdout.log"));
        Assert.All(outcome.AllText(), t => Assert.DoesNotContain("canary marker", t, StringComparison.Ordinal));
    }

    [Fact]
    public void ClaudeConfigBeyondProjectSessionsIsExcludedByConstructionAndNamed()
    {
        StandardRun();
        BundleOutcome outcome = _fixture.Build(new BundleOptions { NoRedact = true });

        Assert.DoesNotContain(outcome.Entries, e => e.Key.Contains(".claude.json", StringComparison.Ordinal));
        Assert.DoesNotContain(outcome.AllText(), t => t.Contains("private-config", StringComparison.Ordinal) && !t.StartsWith("# MANIFEST", StringComparison.Ordinal));
        Assert.Contains(outcome.Manifest, r => r is { Status: "excluded", Reason: "claude-config-excluded", BundlePath: null });
        Assert.DoesNotContain(outcome.Manifest, r => r.Source.Contains("shell-snapshots", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownFileIsListedByPathAndSizeOnly()
    {
        StandardRun();
        File.WriteAllText(Path.Combine(_fixture.PlanDirectory, "state", "fragment-01.json"), "{}");
        BundleOutcome outcome = _fixture.Build();

        BundleManifestRow unknown = outcome.Manifest.Single(r => r.Source.EndsWith("/index.html", StringComparison.Ordinal));
        Assert.Equal(("listed-only", "unknown-kind", 14L, (string?)null), (unknown.Status, unknown.Reason, unknown.BytesRead, unknown.BundlePath));
        Assert.Contains(outcome.Manifest, r => r is { Status: "excluded", Reason: "state-fragments-phase-2" });
    }

    // ------------------------------------------------------------------ determinism (§17.10)

    [Fact]
    public void IdenticalStateAndProbesGiveAByteIdenticalZip()
    {
        StandardRun();
        byte[] first = _fixture.Build().Zip;
        byte[] second = _fixture.Build().Zip;

        Assert.Equal(SHA256.HashData(first), SHA256.HashData(second));
    }

    [Fact]
    public void OnlyTheBundledAtLineVariesWithTheClock()
    {
        StandardRun();
        BundleOutcome early = _fixture.Build(probes: _fixture.Probes(() => BundlePlanFixture.Clock));
        BundleOutcome late = _fixture.Build(probes: _fixture.Probes(() => BundlePlanFixture.Clock.AddDays(3)));

        Assert.Equal(early.Entries.Select(e => e.Key), late.Entries.Select(e => e.Key));
        foreach ((KeyValuePair<string, byte[]> a, KeyValuePair<string, byte[]> b) in early.Entries.Zip(late.Entries))
        {
            Assert.Equal(Mask(Encoding.UTF8.GetString(a.Value)), Mask(Encoding.UTF8.GetString(b.Value)));
        }

        Assert.NotEqual(early.Text("SUMMARY.md"), late.Text("SUMMARY.md"));

        static string Mask(string text) =>
            string.Join('\n', text.Split('\n').Select(l => l.StartsWith("Bundled at: ", StringComparison.Ordinal) ? "Bundled at: <masked>" : l));
    }

    [Fact]
    public void TheZipIsSortedWithFixedTimestampsAndLfGeneratedFiles()
    {
        StandardRun();
        BundleOutcome outcome = _fixture.Build();
        using var archive = new ZipArchive(new MemoryStream(outcome.Zip), ZipArchiveMode.Read);

        List<string> names = [.. archive.Entries.Select(e => e.FullName)];
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
        Assert.All(archive.Entries, e => Assert.Equal(new DateTime(1980, 1, 1), e.LastWriteTime.DateTime));
        Assert.All(archive.Entries, e => Assert.DoesNotContain('\\', e.FullName));
        foreach (string generated in new[] { "SUMMARY.md", "MANIFEST.md", "REDACTIONS.md", "plan/validate.txt", "git/integration.txt" })
        {
            byte[] bytes = outcome.Entries.Single(e => e.Key == generated).Value;
            Assert.DoesNotContain((byte)'\r', bytes);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, $"{generated} has a BOM");
        }
    }

    [Theory]
    [InlineData(RunLivenessState.NotRecorded)]
    [InlineData(RunLivenessState.Running)]
    [InlineData(RunLivenessState.ExitedWithoutFinishing)]
    [InlineData(RunLivenessState.Ended)]
    [InlineData(RunLivenessState.OnAnotherHost)]
    [InlineData(RunLivenessState.CannotCheck)]
    public void EveryLivenessStateRendersInTheSummary(RunLivenessState state)
    {
        StandardRun();
        _fixture.Liveness = state;
        Assert.Contains($"- Liveness: {state} (", _fixture.Build().Text("SUMMARY.md"), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ #798 both ways (§17.4 block 3)

    [Fact]
    public void TheInFlightAttemptComesFromTheMarkerWhenPresent()
    {
        _fixture.WriteJournal(BundlePlanFixture.Journal(
            secondStatus: JournalTaskStatus.Running,
            inFlight: new InFlightAttemptRecord { Attempt = 2, StartedAt = BundlePlanFixture.Clock.AddHours(1), Phase = "guardrails" }));
        _fixture.PromptAttempt("02-second", 1, "first");
        _fixture.PromptAttempt("02-second", 2, "second");
        _fixture.Liveness = RunLivenessState.Running;

        BundleOutcome outcome = _fixture.Build();
        string summary = outcome.Text("SUMMARY.md")!;

        Assert.Contains("- In flight: attempt-2 (phase guardrails, started 2026-09-27T13:00:00Z; from run.json inFlightAttempt; liveness: Running)", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("exists on disk and is not in the journal", summary, StringComparison.Ordinal);
        Assert.Equal("included", outcome.Row("tasks/02-second/attempt-2/feedback.md").Status);
        Assert.NotEqual("newer-than-journal", outcome.Row("tasks/02-second/attempt-2/feedback.md").Reason);
    }

    [Fact]
    public void WithoutTheMarkerTheInFlightAttemptIsInferredFromDiskVersusJournal()
    {
        _fixture.WriteJournal(BundlePlanFixture.Journal(secondStatus: JournalTaskStatus.Running));
        _fixture.PromptAttempt("02-second", 1, "first");
        _fixture.PromptAttempt("02-second", 2, "second");
        _fixture.Liveness = RunLivenessState.ExitedWithoutFinishing;

        BundleOutcome outcome = _fixture.Build();

        Assert.Contains(
            "attempt-2/ exists on disk and is not in the journal: in flight, or the run died during it (liveness: ExitedWithoutFinishing)",
            outcome.Text("SUMMARY.md"), StringComparison.Ordinal);
        Assert.Equal("newer-than-journal", outcome.Row("tasks/02-second/attempt-2/feedback.md").Reason);
    }

    // ------------------------------------------------------------------ the size budget (§17.7)

    [Fact]
    public void Tier2DropsOnlyTheMiddleAttemptsStreamAndKeepsTheFirstAndLatest()
    {
        ThreeAttemptsWithLargeStreams();
        long baseline = _fixture.Build().Zip.LongLength;
        long middleStream = CompressedSize("tasks/02-second/attempt-2/claude-stream.jsonl");

        BundleOutcome outcome = _fixture.Build(new BundleOptions { MaxSizeBytes = baseline - (middleStream / 2) });

        Assert.False(outcome.OverCap);
        Assert.Equal(("trimmed", "trim-tier-2"), Row(outcome, "tasks/02-second/attempt-2/claude-stream.jsonl"));
        Assert.True(outcome.Has("tasks/02-second/attempt-1/claude-stream.jsonl"));
        Assert.True(outcome.Has("tasks/02-second/attempt-3/claude-stream.jsonl"));
        Assert.True(outcome.Has("tasks/02-second/attempt-2/transcript.md"), "tier 3 must not run once tier 2 fits");
        Assert.Contains("Trimmed to fit --max-size", outcome.Text("SUMMARY.md"), StringComparison.Ordinal);
        Assert.Contains("- Trim tier applied: trim-tier-2.", outcome.Text("MANIFEST.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void AProtectedCoreOverTheCapIsStillWrittenAndReportedOverCap()
    {
        ThreeAttemptsWithLargeStreams();
        _fixture.Log("events.jsonl", RandomHexLines(new Random(7990), 200_000)); // over the halved log cap (tier 4)
        BundleOutcome outcome = _fixture.Build(new BundleOptions { MaxSizeBytes = 1024 });

        Assert.True(outcome.OverCap);
        foreach (string core in new[]
                 {
                     "SUMMARY.md", "MANIFEST.md", "REDACTIONS.md", "state/run.json", "plan/guardrails.json",
                     "tasks/02-second/attempt-1/feedback.md", "tasks/02-second/attempt-2/attempt-provenance.json",
                     "tasks/02-second/attempt-3/attempt-route.log",
                 })
        {
            Assert.True(outcome.Has(core), $"protected {core} was trimmed");
        }

        // The latest attempt of a failing task keeps its stream through tier 5 (tier 4 may shorten it).
        Assert.True(outcome.Has("tasks/02-second/attempt-3/claude-stream.jsonl"));
        Assert.Equal(("trimmed", "trim-tier-5"), Row(outcome, "tasks/02-second/attempt-1/claude-stream.jsonl"));
        Assert.Equal(("trimmed", "trim-tier-3"), Row(outcome, "tasks/02-second/attempt-2/transcript.md"));
        Assert.Equal(("trimmed", "trim-tier-4"), Row(outcome, "run/events.jsonl"));
        Assert.True(outcome.Entries.Single(e => e.Key == "run/events.jsonl").Value.Length <= BundleCatalog.LogCap / 2);

        string manifest = outcome.Text("MANIFEST.md")!;
        int[] order = [.. new[] { "trim-tier-2.", "trim-tier-3.", "trim-tier-4.", "trim-tier-5." }
            .Select(t => manifest.IndexOf("Trim tier applied: " + t, StringComparison.Ordinal))];
        Assert.All(order, i => Assert.True(i > 0));
        Assert.Equal(order.OrderBy(i => i), order);
    }

    private void ThreeAttemptsWithLargeStreams()
    {
        _fixture.WriteJournal(BundlePlanFixture.Journal(secondAttempts:
        [
            BundlePlanFixture.Attempt(1, AttemptOutcome.GuardrailFailed),
            BundlePlanFixture.Attempt(2, AttemptOutcome.GuardrailFailed),
            BundlePlanFixture.Attempt(3, AttemptOutcome.NeedsHuman),
        ]));
        var random = new Random(799);
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            _fixture.PromptAttempt("02-second", attempt, $"attempt {attempt}");
            var stream = new StringBuilder();
            while (stream.Length < 400_000)
            {
                stream.Append("{\"type\":\"assistant\",\"hex\":\"").Append(RandomHex(random, 120)).Append("\"}\n");
            }

            _fixture.Log($"02-second/attempt-{attempt}/claude-stream.jsonl", stream.ToString());
            _fixture.Log($"02-second/attempt-{attempt}/transcript.md", RandomHexLines(random, 40_000));
        }
    }

    private long CompressedSize(string path)
    {
        BundleOutcome outcome = _fixture.Build();
        return BundleZip.EntrySize(path, outcome.Entries.Single(e => e.Key == path).Value);
    }

    private static string RandomHex(Random random, int length)
    {
        var bytes = new byte[length / 2];
        random.NextBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string RandomHexLines(Random random, int total)
    {
        var text = new StringBuilder();
        while (text.Length < total)
        {
            text.Append(RandomHex(random, 80)).Append('\n');
        }

        return text.ToString();
    }

    private static (string Status, string Reason) Row(BundleOutcome outcome, string path)
    {
        BundleManifestRow row = outcome.Row(path);
        return (row.Status, row.Reason);
    }

    // ------------------------------------------------------------------ D1 and --without-agent-text (§17.6.5)

    [Fact]
    public void D1NamesEveryBlockVariableTheShellLacksIncludingAJudgeOnlyBlock()
    {
        var runners = new Dictionary<string, PromptRunnerConfig>
        {
            ["gateway"] = BundlePlanFixture.Runner("gateway", authTokenEnv: "LITELLM_MASTER_KEY", baseUrl: "http://127.0.0.1:4000"),
            ["judge"] = BundlePlanFixture.Runner("judge", kind: PromptRunnerKind.OpenAiCompat, apiKeyEnv: "JUDGE_API_KEY"),
            ["plain"] = BundlePlanFixture.Runner("plain"),
        };
        PlanDefinition plan = _fixture.Plan with { Config = _fixture.Plan.Config with { PromptRunners = runners } };

        var shell = new Dictionary<string, string> { ["LITELLM_MASTER_KEY"] = "set-in-this-shell", ["JUDGE_API_KEY"] = "" };
        Assert.Equal(["JUDGE_API_KEY"], BundleD1.UnsetVariables(plan, shell));
        Assert.Equal(["JUDGE_API_KEY", "LITELLM_MASTER_KEY"], BundleD1.UnsetVariables(plan, new Dictionary<string, string>()));
        Assert.Contains("  export JUDGE_API_KEY in this shell and re-run `guardrails bundle`", BundleD1.RefusalLines(["JUDGE_API_KEY"]));
    }

    [Fact]
    public void WithoutAgentTextRemovesAPlantedCanaryFromEveryAgentTextKindAndTheJournal()
    {
        const string canary = "free text canary marker";
        _fixture.WriteJournal(BundlePlanFixture.Journal(
            secondAttempts: [BundlePlanFixture.Attempt(1, AttemptOutcome.GuardrailFailed) with
            {
                FailedGuardrails = [new FailedGuardrail { Name = "02-tests", Reason = canary }],
            }],
            halt: new RunHalt { Kind = RunHaltKind.PlanGuardrailFailed, HaltedAt = BundlePlanFixture.Clock, Headline = canary }));
        _fixture.PromptAttempt("02-second", 1, canary);
        foreach (string file in new[]
                 {
                     "02-second/feedback.md", "02-second/triage.json", "02-second/overwatch.jsonl", "02-second/union-reverify-02-tests.stdout.log",
                     "events.jsonl", "observer.jsonl", "autonomy.jsonl", "escalations/0001-needs-human.json",
                     "guardrails/99-terminal/stdout.log", "guardrails/99-terminal/stderr.log",
                 })
        {
            _fixture.Log(file, $"{{\"text\":\"{canary}\"}}\n");
        }

        _fixture.Log("guardrails/99-terminal/result.json", $"{{\"name\":\"99-terminal\",\"passed\":false,\"exitCode\":1,\"reason\":\"{canary}\"}}\n");
        _fixture.Log("claude-config/projects/p/s.jsonl", $"{{\"text\":\"{canary}\"}}\n");
        File.WriteAllText(Path.Combine(_fixture.PlanDirectory, "tasks", "02-second", "task.json"), $"{{\"description\":\"{canary}\"}}\n");

        BundleOutcome outcome = _fixture.Build(new BundleOptions { WithoutAgentText = true });

        Assert.All(outcome.Entries, entry => Assert.False(
            Encoding.UTF8.GetString(entry.Value).Contains(canary, StringComparison.Ordinal), $"{entry.Key} still carries agent text"));
        Assert.Contains(AgentTextProjection.Withheld, outcome.Text("state/run.json"), StringComparison.Ordinal);
        Assert.Contains("\"passed\": false", outcome.Text("gates/guardrails/99-terminal/result.json"), StringComparison.Ordinal);
        Assert.True(outcome.Has("tasks/02-second/attempt-1/attempt-route.log"));
        Assert.Equal(("withheld", "agent-text"), Row(outcome, "tasks/02-second/attempt-1/feedback.md"));
        Assert.Contains("Variables that forced it: none: requested explicitly.", outcome.Text("SUMMARY.md"), StringComparison.Ordinal);
        Assert.Contains(_fixture.GitCalls, call => call.SequenceEqual(["log", "-5", "--format=%h %ad"]));
    }

    // ------------------------------------------------------------------ pass 4 and pass 6 (§17.6.4, §17.6.6)

    [Fact]
    public void AStreamLogScrubbedLessThanItsTranscriptIsExcluded()
    {
        _fixture.WriteJournal(BundlePlanFixture.Journal());
        _fixture.PromptAttempt("02-second", 1, "plain");
        _fixture.Log("02-second/attempt-1/transcript.md", $"the key is {BundlePlanFixture.KnownToken}\n");
        _fixture.Log("02-second/attempt-1/claude-stream.jsonl", "{\"type\":\"assistant\",\"text\":\"the key is elsewhere\"}\n");

        BundleOutcome outcome = _fixture.Build();

        Assert.Equal(("excluded", "stream-scrubbed-less"), Row(outcome, "tasks/02-second/attempt-1/claude-stream.jsonl"));
        Assert.False(outcome.Has("tasks/02-second/attempt-1/claude-stream.jsonl"));
    }

    [Fact]
    public void AScanThatThrowsOrTimesOutExcludesTheFileRatherThanShippingItRaw()
    {
        StandardRun();
        BundleProbes throwing = _fixture.Probes() with
        {
            Redact = (text, context) => context.ArtifactPath.EndsWith("transcript.md", StringComparison.Ordinal)
                ? throw new InvalidOperationException("scan blew up")
                : BundleRedactor.Redact(text, context),
        };
        BundleOutcome failed = _fixture.Build(probes: throwing);
        Assert.Equal(("excluded", "scan-failed"), Row(failed, "tasks/02-second/attempt-1/transcript.md"));
        Assert.False(failed.Has("tasks/02-second/attempt-1/transcript.md"));

        BundleOutcome slow = _fixture.Build(probes: _fixture.Probes() with { StartScanTimer = () => () => TimeSpan.FromMinutes(5) });
        Assert.Equal(("excluded", "scan-timeout"), Row(slow, "tasks/02-second/attempt-1/feedback.md"));
        Assert.DoesNotContain(slow.Entries, e => e.Key.StartsWith("tasks/", StringComparison.Ordinal));
    }

    [Fact]
    public void AFileThatIsNotUtf8IsExcludedAndNamed()
    {
        StandardRun();
        _fixture.Log("02-second/attempt-1/feedback.md", [0x66, 0x6F, 0xFF, 0xFE, 0x0A]);
        BundleOutcome outcome = _fixture.Build();
        Assert.Equal(("excluded", "not-utf8"), Row(outcome, "tasks/02-second/attempt-1/feedback.md"));
    }

    [Fact]
    public void AFileHeldOpenPastTheRetriesIsExcludedAndNamedNotACrash()
    {
        StandardRun();
        var reader = new BundleFileReader(path =>
        {
            if (path.EndsWith("action-result.json", StringComparison.Ordinal))
            {
                throw new IOException("held", unchecked((int)0x80070020));
            }

            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }, _ => { });

        BundleOutcome outcome = _fixture.Build(probes: _fixture.Probes() with { Reader = reader });
        Assert.Equal(("excluded", "sharing-violation"), Row(outcome, "tasks/02-second/attempt-1/action-result.json"));
    }

    // ------------------------------------------------------------------ --run, --no-redact, REDACTIONS.md, SUMMARY facts

    [Fact]
    public void AnEarlierRunIsBuiltFromItsLogsAloneAndLeavesTheCurrentJournalOut()
    {
        StandardRun();
        string earlier = Path.Combine(_fixture.PlanDirectory, "logs", "2026-09-01T00-00-00Z-old1", "02-second", "attempt-1");
        Directory.CreateDirectory(earlier);
        File.WriteAllText(Path.Combine(earlier, "action-result.json"), "{\"exitCode\":1,\"summary\":\"old failure\"}\n");

        BundleOutcome outcome = _fixture.Build(new BundleOptions { RunId = "2026-09-01T00-00-00Z-old1" });

        Assert.False(outcome.Has("state/run.json"));
        Assert.Equal(("excluded", "other-run-journal"), Row(outcome, "state/run.json"));
        Assert.Contains("Earlier run: this bundle is run 2026-09-01T00-00-00Z-old1", outcome.Text("SUMMARY.md"), StringComparison.Ordinal);
        Assert.Contains("| 1 | unknown (no journal) | - | 1 | old failure |", outcome.Text("SUMMARY.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void NoRedactSaysSoFirstAndInRedactionsMd()
    {
        StandardRun();
        BundleOutcome outcome = _fixture.Build(new BundleOptions { NoRedact = true });

        Assert.StartsWith(BundleBuilder.NotRedactedLine + "\n\n" + BundleBuilder.FullBundleWarning, outcome.Text("SUMMARY.md"), StringComparison.Ordinal);
        Assert.Equal(BundleBuilder.NotRedactedLine + "\n", outcome.Text("REDACTIONS.md"));
        Assert.Contains(BundlePlanFixture.KnownToken, outcome.Text("tasks/02-second/attempt-1/transcript.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void RedactionsMdCountsPerLabelAndNamesEveryLimitButNeverAValue()
    {
        StandardRun();
        string redactions = _fixture.Build().Text("REDACTIONS.md")!;

        Assert.Contains("| tasks/02-second/attempt-1/transcript.md | QWEN_TOKEN#1 | 1 |", redactions, StringComparison.Ordinal);
        Assert.Contains(BundleRedactor.CannotCatchText, redactions, StringComparison.Ordinal);
        Assert.DoesNotContain(BundlePlanFixture.KnownToken, redactions, StringComparison.Ordinal);
    }

    [Fact]
    public void PathsAreAnonymizedByDefaultAndKeptOnRequest()
    {
        StandardRun();
        _fixture.Log("02-second/attempt-1/feedback.md", $"see {Path.Combine(_fixture.PlanDirectory, "tasks", "02-second", "task.json")}\n");

        Assert.Contains("see <workspace>", _fixture.Build().Text("tasks/02-second/attempt-1/feedback.md"), StringComparison.Ordinal);
        Assert.Contains(_fixture.PlanDirectory, _fixture.Build(new BundleOptions { KeepPaths = true }).Text("tasks/02-second/attempt-1/feedback.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSummaryReportsTheGatewayPlaceholderAndTheRunsFactsInOrder()
    {
        _fixture.Plan = _fixture.Plan with
        {
            Config = _fixture.Plan.Config with
            {
                PromptRunners = new Dictionary<string, PromptRunnerConfig>
                {
                    ["qwen36"] = BundlePlanFixture.Runner("qwen36", baseUrl: "http://127.0.0.1:4000"),
                },
            },
        };
        StandardRun();
        string summary = _fixture.Build().Text("SUMMARY.md")!;

        Assert.Contains("- Token in the run: the placeholder `guardrails-gateway-no-auth` was sent (the block names no authTokenEnv)", summary, StringComparison.Ordinal);
        Assert.Contains("- Guardrails (the run's journal): 1.26.0\n", summary, StringComparison.Ordinal);
        Assert.Contains("- Definition drift: none (task.json matches the journal's definitionHash)", summary, StringComparison.Ordinal);
        Assert.Contains("- Last halt or needs-human reason: 02-second:", summary, StringComparison.Ordinal);
        string[] headings = ["## 1. Bundled at", "## 2. Run", "## 3. Tasks", "## 4. Gateway and endpoint blocks", "## 5. Withheld", "## 6. Issue skeleton"];
        int[] positions = [.. headings.Select(h => summary.IndexOf(h, StringComparison.Ordinal))];
        Assert.All(positions, p => Assert.True(p > 0));
        Assert.Equal(positions.OrderBy(p => p), positions);
        Assert.Contains("- File names: `git status` and `git diff --stat` name files", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateIsReRunAndLabelledAsNotReadOnly()
    {
        StandardRun();
        string validate = _fixture.Build().Text("plan/validate.txt")!;
        Assert.StartsWith(BundleBuilder.ValidateLabel + "\n", validate, StringComparison.Ordinal);
        Assert.Contains("OK: plan is valid.", validate, StringComparison.Ordinal);
    }

    [Fact]
    public void ThereIsNoRunToBundleWithoutAJournalOrARunId()
    {
        Assert.Throws<BundleRefusedException>(() => _fixture.Build());
    }
}
