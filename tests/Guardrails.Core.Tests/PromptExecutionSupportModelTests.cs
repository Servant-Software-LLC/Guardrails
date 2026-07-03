using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit-level pin of the model-resolution precedence shared by <see cref="ActionRunner"/> (via
/// <see cref="PromptExecutionSupport.ApplyModelOverride"/>) and <see cref="TaskExecutor.ResolveModel"/>
/// (via <see cref="PromptExecutionSupport.ResolveModelForDisplay"/>) — issue #200. These are the two
/// tiny pure functions the shared resolver is built from; the end-to-end proof (real invocation argv +
/// run.json provenance) lives in <c>Guardrails.Integration.Tests.ActionModelResolutionTests</c>.
/// </summary>
public sealed class PromptExecutionSupportModelTests
{
    [Fact]
    public void ApplyModelOverride_TaskOverride_WinsOverRunnerModel()
    {
        var settings = new PromptRunnerSettings { Model = "claude-sonnet-5" };

        PromptRunnerSettings result = PromptExecutionSupport.ApplyModelOverride(settings, "claude-haiku-4-5");

        Assert.Equal("claude-haiku-4-5", result.Model);
    }

    [Fact]
    public void ApplyModelOverride_NullOverride_LeavesRunnerModelUnchanged()
    {
        var settings = new PromptRunnerSettings { Model = "claude-sonnet-5" };

        PromptRunnerSettings result = PromptExecutionSupport.ApplyModelOverride(settings, null);

        Assert.Equal("claude-sonnet-5", result.Model);
    }

    [Fact]
    public void ApplyModelOverride_BothAbsent_StaysNull()
    {
        var settings = new PromptRunnerSettings { Model = null };

        PromptRunnerSettings result = PromptExecutionSupport.ApplyModelOverride(settings, null);

        Assert.Null(result.Model);
    }

    [Theory]
    [InlineData("claude-haiku-4-5", "claude-sonnet-5", "claude-haiku-4-5")]  // task override wins
    [InlineData(null, "claude-sonnet-5", "claude-sonnet-5")]                  // runner default wins
    [InlineData(null, null, "(cli default)")]                                 // neither set → sentinel
    public void ResolveModelForDisplay_MatchesDocumentedPrecedence(
        string? taskOverride, string? runnerModel, string expected) =>
        Assert.Equal(expected, PromptExecutionSupport.ResolveModelForDisplay(taskOverride, runnerModel));
}
