using Guardrails.Cli.Ui;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.LogSite;

/// <summary>
/// Issue #543 — the log site used to carry a whole-document <c>&lt;meta http-equiv="refresh"
/// content="2"&gt;</c> on every during-run page. That mechanism had <b>no terminal condition of its own</b>:
/// it stopped only because the run reached completion and rewrote the file without it, so a run that was
/// killed, crashed or interrupted left its log pages reloading every two seconds forever, on every machine
/// that ever opened them. It also discarded scroll position and could swallow a click landing mid-tick.
/// <para>
/// These tests pin the replacement — an in-place poll that swaps in the fetched <c>&lt;body&gt;</c> — and,
/// more importantly, pin the two properties that make it <i>stop</i>. They mirror the shapes
/// <c>Graph/DiagramRefreshTests</c> uses for the diagram half (#523), because the two surfaces now share a
/// design and should fail together if either drifts.
/// </para>
/// <para>
/// Every assertion goes through a real <c>LogSiteRenderer</c> write, never a hand-built string. JS is never
/// executed here — these are assertions about what is emitted, which is what determines whether a stranded
/// artifact goes quiet.
/// </para>
/// </summary>
public sealed class LogSiteRefreshTests
{
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

    private sealed class TempDir : IDisposable
    {
        public const string RunId = "test-run";

        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "gr-543-" + Guid.NewGuid().ToString("N"));

        public string LogsRoot => Path.Combine(Dir, "logs", RunId);

        public TempDir() => Directory.CreateDirectory(LogsRoot);

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Renders a plan index and returns its HTML. <paramref name="live"/> selects during-run vs settled.</summary>
    private static string Index(bool live)
    {
        using var temp = new TempDir();
        string path = LogSiteRenderer.WriteIndex(
            temp.LogsRoot,
            TempDir.RunId,
            [FakeTask("01-alpha", "the first task")],
            statusResolver: _ => live ? "running" : "succeeded",
            linkResolver: _ => live ? LogSiteRenderer.IndexLink.Plain : LogSiteRenderer.IndexLink.Static,
            includeRefresh: live);
        return File.ReadAllText(path);
    }

    /// <summary>Renders a per-wave index and returns its HTML.</summary>
    private static string WaveIndex(bool live)
    {
        using var temp = new TempDir();
        TaskNode a = WaveTask("wave-01-alpha", "01-a", "Alpha first");
        string path = LogSiteRenderer.WriteWaveIndex(
            temp.LogsRoot,
            TempDir.RunId,
            Wave("wave-01-alpha", 1, "alpha", a),
            statusResolver: _ => live ? "running" : "succeeded",
            linkResolver: _ => live ? LogSiteRenderer.IndexLink.Plain : LogSiteRenderer.IndexLink.Static,
            includeRefresh: live);
        return File.ReadAllText(path);
    }

    [Fact]
    public void DuringRunPage_HasNoWholeDocumentReload_SoScrollAndClicksSurvive()
    {
        // The mechanism itself, not any particular content="..." value — a page that reloaded every 10s
        // instead of every 2s would still discard scroll and still never stop on its own.
        Assert.DoesNotContain("http-equiv=\"refresh\"", Index(live: true), StringComparison.Ordinal);
    }

    [Fact]
    public void LivePoll_IsPresentDuringTheRun_AndAbsentOnTheFinalSettledPage()
    {
        Assert.Contains("GR_LOG_POLL_MS", Index(live: true), StringComparison.Ordinal);
        Assert.DoesNotContain("GR_LOG_POLL_MS", Index(live: false), StringComparison.Ordinal);
    }

    /// <summary>
    /// The terminal signal is the ABSENCE of the poll block on the fetched page, so the settled page must
    /// carry no fragment of the poll subsystem — not the constant, not its functions, not the notice. If any
    /// one leaked, a browser left open on a finished run would keep polling forever: the exact defect this
    /// issue is about, merely relocated.
    /// </summary>
    [Theory]
    [InlineData("GR_LOG_POLL_MS")]
    [InlineData("grPollLog")]
    [InlineData("grStopLogPoll")]
    [InlineData("grShowLogOffline")]
    [InlineData("gr-live-offline")]
    [InlineData("setInterval")]
    public void SettledPage_CarriesNoTraceOfThePollSubsystem(string fragment)
    {
        Assert.DoesNotContain(fragment, Index(live: false), StringComparison.Ordinal);
        Assert.DoesNotContain(fragment, WaveIndex(live: false), StringComparison.Ordinal);
    }

