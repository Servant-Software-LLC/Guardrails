using Guardrails.Core.Breakdown;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="BreakdownDiff.Compute"/> (SSOT §10): the LOCAL-vs-BASE
/// classification that tells the regeneration merge which authored files a human edited,
/// added, or deleted since the lock was written.
/// </summary>
public sealed class BreakdownDiffTests
{
    private static BreakdownManifest Manifest(params (string Path, string Hash)[] files) => new()
    {
        Files = files.ToDictionary(f => f.Path, f => f.Hash, StringComparer.Ordinal)
    };

    [Fact]
    public void Compute_IdenticalSnapshots_NoDrift()
    {
        BreakdownManifest m = Manifest(("guardrails.json", "aa"), ("tasks/01-a/task.json", "bb"));

        BreakdownDiff diff = BreakdownDiff.Compute(m, m);

        Assert.False(diff.HasDrift);
        Assert.All(diff.Files.Values, s => Assert.Equal(BreakdownFileStatus.Unchanged, s));
    }

    [Fact]
    public void Compute_ChangedHash_IsEdited()
    {
        BreakdownManifest baseM = Manifest(("g/01-build.ps1", "old"));
        BreakdownManifest current = Manifest(("g/01-build.ps1", "new"));

        BreakdownDiff diff = BreakdownDiff.Compute(baseM, current);

        Assert.True(diff.HasDrift);
        Assert.Equal(BreakdownFileStatus.Edited, diff.Files["g/01-build.ps1"]);
        Assert.Equal(new[] { "g/01-build.ps1" }, diff.Edited.ToArray());
    }

    [Fact]
    public void Compute_OnlyInCurrent_IsAdded()
    {
        BreakdownManifest baseM = Manifest(("g/01-build.ps1", "x"));
        BreakdownManifest current = Manifest(("g/01-build.ps1", "x"), ("g/02-extra.ps1", "y"));

        BreakdownDiff diff = BreakdownDiff.Compute(baseM, current);

        Assert.Equal(BreakdownFileStatus.Added, diff.Files["g/02-extra.ps1"]);
        Assert.Equal(new[] { "g/02-extra.ps1" }, diff.Added.ToArray());
    }

    [Fact]
    public void Compute_OnlyInBase_IsMissing()
    {
        BreakdownManifest baseM = Manifest(("g/01-build.ps1", "x"), ("g/02-extra.ps1", "y"));
        BreakdownManifest current = Manifest(("g/01-build.ps1", "x"));

        BreakdownDiff diff = BreakdownDiff.Compute(baseM, current);

        Assert.Equal(BreakdownFileStatus.Missing, diff.Files["g/02-extra.ps1"]);
        Assert.Equal(new[] { "g/02-extra.ps1" }, diff.Missing.ToArray());
    }

    [Fact]
    public void Compute_MixedChanges_ClassifiesEachAndSortsOrdinal()
    {
        BreakdownManifest baseM = Manifest(
            ("a-unchanged", "1"),
            ("b-edited", "1"),
            ("c-missing", "1"));
        BreakdownManifest current = Manifest(
            ("a-unchanged", "1"),
            ("b-edited", "2"),
            ("d-added", "9"));

        BreakdownDiff diff = BreakdownDiff.Compute(baseM, current);

        Assert.True(diff.HasDrift);
        Assert.Equal(BreakdownFileStatus.Unchanged, diff.Files["a-unchanged"]);
        Assert.Equal(BreakdownFileStatus.Edited, diff.Files["b-edited"]);
        Assert.Equal(BreakdownFileStatus.Missing, diff.Files["c-missing"]);
        Assert.Equal(BreakdownFileStatus.Added, diff.Files["d-added"]);

        // Keys are ordinal-sorted regardless of which side they came from.
        Assert.Equal(
            new[] { "a-unchanged", "b-edited", "c-missing", "d-added" },
            diff.Files.Keys.ToArray());
    }

    [Fact]
    public void Compute_NullArguments_Throw()
    {
        BreakdownManifest m = Manifest(("x", "1"));
        Assert.Throws<ArgumentNullException>(() => BreakdownDiff.Compute(null!, m));
        Assert.Throws<ArgumentNullException>(() => BreakdownDiff.Compute(m, null!));
    }
}
