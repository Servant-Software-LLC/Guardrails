namespace Guardrails.Integration.Tests;

/// <summary>
/// A one-task plan that asserts the child environment is <b>hermetic</b> for the harness-owned
/// <c>GUARDRAILS_*</c> namespace (SSOT §5.1, issue #442). The caller runs it from a parent process
/// that deliberately carries poisoned <c>GUARDRAILS_*</c> values — see
/// <see cref="HermeticChildEnvironmentTests"/>.
/// <para>
/// The distinction the scripts test is <b>unset</b> vs <b>empty</b>, and that is the whole point: a
/// <c>ProcessStartInfo.Environment</c> is a COPY of the harness's own block with the caller's overlay
/// merged on top, so before the fix a key the harness withheld (<c>GUARDRAILS_STATE_OUT</c> is removed
/// for guardrails by <c>TaskExecutor.BuildGuardrailEnvironment</c>) still arrived by inheritance. Shells
/// see an empty-but-set variable as SET (<c>[ -n "${V+x}" ]</c>, <c>Test-Path env:V</c>), so blanking is
/// not a fix either — the key has to be gone.
/// </para>
/// <para>
/// Both halves are asserted, because over-clearing would be its own bug: the ACTION must still receive
/// the <c>GUARDRAILS_STATE_OUT</c> the HARNESS declared (not the ambient one), the guardrail must still
/// receive its declared set, and non-<c>GUARDRAILS_*</c> inheritance (<c>PATH</c>) must survive intact —
/// a child action needs its toolchain.
/// </para>
/// </summary>
public sealed class HermeticEnvAssertingPlan : IDisposable
{
    private readonly string _root;

    /// <summary>
    /// An undeclared <c>GUARDRAILS_*</c> key the harness knows nothing about. Present in the parent's
    /// environment, it must reach NEITHER the action nor the guardrail — proving the sweep covers the
    /// whole namespace rather than the one key #442 happened to name.
    /// </summary>
    public const string PoisonVar = "GUARDRAILS_AMBIENT_POISON";

    /// <summary>
    /// The value the parent sets for <c>GUARDRAILS_STATE_OUT</c> and <see cref="PoisonVar"/>. Distinctive
    /// so the action can assert the value it sees is the HARNESS's declaration, not the inherited one.
    /// </summary>
    public const string PoisonValue = "gr442-ambient-poison";

    public HermeticEnvAssertingPlan()
    {
        bool windows = OperatingSystem.IsWindows();
        string action = windows ? WindowsAction : BashAction;
        string guardrail = windows ? WindowsGuardrail : BashGuardrail;

        // The scripts spell PoisonVar/PoisonValue literally — C# raw string literals give no brace
        // escape that survives shell `${...}` syntax, so interpolating them is not an option. Verify
        // the literals still agree with the constants rather than let a rename quietly turn these
        // assertions into no-ops (the exact "certifies something it never verified" shape this fixture
        // exists to catch).
        Require(action.Contains(PoisonVar, StringComparison.Ordinal), $"action script lost {PoisonVar}");
        Require(action.Contains(PoisonValue, StringComparison.Ordinal), $"action script lost {PoisonValue}");
        Require(guardrail.Contains(PoisonVar, StringComparison.Ordinal), $"guardrail script lost {PoisonVar}");

        _root = Path.Combine(Path.GetTempPath(), "guardrails-hermetic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "guardrails.json"),
            """
            { "version": 1, "workspace": "." }
            """);

        string taskDir = Path.Combine(_root, "tasks", "01-hermetic-env");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """
            { "description": "the child GUARDRAILS_* namespace is exactly what the harness declared (#442)", "dependsOn": [] }
            """);

