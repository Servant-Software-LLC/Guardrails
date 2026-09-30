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
        "gateway/sessions/project-1/session-1.jsonl",
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

    // ------------------------------------------------------------------ session directory names never ship

    private static string Encoded(string path) => new([.. path.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);

    [Fact]
    public void SessionDirectoryNamesAreRenumberedAndNeverShipAnywhere()
    {
        StandardRun();
        string encodedHome = Encoded(_fixture.Home) + "-src-app";
        string secretDir = $"-work-{BundlePlanFixture.KnownToken}-repo";
        _fixture.Log($"claude-config/projects/{encodedHome}/s-a.jsonl", "{\"text\":\"one\"}\n");
        _fixture.Log($"claude-config/projects/{secretDir}/s-b.jsonl", "{\"text\":\"two\"}\n");
        _fixture.Log($"02-second/attempt-1/claude-config/projects/{encodedHome}/s-c.jsonl", "{\"text\":\"three\"}\n");

        BundleOutcome outcome = _fixture.Build();
        byte[] raw = outcome.Zip;

        foreach (string leak in new[] { encodedHome, secretDir, BundlePlanFixture.KnownToken, Encoded(_fixture.Home), "fixture-user" })
        {
            Assert.False(Contains(raw, leak), $"'{leak}' is in the raw zip bytes");
            Assert.All(outcome.Entries, e => Assert.DoesNotContain(leak, e.Key, StringComparison.Ordinal));
            Assert.All(outcome.Entries, e => Assert.DoesNotContain(leak, Encoding.UTF8.GetString(e.Value), StringComparison.Ordinal));
            Assert.All(outcome.Manifest, r =>
            {
                Assert.DoesNotContain(leak, r.Source, StringComparison.Ordinal);
                Assert.DoesNotContain(leak, r.BundlePath ?? string.Empty, StringComparison.Ordinal);
            });
        }

        // Ordinal sort of the ORIGINAL names: "-work-…" < "…encoded home…" (a drive letter or a leading '-' path), so
        // the numbering is a function of the inputs alone.
        string[] originals = [.. new[] { encodedHome, secretDir, "proj-a" }.OrderBy(n => n, StringComparer.Ordinal)];
        Assert.True(outcome.Has($"gateway/sessions/project-{Array.IndexOf(originals, encodedHome) + 1}/s-a.jsonl"));
        Assert.True(outcome.Has($"gateway/sessions/project-{Array.IndexOf(originals, secretDir) + 1}/s-b.jsonl"));
        Assert.True(outcome.Has($"gateway/sessions/project-{Array.IndexOf(originals, "proj-a") + 1}/session-1.jsonl"));
        Assert.True(outcome.Has("gateway/sessions/02-second/attempt-1/project-1/s-c.jsonl"));
        Assert.Contains(outcome.Manifest, r => r.Source.EndsWith("(original name withheld: encodes a local path)", StringComparison.Ordinal));
        Assert.Equal(outcome.Zip, _fixture.Build().Zip);
    }

    [Fact]
    public void AWindowsStyleEncodedCwdNeverLeaksTheUserName()
    {
        var anonymizer = new BundlePathAnonymizer(@"C:\Users\Dana", @"C:\Users\Dana\src\app", null, "Dana", [], caseInsensitive: true);

        Assert.Equal("see <workspace>-plan", anonymizer.Apply(@"see C--Users-Dana-src-app-plan"));
        Assert.Equal("see ~-AppData-Local-Temp", anonymizer.Apply(@"see C--Users-Dana-AppData-Local-Temp"));
        Assert.Equal("see D--build-<user>-cache", anonymizer.Apply("see D--build-Dana-cache"));
        // #805 N6: a 4-character name is not distinctive, so it is replaced only in path and encoded-path forms.
        Assert.Equal("task 03-dana-review", anonymizer.Apply("task 03-dana-review"));
    }

    [Fact]
    public void AnEntryNameCarryingASecretIsExcludedFailClosed()
    {
        StandardRun();
        _fixture.Log($"escalations/0001-{BundlePlanFixture.KnownToken}.json", "{\"status\":\"open\"}\n");

        BundleOutcome outcome = _fixture.Build();

        Assert.False(Contains(outcome.Zip, BundlePlanFixture.KnownToken));
        BundleManifestRow row = outcome.Manifest.Single(r => r.Reason == "unsafe-name");
        Assert.Equal(("excluded", "run/escalations/0001-[REDACTED:QWEN_TOKEN#1].json"), (row.Status, row.BundlePath));
    }

    private static bool Contains(byte[] haystack, string needle) =>
        haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;

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
            string.Join('\n', text.Split('\n').Select(l =>
                BundleBuilder.MaskedLinePrefixes.FirstOrDefault(prefix => l.StartsWith(prefix, StringComparison.Ordinal)) is { } masked
                    ? masked + "<masked>"
                    : l));
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
        Assert.Contains(
            "  JUDGE_API_KEY is not set: export JUDGE_API_KEY=<value> in this shell and re-run `guardrails bundle`",
            BundleD1.RefusalLines(["JUDGE_API_KEY"], new Dictionary<string, string>()));
    }

    [Fact]
    public void D1RefusalTellsAnUnsetVariableFromOneSetButEmpty()
    {
        // #814: a bare `export NAME` (no `=value`) exports an EMPTY variable, which D1 still refuses. The refusal must
        // say so, not repeat the remedy the operator just followed.
        var shell = new Dictionary<string, string> { ["EMPTY_KEY"] = "" };

        IReadOnlyList<string> lines = BundleD1.RefusalLines(["EMPTY_KEY", "MISSING_KEY"], shell);

        Assert.Contains(
            "  EMPTY_KEY is set but EMPTY (did you run `export EMPTY_KEY` without `=value`?): "
            + "export EMPTY_KEY=<value> in this shell and re-run `guardrails bundle`",
            lines);
        Assert.Contains(
            "  MISSING_KEY is not set: export MISSING_KEY=<value> in this shell and re-run `guardrails bundle`",
            lines);
        Assert.DoesNotContain(lines, line => line.Contains("MISSING_KEY is set but EMPTY", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("EMPTY_KEY is not set", StringComparison.Ordinal));
    }

    [Fact]
    public void D1RefusalOffersNoRedactForAPrivateBundleAndKeepsWithoutAgentText()
    {
        // #814: --no-redact already bypasses D1; the refusal must name it, scoped to a bundle that stays private.
        IReadOnlyList<string> lines = BundleD1.RefusalLines(["LITELLM_MASTER_KEY"], new Dictionary<string, string>());

        Assert.Contains(
            "  Or pass --no-redact if this bundle stays private (it is written as -UNREDACTED; never attach it to a public issue).",
            lines);
        Assert.Contains(
            "  Or pass --without-agent-text to ship the bundle with all agent-derived free text removed, run-wide.",
            lines);
        Assert.StartsWith("guardrails bundle: refused (D1):", lines[0], StringComparison.Ordinal);
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

        // #805 S7: the only timing exclusion is a Regex's own timeout (there is no wall-clock budget).
        BundleProbes timingOut = _fixture.Probes() with
        {
            Redact = (text, context) => context.ArtifactPath.EndsWith("feedback.md", StringComparison.Ordinal)
                ? throw new System.Text.RegularExpressions.RegexMatchTimeoutException("x", "y", TimeSpan.FromSeconds(10))
                : BundleRedactor.Redact(text, context),
        };
        BundleOutcome slow = _fixture.Build(probes: timingOut);
        Assert.Equal(("excluded", "scan-timeout"), Row(slow, "tasks/02-second/attempt-1/feedback.md"));
        Assert.False(slow.Has("tasks/02-second/attempt-1/feedback.md"));
    }

    // ------------------------------------------------------------------ #805 review: stuck-run evidence and safety

    [Theory]
    [InlineData("fixture-user", false)]
    [InlineData("runner", false)] // GitHub's macOS and Linux runners' OS user
    [InlineData("runner", true)]  // and a macOS-shaped home (/var/folders/<mixed-case>/T/…): the #805 CI failure, on any machine
    public void S1_ALiveRunGetsLiveFilesAnInFlightDurationAndItsProcessTree(string osUser, bool macHome)
    {
        _fixture.UserName = osUser;
        string home = macHome ? "/var/folders/2x/Kq9vR7mL3pB8tw4Nz5y0000gn/T/guardrails-bundle-tests/0123abcd/home" : _fixture.Home;
        _fixture.WriteJournal(BundlePlanFixture.Journal(
            secondStatus: JournalTaskStatus.Running,
            inFlight: new InFlightAttemptRecord { Attempt = 2, StartedAt = BundlePlanFixture.Clock.AddMinutes(-7), Phase = "action" }));
        _fixture.PromptAttempt("02-second", 1, "first");
        _fixture.PromptAttempt("02-second", 2, "second");
        _fixture.Log("02-second/attempt-2/action-stdout.log", "still going\n");
        _fixture.Log("events.jsonl", "{\"kind\":\"attempt-started\"}\n");
        _fixture.Liveness = RunLivenessState.Running;
        _fixture.ProcessTree = pid => new BundleProcessTree(
        [
            new BundleProcessRow(pid, 1, "S", "00:10:00", "0.1", "guardrails run plan-x"),
            new BundleProcessRow(5001, pid, "R", "00:07:00", "97.0", $"claude -p --settings {home}/x QWEN_TOKEN={BundlePlanFixture.KnownToken}"),
        ], null);
        DateTimeOffset written = BundlePlanFixture.Clock.AddMinutes(-2);
        BundleProbes probes = _fixture.Probes() with
        {
            Home = home,
            Stat = path => File.Exists(path) ? new BundleFileStat(new FileInfo(path).Length, written) : null,
        };

        string summary = _fixture.Build(probes: probes).Text("SUMMARY.md")!;

        Assert.Contains("- In flight for 7m 00s (since the marker's startedAt)", summary, StringComparison.Ordinal);
        Assert.Contains("- live: tasks/02-second/attempt-2/claude-stream.jsonl — ", summary, StringComparison.Ordinal);
        Assert.Contains("- live: tasks/02-second/attempt-2/action-stdout.log — 12 bytes, last write 2026-09-27T11:58:00Z (2m 00s before Bundled at)", summary, StringComparison.Ordinal);
        Assert.Contains("- live: run/events.jsonl — ", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("- live: tasks/02-second/attempt-1/", summary, StringComparison.Ordinal);
        string proc = summary.Split('\n').Single(l => l.StartsWith("- proc: 5001", StringComparison.Ordinal));
        // The command line is anonymized and redacted (the entropy rule may take `NAME=value` as one run: over-redaction).
        Assert.StartsWith("- proc: 5001 (parent 4242) R 00:07:00 97.0 — claude -p --settings ~/x ", proc, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:QWEN_TOKEN#1]", proc, StringComparison.Ordinal);
        Assert.DoesNotContain(BundlePlanFixture.KnownToken, summary, StringComparison.Ordinal);
    }

    [Fact]
    public void S1_AProcessTreeThatCannotBeReadIsNotedAndANotRunningOwnerIsNotProbed()
    {
        StandardRun();
        _fixture.Liveness = RunLivenessState.Running;
        _fixture.ProcessTree = _ => BundleProcessTree.Failed("ps is not on PATH");
        Assert.Contains("- proc: unavailable (ps is not on PATH)", _fixture.Build().Text("SUMMARY.md"), StringComparison.Ordinal);

        int probed = 0;
        _fixture.Liveness = RunLivenessState.Ended;
        _fixture.ProcessTree = _ => { probed++; return BundleProcessTree.Failed("unused"); };
        Assert.Contains("Not captured: the run's owner process is not running (liveness: Ended).", _fixture.Build().Text("SUMMARY.md"), StringComparison.Ordinal);
        Assert.Equal(0, probed);
    }

    [Fact]
    public void S1_TheProcessTableParsersKeepOnlyTheOwnersDescendants()
    {
        IReadOnlyList<BundleProcessRow> ps = SystemBundleProcessTree.ParsePs(
            "  1     0 Ss   10:00:00  0.0 /sbin/init\n 4242     1 S    05:00  0.1 dotnet guardrails run plan\n"
            + " 5001  4242 R    04:00 97.0 claude -p --model x\n 6000     1 S    01:00  0.0 unrelated\n 5002  5001 S    03:00  1.0 node tool\n");
        Assert.Equal([4242, 5001, 5002], BundleProcessTree.Descendants(4242, ps).Select(r => r.Pid));
        Assert.Equal("claude -p --model x", ps.Single(r => r.Pid == 5001).Command);

        IReadOnlyList<BundleProcessRow> cim = SystemBundleProcessTree.ParseCim(
            "[{\"ProcessId\":4242,\"ParentProcessId\":1,\"Started\":\"2026-09-27T11:00:00Z\",\"CommandLine\":\"guardrails run\"},"
            + "{\"ProcessId\":7,\"ParentProcessId\":4242,\"Started\":null,\"CommandLine\":null}]",
            BundlePlanFixture.Clock);
        Assert.Equal("01:00:00", cim.Single(r => r.Pid == 4242).Elapsed);
        Assert.Equal([4242, 7], BundleProcessTree.Descendants(4242, cim).Select(r => r.Pid));
    }

    [Fact]
    public void S2_UnderTaskARunLevelSessionOfAnotherTaskOrOfNoAttemptIsListedOnly()
    {
        StandardRun();
        _fixture.PromptAttempt("01-first", 1, "one");
        _fixture.Log($"claude-config/projects/-wt-run-{ClaudeName("01-first/attempt-1")}/s1.jsonl", "{\"t\":1}\n");
        _fixture.Log($"claude-config/projects/-wt-run-{ClaudeName("02-second/attempt-1")}/s2.jsonl", "{\"t\":2}\n");

        // s2 is the newest session: the newest one always ships (#805 N-a), so it must not be the one under test.
        BundleProbes probes = _fixture.Probes() with
        {
            Stat = path => new BundleFileStat(1, path.EndsWith("s2.jsonl", StringComparison.Ordinal)
                ? BundlePlanFixture.Clock : BundlePlanFixture.Clock.AddHours(-1)),
        };
        BundleOutcome outcome = _fixture.Build(new BundleOptions { Tasks = ["02-second"] }, probes);

        List<BundleManifestRow> sessions = [.. outcome.Manifest.Where(r => r.BundlePath?.StartsWith("gateway/sessions/", StringComparison.Ordinal) == true)];
        Assert.Contains(sessions, r => r.BundlePath!.EndsWith("/s2.jsonl", StringComparison.Ordinal) && r.Status == "included");
        Assert.Contains(sessions, r => r.BundlePath!.EndsWith("/s1.jsonl", StringComparison.Ordinal) && (r.Status, r.Reason) == ("listed-only", "task-filter"));
        Assert.Contains(sessions, r => r.BundlePath!.EndsWith("/session-1.jsonl", StringComparison.Ordinal) && (r.Status, r.Reason) == ("listed-only", "task-filter"));
        Assert.DoesNotContain(outcome.Entries, e => e.Key.EndsWith("/s1.jsonl", StringComparison.Ordinal));
    }

    private static string ClaudeName(string path) => BundlePathAnonymizer.Encode(path);

    [Fact]
    public void S3_TierTwoKeepsTheNewestSessionEvenWhenNoAttemptClaimsIt()
    {
        // Serial mode: no session is attributable, so before #805 tier 2 dropped all of them, the live one included.
        StandardRun();
        var random = new Random(8053);
        string older = _fixture.Log("claude-config/projects/p-old/old.jsonl", RandomHexLines(random, 300_000));
        string newer = _fixture.Log("claude-config/projects/p-new/new.jsonl", RandomHexLines(random, 300_000));
        BundleProbes probes = _fixture.Probes() with
        {
            Stat = path => new BundleFileStat(1, path == newer ? BundlePlanFixture.Clock : BundlePlanFixture.Clock.AddHours(-1)),
        };
        long baseline = _fixture.Build(probes: probes).Zip.LongLength;

        BundleOutcome outcome = _fixture.Build(new BundleOptions { MaxSizeBytes = baseline - 1000 }, probes);

        Assert.True(outcome.Entries.Any(e => e.Key.EndsWith("/new.jsonl", StringComparison.Ordinal)), "the newest session was trimmed");
        Assert.Contains(outcome.Manifest, r => r.BundlePath?.EndsWith("/old.jsonl", StringComparison.Ordinal) == true && r.Reason == "trim-tier-2");
        _ = older;
    }

    [Theory]
    [InlineData("2026-09-27T10-00-00Z-ab12", true)]
    [InlineData("../other", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("C:x", false)]
    [InlineData("/abs", false)]
    [InlineData("", false)]
    public void S4_ARunIdIsASinglePathSegment(string runId, bool safe) => Assert.Equal(safe, BundleBuilder.IsSafeRunId(runId));

    [Fact]
    public void S4_ThePathLikeRunIdIsRefusedBeforeAnythingIsRead()
    {
        StandardRun();
        Assert.Throws<BundleRefusedException>(() => _fixture.Build(new BundleOptions { RunId = "../../x" }));
    }

    [Theory]
    [InlineData("abc1234", true)]
    [InlineData("e46f75a26a692180f0ff3d0b27d63a706aef939c", true)]
    [InlineData("--output=/tmp/x", false)]
    [InlineData("HEAD~3", false)]
    [InlineData("ABC1234", false)]
    [InlineData("abc12", false)]
    [InlineData(null, false)]
    public void S5_OnlyACommitIdIsUsedAsABase(string? value, bool ok) => Assert.Equal(ok, BundleBuilder.IsCommitId(value));

    [Fact]
    public void S5_ASegmentOutsideTheWorktreeRootIsSkippedAndAValidBaseUsesEndOfOptions()
    {
        string root = Path.Combine(_fixture.Root, "wt");
        string inside = Path.Combine(root, BundlePlanFixture.RunId, "02-second", "attempt-1");
        string outside = Path.Combine(_fixture.Root, "elsewhere");
        Directory.CreateDirectory(inside);
        Directory.CreateDirectory(outside);

        _fixture.WriteJournal(BundlePlanFixture.Journal(secondAttempts: [BundlePlanFixture.Attempt(1, AttemptOutcome.GuardrailFailed, inside, "abc1234")]));
        _fixture.PromptAttempt("02-second", 1, "x");
        _fixture.Log("02-second/attempt-1/attempt-provenance.json", $"{{\"model\":\"m\",\"worktreePath\":{System.Text.Json.JsonSerializer.Serialize(inside)},\"baseCommit\":\"abc1234\"}}\n");
        BundleOutcome confined = _fixture.Build(probes: _fixture.Probes() with { WorktreeRoot = root });
        Assert.Contains(_fixture.GitCalls, c => c.SequenceEqual(["diff", "--stat", "--end-of-options", "abc1234..HEAD"]));
        Assert.Contains("$ git status", confined.Text("git/02-second.txt"), StringComparison.Ordinal);

        _fixture.GitCalls.Clear();
        _fixture.Log("02-second/attempt-1/attempt-provenance.json", $"{{\"model\":\"m\",\"worktreePath\":{System.Text.Json.JsonSerializer.Serialize(outside)},\"baseCommit\":\"--output=x\"}}\n");
        BundleOutcome escaped = _fixture.Build(probes: _fixture.Probes() with { WorktreeRoot = root });
        Assert.Contains("(skipped: the recorded worktree path does not resolve under a known worktree root", escaped.Text("git/02-second.txt"), StringComparison.Ordinal);
        Assert.DoesNotContain(_fixture.GitCalls, c => c.Contains("--output=x..HEAD"));
    }

    [Fact]
    public void S6_ASymlinkIsListedButNeverReadOrRecursed()
    {
        StandardRun();
        string outsideDir = Path.Combine(_fixture.Root, "outside");
        Directory.CreateDirectory(outsideDir);
        string outsideFile = Path.Combine(outsideDir, "private.txt");
        File.WriteAllText(outsideFile, "outside-the-plan-secret-marker\n");
        string fileLink = Path.Combine(_fixture.RunLogs, "02-second", "attempt-1", "transcript.md");
        string dirLink = Path.Combine(_fixture.RunLogs, "linked-dir");
        File.Delete(fileLink);
        try
        {
            File.CreateSymbolicLink(fileLink, outsideFile);
            Directory.CreateSymbolicLink(dirLink, outsideDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"this machine does not permit creating symbolic links ({ex.GetType().Name}: {ex.Message})");
        }

        BundleOutcome outcome = _fixture.Build();

        Assert.All(outcome.AllText(), t => Assert.DoesNotContain("outside-the-plan-secret-marker", t, StringComparison.Ordinal));
        Assert.Contains(outcome.Manifest, r => r.Source.EndsWith("/attempt-1/transcript.md", StringComparison.Ordinal) && (r.Status, r.Reason) == ("excluded", "symlink"));
        Assert.Contains(outcome.Manifest, r => r.Source.EndsWith("/linked-dir", StringComparison.Ordinal) && (r.Status, r.Reason) == ("excluded", "symlink"));
        Assert.DoesNotContain(outcome.Manifest, r => r.Source.EndsWith("/private.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void S8_TheInFlightMarkerLogAndTheOverwatchAndTriageStreamsAreBundled()
    {
        StandardRun();
        _fixture.Log("02-second/inflight-marker.log", "attempt 1 started\n");
        _fixture.Log("02-second/overwatch-stream-attempt-1.jsonl", $"{{\"text\":\"{BundlePlanFixture.KnownToken}\"}}\n");
        _fixture.Log("02-second/overwatch-noverdict-attempt-1.txt", "no verdict\n");
        _fixture.Log("02-second/triage-stream.jsonl", "{\"text\":\"triage\"}\n");

        BundleOutcome full = _fixture.Build();
        foreach (string path in new[] { "inflight-marker.log", "overwatch-stream-attempt-1.jsonl", "overwatch-noverdict-attempt-1.txt", "triage-stream.jsonl" })
        {
            Assert.True(full.Has("tasks/02-second/" + path), path);
        }

        Assert.Contains("[REDACTED:QWEN_TOKEN#1]", full.Text("tasks/02-second/overwatch-stream-attempt-1.jsonl"), StringComparison.Ordinal);

        BundleOutcome stripped = _fixture.Build(new BundleOptions { WithoutAgentText = true });
        Assert.True(stripped.Has("tasks/02-second/inflight-marker.log"));
        Assert.False(stripped.Has("tasks/02-second/overwatch-stream-attempt-1.jsonl"));
        Assert.False(stripped.Has("tasks/02-second/triage-stream.jsonl"));
    }

    // ------------------------------------------------------------------ #805 final batch

    /// <summary>Make a directory link at <paramref name="link"/>: a symbolic link, or a Windows junction (no privilege).</summary>
    private static void MakeDirectoryLink(string link, string target, bool junction)
    {
        if (!junction)
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Assert.Skip($"this machine does not permit creating symbolic links ({ex.GetType().Name}: {ex.Message})");
            }

            return;
        }

        Assert.SkipUnless(OperatingSystem.IsWindows(), "a junction is a Windows reparse point; the symbolic-link variant covers POSIX");
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/d", "/c", "mklink", "/J", link, target },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        mklink.WaitForExit();
        Assert.True(mklink.ExitCode == 0 && Directory.Exists(link), "fixture: mklink /J failed: " + mklink.StandardError.ReadToEnd());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Item2_ALinkedProjectsDirectoryShipsNoSession(bool junction)
    {
        StandardRun();
        string outside = Path.Combine(_fixture.Root, "all-sessions-on-the-machine");
        Directory.CreateDirectory(Path.Combine(outside, "C--Users-someone-private-repo"));
        File.WriteAllText(Path.Combine(outside, "C--Users-someone-private-repo", "s.jsonl"), "{\"text\":\"private-session-marker\"}\n");
        string projects = Path.Combine(_fixture.RunLogs, "claude-config", "projects");
        Directory.Delete(projects, recursive: true);
        MakeDirectoryLink(projects, outside, junction);

        BundleOutcome outcome = _fixture.Build();

        Assert.DoesNotContain(outcome.Entries, e => e.Key.StartsWith("gateway/sessions/", StringComparison.Ordinal));
        Assert.All(outcome.AllText(), t => Assert.DoesNotContain("private-session-marker", t, StringComparison.Ordinal));
        Assert.Contains(outcome.Manifest, r => r.Source.EndsWith("/claude-config/projects", StringComparison.Ordinal) && (r.Status, r.Reason) == ("excluded", "symlink"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Item8_ALinkedDirectoryUnderTheRunIsListedNotRecursed(bool junction)
    {
        StandardRun();
        string outside = Path.Combine(_fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "private.txt"), "outside-the-plan-secret-marker\n");
        MakeDirectoryLink(Path.Combine(_fixture.RunLogs, "linked-dir"), outside, junction);

        BundleOutcome outcome = _fixture.Build();

        Assert.All(outcome.AllText(), t => Assert.DoesNotContain("outside-the-plan-secret-marker", t, StringComparison.Ordinal));
        Assert.Contains(outcome.Manifest, r => r.Source.EndsWith("/linked-dir", StringComparison.Ordinal) && (r.Status, r.Reason) == ("excluded", "symlink"));
        Assert.DoesNotContain(outcome.Manifest, r => r.Source.EndsWith("/private.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Item4_APartialLineWindowScrubsAKnownValueSuffixAndMasksALeadingToken()
    {
        StandardRun();
        // One line longer than the log-class cap: the window starts 10 characters before the known token ends.
        string secret = BundlePlanFixture.KnownToken;
        string filler = new('z', (int)BundleCatalog.LogCap - 10);
        _fixture.Log("events.jsonl", "prefix " + secret + filler);
        // And one where the window starts inside an unknown token.
        _fixture.Log("autonomy.jsonl", "prefix Qx8QZ3kf9LmNpR2sT4vW6yB1dF5hJ7" + new string(' ', (int)BundleCatalog.LogCap - 10) + "end");

        BundleOutcome outcome = _fixture.Build();

        string events = outcome.Text("run/events.jsonl")!;
        Assert.StartsWith(BundleFileReader.PartialLineMarker + "[REDACTED:QWEN_TOKEN#1]zzz", events, StringComparison.Ordinal);
        Assert.DoesNotContain(secret[^10..], events, StringComparison.Ordinal);
        Assert.Equal(("tail", "partial-line"), Row(outcome, "run/events.jsonl"));

        string autonomy = outcome.Text("run/autonomy.jsonl")!;
        Assert.StartsWith(BundleFileReader.PartialLineMarker + "[REDACTED:partial-token] ", autonomy, StringComparison.Ordinal);
        Assert.DoesNotContain("dF5hJ7", autonomy, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ub4dor&3xSecretTail\",\"next\":1", "&3xSecretTail")]
    [InlineData("Q8\\/Zq7Kf9LmNpTail\",\"n\":2", "Zq7Kf9LmNpTail")]
    public void Item4_TheLeadingTokenMaskRunsPastAmpersandAndBackslash(string windowStart, string tail)
    {
        StandardRun();
        _fixture.Log("observer.jsonl", "prefix " + new string('y', 64) + windowStart + new string(' ', (int)BundleCatalog.LogCap - windowStart.Length - 3) + "end");
        // The window (the last LogCap bytes) begins exactly at windowStart.
        string text = _fixture.Build().Text("run/observer.jsonl")!;

        Assert.StartsWith(BundleFileReader.PartialLineMarker + "[REDACTED:partial-token]\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain(tail, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Item6_UnderTaskTheNewestUnattributedSessionStillShips()
    {
        StandardRun(); // proj-a/session-1.jsonl: unattributed, as every serial-mode session is
        string newest = Path.Combine(_fixture.RunLogs, "claude-config", "projects", "proj-a", "session-1.jsonl");
        _fixture.Log("claude-config/projects/proj-b/older.jsonl", "{\"t\":0}\n");
        BundleProbes probes = _fixture.Probes() with
        {
            Stat = path => new BundleFileStat(1, path == newest ? BundlePlanFixture.Clock : BundlePlanFixture.Clock.AddHours(-2)),
        };

        BundleOutcome outcome = _fixture.Build(new BundleOptions { Tasks = ["02-second"] }, probes);

        Assert.Contains(outcome.Entries, e => e.Key.EndsWith("/session-1.jsonl", StringComparison.Ordinal));
        Assert.Contains(outcome.Manifest, r => r.BundlePath?.EndsWith("/older.jsonl", StringComparison.Ordinal) == true && r.Reason == "task-filter");
    }

    [Fact]
    public void Item7_AWorktreeRootTheJournalRecordedIsAcceptedAndASkipNamesBothRoots()
    {
        string recordedRoot = Path.Combine(_fixture.Root, "wt-recorded");
        string segment = Path.Combine(recordedRoot, BundlePlanFixture.RunId, "02-second", "attempt-1");
        Directory.CreateDirectory(segment);
        _fixture.WriteJournal(BundlePlanFixture.Journal(secondAttempts: [BundlePlanFixture.Attempt(1, AttemptOutcome.GuardrailFailed, segment, "abc1234")]));
        _fixture.PromptAttempt("02-second", 1, "x");
        _fixture.Log("02-second/attempt-1/attempt-provenance.json",
            $"{{\"model\":\"m\",\"worktreePath\":{System.Text.Json.JsonSerializer.Serialize(segment)},\"baseCommit\":\"abc1234\"}}\n");
        string shellRoot = Path.Combine(_fixture.Root, "wt-from-shell");

        BundleOutcome accepted = _fixture.Build(probes: _fixture.Probes() with { WorktreeRoot = shellRoot });
        Assert.Contains("$ git status", accepted.Text("git/02-second.txt"), StringComparison.Ordinal);

        string outside = Path.Combine(_fixture.Root, "elsewhere", "x", "y", "z");
        Directory.CreateDirectory(outside);
        _fixture.Log("02-second/attempt-1/attempt-provenance.json",
            $"{{\"model\":\"m\",\"worktreePath\":{System.Text.Json.JsonSerializer.Serialize(outside)},\"baseCommit\":\"abc1234\"}}\n");
        string skipped = _fixture.Build(probes: _fixture.Probes() with { WorktreeRoot = shellRoot }).Text("git/02-second.txt")!;
        Assert.Contains("the worktree root from the bundling shell is", skipped, StringComparison.Ordinal);
        Assert.Contains("the journal recorded", skipped, StringComparison.Ordinal);
    }

    [Fact]
    public void Item9_AHalvedRetryThatStillTimesOutIsAScanTimeoutNotATrim()
    {
        StandardRun();
        var stream = new StringBuilder();
        while (stream.Length < 400_000)
        {
            stream.Append("{\"type\":\"assistant\",\"text\":\"working\"}\n");
        }

        stream.Append($"{{\"text\":\"token {BundlePlanFixture.KnownToken} here\"}}\n");
        _fixture.Log("02-second/attempt-1/claude-stream.jsonl", stream.ToString());
        BundleProbes alwaysTimingOut = _fixture.Probes() with
        {
            Redact = (text, context) => context.ArtifactPath.EndsWith("claude-stream.jsonl", StringComparison.Ordinal)
                ? throw new System.Text.RegularExpressions.RegexMatchTimeoutException("x", "y", TimeSpan.FromSeconds(10))
                : BundleRedactor.Redact(text, context),
        };

        BundleOutcome outcome = _fixture.Build(probes: alwaysTimingOut);

        Assert.Equal(("excluded", "scan-timeout"), Row(outcome, "tasks/02-second/attempt-1/claude-stream.jsonl"));
    }

    [Fact]
    public void Item9_ACancelledBundleStartsNoChildAndKillsARunningOne()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using (BundleProcess.CancelWith(cancelled.Token))
        {
            BundleProcessResult notStarted = BundleProcess.Run("git", ["--version"], Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(30));
            Assert.True(notStarted.Cancelled);
            Assert.False(notStarted.Succeeded);
        }

        using var midway = new CancellationTokenSource();
        using (BundleProcess.CancelWith(midway.Token))
        {
            (string exe, string[] args) = OperatingSystem.IsWindows()
                ? ("ping", new[] { "-n", "60", "127.0.0.1" })
                : ("sleep", new[] { "60" });
            midway.CancelAfter(TimeSpan.FromMilliseconds(300));
            BundleProcessResult killed = BundleProcess.Run(exe, args, Directory.GetCurrentDirectory(), TimeSpan.FromMinutes(5));
            Assert.True(killed.Cancelled, "the child was not cancelled with the bundle");
            Assert.False(killed.TimedOut);
        }
    }

    // ------------------------------------------------------------------ #812: identifiers and the served model

    [Fact]
    public void AnIdentifierReadsTheSameInEveryFileAndIsCountedInRedactionsMd()
    {
        StandardRun();
        const string tool = "call_Xq7Lk9Zp2Mw8Rt4VbN3c";
        const string session = "5649d0ff-7dee-435f-b28e-452624770dcb";
        _fixture.Log("02-second/attempt-1/claude-stream.jsonl",
            $"{{\"type\":\"assistant\",\"message\":{{\"content\":[{{\"type\":\"tool_use\",\"id\":\"{tool}\"}}]}},\"session_id\":\"{session}\"}}\n" +
            $"{{\"type\":\"user\",\"message\":{{\"content\":[{{\"tool_use_id\":\"{tool}\",\"type\":\"tool_result\"}}]}},\"session_id\":\"{session}\"}}\n" +
            $"{{\"type\":\"assistant\",\"text\":\"token {BundlePlanFixture.KnownToken} here\"}}\n");
        _fixture.Log("claude-config/projects/proj-a/session-1.jsonl", $"{{\"sessionId\":\"x\",\"session_id\":\"{session}\",\"id\":\"{tool}\"}}\n");

        BundleOutcome outcome = _fixture.Build();
        string stream = outcome.Text("tasks/02-second/attempt-1/claude-stream.jsonl")!;
        string sessionFile = outcome.Text("gateway/sessions/project-1/session-1.jsonl")!;

        string toolToken = System.Text.RegularExpressions.Regex.Match(stream, "\"id\":\"(\\[id-\\d+\\])\"").Groups[1].Value;
        string sessionToken = System.Text.RegularExpressions.Regex.Match(stream, "\"session_id\":\"(\\[id-\\d+\\])\"").Groups[1].Value;
        Assert.NotEqual(toolToken, sessionToken);
        Assert.Contains($"\"tool_use_id\":\"{toolToken}\"", stream, StringComparison.Ordinal);
        Assert.Contains($"\"session_id\":\"{sessionToken}\"", sessionFile, StringComparison.Ordinal);
        Assert.Contains($"\"id\":\"{toolToken}\"", sessionFile, StringComparison.Ordinal);
        Assert.All(outcome.AllText(), t => Assert.DoesNotContain(tool, t, StringComparison.Ordinal));
        Assert.All(outcome.AllText(), t => Assert.DoesNotContain(session, t, StringComparison.Ordinal));

        string redactions = outcome.Text("REDACTIONS.md")!;
        Assert.Contains($"| tasks/02-second/attempt-1/claude-stream.jsonl | {BundleRedactor.PseudonymKind} | 4 |", redactions, StringComparison.Ordinal);
        Assert.Contains($"| gateway/sessions/project-1/session-1.jsonl | {BundleRedactor.PseudonymKind} | 2 |", redactions, StringComparison.Ordinal);

        // Deterministic: the same input gives the same bytes, pseudonyms included.
        Assert.Equal(outcome.Zip, _fixture.Build().Zip);
    }

    [Fact]
    public void TheServedModelFileNameSurvivesInSummaryRouteLogJournalAndProvenance()
    {
        const string backend = "http://127.0.0.1:8080 /opt/models/Qwen3.6-35B-A3B-MXFP4_MOE.gguf";
        _fixture.Plan = _fixture.Plan with
        {
            Config = _fixture.Plan.Config with
            {
                PromptRunners = new Dictionary<string, PromptRunnerConfig>
                {
                    ["claude"] = BundlePlanFixture.Runner("claude", baseUrl: "http://127.0.0.1:4000"),
                },
            },
        };
        AttemptRecord attempt = BundlePlanFixture.Attempt(1, AttemptOutcome.GuardrailFailed) with
        {
            Provenance = new AttemptProvenance
            {
                Model = "qwen-3.6-35b-mtp", Runner = "claude", Gateway = "http://127.0.0.1:4000", BackendModel = backend,
            },
        };
        _fixture.WriteJournal(BundlePlanFixture.Journal(secondAttempts: [attempt]));
        _fixture.PromptAttempt("02-second", 1, "x");
        _fixture.Log("02-second/attempt-1/attempt-route.log", $"model: qwen-3.6-35b-mtp\nbackend model: {backend}\n");
        _fixture.Log("02-second/attempt-1/attempt-provenance.json", $"{{\"model\":\"qwen-3.6-35b-mtp\",\"backendModel\":\"{backend}\"}}\n");
        _fixture.Log("observer.jsonl", $"{{\"kind\":\"attempt-started\",\"backendModel\":\"{backend}\",\"model\":\"Qwen3.6-35B-A3B-MXFP4_MOE\"}}\n");

        BundleOutcome outcome = _fixture.Build();

        foreach (string path in new[]
                 {
                     "SUMMARY.md", "state/run.json", "tasks/02-second/attempt-1/attempt-route.log",
                     "tasks/02-second/attempt-1/attempt-provenance.json", "run/observer.jsonl",
                 })
        {
            string text = outcome.Text(path)!;
            Assert.True(text.Contains("Qwen3.6-35B-A3B-MXFP4_MOE.gguf", StringComparison.Ordinal), $"{path} lost the served model file name");
            Assert.DoesNotContain("[REDACTED:high-entropy]", text, StringComparison.Ordinal);
        }

        Assert.Contains("verified: http://127.0.0.1:8080 /opt/models/Qwen3.6-35B-A3B-MXFP4_MOE.gguf", outcome.Text("SUMMARY.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheProtectedLatestStreamIsNeverLostToTimingAlone()
    {
        // 02-second is needs-human, so attempt 1 is the latest attempt of a failing task. A scan that times out on its
        // stream is retried on a halved tail instead of excluding the file.
        StandardRun();
        var stream = new StringBuilder();
        while (stream.Length < 400_000)
        {
            stream.Append("{\"type\":\"assistant\",\"text\":\"working on it\"}\n");
        }

        // The transcript carries the known token; so must the stream's tail, or pass 4 would exclude it for that instead.
        stream.Append($"{{\"type\":\"assistant\",\"text\":\"token {BundlePlanFixture.KnownToken} here\"}}\n");

        _fixture.Log("02-second/attempt-1/claude-stream.jsonl", stream.ToString());
        BundleProbes timingOutOnLargeInput = _fixture.Probes() with
        {
            Redact = (text, context) => context.ArtifactPath.EndsWith("claude-stream.jsonl", StringComparison.Ordinal) && text.Length > 200_000
                ? throw new System.Text.RegularExpressions.RegexMatchTimeoutException("x", "y", TimeSpan.FromSeconds(10))
                : BundleRedactor.Redact(text, context),
        };

        BundleOutcome outcome = _fixture.Build(probes: timingOutOnLargeInput);

        Assert.Equal(("tail", "scan-timeout"), Row(outcome, "tasks/02-second/attempt-1/claude-stream.jsonl"));
        int kept = outcome.Entries.Single(e => e.Key == "tasks/02-second/attempt-1/claude-stream.jsonl").Value.Length;
        Assert.InRange(kept, 64 * 1024, 200_000);
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
