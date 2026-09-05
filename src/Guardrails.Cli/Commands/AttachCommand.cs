using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails attach [folder]</c> — plan 34 §5 (issue #560): a second terminal watches a run without
/// touching it. Tails the run's <c>logs/&lt;runId&gt;/observer.jsonl</c> (task 08's <see cref="ObserverProjection"/>)
/// and replays the exact recorded call sequence into a REAL <see cref="LiveRunObserver"/> constructed here, in
/// THIS process — never a second, hand-rolled renderer that could drift from the shipped one.
///
/// <para>Deliberately NOT a server: no port, no lifetime to manage. It just reads a file — opened with
/// <see cref="FileShare.ReadWrite"/> throughout, so any number of watchers attach concurrently with no
/// contention, and none of them ever write a byte back to the run's own files. Whether the run is still
/// going or already finished is read off the run's own journal (never re-derived or guessed): once every
/// task has settled to a terminal status, attach replays whatever is left in the file and returns — it does
/// not wait forever for lines that will never arrive, the way <c>tail -f</c> would.</para>
///
/// <para>The live table's interactivity is decided HERE, by <see cref="LiveRunObserver"/> probing THIS
/// process's own console — never inherited from however the original run was launched (it may have run
/// <c>--no-ui</c>, or on a different machine entirely). The TTY requirement belongs to the attaching
/// client, never to the run.</para>
/// </summary>
public static class AttachCommand
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public static Command Create(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        var command = new Command(
            "attach",
            "Attach a second terminal to a run's live progress table, replaying its recorded events (read-only; never touches the run). Works while the run is in flight or after it has finished.");
        command.Add(folderArgument);

        command.SetAction((parseResult, cancellationToken) => RunAsync(
            FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out), io, cancellationToken));

        return command;
    }

    private static async Task<int> RunAsync(string folder, IConsoleIo io, CancellationToken cancellationToken)
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
            output.WriteLine(
                "No run journal yet — this plan has not been run. Use 'guardrails run' first, then attach from another terminal.");
            return ExitCodes.HarnessError;
        }

        // Read-only: JournalReader.Read never persists, unlike RunJournal.LoadOrCreate (which applies
        // resume normalization and writes run.json on every load). Attach must never write to the run
        // it is watching.
        JournalDocument document = JournalReader.Read(journalPath);
        string observerPath = Path.Combine(probe.Plan.PlanDirectory, "logs", document.RunId, "observer.jsonl");

        if (!TryOpenForRead(observerPath, out Exception? openError))
        {
            if (openError is FileNotFoundException or DirectoryNotFoundException)
            {
                output.WriteLine(
                    $"Can't attach: no observer.jsonl found for run {document.RunId} (expected at {observerPath}). " +
                    "The run may not have started yet, or ran before this feature existed — nothing to replay.");
            }
            else
            {
                output.WriteLine(
                    $"Can't attach: {observerPath} exists but could not be read (locked, or a permissions issue). Nothing was replayed.");
            }

            return ExitCodes.HarnessError;
        }

        IReadOnlyDictionary<string, TaskNode> taskById =
            probe.Plan.Tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);

        await using var renderer = new LiveRunObserver(
            probe.Plan.Tasks, planDirectory: probe.Plan.PlanDirectory, runId: document.RunId, waves: probe.Plan.Waves);

        try
        {
            int replayed = 0;
            while (true)
            {
                replayed = ReplayNewLines(observerPath, replayed, renderer, taskById);

                if (RunHasEnded(journalPath))
                {
                    // One more pass: a final line may have landed on disk between the read above and the
                    // journal settling to a terminal state.
                    ReplayNewLines(observerPath, replayed, renderer, taskById);
                    break;
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            output.WriteLine("Detached (Ctrl-C) — the run itself is unaffected.");
            return ExitCodes.Cancelled;
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// The run is over when every task the journal knows about has settled to a TERMINAL status — the same
    /// read-only journal every watcher shares, never a second "is it alive" signal that could disagree with
    /// it. Tolerates a journal caught mid-write (the atomic temp-then-rename leaves a brief window where the
    /// path can momentarily fail to open) by reporting "not yet known to have ended" and trying again on the
    /// next poll, rather than throwing out of the tail loop.
    /// </summary>
    private static bool RunHasEnded(string journalPath)
    {
        try
        {
            JournalDocument document = JournalReader.Read(journalPath);
            return document.Tasks.Values.All(entry => entry.Status is
                JournalTaskStatus.Succeeded or JournalTaskStatus.NeedsHuman or JournalTaskStatus.Blocked or JournalTaskStatus.Failed);
        }
        catch (IOException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Replay every line beyond <paramref name="alreadyReplayed"/> into <paramref name="renderer"/>, in file
    /// order, and return the new count replayed. A line that fails to PARSE as JSON is treated as a torn
    /// write caught mid-append (the writer flushes per call, so this is transient) — replay stops there and
    /// the same line is retried on the next poll rather than being skipped or losing everything after it. A
    /// line that parses but names an unrecognised member, or is missing a field this replay expects, is
    /// skipped on its own (logged nowhere — a best-effort forward-compatible read) so one bad event cannot
    /// stall every event after it.
    /// </summary>
    private static int ReplayNewLines(
        string observerPath, int alreadyReplayed, IRunObserver renderer, IReadOnlyDictionary<string, TaskNode> taskById)
    {
        IReadOnlyList<string> lines;
        try
        {
            lines = ReadLines(observerPath);
        }
        catch (IOException)
        {
            return alreadyReplayed; // both sides open with FileShare.ReadWrite; treat as transient.
        }

        for (int i = alreadyReplayed; i < lines.Count; i++)
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(lines[i]);
            }
            catch (JsonException)
            {
                return i;
            }

            if (node is null)
            {
                continue;
            }

            // #637: date the replayed call from WHEN IT HAPPENED, not from when this terminal got to it.
            // Without this the live observer stamps UtcNow, so attaching to a run whose task started twenty
            // minutes ago shows that task's clock starting from zero — and every re-attach resets it again.
            // Null (a pre-#637 log, or an unparseable value) leaves the observer on its own wall clock,
            // which is exactly the behaviour those files already had.
            if (renderer is LiveRunObserver live)
            {
                live.ReplayOccurredAt = ObserverProjection.OccurredAt(node);
            }

            try
            {
                Dispatch(node, renderer, taskById);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                // Unrecognised/malformed single event — skip it, keep replaying the rest of the file.
            }
        }

        return lines.Count;
    }

    /// <summary>Open, read, and close — never holding a handle across polls, so a writer never contends.</summary>
    private static IReadOnlyList<string> ReadLines(string observerPath)
    {
        using var stream = new FileStream(observerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static bool TryOpenForRead(string path, out Exception? error)
    {
        error = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex;
            return false;
        }
    }

    /// <summary>
    /// One observed <see cref="IRunObserver"/> call, decoded off its projected JSON line (task 07's schema:
    /// a <c>member</c> field naming the call, plus its arguments as named camelCase fields — proven by the
    /// pinned <c>TaskFinished</c>/<c>AttemptFinished</c> shapes to FLATTEN a rich argument's own fields
    /// directly into the line, e.g. a <see cref="TaskResult"/> argument becomes <c>taskId</c>/<c>outcome</c>/
    /// <c>summary</c> keys rather than a nested <c>result</c> object) and dispatched onto the real renderer.
    /// A member this replay does not (yet) know how to decode falls through as a no-op — forward-compatible
    /// with event types this task never had to invent a wire shape for.
    /// </summary>
    private static void Dispatch(JsonNode node, IRunObserver renderer, IReadOnlyDictionary<string, TaskNode> taskById)
    {
        string member = RequireString(node, "member");

        switch (member)
        {
            case "TaskStarting":
                renderer.TaskStarting(TaskFor(node, taskById));
                break;

            case "AttemptStarting":
                renderer.AttemptStarting(TaskFor(node, taskById), RequireInt(node, "attempt"), RequireInt(node, "budget"));
                break;

            case "AttemptModelResolved":
                renderer.AttemptModelResolved(
                    TaskFor(node, taskById), RequireInt(node, "attempt"), RequireString(node, "model"),
                    OptionalString(node, "requestedModel"));
                break;

            case "AttemptRouteResolved":
                renderer.AttemptRouteResolved(
                    TaskFor(node, taskById), RequireInt(node, "attempt"), RequireString(node, "runner"),
                    RequireString(node, "model"), OptionalString(node, "tier"), OptionalString(node, "requestedTier"));
                break;

            case "AttemptFinished":
            {
                // The five REQUIRED AttemptRecord members are REQUIRED off the wire too — a line missing
                // any one of them throws FormatException here, which ReplayNewLines catches and skips
                // (forward-compatible with a malformed or torn line) rather than rendering a record this
                // replay had to invent sentinel values for.
                string? model = OptionalString(node, "model");
                string? runner = OptionalString(node, "runner");
                string? tier = OptionalString(node, "tier");
                string? tierSourceToken = OptionalString(node, "tierSource");
                AttemptProvenance? provenance = model is null && runner is null && tier is null && tierSourceToken is null
                    ? null
                    : new AttemptProvenance
                    {
                        Model = model,
                        Runner = runner,
                        Tier = tier,
                        TierSource = tierSourceToken is null ? null : Enum.Parse<TierSource>(tierSourceToken)
                    };

                renderer.AttemptFinished(
                    TaskFor(node, taskById),
                    new AttemptRecord
                    {
                        Attempt = RequireInt(node, "attempt"),
                        StartedAt = RequireDateTimeOffset(node, "startedAt"),
                        EndedAt = RequireDateTimeOffset(node, "endedAt"),
                        Outcome = Enum.Parse<AttemptOutcome>(RequireString(node, "outcome")),
                        LogDir = RequireString(node, "logDir"),
                        CostUsd = node["costUsd"]?.GetValue<decimal>(),
                        Turns = node["turns"]?.GetValue<int>(),
                        NeedsHumanKind = OptionalString(node, "needsHumanKind"),
                        Provenance = provenance
                    });
                break;
            }

            case "TaskFinished":
                renderer.TaskFinished(new TaskResult
                {
                    TaskId = RequireString(node, "taskId"),
                    Outcome = Enum.Parse<TaskOutcome>(RequireString(node, "outcome")),
                    Summary = RequireString(node, "summary"),
                    NeedsHumanKind = OptionalString(node, "needsHumanKind")
                });
                break;

            case "GuardrailFinished":
                renderer.GuardrailFinished(TaskFor(node, taskById), new GuardrailResult
                {
                    Name = RequireString(node, "name"),
                    Passed = RequireBool(node, "passed"),
                    Reason = OptionalString(node, "reason")
                });
                break;

            case "PlanHashMismatch":
                renderer.PlanHashMismatch(RequireString(node, "previousPlanHash"));
                break;

            case "ParallelismClampedNoProvider":
                renderer.ParallelismClampedNoProvider(RequireInt(node, "requested"));
                break;

            case "VerifierAdvisoryFound":
                renderer.VerifierAdvisoryFound(RequireString(node, "taskId"), RequireString(node, "finding"));
                break;

            case "OverwatchNoVerdict":
                renderer.OverwatchNoVerdict(RequireString(node, "taskId"), RequireString(node, "reason"));
                break;

            default:
                // An event type this replay has no wire-shape decision for yet (a wave/cleanup/decision
                // event, or a future addition) — skip it rather than fail the whole replay over it. This
                // DELIBERATELY includes "RunFinished": LiveRunObserver renders nothing from it, and an
                // attaching client built against an older harness must not crash the moment a newer
                // stream starts appending a run-scoped member it has never seen. Do not "fix" this by
                // adding a case for it.
                break;
        }
    }

    private static TaskNode TaskFor(JsonNode node, IReadOnlyDictionary<string, TaskNode> taskById)
    {
        string taskId = RequireString(node, "taskId");
        return taskById.TryGetValue(taskId, out TaskNode? task)
            ? task
            : throw new FormatException($"observer.jsonl references unknown task '{taskId}'.");
    }

    private static string RequireString(JsonNode node, string field) =>
        node[field]?.GetValue<string>() ?? throw new FormatException($"observer.jsonl line is missing '{field}'.");

    private static string? OptionalString(JsonNode node, string field) => node[field]?.GetValue<string>();

    private static int RequireInt(JsonNode node, string field) =>
        node[field]?.GetValue<int>() ?? throw new FormatException($"observer.jsonl line is missing '{field}'.");

    private static DateTimeOffset RequireDateTimeOffset(JsonNode node, string field) =>
        node[field]?.GetValue<DateTimeOffset>() ?? throw new FormatException($"observer.jsonl line is missing '{field}'.");

    private static bool RequireBool(JsonNode node, string field) =>
        node[field]?.GetValue<bool>() ?? throw new FormatException($"observer.jsonl line is missing '{field}'.");
}
