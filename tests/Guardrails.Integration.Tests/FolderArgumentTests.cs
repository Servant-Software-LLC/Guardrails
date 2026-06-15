using Guardrails.Cli.Commands;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Covers <see cref="FolderArgument.Resolve"/> (the "omitted → cwd" contract) and the issue #16
/// plan-file → task-folder fixup (<see cref="FolderArgument.TryResolveMarkdownArgument"/> /
/// <see cref="FolderArgument.ResolveMarkdownArgument"/>): a <c>.md</c> path (or any existing file)
/// whose sibling task folder exists resolves to that folder, with one info line emitted only when
/// resolution actually happens; everything else passes through unchanged so the downstream GR1001
/// "Plan folder does not exist" error still fires for a genuinely bad path.
/// </summary>
public sealed class FolderArgumentTests : IDisposable
{
    private readonly string _scratch;

    public FolderArgumentTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "guardrails-fa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    // --- Resolve (omitted → cwd) -----------------------------------------------------------

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

    // --- TryResolveMarkdownArgument (the pure core) ----------------------------------------

    [Fact]
    public void TryResolve_MdArgWithSiblingFolder_ResolvesToFolder()
    {
        string folder = Path.Combine(_scratch, "0003-foo");
        Directory.CreateDirectory(folder);
        string mdPath = Path.Combine(_scratch, "0003-foo.md");
        File.WriteAllText(mdPath, "# plan\n");

        bool resolved = FolderArgument.TryResolveMarkdownArgument(mdPath, out string result);

        Assert.True(resolved);
        Assert.Equal(folder, result);
    }

    [Fact]
    public void TryResolve_MdArgWithoutSiblingFolder_ReturnedUnchanged()
    {
        // The .md exists but there is NO sibling folder — the user genuinely passed a bad
        // plan-folder path, so the value passes through and the caller still errors (GR1001).
        string mdPath = Path.Combine(_scratch, "0003-foo.md");
        File.WriteAllText(mdPath, "# plan\n");

        bool resolved = FolderArgument.TryResolveMarkdownArgument(mdPath, out string result);

        Assert.False(resolved);
        Assert.Equal(mdPath, result);
    }

    [Fact]
    public void TryResolve_MdSuffixButNoMdFileOnDisk_StillResolvesWhenSiblingFolderExists()
    {
        // The .md file need not exist: a `.md` suffix whose sibling folder exists is enough
        // intent to resolve (the user typed the conventional plan-file name).
        string folder = Path.Combine(_scratch, "0003-foo");
        Directory.CreateDirectory(folder);
        string mdPath = Path.Combine(_scratch, "0003-foo.md"); // not created on disk

        bool resolved = FolderArgument.TryResolveMarkdownArgument(mdPath, out string result);

        Assert.True(resolved);
        Assert.Equal(folder, result);
    }

    [Fact]
    public void TryResolve_ExistingDirectory_ReturnedUnchanged()
    {
        string folder = Path.Combine(_scratch, "0003-foo");
        Directory.CreateDirectory(folder);

        bool resolved = FolderArgument.TryResolveMarkdownArgument(folder, out string result);

        Assert.False(resolved);
        Assert.Equal(folder, result);
    }

    [Fact]
    public void TryResolve_ExistingFileWithoutMdExtension_ResolvesWhenStemFolderExists()
    {
        // The issue's "resolves to a file rather than a directory" clause: a plan file with a
        // different extension whose dir+stem matches a folder still resolves.
        string folder = Path.Combine(_scratch, "0003-foo");
        Directory.CreateDirectory(folder);
        string filePath = Path.Combine(_scratch, "0003-foo.txt");
        File.WriteAllText(filePath, "plan\n");

        bool resolved = FolderArgument.TryResolveMarkdownArgument(filePath, out string result);

        Assert.True(resolved);
        Assert.Equal(folder, result);
    }

    [Theory]
    [InlineData("0003-foo.MD")]
    [InlineData("0003-foo.Md")]
    [InlineData("0003-foo.mD")]
    public void TryResolve_MdCasingVariants_Resolve(string fileName)
    {
        string folder = Path.Combine(_scratch, "0003-foo");
        Directory.CreateDirectory(folder);
        string mdPath = Path.Combine(_scratch, fileName);

        bool resolved = FolderArgument.TryResolveMarkdownArgument(mdPath, out string result);

        Assert.True(resolved);
        Assert.Equal(folder, result);
    }

