// A COMPLETE, representative CORRECT artifact for 04-one-delivery-term-no-second-path.ps1 (#468/#302):
// the run report after stage 13. ONE delivery predicate, its four existing terms intact, the new term
// added and negated, and no second gate. The per-task Succeeded predicate is shipped and unrelated to
// delivery - it is why the ban carries an allowlist. Kept complete rather than a fragment; this header
// names none of the tokens the clauses key on.
using System.Collections.Generic;
using System.Linq;

namespace Guardrails.Core.Execution;

public sealed record TaskReport
{
    public required string Id { get; init; }

    public required TaskOutcome Outcome { get; init; }

    public bool Succeeded => Outcome == TaskOutcome.Succeeded;

    public bool IsGreen => Outcome is TaskOutcome.Succeeded or TaskOutcome.Skipped;
}

public sealed record RunReport
{
    public required IReadOnlyList<TaskReport> Tasks { get; init; }

    /// <summary>
    /// THE single predicate that gates delivery, the green summary and the exit code. One expression, so
    /// a delivery-gate change has a one-expression blast radius - and no second path, which is the lesson
    /// of the defect where a gate that ran AFTER delivery was the problem.
    /// </summary>
    public bool AllSucceeded => !HasDefinitionDrift && !HasWaveHalt && !Aborted
                             && !HasExecutedDefinitionDivergence && Tasks.All(t => t.IsGreen);

    public bool AnyFailed => Tasks.Any(t => !t.IsGreen);

    public bool WhollyGreenButUndelivered { get; init; }

    public bool DeliveryPendingTerminalGate { get; init; }

    public AbortReport? Abort { get; init; }

    public bool Aborted => Abort is not null;

    public DefinitionDriftReport? DefinitionDrift { get; init; }

    public bool HasDefinitionDrift => DefinitionDrift is not null;

    public WaveHalt? WaveHalt { get; init; }

    public bool HasWaveHalt => WaveHalt is not null;

    /// <summary>
    /// The executed-definition divergence: the plan folder moved between a task's load and its settle.
    /// Carries BOTH per-task hashes and the moved-file list, so an operator can see what changed without
    /// re-deriving it.
    /// </summary>
    public ExecutedDefinitionDivergenceReport? ExecutedDefinitionDivergence { get; init; }

    public bool HasExecutedDefinitionDivergence => ExecutedDefinitionDivergence is not null;
}

public sealed record ExecutedDefinitionDivergenceReport
{
    public required IReadOnlyList<DivergedTask> Tasks { get; init; }
}

public sealed record DivergedTask
{
    public required string Id { get; init; }

    public required string DefinitionHashAtLoad { get; init; }

    public required string DefinitionHashAtSettle { get; init; }

    public required IReadOnlyList<string> MovedFiles { get; init; }
}
