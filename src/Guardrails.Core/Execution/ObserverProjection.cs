using System.Text;
using System.Text.Json.Nodes;
using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// The SECOND projection off the <see cref="IRunObserver"/> seam (plan 34 §5) — the render-FIDELITY
/// stream <c>guardrails attach</c> replays into a real <see cref="IRunObserver"/> (the live table). It is
/// deliberately NOT the same file as <c>events.jsonl</c> (<see cref="RunEventStream"/>'s job): that stream
/// is semantic and low-frequency for a supervising agent, while a renderer needs every call verbatim,
/// including the live-only ones (elapsed time, the guardrail currently executing) a filtered agent stream
/// would starve.
///
/// <para>A DECORATOR, wrapping the real <paramref name="inner"/> observer of a run. Every call is:</para>
/// <list type="number">
///   <item>appended as one JSON line to <c>observer.jsonl</c> in the given directory, naming the member and
///     carrying its arguments — so reading the file back reproduces the exact call sequence, in order, which
///     is the property <c>guardrails attach</c> depends on to drive a REAL <see cref="LiveRunObserver"/> in a
///     second terminal (not a reimplementation of it);</item>
///   <item>forwarded to <paramref name="inner"/> — this decorator must never be the run's only observer.</item>
/// </list>
///
/// <para>Every member is declared EXPLICITLY, not left to the interface's default no-op body: §3 of plan 34
/// names the exact trap — <c>IRunObserver</c>'s default-implemented members mean a decorator that omits one
/// silently swallows that event in every mode, the same defect already fixed four times over
/// (<see cref="IRunObserver.VerifierAdvisoryFound"/>, <see cref="IRunObserver.AttemptModelResolved"/>,
/// <see cref="IRunObserver.WaveGateFinished"/>, <see cref="IRunObserver.WaveBreakdownStarting"/>). A
/// projection whose entire purpose is "record every observed call" cannot itself rely on that default body,
/// or the "every" is false from the day it ships.</para>
///
/// <para>Each call opens, appends, and closes <c>observer.jsonl</c> under one in-process lock — so a line is
/// flushed to disk the moment it is written (no buffered writer straddling calls) and the handle is never
/// held open between calls, letting <c>guardrails attach</c>'s tailing readers open their own read-only,
/// share-everything handle without contending for it.</para>
/// </summary>
public sealed class ObserverProjection : IRunObserver
{
    private const string FileName = "observer.jsonl";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IRunObserver _inner;
    private readonly string _directory;
    private readonly object _writeLock = new();

    /// <param name="inner">The real observer (live or console) every call is forwarded to, verbatim.</param>
    /// <param name="directory">
    /// The run's <c>logs/&lt;runId&gt;/</c> tree — <c>observer.jsonl</c> is appended to inside it.
    /// </param>
    public ObserverProjection(IRunObserver inner, string directory)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    public void TaskStarting(TaskNode task)
    {
        Append(new JsonObject { ["member"] = "TaskStarting", ["taskId"] = task.Id });
        _inner.TaskStarting(task);
    }

    public void AttemptStarting(TaskNode task, int attempt, int budget)
    {
        Append(new JsonObject
        {
            ["member"] = "AttemptStarting",
            ["taskId"] = task.Id,
            ["attempt"] = attempt,
            ["budget"] = budget
        });
        _inner.AttemptStarting(task, attempt, budget);
    }