        WriteScript(Path.Combine(taskDir, windows ? "action.ps1" : "action.sh"), action);
        WriteScript(Path.Combine(taskDir, "guardrails", windows ? "01-hermetic.ps1" : "01-hermetic.sh"), guardrail);
    }

    public string PlanDir => _root;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{nameof(HermeticEnvAssertingPlan)} drifted: {message}");
        }
    }

    private const string BashAction =
        """
        #!/usr/bin/env bash
        # The ACTION owns the state channel, so GUARDRAILS_STATE_OUT must be present — and must be the
        # harness's path, not the ambient value the parent process is carrying.
        if [ -z "${GUARDRAILS_STATE_OUT+x}" ]; then echo "action: GUARDRAILS_STATE_OUT missing"; exit 1; fi
        if [ "$GUARDRAILS_STATE_OUT" = "gr442-ambient-poison" ]; then echo "action: GUARDRAILS_STATE_OUT is the INHERITED value"; exit 1; fi
        # An undeclared GUARDRAILS_* key must not reach the action either.
        if [ -n "${GUARDRAILS_AMBIENT_POISON+x}" ]; then echo "action: inherited undeclared GUARDRAILS_AMBIENT_POISON=$GUARDRAILS_AMBIENT_POISON"; exit 1; fi
        # Non-harness inheritance is untouched.
        if [ -z "${PATH+x}" ]; then echo "action: PATH was swept away"; exit 1; fi
        exit 0
        """;

    private const string BashGuardrail =
        """
        #!/usr/bin/env bash
        # #442 acceptance: the guardrail must not see the action's state channel. UNSET, not empty.
        if [ -n "${GUARDRAILS_STATE_OUT+x}" ]; then echo "guardrail: saw GUARDRAILS_STATE_OUT=$GUARDRAILS_STATE_OUT"; exit 1; fi
        if [ -n "${GUARDRAILS_AMBIENT_POISON+x}" ]; then echo "guardrail: saw GUARDRAILS_AMBIENT_POISON=$GUARDRAILS_AMBIENT_POISON"; exit 1; fi
        # The declared set still arrives — the sweep clears, it does not over-clear.
        for v in GUARDRAILS_PLAN_DIR GUARDRAILS_TASK_ID GUARDRAILS_ACTION_STDOUT; do
          if [ -z "${!v}" ]; then echo "guardrail: missing declared $v"; exit 1; fi
        done
        if [ -z "${PATH+x}" ]; then echo "guardrail: PATH was swept away"; exit 1; fi
        exit 0
        """;

    private const string WindowsAction =
        """
        if (-not (Test-Path 'env:GUARDRAILS_STATE_OUT')) { Write-Output 'action: GUARDRAILS_STATE_OUT missing'; exit 1 }
        if ($env:GUARDRAILS_STATE_OUT -eq 'gr442-ambient-poison') { Write-Output 'action: GUARDRAILS_STATE_OUT is the INHERITED value'; exit 1 }
        if (Test-Path 'env:GUARDRAILS_AMBIENT_POISON') { Write-Output "action: inherited undeclared GUARDRAILS_AMBIENT_POISON=$env:GUARDRAILS_AMBIENT_POISON"; exit 1 }
        if (-not (Test-Path 'env:PATH')) { Write-Output 'action: PATH was swept away'; exit 1 }
        exit 0
        """;

    private const string WindowsGuardrail =
        """
        if (Test-Path 'env:GUARDRAILS_STATE_OUT') { Write-Output "guardrail: saw GUARDRAILS_STATE_OUT=$env:GUARDRAILS_STATE_OUT"; exit 1 }
        if (Test-Path 'env:GUARDRAILS_AMBIENT_POISON') { Write-Output "guardrail: saw GUARDRAILS_AMBIENT_POISON=$env:GUARDRAILS_AMBIENT_POISON"; exit 1 }
        foreach ($v in 'GUARDRAILS_PLAN_DIR','GUARDRAILS_TASK_ID','GUARDRAILS_ACTION_STDOUT') {
          if (-not (Test-Path "env:$v") -or [string]::IsNullOrEmpty((Get-Item "env:$v").Value)) {
            Write-Output "guardrail: missing declared $v"; exit 1
          }
        }
        if (-not (Test-Path 'env:PATH')) { Write-Output 'guardrail: PATH was swept away'; exit 1 }
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
