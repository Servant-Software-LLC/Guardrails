using Guardrails.Core.Io;

namespace Guardrails.Cli;

/// <summary>
/// Pure copy logic for <c>guardrails skills install</c>: copies each bundled skill folder
/// (a direct child directory of <c>sourceSkillsDir</c>) recursively into <c>targetDir</c>.
/// Kept free of console and CLI concerns so it can be unit-tested against a fake source base
/// dir without a packaged tool. The command layer (<see cref="Commands.SkillsCommand"/>) owns
/// resolving paths, printing, and exit codes.
///
/// The installed version is stamped INTO each installed <c>SKILL.md</c>'s frontmatter
/// (<c>metadata.guardrails-version</c>) at install time via
/// <see cref="SkillFrontmatterStamper"/>, using the running tool's own version. Stamping at
/// install (rather than at build) is what makes the version survive a <c>PackAsTool</c>
/// package: the packed payload is a fresh <c>dotnet publish</c> of the UNSTAMPED repo source,
/// so a build-time stamp never reaches the shipped <c>.nupkg</c> (issue #169) — but the
/// install command writes the version into the install target, which is what
/// <c>guardrails --version</c> later reads to detect drift (<see cref="SkillVersionReport"/>).
/// </summary>
public static class SkillsInstaller
{
    /// <summary>
    /// The legacy sidecar marker written by preview.26 (issue #152). Superseded by the
    /// frontmatter key (#156); a <c>--force</c> install deletes any leftover so it cannot
    /// linger and confuse a future reader.
    /// </summary>
    public const string LegacySidecarFileName = ".guardrails-skill-version";

    /// <summary>What happened to a single skill folder during an install.</summary>
    public enum SkillOutcome
    {
        /// <summary>Copied into the target (fresh, or overwritten under <c>force</c>).</summary>
        Installed,

        /// <summary>Already present in the target and left untouched (no <c>force</c>).</summary>
        Skipped
    }

    /// <summary>The per-skill result of an install pass.</summary>
    public sealed record SkillResult(string Name, SkillOutcome Outcome);

    /// <summary>
    /// Copy every bundled skill folder under <paramref name="sourceSkillsDir"/> into
    /// <paramref name="targetDir"/>. With <paramref name="force"/>, an existing target skill
    /// folder is replaced (and any leftover preview.26 <c>.guardrails-skill-version</c> sidecar
    /// removed); without it, an existing folder is left untouched and reported
    /// <see cref="SkillOutcome.Skipped"/>. Results are ordered by skill name (ordinal).
    ///
    /// Each INSTALLED skill's top-level <c>SKILL.md</c> frontmatter is then stamped with
    /// <paramref name="toolVersion"/> (under <c>metadata.guardrails-version</c>) via
    /// <see cref="SkillFrontmatterStamper"/>, so the installed copy carries the running tool's
    /// version even though the bundled/published source is unstamped (issue #169). A skill
    /// folder with no top-level <c>SKILL.md</c> is copied but not stamped. A SKIPPED skill is
    /// left untouched — its old or absent frontmatter version is precisely the stale-install
    /// signal <c>guardrails --version</c> surfaces.
    /// </summary>
    /// <param name="toolVersion">
    /// The version to stamp into each installed <c>SKILL.md</c> (normally
    /// <see cref="GuardrailsVersion.Current"/>; explicit so tests inject a known value).
    /// </param>
    /// <exception cref="DirectoryNotFoundException">
    /// <paramref name="sourceSkillsDir"/> does not exist.
    /// </exception>
    public static IReadOnlyList<SkillResult> InstallAll(
        string sourceSkillsDir, string targetDir, bool force, string toolVersion)
    {
        ArgumentNullException.ThrowIfNull(toolVersion);

        if (!Directory.Exists(sourceSkillsDir))
        {
            throw new DirectoryNotFoundException(
                $"Bundled skills directory not found: {sourceSkillsDir}");
        }

        Directory.CreateDirectory(targetDir);

        var results = new List<SkillResult>();

        foreach (string skillDir in BundledSkillDirs(sourceSkillsDir))
        {
            string name = Path.GetFileName(skillDir);
            string destination = Path.Combine(targetDir, name);

            if (Directory.Exists(destination) && !force)
            {
                results.Add(new SkillResult(name, SkillOutcome.Skipped));
                continue;
            }

            if (Directory.Exists(destination))
            {
                // Windows-safe (issue #109): a previously installed skill folder may contain a
                // read-only file; SafeDelete strips the attribute before deleting so a forced
                // reinstall never fails with Access Denied. This also removes any leftover
                // preview.26 .guardrails-skill-version sidecar (issue #156 migration), since the
                // whole stale folder is deleted before the fresh copy.
                SafeDelete.DeleteDirectory(destination);
            }

            CopyDirectory(skillDir, destination);
            StampInstalledSkillVersion(destination, toolVersion);
            results.Add(new SkillResult(name, SkillOutcome.Installed));
        }

        return results;
    }

