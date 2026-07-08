using System.Diagnostics;
using Guardrails.Cli;
using Guardrails.Core.Journal;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #175 — merge-collision ATTRIBUTION, ported (issue #205) onto the FOUR-FOLDER terminal phase
/// (<see cref="PlanGuardrailPhase"/>, the <c>&lt;plan&gt;/guardrails/</c> folder), REFRAMED HEDGED by #272 Part 2.
/// When the terminal plan-guardrail gate fails on the final merged HEAD and two tasks have OVERLAPPING
/// <c>writeScope</c> on a shared file, the harness enriches the halt with the overlapping task pairs + shared
/// path as SUSPECTS a human can verify. #272 Part 2: mere overlap is a WEAK signal (a stub+impl TDD pair
/// overlaps by design and usually merges cleanly), so the hint is now HEDGED — it names the pairs as a
/// possibility to check only if the reported failure detail looks merge-related, and points at that failure
/// detail as the PRIMARY signal — instead of the pre-#272 confident "this IS a merge collision" lead that
/// sent triage down the wrong path on a clean merge. The harness does NOT detect the semantic duplicate
/// itself (that is the build guardrail's job); it surfaces the structural suspects, hedged.
///
/// <para>
/// The pre-#205 fixture pinned this on the LEGACY per-task <c>integrationGate</c> / <c>Scheduler.WithTerminalGateFailure</c>
/// path (bypassing validate so GR2029 would not reject the retired key). #205 ported the attribution — via the
/// shared <see cref="Guardrails.Core.Execution.WriteScope.OverlappingWriteScopeHint"/> helper — onto the new
/// terminal phase, so this test now drives a REAL four-folder plan through the production CLI (<c>guardrails run</c>),
/// which VALIDATES clean (no legacy key, no GR2029). The attribution is asserted from the NEW phase's outputs: the
/// journaled <c>planGuardrails.collisionHint</c> AND the console halt block.
/// </para>
///
/// Driven through a real git repo (worktree mode) + a RED terminal <c>&lt;plan&gt;/guardrails/</c> check that forces
/// the terminal halt. The two impl tasks form a linear chain (both FF-settle, no union), so the run drains green
/// and the terminal phase fires against the merged HEAD.
/// </summary>
[Trait("Category", "Preflights")]
public sealed class OverlappingWriteScopeAttributionTests
{
    private static readonly bool Ps = OperatingSystem.IsWindows();

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;
        public string RepoPath { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-175-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            Directory.CreateDirectory(RepoPath);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            Git(RepoPath, "config", "commit.gpgsign", "false");
            Git(RepoPath, "config", "core.autocrlf", "false");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# overlap-attribution-test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public static string Git(string workingDir, params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
            return stdout;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch { /* best-effort teardown */ }
        }
    }

    private static void WriteScript(string path, string body)
    {
        string content = Ps ? body + "\n" : "#!/usr/bin/env bash\n" + body + "\n";
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    /// <summary>An action that writes (overwrites) a single workspace-relative file in its writeScope.</summary>
    private static string WriteFileAction(string relPath, string content) => Ps
        ? $"Set-Content -NoNewline -Path \"$env:GUARDRAILS_WORKSPACE\\{relPath}\" -Value '{content}'; exit 0"
        : $"printf '%s' '{content}' > \"$GUARDRAILS_WORKSPACE/{relPath}\"; exit 0";

    /// <summary>
    /// Build a four-folder plan in <paramref name="repoPath"/>: two implementation tasks (a linear chain so both
    /// FF-settle) each declaring <paramref name="scopeA"/>/<paramref name="scopeB"/> + writing a file in scope,
    /// plus a plan-level <c>&lt;plan&gt;/guardrails/</c> terminal folder holding one RED check (exit 1). The RED
    /// terminal gate forces <see cref="PlanGuardrailPhase"/> to fail after the DAG drains green — the #205 trigger
    /// for the merge-collision attribution. The chain has one leaf (no fan-in), so GR2028's content-teeth rule is
    /// exempt and a plain exit-1 terminal check validates clean.
    /// </summary>
    private static string CreatePlan(string repoPath, string scopeA, string fileA, string scopeB, string fileB)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(planDir);
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 2
            }
            """);

        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));

        // 01-impl-a and 02-impl-b: a linear chain (B depends on A) so both settle via FF, each writing exactly
        // one in-scope file. The overlapping writeScope on a shared file is the #175 collision shape.
        WriteImplTask(planDir, "01-impl-a", dependsOn: [], scope: scopeA, file: fileA, content: "class CommanderRestImporter {}");
        WriteImplTask(planDir, "02-impl-b", dependsOn: ["01-impl-a"], scope: scopeB, file: fileB, content: "class CommanderRestImporter { void X() {} }");

        WriteTerminalGate(planDir);
        return planDir;
    }

    private static void WriteImplTask(string planDir, string taskId, string[] dependsOn, string scope, string file, string content)
    {
        string taskDir = Path.Combine(planDir, "tasks", taskId);
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        string dependsJson = dependsOn.Length == 0 ? "[]" : "[" + string.Join(", ", dependsOn.Select(d => $"\"{d}\"")) + "]";
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "impl {{taskId}}",
              "dependsOn": {{dependsJson}},
              "writeScope": ["{{scope}}"]
            }
            """);

        WriteScript(Path.Combine(taskDir, ActionFileName), WriteFileAction(file, content));
        // The task's own guardrail opens with a catches: declaration (GR2027 applies to all four folders).
        WriteScript(Path.Combine(taskDir, "guardrails", GuardrailFileName),
            "# catches: the action failed to write its in-scope file\nexit 0");
    }

    /// <summary>
    /// The plan-level <c>&lt;plan&gt;/guardrails/</c> terminal folder — one RED (exit 1) check so the terminal
    /// phase halts the run on the merged HEAD, triggering the #175 attribution.
    /// </summary>
    private static void WriteTerminalGate(string planDir)
    {
        string terminalDir = Path.Combine(planDir, "guardrails");
        Directory.CreateDirectory(terminalDir);
        WriteScript(Path.Combine(terminalDir, TerminalCheckFileName),
            "# catches: the merged HEAD fails the whole-repo build/test on the final integrated bytes\n" +
            (Ps ? "Write-Output 'terminal gate RED (deliberate)'\nexit 1"
                : "echo 'terminal gate RED (deliberate)'\nexit 1"));
    }

    private static string ActionFileName => Ps ? "action.ps1" : "action.sh";
    private static string GuardrailFileName => Ps ? "01-in-scope.ps1" : "01-in-scope.sh";
    private static string TerminalCheckFileName => Ps ? "01-whole-repo-build.ps1" : "01-whole-repo-build.sh";

    /// <summary>Drive the REAL <c>guardrails run</c> CLI (which VALIDATES) over a four-folder plan; capture stdout.</summary>
    private static async Task<(int Exit, string Out)> RunCliAsync(string planDir)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(["run", planDir, "--no-ui", "--no-log-server"]).InvokeAsync();
        return (exit, io.OutText);
    }

    private static JournalDocument ReadJournal(string planDir) =>
        JournalReader.Read(RunJournal.PathFor(planDir));

    [Fact]
    public async Task TerminalGateFails_OverlappingWriteScopes_DiagnosisNamesBothTasksAndSharedFile()
    {
        using var repo = new TempGitRepo();
        // Both impl tasks write Launcher.cs (overlapping writeScope) — the #175 shape. In this linear chain
        // 02 simply OVERWRITES 01's Launcher.cs, so the shared file merges CLEANLY (no conflict marker, no
        // duplicate definition) and the terminal failure is UNRELATED (a deliberate RED gate) — the exact
        // #272 Part 2 "false-lead" scenario, so the hint must be HEDGED, not a confident collision claim.
        string planDir = CreatePlan(repo.RepoPath,
            scopeA: "Launcher.cs", fileA: "Launcher.cs",
            scopeB: "Launcher.cs", fileB: "Launcher.cs");

        (int exit, string output) = await RunCliAsync(planDir);

        // A terminal plan-guardrail halt (exit 2) — NOT the legacy per-task gate; this plan validates clean.
        Assert.Equal(ExitCodes.TaskFailed, exit);

        JournalDocument doc = ReadJournal(planDir);
        Assert.NotNull(doc.PlanGuardrails);
        Assert.Equal(PlanPhaseStatus.PlanGuardrailFailed, doc.PlanGuardrails!.Status);

        // Proof the merge was CLEAN: the merged Launcher.cs on the plan branch has NO conflict markers and a
        // SINGLE class definition — so a confident "this IS a merge collision" would have been a false lead.
        string merged = TempGitRepo.Git(repo.RepoPath, "show", "guardrails/plan:Launcher.cs");
        Assert.DoesNotContain("<<<<<<<", merged, StringComparison.Ordinal);
        Assert.DoesNotContain(">>>>>>>", merged, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(merged, "class CommanderRestImporter"));

        // The #205 port + #272 Part 2: the hint still NAMES both tasks + the shared file (useful attribution),
        // but is HEDGED — overlap is EXPECTED for a stub+impl pair, and the reported failure detail is the
        // PRIMARY signal — NOT the confident "This may be a merge collision" lead that misdirected triage.
        string? hint = doc.PlanGuardrails!.CollisionHint;
        Assert.NotNull(hint);
        Assert.DoesNotContain("This may be a merge collision", hint!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EXPECTED", hint!, StringComparison.Ordinal);
        Assert.Contains("PRIMARY signal", hint!, StringComparison.Ordinal);
        Assert.Contains("01-impl-a", hint!, StringComparison.Ordinal);
        Assert.Contains("02-impl-b", hint!, StringComparison.Ordinal);
        Assert.Contains("Launcher.cs", hint!, StringComparison.Ordinal);

        // ...and RunCommand surfaces the same hedged attribution inline — never the confident false assertion.
        Assert.DoesNotContain("This may be a merge collision", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PRIMARY signal", output, StringComparison.Ordinal);
        Assert.Contains("01-impl-a", output, StringComparison.Ordinal);
        Assert.Contains("02-impl-b", output, StringComparison.Ordinal);
        Assert.Contains("Launcher.cs", output, StringComparison.Ordinal);
    }

    /// <summary>Count non-overlapping occurrences of <paramref name="token"/> in <paramref name="text"/>.</summary>
    private static int CountOccurrences(string text, string token)
    {
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf(token, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += token.Length;
        }
        return count;
    }

    [Fact]
    public async Task TerminalGateFails_DisjointWriteScopes_NoCollisionAnnotation()
    {
        using var repo = new TempGitRepo();
        // Disjoint writeScopes — no overlap, so NO collision hint must be journaled or printed.
        string planDir = CreatePlan(repo.RepoPath,
            scopeA: "A.cs", fileA: "A.cs",
            scopeB: "B.cs", fileB: "B.cs");

        (int exit, string output) = await RunCliAsync(planDir);

        Assert.Equal(ExitCodes.TaskFailed, exit);

        JournalDocument doc = ReadJournal(planDir);
        Assert.NotNull(doc.PlanGuardrails);
        Assert.Equal(PlanPhaseStatus.PlanGuardrailFailed, doc.PlanGuardrails!.Status);

        // Still a terminal halt, but with NO merge-collision attribution (scopes don't overlap).
        Assert.Null(doc.PlanGuardrails!.CollisionHint);
        Assert.DoesNotContain("merge collision", output, StringComparison.OrdinalIgnoreCase);
    }
}
