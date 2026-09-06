using System.Globalization;
using System.Text.RegularExpressions;
using Guardrails.Core.Graph;

namespace Guardrails.Core.Tests.Graph;

/// <summary>
/// RED tests pinning issue #523: the live diagram page currently reloads the WHOLE document every
/// three seconds via <c>&lt;meta http-equiv="refresh"&gt;</c>, destroying the operator's pan, zoom
/// and scroll on every tick and risking a click landing mid-tick being swallowed. These tests
/// encode the target behaviour — an in-place status poll instead of a whole-document reload —
/// against the real <see cref="HtmlDiagramRenderer.Render"/>; the next task in this plan implements
/// the change that makes them pass. Every assertion goes through an actual <c>Render</c> call.
/// </summary>
[Trait("Category", "BacklogSlate")]
public sealed class DiagramRefreshTests
{
    // Fixture shape mirrored from HtmlDiagramRendererTests (those constants are private there, so
    // re-declared here rather than shared across the two test classes).
    private const string Hash = "abc123def456";
    private const string Source = "flowchart TD\n  task_a[\"a\"]:::task\n  classDef task fill:#cfe8ff;";

    private static readonly IReadOnlyDictionary<string, string> OneTarget =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["task_01_a"] = "tasks/01-a/" };

    private static readonly IReadOnlyDictionary<string, string> SomeStatus =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["task_01_a"] = "running" };

    [Fact]
    public void DuringRunPage_HasNoMetaRefresh_SoPanZoomAndScrollSurvive()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget, SomeStatus, duringRun: true);

        // The property is "no whole-document reload at all" — not merely a slower interval — so
        // this checks for the http-equiv mechanism itself, not any particular content="..." value.
        Assert.DoesNotContain("http-equiv", html, StringComparison.Ordinal);
    }

    [Fact]
    public void LivePoll_IsPresentDuringTheRun_AndAbsentOnTheFinalSettledPage()
    {
        string duringRunHtml = HtmlDiagramRenderer.Render(Source, Hash, OneTarget, SomeStatus, duringRun: true);
        string settledHtml = HtmlDiagramRenderer.Render(Source, Hash, OneTarget, SomeStatus, duringRun: false);

        // Both halves in one test: the contrast IS the property. Asserting only the absence half
        // would pass today against a page that has no poll mechanism at all.
        Assert.Contains("GR_LIVE_POLL_MS", duringRunHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("GR_LIVE_POLL_MS", settledHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void LivePollInterval_IsAtLeastFiveSeconds_ForADagThatChangesAtTaskBoundaries()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget, SomeStatus, duringRun: true);

        Match match = Regex.Match(html, @"GR_LIVE_POLL_MS\s*=\s*(\d+)");
        Assert.True(
            match.Success,
            "expected a 'GR_LIVE_POLL_MS = <number>' constant assignment on the during-run page; found none");

        int pollMs = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.True(pollMs >= 5000, $"GR_LIVE_POLL_MS was {pollMs}ms; expected at least 5000ms");
    }

    [Fact]
    public void FileViewFallback_IsPresentAndHidden_SoAnUnpollablePageSaysItIsNotLive()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget, SomeStatus, duringRun: true);

        // Anchor on the literal HTML attribute syntax (not a bare substring search) so this can
        // never accidentally match a "#gr-live-offline { ... }" CSS selector instead of the element.
        int idAttrIndex = html.IndexOf("id=\"gr-live-offline\"", StringComparison.Ordinal);
        Assert.True(idAttrIndex >= 0, "expected an element carrying id=\"gr-live-offline\" on the during-run page");

        int tagStart = html.LastIndexOf('<', idAttrIndex);
        int tagEnd = html.IndexOf('>', idAttrIndex);
        Assert.True(
            tagStart >= 0 && tagEnd > tagStart,
            "expected id=\"gr-live-offline\" to sit inside a well-formed opening tag");
        string openingTag = html[tagStart..(tagEnd + 1)];

        // Presence alone would be satisfied by a notice that is visible on every page, so also
        // require evidence it starts hidden — either directly on the element (a `hidden` attribute
        // or an inline display:none) or via a stylesheet rule keyed to the same id.
        bool hiddenOnElement = Regex.IsMatch(openingTag, @"(^|[\s""])hidden([\s=""/>]|$)")
            || Regex.Replace(openingTag, @"\s+", "").Contains("display:none", StringComparison.Ordinal);
        bool hiddenViaStylesheetRule = Regex.IsMatch(
            Regex.Replace(html, @"\s+", ""),
            @"#gr-live-offline\{[^}]*display:none");

        Assert.True(
            hiddenOnElement || hiddenViaStylesheetRule,
            "expected the gr-live-offline notice to start hidden (a 'hidden' attribute, an inline " +
            "display:none style, or a '#gr-live-offline { display: none }' rule), so it appears only " +
            "when a poll fails and never on a page that is polling fine");
    }

    /// <summary>
    /// Issue #628, the diagram half. The status poll gave up permanently on ONE failed fetch — revealing
    /// the offline notice and cancelling the interval — so a page left in a background tab came back
    /// stale, badged as not-live, while the server was fine and a refresh proved it.
    ///
    /// <para>
    /// The catch now COUNTS and keeps trying. That is the only way a page recovers on its own when the
    /// blip passes, and a dropped poll has several innocent and transient sources: a browser throttling
    /// or aborting <c>fetch</c> in a background tab, the machine sleeping and waking, the server
    /// momentarily busy, a non-2xx while the page is being rewritten mid-run.
    /// </para>
    /// </summary>
    [Fact]
    public void AFailedStatusPoll_CountsButDoesNotStopTheTimer()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget, SomeStatus, duringRun: true);

        // Scope the search to the STATUS POLL. The page carries other catch blocks (the pan-zoom code has
        // one), and an unscoped IndexOf finds the first of them — which is how a mis-aimed anchor reports
        // a finding about a function it was never looking at.
        int pollStart = html.IndexOf("async function pollLiveStatus", StringComparison.Ordinal);
        Assert.True(pollStart >= 0, "expected the status poll function on a during-run page");

        int catchStart = html.IndexOf("} catch (e) {", pollStart, StringComparison.Ordinal);
        Assert.True(catchStart >= 0, "expected the status poll's fetch to be guarded by a catch");
        int catchEnd = html.IndexOf("return;", catchStart, StringComparison.Ordinal);
        Assert.True(catchEnd > catchStart, "expected the catch block to bail out with a return");

        string catchBody = html[catchStart..catchEnd];
        Assert.Contains("liveFails++;", catchBody, StringComparison.Ordinal);
        Assert.DoesNotContain("stopLivePoll();", catchBody, StringComparison.Ordinal);
        Assert.DoesNotContain("showLiveOfflineNotice();", catchBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// A threshold of one is a counter that changes nothing, and it would satisfy every other assertion
    /// here — so assert the VALUE.
    /// </summary>
    [Fact]
    public void TheStatusPollFailureThreshold_IsMoreThanOne()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget, SomeStatus, duringRun: true);

        Match match = Regex.Match(html, @"const GR_LIVE_MAX_FAILS = (\d+);");
        Assert.True(match.Success, "expected a named consecutive-failure threshold");

        int threshold = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.True(threshold > 1, $"a threshold of {threshold} declares the run dead on the first blip");
    }

    /// <summary>
    /// <c>file://</c> is permanent and synchronously decidable, so it is settled before any fetch — which
    /// is what lets the http path stop guessing. Conflating the two produced a message asserting a cause
    /// the code had never established.
    /// </summary>
    [Fact]
    public void AFileUrl_ShowsTheOfflineNoticeUpFront_AndNeverStartsTheTimer()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget, SomeStatus, duringRun: true);

        Assert.Contains(
            "if (window.location.protocol === 'file:') { showLiveOfflineNotice(); return; }",
            html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The transient notice is reversible and the page pauses while hidden — the background tab being the
    /// most common trigger, this removes the cause rather than tolerating it, and polls on return so the
    /// diagram is current the moment somebody looks at it.
    /// </summary>
    [Fact]
    public void ThePausedNotice_IsReversible_AndThePagePausesWhileHidden()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget, SomeStatus, duringRun: true);

        Assert.Contains("id=\"gr-live-paused\"", html, StringComparison.Ordinal);
        Assert.Contains("setLivePausedNotice(false);", html, StringComparison.Ordinal);
        Assert.Contains("notice.hidden = !shown;", html, StringComparison.Ordinal);
        Assert.Contains("visibilitychange", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #552 — the notice must name the command that produces a live copy, not merely assert one
    /// exists somewhere. "The diagram served by the log-site server IS live; open that copy" told the
    /// reader what they were missing and nothing about how to get it, which for a headless or
    /// backgrounded run was a dead end: no server had been started and no URL printed.
    /// <c>guardrails logs &lt;plan-folder&gt;</c> serves the persisted logs — and this diagram —
    /// against a run already in flight, so it is a remedy the reader can act on from the notice alone.
    /// </summary>
    [Fact]
    public void FileViewFallback_NamesTheCommandThatProducesALiveCopy()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget, SomeStatus, duringRun: true);

        int idAttrIndex = html.IndexOf("id=\"gr-live-offline\"", StringComparison.Ordinal);
        Assert.True(idAttrIndex >= 0, "expected the offline notice on the during-run page");

        int noticeEnd = html.IndexOf("</div>", idAttrIndex, StringComparison.Ordinal);
        Assert.True(noticeEnd > idAttrIndex, "expected the offline notice to be a closed element");
        string notice = html[idAttrIndex..noticeEnd];

        Assert.Contains("guardrails logs", notice, StringComparison.Ordinal);
    }

    // Deliberately EXCLUDED from the #523 red census: this pin PASSES against the current tree — it
    // guards provenance/source embedding, which the meta-refresh defect does not touch. It exists so
    // the next task, which adds page chrome around the live-update behaviour above, cannot quietly
    // move the provenance line or re-encode the embedded source; if it did, `graph --check` would
    // report every plan in the repo stale, and nothing else in this plan would notice.
    [Fact]
    public void SourceSha256AndEmbeddedSource_AreUnchangedByTheLiveUpdateChanges()
    {
        string html = HtmlDiagramRenderer.Render(Source, Hash, OneTarget, SomeStatus, duringRun: true);

        string firstLine = html.Split('\n')[0];
        Assert.Equal($"<!-- guardrails:graph v1 source-sha256={Hash} -->", firstLine);

        int scriptStart = html.IndexOf("id=\"graph-source\"", StringComparison.Ordinal);
        Assert.True(scriptStart >= 0, "expected the graph-source script element to be present");
        int contentStart = html.IndexOf('>', scriptStart) + 1;
        int contentEnd = html.IndexOf("</script>", contentStart, StringComparison.Ordinal);
        string embeddedSource = html[contentStart..contentEnd];

        Assert.Equal(Source, embeddedSource);
    }
}
