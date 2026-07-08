namespace Guardrails.Integration.Tests;

/// <summary>
/// A one-task plan whose ACTION and GUARDRAIL are always ".sh" (bash-invoked) regardless of host OS,
/// proving the #263 forward-slash conversion runs through the REAL <c>TaskExecutor</c> attempt path —
/// <c>BuildEnvironment</c> / <c>BuildGuardrailEnvironment</c> — not just the re-verify seam
/// (<see cref="Core.Tests.ReVerifierSeamTests"/> covers that one separately).
/// <para>
/// On Windows this genuinely spawns Git Bash and proves the harness-set <c>GUARDRAILS_*</c> path env
/// vars carry no backslash. On Linux/macOS ".sh" is already the native choice, so the same fixture
/// runs unmodified there too — the "no backslash" assertion holds trivially off Windows (paths are
/// already forward-slash native), making this a genuine cross-platform regression guard.
/// </para>
/// </summary>
public sealed class BashOnlyEnvAssertingPlan : IDisposable
{
    private readonly string _root;

    public BashOnlyEnvAssertingPlan()
    {
        _root = Path.Combine(Path.GetTempPath(), "guardrails-bashenv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "guardrails.json"),
            """
            { "version": 1, "workspace": "." }
            """);

        string taskDir = Path.Combine(_root, "tasks", "01-bash-env-check");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """
            { "description": "bash-invoked GUARDRAILS_* env vars carry no backslash (#263)", "dependsOn": [] }
            """);

        WriteScript(Path.Combine(taskDir, "action.sh"), ActionScript);
        WriteScript(Path.Combine(taskDir, "guardrails", "01-check.sh"), GuardrailScript);
    }

    public string PlanDir => _root;

    // Action env (SSOT §5.1): PLAN_DIR / TASK_DIR / STATE_IN / LOG_DIR / WORKSPACE are all absolute
    // paths the harness itself builds in TaskExecutor.BuildEnvironment.
    private const string ActionScript =
        """
        #!/usr/bin/env bash
        for v in GUARDRAILS_PLAN_DIR GUARDRAILS_TASK_DIR GUARDRAILS_STATE_IN GUARDRAILS_LOG_DIR GUARDRAILS_WORKSPACE; do
          value="${!v}"
          if printf '%s' "$value" | grep -qF '\'; then
            echo "backslash found in action env $v: $value"
            exit 1
          fi
        done
        exit 0
        """;

    // Guardrail env (SSOT §5.1): the same harness-owned vars, plus the guardrail-only GUARDRAILS_ACTION_*
    // pointers built in TaskExecutor.BuildGuardrailEnvironment.
    private const string GuardrailScript =
        """
        #!/usr/bin/env bash
        for v in GUARDRAILS_PLAN_DIR GUARDRAILS_TASK_DIR GUARDRAILS_STATE_IN GUARDRAILS_LOG_DIR GUARDRAILS_WORKSPACE GUARDRAILS_ACTION_STDOUT GUARDRAILS_ACTION_STDERR GUARDRAILS_ACTION_RESULT; do
          value="${!v}"
          if printf '%s' "$value" | grep -qF '\'; then
            echo "backslash found in guardrail env $v: $value"
            exit 1
          fi
        done
        exit 0
        """;

    private static void WriteScript(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
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
            // best-effort cleanup
        }
    }
}
