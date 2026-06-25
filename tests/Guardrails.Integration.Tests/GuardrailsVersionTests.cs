using System.Reflection;
using Guardrails.Cli;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Covers the harness-version helper: <c>+build</c> metadata stripping, the informational-vs-
/// assembly-version fallback, and that <see cref="GuardrailsVersion.Current"/> resolves to a
/// non-empty string for the running build.
/// </summary>
public sealed class GuardrailsVersionTests
{
    [Theory]
    [InlineData("1.0.0-preview.25", "1.0.0-preview.25")]
    [InlineData("1.0.0-preview.25+abc123", "1.0.0-preview.25")]
    [InlineData("  1.2.3+deadbeef  ", "1.2.3")]
    [InlineData("2.0.0", "2.0.0")]
    public void Normalize_StripsBuildMetadataAndTrims(string input, string expected)
    {
        Assert.Equal(expected, GuardrailsVersion.Normalize(input));
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
