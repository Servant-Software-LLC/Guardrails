using System.Reflection;
using Guardrails.Core.Model;
using Guardrails.Core.Telemetry;

namespace Guardrails.Core.Tests.Telemetry;

/// <summary>
/// The task-fingerprint bucket (plan 30 §3.2, issue #548) — nine behaviours pinned to the six named
/// buckets plus the null "no rule matched" case, and a tenth reflection check on the classifier's
/// signature itself. Each behaviour is pinned to an exact method name the census guardrail binds to.
///
/// <para><b>TDD red.</b> Every behavioural test here calls <see cref="TaskFingerprintBucket.Classify"/>,
/// which throws <see cref="NotImplementedException"/> until the implementation task fills it in — so
/// nine of the ten are red. <see cref="ClassifySignatureAdmitsNoTaskIdentity"/> is the one exemption:
/// it asserts on the method's reflected signature, which the stub already carries correctly, so it is
/// green from the start and stays green — that is the point, not an oversight.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class TaskFingerprintBucketTests
{
    // --- 1. empty writeScope is the deliberate "writes nothing" declaration --------------------------

    [Fact]
    public void EmptyWriteScope_IsNoWrite()
    {
        string? bucket = TaskFingerprintBucket.Classify(
            writeScope: [],
            guardrails: [Guardrail("01-run-completed")]);

        Assert.Equal(TaskFingerprintBucket.NoWrite, bucket);
    }

    // --- 2. null writeScope is the off-switch, a different claim from an empty list -------------------

    [Fact]
    public void NullWriteScope_IsNull_NotNoWrite()
    {
        string? bucket = TaskFingerprintBucket.Classify(
            writeScope: null,
            guardrails: [Guardrail("01-run-completed")]);

        Assert.Null(bucket);
    }

    // --- 3. tests-only + a TDD-red guardrail is test-authoring -----------------------------------------

    [Fact]
    public void TestsOnlyWithATddRedGuardrail_IsTestAuthoring()
    {
        string? bucket = TaskFingerprintBucket.Classify(
            writeScope: ["tests/**"],
            guardrails: [Guardrail("02-tests-fail-on-stubs")]);

        Assert.Equal(TaskFingerprintBucket.TestAuthoring, bucket);
    }

    // --- 4. src-only gated by tests-pass is implementation ----------------------------------------------

    [Fact]
    public void SrcOnlyGatedByTestsPass_IsImplementation()
    {
        string? bucket = TaskFingerprintBucket.Classify(
            writeScope: ["src/**"],
            guardrails: [Guardrail("02-tests-pass")]);

        Assert.Equal(TaskFingerprintBucket.Implementation, bucket);
    }

    // --- 5. src-only with no behavioural gate is structural ---------------------------------------------

    [Fact]
    public void SrcOnlyWithNoBehaviouralGate_IsStructural()
    {
        string? bucket = TaskFingerprintBucket.Classify(
            writeScope: ["src/**"],
            guardrails: [Guardrail("01-build-passes")]);

        Assert.Equal(TaskFingerprintBucket.Structural, bucket);
    }

    // --- 6. tests-only with no behavioural gate is structural too ---------------------------------------

    [Fact]
    public void TestsOnlyWithNoBehaviouralGate_IsStructural()
    {
        string? bucket = TaskFingerprintBucket.Classify(
            writeScope: ["tests/**"],
            guardrails: [Guardrail("01-build-passes")]);

        Assert.Equal(TaskFingerprintBucket.Structural, bucket);
    }

    // --- 7. both src and tests is code+tests, even under a TDD-red guardrail ----------------------------

    /// <summary>
    /// The disambiguator (plan 30 §3.2: 67 of 74 multi-root tasks measured exactly this shape). The
    /// write surface decides, not the guardrail shape — so this carries the SAME kind of guardrail as
    /// <see cref="TestsOnlyWithATddRedGuardrail_IsTestAuthoring"/> (its sibling synonym,
    /// <c>tests-fail-on-current-code</c>) and must still land on <c>code+tests</c>, never
    /// <c>test-authoring</c>.
    /// </summary>
    [Fact]
    public void BothSrcAndTests_IsCodePlusTests_EvenWithATddRedGuardrail()
    {
        string? bucket = TaskFingerprintBucket.Classify(
            writeScope: ["src/**", "tests/**"],
            guardrails: [Guardrail("02-tests-fail-on-current-code")]);

        Assert.Equal(TaskFingerprintBucket.CodePlusTests, bucket);
    }

    // --- 8. docs or .claude only is documentation --------------------------------------------------------

    [Fact]
    public void DocsOrClaudeOnly_IsDocumentation()
    {
        Assert.Equal(
            TaskFingerprintBucket.Documentation,
            TaskFingerprintBucket.Classify(writeScope: ["docs/**"], guardrails: [Guardrail("01-build-passes")]));

        Assert.Equal(
            TaskFingerprintBucket.Documentation,
            TaskFingerprintBucket.Classify(writeScope: [".claude/**"], guardrails: [Guardrail("01-build-passes")]));
    }

    // --- 9. a write surface no rule matches is null, rendered as (unbucketed) ----------------------------

    [Fact]
    public void AWriteSurfaceNoRuleMatches_IsNull()
    {
        string? bucket = TaskFingerprintBucket.Classify(
            writeScope: ["src/**", "docs/**"],
            guardrails: [Guardrail("01-build-passes")]);

        Assert.Null(bucket);
    }

    // --- 10. the signature admits no task identity — the one exemption, green by construction ------------

    /// <summary>
    /// Not a TDD-red test: it asserts on <see cref="TaskFingerprintBucket.Classify"/>'s reflected
    /// signature, which the stub already carries correctly, so this is green against the throwing stub
    /// and stays green once the method is implemented. It exists to stop the signature being widened
    /// later to admit a <c>TaskNode</c> or a bare task identifier — the report legend's constraint made
    /// mechanical rather than merely intended.
    /// </summary>
    [Fact]
    public void ClassifySignatureAdmitsNoTaskIdentity()
    {
        MethodInfo method = typeof(TaskFingerprintBucket).GetMethod("Classify")!;
        ParameterInfo[] parameters = method.GetParameters();

        Assert.Equal(2, parameters.Length);
        Assert.Equal("writeScope", parameters[0].Name);
        Assert.Equal("guardrails", parameters[1].Name);

        foreach (ParameterInfo parameter in parameters)
        {
            Assert.NotEqual(typeof(TaskNode), parameter.ParameterType);
            Assert.False(
                parameter.Name is "taskId" or "id" or "name",
                $"parameter '{parameter.Name}' looks like task identity, which this signature must never admit");
        }
    }

    // --- fixtures --------------------------------------------------------------------------------------

    private static GuardrailDefinition Guardrail(string name) =>
        new() { Name = name, Path = $"guardrails/{name}.ps1", Kind = ActionKind.Script };
}
