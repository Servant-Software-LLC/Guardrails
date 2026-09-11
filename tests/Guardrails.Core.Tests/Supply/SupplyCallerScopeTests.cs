using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests.Supply;

/// <summary>
/// Design 40 §5a/§6 — DECIDED (review): a task agent may call <c>guardrails supply</c> only for paths
/// inside its own <c>writeScope</c>; an operator invocation is unrestricted. The caller is distinguished
/// by whether the <c>GUARDRAILS_*</c> namespace is present in its environment, reliable because #442 made
/// that namespace hermetic across the process boundary (SSOT §5.1).
/// <para>
/// This is a guard against the accidental and naive case, not against an adversary who clears the
/// variables — §5a is explicit that the provenance record (§4) is the real defence, and that is a
/// different task. Nothing here asserts detection survives a cleared environment.
/// </para>
/// <para>
/// TDD red: <see cref="SupplyCallerScope.Check"/> currently throws <see cref="NotImplementedException"/>,
/// so every test below is expected to FAIL against the stub. Do not add
/// <c>Assert.Throws&lt;NotImplementedException&gt;</c> wrappers — that would make these pass against the
/// stub, which defeats the point of pinning them red.
/// </para>
/// </summary>
[Trait("Category", "Supply")]
public sealed class SupplyCallerScopeTests
{
    // No GUARDRAILS_* key at all — an operator's own shell, never a task action (design 40 §5a).
    private static readonly IReadOnlyDictionary<string, string> OperatorEnvironment =
        new Dictionary<string, string>();

    // The pair TaskExecutor's BuildEnvironment always sets for an action (design 40 §5a: "sees
    // GUARDRAILS_STATE_OUT / GUARDRAILS_WORKSPACE"), plus the task id a real caller-scope resolution
    // would key its writeScope lookup on.
    private static readonly IReadOnlyDictionary<string, string> TaskEnvironment = new Dictionary<string, string>
    {
        ["GUARDRAILS_TASK_ID"] = "07-do-a-thing",
        ["GUARDRAILS_STATE_OUT"] = "/plan/logs/R/07-do-a-thing/attempt-1/fragment.json",
        ["GUARDRAILS_WORKSPACE"] = "/plan/logs/R/_integration"
    };

    [Fact]
    public void OperatorInvocation_IsUnrestricted()
    {
        // No GUARDRAILS_* in the environment ⇒ any workspace path, even one nowhere near the
        // (irrelevant, for this caller) writeScope below.
        CallerScopeResult result = SupplyCallerScope.Check(
            OperatorEnvironment,
            writeScope: ["src/OnlyThisTask/**"],
            workspaceRelativePath: "src/SomewhereElseEntirely/File.cs");

        Assert.False(result.Refused);
    }

    [Fact]
    public void TaskInvocation_MaySupplyInsideItsOwnWriteScope()
    {
        // The JIT case the reviewer valued: an agent authoring a script it then needs on the base almost
        // always owns that path already.
        CallerScopeResult result = SupplyCallerScope.Check(
            TaskEnvironment,
            writeScope: ["src/Guardrails.Core/Execution/**"],
            workspaceRelativePath: "src/Guardrails.Core/Execution/NewHelper.cs");

        Assert.False(result.Refused);
    }

    [Fact]
    public void TaskInvocation_IsRefusedOutsideItsWriteScope()
    {
        // The bypass, closed: writeScope is what stops a task editing files it does not own, and supply
        // must not sidestep it just because the path arrives via a different verb.
        CallerScopeResult result = SupplyCallerScope.Check(
            TaskEnvironment,
            writeScope: ["src/Guardrails.Core/Execution/**"],
            workspaceRelativePath: "src/Guardrails.Cli/Program.cs");

        Assert.True(result.Refused);
        Assert.False(string.IsNullOrWhiteSpace(result.RefusalReason));
    }

    [Theory]
    [InlineData("src/Guardrails.Core/Execution/Thing.cs", true)]
    [InlineData("src/Guardrails.Cli/Program.cs", false)]
    [InlineData("docs/plans/40-in-flight-resource-supply.md", false)]
    public void TaskInvocation_UsesTheSameMembershipRuleAsTheWriteScopeCheck(string path, bool expectInScope)
    {
        // Scoping must agree with WriteScope.IsInScope, the rule the harness already enforces at write
        // time — two mechanisms for one decision is how a file becomes suppliable and unwritable at the
        // same moment. This pins agreement with the REAL rule (glob semantics and all), not merely that
        // the two InlineData expectations happen to match some independent reimplementation.
        IReadOnlyList<string> writeScope = ["src/Guardrails.Core/Execution/**"];

        CallerScopeResult result = SupplyCallerScope.Check(TaskEnvironment, writeScope, path);

        Assert.Equal(expectInScope, !result.Refused);
        Assert.Equal(WriteScope.IsInScope(path, writeScope), !result.Refused);
    }
}
