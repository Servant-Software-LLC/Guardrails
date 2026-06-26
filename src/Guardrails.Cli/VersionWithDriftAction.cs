using System.CommandLine;
using System.CommandLine.Invocation;

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
/// </summary>
public sealed class VersionWithDriftAction : SynchronousCommandLineAction
{
    private readonly IConsoleIo _io;
    private readonly string _harnessVersion;
    private readonly string _bundledSkillsDir;
    private readonly IReadOnlyList<string> _scanRoots;

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

        WriteWarningBlock(drifted);
    }

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
        err.WriteLine("Remedy: run `guardrails skills install --force`.");
    }

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
