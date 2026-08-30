using System.Globalization;
using System.Net;
using System.Text;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Cli.Ui;

/// <summary>
/// The SINGLE renderer for the log viewer's HTML — the LIVE <see cref="LogServer"/> (dynamic pages
/// polled client-side), the DURING-RUN static site (rewritten on the fly as tasks settle, issue
/// #141 item 2), and the DURABLE post-hoc export (<c>guardrails logs --export</c>, SSOT §12.3) all
/// consume it, so there is no forked look-alike (#103 Request 2). They share the page SHELL (CSS +
/// layout + the same status colours); they differ ONLY in the index's per-task link target and
/// whether the index auto-refreshes:
/// <list type="bullet">
///   <item>DURING-RUN index — an IN-PLACE live status poll (issue #543; see
///     <see cref="LivePollScript"/>) that fetches this page's own url and swaps in the fetched
///     <c>&lt;body&gt;</c>, so scroll survives and the page stops on its own when the run settles OR when
///     a poll fails. It replaced a <c>&lt;meta http-equiv="refresh" content="2"&gt;</c> whole-document
///     reload, which had no terminal condition of its own and so left a killed run's pages reloading
///     forever. A RUNNING task links to the LIVE server URL (a click tails it), a settled/with-attempts
///     task links to its static <c>&lt;taskId&gt;/index.html</c>, a pending/no-attempts task is plain
///     text.</item>
///   <item>FINAL / <c>--export</c> index — NO poll, ALL links static (durable, non-flickering). The
///     absence of the poll block is itself the terminal signal a polling page reads, which is why nothing
///     was added to this page and its bytes are unchanged from before #543.</item>
/// </list>
///
/// <para>The static site is written next to the artifacts it renders, under the <c>logs/&lt;runId&gt;/</c>
/// audit tree (NEVER <c>state/</c>, which is mutable run state): one <c>index.html</c> per task plus a
/// site <c>index.html</c>. It is non-authored audit (excluded from <c>guardrails.baseline</c>, like
/// <c>diagram.html</c>) and is cleared with the rest of <c>logs/</c> by <c>--fresh</c>.</para>
/// </summary>
public static class LogSiteRenderer
{
    // The shared page CSS (dark theme + the status-word colours). The live LogServer templates and the
    // static export both embed THIS constant, so a visual change lands in one place — the renderer-drift
    // trap the project already solved for diagram.md / diagram.html.
    public const string SharedStyle = """
  body { font-family: system-ui, sans-serif; margin: 1.5rem; background: #0b0f14; color: #d6deeb; }
  h1 { font-size: 1.2rem; }
  h2 { font-size: 1rem; color: #8aa0b3; margin-top: 1.6rem; }
  p { color: #8aa0b3; }
  a { color: #7fdbff; text-decoration: none; }
  a:hover { text-decoration: underline; }
  .bar { color: #8aa0b3; margin-bottom: .8rem; }
  table { border-collapse: collapse; margin-top: 1rem; width: 100%; }
  td, th { padding: .45rem .8rem; border-bottom: 1px solid #1c2733; text-align: left; }
  th { color: #8aa0b3; font-weight: 600; }
  select { background: #121a24; color: #d6deeb; border: 1px solid #243343; border-radius: 4px; padding: .2rem .4rem; }
  td.status, span.status { font-weight: 600; }
  .status[data-status="succeeded"], .status[data-status="skipped"] { color: #3fb950; }
  .status[data-status="needs-human"], .status[data-status="failed"] { color: #f85149; }
  .status[data-status="running"] { color: #d29922; }
  .status[data-status="pending"], .status[data-status="blocked"], .status[data-status="unknown"] { color: #8aa0b3; }
  .claim { font-weight: 400; color: #8aa0b3; }
  .empty, option.empty { color: #6b7a8d; }
  pre { background: #06090d; border: 1px solid #1c2733; border-radius: 6px; padding: 1rem;
        white-space: pre-wrap; word-break: break-word; font-size: .82rem; line-height: 1.35;
        max-height: 70vh; overflow: auto; }
""";

    /// <summary>
    /// The in-place live-poll interval (issue #543). Deliberately shorter than the diagram's 15s
    /// (<c>HtmlDiagramRenderer.LivePollMs</c>): re-badging a big Mermaid SVG is heavy, whereas swapping a
    /// small table body is not, and this index is the surface an operator actually watches a run through —
    /// the "click a task the moment it settles" flow wants to feel current. Still well above the 2s
    /// whole-document reload it replaces, because task status only changes at task boundaries.
    /// </summary>
    private const int LivePollMs = 5000;

    /// <summary>
    /// The live-poll offline notice's CSS (issue #543). Appended to the page's one <c>&lt;style&gt;</c>
    /// element ONLY on a during-run page, so the FINAL settled page keeps its exact pre-#543 bytes.
    /// </summary>
    private const string LivePollStyle = """

  #gr-live-offline { position: fixed; bottom: 8px; right: 8px; z-index: 10; background: #3a2410;
                     border: 1px solid #b8860b; border-radius: 6px; padding: .5rem .7rem;
                     color: #f0d9a8; font-size: .78rem; max-width: 32rem; }
""";

    /// <summary>
    /// The during-run page's in-place status poll (issue #543), replacing the whole-document
    /// <c>&lt;meta http-equiv="refresh" content="2"&gt;</c> this page used to carry.
    /// <para>
    /// <b>Why the meta refresh had to go.</b> It reloaded the entire document every 2 seconds, which threw
    /// away scroll position, could swallow a click that landed mid-tick, and — the defect that actually
    /// prompted this — <b>had no terminal condition of its own</b>. It stopped only because the run reached
    /// completion and rewrote the file without it, so a run that was killed, crashed, or interrupted left
    /// its log pages reloading every 2 seconds forever, on every machine that ever opened them.
    /// </para>
    /// <para>
    /// <b>What replaces it.</b> The page fetches its OWN url, and swaps in the fetched document's
    /// <c>&lt;body&gt;</c> — the table, the note, the waves nav and any halt banner all update in place,
    /// with no navigation, so scroll survives. It stops on BOTH terminal conditions:
    /// </para>
    /// <list type="number">
    ///   <item>the fetched page no longer carries <c>GR_LOG_POLL_MS</c> — i.e. the run settled and the
    ///     final static page was written. This is why the terminal signal needs nothing added to the FINAL
    ///     page: its identity IS the absence of this block, so that page stays byte-for-byte what it was
    ///     before #543.</item>
    ///   <item>the poll fails outright — most commonly a plain <c>file://</c> view, where <c>fetch</c> of
    ///     the page's own url is blocked, and equally a run whose server is gone because it was killed.
    ///     That case reveals the <c>#gr-live-offline</c> notice instead of leaving the page looking live
    ///     forever. <b>This is the one that closes the defect</b>: a stranded artifact now goes quiet and
    ///     says so, rather than flashing indefinitely.</item>
    /// </list>
    /// <para>
    /// The trade-off, stated plainly: a during-run page opened over <c>file://</c> no longer updates
    /// itself, because <c>fetch</c> cannot read a <c>file://</c> url. It shows the offline notice and
    /// points at the live server, which is the surface that can actually stream. An honest static snapshot
    /// is worth more than a page that reloads forever and cannot say whether it is current.
    /// </para>
    /// </summary>
    private static string LivePollScript() => $$"""
<div id="gr-live-offline" hidden>Not live &mdash; this page cannot poll for updates (it was opened as a
file, or the run's log server is gone). It is a snapshot. Use the live server URL printed by
<code>guardrails run</code> to watch a run in progress.</div>
<script>
const GR_LOG_POLL_MS = {{LivePollMs}};
let grLogPollTimer = null;
function grStopLogPoll() {
  if (grLogPollTimer !== null) { clearInterval(grLogPollTimer); grLogPollTimer = null; }
}
function grShowLogOffline() {
  const notice = document.getElementById('gr-live-offline');
  if (notice) notice.hidden = false;
}
// Fetch this page's OWN url and swap in the fetched <body>. No navigation happens, so scroll position
// (and any click already in flight) survives — the whole point of not using <meta refresh>.
async function grPollLog() {
  let text;
  try {
    const res = await fetch(window.location.href, { cache: 'no-store' });
    if (!res.ok) throw new Error('HTTP ' + res.status);
    text = await res.text();
  } catch (e) {
    grShowLogOffline();
    grStopLogPoll();
    return;
  }
  const doc = new DOMParser().parseFromString(text, 'text/html');
  if (doc.body) { document.body.innerHTML = doc.body.innerHTML; }
  // The terminal signal: the settled page is rendered WITHOUT this poll block, so a fetched document
  // that no longer mentions GR_LOG_POLL_MS is the run having finished and rewritten the page.
  if (!text.includes('GR_LOG_POLL_MS')) { grStopLogPoll(); }
}
grLogPollTimer = setInterval(grPollLog, GR_LOG_POLL_MS);
</script>
""";

    /// <summary>
    /// The GATE-HALT banner's CSS (issue #436). Deliberately NOT part of <see cref="SharedStyle"/>: it is
    /// appended to the page's one <c>&lt;style&gt;</c> element ONLY when that page actually renders a
    /// banner, so a run that did NOT halt at a gate renders byte-for-byte the same page as before.
    /// <para>
    /// The look is intentionally unlike a task failure. A failed TASK is one red word in a table cell; a
    /// gate halt is a full-width, red-bordered block above the table, because it means the DAG itself never
    /// got to run (or never got to finish) — the state that otherwise reads as "nothing happened".
    /// </para>
    /// </summary>
    private const string HaltStyle = """

  section.halt { border: 1px solid #f85149; border-left: 8px solid #f85149; background: #1d1012;
                 border-radius: 6px; padding: .9rem 1.1rem; margin: 1rem 0 1.5rem; }
  section.halt h2.halt-title { color: #ff7b72; font-size: 1.05rem; margin: 0 0 .45rem;
                               text-transform: uppercase; letter-spacing: .05em; }
  section.halt h3.halt-sub { color: #f0b7b1; font-size: .82rem; margin: .9rem 0 .25rem;
                             text-transform: uppercase; letter-spacing: .04em; }
  section.halt p { margin: .25rem 0; color: #e8d7d5; }
  section.halt p.halt-headline { color: #ffb4ab; font-weight: 600; }
  section.halt code { color: #f0b7b1; }
  .halt-meta { color: #a98b88; font-size: .85rem; margin-top: .55rem; }
  ul.halt-checks { margin: .25rem 0 0 1.1rem; padding: 0; }
  ul.halt-checks li { margin: .3rem 0; color: #e8d7d5; }
  .halt-check { font-weight: 600; color: #ff7b72; }
  .halt-files { color: #8aa0b3; }
  .halt-logdir { margin-top: .7rem; font-size: .9rem; color: #8aa0b3; }
""";

