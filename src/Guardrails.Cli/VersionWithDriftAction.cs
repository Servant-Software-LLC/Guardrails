using System.CommandLine;
using System.CommandLine.Invocation;
using Guardrails.Core.Io;

namespace Guardrails.Cli;

/// <summary>
/// Replaces the built-in <c>--version</c> action so the version line is followed by a
/// skill-drift warning when an installed Guardrails skill's <c>SKILL.md</c> frontmatter version
/// (<c>metadata.guardrails-version</c>, issue #156) no longer matches the running harness
/// (issue #152 — a stale installed <c>/plan-breakdown</c> silently produced legacy output).
///
/// Contract: the harness version is written to <b>stdout</b> exactly as the built-in option
/// would (scripts parse <c>guardrails --version</c>); any drift warning goes to <b>stderr</b>;
/// the exit code stays <b>0</b> (the check is informational). The warning block is emitted
/// ONLY when drift exists — when everything matches, or nothing is installed, or this build
/// carries no bundled skills, <c>--version</c> output is unchanged.
///
/// Output is routed through the injected <see cref="IConsoleIo"/> so it is testable; the scan
/// roots and the bundled-skills directory default to the real locations but are injectable.
///
/// <para>Issue #461 — the warning must be ACTIONABLE, and its action must be safe. Two rules follow:
/// a scan root the repository TRACKS is authored source, not an install, and is left out of the report
/// entirely; and every root that IS reported gets the remedy command that writes to THAT root, rather
/// than one fixed suggestion that only ever updated <c>~/.claude/skills</c>.</para>
/// </summary>
public sealed class VersionWithDriftAction : SynchronousCommandLineAction
{
    private readonly IConsoleIo _io;
    private readonly string _harnessVersion;
    private readonly string _bundledSkillsDir;
    private readonly IReadOnlyList<string> _scanRoots;

    /// <summary>Per-root memo for the issue #461 tracked-source probe — at most one git call per root.</summary>
    private readonly Dictionary<string, bool> _trackedSourceRoots = new(StringComparer.Ordinal);

    /// <summary>Production wiring: real harness version, bundled-skills dir, and scan roots.</summary>
    public VersionWithDriftAction(IConsoleIo io)
        : this(io, GuardrailsVersion.Current, DefaultBundledSkillsDir(), DefaultScanRoots())
    {
    }

    /// <summary>Test-friendly wiring: every dependency injected.</summary>
    public VersionWithDriftAction(
        IConsoleIo io,
        string harnessVersion,
        string bundledSkillsDir,
        IReadOnlyList<string> scanRoots)
    {
        _io = io ?? throw new ArgumentNullException(nameof(io));
        _harnessVersion = harnessVersion ?? throw new ArgumentNullException(nameof(harnessVersion));
        _bundledSkillsDir = bundledSkillsDir ?? throw new ArgumentNullException(nameof(bundledSkillsDir));
        _scanRoots = scanRoots ?? throw new ArgumentNullException(nameof(scanRoots));
    }

    /// <inheritdoc />
    public override int Invoke(ParseResult parseResult)
    {
        // stdout stays exactly the version line — unchanged behaviour scripts rely on.
        _io.Out.WriteLine(_harnessVersion);

        WriteDriftWarningIfAny();
        return ExitCodes.Success;
    }

