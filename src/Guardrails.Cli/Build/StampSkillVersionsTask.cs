// Build-only MSBuild task (issue #156): stamps metadata.guardrails-version into the BUNDLED
// copies of each SKILL.md in the build/publish output, so the version travels INSIDE the skill
// rather than in a sidecar. It is compiled by RoslynCodeTaskFactory at build time (see
// StampSkillVersions.targets) and is NEVER part of the Guardrails.Cli assembly compilation
// (the .csproj removes it from <Compile>), so the shipped tool carries no Microsoft.Build
// dependency.
//
// The per-file YAML transform is intentionally a VERBATIM copy of
// Guardrails.Cli.SkillFrontmatterStamper.Stamp (which is the unit-tested source of truth). Keep
// the two in sync: StampInjectionBuildTests.BuildOutput_MatchesHelperStamp_OverRepoSource asserts
// the REAL build output (written by this task) is byte-identical to the helper's output over the
// repo source, so any divergence between the two copies of the algorithm fails the suite.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

public sealed class StampSkillVersionsTask : Task
{
    /// <summary>The SKILL.md files (in the build output) to stamp.</summary>
    [Required]
    public ITaskItem[] SkillFiles { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The build version to write under metadata.guardrails-version.</summary>
    [Required]
    public string Version { get; set; } = string.Empty;

    public override bool Execute()
    {
        foreach (ITaskItem item in SkillFiles)
        {
            string path = item.ItemSpec;
            if (!File.Exists(path))
            {
                continue;
            }

            string original = File.ReadAllText(path);
            string stamped = Stamp(original, Version);
            if (!string.Equals(stamped, original, StringComparison.Ordinal))
            {
                File.WriteAllText(path, stamped);
            }

            Log.LogMessage(
                MessageImportance.Normal, $"Stamped guardrails-version={Version} into {path}");
        }

        return !Log.HasLoggedErrors;
    }

    // ---- VERBATIM copy of Guardrails.Cli.SkillFrontmatterStamper (keep in sync) ----------------

    private const string VersionKey = "guardrails-version";
    private const string MetadataKey = "metadata";

    private static string Stamp(string content, string version)
    {
        string newline = content.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
        string[] lines = content.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].EndsWith("\r", StringComparison.Ordinal))
            {
                lines[i] = lines[i].Substring(0, lines[i].Length - 1);
            }
        }

        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return content;
        }

        int closeFence = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                closeFence = i;
                break;
            }
        }

        if (closeFence < 0)
        {
            return content;
        }

        var frontmatter = new List<string>();
        for (int i = 1; i < closeFence; i++)
        {
            frontmatter.Add(lines[i]);
        }

        List<string> stamped = StampFrontmatterLines(frontmatter, version);

        var result = new List<string>(lines.Length + 2) { lines[0] };
        result.AddRange(stamped);
        result.Add(lines[closeFence]);
        for (int i = closeFence + 1; i < lines.Length; i++)
        {
            result.Add(lines[i]);
        }

        return string.Join(newline, result);
    }

    private static List<string> StampFrontmatterLines(List<string> frontmatter, string version)
    {
        int metadataLine = FindTopLevelKeyLine(frontmatter, MetadataKey);
        return metadataLine < 0
            ? AppendMetadataBlock(frontmatter, version)
            : SetChildUnderMetadata(frontmatter, metadataLine, version);
    }

    private static int FindTopLevelKeyLine(IReadOnlyList<string> lines, string key)
    {
        string prefix = key + ":";
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            string trimmed = line.TrimEnd();
            if (trimmed == key + ":" || line.StartsWith(prefix + " ", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static List<string> AppendMetadataBlock(List<string> frontmatter, string version)
    {
        var result = new List<string>(frontmatter)
        {
            $"{MetadataKey}:",
            $"  {VersionKey}: {version}"
        };
        return result;
    }

    private static List<string> SetChildUnderMetadata(
        List<string> frontmatter, int metadataLine, string version)
    {
        int firstChild = metadataLine + 1;
        int afterBlock = firstChild;
        while (afterBlock < frontmatter.Count)
        {
            string line = frontmatter[afterBlock];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                break;
            }

            afterBlock++;
        }

        for (int i = firstChild; i < afterBlock; i++)
        {
            string trimmed = frontmatter[i].TrimStart();
            if (trimmed.StartsWith(VersionKey + ":", StringComparison.Ordinal))
            {
                string indent = frontmatter[i].Substring(0, frontmatter[i].Length - trimmed.Length);
                var replaced = new List<string>(frontmatter);
                replaced[i] = $"{indent}{VersionKey}: {version}";
                return replaced;
            }
        }

        string childIndent = "  ";
        if (firstChild < afterBlock)
        {
            string firstChildLine = frontmatter[firstChild];
            string firstTrimmed = firstChildLine.TrimStart();
            childIndent = firstChildLine.Substring(0, firstChildLine.Length - firstTrimmed.Length);
        }

        var inserted = new List<string>(frontmatter);
        inserted.Insert(firstChild, $"{childIndent}{VersionKey}: {version}");
        return inserted;
    }
}
