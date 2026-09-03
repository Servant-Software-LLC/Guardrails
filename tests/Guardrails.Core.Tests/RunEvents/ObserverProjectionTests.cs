using System.Text.Json.Nodes;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.RunEvents;

/// <summary>
/// The RENDER-FIDELITY projection off the <see cref="IRunObserver"/> seam (plan 34 §5) — the one
/// <c>guardrails attach</c> replays into a REAL <c>LiveRunObserver</c>, deliberately a different file from
/// <c>events.jsonl</c> (the semantic, low-frequency agent stream). This one is expected to carry every
/// observed call, verbatim — a filtered subset would starve the renderer of the live-only fields (elapsed
/// time, the guardrail currently executing) that make the attached table worth watching.
///
/// <para>These tests are written to FAIL right now: <see cref="ObserverProjection"/> exists (this task) but
/// every member throws <see cref="NotImplementedException"/> — the recording + forwarding logic lands in
/// task 08, over this exact stub, without touching this file.</para>
/// </summary>
public sealed class ObserverProjectionTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fixtures
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static TaskNode FlatTask(string folder) => new()
    {
        Id = folder,
        Directory = $"/fake/plan/tasks/{folder}",
        Description = $"fixture — {folder}",
        Action = new ActionDefinition { Path = "action.sh", Kind = ActionKind.Script },
        Guardrails = [new GuardrailDefinition { Name = "01-check", Path = "01-check.sh", Kind = ActionKind.Script }]
    };

    private static WaveNode FlatWave(TaskNode task) => new()
    {
        Dir = "wave-01-fixture",
        Number = 1,
        Slug = "fixture",
        Directory = "/fake/plan/wave-01-fixture",
        Tasks = [task]
    };

    /// <summary>A throwaway directory — the projection appends <c>observer.jsonl</c> directly under it.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gr-observer-projection-" + Guid.NewGuid().ToString("N"));

        public TempTree() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }

    /// <summary>
    /// The inner observer a decorator is supposed to be transparent to. Records the WHOLE call, not a count
    /// — a decorator that forwards a mangled argument list is exactly as broken as one that never forwards.
    /// </summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<string> Calls { get; } = [];

        public void TaskStarting(TaskNode task) => Calls.Add($"TaskStarting({task.Id})");

        public void AttemptStarting(TaskNode task, int attempt, int budget) =>
            Calls.Add($"AttemptStarting({task.Id},{attempt},{budget})");

        public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel) =>
            Calls.Add($"AttemptModelResolved({task.Id},{attempt},{model},{requestedModel})");

        public void AttemptRouteResolved(
            TaskNode task, int attempt, string runner, string model, string? tier, string? requestedTier) =>
            Calls.Add($"AttemptRouteResolved({task.Id},{attempt},{runner},{model},{tier},{requestedTier})");

        public void AttemptFinished(TaskNode task, int attempt, AttemptOutcome outcome) =>
            Calls.Add($"AttemptFinished({task.Id},{attempt},{outcome})");

        public void TaskFinished(TaskResult result) => Calls.Add($"TaskFinished({result.TaskId},{result.Outcome})");

        public void GuardrailFinished(TaskNode task, GuardrailResult result) =>
            Calls.Add($"GuardrailFinished({task.Id},{result.Name},{result.Passed})");

        public void PlanHashMismatch(string previousPlanHash) => Calls.Add($"PlanHashMismatch({previousPlanHash})");

        public void ParallelismClampedNoProvider(int requested) =>
            Calls.Add($"ParallelismClampedNoProvider({requested})");

        public void CleanupFailed(string owner, Exception error) =>
            Calls.Add($"CleanupFailed({owner},{error.Message})");

        public void PromptPaused(TaskNode task, string reason, TimeSpan backoff, int pauseCount) =>
            Calls.Add($"PromptPaused({task.Id},{reason},{backoff},{pauseCount})");

        public void DecisionRecorded(DecisionEntry entry) => Calls.Add($"DecisionRecorded({entry.Boundary},{entry.Subject})");

        public void VerifierAdvisoryFound(string taskId, string finding) =>
            Calls.Add($"VerifierAdvisoryFound({taskId},{finding})");

        public void OverwatchNoVerdict(string taskId, string reason) =>
            Calls.Add($"OverwatchNoVerdict({taskId},{reason})");

        public void WaveStarting(WaveNode wave, int index, int total) =>
            Calls.Add($"WaveStarting({wave.Dir},{index},{total})");

        public void WaveFinished(WaveNode wave, WaveStatus status, bool skipped) =>
            Calls.Add($"WaveFinished({wave.Dir},{status},{skipped})");
    }

    /// <summary>
    /// A representative sweep across every parameter SHAPE on <see cref="IRunObserver"/> — task/attempt
    /// primitives, a <see cref="TaskResult"/>, a <see cref="GuardrailResult"/>, a <see cref="DecisionEntry"/>,
    /// a <see cref="WaveNode"/>+<see cref="WaveStatus"/> — not literally all twenty members: several share a
    /// shape already exercised, and the wave-gate/breakdown-context members would mostly test fixture
    /// construction rather than the projection. Order matters: <see cref="Replay_ReproducesTheObservedCallSequence_InOrder"/>
    /// depends on it being reproduced EXACTLY.
    /// </summary>
    private static (string Member, Action<IRunObserver> Invoke)[] SampleCalls(TaskNode task, WaveNode wave) =>
    [
        ("TaskStarting", o => o.TaskStarting(task)),
        ("AttemptStarting", o => o.AttemptStarting(task, 1, 3)),
        ("AttemptModelResolved", o => o.AttemptModelResolved(task, 1, "claude-sonnet-5", requestedModel: null)),
        ("AttemptRouteResolved", o => o.AttemptRouteResolved(task, 1, "claude", "claude-sonnet-5", "standard", requestedTier: null)),
        ("AttemptFinished", o => o.AttemptFinished(task, 1, AttemptOutcome.MaxTurns)),
        ("GuardrailFinished", o => o.GuardrailFinished(task, new GuardrailResult { Name = "01-check", Passed = false, Reason = "boom" })),
        ("TaskFinished", o => o.TaskFinished(new TaskResult { TaskId = task.Id, Outcome = TaskOutcome.Succeeded, Summary = "ok" })),
        ("PlanHashMismatch", o => o.PlanHashMismatch("sha256:aaaaaaaa")),
        ("ParallelismClampedNoProvider", o => o.ParallelismClampedNoProvider(4)),
        ("CleanupFailed", o => o.CleanupFailed(task.Id, new InvalidOperationException("cleanup boom"))),
        ("PromptPaused", o => o.PromptPaused(task, "rate limited", TimeSpan.FromSeconds(30), 1)),
        ("DecisionRecorded", o => o.DecisionRecorded(new DecisionEntry
        {
            Boundary = "drift", Policy = "auto", Decision = "auto-applied", Subject = task.Id, Headline = "drift resolved"
        })),
        ("VerifierAdvisoryFound", o => o.VerifierAdvisoryFound(task.Id, "judge weaker than the work it grades")),
        ("OverwatchNoVerdict", o => o.OverwatchNoVerdict(task.Id, "diagnose runner errored")),
        ("WaveStarting", o => o.WaveStarting(wave, 1, 2)),
        ("WaveFinished", o => o.WaveFinished(wave, WaveStatus.Completed, skipped: false)),
    ];

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Tests
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public void EveryObservedCall_AppendsOneLine_NamingTheMember()
    {
        using var tree = new TempTree();
        TaskNode task = FlatTask("01-first");
        WaveNode wave = FlatWave(task);
        var projection = new ObserverProjection(IRunObserver.Null, tree.Root);
        (string Member, Action<IRunObserver> Invoke)[] calls = SampleCalls(task, wave);

        foreach ((string _, Action<IRunObserver> invoke) in calls)
        {
            invoke(projection);
        }

        string path = Path.Combine(tree.Root, "observer.jsonl");
        Assert.True(File.Exists(path), $"expected {path} to exist after {calls.Length} observed calls");
        string[] lines = File.ReadAllLines(path);
        Assert.Equal(calls.Length, lines.Length);
        for (int i = 0; i < calls.Length; i++)
        {
            JsonNode? line = JsonNode.Parse(lines[i]);
            Assert.Equal(calls[i].Member, line?["member"]?.GetValue<string>());
        }
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void Replay_ReproducesTheObservedCallSequence_InOrder()
    {
        using var tree = new TempTree();
        TaskNode taskA = FlatTask("01-first");
        TaskNode taskB = FlatTask("02-second");
        var projection = new ObserverProjection(IRunObserver.Null, tree.Root);

        projection.TaskStarting(taskA);
        projection.AttemptStarting(taskA, 1, 3);
        projection.AttemptFinished(taskA, 1, AttemptOutcome.Succeeded);
        projection.TaskStarting(taskB);
        projection.PlanHashMismatch("sha256:bbbbbbbb");

        string[] lines = File.ReadAllLines(Path.Combine(tree.Root, "observer.jsonl"));
        Assert.Equal(5, lines.Length);

        string[] expectedMembers = ["TaskStarting", "AttemptStarting", "AttemptFinished", "TaskStarting", "PlanHashMismatch"];
        for (int i = 0; i < expectedMembers.Length; i++)
        {
            JsonNode? line = JsonNode.Parse(lines[i]);
            Assert.Equal(expectedMembers[i], line?["member"]?.GetValue<string>());
        }

        // The two TaskStarting lines (index 0 and 3) each carry THEIR OWN task id, in order — not merely
        // "TaskStarting happened twice", but "happened to taskA, then to taskB".
        Assert.Equal(taskA.Id, JsonNode.Parse(lines[0])?["taskId"]?.GetValue<string>());
        Assert.Equal(taskB.Id, JsonNode.Parse(lines[3])?["taskId"]?.GetValue<string>());

        // AttemptFinished carries its own arguments verbatim — the replay-driven LiveRunObserver needs the
        // SAME attempt number and outcome the original call carried, not merely "some line landed" at index 2.
        JsonNode? attemptFinished = JsonNode.Parse(lines[2]);
        Assert.Equal(1, attemptFinished?["attempt"]?.GetValue<int>());
        Assert.Equal("Succeeded", attemptFinished?["outcome"]?.GetValue<string>());
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void AttemptFinished_IsProjected_WithItsOutcome()
    {
        using var tree = new TempTree();
        TaskNode task = FlatTask("02-second");
        var projection = new ObserverProjection(IRunObserver.Null, tree.Root);

        projection.AttemptFinished(task, 2, AttemptOutcome.GuardrailFailed);

        string line = Assert.Single(File.ReadAllLines(Path.Combine(tree.Root, "observer.jsonl")));
        JsonNode? json = JsonNode.Parse(line);
        Assert.Equal("AttemptFinished", json?["member"]?.GetValue<string>());
        Assert.Equal(task.Id, json?["taskId"]?.GetValue<string>());
        Assert.Equal(2, json?["attempt"]?.GetValue<int>());
        // Not merely "something arrived" — the EXACT outcome, so a projection that hard-codes or
        // mis-maps the value cannot pass by coincidence (the AttemptCompletionForwardingTests lesson).
        Assert.Equal(nameof(AttemptOutcome.GuardrailFailed), json?["outcome"]?.GetValue<string>());
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void Decorator_ForwardsEveryObservedCallToTheInner()
    {
        using var tree = new TempTree();
        TaskNode task = FlatTask("01-first");
        WaveNode wave = FlatWave(task);
        var inner = new RecordingObserver();
        var decorator = new ObserverProjection(inner, tree.Root);
        (string Member, Action<IRunObserver> Invoke)[] calls = SampleCalls(task, wave);

        foreach ((string _, Action<IRunObserver> invoke) in calls)
        {
            invoke(decorator);
        }

        Assert.Equal(calls.Length, inner.Calls.Count);
        for (int i = 0; i < calls.Length; i++)
        {
            Assert.StartsWith(calls[i].Member + "(", inner.Calls[i]);
        }
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task TwoConcurrentReaders_BothReadEveryLine()
    {
        using var tree = new TempTree();
        TaskNode task = FlatTask("01-first");
        var projection = new ObserverProjection(IRunObserver.Null, tree.Root);

        const int eventCount = 25;
        for (int i = 1; i <= eventCount; i++)
        {
            projection.AttemptStarting(task, i, eventCount);
        }

        string path = Path.Combine(tree.Root, "observer.jsonl");

        // Two independent readers, each opening their own handle concurrently — the #560 acceptance is TWO
        // attachments at once, neither perturbing the run. A writer holding an exclusive lock while appending
        // would make the second reader's open throw; this asserts BOTH succeed and BOTH see every line.
        static List<string> ReadAllLines(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lines.Add(line);
            }

            return lines;
        }

        Task<List<string>> readerA = Task.Run(() => ReadAllLines(path));
        Task<List<string>> readerB = Task.Run(() => ReadAllLines(path));
        List<string>[] results = await Task.WhenAll(readerA, readerB);

        Assert.Equal(eventCount, results[0].Count);
        Assert.Equal(eventCount, results[1].Count);
        Assert.Equal(results[0], results[1]);
    }
}
