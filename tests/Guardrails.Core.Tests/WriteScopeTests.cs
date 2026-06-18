using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

public sealed class WriteScopeTests
{
    // ── Parse ────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_EmptyList_Succeeds()
    {
        _ = WriteScope.Parse([]);
    }

    [Fact]
    public void Parse_UniversalSentinel_Succeeds()
    {
        _ = WriteScope.Parse(["**"]);
    }

    [Theory]
    [InlineData("src/Feature/**")]
    [InlineData("tests/Feature/**")]
    [InlineData("src/Feature/Thing.cs")]
    public void Parse_ValidGlob_Succeeds(string glob)
    {
        _ = WriteScope.Parse([glob]);
    }

    [Theory]
    [InlineData("src/?.cs")]       // ? single-char wildcard
    [InlineData("src/{A,B}/**")]   // brace expansion
    [InlineData("!src/**")]        // negation prefix
    public void Parse_MalformedGlob_Throws(string glob) =>
        Assert.Throws<ArgumentException>(() => WriteScope.Parse([glob]));

    // ── Normalization ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_BareDirectory_BehavesLikeDoublestar()
    {
        // "dir" must normalize to "dir/**" — both forms must behave identically
        var bare = WriteScope.Parse(["src/Feature"]);
        var explicit_ = WriteScope.Parse(["src/Feature/**"]);
        var child = WriteScope.Parse(["src/Feature/Thing.cs"]);
        var sibling = WriteScope.Parse(["src/Other/**"]);

        Assert.True(WriteScope.Overlaps(bare, child));
        Assert.True(WriteScope.Overlaps(explicit_, child));
        Assert.False(WriteScope.Overlaps(bare, sibling));
        Assert.False(WriteScope.Overlaps(explicit_, sibling));
    }

    // ── Empty short-circuit ──────────────────────────────────────────────────

    [Fact]
    public void Overlaps_EmptyVsNarrow_IsFalse()
    {
        var empty = WriteScope.Parse([]);
        var narrow = WriteScope.Parse(["src/A/**"]);
        Assert.False(WriteScope.Overlaps(empty, narrow));
        Assert.False(WriteScope.Overlaps(narrow, empty));
    }

    [Fact]
    public void Overlaps_EmptyVsUniversal_IsFalse()
    {
        // [] beats ["**"] — the empty short-circuit fires before the universal short-circuit
        var empty = WriteScope.Parse([]);
        var universal = WriteScope.Parse(["**"]);
        Assert.False(WriteScope.Overlaps(empty, universal));
        Assert.False(WriteScope.Overlaps(universal, empty));
    }

    [Fact]
    public void Overlaps_EmptyVsEmpty_IsFalse()
    {
        var a = WriteScope.Parse([]);
        var b = WriteScope.Parse([]);
        Assert.False(WriteScope.Overlaps(a, b));
    }

    // ── Universal short-circuit ──────────────────────────────────────────────

    [Fact]
    public void Overlaps_UniversalVsNarrow_IsTrue()
    {
        var universal = WriteScope.Parse(["**"]);
        var narrow = WriteScope.Parse(["src/A/**"]);
        Assert.True(WriteScope.Overlaps(universal, narrow));
        Assert.True(WriteScope.Overlaps(narrow, universal));
    }

    [Fact]
    public void Overlaps_BothUniversal_IsTrue()
    {
        var universal = WriteScope.Parse(["**"]);
        Assert.True(WriteScope.Overlaps(universal, universal));
    }

    // ── Narrow overlap cases ─────────────────────────────────────────────────

    [Fact]
    public void Overlaps_DisjointSiblingDirs_IsFalse()
    {
        var a = WriteScope.Parse(["src/A/**"]);
        var b = WriteScope.Parse(["src/B/**"]);
        Assert.False(WriteScope.Overlaps(a, b));
    }

    [Fact]
    public void Overlaps_IdenticalScopes_IsTrue()
    {
        var a = WriteScope.Parse(["src/Feature/**"]);
        Assert.True(WriteScope.Overlaps(a, a));
    }

    [Fact]
    public void Overlaps_ParentGlobAndChildFile_IsTrue()
    {
        var parent = WriteScope.Parse(["src/Feature/**"]);
        var child = WriteScope.Parse(["src/Feature/Thing.cs"]);
        Assert.True(WriteScope.Overlaps(parent, child));
    }

    [Fact]
    public void Overlaps_SiblingPrefixTrap_IsFalse()
    {
        // "src/FeatureX/**" must NOT overlap "src/Feature/**"
        // FeatureX shares a string prefix with Feature but is a sibling directory, not a child
        var featureX = WriteScope.Parse(["src/FeatureX/**"]);
        var feature = WriteScope.Parse(["src/Feature/**"]);
        Assert.False(WriteScope.Overlaps(featureX, feature));
    }

    // ── Conservative bias ────────────────────────────────────────────────────

    [Fact]
    public void Overlaps_DeepPathUnderDoublestar_TreatedAsOverlapping()
    {
        // ["src/**"] covers any depth; a nested file is provably inside it.
        // If the walker cannot prove disjoint (conservative bias), it must return true.
        var broad = WriteScope.Parse(["src/**"]);
        var deep = WriteScope.Parse(["src/A/B/C/D.cs"]);
        Assert.True(WriteScope.Overlaps(broad, deep));
    }
}
