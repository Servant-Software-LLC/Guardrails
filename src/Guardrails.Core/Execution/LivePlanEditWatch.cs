using System;
using System.Collections.Generic;
using Guardrails.Core.Model;

namespace Guardrails.Core.Execution
{
    public sealed record PlanEditedFile(string TaskId, string Label, PlanEditKind Kind);

    public enum PlanEditKind { Added, Removed, Modified }

    public sealed record PlanEdit(string TaskId, string OldHash, string NewHash,
                                  IReadOnlyList<PlanEditedFile> Files);

    public sealed class LivePlanEditWatch
    {
        public LivePlanEditWatch(PlanDefinition plan)
        {
        }

        /// <summary>Recompute the definition surface, return what changed since the last call, and
        /// re-baseline. Empty when nothing changed. Never throws: an unreadable file is skipped.</summary>
        public IReadOnlyList<PlanEdit> Poll()
        {
            throw new NotImplementedException();
        }

        /// <summary>Silently re-baseline these tasks - a HARNESS-authored edit is not an operator edit.
        /// An unknown task id is a no-op. Pass no ids to re-baseline the whole plan.</summary>
        public void Rebaseline(params string[] taskIds)
        {
            throw new NotImplementedException();
        }
    }
}