    public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel)
    {
        Append(new JsonObject
        {
            ["member"] = "AttemptModelResolved",
            ["taskId"] = task.Id,
            ["attempt"] = attempt,
            ["model"] = model,
            ["requestedModel"] = requestedModel
        });
        _inner.AttemptModelResolved(task, attempt, model, requestedModel);
    }

    public void AttemptRouteResolved(
        TaskNode task, int attempt, string runner, string model, string? tier, string? requestedTier)
    {
        Append(new JsonObject
        {
            ["member"] = "AttemptRouteResolved",
            ["taskId"] = task.Id,
            ["attempt"] = attempt,
            ["runner"] = runner,
            ["model"] = model,
            ["tier"] = tier,
            ["requestedTier"] = requestedTier
        });
        _inner.AttemptRouteResolved(task, attempt, runner, model, tier, requestedTier);
    }

    public void AttemptFinished(TaskNode task, int attempt, Journal.AttemptOutcome outcome)
    {
        Append(new JsonObject
        {
            ["member"] = "AttemptFinished",
            ["taskId"] = task.Id,
            ["attempt"] = attempt,
            ["outcome"] = outcome.ToString()
        });
        _inner.AttemptFinished(task, attempt, outcome);
    }

    public void TaskFinished(TaskResult result)
    {
        Append(new JsonObject
        {
            ["member"] = "TaskFinished",
            ["taskId"] = result.TaskId,
            ["outcome"] = result.Outcome.ToString(),
            ["summary"] = result.Summary
        });
        _inner.TaskFinished(result);
    }

    public void GuardrailFinished(TaskNode task, GuardrailResult result)
    {
        Append(new JsonObject
        {
            ["member"] = "GuardrailFinished",
            ["taskId"] = task.Id,
            ["name"] = result.Name,
            ["passed"] = result.Passed,
            ["reason"] = result.Reason
        });
        _inner.GuardrailFinished(task, result);
    }

    public void PlanHashMismatch(string previousPlanHash)
    {
        Append(new JsonObject { ["member"] = "PlanHashMismatch", ["previousPlanHash"] = previousPlanHash });
        _inner.PlanHashMismatch(previousPlanHash);
    }

    public void ParallelismClampedNoProvider(int requested)
    {
        Append(new JsonObject { ["member"] = "ParallelismClampedNoProvider", ["requested"] = requested });
        _inner.ParallelismClampedNoProvider(requested);
    }

    public void CleanupFailed(string owner, Exception error)
    {
        Append(new JsonObject
        {
            ["member"] = "CleanupFailed",
            ["owner"] = owner,
            ["error"] = error.Message
        });
        _inner.CleanupFailed(owner, error);
    }

    public void PromptPaused(TaskNode task, string reason, TimeSpan backoff, int pauseCount)
    {
        Append(new JsonObject
        {
            ["member"] = "PromptPaused",
            ["taskId"] = task.Id,
            ["reason"] = reason,
            ["backoffSeconds"] = backoff.TotalSeconds,
            ["pauseCount"] = pauseCount
        });
        _inner.PromptPaused(task, reason, backoff, pauseCount);
    }

    public void OutOfScopeStripped(TaskNode task, IReadOnlyList<WriteScopeOffense> stripped)
    {
        var strippedPaths = new JsonArray();
        foreach (WriteScopeOffense offense in stripped)
        {
            strippedPaths.Add(JsonValue.Create(offense.Path));
        }

        Append(new JsonObject
        {
            ["member"] = "OutOfScopeStripped",
            ["taskId"] = task.Id,
            ["strippedPaths"] = strippedPaths
        });
        _inner.OutOfScopeStripped(task, stripped);
    }

    public void DecisionRecorded(DecisionEntry entry)
    {
        Append(new JsonObject
        {
            ["member"] = "DecisionRecorded",
            ["boundary"] = entry.Boundary,
            ["policy"] = entry.Policy,
            ["decision"] = entry.Decision,
            ["subject"] = entry.Subject,
            ["headline"] = entry.Headline
        });
        _inner.DecisionRecorded(entry);
    }

    public void VerifierAdvisoryFound(string taskId, string finding)
    {
        Append(new JsonObject { ["member"] = "VerifierAdvisoryFound", ["taskId"] = taskId, ["finding"] = finding });
        _inner.VerifierAdvisoryFound(taskId, finding);
    }

    public void OverwatchNoVerdict(string taskId, string reason)
    {
        Append(new JsonObject { ["member"] = "OverwatchNoVerdict", ["taskId"] = taskId, ["reason"] = reason });
        _inner.OverwatchNoVerdict(taskId, reason);
    }

    public void WaveStarting(WaveNode wave, int index, int total)
    {
        Append(new JsonObject
        {
            ["member"] = "WaveStarting",
            ["waveDir"] = wave.Dir,
            ["index"] = index,
            ["total"] = total
        });
        _inner.WaveStarting(wave, index, total);
    }

    public void WaveFinished(WaveNode wave, Journal.WaveStatus status, bool skipped)
    {
        Append(new JsonObject
        {
            ["member"] = "WaveFinished",
            ["waveDir"] = wave.Dir,
            ["status"] = status.ToString(),
            ["skipped"] = skipped
        });
        _inner.WaveFinished(wave, status, skipped);
    }

    public void WaveGateFinished(
        WaveNode wave, bool isEntryGate, IReadOnlyList<Journal.PlanPreflightCheck> checks)
    {
        var checkArray = new JsonArray();
        foreach (Journal.PlanPreflightCheck check in checks)
        {
            checkArray.Add(new JsonObject
            {
                ["name"] = check.Name,
                ["passed"] = check.Passed,
                ["reason"] = check.Reason
            });
        }

        Append(new JsonObject
        {
            ["member"] = "WaveGateFinished",
            ["waveDir"] = wave.Dir,
            ["isEntryGate"] = isEntryGate,
            ["checks"] = checkArray
        });
        _inner.WaveGateFinished(wave, isEntryGate, checks);
    }

    public void WaveBreakdownStarting(WaveBreakdownContext context)
    {
        Append(new JsonObject
        {
            ["member"] = "WaveBreakdownStarting",
            ["waveDir"] = context.WaveDir,
            ["index"] = context.Index,
            ["total"] = context.Total
        });
        _inner.WaveBreakdownStarting(context);
    }

    public void WaveBreakdownFinished(
        WaveBreakdownContext context, TimeSpan elapsed, int authoredTaskCount, string? failureKind,
        WaveNode? authoredWave)
    {
        Append(new JsonObject
        {
            ["member"] = "WaveBreakdownFinished",
            ["waveDir"] = context.WaveDir,
            ["elapsedSeconds"] = elapsed.TotalSeconds,
            ["authoredTaskCount"] = authoredTaskCount,
            ["failureKind"] = failureKind,
            ["authoredWaveDir"] = authoredWave?.Dir
        });
        _inner.WaveBreakdownFinished(context, elapsed, authoredTaskCount, failureKind, authoredWave);
    }

    /// <summary>
    /// Append one compact single-line JSON object to <c>observer.jsonl</c>, opening, writing (with an
    /// explicit flush on close), and closing the handle on EVERY call rather than holding a buffered
    /// writer across calls. Two things depend on that: the line is durable the instant this method
    /// returns ("flushed as it happens"), and no handle is ever held open between calls for a concurrent
    /// tailing reader to contend with. <see cref="FileShare.ReadWrite"/> lets such a reader open its own
    /// handle even in the narrow window this one IS open. The in-process lock serializes concurrent
    /// callers (M4 workers emit events from multiple threads) so lines are never interleaved or lost to a
    /// sharing violation between two writers.
    /// </summary>
    private void Append(JsonObject line)
    {
        string json = line.ToJsonString();
        lock (_writeLock)
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, FileName);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, Utf8NoBom);
            writer.Write(json);
            writer.Write('\n');
        }
    }
}
