using Guardrails.Cli.Commands;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Covers <see cref="FolderArgument.Resolve"/>: a real value passes through unchanged, and an
/// omitted (null/empty/whitespace) value falls back to the current working directory — the
/// "omitted → cwd" contract every plan command relies on.
/// </summary>
public sealed class FolderArgumentTests
{
    [Fact]
    public void Resolve_NonEmptyValue_ReturnedUnchanged()
    {
        const string folder = "some/plan/folder";

        Assert.Equal(folder, FolderArgument.Resolve(folder));
    }

    [Fact]
    public void Resolve_Null_FallsBackToCurrentDirectory()
    {
        Assert.Equal(Directory.GetCurrentDirectory(), FolderArgument.Resolve(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Resolve_EmptyOrWhitespace_FallsBackToCurrentDirectory(string value)
    {
        Assert.Equal(Directory.GetCurrentDirectory(), FolderArgument.Resolve(value));
    }
}
