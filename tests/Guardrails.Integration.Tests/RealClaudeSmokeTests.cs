using Guardrails.Core.Execution;
using Guardrails.Core.Loading;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Opt-in smoke test that runs a single trivial prompt task against the REAL Claude CLI.
/// Skipped unless <c>GUARDRAILS_REAL_CLAUDE=1</c> (CI and default local runs never spend
/// tokens). When enabled it proves the live wiring: stdin delivery, stream-json parsing,
/// fragment write, and cost capture.
/// </summary>
public sealed class RealClaudeSmokeTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("GUARDRAILS_REAL_CLAUDE") == "1";

    [Fact]
    public async Task TrivialPromptTask_RunsGreen_AgainstRealClaude()
    {
        Assert.SkipUnless(Enabled, "Set GUARDRAILS_REAL_CLAUDE=1 to run the real-claude smoke test.");

        string root = Path.Combine(Path.GetTempPath(), "gr-realclaude-" + Guid.NewGuid().ToString("N"));
        try
        {
            BuildTrivialPromptPlan(root);

            PlanLoadResult load = new PlanLoader().Load(root);
            Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

            Scheduler scheduler = SchedulerFactory.Create(
                load.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);
            RunReport report = await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);

            Assert.True(report.AllSucceeded, string.Join("\n", report.Tasks.Select(t => $"{t.TaskId}: {t.Summary}")));

            string outPath = Path.Combine(root, "out", "answer.txt");
            Assert.True(File.Exists(outPath), "expected the prompt to create out/answer.txt");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    private static void BuildTrivialPromptPlan(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "out"));
        File.WriteAllText(Path.Combine(root, "guardrails.json"),
            """
            {
              "version": 1,
              "workspace": ".",
              "defaultRetries": 0,
              "defaultTimeoutSeconds": 300,
              "promptRunners": {
                "default": "claude",
                "claude": {
                  "command": "claude",
                  "permissionMode": "acceptEdits",
                  "allowedTools": ["Write"],
                  "maxTurns": 5
                }
              }
            }
            """);

        string taskDir = Path.Combine(root, "tasks", "01-trivial");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """{ "description": "Write a trivial answer file", "dependsOn": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"),
            "Create a file at `out/answer.txt` (relative to your working directory) containing exactly the word `ok`. Then stop.\n");

        // Deterministic guardrail: the file exists. Cross-platform via OS-picked script.
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-exists.ps1"),
                "if (-not (Test-Path 'out/answer.txt')) { Write-Output 'out/answer.txt missing'; exit 1 }\nexit 0\n");
        }
        else
        {
            string sh = Path.Combine(taskDir, "guardrails", "01-exists.sh");
            File.WriteAllText(sh, "#!/usr/bin/env bash\n[ -f out/answer.txt ] || { echo 'out/answer.txt missing'; exit 1; }\nexit 0\n");
            File.SetUnixFileMode(sh,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }
}