    /// <summary>
    /// The stopping condition the whole issue turns on. A killed run never reaches the final settle, so the
    /// page's own poll is the only thing that can quiet it: one failed fetch must both stop the timer and
    /// say so. Asserting both calls sit INSIDE the catch is what distinguishes a poll that gives up from one
    /// that retries forever against a server that is never coming back.
    /// </summary>
    [Fact]
    public void AFailedPoll_StopsTheTimerAndRevealsTheOfflineNotice()
    {
        string html = Index(live: true);

        int catchStart = html.IndexOf("} catch (e) {", StringComparison.Ordinal);
        Assert.True(catchStart >= 0, "expected the poll's fetch to be guarded by a catch");
        int catchEnd = html.IndexOf("return;", catchStart, StringComparison.Ordinal);
        Assert.True(catchEnd > catchStart, "expected the catch block to bail out with a return");

        string catchBody = html[catchStart..catchEnd];
        Assert.Contains("grShowLogOffline();", catchBody, StringComparison.Ordinal);
        Assert.Contains("grStopLogPoll();", catchBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// The second stopping condition: a settled page fetched by a still-running poll. The needle the poll
    /// searches for and the token the settled page omits are the SAME string, and nothing but this test
    /// binds them — rename the constant on one side only and the poll silently never stops again. That is
    /// the failure mode this whole change exists to remove, so it gets its own assertion.
    /// </summary>
    [Fact]
    public void TheTerminalNeedle_IsExactlyTheTokenTheSettledPageDrops()
    {
        Assert.Contains("if (!text.includes('GR_LOG_POLL_MS')) { grStopLogPoll(); }",
            Index(live: true), StringComparison.Ordinal);

        // ...and the settled page really does drop it, so the needle actually fires. Without this half the
        // assertion above would pass against a needle that never matches anything.
        Assert.DoesNotContain("GR_LOG_POLL_MS", Index(live: false), StringComparison.Ordinal);
    }

    [Fact]
    public void OfflineNotice_IsPresentAndHidden_SoAPageOnlySpeaksUpOnceItIsStale()
    {
        string html = Index(live: true);
        int idAttr = html.IndexOf("id=\"gr-live-offline\"", StringComparison.Ordinal);
        Assert.True(idAttr >= 0, "expected an element carrying id=\"gr-live-offline\" on the during-run page");

        // It must ship hidden: a notice visible on a healthy live page is noise, and noise is how a signal
        // like this stops being read.
        int tagStart = html.LastIndexOf('<', idAttr);
        int tagEnd = html.IndexOf('>', idAttr);
        Assert.True(tagStart >= 0 && tagEnd > tagStart, "expected a well-formed element for the notice");
        Assert.Contains("hidden", html[tagStart..tagEnd], StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #552 — the notice must name a remedy the reader can actually carry out. It used to say
    /// "use the live server URL printed by <c>guardrails run</c>", which is precisely backwards: the
    /// reader is looking at this notice BECAUSE no server is reachable, and until #552 a headless or
    /// redirected run never printed such a URL at all. So the banner sent the operator to look for a
    /// line that, in the very case that produced the banner, had never existed. `guardrails logs` is
    /// the verb that produces one on demand, against a run already in flight.
    /// </summary>
    [Fact]
    public void OfflineNotice_NamesGuardrailsLogs_NotAUrlTheRunMayNeverHavePrinted()
    {
        string html = Index(live: true);

        Assert.Contains("guardrails logs", html, StringComparison.Ordinal);
        Assert.DoesNotContain("URL printed by", html, StringComparison.Ordinal);
    }

    [Fact]
    public void PollInterval_IsWellAboveTheTwoSecondReloadItReplaced()
    {
        string html = Index(live: true);
        const string marker = "const GR_LOG_POLL_MS = ";
        int i = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(i >= 0, "expected the poll interval constant on the during-run page");

        int valueStart = i + marker.Length;
        int valueEnd = html.IndexOf(';', valueStart);
        Assert.True(valueEnd > valueStart, "expected the interval constant to be terminated");
        Assert.True(int.TryParse(html[valueStart..valueEnd], out int pollMs),
            $"expected a numeric interval, got '{html[valueStart..valueEnd]}'");

        Assert.True(pollMs > 2000, $"GR_LOG_POLL_MS was {pollMs}ms; the reload it replaced was already 2000ms");
    }

    /// <summary>
    /// The wave page had the identical defect and needed the identical treatment — it renders through a
    /// separate method, which is exactly the kind of sibling surface a fix misses.
    /// </summary>
    [Fact]
    public void TheWavePage_GetsTheSamePoll_AndTheSameSettledSilence()
    {
        string duringRun = WaveIndex(live: true);

        Assert.DoesNotContain("http-equiv=\"refresh\"", duringRun, StringComparison.Ordinal);
        Assert.Contains("GR_LOG_POLL_MS", duringRun, StringComparison.Ordinal);
        Assert.DoesNotContain("GR_LOG_POLL_MS", WaveIndex(live: false), StringComparison.Ordinal);
    }
}
