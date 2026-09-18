using System.Text.Json;
using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;

namespace Guardrails.Integration.Tests.Supply;

/// <summary>
/// Design 41 §6 "Later gate halts" — the DECIDED answer to <c>d41-terminal-gate-names-supply</c>: the
/// TERMINAL gate (<see cref="PlanGuardrailPhase.EvaluateAsync"/>) must name supplied and refreshed content
/// in a failing gate's halt, exactly as <c>Scheduler.BuildGateHalt</c> already does for a wave ENTRY/EXIT
/// gate halt (design 39 §1c/§5, design 40 §4). Today <c>PlanGuardrailPhase.cs</c> never calls
/// <see cref="Journal.UnauthoredContentNote"/> at all — grep it and you get zero hits — so on a FLAT plan
/// (no waves) a terminal-gate halt never discloses an overwatcher supply or a post-delivery refresh, even
/// though design 40 §3 names the terminal gate as exactly where a wrongly chosen file does its damage.
/// <para>
/// <b>There is no stub file.</b> Every production member these tests drive already exists and is reachable:
/// <see cref="PlanGuardrailPhase.EvaluateAsync"/> is <c>public static</c>, and
/// <see cref="Journal.UnauthoredContentNote"/> is a shipped <c>public static class</c>. What is missing is
/// the CALL, not a type.
/// </para>
/// <para>
/// <b>Serial mode, no git.</b> A serial plan (<c>maxParallelism: 1</c>) resolves its evaluation workspace
/// as <c>plan.Workspace</c> directly (<c>PlanPhaseWorkspace.Resolve</c>) and never spawns a git worktree
/// probe, so the whole fixture lives in an ordinary temp directory — no <c>TempGitRepo</c> needed.
/// </para>
/// <para>
/// <b>RED vs declared-exempt.</b> The four "FailedTerminalGate_...NamesThe.../WritesOneDetailLinePer.../
/// ListsEveryRecordOldestFirst..." tests are expected to FAIL against the current tree — nothing appends
/// <see cref="Journal.UnauthoredContentNote"/>'s output anywhere in <c>PlanGuardrailPhase</c> yet.
/// <see cref="FailedTerminalGate_WithNoUnauthoredContent_KeepsAByteIdenticalHalt"/> and
/// <see cref="PassingTerminalGate_AfterASupply_WritesNoHaltAtAll"/> are declared EXEMPT from that census:
/// they assert the never-weaker half, which is true of code that calls
/// <see cref="Journal.UnauthoredContentNote.HeadlineSuffix"/> nowhere at all, so they are green BY
/// CONSTRUCTION — they exist to catch an implementation that over-fires once the call lands.
/// </para>
/// </summary>
[Trait("Category", "OverwatchSupply")]
public sealed class SuppliedTerminalGateHaltTests
{
    private static readonly bool Ps = OperatingSystem.IsWindows();

    private const string TerminalCheckName = "01-terminal";
    private const string BaseHeadline = "Terminal gate FAILED on the merged HEAD: " + TerminalCheckName;

    // Deliberately > 10 characters so the 10-char truncation UnauthoredContentNote.Truncate performs is
    // actually exercised (and distinguishable from a headline that dumped the whole value).
    private const string SupplyCommit = "abcdef1234567890fedcba";
    private const string SupplyCommitFirstTen = "abcdef1234";

