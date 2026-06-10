using System.Text;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Builds a real, runnable script-only plan folder in a temp directory for integration
/// tests. Emits OS-appropriate scripts — PowerShell on Windows (pwsh/powershell are
/// always present there), bash elsewhere (always present on the Linux/macOS runners) —
/// selected by <see cref="OperatingSystem.IsWindows"/>, so the same test genuinely runs
/// cross-platform.
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

    private static string ActionFileName => UsePowerShell ? "action.ps1" : "action.sh";
    private static string GuardrailFileName => UsePowerShell ? "01-check.ps1" : "01-check.sh";

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
}