    /// <summary>
    /// The WAVE-PHASE panel's CSS (issue #469). Appended to the page's one <c>&lt;style&gt;</c> element ONLY
    /// when that page renders a panel — the same discipline #436 used for <see cref="HaltStyle"/> — so every
    /// page without a breakdown keeps its exact current bytes.
    /// <para>The palette extends the shipped dark theme rather than inventing a scheme: running is the amber
    /// the <c>running</c> status word already uses, a settled-good panel is the shipped green, and every
    /// failure state reuses the halt red. State is carried by <c>data-state</c> so no colour is the only
    /// carrier of meaning.</para>
    /// </summary>
    private const string PhaseStyle = """

  section.phase { border: 1px solid #8aa0b3; border-left: 8px solid #8aa0b3; background: #121a24;
                  border-radius: 6px; padding: .9rem 1.1rem; margin: 1rem 0 1.5rem; }
  section.phase h2.phase-title { color: #d6deeb; font-size: 1.05rem; margin: 0 0 .45rem;
                                 text-transform: uppercase; letter-spacing: .05em; }
  section.phase h3.phase-sub { color: #8aa0b3; font-size: .82rem; margin: .9rem 0 .25rem;
                               text-transform: uppercase; letter-spacing: .04em; }
  section.phase p { margin: .25rem 0; color: #d6deeb; }
  section.phase p.phase-headline { font-weight: 600; }
  section.phase p.phase-note { color: #8aa0b3; font-size: .9rem; }
  section.phase pre.phase-detail { margin: .5rem 0 0; font-size: .8rem; max-height: 24rem; }
  .phase-evidence { margin-top: .7rem; font-size: .9rem; color: #8aa0b3; }
  section.phase[data-state="running"] { border-color: #d29922; border-left-color: #d29922; background: #1d1a10; }
  section.phase[data-state="running"] h2.phase-title { color: #e3b341; }
  section.phase[data-state="authored"] { border-color: #3fb950; border-left-color: #3fb950; background: #101d12; }
  section.phase[data-state="authored"] h2.phase-title { color: #56d364; }
  section.phase[data-state="cut-off"], section.phase[data-state="incomplete"] {
                  border-color: #f85149; border-left-color: #f85149; background: #1d1012; }
  section.phase[data-state="cut-off"] h2.phase-title,
  section.phase[data-state="incomplete"] h2.phase-title { color: #ff7b72; }
""";

    // The breakdown evidence files the phase panel links, in the order a post-mortem reader wants them.
    // Only files that really exist are linked, and each carries its size — the composed-prompt size is
    // deliberately HERE and on no live surface (design 23 §4): a reader correlating truncations across runs
    // is not making a Ctrl+C decision.
    private static readonly string[] BreakdownEvidencePrefixes =
        ["composed-prompt", "claude-stream", "transcript"];

    // Per-attempt file the static page inlines FIRST (mirrors LogServer's PreferenceOrder): the groomed
    // transcript leads, then the raw stream, then script stdout.
    private static readonly string[] PreferenceOrder =
        ["transcript.md", "claude-stream.jsonl", "action-stdout.log"];

    // The per-check files GateArtifacts.WriteCheck persists under <logDir>/<check-name>/ (issue #432).
    // The halt banner links whichever of them actually exist, so it is one click from the evidence.
    private static readonly string[] CapturedGateFiles = ["stdout.log", "stderr.log", "result.json"];

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The per-task link target an index renders for one task. <see cref="StaticPage"/> points at the
    /// task's static <c>&lt;taskId&gt;/index.html</c>; <see cref="Live"/> carries an absolute live-server
    /// URL whose click tails the task; <see cref="PlainText"/> renders the id as plain text (no anchor).
    /// </summary>
    public enum LinkKind
    {
        /// <summary>Plain text — no anchor (a pending / no-attempts task offline).</summary>
        PlainText,

        /// <summary>A relative link to the task's static <c>&lt;taskId&gt;/index.html</c>.</summary>
        StaticPage,

        /// <summary>An absolute live-server URL (a click tails the running task).</summary>
        Live,
    }

    /// <summary>How an index renders one task: the link target kind plus the URL for a <see cref="LinkKind.Live"/> link.</summary>
    public readonly record struct IndexLink(LinkKind Kind, string? LiveUrl = null)
    {
        /// <summary>A plain-text (non-link) cell.</summary>
        public static readonly IndexLink Plain = new(LinkKind.PlainText);

        /// <summary>A relative link to the task's static page.</summary>
        public static readonly IndexLink Static = new(LinkKind.StaticPage);

        /// <summary>An absolute live-server link to tail the running task.</summary>
        public static IndexLink LiveTo(string url) => new(LinkKind.Live, url);
    }

    /// <summary>
    /// Export the whole DURABLE static site for one FLAT run (no waves) — see the waves-aware overload
    /// <see cref="ExportSite(string, IReadOnlyList{TaskNode}, IReadOnlyList{WaveNode}, JournalDocument)"/>.
    /// </summary>
    public static string ExportSite(string logsRoot, IReadOnlyList<TaskNode> tasks, JournalDocument journal) =>
        ExportSite(logsRoot, tasks, Array.Empty<WaveNode>(), journal);

    /// <summary>
    /// Export the whole DURABLE static site for one run into <paramref name="logsRoot"/>
    /// (<c>&lt;planDir&gt;/logs/&lt;runId&gt;/</c>): a per-task <c>index.html</c> for every task that
    /// has attempts on disk, plus a site <c>index.html</c> with NO refresh and ALL-static links. Idempotent
    /// — regenerates everything each call (like <c>guardrails graph</c>). Returns the path to the site index.
    /// A task with no attempts on disk is listed (as plain text, not a link) in the index but writes no page.
    /// This is the post-hoc <c>logs --export</c> surface (SSOT §12.3) — preserved verbatim.
    ///
    /// <para>For a WAVED plan (SSOT §14, issue #380) <paramref name="waves"/> is non-empty and each wave
    /// additionally gets its own <c>&lt;waveDir&gt;/index.html</c> — a wave-scoped drill-down listing that
    /// wave's tasks (status + a link to each task's static page) with a breadcrumb back to the plan index,
    /// and the plan-wide index links to each wave's index. A FLAT plan passes an empty
    /// <paramref name="waves"/>, so no wave index is written and the plan index is byte-for-byte unchanged.</para>
    ///
    /// <para>Issue #436: when the journal carries the <c>halt</c> record (SSOT §7, issue #432) the plan
    /// index leads with the GATE-HALT banner, and a WAVE-scoped halt (<see cref="RunHalt.WaveDir"/>)
    /// additionally leads that one wave's own index — the page whose tasks the gate stopped. Every other
    /// wave's page, and every page of a run that did not halt at a gate, is unchanged.</para>
    /// </summary>
    public static string ExportSite(
        string logsRoot, IReadOnlyList<TaskNode> tasks, IReadOnlyList<WaveNode> waves, JournalDocument journal)
    {
        Directory.CreateDirectory(logsRoot);

        // Durable export: a settled-with-attempts task links to its static page, a not-yet-run task is
        // plain text. No live URLs, no meta-refresh (#103 / SSOT §12.3).
        Func<string, string> statusResolver = id => StatusWord(journal, id);
        Func<string, string?> claimResolver = id => ClaimWord(journal, id);
        Func<string, IndexLink> linkResolver =
            id => AttemptDirs(logsRoot, id).Count > 0 ? IndexLink.Static : IndexLink.Plain;

        // #524 / design 29 §4.8: the run-level index's Model column — the FULL model id (no Spectre width
        // crisis here, and this is the audit surface), disclosing a route mismatch through the shipped
        // AttemptModelSummary wording so the log site and the console/live surfaces state one fact one
        // way. Only ExportSite supplies this: the during-run index (WriteIndex, called directly by
        // OnTheFlyLogSiteObserver) gets no resolver and therefore renders no Model column at all — #524
        // was raised about a task that had already FINISHED, and the during-run index is exactly the
        // transient surface that cannot answer the question anyway.
        Func<string, string?> modelResolver = id => ModelCellText(journal, id);

        foreach (TaskNode task in tasks)
        {
            WriteTaskPageIfHasAttempts(logsRoot, task, statusResolver(task.Id), claimResolver(task.Id));
        }

        // Per-wave index (issue #380): one durable index per wave, all-static links, no refresh.
        foreach (WaveNode wave in waves)
        {
            // The DURABLE post-mortem for the JIT breakdown (issue #469). It is read from decisions[] —
            // the canonical durable store for the phase — because a breakdown halt is not a RunHalt, so
            // without this the wave page is permanently a dead end: the wave name, 0/0 tasks, and an empty
            // table. Null for an ordinary authored wave, which therefore renders unchanged.
            WriteWaveIndex(
                logsRoot, journal.RunId, wave, statusResolver, linkResolver, includeRefresh: false,
                halt: HaltForWave(journal.Halt, wave.Dir), claimResolver: claimResolver,
                phase: BreakdownPanel(logsRoot, wave, journal.Decisions));
        }

        string index = IndexHtml(
            logsRoot, journal.RunId, tasks, waves, statusResolver, linkResolver, includeRefresh: false,
            halt: journal.Halt, claimResolver: claimResolver, modelResolver: modelResolver);

        string indexPath = Path.Combine(logsRoot, "index.html");
        AtomicFile.WriteAllText(indexPath, index);
        return indexPath;
    }

