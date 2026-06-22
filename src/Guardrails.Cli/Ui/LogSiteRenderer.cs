using System.Net;
using System.Text;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Cli.Ui;

/// <summary>
/// The SINGLE renderer for the log viewer's HTML — both the LIVE <see cref="LogServer"/> (dynamic
/// pages whose content is polled client-side) and the DURABLE static export
/// (<c>guardrails logs --export</c>, SSOT §12.3) consume it, so there is no forked look-alike
/// (#103 Request 2). The two modes share the page SHELL (CSS + layout + the same status colours);
/// they differ ONLY in the data source: the live page fetches <c>/file?name=…</c> and tails it; the
/// static page INLINES the on-disk attempt artifacts and has no polling JS (a <c>file://</c> page
/// cannot fetch its sibling logs — browsers block that).
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
  pre { background: #06090d; border: 1px solid #1c2733; border-radius: 6px; padding: 1rem;
        white-space: pre-wrap; word-break: break-word; font-size: .82rem; line-height: 1.35;
        max-height: 70vh; overflow: auto; }
""";

    // Per-attempt file the static page inlines FIRST (mirrors LogServer's PreferenceOrder): the groomed
    // transcript leads, then the raw stream, then script stdout.
    private static readonly string[] PreferenceOrder =
        ["transcript.md", "claude-stream.jsonl", "action-stdout.log"];

    /// <summary>
    /// Export the whole static site for one run into <paramref name="logsRoot"/>
    /// (<c>&lt;planDir&gt;/logs/&lt;runId&gt;/</c>): a per-task <c>index.html</c> for every task that
    /// has attempts on disk, plus a site <c>index.html</c>. Idempotent — regenerates everything each
    /// call (like <c>guardrails graph</c>). Returns the path to the site index. A task with no attempts
    /// on disk is listed (as plain text, not a link) in the index but writes no page.
    /// </summary>
    public static string ExportSite(string logsRoot, IReadOnlyList<TaskNode> tasks, JournalDocument journal)
    {
        Directory.CreateDirectory(logsRoot);

        foreach (TaskNode task in tasks)
        {
            if (AttemptDirs(logsRoot, task.Id).Count == 0)
            {
                continue; // nothing to render — task never ran / not started; it stays a non-link in the index
            }

            string page = TaskPage(logsRoot, task);
            File.WriteAllText(Path.Combine(logsRoot, task.Id, "index.html"), page, Utf8NoBom);
        }

        string indexPath = Path.Combine(logsRoot, "index.html");
        File.WriteAllText(indexPath, SiteIndex(logsRoot, tasks, journal), Utf8NoBom);
        return indexPath;
    }

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // --- site index (projection of the journal) ---------------------------------------------

    /// <summary>
    /// The site landing page: every task with its journal status. A task that has attempts on disk is a
    /// LINK to its static page; a task with no attempts (pending / not started) is PLAIN TEXT — exactly
    /// the linkability rule from #103 (only settled-with-attempts tasks are navigable offline). Driven
    /// by the journal status word, regenerated on every export (never appended).
    /// </summary>
    private static string SiteIndex(string logsRoot, IReadOnlyList<TaskNode> tasks, JournalDocument journal)
    {
        var rows = new StringBuilder();
        foreach (TaskNode task in tasks)
        {
            string status = StatusWord(journal, task.Id);
            bool hasPage = AttemptDirs(logsRoot, task.Id).Count > 0;

            string cell = hasPage
                ? $"<a href=\"{Uri.EscapeDataString(task.Id)}/index.html\">{Enc(task.Id)}</a>"
                : Enc(task.Id); // not-started / no attempts → plain text, not a link

            rows.Append("<tr><td>").Append(cell).Append("</td>")
                .Append("<td class=\"status\" data-status=\"").Append(Enc(status)).Append("\">")
                .Append(Enc(status)).Append("</td>")
                .Append("<td>").Append(Enc(task.Description)).Append("</td></tr>");
        }

        return $"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Guardrails run {Enc(journal.RunId)} — log site</title>
<style>
{SharedStyle}
</style>
</head>
<body>
<h1>Guardrails run — task logs</h1>
<p>Static export of run <code>{Enc(journal.RunId)}</code>. Settled tasks link to their inlined log page; not-yet-run tasks are plain text.</p>
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

    // --- per-task page (inlined attempts) ---------------------------------------------------

    /// <summary>
    /// One task's static page: every attempt on disk, each inlining its PREFERRED file's content (the
    /// transcript, else the raw stream, else action stdout) plus a list of the attempt's other files as
    /// relative <c>file://</c> links the browser can reach. No polling — the content is baked in. A
    /// missing/empty file renders as "no output captured" (a static snapshot of an in-flight run is a
    /// valid use, so the page never errors on an absent artifact).
    /// </summary>
    private static string TaskPage(string logsRoot, TaskNode task)
    {
        IReadOnlyList<int> attempts = AttemptNumbers(logsRoot, task.Id);
        var sections = new StringBuilder();

        foreach (int n in attempts)
        {
            string attemptDir = Path.Combine(logsRoot, task.Id, $"attempt-{n}");
            IReadOnlyList<string> files = AttemptFiles(attemptDir);
            string? preferred = PreferenceOrder.FirstOrDefault(files.Contains) ?? files.FirstOrDefault();

            sections.Append("<h2>attempt ").Append(n).Append("</h2>");

            // Inline the preferred file's content; link out to the rest (raw stream can be large — a
            // relative file:// link reaches it without inlining multi-MB into the HTML, #103 decision 5).
            string body = preferred is not null ? ReadOrEmpty(Path.Combine(attemptDir, preferred)) : string.Empty;
            sections.Append("<div class=\"bar\">showing <code>").Append(Enc(preferred ?? "(no files)")).Append("</code></div>");
            sections.Append("<pre>").Append(Enc(body.Length > 0 ? body : "no output captured")).Append("</pre>");

            if (files.Count > 0)
            {
                sections.Append("<div class=\"bar\">files: ");
                sections.Append(string.Join(" · ", files.Select(f =>
                    $"<a href=\"attempt-{n}/{Uri.EscapeDataString(f)}\">{Enc(f)}</a>")));
                sections.Append("</div>");
            }
        }

        if (attempts.Count == 0)
        {
            sections.Append("<pre>no attempts captured</pre>");
        }

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
</body>
</html>
""";
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
