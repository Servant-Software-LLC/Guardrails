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
    // #272 Part 1: a failing plan-level gate's reason must carry the TAIL of stdout
    // (the #179-style re-emitted failure detail), NOT the preamble FIRST line — this
    // is the ONLY signal an operator gets (a plan gate never retries / composes feedback).
    // The re-verify seam funnels BOTH plan-level gates (PlanPreflightPhase +
    // PlanGuardrailPhase), so pinning it here covers both.
    // =========================================================================

    [Fact]
    public async Task FailingGuardrail_Reason_CarriesTail_NotPreambleFirstLine()
    {
        const string preamble = "added 464 packages, and audited 465 packages in 24s";
        const string tailDetail = "FAIL  dsl-tools/dfd.test.ts > round-trips the DSL";
        const string tailSummary = "vitest suite is not green at the terminal gate";

        // Realistic shape: an npm-ci preamble line, then a FULL test run (many PASS lines) so the preamble is
        // far from the end, then the #179-style re-emitted failure block at the very END. The reason is the
        // TAIL, so the preamble genuinely scrolls off and the re-emitted detail lands.
        string emit(string line) => OperatingSystem.IsWindows() ? $"Write-Output '{line}'\n" : $"echo '{line}'\n";
        var body = new System.Text.StringBuilder();
        if (!OperatingSystem.IsWindows())
        {
            body.Append("#!/usr/bin/env bash\n");
        }
        body.Append(emit(preamble));
        for (int i = 1; i <= 20; i++)
        {
            body.Append(emit($"PASS  dsl-tools/case-{i:00}.test.ts"));
        }
        body.Append(emit("=== Failure details (re-emitted at the end, #179) ==="));
        body.Append(emit(tailDetail));
        body.Append(emit(tailSummary));
        body.Append("exit 1");

        IReVerifier verifier = CreateVerifier();
        GuardrailDefinition g = WriteGuardrailScript("01-vitest-suite", body.ToString());

        ReVerifyResult result = await verifier.ReVerifyAsync(
            _tempRoot, [g], TestContext.Current.CancellationToken);

        GuardrailResult failed = Assert.Single(result.FailedGuardrails);
        string reason = failed.Reason ?? string.Empty;

        // The re-emitted failure detail at the END is carried (both the FAIL line and the summary line)...
        Assert.Contains(tailDetail, reason, StringComparison.Ordinal);
        Assert.Contains(tailSummary, reason, StringComparison.Ordinal);
        // ...and the npm-ci preamble FIRST line (the pre-#272 mis-reported "reason") is NOT surfaced.
        Assert.DoesNotContain(preamble, reason, StringComparison.Ordinal);
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

    // =========================================================================
    // #173: adversarial-but-valid quoting must run through the REAL script path
    // (ScriptUnitRunner → InterpreterMap → ProcessRunner) with NO parser error.
    //
    // The reported symptom — a single-quoted string with embedded double-quotes
    // (e.g. '"test-commander-rest"') raising pwsh "Missing closing '}'" — can only
    // arise when the script CONTENT is fed through a -Command / shell-string layer
    // that strips the outer single quotes. The harness never does that: it invokes
    // `pwsh -NoProfile -ExecutionPolicy Bypass -File <path>` via ArgumentList (SSOT
    // §5.1/§5.2), so pwsh parses the file's own bytes. These tests LOCK that contract:
    // a guardrail whose body contains '"..."' (and other tricky-but-valid quoting)
    // must execute correctly — expected exit code, expected stdout, no parser error —
    // through the same ScriptUnitRunner the in-attempt and re-verify paths share.
    // =========================================================================

    [Theory]
    [MemberData(nameof(AdversarialQuotingCases))]
    public async Task AdversarialQuoting_RunsThroughRealScriptPath_NoParserError(
        string caseName, string windowsBody, string posixBody, bool expectPass, string expectedStdoutToken)
    {
        // The exact #173 reproduction lives in the first case: a single-quoted regex
        // literal containing embedded double-quotes, matched against content that does
        // NOT contain it, so the guardrail takes the `if {...}` branch (exit 1) — the
        // branch whose opening `{` the reporter's parser error pointed at.
        IReVerifier verifier = CreateVerifier();
        GuardrailDefinition g = WriteGuardrailScript(
            "01-" + caseName, OperatingSystem.IsWindows() ? windowsBody : posixBody);

        ReVerifyResult result = await verifier.ReVerifyAsync(
            _tempRoot, [g], TestContext.Current.CancellationToken);

        // A parser error would surface as a non-zero exit with the script never producing
        // its own stdout token — so asserting BOTH the pass/fail verdict AND the expected
        // stdout token proves the script genuinely PARSED and RAN, not that it merely failed.
        GuardrailResult? failure = result.FailedGuardrails.SingleOrDefault();

        Assert.True(
            result.Passed == expectPass,
            $"case '{caseName}': expected Passed={expectPass} but the guardrail " +
            (result.Passed
                ? "passed unexpectedly"
                : $"failed: {failure?.Reason}") +
            ". A pwsh ParserError (#173) would show as an unexpected failure here.");

        // The reason a failing guardrail surfaces is the TAIL of its stdout (#272 Part 1); each case
        // here prints a single marker line, so that line IS the tail. A passing one we re-run to capture
        // stdout would be redundant — instead the failing cases assert their printed token, proving the
        // body parsed and reached its Write-Output.
        if (!expectPass)
        {
            Assert.NotNull(failure);
            Assert.Contains(expectedStdoutToken, failure!.Reason ?? string.Empty, StringComparison.Ordinal);
            // Belt-and-braces: the classic #173 mis-parse renders as "Missing closing"; assert
            // that phrase never leaks into the failure reason for ANY case.
            Assert.DoesNotContain("Missing closing", failure.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    // =========================================================================
    // #263: a bash-invoked (.sh) guardrail must receive GUARDRAILS_WORKSPACE without any backslash —
    // on Windows the harness converts the native backslash absolute path to forward-slash form before
    // launch (WindowsBashPaths, gated in ScriptUnitRunner); off Windows the path is already
    // forward-slash native, so the assertion holds unconditionally and this test genuinely runs
    // cross-platform through the REAL production path (GuardrailReVerifier → ScriptUnitRunner →
    // InterpreterMap → ProcessRunner) — the exact seam the issue's bash-guardrail bug lived in.
    // =========================================================================

    [Fact]
    public async Task ShGuardrail_ReceivesWorkspaceWithoutBackslashes()
    {
        const string noBackslashCheck =
            """
            #!/usr/bin/env bash
            if printf '%s' "$GUARDRAILS_WORKSPACE" | grep -qF '\'; then
                echo "backslash found in GUARDRAILS_WORKSPACE: $GUARDRAILS_WORKSPACE"
                exit 1
            fi
            exit 0
            """;

        IReVerifier verifier = CreateVerifier();
        GuardrailDefinition g = new()
        {
            Name = "01-no-backslash",
            Path = Path.Combine(_tempRoot, "01-no-backslash.sh"),
            Kind = ActionKind.Script
        };
        File.WriteAllText(g.Path, noBackslashCheck);

        ReVerifyResult result = await verifier.ReVerifyAsync(
            _tempRoot, [g], TestContext.Current.CancellationToken);

        Assert.True(result.Passed,
            "GUARDRAILS_WORKSPACE handed to a bash-invoked (.sh) guardrail must never contain a " +
            "backslash (#263) — a guardrail that interpolates it into an escape-sensitive context " +
            "(node -e, a regex, sed/awk) would otherwise have the path silently corrupted. " +
            string.Join("; ", result.FailedGuardrails.Select(f => $"{f.Name}: {f.Reason}")));
    }

    [Fact]
    public async Task Ps1Guardrail_OnWindows_KeepsNativeBackslashWorkspace()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // A native-backslash assertion is only meaningful on Windows.
        }

        // #263 negative control: the forward-slash conversion is scoped to BASH invocations only — a
        // PowerShell (.ps1) guardrail, which is backslash-native, must see the workspace UNCHANGED.
        const string keepsBackslashCheck =
            """
            if ($env:GUARDRAILS_WORKSPACE -notlike '*\*') {
                Write-Output "GUARDRAILS_WORKSPACE was unexpectedly forward-slashed: $env:GUARDRAILS_WORKSPACE"
                exit 1
            }
            exit 0
            """;

        IReVerifier verifier = CreateVerifier();
        GuardrailDefinition g = WriteGuardrailScript("01-native-backslash", keepsBackslashCheck);

        ReVerifyResult result = await verifier.ReVerifyAsync(
            _tempRoot, [g], TestContext.Current.CancellationToken);

        Assert.True(result.Passed,
            "A PowerShell guardrail must keep the native backslash GUARDRAILS_WORKSPACE form (#263) — " +
            "the forward-slash conversion is scoped to bash invocations only. " +
            string.Join("; ", result.FailedGuardrails.Select(f => $"{f.Name}: {f.Reason}")));
    }

    public static TheoryData<string, string, string, bool, string> AdversarialQuotingCases() => new()
    {
        // Case 1 — the exact #173 shape: single-quoted regex with embedded double-quotes,
        // taking the if-block (the `{` the reporter's caret pointed at). Content lacks the
        // token, so -notmatch is true → exit 1, printing the marker.
        {
            "embedded-double-quotes",
            "$content = 'route(rest)'\n" +
            "if ($content -notmatch '\"test-commander-rest\"') {\n" +
            "    Write-Output 'MISSING_PROBE'\n" +
            "    exit 1\n" +
            "}\n" +
            "Write-Output 'PRESENT'\n" +
            "exit 0\n",
            "#!/usr/bin/env bash\n" +
            "content='route(rest)'\n" +
            "if ! printf '%s' \"$content\" | grep -q '\"test-commander-rest\"'; then\n" +
            "    echo 'MISSING_PROBE'\n" +
            "    exit 1\n" +
            "fi\n" +
            "echo 'PRESENT'\n" +
            "exit 0\n",
            /* expectPass */ false,
            /* expectedStdoutToken */ "MISSING_PROBE"
        },
        // Case 2 — single-quoted string mixing a double-quote AND a brace character inside
        // the literal, again entering an if-block. A naive quote-stripping layer would let
        // the `}` inside the literal terminate the block early.
        {
            "quote-and-brace-in-literal",
            "if ('x' -ne 'a\"}b') {\n" +
            "    Write-Output 'TOOK_BRANCH'\n" +
            "    exit 1\n" +
            "}\n" +
            "exit 0\n",
            "#!/usr/bin/env bash\n" +
            "if [ 'x' != 'a\"}b' ]; then\n" +
            "    echo 'TOOK_BRANCH'\n" +
            "    exit 1\n" +
            "fi\n" +
            "exit 0\n",
            /* expectPass */ false,
            /* expectedStdoutToken */ "TOOK_BRANCH"
        },
        // Case 3 — a passing guardrail whose body STILL contains '"..."' to prove embedded
        // double-quotes parse fine on the success path too (exit 0, no parser error).
        {
            "embedded-double-quotes-passing",
            "$pattern = '\"test-commander-rest\"'\n" +
            "if ($pattern.Length -gt 0) { exit 0 } else { Write-Output 'EMPTY'; exit 1 }\n",
            "#!/usr/bin/env bash\n" +
            "pattern='\"test-commander-rest\"'\n" +
            "if [ -n \"$pattern\" ]; then exit 0; else echo 'EMPTY'; exit 1; fi\n",
            /* expectPass */ true,
            /* expectedStdoutToken */ ""
        },
    };
}
