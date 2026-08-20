using System.Text.RegularExpressions;

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
}
