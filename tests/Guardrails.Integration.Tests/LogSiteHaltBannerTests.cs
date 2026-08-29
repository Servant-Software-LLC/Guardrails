using System.CommandLine;
using System.Diagnostics;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Cli.Ui;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #436 — the log site must SURFACE the <c>halt</c> record issue #432 started persisting.
///
/// <para>
/// #432 closed the "the evidence is LOST" half of the story: a failed gate now captures each check's
/// stdout/stderr/result under <c>logs/&lt;runId&gt;/…</c> and records a top-level <c>halt</c> in
/// <c>run.json</c>. It left the "the evidence is UNSURFACED" half: <c>index.html</c> — the page the run
/// itself prints as "All tasks (static log site)" — did not read <c>halt</c>, so the human who followed the
/// harness's own link landed on a table of silent <c>pending</c> rows and learned nothing. These tests pin
/// the render for all four gate kinds, and pin that a run which did NOT halt renders exactly as before.
/// </para>
///
/// <para>
/// The four halt tests drive the REAL <c>run</c> command over a real git repo with OS-picked
/// <c>.ps1</c>/<c>.sh</c> gate scripts, so the assertions cover the whole path — gate → journal → renderer
/// → the file on disk — rather than a hand-built <see cref="RunHalt"/> a broken composition root would
/// still satisfy. Each asserts the banner exists, leads the page, names the failing check with its reason,
/// and that the link it renders into the captured output RESOLVES to a file that is actually there.
/// </para>
/// </summary>
public sealed class LogSiteHaltBannerTests
{
    private static readonly bool Ps = OperatingSystem.IsWindows();

    /// <summary>A sentinel the failing check prints, so a "reason" assertion proves real captured bytes.</summary>
    private const string FailSentinel = "HALT-BANNER-DETAIL-SENTINEL";

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 1. Plan preflight  —  halt.kind = plan-preflight-failed
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlanPreflightHalt_IndexLeadsWithBanner_NamesTheCheck_AndLinksItsCapturedOutput()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateFlatPlan(repo.RepoPath);
        WriteGate(planDir, "preflights", "01-environment-ready", passes: false);
        WriteTask(Path.Combine(planDir, "tasks"), "01-only", "only.txt");

        Assert.Equal(ExitCodes.TaskFailed, await RunCliAsync(planDir));

        JournalDocument journal = ReadJournal(planDir);
        Assert.Equal(RunHaltKind.PlanPreflightFailed, journal.Halt!.Kind);

        string logsRoot = LogsRoot(planDir, journal.RunId);
        string index = ReadSitePage(logsRoot, "index.html");

        AssertBanner(index, "plan-preflight-failed", "01-environment-ready", hrefBase: "preflights");
        AssertLinkedArtifactsExist(logsRoot, hrefBase: "preflights", checkName: "01-environment-ready");