    /// <summary>
    /// The halt to render on ONE wave's page (issue #436): the run's halt when it is scoped to exactly this
    /// wave, otherwise null. A plan-scoped halt (Full Flight Checks / terminal gate) belongs on the plan
    /// index only — repeating it on every wave page would claim each wave's own gate stopped the run.
    /// </summary>
    private static RunHalt? HaltForWave(RunHalt? halt, string waveDir) =>
        halt is not null && string.Equals(halt.WaveDir, waveDir, StringComparison.Ordinal) ? halt : null;

    /// <summary>
    /// Render and atomically write the during-run / final site index to
    /// <c>&lt;logsRoot&gt;/index.html</c> (issue #141 item 2). The caller supplies the status word and
    /// the per-task link target so the SAME renderer serves the during-run index (live URLs + refresh)
    /// and the settled final index (all static, no refresh). <paramref name="includeRefresh"/> is true
    /// while the run is in flight (so a <c>file://</c> browser re-reads it as it is rewritten) and false
    /// for the durable final write. Atomic temp+rename so a browser never reads a half-written file.
    /// Returns the path written.
    /// <para><paramref name="halt"/> (issue #436) is the run's gate-halt record, when one has been made;
    /// null — the during-run default, since a gate halt is only known once it has been journaled — renders
    /// the page exactly as before.</para>
    /// </summary>
    public static string WriteIndex(
        string logsRoot,
        string runId,
        IReadOnlyList<TaskNode> tasks,
        Func<string, string> statusResolver,
        Func<string, IndexLink> linkResolver,
        bool includeRefresh,
        IReadOnlyList<WaveNode>? waves = null,
        RunHalt? halt = null,
        Func<string, string?>? claimResolver = null,
        Func<string, string?>? modelResolver = null)
    {
        string index = IndexHtml(
            logsRoot, runId, tasks, waves ?? Array.Empty<WaveNode>(), statusResolver, linkResolver,
            includeRefresh, halt, claimResolver, modelResolver);
        string indexPath = Path.Combine(logsRoot, "index.html");
        AtomicFile.WriteAllText(indexPath, index);
        return indexPath;
    }

    /// <summary>
    /// Atomically write one task's static page (<c>&lt;logsRoot&gt;/&lt;taskId&gt;/index.html</c>) with its
    /// final inlined logs and a Source section, when the task has attempts on disk (issue #141 item 2).
    /// No-op when the task has no attempts yet. <paramref name="logsRoot"/> is the run's
    /// <c>logs/&lt;runId&gt;/</c> tree. Atomic temp+rename so a browser viewing the page never reads a torn file.
    /// </summary>
    /// <param name="logsRoot">The run's <c>logs/&lt;runId&gt;/</c> tree.</param>
    /// <param name="task">The task whose page is written.</param>
    /// <param name="status">
    /// The task's status word for the page's breadcrumb bar (issue #485), or null to omit it. The task page
    /// rendered NO status at all before #485 — yet the live table's finished-task <c>logs</c> link points
    /// exactly here, so an operator could click a RED row and land on a page that never said the task
    /// halted. Both production callers now supply it.
    /// </param>
    /// <param name="needsHumanKind">The agent's needs-human claim (issue #485) to chip beside the status, or null.</param>
    public static void WriteTaskPageIfHasAttempts(
        string logsRoot, TaskNode task, string? status = null, string? needsHumanKind = null)
    {
        if (AttemptDirs(logsRoot, task.Id).Count == 0)
        {
            return; // nothing to render — task never ran / not started; it stays a non-link in the index
        }

        string page = TaskPage(logsRoot, task, status, needsHumanKind);
        AtomicFile.WriteAllText(Path.Combine(logsRoot, task.Id, "index.html"), page);
    }

    // --- site index (projection of status + link target) ------------------------------------

