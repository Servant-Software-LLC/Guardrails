using System.Text.Json;
using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;

namespace Guardrails.Integration.Tests.Supply;

/// <summary>
/// Design 41 §6 "Later gate halts" — the DECIDED answer to <c>d41-terminal-gate-names-supply</c>: the
/// terminal gate must halt naming supplied and refreshed content, exactly as
/// <c>Scheduler.BuildGateHalt</c> already does for a wave ENTRY/EXIT gate
/// (<c>src/Guardrails.Core/Execution/Scheduler.cs</c>, <see cref="UnauthoredContentNote"/>). The terminal
/// gate, <see cref="PlanGuardrailPhase"/>, does not — grep it and today it names
/// <see cref="UnauthoredContentNote"/> nowhere. On a FLAT plan (no waves) the terminal gate is the ONLY
/// gate a run ever reaches, so without this closed, no halt a flat plan produces ever mentions an
/// overwatcher supply at all, even though design 40 §3 names the terminal gate as exactly where a wrongly
/// chosen file does its damage.
/// <para>
/// <b>There is no stub file.</b> Every production member these tests drive already exists and is
/// reachable: <see cref="PlanGuardrailPhase.EvaluateAsync"/> is <c>public static</c>, and
/// <see cref="UnauthoredContentNote"/> is a shipped <c>public static class</c> with
/// <c>HeadlineSuffix(JournalDocument)</c> and <c>DetailLines(JournalDocument)</c>. What is missing is the
/// CALL from <c>PlanGuardrailPhase.cs</c>, not a type — this file's writeScope is itself alone and holds no
/// production path, deliberately.
/// </para>
/// <para>
/// <b>Serial mode, no git.</b> Every fixture below sets <c>"maxParallelism": 1</c>, so
/// <c>PlanPhaseWorkspace.Resolve</c> returns <see cref="PlanDefinition.Workspace"/> directly
/// (<c>SchedulerFactory.ResolveWorktreeMode</c> returns early for <c>MaxParallelism &lt;= 1</c>, before any
/// git probe) and the whole fixture lives in an ordinary temp directory.
/// </para>
/// <para>
/// TDD red: today <c>PlanGuardrailPhase.cs</c> mentions <see cref="UnauthoredContentNote"/> nowhere, so the
/// four headline/detail-line tests below are expected to FAIL against this tree.
/// <see cref="FailedTerminalGate_WithNoUnauthoredContent_KeepsAByteIdenticalHalt"/> and
/// <see cref="PassingTerminalGate_AfterASupply_WritesNoHaltAtAll"/> are declared EXEMPT from that red
/// census — <see cref="UnauthoredContentNote.HeadlineSuffix"/> already returns <c>null</c> for an
/// empty/absent disclosure, so a correct implementation adds nothing and these are green BY CONSTRUCTION,
/// not despite it; they exist to catch an implementation that over-fires once the disclosure is wired.
/// </para>
/// </summary>
[Trait("Category", "OverwatchSupply")]
public sealed class SuppliedTerminalGateHaltTests
{
    private const string CheckName = "01-terminal-check";
    private const string RunId = "2026-09-16T10-00-00Z-d41t";
    private const string PlanHashValue =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>
    /// The exact headline <c>PlanGuardrailPhase.EvaluateAsync</c> writes today for one failing check named
    /// <see cref="CheckName"/> — the byte-identical baseline
    /// <see cref="FailedTerminalGate_WithNoUnauthoredContent_KeepsAByteIdenticalHalt"/> pins.
    /// </summary>
    private const string ExpectedByteIdenticalHeadline =
        "Terminal gate FAILED on the merged HEAD: " + CheckName;

    // ── the four pinned RED behaviours ──────────────────────────────────────────────────────

