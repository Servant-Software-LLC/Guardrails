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
///
/// <para>The harness version is the STABLE <c>X.Y.0</c> release scheme (issue #421). The drifted
/// installs are deliberately legacy <c>1.0.0-preview.N</c> values: that mixed pair — a
/// prerelease-stamped skill against a stable harness — is exactly what a user upgrading off the
/// preview line has on disk, and it is the drift the warning most needs to surface correctly.</para>
/// </summary>
public sealed class VersionDriftCliTests : IDisposable
{
    private const string HarnessVersion = "1.1.0";

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
        InstallSkill("plan-breakdown", "1.0.0-preview.49"); // drifted

        (int exitCode, string outText, string errText) = await InvokeVersionAsync();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(HarnessVersion, outText.Trim());           // stdout unchanged

        Assert.Contains("WARNING", errText);
        Assert.Contains("plan-breakdown", errText);
        Assert.Contains("1.0.0-preview.49", errText);            // the stale version
        Assert.Contains(_scanRoot, errText);                    // the root location

        // Issue #461: the remedy must target the root the warning just named. This scan root is neither
        // the user-level nor the project-level default, so the only command that can clear this warning is
        // the explicit --target form. The pre-#461 fixed `skills install --force` wrote to ~/.claude/skills
        // and would have left this warning standing verbatim.
        Assert.Contains(
            $"Remedy for {_scanRoot}: run `guardrails skills install --target {_scanRoot} --force`.",
            errText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Version_TwoDriftedRoots_EachGetsTheRemedyForThatRoot()
    {
        // Issue #461 — one fixed remedy line cannot be right for two roots. Each root gets the command
        // that writes to IT, so running both clears the whole block.
        string secondRoot = Path.Combine(_root, "installed-two", "skills");
        Directory.CreateDirectory(Path.Combine(secondRoot, "guardrails-review"));
        File.WriteAllText(
            Path.Combine(secondRoot, "guardrails-review", "SKILL.md"),
            "---\nname: guardrails-review\ndescription: a skill\n---\n# guardrails-review\n");
        InstallSkill("plan-breakdown", "1.0.0-preview.49");

        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        VersionOption versionOption = root.Options.OfType<VersionOption>().Single();
        versionOption.Action = new VersionWithDriftAction(
            io, HarnessVersion, _bundledSkills, new[] { _scanRoot, secondRoot });

        await root.Parse("--version").InvokeAsync(configuration: null, TestContext.Current.CancellationToken);
        string errText = io.ErrorText;

        Assert.Contains($"Remedy for {_scanRoot}: run `guardrails skills install --target {_scanRoot} --force`.",
            errText, StringComparison.Ordinal);
        Assert.Contains($"Remedy for {secondRoot}: run `guardrails skills install --target {secondRoot} --force`.",
            errText, StringComparison.Ordinal);
    }

    // ── #461 — a git-TRACKED skills root is authored SOURCE, not a stale install ──────────────────

    [Fact]
    public async Task Version_GitTrackedRoot_IsNotReportedAsAStaleInstall()
    {
        // THE #461 CASE, reproduced against real git: in this very repo `./.claude/skills` is tracked
        // source — where the shipped skills are AUTHORED, deliberately left unstamped. Reporting it as a
        // stale install was wrong at the category level: no install command writes an author's source, so
        // no remedy could ever clear the warning, and the one command that targets that root
        // (`skills install --project --force`) would have replaced the author's work with the bundle.
        using var repo = new TempSkillsRepo();
        string trackedRoot = repo.CommitSkillsRoot("plan-breakdown");

        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        VersionOption versionOption = root.Options.OfType<VersionOption>().Single();
        versionOption.Action = new VersionWithDriftAction(
            io, HarnessVersion, _bundledSkills, new[] { trackedRoot });

        int exitCode = await root.Parse("--version")
            .InvokeAsync(configuration: null, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(HarnessVersion, io.OutText.Trim());
        Assert.Equal(string.Empty, io.ErrorText);   // no warning, and so no remedy that cannot help
    }

    [Fact]
    public async Task Version_UntrackedRootInsideARepo_StillWarns()
    {
        // The control that keeps the #461 fix honest: the discriminator is TRACKED, not "inside a git
        // repo". An install directory that happens to sit in a working copy (git knows nothing about it)
        // is still an install, and a stale one still has to be reported.
        using var repo = new TempSkillsRepo();
        string untrackedRoot = repo.SkillsRootWithoutCommitting("plan-breakdown");

        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        VersionOption versionOption = root.Options.OfType<VersionOption>().Single();
        versionOption.Action = new VersionWithDriftAction(
            io, HarnessVersion, _bundledSkills, new[] { untrackedRoot });

        await root.Parse("--version").InvokeAsync(configuration: null, TestContext.Current.CancellationToken);

        Assert.Contains("WARNING", io.ErrorText);
        Assert.Contains("plan-breakdown", io.ErrorText);
    }

    [Fact]
    public async Task Version_StableButOlderInstall_WarnsOnStderr_ExitZero()
    {
        // Stable-vs-stable drift: both sides are X.Y.0 with no prerelease segment. Pinned
        // separately from the legacy case above so a stable version is proven to survive the
        // stamp → read → compare → report path intact (it is echoed verbatim in the warning).
        InstallSkill("plan-breakdown", "1.0.0");

        (int exitCode, string outText, string errText) = await InvokeVersionAsync();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(HarnessVersion, outText.Trim());
        Assert.Contains("WARNING", errText);
        Assert.Contains("[v1.0.0]", errText);      // the stale version, verbatim and un-mangled
        Assert.DoesNotContain("unversioned", errText);
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
        InstallSkill("plan-breakdown", "1.0.0-preview.49"); // drifted
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
        InstallSkill("plan-breakdown", "1.0.0-preview.49"); // would be drift, but unknown skill

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
