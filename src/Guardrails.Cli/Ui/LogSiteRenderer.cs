using System.Net;
using System.Text;
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
///   <item>DURING-RUN index — a <c>&lt;meta http-equiv="refresh"&gt;</c> so a <c>file://</c> view
///     re-reads it as the harness rewrites it; a RUNNING task links to the LIVE server URL (a click
///     tails it), a settled/with-attempts task links to its static <c>&lt;taskId&gt;/index.html</c>,
///     a pending/no-attempts task is plain text.</item>
///   <item>FINAL / <c>--export</c> index — NO refresh, ALL links static (durable, non-flickering).</item>
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
  .empty, option.empty { color: #6b7a8d; }
  pre { background: #06090d; border: 1px solid #1c2733; border-radius: 6px; padding: 1rem;
        white-space: pre-wrap; word-break: break-word; font-size: .82rem; line-height: 1.35;
        max-height: 70vh; overflow: auto; }
""";

    // Per-attempt file the static page inlines FIRST (mirrors LogServer's PreferenceOrder): the groomed
    // transcript leads, then the raw stream, then script stdout.
    private static readonly string[] PreferenceOrder =
        ["transcript.md", "claude-stream.jsonl", "action-stdout.log"];

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
    /// Export the whole DURABLE static site for one run into <paramref name="logsRoot"/>
    /// (<c>&lt;planDir&gt;/logs/&lt;runId&gt;/</c>): a per-task <c>index.html</c> for every task that
    /// has attempts on disk, plus a site <c>index.html</c> with NO refresh and ALL-static links. Idempotent
    /// — regenerates everything each call (like <c>guardrails graph</c>). Returns the path to the site index.
    /// A task with no attempts on disk is listed (as plain text, not a link) in the index but writes no page.
    /// This is the post-hoc <c>logs --export</c> surface (SSOT §12.3) — preserved verbatim.
    /// </summary>
    public static string ExportSite(string logsRoot, IReadOnlyList<TaskNode> tasks, JournalDocument journal)
    {
        Directory.CreateDirectory(logsRoot);

        foreach (TaskNode task in tasks)
        {
            WriteTaskPageIfHasAttempts(logsRoot, task);
        }

        // Durable export: a settled-with-attempts task links to its static page, a not-yet-run task is
        // plain text. No live URLs, no meta-refresh (#103 / SSOT §12.3).
        string index = IndexHtml(
            journal.RunId,
            tasks,
            statusResolver: id => StatusWord(journal, id),
            linkResolver: id => AttemptDirs(logsRoot, id).Count > 0 ? IndexLink.Static : IndexLink.Plain,
            includeRefresh: false);

        string indexPath = Path.Combine(logsRoot, "index.html");
        AtomicFile.WriteAllText(indexPath, index);
        return indexPath;
    }

    /// <summary>
    /// Render and atomically write the during-run / final site index to
    /// <c>&lt;logsRoot&gt;/index.html</c> (issue #141 item 2). The caller supplies the status word and
    /// the per-task link target so the SAME renderer serves the during-run index (live URLs + refresh)
    /// and the settled final index (all static, no refresh). <paramref name="includeRefresh"/> is true
    /// while the run is in flight (so a <c>file://</c> browser re-reads it as it is rewritten) and false
    /// for the durable final write. Atomic temp+rename so a browser never reads a half-written file.
    /// Returns the path written.
    /// </summary>
    public static string WriteIndex(
        string logsRoot,
        string runId,
        IReadOnlyList<TaskNode> tasks,
        Func<string, string> statusResolver,
        Func<string, IndexLink> linkResolver,
        bool includeRefresh)
    {
        string index = IndexHtml(runId, tasks, statusResolver, linkResolver, includeRefresh);
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
    public static void WriteTaskPageIfHasAttempts(string logsRoot, TaskNode task)
    {
        if (AttemptDirs(logsRoot, task.Id).Count == 0)
        {
            return; // nothing to render — task never ran / not started; it stays a non-link in the index
        }

        string page = TaskPage(logsRoot, task);
        AtomicFile.WriteAllText(Path.Combine(logsRoot, task.Id, "index.html"), page);
    }

    // --- site index (projection of status + link target) ------------------------------------

    /// <summary>
    /// The site landing page: every task with its status word and a link target chosen by the caller's
    /// <paramref name="linkResolver"/> (static page / live URL / plain text). Regenerated on every write
    /// (never appended). When <paramref name="includeRefresh"/> is true a <c>meta refresh</c> makes a
    /// <c>file://</c> view re-read the file as the harness rewrites it ("updated on the fly"); the durable
    /// final / <c>--export</c> index omits it.
    /// </summary>
    private static string IndexHtml(
        string runId,
        IReadOnlyList<TaskNode> tasks,
        Func<string, string> statusResolver,
        Func<string, IndexLink> linkResolver,
        bool includeRefresh)
    {
        var rows = new StringBuilder();
        foreach (TaskNode task in tasks)
        {
            string status = statusResolver(task.Id);
            string cell = IndexCell(task.Id, linkResolver(task.Id));

            rows.Append("<tr><td>").Append(cell).Append("</td>")
                .Append("<td class=\"status\" data-status=\"").Append(Enc(status)).Append("\">")
                .Append(Enc(status)).Append("</td>")
                .Append("<td>").Append(Enc(task.Description)).Append("</td></tr>");
        }

        // The meta-refresh is the "no server needed" mechanism for the during-run file:// view (#141):
        // the browser re-reads index.html every 2s, so a click after a task settles finds the static page.
        string refresh = includeRefresh
            ? "\n<meta http-equiv=\"refresh\" content=\"2\">"
            : string.Empty;

        string note = includeRefresh
            ? "Live run — this page refreshes itself. Running tasks tail their log; settled tasks link to a static page; not-yet-run tasks are plain text."
            : "Static export of this run. Settled tasks link to their inlined log page; not-yet-run tasks are plain text.";

        return $"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">{refresh}
<title>Guardrails run {Enc(runId)} — log site</title>
<style>
{SharedStyle}
</style>
</head>
<body>
<h1>Guardrails run — task logs</h1>
<p>{Enc(note)}</p>
<table>
<thead><tr><th>Task</th><th>Status</th><th>Description</th></tr></thead>
<tbody>
{rows}
</tbody>
</table>
</body>
</html>
""";
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
    /// blocks (the PREFERRED file — transcript, else raw stream, else action stdout — shown first). When
    /// there is more than one attempt, an attempt <c>&lt;select id="attemptselect"&gt;</c> — mirroring the
    /// live viewer's attempt dropdown (<see cref="LogServer"/>) — lets the user pick which attempt's
    /// section is visible; every attempt's markup stays inlined in the one exported file (a <c>file://</c>
    /// page can't fetch siblings), the dropdown only shows/hides <c>&lt;section&gt;</c>s. Default selection
    /// is the LATEST attempt, matching the live viewer's default. A single-attempt task renders no
    /// attempt dropdown at all — its one section is simply always visible. The existing per-attempt file
    /// combobox is untouched: it stays nested inside its attempt's <c>&lt;section&gt;</c> and is shown/hidden
    /// as part of it. A Source section (action file + guardrail scripts from <c>&lt;task.Directory&gt;</c>,
    /// #141 item 3) and the "← all tasks" back-link follow, unchanged.
    ///
    /// <para>Trade-off (#145 Feature 2, extended by #206): inlining every attempt's every file bloats the
    /// page by the full size of each raw stream. Accepted for the audit/demo use — a <c>file://</c> page
    /// has no other way to show siblings — so it is deliberately uncapped.</para>
    /// </summary>
    private static string TaskPage(string logsRoot, TaskNode task)
    {
        IReadOnlyList<int> attempts = AttemptNumbers(logsRoot, task.Id);
        var sections = new StringBuilder();

        // The latest attempt (highest number) is the default-visible one, matching the live viewer's
        // "follow latest" default (LogServer's TaskTemplate: `asel.value = ... d.attempt`).
        int latest = attempts.Count > 0 ? attempts[^1] : 0;

        if (attempts.Count > 1)
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
            AppendAttemptFiles(sections, attemptDir, n, files, preferred);
            sections.Append("</section>");
        }

        if (attempts.Count == 0)
        {
            sections.Append("<pre>no attempts captured</pre>");
        }

        sections.Append(SourceSection(logsRoot, task));

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
<div class="bar"><a href="../index.html">&larr; all tasks</a> &middot; {Enc(task.Description)}</div>
{sections}
{FileToggleScript}
</body>
</html>
""";
    }

    /// <summary>
    /// Render the attempt-level <c>&lt;select id="attemptselect"&gt;</c> (issue #206): one option per
    /// attempt (oldest first, matching the on-page section order), the <paramref name="latest"/> attempt
    /// pre-selected — mirroring the live viewer's "default to latest" behaviour. Only emitted when a task
    /// has more than one attempt; a single-attempt task has nothing to pick between.
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
        // Encode each path segment so spaces / unusual chars survive as a file:// URL, but keep the
        // slashes literal so the relative path stays navigable.
        string href = string.Join('/', rel.Split('/').Select(Uri.EscapeDataString));
        return $"<a{cls} href=\"{href}\">{label}</a>";
    }

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