    private const string RefreshUpstream = "9988776655443322110099";
    private const string RefreshUpstreamFirstTen = "9988776655";
    private const string RefreshCommit = "1a2b3c4d5e6f7a8b9c0d1e2f";
    private const string RefreshFrom = "feature/my-branch";

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // RED — nothing in PlanGuardrailPhase.cs calls UnauthoredContentNote yet.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FailedTerminalGate_AfterASupply_NamesTheSupplierAndShaInTheHaltHeadline()
    {
        using var fixture = new TerminalGateFixture();
        fixture.WriteJournal(supplied:
        [
            new SuppliedRecord
            {
                At = DateTimeOffset.UtcNow.AddMinutes(-5),
                Commit = SupplyCommit,
                Paths = ["vendor/mermaid.min.js"],
                Bytes = 4096,
                By = "overwatcher"
            }
        ]);

        RunHalt halt = await fixture.EvaluateFailingGateAsync();

        Assert.Contains("supplied by overwatcher at ", halt.Headline, StringComparison.Ordinal);
        Assert.Contains(SupplyCommitFirstTen, halt.Headline, StringComparison.Ordinal);
        // The truncation is what this test pins — a headline that dumps the whole sha must still be caught.
        Assert.DoesNotContain(SupplyCommit, halt.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedTerminalGate_AfterARefresh_NamesTheRefreshInTheHaltHeadline()
    {
        using var fixture = new TerminalGateFixture();
        fixture.WriteJournal(refreshed:
        [
            new RefreshedRecord
            {
                At = DateTimeOffset.UtcNow.AddMinutes(-5),
                Commit = RefreshCommit,
                From = RefreshFrom,
                Upstream = RefreshUpstream,
                DeliveredWave = "wave-01",
                Paths = ["src/Foo.cs"]
            }
        ]);

        RunHalt halt = await fixture.EvaluateFailingGateAsync();

        // The decision this task closes: "supplied AND refreshed" — a consumer that reads only
        // supplied[] is the exact defect UnauthoredContentNote exists to prevent.
        Assert.Contains($"refresh from '{RefreshFrom}'", halt.Headline, StringComparison.Ordinal);
        Assert.Contains(RefreshUpstreamFirstTen, halt.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain(RefreshUpstream, halt.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedTerminalGate_WritesOneDetailLinePerUnauthoredRecord()
    {
        using var fixture = new TerminalGateFixture();
        fixture.WriteJournal(
            supplied:
            [
                new SuppliedRecord
                {
                    At = DateTimeOffset.UtcNow.AddMinutes(-10),
                    Commit = SupplyCommit,
                    Paths = ["vendor/mermaid.min.js"],
                    Bytes = 4096,
                    By = "overwatcher"
                }
            ],
            refreshed:
            [
                new RefreshedRecord
                {
                    At = DateTimeOffset.UtcNow.AddMinutes(-5),
                    Commit = RefreshCommit,
                    From = RefreshFrom,
                    Upstream = RefreshUpstream,
                    DeliveredWave = "wave-01",
                    Paths = ["src/Foo.cs"]
                }
            ]);

        var heartbeatOut = new StringWriter();
        await fixture.EvaluateFailingGateAsync(heartbeatOut);
        string detail = heartbeatOut.ToString();

        // The FULL commit sha (not truncated) and the affected paths carry through to the detail lines —
        // the surface this task can add to (RunHalt has no Detail field of its own).
        Assert.Contains(SupplyCommit, detail, StringComparison.Ordinal);
        Assert.Contains("vendor/mermaid.min.js", detail, StringComparison.Ordinal);
        Assert.Contains(RefreshUpstream, detail, StringComparison.Ordinal);
        Assert.Contains(RefreshCommit, detail, StringComparison.Ordinal);
        Assert.Contains("src/Foo.cs", detail, StringComparison.Ordinal);

        int supplyLineCount = detail.Split('\n').Count(l => l.Contains(SupplyCommit, StringComparison.Ordinal));
        int refreshLineCount = detail.Split('\n').Count(l => l.Contains(RefreshCommit, StringComparison.Ordinal));
        Assert.Equal(1, supplyLineCount);
        Assert.Equal(1, refreshLineCount);
    }

    [Fact]
    public async Task FailedTerminalGate_ListsEveryRecordOldestFirst_AcrossBothSections()
    {
        DateTimeOffset t1 = DateTimeOffset.UtcNow.AddMinutes(-30);
        DateTimeOffset t2 = DateTimeOffset.UtcNow.AddMinutes(-20);
        DateTimeOffset t3 = DateTimeOffset.UtcNow.AddMinutes(-10);

        const string firstSupplyCommit = "1111111111aaaaaaaaaa";
        const string secondSupplyCommit = "3333333333cccccccccc";

        using var fixture = new TerminalGateFixture();
        fixture.WriteJournal(
            supplied:
            [
                new SuppliedRecord { At = t1, Commit = firstSupplyCommit, Paths = ["a.txt"], Bytes = 1, By = "operator" },
                new SuppliedRecord { At = t3, Commit = secondSupplyCommit, Paths = ["c.txt"], Bytes = 3, By = "operator" }
            ],
            refreshed:
            [
                new RefreshedRecord
                {
                    At = t2,
                    Commit = RefreshCommit,
                    From = RefreshFrom,
                    Upstream = RefreshUpstream,
                    DeliveredWave = "wave-01",
                    Paths = ["b.txt"]
                }
            ]);

        var heartbeatOut = new StringWriter();
        await fixture.EvaluateFailingGateAsync(heartbeatOut);
        string detail = heartbeatOut.ToString();

        int firstAt = detail.IndexOf(firstSupplyCommit, StringComparison.Ordinal);
        int refreshAt = detail.IndexOf(RefreshCommit, StringComparison.Ordinal);
        int secondAt = detail.IndexOf(secondSupplyCommit, StringComparison.Ordinal);

        Assert.True(firstAt >= 0, $"expected the T1 supply ({firstSupplyCommit}) in:\n{detail}");
        Assert.True(refreshAt >= 0, $"expected the T2 refresh ({RefreshCommit}) in:\n{detail}");
        Assert.True(secondAt >= 0, $"expected the T3 supply ({secondSupplyCommit}) in:\n{detail}");

        // Oldest first, ACROSS both sections — rejects concatenating supplied[] then refreshed[] (which
        // would put the T3 supply before the T2 refresh) and rejects newest-first order.
        Assert.True(firstAt < refreshAt,
            $"expected the T1 supply before the T2 refresh in:\n{detail}");
        Assert.True(refreshAt < secondAt,
            $"expected the T2 refresh before the T3 supply in:\n{detail}");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // DECLARED EXEMPT from the RED census — green on the current tree BY CONSTRUCTION, not despite it.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FailedTerminalGate_WithNoUnauthoredContent_KeepsAByteIdenticalHalt()
    {
        // Neither section present at all.
        using (var absent = new TerminalGateFixture())
        {
            absent.WriteJournal();
            RunHalt halt = await absent.EvaluateFailingGateAsync();
            Assert.Equal(BaseHeadline, halt.Headline);
        }

        // Both sections present but EMPTY — the shape a careless `?? []` produces.
        using (var empty = new TerminalGateFixture())
        {
            empty.WriteJournal(supplied: [], refreshed: []);
            RunHalt halt = await empty.EvaluateFailingGateAsync();
            Assert.Equal(BaseHeadline, halt.Headline);
        }
    }

    [Fact]
    public async Task PassingTerminalGate_AfterASupply_WritesNoHaltAtAll()
    {
        using var fixture = new TerminalGateFixture(terminalGatePasses: true);
        fixture.WriteJournal(supplied:
        [
            new SuppliedRecord
            {
                At = DateTimeOffset.UtcNow.AddMinutes(-5),
                Commit = SupplyCommit,
                Paths = ["vendor/mermaid.min.js"],
                Bytes = 4096,
                By = "overwatcher"
            }
        ]);

        bool passed = await fixture.EvaluateAsync(heartbeatOut: null);
        Assert.True(passed, "the terminal gate fixture's check is deliberately GREEN here and must pass.");

        JournalDocument doc = fixture.ReadJournal();
        // The disclosure rides on a halt; it must not become an unconditional announcement on every
        // green run.
        Assert.Null(doc.Halt);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // TerminalGateFixture — a single-task SERIAL plan (maxParallelism 1, workspace ".", no git) with a
    // plan-level <plan>/guardrails/ terminal check that is RED (or GREEN, on request). No process-wide
    // state is touched: nothing here sets an environment variable, changes cwd, or touches the console.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class TerminalGateFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _planDir;

        public TerminalGateFixture(bool terminalGatePasses = false)
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-term-halt-" + Guid.NewGuid().ToString("N"));
            _planDir = Path.Combine(_root, "plan");
            Directory.CreateDirectory(_planDir);
            Directory.CreateDirectory(Path.Combine(_planDir, "state"));
            Directory.CreateDirectory(Path.Combine(_planDir, "tasks"));

            File.WriteAllText(Path.Combine(_planDir, "guardrails.json"),
                """
                {
                  "version": 1,
                  "guardrailMode": "failFast",
                  "workspace": ".",
                  "defaultRetries": 0,
                  "maxParallelism": 1
                }
                """);

            WriteTask("01-a");

            string terminalDir = Path.Combine(_planDir, "guardrails");
            Directory.CreateDirectory(terminalDir);
            string terminalPath = Path.Combine(terminalDir, Ps ? $"{TerminalCheckName}.ps1" : $"{TerminalCheckName}.sh");
            WriteScript(terminalPath, TerminalCheckScript(passes: terminalGatePasses));
        }

        /// <summary>
        /// Hand-write <c>state/run.json</c> BEFORE <see cref="EvaluateAsync"/> runs — <c>PlanGuardrailPhase</c>
        /// persists through <c>PlanPhaseJournalWriter.Update</c>, which re-reads the document from disk, and
        /// that throws on a missing file. Hand-writing (rather than driving <c>guardrails supply</c>) is the
        /// honest instrument here: these tests exercise the DISCLOSURE, not the supply mechanism.
        /// </summary>
        public void WriteJournal(
            IReadOnlyList<SuppliedRecord>? supplied = null,
            IReadOnlyList<RefreshedRecord>? refreshed = null)
        {
            var document = new JournalDocument
            {
                RunId = "2026-09-18T00-00-00Z-test",
                PlanHash = "sha256:terminal-gate-halt-fixture",
                Supplied = supplied,
                Refreshed = refreshed
            };

            File.WriteAllText(
                RunJournal.PathFor(_planDir),
                JsonSerializer.Serialize(document, JournalJson.Options));
        }

        /// <summary>Load the fixture plan and run the real terminal phase. Null <c>runId</c> ⇒ no artifact capture.</summary>
        public async Task<bool> EvaluateAsync(TextWriter? heartbeatOut)
        {
            PlanLoadResult load = new PlanLoader().Load(_planDir);
            Assert.NotNull(load.Plan);
            Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

            return await PlanGuardrailPhase.EvaluateAsync(
                load.Plan!, new ProcessRunner(), heartbeatOut, runId: null,
                TestContext.Current.CancellationToken);
        }

        /// <summary>As <see cref="EvaluateAsync"/>, asserting the (deliberately RED) gate failed, and returning the recorded halt.</summary>
        public async Task<RunHalt> EvaluateFailingGateAsync(TextWriter? heartbeatOut = null)
        {
            bool passed = await EvaluateAsync(heartbeatOut);
            Assert.False(passed, "the terminal gate fixture's check is deliberately RED and must fail.");

            JournalDocument doc = ReadJournal();
            Assert.NotNull(doc.Halt);
            Assert.Equal(RunHaltKind.PlanGuardrailFailed, doc.Halt!.Kind);
            return doc.Halt!;
        }

        public JournalDocument ReadJournal() => JournalReader.Read(RunJournal.PathFor(_planDir));

        private void WriteTask(string id)
        {
            string taskDir = Path.Combine(_planDir, "tasks", id);
            Directory.CreateDirectory(taskDir);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                """
                { "description": "terminal-gate-halt fixture task", "writeScope": ["src/**"], "dependsOn": [] }
                """);

            WriteScript(Path.Combine(taskDir, Ps ? "action.ps1" : "action.sh"), TrivialScript());
            WriteScript(Path.Combine(taskDir, "guardrails", Ps ? "01-check.ps1" : "01-check.sh"), TrivialScript());
        }

        public void Dispose() => SafeDelete.DeleteDirectory(_root);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Script helpers — mirror PlanGuardrailPhaseTests' OS-picked interpreter convention.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static string TrivialScript() => Ps ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n";

    /// <summary>
    /// The plan-level <c>&lt;plan&gt;/guardrails/</c> terminal check. Opens with the required
    /// <c>catches:</c> declaration (GR2027, enforced on plan-level folders). RED (exit 1) unless
    /// <paramref name="passes"/>.
    /// </summary>
    private static string TerminalCheckScript(bool passes)
    {
        int code = passes ? 0 : 1;
        if (Ps)
        {
            return
                "# catches: a terminal-gate failure that never discloses supplied/refreshed content in its halt\n" +
                "exit " + code + "\n";
        }

        return
            "#!/usr/bin/env bash\n" +
            "# catches: a terminal-gate failure that never discloses supplied/refreshed content in its halt\n" +
            "exit " + code + "\n";
    }

    private static void WriteScript(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }
}
