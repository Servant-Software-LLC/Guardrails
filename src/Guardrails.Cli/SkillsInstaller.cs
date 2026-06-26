using Guardrails.Core.Io;

namespace Guardrails.Cli;

/// <summary>
/// Pure copy logic for <c>guardrails skills install</c>: copies each bundled skill folder
/// (a direct child directory of <c>sourceSkillsDir</c>) recursively into <c>targetDir</c>.
/// Kept free of console and CLI concerns so it can be unit-tested against a fake source base
/// dir without a packaged tool. The command layer (<see cref="Commands.SkillsCommand"/>) owns
/// resolving paths, printing, and exit codes.
///
/// The installed version is carried INSIDE each bundled <c>SKILL.md</c>'s frontmatter
/// (<c>metadata.guardrails-version</c>, injected at build by
/// <see cref="SkillFrontmatterStamper"/>), so install is a plain copy that preserves it — no
/// install-time mutation. <c>guardrails --version</c> later reads that frontmatter to detect
/// drift (<see cref="SkillVersionReport"/>).
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
    /// The version is intrinsic to the copied <c>SKILL.md</c> (its
    /// <c>metadata.guardrails-version</c> frontmatter, stamped at build), so a plain recursive
    /// copy carries it; a SKIPPED skill is left untouched — its old or absent frontmatter
    /// version is precisely the stale-install signal <c>guardrails --version</c> surfaces.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">
    /// <paramref name="sourceSkillsDir"/> does not exist.
    /// </exception>
    public static IReadOnlyList<SkillResult> InstallAll(
        string sourceSkillsDir, string targetDir, bool force)
    {
        if (!Directory.Exists(sourceSkillsDir))
        {
            throw new DirectoryNotFoundException(
                $"Bundled skills directory not found: {sourceSkillsDir}");
        }

        Directory.CreateDirectory(targetDir);

        var results = new List<SkillResult>();

        // Ordinal sort so the install report is stable across locales/filesystems.
        IEnumerable<string> skillDirs = Directory
            .EnumerateDirectories(sourceSkillsDir)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal);

        foreach (string skillDir in skillDirs)
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
            results.Add(new SkillResult(name, SkillOutcome.Installed));
        }

        return results;
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
