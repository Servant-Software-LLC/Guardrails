using System.CommandLine;
using Guardrails.Cli;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Covers the <c>guardrails --version</c> drift behaviour (issues #152/#156): stdout is exactly
/// the harness version line; a stale/unversioned installed skill produces a stderr warning block;
/// a matching install is silent; the exit code is always 0. The installed version now lives in
/// each skill's <c>SKILL.md</c> frontmatter (<c>metadata.guardrails-version</c>). Drives the real
/// <see cref="VersionWithDriftAction"/> through a System.CommandLine pipeline with injected
/// version, bundled-skills dir, and scan roots so nothing touches the user's real
/// <c>~/.claude/skills</c>.
/// </summary>
public sealed class VersionDriftCliTests : IDisposable
{
    private const string HarnessVersion = "1.0.0-preview.27";

    private readonly string _root;
    private readonly string _bundledSkills;
    private readonly string _scanRoot;

    public VersionDriftCliTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-version-cli-" + Guid.NewGuid().ToString("N"));
        _bundledSkills = Path.Combine(_root, "bundled", "skills");
        _scanRoot = Path.Combine(_root, "installed", "skills");

        // The bundled set defines the "known skills" the warning checks for.
        Directory.CreateDirectory(Path.Combine(_bundledSkills, "plan-breakdown"));
        Directory.CreateDirectory(Path.Combine(_bundledSkills, "guardrails-review"));
    }

    private async Task<(int ExitCode, string Out, string Err)> InvokeVersionAsync()
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");

        VersionOption versionOption = root.Options.OfType<VersionOption>().Single();
        versionOption.Action = new VersionWithDriftAction(
            io, HarnessVersion, _bundledSkills, new[] { _scanRoot });

        int exitCode = await root.Parse("--version").InvokeAsync(configuration: null, TestContext.Current.CancellationToken);
        return (exitCode, io.OutText, io.ErrorText);
    }

    /// <summary>
    /// Install a skill folder under the scan root. With <paramref name="version"/> set, write a
    /// SKILL.md whose frontmatter carries that metadata.guardrails-version; with it null, install
    /// a folder whose SKILL.md has no version key (the unversioned case).
    /// </summary>
    private void InstallSkill(string name, string? version)
    {
        string dir = Path.Combine(_scanRoot, name);
        Directory.CreateDirectory(dir);

        string frontmatter = version is null
            ? $"---\nname: {name}\ndescription: a skill\n---\n# {name}\n"
            : $"---\nname: {name}\ndescription: |\n  A skill.\nmetadata:\n  guardrails-version: {version}\n---\n# {name}\n";
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), frontmatter);
    }

    [Fact]
    public async Task Version_StdoutIsExactlyTheHarnessVersion()
    {
        (int exitCode, string outText, _) = await InvokeVersionAsync();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(HarnessVersion, outText.Trim());
    }

    [Fact]
    public async Task Version_StaleFrontmatter_WarnsOnStderr_ExitZero()
    {
        InstallSkill("plan-breakdown", "1.0.0-preview.26"); // drifted

        (int exitCode, string outText, string errText) = await InvokeVersionAsync();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(HarnessVersion, outText.Trim());           // stdout unchanged

        Assert.Contains("WARNING", errText);
        Assert.Contains("plan-breakdown", errText);
        Assert.Contains("1.0.0-preview.26", errText);            // the stale version
        Assert.Contains(_scanRoot, errText);                    // the root location
        Assert.Contains("guardrails skills install --force", errText);
    }

    [Fact]
    public async Task Version_UnversionedInstall_WarnsAsUnversioned()
    {
        InstallSkill("plan-breakdown", version: null); // no frontmatter version → unversioned

        (int exitCode, _, string errText) = await InvokeVersionAsync();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("WARNING", errText);
        Assert.Contains("unversioned", errText);
        Assert.Contains("plan-breakdown", errText);
    }

    [Fact]
    public async Task Version_AllMatching_NoWarning()
    {
        InstallSkill("plan-breakdown", HarnessVersion);
        InstallSkill("guardrails-review", HarnessVersion);

        (int exitCode, string outText, string errText) = await InvokeVersionAsync();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(HarnessVersion, outText.Trim());
        Assert.Equal(string.Empty, errText);                    // nothing on stderr
    }

    [Fact]
    public async Task Version_NothingInstalled_NoWarning()
    {
        // _scanRoot has no installed skills at all.
        (int exitCode, _, string errText) = await InvokeVersionAsync();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, errText);
    }

    [Fact]
    public async Task Version_CollidingScanRoots_WarnsOncePerSkill_NotOncePerRoot()
    {
        // Issue: DefaultScanRoots() returns a user-level and a project-level root that
        // legitimately collapse to the same physical directory when the cwd resolves under the
        // user's profile. Before the fix, every installed skill under that one directory was
        // warned about twice — once per (identical) root string.
        InstallSkill("plan-breakdown", "1.0.0-preview.26"); // drifted
        InstallSkill("guardrails-review", version: null);   // unversioned

        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        VersionOption versionOption = root.Options.OfType<VersionOption>().Single();

        // Two scan-root strings that resolve to the exact same directory as _scanRoot.
        string collidingRoot = _scanRoot + Path.DirectorySeparatorChar;
        versionOption.Action = new VersionWithDriftAction(
            io, HarnessVersion, _bundledSkills, new[] { _scanRoot, collidingRoot });

        int exitCode = await root.Parse("--version").InvokeAsync(configuration: null, TestContext.Current.CancellationToken);
        string errText = io.ErrorText;

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(1, CountOccurrences(errText, "- plan-breakdown"));
        Assert.Equal(1, CountOccurrences(errText, "- guardrails-review"));
        Assert.Contains("WARNING: 2 installed", errText);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    [Fact]
    public async Task Version_NoBundledSkills_SkipsCheckSilently()
    {
        // A build that does not carry skills: the bundled dir is absent.
        Directory.Delete(_bundledSkills, recursive: true);
        InstallSkill("plan-breakdown", "1.0.0-preview.26"); // would be drift, but unknown skill

        (int exitCode, string outText, string errText) = await InvokeVersionAsync();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(HarnessVersion, outText.Trim());
        Assert.Equal(string.Empty, errText);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
