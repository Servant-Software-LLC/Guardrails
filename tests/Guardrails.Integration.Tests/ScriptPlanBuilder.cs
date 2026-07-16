using System.Text;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Builds a real, runnable script-only plan folder in a temp directory for integration
/// tests. Emits OS-appropriate scripts — PowerShell on Windows (pwsh/powershell are
/// always present there), bash elsewhere (always present on the Linux/macOS runners) —
/// selected by <see cref="OperatingSystem.IsWindows"/>, so the same test genuinely runs
/// cross-platform.
/// <para>
/// Supports both flat plans (via <see cref="AddTask"/>) and waved plans (via
/// <see cref="AddWave"/>). The two modes are mutually exclusive: call <see cref="AddWave"/>
/// before any root <see cref="AddTask"/> calls, or use only <see cref="AddTask"/> for a
/// flat plan.
/// </para>
/// </summary>
public sealed class ScriptPlanBuilder : IDisposable
{
    private static readonly bool UsePowerShell = OperatingSystem.IsWindows();

    private readonly string _root;
    private readonly List<string> _taskFolders = [];

    public ScriptPlanBuilder()
    {
        _root = Path.Combine(Path.GetTempPath(), "guardrails-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        // defaultRetries 0: these fixtures assert single-attempt semantics exactly.
        File.WriteAllText(Path.Combine(_root, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": ".",
              "defaultRetries": 0,
              "maxParallelism": 1
            }
            """);
        Directory.CreateDirectory(Path.Combine(_root, "tasks"));
    }

    /// <summary>Absolute path to the generated plan folder (pass this to the CLI / executor).</summary>
    public string PlanDir => _root;

    /// <summary>
    /// Add a task. <paramref name="actionSucceeds"/> controls the action exit code;
    /// <paramref name="guardrailPasses"/> the single guardrail's verdict.
    /// </summary>
    public ScriptPlanBuilder AddTask(
        string id,
        bool actionSucceeds = true,
        bool guardrailPasses = true,
        params string[] dependsOn)
    {
        string taskDir = Path.Combine(_root, "tasks", id);
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        _taskFolders.Add(taskDir);

        string dependsJson = dependsOn.Length == 0
            ? "[]"
            : "[" + string.Join(", ", dependsOn.Select(d => $"\"{d}\"")) + "]";

        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "Integration task {{id}}",
              "dependsOn": {{dependsJson}}
            }
            """);

        WriteScript(Path.Combine(taskDir, ActionFileName), Body(actionSucceeds, "action"));
        WriteScript(Path.Combine(taskDir, "guardrails", GuardrailFileName), Body(guardrailPasses, "guardrail"));

        return this;
    }

    /// <summary>
    /// Switch this builder to WAVED mode and add a wave. Must be called BEFORE any root
    /// <see cref="AddTask"/> calls (a waved plan has no root <c>tasks/</c> directory).
    /// Returns a wave-scoped <see cref="WaveBuilder"/> for adding tasks to that wave.
    /// </summary>
    public WaveBuilder AddWave(string waveDirName)
    {
        // The constructor always creates a root tasks/ dir for the flat-plan case. Remove it on
        // the first AddWave call (waved plans must not have a root tasks/ dir — GR2032). Guard
        // with _taskFolders.Any() so a second AddWave call (dir already gone) is safe.
        string rootTasks = Path.Combine(_root, "tasks");
        if (Directory.Exists(rootTasks) && !_taskFolders.Any())
            Directory.Delete(rootTasks);

        string waveDir = Path.Combine(_root, waveDirName);
        Directory.CreateDirectory(waveDir);
        Directory.CreateDirectory(Path.Combine(waveDir, "tasks"));
        return new WaveBuilder(this, waveDir);
    }

    /// <summary>
    /// Add a task inside a wave folder. Called by <see cref="WaveBuilder.AddTask"/>; not part
    /// of the public flat-plan API. Writes a <c>task.json</c> with the wave-qualified
    /// <paramref name="qualifiedDepsOn"/> array plus an action and a guardrail script.
    /// </summary>
    internal void AddWaveTask(
        string waveDir,
        string id,
        bool actionSucceeds,
        bool guardrailPasses,
        string[] qualifiedDepsOn)
    {
        string taskDir = Path.Combine(waveDir, "tasks", id);
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        _taskFolders.Add(taskDir);

        string waveName = Path.GetFileName(waveDir);
        string qualifiedId = $"{waveName}/{id}";

        string dependsJson = qualifiedDepsOn.Length == 0
            ? "[]"
            : "[" + string.Join(", ", qualifiedDepsOn.Select(d => $"\"{d}\"")) + "]";

        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "Wave task {{qualifiedId}}",
              "dependsOn": {{dependsJson}}
            }
            """);

        WriteScript(Path.Combine(taskDir, ActionFileName), Body(actionSucceeds, "action"));
        WriteScript(Path.Combine(taskDir, "guardrails", GuardrailFileName), Body(guardrailPasses, "guardrail"));
    }

    private static string ActionFileName => UsePowerShell ? "action.ps1" : "action.sh";
    private static string GuardrailFileName => UsePowerShell ? "01-check.ps1" : "01-check.sh";

    /// <summary>Absolute path to a task's action file (OS-appropriate extension).</summary>
    public string ActionPath(string taskId) => Path.Combine(_root, "tasks", taskId, ActionFileName);

    /// <summary>Absolute path to a task's single (<c>01-check</c>) guardrail file.</summary>
    public string GuardrailPath(string taskId) =>
        Path.Combine(_root, "tasks", taskId, "guardrails", GuardrailFileName);

    /// <summary>
    /// Rewrite a task's guardrail body to a byte-different but still-passing script — the issue's
    /// "weaken a guardrail after review" edit (e.g. dropping a real check for a bare <c>exit 0</c>).
    /// Different bytes, so it must re-stale the review marker (issue #260).
    /// </summary>
    public void WeakenGuardrail(string taskId) =>
        WriteScript(GuardrailPath(taskId), Body(succeeds: true, "weakened guardrail"));

    private static string Body(bool succeeds, string label)
    {
        if (UsePowerShell)
        {
            var sb = new StringBuilder();
            if (!succeeds)
            {
                sb.AppendLine($"Write-Output \"{label} failed deliberately\"");
                sb.AppendLine("exit 1");
            }
            else
            {
                sb.AppendLine($"Write-Output \"{label} ok\"");
                sb.AppendLine("exit 0");
            }
            return sb.ToString();
        }

        var bash = new StringBuilder();
        bash.AppendLine("#!/usr/bin/env bash");
        if (!succeeds)
        {
            bash.AppendLine($"echo \"{label} failed deliberately\"");
            bash.AppendLine("exit 1");
        }
        else
        {
            bash.AppendLine($"echo \"{label} ok\"");
            bash.AppendLine("exit 0");
        }
        return bash.ToString();
    }

    private static void WriteScript(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            // Make the .sh executable bit irrelevant (we spawn via bash), but harmless to set.
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file should not fail the test.
        }
    }

    // -----------------------------------------------------------------------------------------
    // Wave support
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A builder scoped to a single wave folder, returned by <see cref="ScriptPlanBuilder.AddWave"/>.
    /// Add tasks to the wave with <see cref="AddTask"/>; use <see cref="WaveDir"/> to get the
    /// wave folder's absolute path (the target for <c>guardrails graph</c> in wave-scoped tests).
    /// </summary>
    public sealed class WaveBuilder
    {
        private readonly ScriptPlanBuilder _parent;
        private readonly string _waveDir;

        internal WaveBuilder(ScriptPlanBuilder parent, string waveDir)
        {
            _parent = parent;
            _waveDir = waveDir;
        }

        /// <summary>Absolute path to this wave's folder.</summary>
        public string WaveDir => _waveDir;

        /// <summary>
        /// Add a task to this wave. Dependencies that already contain a <c>/</c> are passed
        /// through unchanged (already wave-qualified, SSOT §14.2); bare folder names are
        /// automatically prefixed with this wave's directory name.
        /// </summary>
        public WaveBuilder AddTask(
            string id,
            bool actionSucceeds = true,
            bool guardrailPasses = true,
            params string[] dependsOn)
        {
            string waveName = Path.GetFileName(_waveDir);
            string[] qualifiedDeps = dependsOn
                .Select(d => d.Contains('/') ? d : $"{waveName}/{d}")
                .ToArray();
            _parent.AddWaveTask(_waveDir, id, actionSucceeds, guardrailPasses, qualifiedDeps);
            return this;
        }
    }
}
