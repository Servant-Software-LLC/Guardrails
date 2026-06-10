namespace Guardrails.Integration.Tests;

/// <summary>
/// A one-task plan whose guardrail fails unless all four M2 env vars (SSOT §5.1:
/// GUARDRAILS_PLAN_DIR / TASK_ID / TASK_DIR / ATTEMPT) are present in the child process.
/// </summary>
public sealed class EnvAssertingPlan : IDisposable
{
    private readonly string _root;

    public EnvAssertingPlan()
    {
        _root = Path.Combine(Path.GetTempPath(), "guardrails-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "guardrails.json"),
            """
            { "version": 1, "workspace": "." }
            """);

        string taskDir = Path.Combine(_root, "tasks", "01-env-check");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """
            { "description": "env contract check", "dependsOn": [] }
            """);

        bool windows = OperatingSystem.IsWindows();
        WriteScript(Path.Combine(taskDir, windows ? "action.ps1" : "action.sh"),
            windows ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n");
        WriteScript(Path.Combine(taskDir, "guardrails", windows ? "01-env.ps1" : "01-env.sh"),
            windows ? WindowsGuardrail : BashGuardrail);
    }

    public string PlanDir => _root;

    private const string BashGuardrail =
        """
        #!/usr/bin/env bash
        for v in GUARDRAILS_PLAN_DIR GUARDRAILS_TASK_ID GUARDRAILS_TASK_DIR GUARDRAILS_ATTEMPT; do
          if [ -z "${!v}" ]; then echo "missing env var $v"; exit 1; fi
        done
        if [ "$GUARDRAILS_TASK_ID" != "01-env-check" ]; then echo "wrong task id: $GUARDRAILS_TASK_ID"; exit 1; fi
        if [ "$GUARDRAILS_ATTEMPT" != "1" ]; then echo "wrong attempt: $GUARDRAILS_ATTEMPT"; exit 1; fi
        exit 0
        """;

    private const string WindowsGuardrail =
        """
        foreach ($v in 'GUARDRAILS_PLAN_DIR','GUARDRAILS_TASK_ID','GUARDRAILS_TASK_DIR','GUARDRAILS_ATTEMPT') {
          if (-not (Test-Path "env:$v") -or [string]::IsNullOrEmpty((Get-Item "env:$v").Value)) {
            Write-Output "missing env var $v"; exit 1
          }
        }
        if ($env:GUARDRAILS_TASK_ID -ne '01-env-check') { Write-Output "wrong task id: $env:GUARDRAILS_TASK_ID"; exit 1 }
        if ($env:GUARDRAILS_ATTEMPT -ne '1') { Write-Output "wrong attempt: $env:GUARDRAILS_ATTEMPT"; exit 1 }
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
