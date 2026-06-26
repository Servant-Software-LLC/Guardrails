using Guardrails.Core.Prompts;

namespace Guardrails.Cli;

/// <summary>
/// The drift status of one installed skill under one scan root: whether the
/// <c>metadata.guardrails-version</c> in its <c>SKILL.md</c> frontmatter matches the running
/// harness.
/// </summary>
/// <param name="Name">The skill folder name (e.g. <c>plan-breakdown</c>).</param>
/// <param name="Root">The scan root the skill folder was found under.</param>
/// <param name="InstalledVersion">
/// The frontmatter version, or <c>null</c> when the <c>SKILL.md</c> is absent or carries no
/// <c>metadata.guardrails-version</c> (an older install that predates frontmatter stamping —
/// e.g. a preview.26 sidecar install, which reads as <c>unversioned</c> until a
/// <c>--force</c> reinstall).
/// </param>
/// <param name="Drifted">
/// <c>true</c> when the version is missing or its normalised value differs from the harness.
/// </param>
public sealed record SkillVersionStatus(string Name, string Root, string? InstalledVersion, bool Drifted);

/// <summary>
/// Pure drift report behind the <c>guardrails --version</c> warning. For each known skill that
/// EXISTS as a folder under a scan root, reads <c>metadata.guardrails-version</c> from
/// <c>&lt;root&gt;/&lt;name&gt;/SKILL.md</c> and compares it to the harness version (both
/// normalised — <c>+build</c> metadata ignored). A skill absent from a root is not reported for
/// that root. The only filesystem touch is reading the <c>SKILL.md</c> (overridable via the
/// <c>readVersion</c> seam for unit tests); the comparison itself is free of console and walk
/// concerns.
/// </summary>
public static class SkillVersionReport
{
    /// <summary>The skill file whose frontmatter carries the version (issue #156).</summary>
    public const string SkillFileName = "SKILL.md";

    /// <summary>
    /// Build the drift report. <paramref name="knownSkillNames"/> are the skills the harness
    /// bundles; <paramref name="scanRoots"/> are the directories to look under (e.g.
    /// <c>~/.claude/skills</c> and <c>./.claude/skills</c>). Results are ordered by root (in the
    /// given order) then skill name (ordinal).
    /// </summary>
    /// <param name="readVersion">
    /// Optional seam reading a skill folder's frontmatter version, or <c>null</c> if absent.
    /// Defaults to reading <c>SKILL.md</c> from disk. Tests inject an in-memory map to avoid
    /// touching disk. The argument is the absolute skill-folder path.
    /// </param>
    public static IReadOnlyList<SkillVersionStatus> Build(
        string harnessVersion,
        IReadOnlyList<string> knownSkillNames,
        IReadOnlyList<string> scanRoots,
        Func<string, string?>? readVersion = null)
    {
        ArgumentNullException.ThrowIfNull(harnessVersion);
        ArgumentNullException.ThrowIfNull(knownSkillNames);
        ArgumentNullException.ThrowIfNull(scanRoots);

        readVersion ??= ReadVersionFromDisk;
        string normalizedHarness = GuardrailsVersion.Normalize(harnessVersion);

        var statuses = new List<SkillVersionStatus>();

        foreach (string root in scanRoots)
        {
            foreach (string name in knownSkillNames.OrderBy(n => n, StringComparer.Ordinal))
            {
                string skillDir = Path.Combine(root, name);
                if (!Directory.Exists(skillDir))
                {
                    // A skill absent from this root is not its problem — nothing installed.
                    continue;
                }

                string? installed = readVersion(skillDir);

                bool drifted = installed is null
                    || GuardrailsVersion.Normalize(installed) != normalizedHarness;

                statuses.Add(new SkillVersionStatus(name, root, installed, drifted));
            }
        }

        return statuses;
    }

    /// <summary>
    /// Read <c>metadata.guardrails-version</c> from the skill folder's <c>SKILL.md</c>
    /// frontmatter, or <c>null</c> if the file is absent or carries no version key.
    /// </summary>
    private static string? ReadVersionFromDisk(string skillDir)
    {
        string skillFile = Path.Combine(skillDir, SkillFileName);
        if (!File.Exists(skillFile))
        {
            return null;
        }

        return SkillFrontmatter.ReadGuardrailsVersion(File.ReadAllText(skillFile));
    }
}