        // The state the banner exists to explain: the DAG never ran, so the table below is all `pending`.
        Assert.Contains("<td class=\"status\" data-status=\"pending\">pending</td>", index);
        Assert.Contains("stopped BEFORE the task DAG", index);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 2. Wave ENTRY gate  —  halt.kind = wave-entry-gate-failed   (the #432 incident's own case)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WaveEntryGateHalt_BannersBothThePlanIndexAndThatWavesOwnIndex()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateWavedPlan(repo.RepoPath, entryPasses: false, exitPasses: true);

        Assert.Equal(ExitCodes.TaskFailed, await RunCliAsync(planDir));

        JournalDocument journal = ReadJournal(planDir);
        Assert.Equal(RunHaltKind.WaveEntryGateFailed, journal.Halt!.Kind);

        string logsRoot = LogsRoot(planDir, journal.RunId);

        // (a) The plan-wide index: the banner names the wave and links its page and its captured output,
        // whose paths are relative to the SITE ROOT the plan index sits in.
        string index = ReadSitePage(logsRoot, "index.html");
        AssertBanner(index, "wave-entry-gate-failed", "01-upstream-materialized",
            hrefBase: "wave-01-scaffold/preflights");
        Assert.Contains("&middot; wave <a href=\"wave-01-scaffold/index.html\">wave-01-scaffold</a>", index);
        Assert.Contains("stopped BEFORE the tasks of this wave", index);
        AssertLinkedArtifactsExist(logsRoot, "wave-01-scaffold/preflights", "01-upstream-materialized");

        // (b) The halted wave's OWN index (issue #436 item 3) carries the same banner, with hrefs relative
        // to the wave folder that page lives in — one directory shorter, and still resolving.
        string wave = ReadSitePage(logsRoot, Path.Combine("wave-01-scaffold", "index.html"));
        AssertBanner(wave, "wave-entry-gate-failed", "01-upstream-materialized", hrefBase: "preflights");
        Assert.True(
            File.Exists(Path.Combine(logsRoot, "wave-01-scaffold", "preflights", "01-upstream-materialized", "stdout.log")),
            "the wave page's stdout.log link must resolve relative to the wave folder it sits in");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 3. Wave EXIT gate  —  halt.kind = wave-exit-gate-failed
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WaveExitGateHalt_BannersBothPages_AndSaysTheWavesTasksHadAlreadyRun()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateWavedPlan(repo.RepoPath, entryPasses: true, exitPasses: false);

        Assert.Equal(ExitCodes.TaskFailed, await RunCliAsync(planDir));

        JournalDocument journal = ReadJournal(planDir);
        Assert.Equal(RunHaltKind.WaveExitGateFailed, journal.Halt!.Kind);

        string logsRoot = LogsRoot(planDir, journal.RunId);

        string index = ReadSitePage(logsRoot, "index.html");
        AssertBanner(index, "wave-exit-gate-failed", "01-wave-sound", hrefBase: "wave-01-scaffold/guardrails");
        AssertLinkedArtifactsExist(logsRoot, "wave-01-scaffold/guardrails", "01-wave-sound");

        // An EXIT gate fires AFTER the wave's tasks drained, so the banner must NOT claim nothing ran —
        // the honest distinction between the four kinds is the whole point of naming the kind.
        Assert.Contains("stopped AFTER the tasks of this wave drained", index);
        Assert.DoesNotContain("stopped BEFORE the task DAG", index);

        string wave = ReadSitePage(logsRoot, Path.Combine("wave-01-scaffold", "index.html"));
        AssertBanner(wave, "wave-exit-gate-failed", "01-wave-sound", hrefBase: "guardrails");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 4. Terminal plan gate  —  halt.kind = plan-guardrail-failed
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TerminalPlanGateHalt_BannersTheIndex_EvenThoughEveryTaskIsGreen()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateFlatPlan(repo.RepoPath);
        WriteTask(Path.Combine(planDir, "tasks"), "01-only", "only.txt");
        WriteGate(planDir, "guardrails", "01-whole-repo-build", passes: false);

        Assert.Equal(ExitCodes.TaskFailed, await RunCliAsync(planDir));

        JournalDocument journal = ReadJournal(planDir);
        Assert.Equal(RunHaltKind.PlanGuardrailFailed, journal.Halt!.Kind);

        string logsRoot = LogsRoot(planDir, journal.RunId);
        string index = ReadSitePage(logsRoot, "index.html");

        AssertBanner(index, "plan-guardrail-failed", "01-whole-repo-build", hrefBase: "guardrails");
        AssertLinkedArtifactsExist(logsRoot, "guardrails", "01-whole-repo-build");

        // The most misleading of the four: every task IS succeeded, so without the banner the page reads
        // as an unqualified green run that simply stopped.
        Assert.Contains("data-status=\"succeeded\"", index);
        Assert.Contains("stopped AFTER the task DAG drained green", index);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 5. No halt — the page must be byte-for-byte what it was before #436.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoHalt_PlanIndex_IsByteForBytePreBannerOutput()
    {
        // The golden below is the pre-#436 plan-index template transcribed verbatim (git HEAD of
        // LogSiteRenderer at the time of this change), parameterised only by the SHARED style constant —
        // so a legitimate CSS change stays a one-place edit while ANY change to the page skeleton, the
        // insertion points, or the emitted rows fails here. This is the tripwire for "a halt-less run's
        // page did not move".
        //
        // RE-BASELINED for issue #524 / design 29 §4.8: the tripwire fired on an INTENDED change — the
        // EXPORTED site's index now carries a Model column (`<th>Model</th>` after Description, and a
        // per-row cell that is the placeholder `—` for a task with no journaled attempt provenance, as
        // both fixtures here are). IndexHtml is the only renderer that takes a modelResolver, and
        // ExportSite is the only caller that supplies one: the during-run index (WriteIndex, called by
        // OnTheFlyLogSiteObserver) passes none, and the wave page (WriteWaveIndex) has no such parameter
        // at all. So the OTHER goldens stay untouched — NoHalt_WaveIndex_IsByteForBytePreBannerOutput
        // below and T11_WavePageWithNoBreakdown_… in JitBreakdownVisibilityTests. If one of THOSE ever
        // needs this edit, the column has leaked off the exported site and that is a bug, not a re-baseline.
        using var site = new TempSite();
        TaskNode ran = FakeTask("01-first", "First");
        TaskNode pending = FakeTask("02-second", "Second");
        site.WriteAttempt("01-first", 1, "action-stdout.log", "did it");

        LogSiteRenderer.ExportSite(site.LogsRoot, [ran, pending], JournalWith(TempSite.RunId, halt: null,
            ("01-first", Core.Journal.TaskStatus.Succeeded), ("02-second", Core.Journal.TaskStatus.Pending)));

        string expected = $"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Guardrails run {TempSite.RunId} — log site</title>
<style>
{LogSiteRenderer.SharedStyle}
</style>
</head>
<body>
<h1>Guardrails run — task logs</h1>
<p>Static export of this run. Settled tasks link to their inlined log page; not-yet-run tasks are plain text.</p>
<table>
<thead><tr><th>Task</th><th>Status</th><th>Description</th><th>Model</th></tr></thead>
<tbody>
<tr><td><a href="01-first/index.html">01-first</a></td><td class="status" data-status="succeeded">succeeded</td><td>First</td><td>—</td></tr><tr><td>02-second</td><td class="status" data-status="pending">pending</td><td>Second</td><td>—</td></tr>
</tbody>
</table>
</body>
</html>
""";

        AssertSamePage(expected, ReadSitePage(site.LogsRoot, "index.html"));
    }

    [Fact]
    public void NoHalt_WaveIndex_IsByteForBytePreBannerOutput()
    {
        // Same tripwire for the per-wave page, which #436 also threads a halt through.
        using var site = new TempSite();
        TaskNode a = WaveTask("wave-01-alpha", "01-a", "Alpha first");
        WaveNode wave = Wave("wave-01-alpha", 1, "alpha", a);
        site.WriteAttempt("wave-01-alpha/01-a", 1, "action-stdout.log", "did it");

        LogSiteRenderer.ExportSite(site.LogsRoot, [a], [wave],
            JournalWith(TempSite.RunId, halt: null, ("wave-01-alpha/01-a", Core.Journal.TaskStatus.Succeeded)));

        string expected = $"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>wave-01-alpha — Guardrails wave log ({TempSite.RunId})</title>
<style>
{LogSiteRenderer.SharedStyle}
</style>
</head>
<body>
<h1>wave-01-alpha — wave log</h1>
<div class="bar"><a href="../index.html">&larr; all waves</a> &middot; 1/1 complete</div>
<p>Static export of this wave. Settled tasks link to their inlined log page; not-yet-run tasks are plain text.</p>
<table>
<thead><tr><th>Task</th><th>Status</th><th>Description</th></tr></thead>
<tbody>
<tr><td><a href="01-a/index.html">01-a</a></td><td class="status" data-status="succeeded">succeeded</td><td>Alpha first</td></tr>
</tbody>
</table>
</body>
</html>
""";

        AssertSamePage(expected, ReadSitePage(site.LogsRoot, Path.Combine("wave-01-alpha", "index.html")));
    }

    [Fact]
    public async Task GreenRun_RendersNoBanner_AndNoBannerCss()
    {
        // The end-to-end companion to the two goldens: a real run that passes both plan-level gates must
        // not gain a single byte of #436 markup anywhere on the site.
        using var repo = new TempGitRepo();
        string planDir = CreateFlatPlan(repo.RepoPath);
        WriteGate(planDir, "preflights", "01-environment-ready", passes: true);
        WriteTask(Path.Combine(planDir, "tasks"), "01-only", "only.txt");
        WriteGate(planDir, "guardrails", "01-whole-repo-build", passes: true);

        Assert.Equal(ExitCodes.Success, await RunCliAsync(planDir));

        JournalDocument journal = ReadJournal(planDir);
        Assert.Null(journal.Halt);

        string index = ReadSitePage(LogsRoot(planDir, journal.RunId), "index.html");
        Assert.DoesNotContain("class=\"halt\"", index);
        Assert.DoesNotContain("section.halt", index); // the banner CSS is not emitted either
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 6. Scoping — a PLAN-scoped halt belongs to the plan index only.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PlanScopedHalt_OnAWavedPlan_BannersThePlanIndexOnly_NotEveryWavePage()
    {
        // A terminal-gate halt is not any wave's gate. Repeating it on each wave page would tell a reader
        // that wave-02's own gate stopped the run, which is the kind of confident wrong answer #436 is
        // trying to replace — so the renderer only banners a wave whose OWN dir the halt names.
        using var site = new TempSite();
        TaskNode a = WaveTask("wave-01-alpha", "01-a", "Alpha first");
        TaskNode b = WaveTask("wave-02-beta", "01-b", "Beta first");
        WaveNode w1 = Wave("wave-01-alpha", 1, "alpha", a);
        WaveNode w2 = Wave("wave-02-beta", 2, "beta", b);

        var halt = new RunHalt
        {
            Kind = RunHaltKind.PlanGuardrailFailed,
            HaltedAt = DateTimeOffset.UnixEpoch,
            Headline = "Terminal gate FAILED on the merged HEAD: 01-full-suite",
            FailedChecks = [new FailedGuardrail { Name = "01-full-suite", Reason = "3 tests failed" }],
            LogDir = $"logs/{TempSite.RunId}/guardrails"
        };

        LogSiteRenderer.ExportSite(site.LogsRoot, [a, b], [w1, w2], JournalWith(TempSite.RunId, halt,
            ("wave-01-alpha/01-a", Core.Journal.TaskStatus.Succeeded),
            ("wave-02-beta/01-b", Core.Journal.TaskStatus.Succeeded)));

        Assert.Contains("data-halt-kind=\"plan-guardrail-failed\"", ReadSitePage(site.LogsRoot, "index.html"));
        Assert.DoesNotContain("class=\"halt\"", ReadSitePage(site.LogsRoot, Path.Combine("wave-01-alpha", "index.html")));
        Assert.DoesNotContain("class=\"halt\"", ReadSitePage(site.LogsRoot, Path.Combine("wave-02-beta", "index.html")));
    }

    [Fact]
    public void HaltWithUncapturedOutput_StillBanners_WithNoDanglingLinks()
    {
        // Capture is best-effort by contract (SSOT §8), and `logDir` is absent when the run id was not
        // available. The banner must still explain the stop — and must not render an anchor to a file that
        // is not there, which would replace one dead end with another.
        using var site = new TempSite();
        TaskNode t = FakeTask("01-first", "First");

        var halt = new RunHalt
        {
            Kind = RunHaltKind.PlanPreflightFailed,
            HaltedAt = DateTimeOffset.UnixEpoch,
            Headline = "Plan preflight FAILED — halting before scheduling any task: 01-baseline-green",
            FailedChecks = [new FailedGuardrail { Name = "01-baseline-green", Reason = "3 tests already red" }],
            LogDir = null
        };

        LogSiteRenderer.ExportSite(site.LogsRoot, [t], JournalWith(TempSite.RunId, halt,
            ("01-first", Core.Journal.TaskStatus.Pending)));

        string index = ReadSitePage(site.LogsRoot, "index.html");
        Assert.Contains("data-halt-kind=\"plan-preflight-failed\"", index);
        Assert.Contains("01-baseline-green", index);
        Assert.Contains("3 tests already red", index);
        Assert.Contains("<code>(not captured)</code>", index);
        Assert.DoesNotContain("stdout.log", index); // no link is offered to output that was never written
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Assertions
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The banner contract: present, keyed by the SSOT §7 <c>halt.kind</c> token, rendered ABOVE the task
    /// table (a reader must not have to scroll past the pending rows to find out why they are pending),
    /// visually a block rather than a task row (its own CSS is emitted), naming the failing check with the
    /// captured reason, and linking the gate's <c>logDir</c> plus that check's stdout/result files.
    /// </summary>
    private static void AssertBanner(string page, string kindToken, string checkName, string hrefBase)
    {
        Assert.Contains($"<section class=\"halt\" data-halt-kind=\"{kindToken}\">", page);
        Assert.Contains("Run halted at a gate", page);
        Assert.Contains("section.halt {", page); // the banner's own CSS, emitted only alongside a banner

        int banner = page.IndexOf("<section class=\"halt\"", StringComparison.Ordinal);
        int table = page.IndexOf("<table>", StringComparison.Ordinal);
        Assert.InRange(banner, 0, table);

        Assert.Contains($"<span class=\"halt-check\">{checkName}</span>", page);
        Assert.Contains(FailSentinel, page); // the REASON, carried through from the check's own output

        Assert.Contains($"<a href=\"{hrefBase}\">", page);                          // the logDir itself
        Assert.Contains($"href=\"{hrefBase}/{checkName}/stdout.log\"", page);       // one click to the output
        Assert.Contains($"href=\"{hrefBase}/{checkName}/result.json\"", page);
    }

    /// <summary>Every artifact the banner links is really on disk — an anchor the reader can follow.</summary>
    private static void AssertLinkedArtifactsExist(string logsRoot, string hrefBase, string checkName)
    {
        string dir = Path.Combine(logsRoot, Path.Combine(hrefBase.Split('/')), checkName);
        foreach (string file in new[] { "stdout.log", "stderr.log", "result.json" })
        {
            Assert.True(File.Exists(Path.Combine(dir, file)), $"the banner links {file}, so it must exist at {dir}");
        }
    }

    /// <summary>
    /// Compare a rendered page against its golden. Line endings are normalised on BOTH sides because they
    /// are a property of the CHECKOUT, not of the renderer: the template and this golden are both C# raw
    /// string literals, so they are CRLF together on a Windows working tree and LF together on Linux/macOS.
    /// Every other character is compared exactly.
    /// </summary>
    private static void AssertSamePage(string expected, string actual) =>
        Assert.Equal(expected.ReplaceLineEndings("\n"), actual.ReplaceLineEndings("\n"));

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Renderer fixtures
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static JournalDocument JournalWith(
        string runId, RunHalt? halt, params (string Id, Core.Journal.TaskStatus Status)[] tasks) => new()
        {
            RunId = runId,
            PlanHash = "sha256:deadbeef",
            Tasks = tasks.ToDictionary(t => t.Id, t => new TaskJournalEntry { Status = t.Status }, StringComparer.Ordinal),
            Halt = halt
        };

    private static TaskNode FakeTask(string id, string description) => new()
    {
        Id = id,
        Directory = id,
        Description = description,
        Action = new ActionDefinition { Path = "action.ps1", Kind = ActionKind.Script },
        Guardrails = [new GuardrailDefinition { Name = "01-x", Path = "01-x.ps1", Kind = ActionKind.Script }],
    };

    private static TaskNode WaveTask(string waveDir, string folder, string description) => new()
    {
        Id = $"{waveDir}/{folder}",
        WaveDir = waveDir,
        Directory = folder,
        Description = description,
        Action = new ActionDefinition { Path = "action.ps1", Kind = ActionKind.Script },
        Guardrails = [new GuardrailDefinition { Name = "01-x", Path = "01-x.ps1", Kind = ActionKind.Script }],
    };

    private static WaveNode Wave(string dir, int number, string slug, params TaskNode[] tasks) => new()
    {
        Dir = dir,
        Number = number,
        Slug = slug,
        Directory = dir,
        Tasks = tasks,
    };

    private static string LogsRoot(string planDir, string runId) => Path.Combine(planDir, "logs", runId);

    private static string ReadSitePage(string logsRoot, string relativePath)
    {
        string path = Path.Combine(logsRoot, relativePath);
        Assert.True(File.Exists(path), $"expected a rendered page at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>A throwaway <c>logs/&lt;runId&gt;/</c> tree for the renderer-level tests.</summary>
    private sealed class TempSite : IDisposable
    {
        public const string RunId = "test-run";

        private string Dir { get; } = Path.Combine(Path.GetTempPath(), "gr-436-" + Guid.NewGuid().ToString("N"));

        public string LogsRoot => Path.Combine(Dir, "logs", RunId);

        public TempSite() => Directory.CreateDirectory(LogsRoot);

        public void WriteAttempt(string taskId, int attempt, string fileName, string content)
        {
            string attemptDir = Path.Combine(LogsRoot, Path.Combine(taskId.Split('/')), $"attempt-{attempt}");
            Directory.CreateDirectory(attemptDir);
            File.WriteAllText(Path.Combine(attemptDir, fileName), content);
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Plan fixtures (mirroring GateFailurePersistenceTests, whose four halts these four render)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static string CreateFlatPlan(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            { "version": 1, "guardrailMode": "failFast", "workspace": "..", "defaultRetries": 0, "maxParallelism": 2 }
            """);
        return planDir;
    }

    private static string CreateWavedPlan(string repoPath, bool entryPasses, bool exitPasses)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(planDir);
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            { "version": 1, "guardrailMode": "failFast", "workspace": "..", "defaultRetries": 0, "maxParallelism": 2 }
            """);

        string wave = Path.Combine(planDir, "wave-01-scaffold");
        WriteGate(wave, "preflights", "01-upstream-materialized", entryPasses);
        WriteTask(Path.Combine(wave, "tasks"), "01-config", "config.txt");
        WriteGate(wave, "guardrails", "01-wave-sound", exitPasses);

        return planDir;
    }

    private static void WriteGate(string root, string folder, string name, bool passes)
    {
        string dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        string catches = $"# catches: {name} — a gate whose halt is invisible on the log site (issue #436)";
        string code = passes ? "exit 0" : "exit 1";
        WriteScript(
            Path.Combine(dir, Script(name)),
            $"{catches}\nWrite-Output '{FailSentinel}'\n{code}",
            $"{catches}\necho '{FailSentinel}'\n{code}");
    }

    private static void WriteTask(string tasksDir, string id, string file)
    {
        string taskDir = Path.Combine(tasksDir, id);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "halt-banner fixture {{id}}", "writeScope": ["{{file}}"] }""");

        WriteScript(
            Path.Combine(taskDir, Script("action")),
            $"Set-Content -NoNewline -Path (Join-Path $env:GUARDRAILS_WORKSPACE '{file}') -Value 'x'\nexit 0",
            $"printf 'x' > \"$GUARDRAILS_WORKSPACE/{file}\"\nexit 0");

        WriteScript(
            Path.Combine(taskDir, "guardrails", Script("01-check")),
            $"# catches: {file} missing\nif (-not (Test-Path (Join-Path $env:GUARDRAILS_WORKSPACE '{file}'))) "
            + $"{{ Write-Output '{file} missing'; exit 1 }}\nexit 0",
            $"# catches: {file} missing\n[ -f \"$GUARDRAILS_WORKSPACE/{file}\" ] || {{ echo '{file} missing'; exit 1; }}\nexit 0");
    }

    private static string Script(string stem) => Ps ? stem + ".ps1" : stem + ".sh";

    private static void WriteScript(string path, string psBody, string bashBody)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Ps ? psBody + "\n" : "#!/usr/bin/env bash\n" + bashBody + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Harness
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<int> RunCliAsync(string planDir)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("halt-banner render test root");
        root.Add(RunCommand.Create(io));
        return await root
            .Parse(["run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success"])
            .InvokeAsync();
    }

    private static JournalDocument ReadJournal(string planDir) =>
        JournalReader.Read(RunJournal.PathFor(planDir));

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-436-repo-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            Directory.CreateDirectory(RepoPath);

            Git("init");
            Git("config", "user.email", "test@guardrails.local");
            Git("config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# halt-banner-render");
            Git("add", ".");
            Git("commit", "-m", "Initial commit");
        }

        private void Git(params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            using Process proc = Process.Start(psi)!;
            proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {string.Join(" ", args)} exited {proc.ExitCode}: {stderr.Trim()}");
            }
        }

        public void Dispose()
        {
            try { SafeDelete.DeleteDirectory(_root); }
            catch { /* best-effort teardown */ }
        }
    }
}