    [Fact]
    public void TryResolve_NonexistentPathWithoutMd_ReturnedUnchanged()
    {
        // Neither a .md suffix nor an existing file → not our case; pass through so the caller
        // still emits the genuine missing-folder error.
        string missing = Path.Combine(_scratch, "no-such-thing");

        bool resolved = FolderArgument.TryResolveMarkdownArgument(missing, out string result);

        Assert.False(resolved);
        Assert.Equal(missing, result);
    }

    [Fact]
    public void TryResolve_MdArgButSiblingIsAFileNotAFolder_ReturnedUnchanged()
    {
        // The sibling stem exists, but as a FILE, not a directory — there is no task folder to
        // resolve to, so the value passes through.
        string mdPath = Path.Combine(_scratch, "0003-foo.md");
        File.WriteAllText(mdPath, "# plan\n");
        File.WriteAllText(Path.Combine(_scratch, "0003-foo"), "not a folder\n");

        bool resolved = FolderArgument.TryResolveMarkdownArgument(mdPath, out string result);

        Assert.False(resolved);
        Assert.Equal(mdPath, result);
    }

    // --- ResolveMarkdownArgument (info-trace behaviour) ------------------------------------

    [Fact]
    public void ResolveMarkdown_WhenResolved_EmitsExactlyOneInfoLine()
    {
        string folder = Path.Combine(_scratch, "0003-foo");
        Directory.CreateDirectory(folder);
        string mdPath = Path.Combine(_scratch, "0003-foo.md");
        File.WriteAllText(mdPath, "# plan\n");

        using var writer = new StringWriter();
        string result = FolderArgument.ResolveMarkdownArgument(mdPath, writer);

        Assert.Equal(folder, result);
        string output = writer.ToString();
        Assert.Contains($"info: resolved plan file → task folder \"{folder}\"", output);
        Assert.Equal(1, CountLines(output));
    }

    [Fact]
    public void ResolveMarkdown_WhenNotResolved_EmitsNothing()
    {
        // No sibling folder → no resolution → no info line (the value passes through unchanged).
        string mdPath = Path.Combine(_scratch, "0003-foo.md");
        File.WriteAllText(mdPath, "# plan\n");

        using var writer = new StringWriter();
        string result = FolderArgument.ResolveMarkdownArgument(mdPath, writer);

        Assert.Equal(mdPath, result);
        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void ResolveMarkdown_ExistingDirectory_PassesThroughSilently()
    {
        string folder = Path.Combine(_scratch, "0003-foo");
        Directory.CreateDirectory(folder);

        using var writer = new StringWriter();
        string result = FolderArgument.ResolveMarkdownArgument(folder, writer);

        Assert.Equal(folder, result);
        Assert.Equal(string.Empty, writer.ToString());
    }

    // --- ResolveAndAnnounce (the seam every command calls) --------------------------------

    [Fact]
    public void ResolveAndAnnounce_MdArgWithSiblingFolder_ResolvesAndAnnounces()
    {
        string folder = Path.Combine(_scratch, "0003-foo");
        Directory.CreateDirectory(folder);
        string mdPath = Path.Combine(_scratch, "0003-foo.md");
        File.WriteAllText(mdPath, "# plan\n");

        using var writer = new StringWriter();
        string result = FolderArgument.ResolveAndAnnounce(mdPath, writer);

        Assert.Equal(folder, result);
        Assert.Contains("info: resolved plan file → task folder", writer.ToString());
    }

    [Fact]
    public void ResolveAndAnnounce_OmittedValue_AnnouncesCurrentDirectory_NoMarkdownLine()
    {
        using var writer = new StringWriter();
        string result = FolderArgument.ResolveAndAnnounce(null, writer);

        Assert.Equal(Directory.GetCurrentDirectory(), result);
        string output = writer.ToString();
        Assert.Contains("Using current directory:", output);
        Assert.DoesNotContain("info: resolved plan file", output);
    }

    private static int CountLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Length;
}
