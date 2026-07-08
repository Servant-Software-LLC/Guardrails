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

    /// <summary>Add a wave EXIT-gate guardrail file at <c>&lt;waveDir&gt;/guardrails/&lt;name&gt;</c> (auto-prefixed with a <c>catches:</c> comment).</summary>
    public WavePlanBuilder WaveGuardrail(string waveDir, string name, string body)
    {
        string dir = Path.Combine(PlanDir, waveDir, "guardrails");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), "# catches: a wrong implementation\n" + body);
        return this;
    }

    /// <summary>Add a wave ENTRY-gate preflight file at <c>&lt;waveDir&gt;/preflights/&lt;name&gt;</c> (auto-prefixed with a <c>catches:</c> comment).</summary>
    public WavePlanBuilder WavePreflight(string waveDir, string name, string body)
    {
        string dir = Path.Combine(PlanDir, waveDir, "preflights");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), "# catches: a missing dependency\n" + body);
        return this;
    }

    public PlanLoadResult Load() => new PlanLoader().Load(PlanDir);

    private static void WriteTaskFolder(string taskDir, string folder, string[]? dependsOn, string? actionBody)
    {
        Directory.CreateDirectory(taskDir);

        string deps = dependsOn is { Length: > 0 }
            ? ", \"dependsOn\": [" + string.Join(", ", dependsOn.Select(d => $"\"{d}\"")) + "]"
            : string.Empty;
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "{{folder}}"{{deps}} }""");

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
