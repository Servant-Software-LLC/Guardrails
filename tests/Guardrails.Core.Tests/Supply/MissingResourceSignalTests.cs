using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests.Supply;

/// <summary>
/// Design 41 §2.1 — the shared "does this halt name a workspace path?" predicate, widened three ways
/// from the single-match regex it replaces (<c>RunCommand.MissingResourceHaltLines</c>'s
/// <c>ResourcePathToken</c>): every token, not only the first; a segment may carry an <c>@</c>, so a
/// scoped <c>node_modules</c> path matches whole; and a leading <c>./</c> is normalized away. The
/// property the widening must NOT cost — prose with no slash never matches — is pinned alongside them.
/// <para>
/// TDD red: <see cref="MissingResourceSignal.PathsIn"/> currently throws
/// <see cref="NotImplementedException"/>, so every test below is expected to FAIL against the stub. Do
/// not add <c>Assert.Throws&lt;NotImplementedException&gt;</c> wrappers — that would make these pass
/// against the stub, which defeats the point of pinning them red.
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

        Assert.Equal(["vendor/mermaid.min.js", "docs/notes/readme.md"], paths);
    }

    [Fact]
    public void PathsIn_MatchesAScopedNodeModulesPathWhole_BecauseASegmentMayContainAnAtSign()
    {
        IReadOnlyList<string> paths = MissingResourceSignal.PathsIn(
            "I need node_modules/@scope/x/index.js to exist before this task can run.");

        Assert.Equal(["node_modules/@scope/x/index.js"], paths);
    }

    [Fact]
    public void PathsIn_NormalizesALeadingDotSlash_SoARootLevelFileIsNamed()
    {
        IReadOnlyList<string> paths = MissingResourceSignal.PathsIn(
            "Please supply ./mermaid.min.js so the runtime can load it.");

        Assert.Equal(["mermaid.min.js"], paths);
    }

    [Fact]
    public void PathsIn_IgnoresProseWithNoSlash_SoNodeJsAndEgNeverMatch()
    {
        IReadOnlyList<string> paths = MissingResourceSignal.PathsIn(
            "This needs Node.js installed, e.g. via nvm, before anything else.");

        Assert.Empty(paths);
    }
}