    /// <summary>
    /// The site landing page: every task with its status word and a link target chosen by the caller's
    /// <paramref name="linkResolver"/> (static page / live URL / plain text). Regenerated on every write
    /// (never appended). When <paramref name="includeRefresh"/> is true the page carries the in-place live
    /// poll (issue #543, <see cref="LivePollScript"/>) so a SERVED view updates itself as the harness
    /// rewrites the file ("updated on the fly") and stops on its own at a settled run or a failed poll; the
    /// durable final / <c>--export</c> index omits it.
    /// <para>When <paramref name="halt"/> is present (issue #436) the page LEADS with the gate-halt banner
    /// and its CSS; when it is null not one byte of the page changes.</para>
    /// <para>
    /// Issue #524 / design 29 §4.8: <paramref name="modelResolver"/> supplies the run-level Model column
    /// — the full model id that actually ran, or the shared <c>AttemptModelSummary</c> mismatch wording.
    /// Null (the during-run <see cref="WriteIndex"/> default) renders NO Model column at all, so that
    /// transient surface stays exactly as it was; only <see cref="ExportSite(string,IReadOnlyList{TaskNode},IReadOnlyList{WaveNode},JournalDocument)"/>
    /// supplies one. A task the resolver has nothing for (never run) gets the placeholder <c>—</c> rather
    /// than an empty cell that could read as a repeat of the row above it.
    /// </para>
    /// </summary>
    private static string IndexHtml(
        string logsRoot,
        string runId,
        IReadOnlyList<TaskNode> tasks,
        IReadOnlyList<WaveNode> waves,
        Func<string, string> statusResolver,
        Func<string, IndexLink> linkResolver,
        bool includeRefresh,
        RunHalt? halt,
        Func<string, string?>? claimResolver,
        Func<string, string?>? modelResolver = null)
    {
        var rows = new StringBuilder();
        foreach (TaskNode task in tasks)
        {
            string status = statusResolver(task.Id);
            string cell = IndexCell(task.Id, linkResolver(task.Id));
            string? claim = claimResolver?.Invoke(task.Id);

            rows.Append("<tr><td>").Append(cell).Append("</td>")
                .Append("<td class=\"status\" data-status=\"").Append(Enc(status)).Append('"')
                .Append(ClaimAttribute(claim)).Append('>')
                .Append(Enc(status)).Append(ClaimChip(claim)).Append("</td>")
                .Append("<td>").Append(Enc(task.Description)).Append("</td>");

            if (modelResolver is not null)
            {
                string model = modelResolver(task.Id) ?? "—";
                rows.Append("<td>").Append(Enc(model)).Append("</td>");
            }

            rows.Append("</tr>");
        }

        // The during-run page updates IN PLACE (issue #543 — see LivePollScript) instead of reloading the
        // whole document every 2s. Both the script and its CSS come from this one conditional, so the
        // FINAL settled page carries no trace of the poll and keeps its exact pre-#543 bytes.
        string livePoll = includeRefresh ? LivePollScript() : string.Empty;
        string livePollStyle = includeRefresh ? LivePollStyle : string.Empty;

        string note = includeRefresh
            ? "Live run — this page updates itself in place. Running tasks tail their log; settled tasks link to a static page; not-yet-run tasks are plain text."
            : "Static export of this run. Settled tasks link to their inlined log page; not-yet-run tasks are plain text.";

        // Waves drill-down (issue #380): for a WAVED plan, a nav section linking each wave's own index —
        // the wave-scoped drill-down target #379's collapsed completed-wave line points at. A FLAT plan
        // (empty waves) renders nothing here, so its index bytes are unchanged.
        string wavesNav = WavesNav(waves, statusResolver);

        // The gate-halt banner (issue #436) and its CSS — both empty strings when the run did not halt at
        // a gate, so the no-halt page is byte-identical to the pre-#436 one.
        string banner = HaltBanner(logsRoot, halt, runId, pageWaveDir: null);
        string haltStyle = banner.Length == 0 ? string.Empty : HaltStyle;

        // Additive only (design 29 §4.8): appended after Description, so the existing Task/Status/
        // Description head and every existing consumer of it is untouched when modelResolver is null.
        string modelHeader = modelResolver is not null ? "<th>Model</th>" : string.Empty;

        return $"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Guardrails run {Enc(runId)} — log site</title>
<style>
{SharedStyle}{haltStyle}{livePollStyle}
</style>
</head>
<body>
<h1>Guardrails run — task logs</h1>{banner}
<p>{Enc(note)}</p>{wavesNav}
<table>
<thead><tr><th>Task</th><th>Status</th><th>Description</th>{modelHeader}</tr></thead>
<tbody>
{rows}
</tbody>
</table>{livePoll}
</body>
</html>
""";
    }

    /// <summary>
    /// The plan-wide index's "Waves" drill-down nav (issue #380): one link per wave to its own
    /// <c>&lt;waveDir&gt;/index.html</c>, each with a task-progress count. Empty string for a FLAT plan
    /// (no waves) so the plan index is byte-for-byte unchanged. Rendered with a leading newline so it slots
    /// cleanly under the intro paragraph.
    /// </summary>
    private static string WavesNav(IReadOnlyList<WaveNode> waves, Func<string, string> statusResolver)
    {
        if (waves.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("\n<h2>Waves</h2>\n<div class=\"bar\">");
        for (int i = 0; i < waves.Count; i++)
        {
            WaveNode wave = waves[i];
            if (i > 0)
            {
                sb.Append(" &middot; ");
            }

            sb.Append("<a href=\"").Append(Uri.EscapeDataString(wave.Dir)).Append("/index.html\">")
                .Append(Enc(wave.Dir)).Append("</a> (").Append(Enc(WaveProgress(wave, statusResolver))).Append(')');
        }

        sb.Append("</div>");
        return sb.ToString();
    }

    // --- gate-halt banner (issue #436) ------------------------------------------------------

    /// <summary>
    /// The GATE-HALT banner (issue #436) — the render of the <c>halt</c> record #432 started persisting.
    /// Empty string when <paramref name="halt"/> is null, which is the only reason the no-halt page stays
    /// byte-identical.
    /// <para>
    /// Why it exists: a gate halt settles NO task, so the table below it is a wall of <c>pending</c> rows —
    /// the exact page a maintainer opened after a failed wave-entry gate and concluded "nothing happened".
    /// The banner therefore states, per gate kind, WHERE in the run the stop occurred (before the DAG /
    /// before this wave's tasks / after them / after the whole DAG drained green), names every failing
    /// check with its reason, and links straight into that check's captured
    /// <c>stdout.log</c>/<c>stderr.log</c>/<c>result.json</c>.
    /// </para>
    /// <para>
    /// Hrefs are relative to the PAGE being rendered: the plan index sits at
    /// <c>logs/&lt;runId&gt;/index.html</c> and a wave index at <c>logs/&lt;runId&gt;/&lt;waveDir&gt;/index.html</c>,
    /// while <see cref="RunHalt.LogDir"/> is PLAN-relative — so <paramref name="pageWaveDir"/> says which
    /// prefix the page already stands in. Only artifacts that really exist on disk are linked (capture is
    /// best-effort by contract, SSOT §8), and a zero-byte one is <c>(empty)</c>-marked exactly as the task
    /// page marks an empty attempt file (#141 item 4) — a rendered link here is always a real file.
    /// </para>
    /// </summary>
    private static string HaltBanner(string logsRoot, RunHalt? halt, string runId, string? pageWaveDir)
    {
        if (halt is null)
        {
            return string.Empty;
        }

        string kind = JournalJson.RunHaltToken(halt.Kind);
        string? hrefBase = SiteRelativeLogDir(halt.LogDir, runId, pageWaveDir);
        string? rootRelative = SiteRelativeLogDir(halt.LogDir, runId, pageWaveDir: null);
        string? absoluteLogDir = rootRelative is null
            ? null
            : Path.Combine(logsRoot, rootRelative.Replace('/', Path.DirectorySeparatorChar));

        var sb = new StringBuilder();
        sb.Append("\n<section class=\"halt\" data-halt-kind=\"").Append(Enc(kind)).Append("\">");
        sb.Append("<h2 class=\"halt-title\">Run halted at a gate &mdash; ")
            .Append(Enc(GateLabel(halt.Kind))).Append("</h2>");
        sb.Append("<p class=\"halt-headline\">").Append(Enc(halt.Headline)).Append("</p>");
        sb.Append("<p class=\"halt-scope\">").Append(Enc(HaltScopeNote(halt.Kind))).Append("</p>");

        sb.Append("<div class=\"halt-meta\">kind <code>").Append(Enc(kind)).Append("</code>");
        if (!string.IsNullOrEmpty(halt.WaveDir))
        {
            // From the plan index the halted wave's own page is one click away; ON that page it IS the page.
            sb.Append(" &middot; wave ").Append(pageWaveDir is null
                ? $"<a href=\"{Href(halt.WaveDir + "/index.html")}\">{Enc(halt.WaveDir)}</a>"
                : $"<code>{Enc(halt.WaveDir)}</code>");
        }

        sb.Append(" &middot; halted ").Append(Enc(FormatHaltedAt(halt.HaltedAt))).Append("</div>");

        if (halt.FailedChecks.Count > 0)
        {
            sb.Append("<h3 class=\"halt-sub\">Failed check").Append(halt.FailedChecks.Count == 1 ? string.Empty : "s")
                .Append("</h3><ul class=\"halt-checks\">");
            foreach (FailedGuardrail check in halt.FailedChecks)
            {
                sb.Append("<li><span class=\"halt-check\">").Append(Enc(check.Name)).Append("</span> &mdash; ")
                    .Append("<span class=\"halt-reason\">").Append(Enc(check.Reason)).Append("</span>");
                AppendCapturedCheckLinks(sb, absoluteLogDir, hrefBase, check.Name);
                sb.Append("</li>");
            }

            sb.Append("</ul>");
        }

        sb.Append("<div class=\"halt-logdir\">Captured gate output: ").Append(LogDirLink(halt.LogDir, hrefBase, absoluteLogDir))
            .Append("</div></section>");

        return sb.ToString();
    }

    /// <summary>The gate a halt kind names, in the words the four-folder model uses.</summary>
    private static string GateLabel(RunHaltKind kind) => kind switch
    {
        RunHaltKind.PlanPreflightFailed => "plan preflight (Full Flight Checks)",
        RunHaltKind.WaveEntryGateFailed => "wave entry gate",
        RunHaltKind.WaveExitGateFailed => "wave exit gate",
        RunHaltKind.PlanGuardrailFailed => "terminal plan gate",
        _ => "gate"
    };

    /// <summary>
    /// WHERE in the run the stop happened — the sentence that separates a gate halt from a task failure.
    /// Each kind is stated precisely rather than blanket "no task ran": a wave EXIT gate and the terminal
    /// plan gate fire AFTER work has run, and claiming otherwise would be a new lie in place of the silence.
    /// </summary>
    private static string HaltScopeNote(RunHaltKind kind) => kind switch
    {
        RunHaltKind.PlanPreflightFailed =>
            "The run stopped BEFORE the task DAG: no task was scheduled and none ran, so every task below is "
            + "still pending. This is not a task failure.",
        RunHaltKind.WaveEntryGateFailed =>
            "The run stopped BEFORE the tasks of this wave: the wave entry gate failed, so no task in the "
            + "wave was scheduled and they are still pending. This is not a task failure.",
        RunHaltKind.WaveExitGateFailed =>
            "The run stopped AFTER the tasks of this wave drained: the wave exit gate failed, so no later "
            + "wave ran. This is a gate failure, not a task failure.",
        RunHaltKind.PlanGuardrailFailed =>
            "The run stopped AFTER the task DAG drained green: the terminal plan gate failed on the merged "
            + "HEAD. Every task can be green and the run still stopped here.",
        _ => "The run stopped at a deterministic gate, not inside a task."
    };

    /// <summary>The halt time, rendered culture-invariantly so the page bytes never depend on the host locale.</summary>
    private static string FormatHaltedAt(DateTimeOffset haltedAt) =>
        haltedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The <c>logDir</c> pointer itself: a link when that directory really is on disk under this site,
    /// otherwise the recorded path as plain text (a halt whose capture never landed still says WHERE it
    /// would have been). Empty-ish <c>logDir</c> (no run id at halt time) says so instead of linking nowhere.
    /// </summary>
    private static string LogDirLink(string? logDir, string? hrefBase, string? absoluteLogDir)
    {
        if (string.IsNullOrEmpty(logDir))
        {
            return "<code>(not captured)</code>";
        }

        return hrefBase is not null && absoluteLogDir is not null && Directory.Exists(absoluteLogDir)
            ? $"<a href=\"{Href(hrefBase)}\">{Enc(logDir)}</a>"
            : $"<code>{Enc(logDir)}</code>";
    }

    /// <summary>
    /// Append the one-click links into a failing check's captured output —
    /// <c>&lt;logDir&gt;/&lt;check-name&gt;/{stdout.log,stderr.log,result.json}</c> (issue #432's layout,
    /// keyed through the SAME <see cref="Core.Execution.GateArtifacts.Sanitize"/> the writer used). Only
    /// files that exist are linked; nothing is appended when the capture is missing entirely.
    /// </summary>
    private static void AppendCapturedCheckLinks(
        StringBuilder sb, string? absoluteLogDir, string? hrefBase, string checkName)
    {
        if (absoluteLogDir is null || hrefBase is null)
        {
            return;
        }

        string folder = Core.Execution.GateArtifacts.Sanitize(checkName);
        var links = new List<string>();
        foreach (string file in CapturedGateFiles)
        {
            string path = Path.Combine(absoluteLogDir, folder, file);
            if (!File.Exists(path))
            {
                continue;
            }

            bool empty = IsZeroByte(path);
            string cls = empty ? " class=\"empty\"" : string.Empty;
            string label = empty ? $"{file} (empty)" : file;
            links.Add($"<a{cls} href=\"{Href($"{hrefBase}/{folder}/{file}")}\">{Enc(label)}</a>");
        }

        if (links.Count > 0)
        {
            sb.Append("<span class=\"halt-files\"> &mdash; ").Append(string.Join(" &middot; ", links)).Append("</span>");
        }
    }

    /// <summary>
    /// The journal's PLAN-relative <c>logDir</c> (<c>logs/&lt;runId&gt;/…</c>) re-expressed relative to the
    /// page rendering it: the plan index (<paramref name="pageWaveDir"/> null) stands in
    /// <c>logs/&lt;runId&gt;/</c>, a wave index in <c>logs/&lt;runId&gt;/&lt;waveDir&gt;/</c>. Null when the
    /// record carries no <c>logDir</c>, or when it does not sit under the page's own tree — in which case
    /// the banner shows the recorded path as text rather than inventing a traversal out of the site.
    /// </summary>
    private static string? SiteRelativeLogDir(string? logDir, string runId, string? pageWaveDir)
    {
        if (string.IsNullOrEmpty(logDir) || string.IsNullOrEmpty(runId))
        {
            return null;
        }

        string prefix = string.IsNullOrEmpty(pageWaveDir)
            ? $"logs/{runId}/"
            : $"logs/{runId}/{pageWaveDir}/";

        return logDir.StartsWith(prefix, StringComparison.Ordinal) && logDir.Length > prefix.Length
            ? logDir[prefix.Length..]
            : null;
    }

    // --- wave-phase panel (issue #469) ------------------------------------------------------

    /// <summary>
    /// One wave-scoped PHASE, rendered as a block above that wave's (possibly empty) task table. It closes
    /// the gap that a JIT breakdown leaves on this surface: no task event fires during it, and a breakdown
    /// halt is not a <c>RunHalt</c>, so before #469 a reader opened <c>&lt;wave&gt;/index.html</c> and found
    /// the wave name, <c>0/0 tasks</c>, and an empty table — with no banner, no explanation, and no pointer
    /// to the evidence.
    /// <para>Named for the general case (design 23 §9) so #476's wave gates reuse the same panel as a
    /// CONTENT change, not a second mechanism.</para>
    /// </summary>
    /// <param name="Phase">The phase discriminator, rendered as <c>data-phase</c>. Only <c>breakdown</c> exists today.</param>
    /// <param name="State">
    /// <c>pending</c> · <c>running</c> · <c>authored</c> · <c>incomplete</c> · <c>cut-off</c>. Rendered as
    /// <c>data-state</c> AND used to pick the palette, so state is never carried by colour alone.
    /// </param>
    /// <param name="Title">The panel heading.</param>
    /// <param name="Headline">The one-line summary, or null.</param>
    /// <param name="Body">Plain-text paragraphs, each rendered as its own <c>&lt;p&gt;</c>.</param>
    /// <param name="Detail">
    /// The verbatim halt detail (the §6.1/§6.2 accounting), rendered pre-formatted so the reverted/kept
    /// lists survive. Null while the phase is running.
    /// </param>
    /// <param name="Note">A muted trailing note (the "no percentage is shown, and why" paragraph).</param>
    /// <param name="EvidenceDirectory">
    /// The breakdown log directory ON DISK, used to size and existence-check the evidence links. Null ⇒ no
    /// evidence list (a link is only ever rendered for a file that is really there).
    /// </param>
    /// <param name="EvidenceHref">The page-relative href of that same directory (e.g. <c>breakdown</c>).</param>
    public sealed record PhasePanel(
        string Phase,
        string State,
        string Title,
        string? Headline = null,
        IReadOnlyList<string>? Body = null,
        string? Detail = null,
        string? Note = null,
        string? EvidenceDirectory = null,
        string? EvidenceHref = null);

    /// <summary>
    /// The panel a WAVE page should carry for the JIT breakdown, derived from what is DURABLY recorded —
    /// the journal's <c>decisions[]</c> (the canonical store for this phase, SSOT §7) plus the wave's own
    /// task count. Returns null for an ordinary authored wave, so its page keeps its exact current bytes.
    /// </summary>
    /// <param name="logsRoot">The run's <c>logs/&lt;runId&gt;/</c> tree.</param>
    /// <param name="wave">The wave whose page is being rendered.</param>
    /// <param name="decisions">The journal's <c>decisions[]</c>, or null.</param>
    public static PhasePanel? BreakdownPanel(
        string logsRoot, WaveNode wave, IReadOnlyList<DecisionEntry>? decisions)
    {
        DecisionEntry? settled = decisions?
            .LastOrDefault(d => BreakdownGates.IsBreakdown(d.Gate)
                                && string.Equals(d.Subject, wave.Dir, StringComparison.Ordinal));

        if (settled is null)
        {
            // No breakdown was recorded for this wave. A wave that still has NO tasks is a JIT stub whose
            // barrier was never reached; every other wave gets no panel at all.
            return wave.Tasks.Count > 0 ? null : PendingBreakdownPanel(logsRoot, wave);
        }

        (string state, string title) = settled.Gate switch
        {
            BreakdownGates.Complete => ("authored", "Tasks authored &mdash; JIT breakdown"),
            BreakdownGates.Incomplete => ("incomplete", "Breakdown INCOMPLETE &mdash; valid prefix kept"),
            _ => ("cut-off", "Breakdown FAILED &mdash; the attempt was reverted")
        };

        return new PhasePanel(
            BreakdownPhaseToken,
            state,
            title,
            Headline: settled.Headline,
            Detail: settled.Detail,
            EvidenceDirectory: BreakdownDirectory(logsRoot, wave.Dir),
            EvidenceHref: BreakdownHref);
    }

    /// <summary>The PENDING panel: a JIT stub whose barrier has not been reached. Nothing here has run.</summary>
    private static PhasePanel PendingBreakdownPanel(string logsRoot, WaveNode wave) =>
        new(BreakdownPhaseToken,
            "pending",
            "Not yet authored &mdash; JIT breakdown pending",
            Body:
            [
                "This wave is a JIT stub. Its tasks are authored at the wave barrier, after the previous "
                + "wave completes and its exit gate passes. Nothing here has run."
            ],
            EvidenceDirectory: BreakdownDirectory(logsRoot, wave.Dir),
            EvidenceHref: BreakdownHref);

    /// <summary>
    /// The RUNNING panel for an in-flight breakdown (design 23 §5.3) — the during-run page carries the
    /// in-place live poll (issue #543), which re-fetches and swaps the body, so this elapsed clock still
    /// animates for free; only the cadence changed, from the old 2s whole-document reload to the poll's
    /// <see cref="LivePollMs"/>. The note is load-bearing: it says in words why there is no percentage,
    /// which is the question the count invites.
    /// </summary>
    /// <param name="logsRoot">The run's <c>logs/&lt;runId&gt;/</c> tree.</param>
    /// <param name="waveDir">The wave being authored.</param>
    /// <param name="elapsed">How long the session has run.</param>
    /// <param name="ceiling">The hard wall-clock ceiling.</param>
    /// <param name="progress">The observed count/liveness fragments (already rendered by <c>BreakdownProgress</c>).</param>
    public static PhasePanel RunningBreakdownPanel(
        string logsRoot, string waveDir, TimeSpan elapsed, TimeSpan ceiling, string progress) =>
        new(BreakdownPhaseToken,
            "running",
            "Authoring tasks &mdash; JIT breakdown",
            Headline: $"{BreakdownProgress.FormatClock(elapsed)} elapsed of a "
                      + $"{BreakdownProgress.FormatClock(ceiling)} ceiling.",
            Body: progress.Length == 0 ? [] : [progress],
            Note: "This wave had no tasks when the run started; the harness is authoring them now. The "
                  + "folder count is what is on disk and only goes up — the final task count is not known "
                  + "in advance, so no percentage is shown.",
            EvidenceDirectory: BreakdownDirectory(logsRoot, waveDir),
            EvidenceHref: BreakdownHref);

    /// <summary>
    /// The panel for a breakdown that has JUST settled, from what the observer knows at the event — so the
    /// page is correct the instant the phase ends rather than the instant the journal is exported. The
    /// durable export then supersedes it with the full halt accounting from <c>decisions[]</c>.
    /// </summary>
    /// <param name="logsRoot">The run's <c>logs/&lt;runId&gt;/</c> tree.</param>
    /// <param name="waveDir">The wave that was being authored.</param>
    /// <param name="failureKind">Null when the session ended cleanly and the wave validated; else the reason token.</param>
    /// <param name="elapsed">The session's wall clock.</param>
    /// <param name="detail">The already-composed outcome fragment (<c>BreakdownProgress.TerminalDetail</c>).</param>
    public static PhasePanel SettledBreakdownPanel(
        string logsRoot, string waveDir, string? failureKind, TimeSpan elapsed, string detail)
    {
        (string state, string title) = failureKind switch
        {
            null => ("authored", "Tasks authored &mdash; JIT breakdown"),
            BreakdownFailureTokens.Incomplete => ("incomplete", "Breakdown INCOMPLETE &mdash; valid prefix kept"),
            _ => ("cut-off", "Breakdown did not complete &mdash; JIT breakdown")
        };

        return new PhasePanel(
            BreakdownPhaseToken,
            state,
            title,
            Headline: $"{BreakdownProgress.TerminalWord(failureKind)} after "
                      + $"{BreakdownProgress.FormatClock(elapsed)} — {detail}",
            EvidenceDirectory: BreakdownDirectory(logsRoot, waveDir),
            EvidenceHref: BreakdownHref);
    }

    /// <summary>The phase token every breakdown panel carries as <c>data-phase</c>.</summary>
    private const string BreakdownPhaseToken = "breakdown";

    /// <summary>The breakdown log directory's href, relative to the wave page it is rendered on.</summary>
    private const string BreakdownHref = "breakdown";

    private static string BreakdownDirectory(string logsRoot, string waveDir) =>
        Path.Combine(logsRoot, waveDir, BreakdownHref);

    /// <summary>
    /// Render one <see cref="PhasePanel"/>, or the empty string when there is none — which is what keeps a
    /// no-breakdown page byte-identical.
    /// </summary>
    private static string PhaseSection(PhasePanel? panel)
    {
        if (panel is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("\n<section class=\"phase\" data-phase=\"").Append(Enc(panel.Phase))
            .Append("\" data-state=\"").Append(Enc(panel.State)).Append("\">");
        sb.Append("<h2 class=\"phase-title\">").Append(panel.Title).Append("</h2>");

        if (!string.IsNullOrEmpty(panel.Headline))
        {
            sb.Append("<p class=\"phase-headline\">").Append(Enc(panel.Headline)).Append("</p>");
        }

        foreach (string paragraph in panel.Body ?? [])
        {
            sb.Append("<p>").Append(Enc(paragraph)).Append("</p>");
        }

        if (!string.IsNullOrWhiteSpace(panel.Detail))
        {
            // Pre-formatted: the §14.11 revert accounting is a list of aligned lines, and re-flowing it
            // into a paragraph would lose exactly the "what moved / what was kept" structure #471 added.
            sb.Append("<h3 class=\"phase-sub\">What the harness recorded</h3>");
            sb.Append("<pre class=\"phase-detail\">").Append(Enc(panel.Detail)).Append("</pre>");
        }

        if (!string.IsNullOrEmpty(panel.Note))
        {
            sb.Append("<p class=\"phase-note\">").Append(Enc(panel.Note)).Append("</p>");
        }

        AppendPhaseEvidence(sb, panel);
        sb.Append("</section>");
        return sb.ToString();
    }

    /// <summary>
    /// The evidence list: every file the breakdown teed, with its size, plus the quarantine folder when one
    /// exists. Only files really on disk are linked (capture is best-effort by contract, SSOT §8), so a
    /// rendered anchor is never a dead end — the same rule the halt banner follows.
    /// </summary>
    private static void AppendPhaseEvidence(StringBuilder sb, PhasePanel panel)
    {
        if (panel.EvidenceDirectory is not { } dir || panel.EvidenceHref is not { } href)
        {
            return;
        }

        var links = new List<string>();
        try
        {
            if (Directory.Exists(dir))
            {
                foreach (string file in Directory.EnumerateFiles(dir)
                             .Where(f => BreakdownEvidencePrefixes.Any(p =>
                                 Path.GetFileName(f).StartsWith(p, StringComparison.Ordinal)))
                             .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
                {
                    string name = Path.GetFileName(file);
                    long bytes = new FileInfo(file).Length;
                    links.Add($"<a href=\"{Href($"{href}/{name}")}\">{Enc(name)}</a> ({Enc(FormatBytes(bytes))})");
                }

                if (Directory.Exists(Path.Combine(dir, "rejected")))
                {
                    links.Insert(0, $"quarantined to <a href=\"{Href($"{href}/rejected/")}\">{href}/rejected/</a>");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // best-effort, exactly like every other site probe
        }

        if (links.Count == 0)
        {
            return;
        }

        sb.Append("<div class=\"phase-evidence\">Evidence: ").Append(string.Join(" &middot; ", links)).Append("</div>");
    }

    /// <summary>A file size in the units a reader thinks in, culture-invariantly so the page bytes never depend on the host locale.</summary>
    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):F1} MB")
    };

    // --- per-wave index (issue #380) --------------------------------------------------------

    /// <summary>
    /// Render and atomically write one wave's index (<c>&lt;logsRoot&gt;/&lt;waveDir&gt;/index.html</c>,
    /// issue #380) — the wave-scoped drill-down that lists ONLY that wave's tasks (status + a link to each
    /// task's static page) with a progress count and a breadcrumb back to the plan-wide index. It reuses the
    /// SAME shared shell (<see cref="SharedStyle"/> + status colours + table layout) as the plan index — no
    /// forked template (#103 Request 2). The caller supplies the status word and per-task link target keyed
    /// by the WAVE-QUALIFIED task id (identical resolvers to the plan index), so it serves both the
    /// during-run wave index (live URLs + refresh) and the durable final one (all-static, no refresh). A
    /// task's link is rendered relative to THIS wave folder (its wave-relative folder name), because the
    /// wave index sits one level up from its task pages. Atomic temp+rename. Returns the path written.
    /// <para><paramref name="halt"/> (issue #436) is THIS wave's gate halt — the entry or exit gate of
    /// <paramref name="wave"/> — or null for every other page, which renders exactly as before. The caller
    /// selects it; the renderer never guesses which wave a halt belongs to.</para>
    /// </summary>
    public static string WriteWaveIndex(
        string logsRoot,
        string runId,
        WaveNode wave,
        Func<string, string> statusResolver,
        Func<string, IndexLink> linkResolver,
        bool includeRefresh,
        RunHalt? halt = null,
        Func<string, string?>? claimResolver = null,
        PhasePanel? phase = null)
    {
        string html = WaveIndexHtml(
            logsRoot, runId, wave, statusResolver, linkResolver, includeRefresh, halt, claimResolver, phase);
        string waveDir = Path.Combine(logsRoot, wave.Dir);
        Directory.CreateDirectory(waveDir); // the wave folder may not exist yet (all tasks still pending)
        string indexPath = Path.Combine(waveDir, "index.html");
        AtomicFile.WriteAllText(indexPath, html);
        return indexPath;
    }

    private static string WaveIndexHtml(
        string logsRoot,
        string runId,
        WaveNode wave,
        Func<string, string> statusResolver,
        Func<string, IndexLink> linkResolver,
        bool includeRefresh,
        RunHalt? halt,
        Func<string, string?>? claimResolver,
        PhasePanel? phase)
    {
        var rows = new StringBuilder();
        foreach (TaskNode task in wave.Tasks)
        {
            string status = statusResolver(task.Id);
            string cell = WaveIndexCell(task, linkResolver(task.Id));
            string? claim = claimResolver?.Invoke(task.Id);

            rows.Append("<tr><td>").Append(cell).Append("</td>")
                .Append("<td class=\"status\" data-status=\"").Append(Enc(status)).Append('"')
                .Append(ClaimAttribute(claim)).Append('>')
                .Append(Enc(status)).Append(ClaimChip(claim)).Append("</td>")
                .Append("<td>").Append(Enc(task.Description)).Append("</td></tr>");
        }

        // Same in-place poll as the plan index (issue #543 — see LivePollScript); the wave page had the
        // identical 2s whole-document reload and the identical never-stops defect.
        string livePoll = includeRefresh ? LivePollScript() : string.Empty;
        string livePollStyle = includeRefresh ? LivePollStyle : string.Empty;

        string note = includeRefresh
            ? "Live run — this wave page updates itself in place. Running tasks tail their log; settled tasks link to a static page; not-yet-run tasks are plain text."
            : "Static export of this wave. Settled tasks link to their inlined log page; not-yet-run tasks are plain text.";

        // This wave's own gate halt (issue #436); empty for every wave whose gates did not stop the run,
        // so those pages keep their exact pre-#436 bytes.
        string banner = HaltBanner(logsRoot, halt, runId, pageWaveDir: wave.Dir);
        string haltStyle = banner.Length == 0 ? string.Empty : HaltStyle;

        // The wave-phase panel (issue #469) and its CSS — both empty strings for a wave with no breakdown,
        // so those pages keep their exact pre-#469 bytes.
        string phasePanel = PhaseSection(phase);
        string phaseStyle = phasePanel.Length == 0 ? string.Empty : PhaseStyle;

        return $"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{Enc(wave.Dir)} — Guardrails wave log ({Enc(runId)})</title>
<style>
{SharedStyle}{haltStyle}{phaseStyle}{livePollStyle}
</style>
</head>
<body>
<h1>{Enc(wave.Dir)} — wave log</h1>{banner}{phasePanel}
<div class="bar"><a href="../index.html">&larr; all waves</a> &middot; {Enc(WaveProgress(wave, statusResolver))}</div>
<p>{Enc(note)}</p>
<table>
<thead><tr><th>Task</th><th>Status</th><th>Description</th></tr></thead>
<tbody>
{rows}
</tbody>
</table>{livePoll}
</body>
</html>
""";
    }

