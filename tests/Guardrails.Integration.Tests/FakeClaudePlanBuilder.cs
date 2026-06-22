namespace Guardrails.Integration.Tests;

/// <summary>
/// Builds a real, runnable plan folder whose prompt runner <c>command</c> is a FAKE Claude
/// CLI — an OS-appropriate script (<c>.cmd</c> on Windows, <c>.sh</c> elsewhere, both directly
/// spawnable) that (a) drains stdin, (b) emits a canned stream-json result, and (c) writes a
/// state fragment or a verdict file based on the env it receives. This proves the whole prompt
/// pipeline (compose → invoke → verdict/fragment → merge → journal cost) without any tokens.
///
/// Scenario control flows through env vars set on the action (which the harness propagates to
/// guardrails too): <c>FAKE_MODE</c> = fragment | nofragment | needshuman | iserror;
/// <c>FAKE_COST</c> = a cost string, or <c>none</c> to omit <c>total_cost_usd</c> from the result
/// line (models a succeeded prompt whose runner reported no cost → null CostUsd); <c>FAKE_VERDICT</c>
/// = pass | fail (read by guardrail invocations). <c>nofragment</c> succeeds cleanly but contributes NO state fragment — the
/// shape a task takes when a reset + re-run produces a later succeeded attempt that does not
/// touch <c>state.json</c>.
/// </summary>
public sealed class FakeClaudePlanBuilder : IDisposable
{
    private static readonly bool Windows = OperatingSystem.IsWindows();

    private readonly string _root;
    private readonly string _fakeCliPath;

