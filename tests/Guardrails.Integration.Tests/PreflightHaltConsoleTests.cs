using System.CommandLine;
using System.Diagnostics;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;

namespace Guardrails.Integration.Tests;

/// <summary>
/// #762 through the REAL <c>run</c> command: a failed plan preflight's console output carries the full headline,
/// each failed check's name and reason, and the captured-output directory, in that order, with the
/// <c>run.json</c> pointer as the last line. Before the fix the same run printed only
/// <c>Plan preflight FAILED — halting before scheduling any task (SSOT §7 planPreflights).</c> and the pointer.
/// Real git repo, OS-picked <c>.ps1</c>/<c>.sh</c> checks; output captured through an injected
/// <see cref="StringConsoleIo"/>, so nothing touches the process console.
/// </summary>
public sealed class PreflightHaltConsoleTests
{
    private static readonly bool Ps = OperatingSystem.IsWindows();

    [Fact]
    public async Task FailedPlanPreflights_PrintEveryFailedCheck_WithItsReason_ThenLogs_ThenTheStatePointerLast()
    {
        using var repo = new TempGitRepo();
        string planDir = CreatePlan(repo.RepoPath);
        WriteCheck(planDir, "01-alpha", ["ALPHA diagnosis: the baseline commit has no CI run", "ALPHA remedy: widen the search window"], passes: false);
        WriteCheck(planDir, "02-beta", ["BETA diagnosis: the toolchain is missing"], passes: false);
        WriteCheck(planDir, "03-gamma", ["GAMMA is fine"], passes: true);

        (int exit, string output) = await RunAsync(["run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success"]);

        Assert.Equal(ExitCodes.TaskFailed, exit);
        JournalDocument journal = JournalReader.Read(RunJournal.PathFor(planDir));
        string logDir = Path.GetFullPath(Path.Combine(planDir, journal.Halt!.LogDir!));

        AssertInOrder(output,
            "Plan preflight FAILED — halting before scheduling any task: 01-alpha, 02-beta",
            "  FAILED: 01-alpha",
            "    ALPHA diagnosis: the baseline commit has no CI run",
            "    ALPHA remedy: widen the search window",
            "  FAILED: 02-beta",
            "    BETA diagnosis: the toolchain is missing",
            $"  Logs:  {logDir}",
            $"  State: {RunJournal.PathFor(planDir)} (\"planPreflights\")");

        Assert.Equal($"  State: {RunJournal.PathFor(planDir)} (\"planPreflights\")", LastNonEmptyLine(output));

        // A passing check is not reported as failed, and the old truncated headline is gone.
        Assert.DoesNotContain("FAILED: 03-gamma", output, StringComparison.Ordinal);
        Assert.DoesNotContain("halting before scheduling any task (SSOT §7 planPreflights).", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevalidatingPlanPreflights_PrintsTheSameBlock()
    {
        using var repo = new TempGitRepo();
        string planDir = CreatePlan(repo.RepoPath);
        WriteCheck(planDir, "01-alpha", ["ALPHA still broken"], passes: false);
        WriteTask(planDir);

        (int exit, string output) = await RunAsync(
            ["run", planDir, "--revalidate-task", "plan:preflights", "--no-ui", "--no-log-server"]);

        Assert.Equal(ExitCodes.TaskFailed, exit);
        // Revalidate's own lead line, not the recorded "halting before scheduling any task" headline.
        AssertInOrder(output,
            "Plan preflight still failing:",
            "  FAILED: 01-alpha",
            "    ALPHA still broken",
            $"  State: {RunJournal.PathFor(planDir)} (\"planPreflights\")");
        Assert.Equal($"  State: {RunJournal.PathFor(planDir)} (\"planPreflights\")", LastNonEmptyLine(output));
        Assert.DoesNotContain("halting before scheduling any task", output, StringComparison.Ordinal);
    }

    private static void AssertInOrder(string output, params string[] lines)
    {
        string[] actual = output.Replace("\r\n", "\n").Split('\n');
        int from = 0;
        foreach (string expected in lines)
        {
            int at = Array.IndexOf(actual, expected, from);
            Assert.True(at >= 0, $"expected line (in order, from line {from}) not found: [{expected}]\n--- output ---\n{output}");
            from = at + 1;
        }
    }

    private static string LastNonEmptyLine(string output) =>
        output.Replace("\r\n", "\n").Split('\n').Last(l => l.Trim().Length > 0);

    private static async Task<(int Exit, string Output)> RunAsync(string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("preflight halt console test root");
        root.Add(RunCommand.Create(io));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    /// <summary>A flat worktree-mode plan with one trivial task, so the plan loads and the DAG would be non-empty.</summary>
    private static string CreatePlan(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            { "version": 1, "guardrailMode": "failFast", "workspace": "..", "defaultRetries": 0, "maxParallelism": 2 }
            """);
        WriteTask(planDir);
        return planDir;
    }

    private static void WriteTask(string planDir)
    {
        string taskDir = Path.Combine(planDir, "tasks", "01-only");
        if (Directory.Exists(taskDir))
        {
            return;
        }

        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "preflight-halt fixture", "writeScope": ["only.txt"] }""");
        WriteScript(Path.Combine(taskDir, Script("action")),
            "Set-Content -NoNewline -Path (Join-Path $env:GUARDRAILS_WORKSPACE 'only.txt') -Value 'x'\nexit 0",
            "printf 'x' > \"$GUARDRAILS_WORKSPACE/only.txt\"\nexit 0");
        WriteScript(Path.Combine(taskDir, "guardrails", Script("01-check")),
            "# catches: only.txt missing\nif (-not (Test-Path (Join-Path $env:GUARDRAILS_WORKSPACE 'only.txt'))) { exit 1 }\nexit 0",
            "# catches: only.txt missing\n[ -f \"$GUARDRAILS_WORKSPACE/only.txt\" ] || exit 1\nexit 0");
    }

    /// <summary>A <c>&lt;plan&gt;/preflights/</c> check that prints <paramref name="lines"/> and passes or fails.</summary>
    private static void WriteCheck(string planDir, string name, string[] lines, bool passes)
    {
        string catches = $"# catches: {name} — a preflight whose reason never reached the console (#762)";
        string code = passes ? "exit 0" : "exit 1";
        WriteScript(
            Path.Combine(planDir, "preflights", Script(name)),
            $"{catches}\n{string.Join("\n", lines.Select(l => $"Write-Output '{l}'"))}\n{code}",
            $"{catches}\n{string.Join("\n", lines.Select(l => $"echo '{l}'"))}\n{code}");
    }

    private static string Script(string stem) => Ps ? stem + ".ps1" : stem + ".sh";

    private static void WriteScript(string path, string psBody, string bashBody)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Ps ? psBody + "\n" : "#!/usr/bin/env bash\n" + bashBody + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-preflight-halt-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            Directory.CreateDirectory(RepoPath);

            Git("init");
            Git("config", "user.email", "test@guardrails.local");
            Git("config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# preflight-halt-console");
            Git("add", ".");
            Git("commit", "-m", "Initial commit");
        }

        private void Git(params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            using Process proc = Process.Start(psi)!;
            proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {string.Join(" ", args)} exited {proc.ExitCode}: {stderr.Trim()}");
            }
        }

        public void Dispose()
        {
            try { SafeDelete.DeleteDirectory(_root); }
            catch { /* best-effort teardown */ }
        }
    }
}