    [Fact]
    public async Task FailedTerminalGate_AfterASupply_NamesTheSupplierAndShaInTheHaltHeadline()
    {
        using var fixture = new PlanFixture(checkPasses: false);
        fixture.SeedJournal(supplied:
        [
            new SuppliedRecord
            {
                At = DateTimeOffset.Parse("2026-09-16T09:00:00Z"),
                Commit = "2ada000111222333444555666777888999aaabb",
                Paths = ["vendor/mermaid.min.js"],
                Bytes = 214_512,
                By = "overwatcher"
            }
        ]);

        (bool passed, _) = await fixture.RunGateAsync();
        Assert.False(passed);

        RunHalt halt = ReadHalt(fixture.PlanDir);
        Assert.Contains("supplied by overwatcher at ", halt.Headline, StringComparison.Ordinal);
        // First TEN characters of the commit sha — and only those, so an implementation that dumps the
        // full sha into the headline is still caught.
        Assert.Contains("2ada000111", halt.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("2ada000111222333444555666777888999aaabb", halt.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedTerminalGate_AfterARefresh_NamesTheRefreshInTheHaltHeadline()
    {
        using var fixture = new PlanFixture(checkPasses: false);
        fixture.SeedJournal(refreshed:
        [
            new RefreshedRecord
            {
                At = DateTimeOffset.Parse("2026-09-16T09:00:00Z"),
                Commit = "7e1d000111222333444555666777888999aaabb",
                From = "master",
                Upstream = "4c9a000111222333444555666777888999aaabb",
                DeliveredWave = "wave-01-scaffold",
                Paths = ["src/Teammate.cs"]
            }
        ]);

        (bool passed, _) = await fixture.RunGateAsync();
        Assert.False(passed);

        // §6's own reminder, the half a reader forgets: "supplied AND refreshed" — a consumer that reads
        // only supplied[] is the exact defect UnauthoredContentNote exists to prevent.
        RunHalt halt = ReadHalt(fixture.PlanDir);
        Assert.Contains("refresh from 'master'", halt.Headline, StringComparison.Ordinal);
        // First TEN characters of the UPSTREAM sha (what was merged), never the commit (the merge itself).
        Assert.Contains("4c9a000111", halt.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("4c9a000111222333444555666777888999aaabb", halt.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedTerminalGate_WritesOneDetailLinePerUnauthoredRecord()
    {
        using var fixture = new PlanFixture(checkPasses: false);
        var supplied = new SuppliedRecord
        {
            At = DateTimeOffset.Parse("2026-09-16T09:00:00Z"),
            Commit = "2ada000111222333444555666777888999aaabb",
            Paths = ["vendor/mermaid.min.js"],
            Bytes = 214_512,
            By = "overwatcher"
        };
        var refreshed = new RefreshedRecord
        {
            At = DateTimeOffset.Parse("2026-09-16T09:30:00Z"),
            Commit = "7e1d000111222333444555666777888999aaabb",
            From = "master",
            Upstream = "4c9a000111222333444555666777888999aaabb",
            DeliveredWave = "wave-01-scaffold",
            Paths = ["src/Teammate.cs"]
        };
        fixture.SeedJournal(supplied: [supplied], refreshed: [refreshed]);

        (bool passed, string heartbeatText) = await fixture.RunGateAsync();
        Assert.False(passed);

        // The DETAIL surface is not truncated, unlike the headline — the full sha must reach the operator
        // somewhere, and this is the one place it does.
        Assert.Contains(supplied.Commit, heartbeatText, StringComparison.Ordinal);
        Assert.Contains(refreshed.Commit, heartbeatText, StringComparison.Ordinal);
        Assert.Contains(refreshed.Upstream, heartbeatText, StringComparison.Ordinal);
        Assert.Contains("vendor/mermaid.min.js", heartbeatText, StringComparison.Ordinal);
        Assert.Contains("src/Teammate.cs", heartbeatText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedTerminalGate_ListsEveryRecordOldestFirst_AcrossBothSections()
    {
        using var fixture = new PlanFixture(checkPasses: false);
        var supplyT1 = new SuppliedRecord
        {
            At = DateTimeOffset.Parse("2026-09-16T08:00:00Z"),
            Commit = "1111111111222222222233333333334444444455",
            Paths = ["vendor/a.js"],
            Bytes = 100,
            By = "operator"
        };
        var refreshT2 = new RefreshedRecord
        {
            At = DateTimeOffset.Parse("2026-09-16T09:00:00Z"),
            Commit = "2222222222333333333344444444445555555566",
            From = "master",
            Upstream = "3333333333444444444455555555556666666677",
            DeliveredWave = "wave-01-scaffold",
            Paths = ["src/b.cs"]
        };
        var supplyT3 = new SuppliedRecord
        {
            At = DateTimeOffset.Parse("2026-09-16T10:00:00Z"),
            Commit = "4444444444555555555566666666667777777788",
            Paths = ["vendor/c.js"],
            Bytes = 300,
            By = "task:05-author-tests-provenance"
        };
        // Never adjacent-only, and never concatenated by section: the two supplies are on either side of
        // the one refresh, in TIME order, not section order.
        fixture.SeedJournal(supplied: [supplyT1, supplyT3], refreshed: [refreshT2]);

        (bool passed, _) = await fixture.RunGateAsync();
        Assert.False(passed);

        RunHalt halt = ReadHalt(fixture.PlanDir);
        int indexT1 = halt.Headline.IndexOf(supplyT1.Commit[..10], StringComparison.Ordinal);
        int indexT2 = halt.Headline.IndexOf(refreshT2.Upstream[..10], StringComparison.Ordinal);
        int indexT3 = halt.Headline.IndexOf(supplyT3.Commit[..10], StringComparison.Ordinal);

        Assert.True(
            indexT1 >= 0 && indexT2 > indexT1 && indexT3 > indexT2,
            $"expected oldest-first order T1 < T2 < T3 in the halt headline, got indices " +
            $"{indexT1}, {indexT2}, {indexT3}: {halt.Headline}");
    }

    // ── declared EXEMPT from the red census: green on the current tree, by construction ────────

    [Fact]
    public async Task FailedTerminalGate_WithNoUnauthoredContent_KeepsAByteIdenticalHalt()
    {
        using var fixture = new PlanFixture(checkPasses: false);

        fixture.SeedJournal();
        (bool passedNoSections, _) = await fixture.RunGateAsync();
        Assert.False(passedNoSections);
        RunHalt haltNoSections = ReadHalt(fixture.PlanDir);
        Assert.Equal(ExpectedByteIdenticalHeadline, haltNoSections.Headline);

        // The shape a careless `?? []` produces: both sections PRESENT but EMPTY must render exactly the
        // same as neither section being present at all.
        fixture.SeedJournal(supplied: [], refreshed: []);
        (bool passedEmptySections, _) = await fixture.RunGateAsync();
        Assert.False(passedEmptySections);
        RunHalt haltEmptySections = ReadHalt(fixture.PlanDir);
        Assert.Equal(ExpectedByteIdenticalHeadline, haltEmptySections.Headline);
    }

    [Fact]
    public async Task PassingTerminalGate_AfterASupply_WritesNoHaltAtAll()
    {
        using var fixture = new PlanFixture(checkPasses: true);
        fixture.SeedJournal(supplied:
        [
            new SuppliedRecord
            {
                At = DateTimeOffset.Parse("2026-09-16T09:00:00Z"),
                Commit = "2ada000111222333444555666777888999aaabb",
                Paths = ["vendor/mermaid.min.js"],
                Bytes = 214_512,
                By = "overwatcher"
            }
        ]);

        (bool passed, _) = await fixture.RunGateAsync();
        Assert.True(passed);

        // The disclosure rides on the halt; it must not become an unconditional announcement on every
        // green run.
        JournalDocument document = JournalReader.Read(RunJournal.PathFor(fixture.PlanDir));
        Assert.Null(document.Halt);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static RunHalt ReadHalt(string planDir)
    {
        JournalDocument document = JournalReader.Read(RunJournal.PathFor(planDir));
        Assert.NotNull(document.Halt);
        return document.Halt!;
    }

    /// <summary>
    /// A minimal, git-free plan folder — <c>maxParallelism: 1</c> so
    /// <c>Guardrails.Cli.PlanPhaseWorkspace.Resolve</c> returns <see cref="PlanDefinition.Workspace"/>
    /// directly and never probes for a repository (see the type remarks above). One task, never executed
    /// (these tests drive <see cref="PlanGuardrailPhase.EvaluateAsync"/> directly, not the Scheduler), and
    /// one plan-level <c>guardrails/</c> check whose exit code is fixed at construction.
    /// </summary>
    private sealed class PlanFixture : IDisposable
    {
        public string PlanDir { get; }

        public PlanFixture(bool checkPasses)
        {
            PlanDir = Path.Combine(Path.GetTempPath(), "gr41-tgh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(PlanDir, "state"));

            File.WriteAllText(Path.Combine(PlanDir, "guardrails.json"),
                """
                {
                  "version": 1,
                  "workspace": ".",
                  "maxParallelism": 1
                }
                """);

            WriteTask();
            WriteGuardrailsFolder(checkPasses);
        }

        /// <summary>
        /// Hand-write <c>state/run.json</c> — the honest instrument here: these tests exercise the
        /// DISCLOSURE, not the supply/refresh mechanism itself.
        /// </summary>
        public void SeedJournal(
            IReadOnlyList<SuppliedRecord>? supplied = null, IReadOnlyList<RefreshedRecord>? refreshed = null)
        {
            var document = new JournalDocument
            {
                RunId = RunId,
                PlanHash = PlanHashValue,
                Supplied = supplied,
                Refreshed = refreshed
            };

            File.WriteAllText(
                RunJournal.PathFor(PlanDir), JsonSerializer.Serialize(document, JournalJson.Options));
        }

        public async Task<(bool Passed, string HeartbeatText)> RunGateAsync()
        {
            PlanLoadResult load = new PlanLoader().Load(PlanDir);
            Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
            Assert.NotNull(load.Plan);

            var heartbeatOut = new StringWriter { NewLine = "\n" };
            bool passed = await PlanGuardrailPhase.EvaluateAsync(
                load.Plan!, new ProcessRunner(), heartbeatOut, runId: null,
                TestContext.Current.CancellationToken);

            return (passed, heartbeatOut.ToString());
        }

        public void Dispose() => SafeDelete.DeleteDirectory(PlanDir);

        private void WriteTask()
        {
            string taskDir = Path.Combine(PlanDir, "tasks", "01-task");
            Directory.CreateDirectory(taskDir);
            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                """
                {
                  "description": "a minimal task; never executed by these tests"
                }
                """);

            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path.Combine(taskDir, "action.ps1"), "exit 0\n");
            }
            else
            {
                string actionPath = Path.Combine(taskDir, "action.sh");
                File.WriteAllText(actionPath, "#!/usr/bin/env bash\nexit 0\n");
                File.SetUnixFileMode(actionPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }

        /// <summary>
        /// The plan-level <c>&lt;plan&gt;/guardrails/</c> folder (SSOT §4) — a SINGLE check, named
        /// <see cref="CheckName"/>, whose exit code <paramref name="checkPasses"/> fixes. Every file here
        /// must open with a <c>catches:</c> comment (GR2027).
        /// </summary>
        private void WriteGuardrailsFolder(bool checkPasses)
        {
            string guardrailsDir = Path.Combine(PlanDir, "guardrails");
            Directory.CreateDirectory(guardrailsDir);
            string exitLine = checkPasses ? "exit 0\n" : "exit 1\n";

            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path.Combine(guardrailsDir, $"{CheckName}.ps1"),
                    "# catches: content the terminal gate must fail the run on\n" + exitLine);
            }
            else
            {
                string checkPath = Path.Combine(guardrailsDir, $"{CheckName}.sh");
                File.WriteAllText(checkPath,
                    "#!/usr/bin/env bash\n# catches: content the terminal gate must fail the run on\n" + exitLine);
                File.SetUnixFileMode(checkPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }
    }
}