    public FakeClaudePlanBuilder(int defaultRetries = 0, int maxParallelism = 1)
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-fakecli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "tasks"));

        _fakeCliPath = Path.Combine(_root, Windows ? "fake-claude.cmd" : "fake-claude.sh");
        WriteFakeCli(_fakeCliPath);

        // The runner command is the fake CLI. allowedTools/permissionMode are carried but the
        // fake ignores all args (it reads stdin + env only).
        string commandJson = _fakeCliPath.Replace("\\", "\\\\");
        File.WriteAllText(Path.Combine(_root, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": ".",
              "defaultRetries": {{defaultRetries}},
              "maxParallelism": {{maxParallelism}},
              "defaultTimeoutSeconds": 60,
              "promptRunners": {
                "default": "claude",
                "claude": {
                  "command": "{{commandJson}}",
                  "permissionMode": "acceptEdits",
                  "allowedTools": ["Read", "Write"],
                  "maxTurns": 5,
                  "guardrailOverrides": { "permissionMode": "default", "maxTurns": 3 }
                }
              }
            }
            """);
    }

    /// <summary>Absolute path to the generated plan folder.</summary>
    public string PlanDir => _root;

    /// <summary>Absolute path to <c>state/state.json</c> after a run.</summary>
    public string StateJsonPath => Path.Combine(_root, "state", "state.json");

    /// <summary>Absolute path to <c>state/run.json</c> after a run.</summary>
    public string RunJsonPath => Path.Combine(_root, "state", "run.json");

    /// <summary>
    /// Add a prompt-action task. <paramref name="mode"/> drives the fake action; <paramref name="cost"/>
    /// is the reported cost; <paramref name="env"/> adds extra control vars (e.g. a guardrail's
    /// FAKE_VERDICT). <paramref name="promptGuardrail"/> adds a prompt verdict guardrail.
    /// </summary>
    public FakeClaudePlanBuilder AddPromptTask(
        string id,
        string mode = "fragment",
        string cost = "0.0150",
        bool promptGuardrail = false,
        IReadOnlyDictionary<string, string>? env = null,
        params string[] dependsOn)
    {
        string taskDir = Path.Combine(_root, "tasks", id);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        string dependsJson = dependsOn.Length == 0
            ? "[]"
            : "[" + string.Join(", ", dependsOn.Select(d => $"\"{d}\"")) + "]";

        var envEntries = new List<string> { $"\"FAKE_MODE\": \"{mode}\"", $"\"FAKE_COST\": \"{cost}\"" };
        if (env is not null)
        {
            envEntries.AddRange(env.Select(kv => $"\"{kv.Key}\": \"{kv.Value}\""));
        }

        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "fake prompt task {{id}}",
              "dependsOn": {{dependsJson}},
              "action": {
                "path": "action.prompt.md",
                "env": { {{string.Join(", ", envEntries)}} }
              }
            }
            """);

        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"),
            "Generate the thing. Write your fragment to the state-out path.\n");

        if (promptGuardrail)
        {
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-verdict.prompt.md"),
                "---\nmaxTurns: 3\n---\nYou are a verifier. Judge the thing and write a verdict.\n");
        }
        else
        {
            // A trivial always-pass deterministic guardrail.
            WriteScript(Path.Combine(taskDir, "guardrails", Windows ? "01-ok.cmd" : "01-ok.sh"),
                Windows ? "@echo off\r\nexit /b 0\r\n" : "#!/usr/bin/env bash\nexit 0\n");
        }

        return this;
    }

    /// <summary>
    /// Rewrite an existing prompt task's <c>FAKE_MODE</c> (preserving its cost) so a re-run after
    /// a reset can take a different shape — e.g. flip a previously fragment-producing task to
    /// <c>nofragment</c> so its later succeeded attempt leaves <c>state.json</c> untouched.
    /// </summary>
    public FakeClaudePlanBuilder SetMode(string id, string mode, string cost = "0.0150")
    {
        string taskDir = Path.Combine(_root, "tasks", id);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "fake prompt task {{id}}",
              "dependsOn": [],
              "action": {
                "path": "action.prompt.md",
                "env": { "FAKE_MODE": "{{mode}}", "FAKE_COST": "{{cost}}" }
              }
            }
            """);
        return this;
    }

    private void WriteFakeCli(string path)
    {
        // The fake CLI: drain stdin, then either write a verdict (guardrail) or a fragment
        // (action), then emit a single-line stream-json result. is_error is true only for the
        // "iserror" action mode. Cost comes from FAKE_COST.
        if (OperatingSystem.IsWindows())
        {
            // The directly-spawnable .cmd forwards to a sibling .ps1 (clean, no inline quoting).
            string ps1 = Path.ChangeExtension(path, ".ps1");
            File.WriteAllText(ps1, FakePowerShellBody());
            string ps1Quoted = ps1.Replace("\"", "");
            File.WriteAllText(path,
                $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1Quoted}\"\r\n");
        }
        else
        {
            File.WriteAllText(path, FakeBashBody());
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static string FakePowerShellBody() =>
        """
        $null = [Console]::In.ReadToEnd()
        $cost = $env:FAKE_COST; if (-not $cost) { $cost = '0' }
        if ($env:GUARDRAILS_VERDICT_OUT) {
            if ($env:FAKE_VERDICT -eq 'fail') {
                $body = '{"pass": false, "reason": "the thing is wrong: fix the X"}'
            } else {
                $body = '{"pass": true, "reason": "looks good"}'
            }
            Set-Content -NoNewline -Path $env:GUARDRAILS_VERDICT_OUT -Value $body
        } elseif ($env:GUARDRAILS_STATE_OUT) {
            if ($env:FAKE_MODE -eq 'needshuman') {
                Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value '{"needsHuman": "which color should I use?"}'
            } elseif ($env:FAKE_MODE -eq 'iserror' -or $env:FAKE_MODE -eq 'nofragment' -or $env:FAKE_MODE -eq 'overcap' -or $env:FAKE_MODE -eq 'transient') {
                # no fragment (iserror/overcap/transient report is_error; nofragment succeeds cleanly)
            } else {
                $frag = '{"' + $env:GUARDRAILS_TASK_ID + '": {"produced": true}}'
                Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value $frag
            }
        }
        # Some modes emit a stream-json result whose `result` text carries a recognised signal so the
        # ClaudePromptRunner classifies it (overcap = output-token cap #114; transient = rate-limit #115).
        if ($env:FAKE_MODE -eq 'overcap') {
            Write-Output ('{"type":"result","is_error":true,"result":"API Error: Claude''s response exceeded the 32000 output token maximum","num_turns":4}')
        } elseif ($env:FAKE_MODE -eq 'transient') {
            Write-Output ('{"type":"result","is_error":true,"result":"You''ve hit your session limit · resets 11:20am (America/Chicago)","num_turns":1}')
        } else {
            $err = if ($env:FAKE_MODE -eq 'iserror') { 'true' } else { 'false' }
            if ($cost -eq 'none') {
                # Omit total_cost_usd — models a succeeded prompt whose runner reported no cost.
                Write-Output ('{"type":"result","is_error":' + $err + ',"result":"fake done","num_turns":2}')
            } else {
                Write-Output ('{"type":"result","is_error":' + $err + ',"result":"fake done","total_cost_usd":' + $cost + ',"num_turns":2}')
            }
        }
        """;

    private static string FakeBashBody() =>
        """
        #!/usr/bin/env bash
        cat > /dev/null
        cost="${FAKE_COST:-0}"
        if [ -n "$GUARDRAILS_VERDICT_OUT" ]; then
          if [ "$FAKE_VERDICT" = "fail" ]; then
            printf '{"pass": false, "reason": "the thing is wrong: fix the X"}' > "$GUARDRAILS_VERDICT_OUT"
          else
            printf '{"pass": true, "reason": "looks good"}' > "$GUARDRAILS_VERDICT_OUT"
          fi
        elif [ -n "$GUARDRAILS_STATE_OUT" ]; then
          if [ "$FAKE_MODE" = "needshuman" ]; then
            printf '{"needsHuman": "which color should I use?"}' > "$GUARDRAILS_STATE_OUT"
          elif [ "$FAKE_MODE" = "iserror" ] || [ "$FAKE_MODE" = "nofragment" ] || [ "$FAKE_MODE" = "overcap" ] || [ "$FAKE_MODE" = "transient" ]; then
            : # no fragment (iserror/overcap/transient report is_error; nofragment succeeds cleanly)
          else
            printf '{"%s": {"produced": true}}' "$GUARDRAILS_TASK_ID" > "$GUARDRAILS_STATE_OUT"
          fi
        fi
        # Some modes emit a result whose `result` text carries a recognised signal so the
        # ClaudePromptRunner classifies it (overcap = output-token cap #114; transient = rate-limit #115).
        if [ "$FAKE_MODE" = "overcap" ]; then
          printf '{"type":"result","is_error":true,"result":"API Error: Claude'"'"'s response exceeded the 32000 output token maximum","num_turns":4}\n'
        elif [ "$FAKE_MODE" = "transient" ]; then
          printf '{"type":"result","is_error":true,"result":"You'"'"'ve hit your session limit \xc2\xb7 resets 11:20am (America/Chicago)","num_turns":1}\n'
        else
          if [ "$FAKE_MODE" = "iserror" ]; then err=true; else err=false; fi
          if [ "$cost" = "none" ]; then
            # Omit total_cost_usd — models a succeeded prompt whose runner reported no cost.
            printf '{"type":"result","is_error":%s,"result":"fake done","num_turns":2}\n' "$err"
          else
            printf '{"type":"result","is_error":%s,"result":"fake done","total_cost_usd":%s,"num_turns":2}\n' "$err" "$cost"
          fi
        fi
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
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }
}
