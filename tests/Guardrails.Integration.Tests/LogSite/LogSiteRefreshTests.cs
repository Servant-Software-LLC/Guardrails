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
    [InlineData("gr-live-paused")]
    [InlineData("GR_LOG_MAX_FAILS")]
    [InlineData("visibilitychange")]
    [InlineData("setInterval")]
    public void SettledPage_CarriesNoTraceOfThePollSubsystem(string fragment)
    {
        Assert.DoesNotContain(fragment, Index(live: false), StringComparison.Ordinal);
        Assert.DoesNotContain(fragment, WaveIndex(live: false), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>This assertion used to say the opposite, and it was the defect (#628).</b> It required ONE failed
    /// fetch to reveal the notice and cancel the interval permanently. #543 was right that a stranded page
    /// polling forever is a defect and right to add a terminal condition; the branch it added was simply
    /// unconditional on the first error, so the cure fired on healthy runs.
    ///
    /// <para>
    /// Measured: a log-site page left open in a background tab showed <i>"Not live — this page cannot poll
    /// for updates (it was opened as a file, or the run's log server is gone). It is a snapshot."</i> while
    /// a manual refresh immediately rendered the CURRENT page — which proves the log server was alive and
    /// neither stated cause was true. A dropped poll has several innocent sources, all of them transient: a
    /// browser throttling or aborting <c>fetch</c> in a background tab, the machine sleeping and waking, the
    /// server momentarily busy, a non-2xx while the page is being rewritten mid-run.
    /// </para>
    ///
    /// <para>
    /// So the catch must NOT stop the timer. It counts, and the page keeps trying — which is the only way a
    /// page recovers on its own when the blip passes.
    /// </para>
    /// </summary>
    [Fact]
    public void AFailedPoll_CountsButDoesNotStopTheTimer()
    {
        string html = Index(live: true);

        // Scope to the POLL function. An unscoped IndexOf would find whichever catch block comes first on
        // the page, which is a finding about code the test was never aiming at.
        int pollStart = html.IndexOf("async function grPollLog", StringComparison.Ordinal);
        Assert.True(pollStart >= 0, "expected the poll function on a during-run page");

        int catchStart = html.IndexOf("} catch (e) {", pollStart, StringComparison.Ordinal);
        Assert.True(catchStart >= 0, "expected the poll's fetch to be guarded by a catch");
        int catchEnd = html.IndexOf("return;", catchStart, StringComparison.Ordinal);
        Assert.True(catchEnd > catchStart, "expected the catch block to bail out with a return");

        string catchBody = html[catchStart..catchEnd];
        Assert.Contains("grLogFails++;", catchBody, StringComparison.Ordinal);
        Assert.DoesNotContain("grStopLogPoll();", catchBody, StringComparison.Ordinal);
        Assert.DoesNotContain("grShowLogOffline();", catchBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// The threshold has to be greater than one, or the count is decoration and the first blip still
    /// declares the run dead. Asserted on the VALUE rather than on its presence, because a threshold of 1
    /// would satisfy every other assertion in this file.
    /// </summary>
    [Fact]
    public void TheFailureThreshold_IsMoreThanOne()
    {
        string html = Index(live: true);

        int at = html.IndexOf("const GR_LOG_MAX_FAILS = ", StringComparison.Ordinal);
        Assert.True(at >= 0, "expected a named consecutive-failure threshold");
        int end = html.IndexOf(';', at);
        string value = html[(at + "const GR_LOG_MAX_FAILS = ".Length)..end];

        Assert.True(int.TryParse(value, out int threshold), $"threshold '{value}' is not a number");
        Assert.True(threshold > 1, $"a threshold of {threshold} declares the run dead on the first blip");
    }

    /// <summary>
    /// <c>file://</c> is the case #543 was really aimed at, and it is PERMANENT and synchronously decidable
    /// — <c>window.location.protocol</c> is knowable before any poll is attempted. Deciding it up front is
    /// what lets the http case stop guessing: the two were conflated into one message that asserted a cause
    /// the code had never established.
    /// </summary>
    [Fact]
    public void AFileUrl_IsDecidedUpFront_AndNeverStartsTheTimer()
    {
        string html = Index(live: true);

        Assert.Contains(
            "if (window.location.protocol === 'file:') { grShowLogOffline(); } else { grStartLogPoll(); }",
            html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The notice must be REVERSIBLE. Nothing ever un-hid the old one, so even a recovered server left a
    /// false claim on screen for as long as the page stayed open — a page that had gone quiet for one
    /// throttled fetch and then resumed updating still told the operator it was a snapshot.
    /// </summary>
    [Fact]
    public void ThePausedNotice_ClearsItselfWhenAPollSucceeds()
    {
        string html = Index(live: true);

        Assert.Contains("grSetLogPaused(false);", html, StringComparison.Ordinal);
        Assert.Contains("notice.hidden = !shown;", html, StringComparison.Ordinal);

        // And it says only what is known — N attempts failed — never that the server is gone.
        Assert.Contains("Live updates paused", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The background tab is the most common trigger, so the page stops polling while hidden and polls
    /// IMMEDIATELY on return — removing the cause outright rather than merely tolerating it, and making the
    /// page current the moment somebody looks at it.
    /// </summary>
    [Fact]
    public void ThePagePausesWhileHidden_AndPollsOnReturn()
    {
        string html = Index(live: true);

        Assert.Contains("visibilitychange", html, StringComparison.Ordinal);
        int at = html.IndexOf("visibilitychange", StringComparison.Ordinal);
        string handler = html[at..Math.Min(html.Length, at + 400)];
        Assert.Contains("grStopLogPoll();", handler, StringComparison.Ordinal);
        Assert.Contains("grPollLog();", handler, StringComparison.Ordinal);
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
