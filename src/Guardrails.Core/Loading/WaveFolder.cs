using System.Text.RegularExpressions;
using Guardrails.Core.Model;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Guardrails.Core.Loading;

/// <summary>
/// The ONE spelling of the wave-directory convention (SSOT §14.1, Open Decision F) plus the
/// <b>wave-target resolution</b> every verb that accepts a wave folder shares (issue #472).
///
/// <para>A wave folder is <b>not independently loadable</b>: in the nested layout it holds
/// <c>preflights/</c> + <c>guardrails/</c> + <c>tasks/</c> but deliberately no <c>guardrails.json</c> —
/// the config lives once at the plan root ("ONE shared run config", §14.1). So a verb pointed at a wave
/// must resolve it <b>through its parent plan</b>: load the one plan, then select the
/// <see cref="Model.WaveNode"/>. One loader, one <c>guardrails.json</c>, no second notion of a plan
/// (design <c>20-jit-breakdown-durability.md</c> §8.2).</para>
///
/// <para>Path-shape inference is acceptable here <b>only</b> because <see cref="DirectoryPattern"/> is
/// already load-bearing — wave DETECTION itself keys on it (§14.1) — so no new inference surface is
/// created. There is deliberately <b>one spelling</b>: the wave folder is the ordinary positional path
/// argument. A <c>--wave &lt;slug&gt;</c> flag was cut as KISS debt (design §7.2/C5).</para>
/// </summary>
public static class WaveFolder
{
    private const string ConfigFileName = "guardrails.json";

    /// <summary>
    /// The wave-directory convention (SSOT §14.1): <c>wave-</c>, a numeric prefix (group 1, load-bearing —
    /// it drives the strict total order, there is no <c>dependsOnWave</c> edge), a hyphen, then a kebab
    /// slug (group 2). Anchored. Shared by the loader's detection and by wave-target resolution so the two
    /// can never disagree about what a wave folder is.
    /// </summary>
    public static readonly Regex DirectoryPattern =
        new("^wave-([0-9]+)-([a-z0-9-]+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="name"/> is a conforming wave DIRECTORY NAME (not a path).</summary>
    public static bool IsWaveDirectoryName(string name) => DirectoryPattern.IsMatch(name);

    /// <summary>
    /// Resolve <paramref name="directory"/> as a WAVE target: true when it is an existing directory whose
    /// name matches <see cref="DirectoryPattern"/>, holds no <c>guardrails.json</c> of its own, and whose
    /// PARENT holds one — i.e. exactly the §14.1 nested shape. <paramref name="planRoot"/> receives the
    /// parent plan root and <paramref name="waveDir"/> the wave's directory name (its
    /// <see cref="Model.WaveNode.Dir"/>).
    ///
    /// <para>The design says "walk up to the nearest ancestor holding <c>guardrails.json</c> and require
    /// the target to be an immediate child": in the §14.1 layout a wave IS an immediate child, so the walk
    /// collapses to one level and is written as one level — a deeper target (a task folder, say) is not a
    /// wave and must keep failing exactly as it does today rather than being silently re-pointed at some
    /// ancestor plan.</para>
    ///
    /// <para>A directory that carries its own <c>guardrails.json</c> is a plan in its own right and is
    /// NEVER treated as a wave, so nesting a real plan inside another plan behaves as it always has.</para>
    /// </summary>
    public static bool TryResolveWaveTarget(string directory, out string planRoot, out string waveDir)
    {
        planRoot = string.Empty;
        waveDir = string.Empty;

        string full = Path.GetFullPath(directory);
        if (!Directory.Exists(full) || File.Exists(Path.Combine(full, ConfigFileName)))
        {
            return false;
        }

        string name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!IsWaveDirectoryName(name))
        {
            return false;
        }

        string? parent = Path.GetDirectoryName(full);
        if (parent is null || !File.Exists(Path.Combine(parent, ConfigFileName)))
        {
            return false;
        }

        planRoot = parent;
        waveDir = name;
        return true;
    }

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Parse the DECLARED <c>delivers</c> flag (design 39 §1b/§3) from <paramref name="waveDirectory"/>'s
    /// OPTIONAL <see cref="WaveNode.BriefFileName"/> YAML front matter — NOT a new per-wave manifest (SSOT
    /// §14.1 has none in v1). No <c>brief.md</c>, a brief with no front matter, a front matter with no
    /// <c>delivers</c> key, or malformed YAML all yield <c>false</c> — the never-weaker default an
    /// already-waved plan relies on (a garbled brief is treated as unset, not an error, the same lenient
    /// posture as <see cref="Prompts.SkillFrontmatter"/>).
    /// </summary>
    public static bool ReadDeliversFlag(string waveDirectory)
    {
        string briefPath = Path.Combine(waveDirectory, WaveNode.BriefFileName);
        if (!File.Exists(briefPath))
        {
            return false;
        }

        string? yaml = ExtractFrontMatterYaml(File.ReadAllText(briefPath));
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return false;
        }

        try
        {
            RawWaveBrief? raw = Yaml.Deserialize<RawWaveBrief>(yaml);
            return raw?.Delivers ?? false;
        }
        catch (YamlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Return the YAML between the leading <c>---</c> fences (the same convention as
    /// <see cref="Prompts.SkillFrontmatter"/>), or <c>null</c> if the content has no opening fence or no
    /// closing fence.
    /// </summary>
    private static string? ExtractFrontMatterYaml(string content)
    {
        string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalized.Split('\n');

        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return null;
        }

        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                return string.Join("\n", lines[1..i]);
            }
        }

        return null; // opening fence with no close
    }

    /// <summary>The slice of a wave brief's frontmatter we read: just the <c>delivers</c> key.</summary>
    private sealed class RawWaveBrief
    {
        public bool Delivers { get; set; }
    }
}
