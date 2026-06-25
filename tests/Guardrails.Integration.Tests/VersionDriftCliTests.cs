using System.CommandLine;
using Guardrails.Cli;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Covers the <c>guardrails --version</c> drift behaviour (issue #152): stdout is exactly the
/// harness version line; a stale/unversioned installed skill produces a stderr warning block;
/// a matching install is silent; the exit code is always 0. Drives the real
/// <see cref="VersionWithDriftAction"/> through a System.CommandLine pipeline with injected
/// version, bundled-skills dir, and scan roots so nothing touches the user's real
/// <c>~/.claude/skills</c>.
/// </summary>
public sealed class VersionDriftCliTests : IDisposable
{
    private const string HarnessVersion = "1.0.0-preview.25";

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

        int exitCode = await root.Parse("--version").InvokeAsync();
        return (exitCode, io.OutText, io.ErrorText);
    }

    private void InstallSkill(string name, string? markerContent)
    {
        string dir = Path.Combine(_scanRoot, name);
        Directory.CreateDirectory(dir);
        if (markerContent is not null)
        {
            File.WriteAllText(Path.Combine(dir, SkillVersionReport.MarkerFileName), markerContent);
        }
    }

    [Fact]
    public async Task Version_StdoutIsExactlyTheHarnessVersion()
    {
        (int exitCode, string outText, _) = await InvokeVersionAsync();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(HarnessVersion, outText.Trim());
    }

    [Fact]
    public async Task Version_StaleMarker_WarnsOnStderr_ExitZero()
    {
        InstallSkill("plan-breakdown", "1.0.0-preview.24"); // drifted

        (int exitCode, string outText, string errText) = await InvokeVersionAsync();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(HarnessVersion, outText.Trim());           // stdout unchanged

        Assert.Contains("WARNING", errText);
        Assert.Contains("plan-breakdown", errText);
        Assert.Contains("1.0.0-preview.24", errText);            // the stale version
        Assert.Contains(_scanRoot, errText);                    // the root location
        Assert.Contains("guardrails skills install --force", errText);
    }

    [Fact]
    public async Task Version_UnversionedInstall_WarnsAsUnversioned()
    {
        InstallSkill("plan-breakdown", markerContent: null); // no marker → unversioned

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
    public async Task Version_NoBundledSkills_SkipsCheckSilently()
    {
        // A build that does not carry skills: the bundled dir is absent.
        Directory.Delete(_bundledSkills, recursive: true);
        InstallSkill("plan-breakdown", "1.0.0-preview.24"); // would be drift, but unknown skill

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
