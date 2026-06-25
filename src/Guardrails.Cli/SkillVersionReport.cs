namespace Guardrails.Cli;

/// <summary>
/// The drift status of one installed skill under one scan root: whether its
/// <c>.guardrails-skill-version</c> marker matches the running harness.
/// </summary>
/// <param name="Name">The skill folder name (e.g. <c>plan-breakdown</c>).</param>
/// <param name="Root">The scan root the skill folder was found under.</param>
/// <param name="InstalledVersion">
/// The trimmed marker content, or <c>null</c> if the marker file is absent (an older install
/// that predates version stamping).
/// </param>
/// <param name="Drifted">
/// <c>true</c> when the marker is missing or its normalised version differs from the harness.
/// </param>
public sealed record SkillVersionStatus(string Name, string Root, string? InstalledVersion, bool Drifted);

/// <summary>
/// Pure drift report behind the <c>guardrails --version</c> warning. For each known skill that
/// EXISTS as a folder under a scan root, reads <c>&lt;root&gt;/&lt;name&gt;/.guardrails-skill-version</c>
/// and compares it to the harness version (both normalised — <c>+build</c> metadata ignored).
/// A skill absent from a root is not reported for that root. The only filesystem touch is
/// reading the marker (overridable via the <c>readMarker</c> seam for unit tests); the
/// comparison itself is free of console and walk concerns.
/// </summary>
public static class SkillVersionReport
{
    /// <summary>The marker file written into an installed skill folder by the installer.</summary>
    public const string MarkerFileName = ".guardrails-skill-version";

    /// <summary>
    /// Build the drift report. <paramref name="knownSkillNames"/> are the skills the harness
    /// bundles; <paramref name="scanRoots"/> are the directories to look under (e.g.
    /// <c>~/.claude/skills</c> and <c>./.claude/skills</c>). Results are ordered by root (in the
    /// given order) then skill name (ordinal).
    /// </summary>
    /// <param name="readMarker">
    /// Optional seam reading a marker file's trimmed content, or <c>null</c> if absent. Defaults
    /// to a real file read. Tests inject an in-memory map to avoid touching disk.
    /// </param>
    public static IReadOnlyList<SkillVersionStatus> Build(
        string harnessVersion,
        IReadOnlyList<string> knownSkillNames,
        IReadOnlyList<string> scanRoots,
        Func<string, string?>? readMarker = null)
    {
        ArgumentNullException.ThrowIfNull(harnessVersion);
        ArgumentNullException.ThrowIfNull(knownSkillNames);
        ArgumentNullException.ThrowIfNull(scanRoots);

        readMarker ??= ReadMarkerFromDisk;
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

                string markerPath = Path.Combine(skillDir, MarkerFileName);
                string? installed = readMarker(markerPath);

                bool drifted = installed is null
                    || GuardrailsVersion.Normalize(installed) != normalizedHarness;

                statuses.Add(new SkillVersionStatus(name, root, installed, drifted));
            }
        }

        return statuses;
    }

    /// <summary>Read a marker file's trimmed content, or <c>null</c> if it does not exist.</summary>
    private static string? ReadMarkerFromDisk(string markerPath)
    {
        if (!File.Exists(markerPath))
        {
            return null;
        }

        return File.ReadAllText(markerPath).Trim();
    }
}
