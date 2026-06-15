using System.CommandLine;
using System.Diagnostics;
using Guardrails.Cli.Ui;
using Guardrails.Core.Journal;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails logs [folder] [--port n] [--task id] [--no-open]</c> — serve the same web log
/// viewer as a live <c>run</c>, but against the PERSISTED on-disk logs, decoupled from any active
/// run. The post-mortem companion to the run-time server (issue #23): review an overnight run, or
/// evaluate whether a passing task's guardrails were strong enough — from the same attempt logs.
/// Reads per-task status from the run journal so the landing page shows pass/needs-human/fail.
/// Bound to localhost; runs until Ctrl-C. Defaults to the current directory when omitted.
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

        var command = new Command("logs", "Serve the web log viewer over a plan's persisted logs (post-mortem; runs until Ctrl-C).");
        command.Add(folderArgument);
        command.Add(portOption);
        command.Add(taskOption);
        command.Add(noOpenOption);

        command.SetAction((parseResult, cancellationToken) => RunAsync(
            FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out),
            parseResult.GetValue(portOption),
            parseResult.GetValue(taskOption),
            parseResult.GetValue(noOpenOption),
            io,
            cancellationToken));

        return command;
    }

    private static async Task<int> RunAsync(
        string folder, int port, string? task, bool noOpen, IConsoleIo io, CancellationToken cancellationToken)
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
        Func<string, string?> statusForTask = id =>
            document.Tasks.TryGetValue(id, out TaskJournalEntry? entry) ? StatusText(entry.Status) : "unknown";

        LogServer? server = LogServer.TryStart(probe.Plan.PlanDirectory, probe.Plan.Tasks, port, output, statusForTask);
        if (server is null)
        {
            return ExitCodes.HarnessError; // TryStart already explained why
        }

        await using (server.ConfigureAwait(false))
        {
            string openUrl = server.BaseUrl;
            if (!string.IsNullOrWhiteSpace(task))
            {
                if (server.UrlForTask(task) is { } taskUrl)
                {
                    openUrl = taskUrl;
                }
                else
                {
                    output.WriteLine($"Unknown task '{task}' — opening the task list instead.");
                }
            }

            output.WriteLine($"Serving logs at {server.BaseUrl} — press Ctrl-C to stop.");

            if (!noOpen)
            {
                OpenBrowser(openUrl);
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

    private static string StatusText(JournalTaskStatus status) => status switch
    {
        JournalTaskStatus.Pending => "pending",
        JournalTaskStatus.Running => "running",
        JournalTaskStatus.Succeeded => "succeeded",
        JournalTaskStatus.NeedsHuman => "needs-human",
        JournalTaskStatus.Blocked => "blocked",
        JournalTaskStatus.Failed => "failed",
        _ => status.ToString()
    };
}
