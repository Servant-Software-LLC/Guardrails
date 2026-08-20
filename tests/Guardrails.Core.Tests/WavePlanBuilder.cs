using Guardrails.Core.Loading;

namespace Guardrails.Core.Tests;

/// <summary>
/// Builds a WAVED (or mixed/flat) plan folder on disk in a temp directory (git drops empty dirs, and the
/// nested-layout permutations are new, so on-disk construction is cleaner than committed fixtures — the
/// same pattern <see cref="StateManagerTests"/> and the empty-tasks loader test use). Defaults to
/// <c>maxParallelism: 1</c> so serial-mode validation does not require a git workspace (GR2015) or a
/// terminal integration gate (GR2028).
/// </summary>
internal sealed class WavePlanBuilder : IDisposable
{
    public string PlanDir { get; }

    public WavePlanBuilder(int maxParallelism = 1)
    {
        PlanDir = Path.Combine(Path.GetTempPath(), "gr-wave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(PlanDir);
        File.WriteAllText(Path.Combine(PlanDir, "guardrails.json"),
            $$"""{ "version": 1, "maxParallelism": {{maxParallelism}} }""");
    }

    /// <summary>Add a wave task at <c>&lt;waveDir&gt;/tasks/&lt;folder&gt;/</c> (task.json + action.sh + one guardrail).</summary>
    public WavePlanBuilder Task(string waveDir, string folder, string[]? dependsOn = null, string? actionBody = null)
    {
        string taskDir = Path.Combine(PlanDir, waveDir, "tasks", folder);
        WriteTaskFolder(taskDir, folder, dependsOn, actionBody);
        return this;
    }

    /// <summary>Add a FLAT root task at <c>tasks/&lt;folder&gt;/</c> (used to force a mixed layout).</summary>
    public WavePlanBuilder FlatTask(string folder, string[]? dependsOn = null)
    {
        string taskDir = Path.Combine(PlanDir, "tasks", folder);
        WriteTaskFolder(taskDir, folder, dependsOn, actionBody: null);
        return this;
    }

    /// <summary>Create a bare subdirectory at the plan root (e.g. a non-conforming sibling for GR2033).</summary>
    public WavePlanBuilder RootDir(string name)
    {
        Directory.CreateDirectory(Path.Combine(PlanDir, name));
        return this;
    }

    /// <summary>
    /// Create a not-yet-authored JIT wave STUB: the wave folder with an empty <c>tasks/</c> and nothing else
    /// (SSOT §14.4 — it loads as zero tasks with no error, and the between-wave checkpoint breaks it down).
    /// </summary>
    public WavePlanBuilder WaveStub(string waveDir)
    {
        Directory.CreateDirectory(Path.Combine(PlanDir, waveDir, "tasks"));
        return this;
    }

    /// <summary>
    /// Write the wave's OPTIONAL human-authored <c>brief.md</c> (SSOT §14.10) — breakdown INPUT, excluded
    /// from <see cref="Journal.PlanDefinitionHash"/> but FOLDED into <see cref="Journal.WaveDefinitionHash"/>.
    /// </summary>
    public WavePlanBuilder WaveBrief(string waveDir, string text)
    {
        string dir = Path.Combine(PlanDir, waveDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, Model.WaveNode.BriefFileName), text);
        return this;
    }

    /// <summary>Overwrite an existing wave EXIT-gate guardrail body (a post-review edit to reviewed content).</summary>
    public WavePlanBuilder EditWaveGuardrail(string waveDir, string name, string body)
    {
        File.WriteAllText(Path.Combine(PlanDir, waveDir, "guardrails", name), "# catches: a wrong implementation\n" + body);
        return this;
    }

    /// <summary>Overwrite the shared plan-root <c>guardrails.json</c> (Open Decision C's config edit).</summary>
    public WavePlanBuilder EditConfig(string json)
    {
        File.WriteAllText(Path.Combine(PlanDir, "guardrails.json"), json);
        return this;
    }

