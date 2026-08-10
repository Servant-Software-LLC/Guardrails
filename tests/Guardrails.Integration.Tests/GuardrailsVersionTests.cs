using System.Reflection;
using Guardrails.Cli;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Covers the harness-version helper: <c>+build</c> metadata stripping, the informational-vs-
/// assembly-version fallback, and that <see cref="GuardrailsVersion.Current"/> resolves to a
/// non-empty string for the running build.
///
/// <para>Releases are STABLE <c>X.Y.0</c> versions with no prerelease suffix (issue #421), so the
/// normaliser is pinned on BOTH shapes: a stable version must survive untouched (nothing may
/// assume a <c>-preview.N</c> segment is present), and a legacy prerelease must still normalise
/// as it always did (a user upgrading off the preview line has preview-stamped skills installed
/// against a stable harness).</para>
/// </summary>
public sealed class GuardrailsVersionTests
{
    [Theory]
    // Stable X.Y.0 — the current release scheme. A stable version has no '-' segment at all, so
    // this pins that Normalize never depends on one being there.
    [InlineData("1.1.0", "1.1.0")]
    [InlineData("1.1.0+abc123", "1.1.0")]
    [InlineData("  1.2.0+deadbeef  ", "1.2.0")]
    [InlineData("2.0.0", "2.0.0")]
    [InlineData("10.20.30", "10.20.30")]
    // Legacy prerelease — still normalised identically (the '-preview.N' segment is data, not
    // structure: only '+build' metadata is stripped, and the hyphen is never a split point).
    [InlineData("1.0.0-preview.49", "1.0.0-preview.49")]
    [InlineData("1.0.0-preview.49+abc123", "1.0.0-preview.49")]
    public void Normalize_StripsBuildMetadataAndTrims(string input, string expected)
    {
        Assert.Equal(expected, GuardrailsVersion.Normalize(input));
    }

    [Fact]
    public void Normalize_StableVersion_KeepsEveryDotSeparatedComponent()
    {
        // Guards the specific failure mode a stable scheme could introduce: a normaliser that
        // truncated at a separator would silently collapse 1.1.0 to "1.1" or "1", and every
        // stamped-skill comparison would then read as a false match.
        Assert.Equal("1.1.0", GuardrailsVersion.Normalize("1.1.0"));
        Assert.NotEqual("1.1", GuardrailsVersion.Normalize("1.1.0"));
    }

    [Fact]
    public void Resolve_UsesInformationalVersion_StrippingBuildMetadata()
    {
        // The CLI assembly carries an InformationalVersion (the value --version prints).
        Assembly cli = typeof(GuardrailsVersion).Assembly;
        string resolved = GuardrailsVersion.Resolve(cli);

        Assert.False(string.IsNullOrWhiteSpace(resolved));
        Assert.DoesNotContain("+", resolved);
    }

    [Fact]
    public void Current_IsNonEmptyAndMetadataFree()
    {
        Assert.False(string.IsNullOrWhiteSpace(GuardrailsVersion.Current));
        Assert.DoesNotContain("+", GuardrailsVersion.Current);
    }
}
