namespace Guardrails.Cli;

/// <summary>
/// Pure copy logic for <c>guardrails skills install</c>: copies each bundled skill folder
/// (a direct child directory of <c>sourceSkillsDir</c>) recursively into <c>targetDir</c>.
/// Kept free of console and CLI concerns so it can be unit-tested against a fake source base
/// dir without a packaged tool. The command layer (<see cref="Commands.SkillsCommand"/>) owns
/// resolving paths, printing, and exit codes.
/// </summary>
public static class SkillsInstaller
{
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
    /// folder is replaced; without it, an existing folder is left untouched and reported
    /// <see cref="SkillOutcome.Skipped"/>. Results are ordered by skill name (ordinal).
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">
    /// <paramref name="sourceSkillsDir"/> does not exist.
    /// </exception>
    public static IReadOnlyList<SkillResult> InstallAll(string sourceSkillsDir, string targetDir, bool force)
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
                Directory.Delete(destination, recursive: true);
            }

            CopyDirectory(skillDir, destination);
            results.Add(new SkillResult(name, SkillOutcome.Installed));
        }

        return results;
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
