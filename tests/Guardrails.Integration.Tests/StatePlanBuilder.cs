namespace Guardrails.Integration.Tests;

/// <summary>
/// Builds a real, runnable plan folder in a temp directory for M3 state/journal/resume
/// integration tests. Like <see cref="ScriptPlanBuilder"/> it emits OS-appropriate scripts
/// (PowerShell on Windows, bash elsewhere), but each task's action and guardrail bodies are
/// supplied as OS-specific snippets so a test can drive state writes, counters, and
/// deliberate failures. Bodies are written verbatim — the caller owns the shebang-free
/// snippet and we add the bash shebang.
/// </summary>
public sealed class StatePlanBuilder : IDisposable
{
    /// <summary>True when scripts should be PowerShell (Windows); false for bash.</summary>
    public static bool UsePowerShell => OperatingSystem.IsWindows();

    private readonly string _root;

    public StatePlanBuilder(string? seedJson = null)
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-m3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "."
            }
            """);
        Directory.CreateDirectory(Path.Combine(_root, "tasks"));

        if (seedJson is not null)
        {
            string stateDir = Path.Combine(_root, "state");
            Directory.CreateDirectory(stateDir);
            File.WriteAllText(Path.Combine(stateDir, "seed.json"), seedJson);
        }
    }

    /// <summary>Absolute path to the generated plan folder.</summary>
    public string PlanDir => _root;

    /// <summary>Absolute path to the runtime <c>state/state.json</c> (after a run).</summary>
    public string StateJsonPath => Path.Combine(_root, "state", "state.json");

    /// <summary>
    /// Add a task. <paramref name="actionBody"/> / <paramref name="guardrailBody"/> are the
    /// OS-appropriate script bodies (no shebang). Each defaults to a trivial success.
    /// </summary>
    public StatePlanBuilder AddTask(
        string id,
        string? actionBody = null,
        string? guardrailBody = null,
        params string[] dependsOn)
    {
        string taskDir = Path.Combine(_root, "tasks", id);
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        string dependsJson = dependsOn.Length == 0
            ? "[]"
            : "[" + string.Join(", ", dependsOn.Select(d => $"\"{d}\"")) + "]";

        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "M3 task {{id}}",
              "dependsOn": {{dependsJson}}
            }
            """);

        WriteScript(Path.Combine(taskDir, ActionFileName), actionBody ?? Succeed());
        WriteScript(Path.Combine(taskDir, "guardrails", GuardrailFileName), guardrailBody ?? Succeed());

        return this;
    }

    public static string ActionFileName => UsePowerShell ? "action.ps1" : "action.sh";
    public static string GuardrailFileName => UsePowerShell ? "01-check.ps1" : "01-check.sh";

    /// <summary>A body that simply exits 0.</summary>
    public static string Succeed() => UsePowerShell ? "exit 0" : "exit 0";

    /// <summary>A body that prints a reason and exits 1 (a deliberate failure).</summary>
    public static string Fail(string reason) => UsePowerShell
        ? $"Write-Output \"{reason}\"; exit 1"
        : $"echo \"{reason}\"; exit 1";

    private static void WriteScript(string path, string body)
    {
        string content = UsePowerShell ? body + "\n" : "#!/usr/bin/env bash\n" + body + "\n";
        File.WriteAllText(path, content);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    /// <summary>Overwrite a task's single guardrail body (used to "fix" a failing guardrail between runs).</summary>
    public void SetGuardrail(string taskId, string body)
    {
        string path = Path.Combine(_root, "tasks", taskId, "guardrails", GuardrailFileName);
        WriteScript(path, body);
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
            // best-effort cleanup
        }
    }
}
