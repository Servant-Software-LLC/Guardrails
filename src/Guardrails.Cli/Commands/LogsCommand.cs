using System.CommandLine;
using System.Diagnostics;
using Guardrails.Cli.Ui;
using Guardrails.Core.Journal;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails logs [folder] [--port n] [--task id] [--no-open]</c> — review a plan's PERSISTED
/// on-disk logs, decoupled from any active run. The post-mortem companion to the run-time viewer
/// (issue #23): review an overnight run, or evaluate whether a passing task's guardrails were strong
/// enough — from the same attempt logs. The canonical "all tasks" entry point is the static index FILE
/// (<c>logs/&lt;runId&gt;/index.html</c>), regenerated from the journal here and advertised by its
/// <c>file://</c> path (issue #143); a live tailing server is also started so a running task's live
/// page works (a completed run simply leaves it unused). Bound to localhost; runs until Ctrl-C.
/// Defaults to the current directory when omitted.
/// </summary>
public static class LogsCommand
{
    public static Command Create(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        var portOption = new Option<int>("--port")
        {
            Description = "Port for the log viewer (default 0 = an automatically chosen free port). Bound to localhost only."
        };

        var taskOption = new Option<string?>("--task")
        {
            Description = "Open straight to this task's log page instead of the task list."
        };

        var noOpenOption = new Option<bool>("--no-open")
        {
            Description = "Do not open a browser automatically; just print the URL."
        };

        var exportOption = new Option<bool>("--export")
        {
            Description = "Render a durable static HTML log site under logs/<runId>/ and exit (no server). " +
                          "Viewable after the run; --port/--task are ignored."
        };

        var command = new Command("logs", "Serve the web log viewer over a plan's persisted logs (post-mortem; runs until Ctrl-C).");
        command.Add(folderArgument);
        command.Add(portOption);
        command.Add(taskOption);
        command.Add(noOpenOption);
        command.Add(exportOption);

        command.SetAction((parseResult, cancellationToken) => RunAsync(
            FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out),
            parseResult.GetValue(portOption),
            parseResult.GetValue(taskOption),
            parseResult.GetValue(noOpenOption),
            parseResult.GetValue(exportOption),
            io,
            cancellationToken));

        return command;
    }

    private static async Task<int> RunAsync(
        string folder, int port, string? task, bool noOpen, bool export, IConsoleIo io, CancellationToken cancellationToken)
    {
        TextWriter output = io.Out;

        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, output);
            output.WriteLine("\nCould not load the plan.");
            return ExitCodes.HarnessError;
        }

        string journalPath = RunJournal.PathFor(probe.Plan.PlanDirectory);
        if (!File.Exists(journalPath))
        {
            output.WriteLine("No run journal yet — this plan has not been run. Use 'guardrails run' first.");
            return ExitCodes.Success;
        }

        // Read-only snapshot of the journal — the landing page shows each task's status as it
        // stands on disk (works after a run, or mid-run from another terminal).
        JournalDocument document = JournalReader.Read(journalPath);

        // --export: render the durable static site (SSOT §12.3) and exit — no server, no blocking.
        // The site lands under logs/<runId>/ (next to the attempt artifacts it renders), so it is
        // viewable via file:// after the LogServer process is gone, and --fresh cleans it for free.
        if (export)
        {
            string logsRoot = Path.Combine(probe.Plan.PlanDirectory, "logs", document.RunId);
            string indexPath = LogSiteRenderer.ExportSite(logsRoot, probe.Plan.Tasks, document);
            bool linkable = !Console.IsOutputRedirected && Spectre.Console.AnsiConsole.Profile.Capabilities.Links;
            output.WriteLine($"Static log site written: {RunCommand.Hyperlink(Path.GetFullPath(indexPath), linkable)}");
            return ExitCodes.Success;
        }

        // The canonical "all tasks" page is the static index FILE (issue #143) — durable and
        // server-independent. (Re)generate the static site for this run so the entry point reflects
        // the journal as it stands now, then advertise its file:// path. The live server below is the
        // active-task tailing backend the static index links a running task to.
        string siteRoot = Path.Combine(probe.Plan.PlanDirectory, "logs", document.RunId);
        string staticIndexPath = Path.GetFullPath(LogSiteRenderer.ExportSite(siteRoot, probe.Plan.Tasks, document));

        LogServer? server = LogServer.TryStart(
            probe.Plan.PlanDirectory, document.RunId, probe.Plan.Tasks, port, output);
        if (server is null)
        {
            return ExitCodes.HarnessError; // TryStart already explained why
        }

        await using (server.ConfigureAwait(false))
        {
            // --task is a convenience that opens straight to a running task's live tailing page; with
            // no --task the entry point is the canonical static index file (the all-tasks page).
            string? openTaskUrl = null;
            if (!string.IsNullOrWhiteSpace(task))
            {
                if (server.UrlForTask(task) is { } taskUrl)
                {
                    openTaskUrl = taskUrl;
                }
                else
                {
                    output.WriteLine($"Unknown task '{task}' — opening the static all-tasks index instead.");
                }
            }

            bool linkable = !Console.IsOutputRedirected && Spectre.Console.AnsiConsole.Profile.Capabilities.Links;
            output.WriteLine($"All tasks (static log site): {RunCommand.Hyperlink(staticIndexPath, linkable)}");
            output.WriteLine($"Live tailing server (active tasks): {server.BaseUrl} — press Ctrl-C to stop.");

            if (!noOpen)
            {
                // Open the running task's live page when one was named; otherwise the static index file.
                OpenBrowser(openTaskUrl ?? new Uri(staticIndexPath).AbsoluteUri);
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ctrl-C — fall through to dispose the server and exit cleanly.
            }

            output.WriteLine("Log viewer stopped.");
            return ExitCodes.Success;
        }
    }

    /// <summary>Best-effort: launch the OS default handler for the URL. Never fails the command.</summary>
    private static void OpenBrowser(string url)
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Headless / no registered browser — the URL was printed, so the user can open it.
        }
    }
}
