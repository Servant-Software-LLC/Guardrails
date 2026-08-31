// Sample: the ONE defect 02-stubs-declared-and-inert.ps1 exists to catch -> must exit NON-ZERO.
// Stage into a scratch tree at src/Guardrails.Core/Execution/LivePlanEditWatch.cs.
//
// Built from the traps: the doc comments legitimately describe behaviour ("Never throws") and one of
// them would trip an inertness clause read over raw text; the constructor stores the plan and does
// NOT throw, which is what makes stage 7's reds behavioural rather than construction failures.
using System;
using System.Collections.Generic;
using Guardrails.Core.Loading;

namespace Guardrails.Core.Execution;

public sealed record PlanEditedFile(string TaskId, string Label, PlanEditKind Kind);

public enum PlanEditKind { Added, Removed, Modified }

public sealed record PlanEdit(string TaskId, string OldHash, string NewHash,
                              IReadOnlyList<PlanEditedFile> Files);

public sealed class LivePlanEditWatch
{
    private readonly PlanDefinition _plan;

    public LivePlanEditWatch(PlanDefinition plan)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
    }

    /// <summary>Recompute the definition surface, return what changed since the last call, and
    /// re-baseline. Empty when nothing changed. Never throws: an unreadable file is skipped.</summary>
    public IReadOnlyList<PlanEdit> Poll()
    {
        return Array.Empty<PlanEdit>();
    }

    /// <summary>Silently re-baseline these tasks - a HARNESS-authored edit is not an operator edit.
    /// An unknown task id is a no-op. Pass no ids to re-baseline the whole plan.</summary>
    public void Rebaseline(params string[] taskIds)
    {
        throw new NotImplementedException();
    }
}