    /// <summary>
    /// The per-task cell for a WAVE index: like <see cref="IndexCell"/> but the static-page href and the
    /// displayed id are the task's WAVE-RELATIVE folder name (the segment after the last <c>/</c> of the
    /// wave-qualified id, e.g. <c>01-author-tests</c> for <c>wave-02-provision/01-author-tests</c>) — because
    /// the wave index lives INSIDE the wave folder, one level up from its task pages, so the relative path is
    /// just <c>&lt;taskFolder&gt;/index.html</c>. A live URL is absolute (unchanged); plain text otherwise.
    /// </summary>
    private static string WaveIndexCell(TaskNode task, IndexLink link)
    {
        string folder = WaveRelativeFolder(task.Id);
        return link.Kind switch
        {
            LinkKind.StaticPage => $"<a href=\"{Uri.EscapeDataString(folder)}/index.html\">{Enc(folder)}</a>",
            LinkKind.Live when link.LiveUrl is { } url => $"<a href=\"{Enc(url)}\">{Enc(folder)}</a>",
            _ => Enc(folder), // PlainText, or a Live link with no URL (no server) → not a link
        };
    }

    /// <summary>The wave-relative task folder name — the segment after the last <c>/</c> of a wave-qualified id.</summary>
    private static string WaveRelativeFolder(string taskId)
    {
        int slash = taskId.LastIndexOf('/');
        return slash >= 0 ? taskId[(slash + 1)..] : taskId;
    }

