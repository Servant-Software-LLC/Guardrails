using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #306 review WEAK-1: the protected-artifact archetype matcher that gates retry-salvage
/// suppression must be robust past a bare <c>"untouched"</c> substring — the review's
/// <c>03-test-files-pristine</c> counter-example slipped that and left the feedback actively
/// instructing <c>git apply</c> on the gamed patch. It must also not over-match ordinary guardrails.
/// </summary>
public sealed class GuardrailArchetypesTests
{
    [Theory]
    [InlineData("03-tests-untouched")]        // the doctrine archetype
    [InlineData("03-test-files-pristine")]    // the review counter-example the old substring missed
    [InlineData("04-goldens-unchanged")]
    [InlineData("02-schema-unmodified")]
    [InlineData("05-config-immutable")]
    [InlineData("01-DO-NOT-EDIT-fixtures")]   // case-insensitive
    [InlineData("06-manifest-read-only")]
    public void IsProtectedArtifactCheck_MatchesTheMustNotBeModifiedFamily(string name)
    {
        Assert.True(GuardrailArchetypes.IsProtectedArtifactCheck(name), name);
    }

    [Theory]
    [InlineData("01-build-passes")]
    [InlineData("02-tests-pass")]
    [InlineData("03-covers-key-behaviors")]
    [InlineData("04-imports-clean")]
    [InlineData("05-lint")]
    public void IsProtectedArtifactCheck_DoesNotMatchOrdinaryGuardrails(string name)
    {
        Assert.False(GuardrailArchetypes.IsProtectedArtifactCheck(name), name);
    }
}
