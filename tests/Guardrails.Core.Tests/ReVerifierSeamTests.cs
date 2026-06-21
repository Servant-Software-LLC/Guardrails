using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// RED tests for plan 08 M4 §4.3 / feasibility-fix-2: encode the IReVerifier seam — the
/// new public, attempt-decoupled interface that runs a given guardrail set against arbitrary
/// worktree bytes, with NO dependence on an attempt logDir, attempt number, or action result
/// (SSOT §4.3, plan 08 feasibility-fix-2).
///
/// This file MUST fail to compile against pre-M4 code because IReVerifier, ReVerifyResult,
/// and GuardrailReVerifier do not yet exist. That compile failure IS the "fails on current
/// code" signal. Do NOT implement the seam here; implement M4 instead.
/// </summary>
public sealed class ReVerifierSeamTests : IDisposable
{
    // Temp root for guardrail script files. Not a git repo — IReVerifier operates
    // on arbitrary worktree bytes; no git repository is required for re-verify.
    private readonly string _tempRoot;

    public ReVerifierSeamTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "gr-rev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { }
    }

    // -------------------------------------------------------------------------
    // Factory — references GuardrailReVerifier (does not yet exist → compile error)
    // -------------------------------------------------------------------------

    private static IReVerifier CreateVerifier() =>
        new GuardrailReVerifier(new ProcessRunner(), new InterpreterMap(new PathExecutableProbe()));

    // -------------------------------------------------------------------------
    // Script helpers — platform-appropriate guardrail scripts
    // -------------------------------------------------------------------------

    private static string ScriptExtension => OperatingSystem.IsWindows() ? ".ps1" : ".sh";

    private static string MakePassScript() =>
        OperatingSystem.IsWindows()
            ? "exit 0"
            : "#!/usr/bin/env bash\nexit 0";

    private static string MakeFailScript(string reason) =>
        OperatingSystem.IsWindows()
            ? $"Write-Output '{reason}'; exit 1"
            : $"#!/usr/bin/env bash\necho '{reason}'\nexit 1";

    private GuardrailDefinition WriteGuardrailScript(string name, string scriptContent)
    {
        string path = Path.Combine(_tempRoot, name + ScriptExtension);
        File.WriteAllText(path, scriptContent);
        return new GuardrailDefinition { Name = name, Path = path, Kind = ActionKind.Script };
    }

    // =========================================================================
    // Passing guardrail set → pass result
    // =========================================================================

    [Fact]
    public async Task PassingGuardrailSet_ReturnsPass()
    {
        // IReVerifier must return a passing result when every guardrail in the set
        // exits 0 (deterministic pass/fail by exit code, SSOT §4).
        IReVerifier verifier = CreateVerifier();
        GuardrailDefinition g = WriteGuardrailScript("01-pass", MakePassScript());

        ReVerifyResult result = await verifier.ReVerifyAsync(
            _tempRoot,
            [g],
            TestContext.Current.CancellationToken);

        Assert.True(result.Passed,
            "A guardrail set where every guardrail passes must yield a passing ReVerifyResult");
        Assert.Empty(result.FailedGuardrails);
    }

    // =========================================================================
    // Failing guardrail set → fail result with the failing guardrail's output
    // =========================================================================

    [Fact]
    public async Task FailingGuardrailSet_ReturnsFail_WithFailingGuardrailOutput()
    {
        // IReVerifier must return a failing result that names the failing guardrail
        // and exposes its reason so callers can surface actionable failure feedback.
        const string failReason = "integration-build-failed";
        IReVerifier verifier = CreateVerifier();
        GuardrailDefinition g = WriteGuardrailScript("01-fail", MakeFailScript(failReason));

        ReVerifyResult result = await verifier.ReVerifyAsync(
            _tempRoot,
            [g],
            TestContext.Current.CancellationToken);

        Assert.False(result.Passed,
            "A guardrail set with a failing guardrail must yield a failing ReVerifyResult");
        GuardrailResult failed = Assert.Single(result.FailedGuardrails);
        Assert.Equal("01-fail", failed.Name);
        Assert.Contains(failReason, failed.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Attempt-decoupled: GUARDRAILS_ACTION_* env vars must NOT be injected
    // The scenarios-present guardrail greps for the exact method name below.
    // =========================================================================

    [Fact]
    public async Task ReVerify_DoesNotReadActionEnv()
    {
        // IReVerifier is attempt-decoupled (SSOT §4.3 / plan 08 feasibility-fix-2):
        // it runs guardrails against arbitrary union bytes where NO action ran. Therefore
        // it MUST NOT inject GUARDRAILS_ACTION_STDOUT, GUARDRAILS_ACTION_STDERR, or
        // GUARDRAILS_ACTION_RESULT into the child process environment — those variables
        // belong to the attempt lifecycle, not the re-verify context.
        //
        // The guardrail below asserts those vars are absent/empty. If IReVerifier wrongly
        // injects them, the guardrail exits non-zero → result.Passed == false → test fails.
        string actionEnvCheckScript = OperatingSystem.IsWindows()
            ? """
              if ($env:GUARDRAILS_ACTION_STDOUT -or $env:GUARDRAILS_ACTION_STDERR -or $env:GUARDRAILS_ACTION_RESULT) {
                  Write-Output 'GUARDRAILS_ACTION_ vars must be absent in re-verify context'
                  exit 1
              }
              exit 0
              """
            : """
              #!/usr/bin/env bash
              if [ -n "${GUARDRAILS_ACTION_STDOUT:-}" ] || [ -n "${GUARDRAILS_ACTION_STDERR:-}" ] || [ -n "${GUARDRAILS_ACTION_RESULT:-}" ]; then
                  echo 'GUARDRAILS_ACTION_ vars must be absent in re-verify context'
                  exit 1
              fi
              exit 0
              """;

        IReVerifier verifier = CreateVerifier();
        GuardrailDefinition g = WriteGuardrailScript("01-assert-no-action-env", actionEnvCheckScript);

        // No attempt logDir, no attempt number, no action result — pure re-verify context.
        ReVerifyResult result = await verifier.ReVerifyAsync(
            _tempRoot,
            [g],
            TestContext.Current.CancellationToken);

        Assert.True(result.Passed,
            "IReVerifier must not inject GUARDRAILS_ACTION_STDOUT/_STDERR/_RESULT — " +
            "those vars belong to the attempt lifecycle, not the re-verify context.");
    }

    // =========================================================================
    // #124: re-verify must set GUARDRAILS_WORKSPACE to the integration worktree
    // The scenarios-present guardrail greps for the exact method name below.
    // =========================================================================

    [Fact]
    public async Task ReVerify_SetsWorkspaceToWorktree()
    {
        // #124: a re-verify guardrail's effective workspace IS the integration worktree (its cwd),
        // so GUARDRAILS_WORKSPACE must point there — identical to the in-attempt contract where the
        // segment worktree is both cwd and GUARDRAILS_WORKSPACE (SSOT §5.1). A guardrail that
        // resolves files via $GUARDRAILS_WORKSPACE must see the SAME value in both contexts; if the
        // re-verifier left it unset, such a guardrail would silently misbehave at the union point.
        //
        // The guardrail below FAILS (exit 1) unless GUARDRAILS_WORKSPACE is set AND equals the
        // worktree path the re-verifier was given (_tempRoot here).
        string workspaceCheckScript = OperatingSystem.IsWindows()
            ? """
              if (-not $env:GUARDRAILS_WORKSPACE) {
                  Write-Output 'GUARDRAILS_WORKSPACE must be set in the re-verify context (#124)'
                  exit 1
              }
              $expected = (Resolve-Path $env:GR_EXPECTED_WORKSPACE).Path
              $actual   = (Resolve-Path $env:GUARDRAILS_WORKSPACE).Path
              if ($actual -ne $expected) {
                  Write-Output "GUARDRAILS_WORKSPACE '$actual' != expected worktree '$expected'"
                  exit 1
              }
              exit 0
              """
            : """
              #!/usr/bin/env bash
              if [ -z "${GUARDRAILS_WORKSPACE:-}" ]; then
                  echo 'GUARDRAILS_WORKSPACE must be set in the re-verify context (#124)'
                  exit 1
              fi
              actual="$(cd "$GUARDRAILS_WORKSPACE" && pwd -P)"
              expected="$(cd "$GR_EXPECTED_WORKSPACE" && pwd -P)"
              if [ "$actual" != "$expected" ]; then
                  echo "GUARDRAILS_WORKSPACE '$actual' != expected worktree '$expected'"
                  exit 1
              fi
              exit 0
              """;

        // The guardrail compares $GUARDRAILS_WORKSPACE against GR_EXPECTED_WORKSPACE; set the latter
        // in the parent env so the child inherits it (the re-verifier injects only its own vars).
        Environment.SetEnvironmentVariable("GR_EXPECTED_WORKSPACE", _tempRoot);
        try
        {
            IReVerifier verifier = CreateVerifier();
            GuardrailDefinition g = WriteGuardrailScript("01-assert-workspace-set", workspaceCheckScript);

            ReVerifyResult result = await verifier.ReVerifyAsync(
                _tempRoot,
                [g],
                TestContext.Current.CancellationToken);

            Assert.True(result.Passed,
                "IReVerifier must set GUARDRAILS_WORKSPACE to the integration worktree path (#124) " +
                "so the env contract is identical in-attempt and at re-verify. Failure detail: " +
                string.Join("; ", result.FailedGuardrails.Select(f => $"{f.Name}: {f.Reason}")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GR_EXPECTED_WORKSPACE", null);
        }
    }
}