    /// <summary>
    /// A wave's task-progress count for the wave heading / plan-index nav: "<c>N/M complete</c>", where a
    /// task counts as complete when its status word is <c>succeeded</c> or <c>skipped</c> (a resumed
    /// already-green task). Read through the SAME status resolver the rows use, so it is always consistent
    /// with the table below it.
    /// </summary>
    private static string WaveProgress(WaveNode wave, Func<string, string> statusResolver)
    {
        int total = wave.Tasks.Count;
        int complete = 0;
        foreach (TaskNode task in wave.Tasks)
        {
            string status = statusResolver(task.Id);
            if (status is "succeeded" or "skipped")
            {
                complete++;
            }
        }

        return $"{complete}/{total} complete";
    }

    private static string IndexCell(string taskId, IndexLink link) => link.Kind switch
    {
        LinkKind.StaticPage => $"<a href=\"{Uri.EscapeDataString(taskId)}/index.html\">{Enc(taskId)}</a>",
        LinkKind.Live when link.LiveUrl is { } url => $"<a href=\"{Enc(url)}\">{Enc(taskId)}</a>",
        _ => Enc(taskId), // PlainText, or a Live link with no URL (no server) → not a link
    };

    // --- per-task page (inlined attempts + source) ------------------------------------------

    /// <summary>
    /// One task's static page: every attempt on disk is rendered into its own <c>&lt;section
    /// data-attempt="N"&gt;</c> (issue #206), each carrying a file <c>&lt;select&gt;</c> combobox that
    /// toggles between that attempt's files, all inlined as hidden <c>&lt;pre class="filebody"&gt;</c>
    /// blocks (the PREFERRED file — transcript, else raw stream, else action stdout — shown first).
    /// Whenever there is at least one attempt, an attempt <c>&lt;select id="attemptselect"&gt;</c> — always
    /// rendered, matching the live viewer's attempt dropdown (<see cref="LogServer"/>: its
    /// <c>&lt;select id="attempt"&gt;</c> is unconditionally present in the page shell, even with a single
    /// attempt — populated with just one option) — lets the user pick which attempt's section is visible;
    /// every attempt's markup stays inlined in the one exported file (a <c>file://</c> page can't fetch
    /// siblings), the dropdown only shows/hides <c>&lt;section&gt;</c>s. Default selection is the LATEST
    /// attempt, matching the live viewer's default. (Before issue #241's follow-up, a single-attempt task
    /// rendered no dropdown at all — an inconsistency with the live viewer, caught live during a real
    /// dogfood: a user who watched a task pass on attempt 1 in the live view, then checked the exported
    /// static page afterward, found the dropdown had disappeared.) The existing per-attempt file combobox
    /// is untouched: it stays nested inside its attempt's <c>&lt;section&gt;</c> and is shown/hidden as
    /// part of it. A Source section (action file + guardrail scripts from <c>&lt;task.Directory&gt;</c>,
    /// #141 item 3) and the "← all tasks" back-link follow, unchanged.
    ///
    /// <para>Trade-off (#145 Feature 2, extended by #206): inlining every attempt's every file bloats the
    /// page by the full size of each raw stream. Accepted for the audit/demo use — a <c>file://</c> page
    /// has no other way to show siblings — so it is deliberately uncapped.</para>
    /// </summary>
    private static string TaskPage(string logsRoot, TaskNode task, string? status, string? needsHumanKind)
    {
        IReadOnlyList<int> attempts = AttemptNumbers(logsRoot, task.Id);
        var sections = new StringBuilder();

        // The latest attempt (highest number) is the default-visible one, matching the live viewer's
        // "follow latest" default (LogServer's TaskTemplate: `asel.value = ... d.attempt`).
        int latest = attempts.Count > 0 ? attempts[^1] : 0;

        if (attempts.Count > 0)
        {
            AppendAttemptSelect(sections, attempts, latest);
        }

        foreach (int n in attempts)
        {
            string attemptDir = Path.Combine(logsRoot, task.Id, $"attempt-{n}");
            IReadOnlyList<string> files = AttemptFiles(attemptDir);
            string? preferred = PreferenceOrder.FirstOrDefault(files.Contains) ?? files.FirstOrDefault();
            bool visible = n == latest;

            sections.Append("<section class=\"attempt\" data-attempt=\"").Append(n).Append('"')
                .Append(visible ? string.Empty : " hidden").Append('>');
            sections.Append("<h2>attempt ").Append(n).Append("</h2>");
            AppendRouteLogLink(sections, n, files);
            AppendAttemptFiles(sections, attemptDir, n, files, preferred);
            sections.Append("</section>");
        }

        if (attempts.Count == 0)
        {
            sections.Append("<pre>no attempts captured</pre>");
        }

        sections.Append(SourceSection(logsRoot, task));

        // #485: the page finally SAYS what happened. Deliberately bounded to the existing bar line — no new
        // banner, no new section, no inlined question text. `span.status` is already declared and its colour
        // rules are element-agnostic, so this costs no CSS beyond `.claim`. The pointer names
        // action-out-fragment.json because that file is one selection away in the attempt combobox above.
        string statusBar = status is { Length: > 0 }
            ? $" &middot; <span class=\"status\" data-status=\"{Enc(status)}\"{ClaimAttribute(needsHumanKind)}>"
              + $"{Enc(status)}</span>{ClaimChip(needsHumanKind)}"
            : string.Empty;

        return $"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{Enc(task.Id)} — Guardrails log</title>
<style>
{SharedStyle}
</style>
</head>
<body>
<h1>{Enc(task.Id)}</h1>
<div class="bar"><a href="../index.html">&larr; all tasks</a> &middot; {Enc(task.Description)}{statusBar}</div>
{sections}
{FileToggleScript}
</body>
</html>
""";
    }

    /// <summary>
    /// A named link to this attempt's <c>attempt-route.log</c> (issue #524): the file was already
    /// inlined as one more option in the per-attempt file combobox (<see cref="AppendAttemptFiles"/>),
    /// but that answers nobody looking at the page — nothing there names what the file is FOR. This adds
    /// a real <c>&lt;a&gt;</c>, in the page's own <c>&lt;div class="bar"&gt;</c> idiom, labelled with what
    /// it answers (the model that ran and the route that was resolved) rather than just its filename.
    /// Only rendered when the file actually exists on disk — the same "a link is only ever a real file"
    /// discipline <see cref="SourceLink"/> and the halt banner already follow.
    /// </summary>
    private static void AppendRouteLogLink(StringBuilder sections, int attempt, IReadOnlyList<string> files)
    {
        const string routeLog = "attempt-route.log";
        if (!files.Contains(routeLog))
        {
            return;
        }

        string href = Href($"attempt-{attempt}/{routeLog}");
        sections.Append("<div class=\"bar\"><a href=\"").Append(href)
            .Append("\">which model ran &amp; the route resolved (").Append(routeLog).Append(")</a></div>");
    }

    /// <summary>
    /// Render the attempt-level <c>&lt;select id="attemptselect"&gt;</c> (issue #206): one option per
    /// attempt (oldest first, matching the on-page section order), the <paramref name="latest"/> attempt
    /// pre-selected — mirroring the live viewer's "default to latest" behaviour. Emitted whenever a task
    /// has at least one attempt (issue #241) — including exactly one — so the static export always
    /// matches the live viewer's page shell, which unconditionally carries an attempt <c>&lt;select&gt;</c>
    /// regardless of how many attempts exist.
    /// </summary>
    private static void AppendAttemptSelect(StringBuilder sections, IReadOnlyList<int> attempts, int latest)
    {
        sections.Append("<div class=\"bar\">attempt <select id=\"attemptselect\" class=\"attemptselect\">");
        foreach (int n in attempts)
        {
            string sel = n == latest ? " selected" : string.Empty;
            sections.Append("<option value=\"").Append(n).Append('"').Append(sel).Append('>')
                .Append("attempt ").Append(n).Append("</option>");
        }

        sections.Append("</select></div>");
    }

    /// <summary>
    /// Render one attempt's file combobox: a <c>&lt;select&gt;</c> listing every file in the attempt (the
    /// <paramref name="preferred"/> one pre-selected; zero-byte files <c>empty</c>-marked + " (empty)"),
    /// then every file's content inlined as a hidden <c>&lt;pre class="filebody"&gt;</c> (only the preferred
    /// one shown). All elements carry <c>data-attempt="N"</c> + <c>data-file="..."</c> so the toggle script
    /// scopes show/hide to THIS attempt — multiple attempts on the page never collide. Content is
    /// HTML-encoded (arbitrary logs / LLM output) via <see cref="Enc"/> and read through the shared-handle
    /// <see cref="ReadOrEmpty"/>. An attempt with no files shows a single "no output captured" body.
    /// </summary>
    private static void AppendAttemptFiles(
        StringBuilder sections, string attemptDir, int attempt, IReadOnlyList<string> files, string? preferred)
    {
        if (files.Count == 0)
        {
            sections.Append("<div class=\"bar\">showing <code>(no files)</code></div>");
            sections.Append("<pre class=\"filebody\" data-attempt=\"").Append(attempt)
                .Append("\" data-file=\"\">").Append(Enc("no output captured")).Append("</pre>");
            return;
        }

        // The file <select>: one option per file, scoped to this attempt; the preferred file is selected.
        sections.Append("<div class=\"bar\">file <select class=\"fileselect\" data-attempt=\"").Append(attempt).Append("\">");
        foreach (string f in files)
        {
            bool empty = IsZeroByte(Path.Combine(attemptDir, f));
            string label = empty ? $"{f} (empty)" : f;
            string sel = string.Equals(f, preferred, StringComparison.Ordinal) ? " selected" : string.Empty;
            string cls = empty ? " class=\"empty\"" : string.Empty;
            sections.Append("<option").Append(cls)
                .Append(" data-attempt=\"").Append(attempt).Append('"')
                .Append(" data-file=\"").Append(Enc(f)).Append('"').Append(sel).Append('>')
                .Append(Enc(label)).Append("</option>");
        }

        sections.Append("</select></div>");

        // Inline every file's content as a hidden <pre>; only the preferred file's block is shown. A
        // file:// page can't fetch siblings, so all bodies are baked in and the <select> toggles them
        // (no fetch). Encode every body — arbitrary logs / LLM output. (Trade-off: this bloats the page
        // by the full raw-stream size; accepted for audit/demo — see TaskPage's remarks. Uncapped.)
        foreach (string f in files)
        {
            string raw = ReadOrEmpty(Path.Combine(attemptDir, f));
            bool shown = string.Equals(f, preferred, StringComparison.Ordinal);
            string hidden = shown ? string.Empty : " hidden";
            sections.Append("<pre class=\"filebody\"").Append(hidden)
                .Append(" data-attempt=\"").Append(attempt).Append('"')
                .Append(" data-file=\"").Append(Enc(f)).Append("\">")
                .Append(Enc(raw.Length > 0 ? raw : "no output captured")).Append("</pre>");
        }
    }

    /// <summary>
    /// The vanilla-JS toggle that wires every per-attempt file <c>&lt;select&gt;</c> AND (#206) the
    /// attempt-level <c>&lt;select id="attemptselect"&gt;</c>, reusing the SAME show/hide mechanism for
    /// both — no second pattern invented: on change, hide every element for that scope and unhide the one
    /// whose data attribute matches the selected option. The file toggle hides <c>.filebody</c> elements
    /// scoped by <c>data-attempt</c> so attempts don't collide; the attempt toggle hides
    /// <c>section.attempt</c> elements scoped by <c>data-attempt</c> (the nested file combobox travels
    /// with its parent section — switching attempts shows/hides it along with everything else in the
    /// section, untouched). Pure DOM — NO fetch — because this is a static <c>file://</c> page (a fetch of
    /// a sibling file is blocked offline).
    /// </summary>
    private const string FileToggleScript = """
<script>
(function () {
  document.querySelectorAll('select.fileselect').forEach(function (sel) {
    sel.addEventListener('change', function () {
      var attempt = sel.getAttribute('data-attempt');
      var file = sel.options[sel.selectedIndex].getAttribute('data-file');
      document.querySelectorAll('pre.filebody[data-attempt="' + attempt + '"]').forEach(function (pre) {
        pre.hidden = pre.getAttribute('data-file') !== file;
      });
    });
  });

  var attemptSel = document.getElementById('attemptselect');
  if (attemptSel) {
    attemptSel.addEventListener('change', function () {
      var attempt = attemptSel.options[attemptSel.selectedIndex].value;
      document.querySelectorAll('section.attempt').forEach(function (section) {
        section.hidden = section.getAttribute('data-attempt') !== attempt;
      });
    });
  }
})();
</script>
""";

    /// <summary>
    /// The static "Source" section (#141 item 3): a list of relative <c>file://</c> links from the task's
    /// static page (<c>logs/&lt;runId&gt;/&lt;taskId&gt;/index.html</c>) back to the action file and every
    /// guardrail script under <c>&lt;task.Directory&gt;</c>, so a user whose guardrail threw can open the
    /// script with one click. Paths are computed with <see cref="Path.GetRelativePath"/> (browsers open
    /// text inline). The guardrail scripts are the motivating case, so they get their own sub-list.
    /// </summary>
    private static string SourceSection(string logsRoot, TaskNode task)
    {
        // Relative from the page that links them: logs/<runId>/<taskId>/index.html lives in this dir.
        string fromDir = Path.Combine(logsRoot, task.Id);

        var sb = new StringBuilder();
        sb.Append("<h2>Source</h2>");

        SourceFile action = ActionSource(task);
        sb.Append("<div class=\"bar\">action: ").Append(SourceLink(fromDir, action)).Append("</div>");

        IReadOnlyList<SourceFile> guardrails = GuardrailSources(task);
        if (guardrails.Count > 0)
        {
            sb.Append("<div class=\"bar\">guardrails: ");
            sb.Append(string.Join(" · ", guardrails.Select(g => SourceLink(fromDir, g))));
            sb.Append("</div>");
        }

        return sb.ToString();
    }

    private static string SourceLink(string fromDir, SourceFile file)
    {
        string rel = Path.GetRelativePath(fromDir, file.Path).Replace('\\', '/');
        bool empty = IsZeroByte(file.Path);
        string label = empty ? $"{Enc(file.Label)} (empty)" : Enc(file.Label);
        string cls = empty ? " class=\"empty\"" : string.Empty;
        return $"<a{cls} href=\"{Href(rel)}\">{label}</a>";
    }

    /// <summary>
    /// A forward-slash relative path as an href: each SEGMENT is escaped so spaces / unusual characters
    /// survive as a <c>file://</c> URL, while the slashes stay literal so the path remains navigable.
    /// </summary>
    private static string Href(string relativePath) =>
        string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));

    // --- source discovery (shared with the live LogServer) ----------------------------------

    /// <summary>A source file the viewers surface: its absolute path plus the label shown to the user.</summary>
    public readonly record struct SourceFile(string Name, string Label, string Path);

    /// <summary>The task's action file (absolute path + label), from <see cref="ActionDefinition.Path"/>.</summary>
    public static SourceFile ActionSource(TaskNode task)
    {
        string name = System.IO.Path.GetFileName(task.Action.Path);
        return new SourceFile(name, name, task.Action.Path);
    }

    /// <summary>
    /// The task's guardrail source files (absolute path + label), in filename sort order. Each guardrail's
    /// script is included; an optional <c>&lt;name&gt;.json</c> metadata sidecar (SSOT §4.1) is included
    /// too when present, so the user sees the full guardrail definition. Ordinal-sorted by label.
    /// </summary>
    public static IReadOnlyList<SourceFile> GuardrailSources(TaskNode task)
    {
        var files = new List<SourceFile>();
        foreach (GuardrailDefinition guardrail in task.Guardrails)
        {
            string scriptName = System.IO.Path.GetFileName(guardrail.Path);
            files.Add(new SourceFile(scriptName, scriptName, guardrail.Path));

            // The optional metadata sidecar (SSOT §4.1) lives beside the script as <name>.json.
            string sidecar = System.IO.Path.ChangeExtension(guardrail.Path, ".json");
            if (File.Exists(sidecar))
            {
                string sidecarName = System.IO.Path.GetFileName(sidecar);
                files.Add(new SourceFile(sidecarName, sidecarName, sidecar));
            }
        }

        return files.OrderBy(f => f.Label, StringComparer.Ordinal).ToList();
    }

    // --- helpers ----------------------------------------------------------------------------

    private static string StatusWord(JournalDocument journal, string taskId) =>
        journal.Tasks.TryGetValue(taskId, out TaskJournalEntry? entry) ? StatusText(entry.Status) : "unknown";

    /// <summary>
    /// The agent's <c>needsHuman.kind</c> claim for a task, read from its LAST journaled attempt (issue
    /// #485) — the only place the static export can find it, since it reads the journal alone. Null (⇒ no
    /// chip, no attribute) for a task with no attempts, a non-escalating attempt, and every pre-#485 journal.
    /// </summary>
    private static string? ClaimWord(JournalDocument journal, string taskId) =>
        journal.Tasks.TryGetValue(taskId, out TaskJournalEntry? entry) && entry.Attempts.Count > 0
            ? NeedsHumanKinds.Parse(entry.Attempts[^1].NeedsHumanKind)
            : null;

    /// <summary>
    /// The run-level index's Model cell text (issue #524, design 29 §4.8): the LAST journaled attempt's
    /// best-known-actual model, disclosing a route mismatch through the SAME shared wording
    /// <see cref="LiveRunObserver.AttemptModelSummary"/> renders for the console and live surfaces — one
    /// fact, one vocabulary, never a second disclosure sentence invented here. Null for a task with no
    /// attempts (never run) or no recorded provenance/model (e.g. a script attempt), which the caller
    /// renders as the placeholder <c>—</c> rather than silently repeating a neighbouring row's value.
    /// </summary>
    private static string? ModelCellText(JournalDocument journal, string taskId)
    {
        if (!journal.Tasks.TryGetValue(taskId, out TaskJournalEntry? entry) || entry.Attempts.Count == 0)
        {
            return null;
        }

        AttemptRecord last = entry.Attempts[^1];
        return last.Provenance?.Model is { } model
            ? LiveRunObserver.AttemptModelSummary(model, last.Provenance.RequestedModel)
            : null;
    }

    /// <summary>
    /// The status-cell suffix naming the agent's needs-human claim (issue #485): a leading space plus
    /// <c>&lt;span class="claim"&gt;work|guardrail&lt;/span&gt;</c>, or the EMPTY string when unclassified —
    /// so an unclassified cell is byte-for-byte what it has always been. Uses the width-scarce terse form
    /// (the machine-readable full token rides on <see cref="ClaimAttribute"/>'s <c>data-claim</c>).
    /// <para>Pure and public: the Cli assembly ships no <c>InternalsVisibleTo</c>, so the mapping itself is
    /// the test seam.</para>
    /// </summary>
    public static string ClaimChip(string? kind) =>
        NeedsHumanKinds.Terse(kind) is { } terse ? $" <span class=\"claim\">{Enc(terse)}</span>" : string.Empty;

    /// <summary>
    /// The status cell's machine-readable <c> data-claim="&lt;kind&gt;"</c> attribute (issue #485), or the
    /// EMPTY string when unclassified. <c>data-status</c> is deliberately left alone so the existing
    /// needs-human red rule still applies — nothing in this design is colour-only.
    /// </summary>
    private static string ClaimAttribute(string? kind) =>
        NeedsHumanKinds.Parse(kind) is { } parsed ? $" data-claim=\"{Enc(parsed)}\"" : string.Empty;

    /// <summary>Map a journal status to the SSOT status word shown in the UI (shared with the live viewer).</summary>
    public static string StatusText(JournalTaskStatus status) => status switch
    {
        JournalTaskStatus.Pending => "pending",
        JournalTaskStatus.Running => "running",
        JournalTaskStatus.Succeeded => "succeeded",
        JournalTaskStatus.NeedsHuman => "needs-human",
        JournalTaskStatus.Blocked => "blocked",
        JournalTaskStatus.Failed => "failed",
        _ => status.ToString()
    };

    private static IReadOnlyList<string> AttemptDirs(string logsRoot, string taskId)
    {
        string taskDir = Path.Combine(logsRoot, taskId);
        if (!Directory.Exists(taskDir))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateDirectories(taskDir)
            .Where(d => Path.GetFileName(d).StartsWith("attempt-", StringComparison.Ordinal))
            .ToList();
    }

    private static IReadOnlyList<int> AttemptNumbers(string logsRoot, string taskId)
    {
        var numbers = new List<int>();
        foreach (string dir in AttemptDirs(logsRoot, taskId))
        {
            string leaf = Path.GetFileName(dir);
            if (int.TryParse(leaf.AsSpan("attempt-".Length), out int n))
            {
                numbers.Add(n);
            }
        }

        numbers.Sort();
        return numbers;
    }

    private static IReadOnlyList<string> AttemptFiles(string attemptDir) =>
        Directory.Exists(attemptDir)
            ? Directory.EnumerateFiles(attemptDir)
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList()
            : Array.Empty<string>();

    /// <summary>True when the file exists and is zero bytes (an empty stdout/stderr capture, #141 item 4).</summary>
    private static bool IsZeroByte(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == 0;
        }
        catch (IOException)
        {
            return false; // never let a stat failure abort the render
        }
    }

    /// <summary>Read a file with a fully shared handle (a producer may still be writing); empty if absent.</summary>
    private static string ReadOrEmpty(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty; // never let an unreadable artifact abort the whole export
        }
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
