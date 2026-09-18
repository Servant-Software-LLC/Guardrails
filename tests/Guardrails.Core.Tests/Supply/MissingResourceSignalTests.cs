using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests.Supply;

/// <summary>
/// Design 41 §2.1 — the shared path-token predicate that both a missing-resource halt's text and the
/// overwatcher's missing-resource consult read, so the two can never disagree about what the agent
/// asked for. Today's regex-based match takes only the first token; these rows pin the three widenings
/// §2.1 requires, plus the property the widening must not lose.
/// <para>
/// TDD red: <see cref="MissingResourceSignal.PathsIn"/> currently throws <see cref="NotImplementedException"/>,
/// so every test below is expected to FAIL against the stub. Do not add
/// <c>Assert.Throws&lt;NotImplementedException&gt;</c> wrappers — that would make these pass against the
/// stub, which defeats the point of pinning them red.
/// </para>
/// </summary>
[Trait("Category", "OverwatchSupply")]
public sealed class MissingResourceSignalTests
{
    [Fact]
    public void PathsIn_ReturnsEveryPathToken_NotOnlyTheFirst()
    {
        IReadOnlyList<string> paths = MissingResourceSignal.PathsIn(
            "Both vendor/mermaid.min.js and docs/notes/readme.md are missing from the workspace.");

        Assert.Equal(new[] { "vendor/mermaid.min.js", "docs/notes/readme.md" }, paths);
    }

    [Fact]
    public void PathsIn_MatchesAScopedNodeModulesPathWhole_BecauseASegmentMayContainAnAtSign()
    {
        IReadOnlyList<string> paths = MissingResourceSignal.PathsIn(
            "Cannot find node_modules/@scope/x/index.js on disk.");

        Assert.Equal(new[] { "node_modules/@scope/x/index.js" }, paths);
    }

    [Fact]
    public void PathsIn_NormalizesALeadingDotSlash_SoARootLevelFileIsNamed()
    {
        IReadOnlyList<string> paths = MissingResourceSignal.PathsIn(
            "Missing ./mermaid.min.js at the workspace root.");

        Assert.Equal(new[] { "mermaid.min.js" }, paths);
    }

    [Fact]
    public void PathsIn_IgnoresProseWithNoSlash_SoNodeJsAndEgNeverMatch()
    {
        IReadOnlyList<string> paths = MissingResourceSignal.PathsIn(
            "This requires Node.js, e.g. a newer runtime, but names no file.");

        Assert.Empty(paths);
    }
}
