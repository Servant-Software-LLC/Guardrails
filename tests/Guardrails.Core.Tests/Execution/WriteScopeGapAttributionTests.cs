using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using static Guardrails.Core.Tests.PlanFixtures;

namespace Guardrails.Core.Tests.Execution;

/// <summary>
/// Issue #707's attribution rule — whether the trailer on a path's last commit names an UPSTREAM author of the
/// task that just wrote it out of scope — pinned at the decision seam, without git.
///
/// <para><b>Why here and not end to end.</b> The ancestry half is also proven through a real run
/// (<c>WriteScopeRunTests.PathLastCommittedByATaskThisOneDoesNotDependOn_IsNotAPlanGap_Issue707</c>). The hash half
/// cannot be: a trailer naming a real task of the plan with a FOREIGN definition hash, staged on a run's base, is
/// read by the Scheduler's plan-branch reconcile as that task's completion record and settles as drift before any
/// attempt runs. Measured: the end-to-end control's non-vacuity guard tripped because the implementing task never
/// ran. So the hash condition is defense in depth behind the reconcile, and this is where it can be held.</para>
/// </summary>
public sealed class WriteScopeGapAttributionTests
{
    private static readonly IReadOnlyDictionary<string, TaskNode> TasksById = new Dictionary<string, TaskNode>
    {
        ["01-author"] = PromptTask("01-author") with { DefinitionHashAtLoad = "sha256:loaded" },
        ["03-sibling"] = PromptTask("03-sibling") with { DefinitionHashAtLoad = "sha256:sibling" }
    };

    /// <summary>The implementing task depends on 01-author only; 03-sibling is in the plan but not upstream.</summary>
    private static readonly IReadOnlySet<string> Ancestors = new HashSet<string>(StringComparer.Ordinal) { "01-author" };

    [Fact]
    public void AnAncestorsCommit_CarryingTheDefinitionHashThisRunLoaded_NamesTheUpstreamAuthor()
    {
        Assert.True(TaskExecutor.IsUpstreamAuthor("01-author", "sha256:loaded", Ancestors, TasksById));
    }

    [Theory]
    [InlineData("01-author", "sha256:from-another-plan")] // an older plan's task that shares the id
    [InlineData("03-sibling", "sha256:sibling")]          // this plan's own task and hash, but not a dependency
    [InlineData("01-author", null)]                        // a trailer with no definition hash names no one
    [InlineData(null, "sha256:loaded")]                    // no Guardrails-Task: trailer at all
    [InlineData("99-elsewhere", "sha256:loaded")]          // an id this plan does not have
    public void AnythingLess_IsNotAnUpstreamAuthor(string? author, string? hash)
    {
        Assert.False(TaskExecutor.IsUpstreamAuthor(author, hash, Ancestors, TasksById));
    }
}