    /// <summary>
    /// The bundled skill folders under <paramref name="sourceSkillsDir"/> — its direct child directories,
    /// ordinal-sorted so the install report (and anything derived from it) is stable across locales and
    /// filesystems. One definition, so <see cref="PlannedOverwrites"/> can never disagree with
    /// <see cref="InstallAll"/> about which folders an install touches.
    /// </summary>
    private static IEnumerable<string> BundledSkillDirs(string sourceSkillsDir) =>
        Directory.EnumerateDirectories(sourceSkillsDir).OrderBy(Path.GetFileName, StringComparer.Ordinal);

    /// <summary>
    /// The destination skill folders a <c>--force</c> install would DELETE and replace (issue #461): those
    /// that exist in <paramref name="targetDir"/> under a bundled skill's name. Without <c>--force</c> the
    /// same folders are skipped and nothing is destroyed, so this is the exact set a caller must vet before
    /// letting <c>--force</c> proceed. Absolute paths, ordinal order; empty when the target is fresh or
    /// <paramref name="sourceSkillsDir"/> does not exist.
    /// </summary>
    public static IReadOnlyList<string> PlannedOverwrites(string sourceSkillsDir, string targetDir)
    {
        if (!Directory.Exists(sourceSkillsDir))
        {
            return [];
        }

        return BundledSkillDirs(sourceSkillsDir)
            .Select(skillDir => Path.Combine(targetDir, Path.GetFileName(skillDir)))
            .Where(Directory.Exists)
            .ToList();
    }

    /// <summary>
    /// Stamp <paramref name="toolVersion"/> into the destination skill folder's top-level
    /// <c>SKILL.md</c> frontmatter (<c>metadata.guardrails-version</c>). If the folder has no
    /// top-level <c>SKILL.md</c>, there is nothing to stamp and the skill is left as copied.
    /// </summary>
    private static void StampInstalledSkillVersion(string destination, string toolVersion)
    {
        string skillMd = Path.Combine(destination, SkillVersionReport.SkillFileName);
        if (!File.Exists(skillMd))
        {
            return;
        }

        string original = File.ReadAllText(skillMd);
        string stamped = SkillFrontmatterStamper.Stamp(original, toolVersion);
        if (!string.Equals(stamped, original, StringComparison.Ordinal))
        {
            File.WriteAllText(skillMd, stamped);
        }
    }

    /// <summary>
    /// Resolve where skills should be installed, in precedence order:
    /// an explicit <paramref name="target"/> wins; else <paramref name="project"/> means
    /// <c>./.claude/skills</c> under the current directory (a repo-scoped install); else the
    /// default <c>~/.claude/skills</c> in the user home (available in every repo). The chosen
    /// directory is created by <see cref="InstallAll"/> if it does not yet exist.
    /// </summary>
    public static string ResolveTargetDir(string? target, bool project)
    {
        if (!string.IsNullOrWhiteSpace(target))
        {
            return target;
        }

        if (project)
        {
            return Path.Combine(Directory.GetCurrentDirectory(), ".claude", "skills");
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".claude", "skills");
    }

    /// <summary>Recursively copy <paramref name="source"/> into <paramref name="destination"/>.</summary>
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            string target = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
        }

        foreach (string subDir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(subDir, Path.Combine(destination, Path.GetFileName(subDir)));
        }
    }
}