    /// <summary>
    /// Add a wave EXIT-gate guardrail file at <c>&lt;waveDir&gt;/guardrails/&lt;name&gt;</c> (auto-prefixed
    /// with a <c>catches:</c> comment), optionally with its §4.1 metadata sidecar (<c>&lt;name&gt;.json</c>).
    /// </summary>
    public WavePlanBuilder WaveGuardrail(string waveDir, string name, string body, string? sidecarJson = null)
    {
        string dir = Path.Combine(PlanDir, waveDir, "guardrails");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), "# catches: a wrong implementation\n" + body);
        WriteSidecar(dir, name, sidecarJson);
        return this;
    }

    /// <summary>
    /// Add a wave ENTRY-gate preflight file at <c>&lt;waveDir&gt;/preflights/&lt;name&gt;</c> (auto-prefixed
    /// with a <c>catches:</c> comment), optionally with its §4.1 metadata sidecar.
    /// </summary>
    public WavePlanBuilder WavePreflight(string waveDir, string name, string body, string? sidecarJson = null)
    {
        string dir = Path.Combine(PlanDir, waveDir, "preflights");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), "# catches: a missing dependency\n" + body);
        WriteSidecar(dir, name, sidecarJson);
        return this;
    }

    /// <summary>
    /// Add a PLAN-ROOT terminal-gate guardrail at <c>&lt;plan&gt;/guardrails/&lt;name&gt;</c> (SSOT §3.3;
    /// optional-additive on a waved plan, §14.3), optionally with its §4.1 metadata sidecar. The negative
    /// control for GR2059: this is the position where <c>scope:"integration"</c> DOES take effect.
    /// </summary>
    public WavePlanBuilder PlanGuardrail(string name, string body, string? sidecarJson = null)
    {
        string dir = Path.Combine(PlanDir, "guardrails");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), "# catches: a wrong implementation\n" + body);
        WriteSidecar(dir, name, sidecarJson);
        return this;
    }

    /// <summary>
    /// Attach a §4.1 metadata sidecar to a wave TASK's default <c>01-ok.sh</c> guardrail — the other
    /// position where <c>scope:"integration"</c> takes effect, so GR2059 must stay silent there.
    /// </summary>
    public WavePlanBuilder WaveTaskGuardrailSidecar(string waveDir, string folder, string sidecarJson)
    {
        WriteSidecar(Path.Combine(PlanDir, waveDir, "tasks", folder, "guardrails"), "01-ok.sh", sidecarJson);
        return this;
    }

    private static void WriteSidecar(string dir, string guardrailFileName, string? sidecarJson)
    {
        if (sidecarJson is null)
        {
            return;
        }

        File.WriteAllText(
            Path.Combine(dir, Path.GetFileNameWithoutExtension(guardrailFileName) + ".json"),
            sidecarJson);
    }

    public PlanLoadResult Load() => new PlanLoader().Load(PlanDir);

    private static void WriteTaskFolder(string taskDir, string folder, string[]? dependsOn, string? actionBody)
    {
        Directory.CreateDirectory(taskDir);

        string deps = dependsOn is { Length: > 0 }
            ? ", \"dependsOn\": [" + string.Join(", ", dependsOn.Select(d => $"\"{d}\"")) + "]"
            : string.Empty;
        // #389: writeScope is REQUIRED on every task; the default action `echo hi` writes nothing → [].
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "{{folder}}", "writeScope": []{{deps}} }""");

        File.WriteAllText(Path.Combine(taskDir, "action.sh"), actionBody ?? "#!/bin/sh\necho hi\n");

        string guardrailsDir = Path.Combine(taskDir, "guardrails");
        Directory.CreateDirectory(guardrailsDir);
        File.WriteAllText(Path.Combine(guardrailsDir, "01-ok.sh"), "#!/bin/sh\nexit 0\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(PlanDir, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }
}