    private void WriteDriftWarningIfAny()
    {
        // Some builds do not carry the bundled skills; with no known-skill list there is
        // nothing to check, so the warning is skipped silently.
        if (!Directory.Exists(_bundledSkillsDir))
        {
            return;
        }

        IReadOnlyList<string> knownSkillNames = Directory
            .EnumerateDirectories(_bundledSkillsDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();

        if (knownSkillNames.Count == 0)
        {
            return;
        }

        IReadOnlyList<SkillVersionStatus> statuses =
            SkillVersionReport.Build(_harnessVersion, knownSkillNames, _scanRoots);

        var drifted = statuses.Where(s => s.Drifted).ToList();
        if (drifted.Count == 0)
        {
            return;
        }

        // Issue #461 — drop roots that are git-tracked SOURCE before warning about them. Probed only
        // here, on the roots that would otherwise be reported, so a clean `--version` spawns no git.
        drifted = drifted.Where(s => !IsTrackedSourceRoot(s.Root)).ToList();
        if (drifted.Count == 0)
        {
            return;
        }

        WriteWarningBlock(drifted);
    }

    /// <summary>
    /// Issue #461 — is <paramref name="root"/> a skills directory the repository TRACKS, i.e. authored
    /// SOURCE rather than an install? In this very repo <c>./.claude/skills</c> is where the shipped skills
    /// are written; reporting it as a stale INSTALL was wrong at the category level, and no install command
    /// could clear the warning because none of them writes an author's source. Worse, the only command that
    /// targets that root — <c>skills install --project --force</c> — would have replaced the author's work
    /// with the bundle from whatever tool version happened to be installed. A tracked root is therefore
    /// excluded from the drift report entirely: its stamp is intentionally absent (the repo source stays
    /// unstamped by design — only installs are stamped), so any "remedy" would be noise on every single
    /// <c>--version</c>. Memoised per root — the two default scan roots cost at most two git probes.
    /// </summary>
    private bool IsTrackedSourceRoot(string root) =>
        _trackedSourceRoots.TryGetValue(root, out bool tracked)
            ? tracked
            : _trackedSourceRoots[root] = GitWorkingTree.TracksAnyFileUnder(root);

    private void WriteWarningBlock(IReadOnlyList<SkillVersionStatus> drifted)
    {
        TextWriter err = _io.Error;
        err.WriteLine();
        err.WriteLine(
            $"WARNING: {drifted.Count} installed Guardrails skill(s) do not match this harness " +
            $"(v{_harnessVersion}):");

        foreach (SkillVersionStatus status in drifted)
        {
            string installed = status.InstalledVersion is null
                ? "unversioned"
                : $"v{status.InstalledVersion}";
            err.WriteLine($"  - {status.Name} [{installed}] in {status.Root}");
        }

        err.WriteLine("A stale skill can silently produce output for an older harness.");

        // Issue #461: one remedy per drifted ROOT, naming the command that writes to THAT root. The old
        // single fixed line always said `skills install --force`, which writes to ~/.claude/skills — so for
        // drift found anywhere else it was a command that provably could not clear the warning it followed.
        foreach (IGrouping<string, SkillVersionStatus> group in
                 drifted.GroupBy(s => s.Root, StringComparer.Ordinal))
        {
            err.WriteLine($"Remedy for {group.Key}: run `{RemedyCommandFor(group.Key)}`.");
        }
    }

    /// <summary>
    /// The <c>skills install</c> invocation whose destination is <paramref name="root"/> (issue #461),
    /// resolved against the SAME destinations the install command computes
    /// (<see cref="SkillsInstaller.ResolveTargetDir"/>) so the printed remedy cannot drift from where the
    /// command would actually write. The mapping itself lives in <see cref="SkillsInstallRemedy"/>.
    /// </summary>
    private static string RemedyCommandFor(string root) =>
        SkillsInstallRemedy.CommandFor(
            root,
            SkillsInstaller.ResolveTargetDir(target: null, project: false),
            SkillsInstaller.ResolveTargetDir(target: null, project: true));

    /// <summary>The bundled skills directory beside the entry assembly.</summary>
    private static string DefaultBundledSkillsDir() =>
        Path.Combine(AppContext.BaseDirectory, "skills");

    /// <summary>The two scan roots: user-level then project-level skills directories.</summary>
    private static IReadOnlyList<string> DefaultScanRoots()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Path.Combine(userProfile, ".claude", "skills"),
            Path.Combine(Directory.GetCurrentDirectory(), ".claude", "skills")
        };
    }
}
