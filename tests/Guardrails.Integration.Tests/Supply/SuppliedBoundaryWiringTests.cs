using System.Diagnostics;
using Guardrails.Cli;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;

namespace Guardrails.Integration.Tests.Supply;

/// <summary>
/// Design 40 §1's table — the two DRAIN BOUNDARIES — driven through the REAL production seam: a real
/// <c>Scheduler</c> (via <see cref="CommandFactory.BuildRootCommand"/>'s <c>run</c>, never a hand-built
/// root or an injected fake drain) against a real git repository (worktree mode, <c>maxParallelism</c>
/// 2), so a task's own worktree is a REAL segment branched from the run's REAL base.
/// <para>
/// <b>There is no stub file for this task.</b> Both boundaries' PRODUCTION TYPES already exist —
/// <c>SuppliedDrain</c> (task 04), the <c>run.json</c> <c>supplied[]</c> write path (task 06), and the
/// observer announcement + its CLI rendering (tasks 07-09) — what is missing is the CALL: nothing in
/// <c>Scheduler</c> or the CLI's resume path invokes <c>SuppliedDrain.Drain</c> anywhere. That absence is
/// exactly what every test below proves, by arranging the ONE input the drain would need to see (a file
/// staged at the fixed, already-implemented location <c>logs/&lt;runId&gt;/supplied/</c>) and asserting an
/// effect ONLY the wiring — not the tests, not a double — could have produced.
/// </para>
/// <para>
/// <b>Staging is done directly, never through <c>guardrails supply</c>.</b> That CLI verb (design 40
/// task 11) is a sibling of this task and has not landed: <c>SupplyCommand</c> is neither registered in
/// <see cref="CommandFactory"/> nor implemented. The staging TREE LAYOUT it would write to, though, is
/// already a fixed, shipped contract (<c>SuppliedStagingTree</c>, §1:
/// <c>logs/&lt;runId&gt;/supplied/&lt;workspaceRelativePath&gt;</c>) — the same convention
/// <c>SuppliedDrainTests</c>' own <c>Stage</c> helper uses. Writing straight to it is equivalent to what
/// <c>supply</c> will do, without coupling these tests to an unrelated, not-yet-built command.
/// </para>
/// <para>
/// <b>Row 1</b> (<see cref="TaskBoundary_DrainsFilesStagedWhileTheRunIsExecuting"/>) has a task stage a
/// file as part of ITS OWN action — design 40 §5a's JIT case, the realistic way a file gets staged
/// "while the run is executing" inside one continuous <c>run</c> invocation — and a dependent task read
/// its OWN workspace to prove the file was already on the base when ITS worktree branched from it.
/// </para>
/// <para>
/// <b>Row 2</b> (<see cref="RunStart_DrainsFilesStagedAfterTheRunHalted"/>) is the measured incident: a
/// task settles <c>needs-human</c> with zero retries, the run process EXITS, the file is staged AFTER
/// that exit (there is no live task boundary left), and a SECOND <c>run</c> invocation must drain it
/// before scheduling the reset task — the cleanest boundary, since nothing is yet branched from the base.
/// </para>
/// <para>
/// <b>Why the last two exist (review, 2026-09-11).</b> Tasks 06 / 07-09 unit-tested the record round-trip
/// and the hand-raised event in isolation; nothing asserted the production DRAIN actually calls either.
/// <see cref="TaskBoundary_WritesTheSuppliedProvenanceIntoRunJson"/> reads <c>run.json</c> AFTER a real
/// drain; <see cref="Drain_AnnouncesThroughTheRealObserverPipeline"/> reads the real <c>--no-ui</c>
/// transcript, never an injected <c>IRunObserver</c> double.
/// </para>
/// <para>
/// <b><see cref="ARunThatSuppliesNothing_IsUnchanged"/> is DECLARED-EXEMPT</b> from the red census
/// (<c>guardrails/02-tests-fail-on-current-code.ps1</c>'s pinned list is only the other three behaviours)
/// — the never-weaker requirement means a run that stages nothing is byte-identical whether or not the
/// drain is wired, so it is green against the unwired code BY CONSTRUCTION, not despite it.
/// </para>
/// <para>
/// TDD red: <c>Scheduler</c> and the CLI resume path call <c>SuppliedDrain.Drain</c> NOWHERE today, so
/// every non-exempt test below is expected to FAIL against this tree.
/// </para>
/// </summary>
[Trait("Category", "Supply")]
public sealed class SuppliedBoundaryWiringTests
{
    private const string StagedFileName = "supplied-marker.txt";
    private const string StagedFileContent = "staged-while-executing";

    // ── row 1: the task boundary, mid-run ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TaskBoundary_DrainsFilesStagedWhileTheRunIsExecuting()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateTaskBoundaryObservationPlan(repo.RepoPath);

        (int exit, _) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");

        // Both tasks' own guardrails pass UNCONDITIONALLY regardless of the drain — 02-second only
        // RECORDS what it found, it never gates on it — so a wholly-green run is the sanity floor, and
        // the RED/GREEN signal lives entirely in what 02-second observed, never in this exit code.
        Assert.Equal(ExitCodes.Success, exit);

        string runId = RunIdOf(planDir);
        string observed = repo.Show("guardrails/plan", "observed.txt");

        // §1 row 1: a file staged by 01-first's OWN action while the run is still executing must be on
        // the base by the time 02-second's worktree branches from it — proven by 02-second's OWN read of
        // its OWN workspace at that moment, never by the test reaching into scheduler internals.
        Assert.Equal("found", observed);

        // §4: the drain's commit must carry the trailer — never the design doc's literal first-draft
        // wording (`Supplied-By-Operator`), which the shipped `SuppliedDrain` deliberately does not use
        // (it would misattribute a task- or overwatcher-triggered supply as an operator action).
        string trailerLog = repo.LogBody("guardrails/plan");
        Assert.Contains("Supplied-By:", trailerLog);
        Assert.Contains($"Guardrails-Run: {runId}", trailerLog);
    }

    // ── row 2: run start, after the run halted and exited (the measured incident) ──────────────────

    [Fact]
    public async Task RunStart_DrainsFilesStagedAfterTheRunHalted()
    {
        using var repo = new TempGitRepo();
        const string markerRelPath = "resume-marker.txt";
        const string markerContent = "drained-at-run-start";
        string planDir = CreateHaltThenResumePlan(repo.RepoPath, markerRelPath, markerContent);

        (int firstExit, _) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");

        // The measured case: 02-second's guardrail finds nothing yet, settles needs-human with zero
        // retries, and the run EXITS — there is no live process, and so no live task boundary, left.
        Assert.Equal(ExitCodes.TaskFailed, firstExit);

        string runId = RunIdOf(planDir);
        StageDirectly(planDir, runId, markerRelPath, markerContent);

        // The design's own three-command recipe (§3(b)), minus `supply` itself (task 11, not yet built):
        // stage, reset the halted task, resume.
        (int resetExit, _) = await RunViaCliAsync("reset", planDir, "02-second");
        Assert.Equal(ExitCodes.Success, resetExit);

        (int secondExit, _) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");

        // §1 row 2: the drain happens at RUN START, before the first (just-reset) task is scheduled —
        // the cleanest of the two boundaries, since nothing is yet branched from the base.
        Assert.Equal(ExitCodes.Success, secondExit);

        string trailerLog = repo.LogBody("guardrails/plan");
        Assert.Contains("Supplied-By:", trailerLog);
        Assert.Contains($"Guardrails-Run: {runId}", trailerLog);
        Assert.Equal(markerContent, repo.Show("guardrails/plan", markerRelPath));
    }

    // ── §2's closing rule: supplying does not re-run settled work ──────────────────────────────────

    [Fact]
    public async Task SettledTasksAreNotReRunBySupplying()
    {
        using var repo = new TempGitRepo();
        const string markerRelPath = "resume-marker.txt";
        const string markerContent = "drained-at-run-start";
        string planDir = CreateHaltThenResumePlan(repo.RepoPath, markerRelPath, markerContent);

        (int firstExit, _) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");
        Assert.Equal(ExitCodes.TaskFailed, firstExit);

        TaskJournalEntry firstBeforeSupply = ReadJournal(planDir).Tasks["01-first"];
        Assert.Equal(Core.Journal.TaskStatus.Succeeded, firstBeforeSupply.Status);
        Assert.Single(firstBeforeSupply.Attempts);

        string runId = RunIdOf(planDir);
        StageDirectly(planDir, runId, markerRelPath, markerContent);

        (int resetExit, _) = await RunViaCliAsync("reset", planDir, "02-second");
        Assert.Equal(ExitCodes.Success, resetExit);

        (int secondExit, _) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");

        // The primary signal is still the drain actually firing (Success only once 02-second finds the
        // marker); a re-run-everything implementation would ALSO reach Success here, which is exactly
        // why the journal check below is load-bearing, not decorative: a design that re-ran the whole
        // DAG on every supply would be unusable on a long plan (§2), and this is what would catch it.
        Assert.Equal(ExitCodes.Success, secondExit);

        TaskJournalEntry firstAfterSupply = ReadJournal(planDir).Tasks["01-first"];
        Assert.Equal(Core.Journal.TaskStatus.Succeeded, firstAfterSupply.Status);
        Assert.Single(firstAfterSupply.Attempts); // never reset to pending, never re-attempted
    }

    // ── the never-weaker floor — DECLARED-EXEMPT from the red census, green by construction ─────────

    [Fact]
    public async Task ARunThatSuppliesNothing_IsUnchanged()
    {
        using var repo = new TempGitRepo();
        string planDir = Path.Combine(repo.RepoPath, "plan");
        WriteGuardrailsJson(planDir);
        WriteTrivialTask(
            Path.Combine(planDir, "tasks", "01-first"), dependsOn: [],
            "an ordinary green task; nothing is ever staged or supplied");

        (int exit, string output) =
            await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");

        Assert.Equal(ExitCodes.Success, exit);

        // No commit, no journal section, no observer event (§2 step 1 — never-weaker).
        Assert.DoesNotContain("[supplied]", output);
        Assert.Null(ReadJournal(planDir).Supplied);
        Assert.DoesNotContain("Supplied-By:", repo.LogBody("guardrails/plan"));
    }

    // ── §4: provenance read back from run.json AFTER a real drain ──────────────────────────────────

    [Fact]
    public async Task TaskBoundary_WritesTheSuppliedProvenanceIntoRunJson()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateTwoTaskStagingPlan(repo.RepoPath);

        (int exit, _) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");
        Assert.Equal(ExitCodes.Success, exit);

        JournalDocument journal = ReadJournal(planDir);

        // §4: read back AFTER the drain, never asserted against an injected fake of
        // RunJournal.RecordSupplied — the record must be the ACTUAL write the real drain performed.
        Assert.NotNull(journal.Supplied);
        SuppliedRecord record = Assert.Single(journal.Supplied!);
        Assert.Contains(StagedFileName, record.Paths);
        Assert.True(record.Bytes > 0);
        Assert.False(string.IsNullOrWhiteSpace(record.Commit));

        // The recorded commit must be a REAL object in the repo, not a fabricated placeholder sha.
        Assert.Equal("commit", repo.CatFileType(record.Commit).Trim());
    }

    // ── §2 step 3: the announcement, through the real --no-ui observer pipeline ─────────────────────

    [Fact]
    public async Task Drain_AnnouncesThroughTheRealObserverPipeline()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateTwoTaskStagingPlan(repo.RepoPath);

        (int exit, string output) =
            await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");
        Assert.Equal(ExitCodes.Success, exit);

        // §2 step 3, through ConsoleRunObserver's REAL --no-ui rendering ("[supplied] N resource(s)
        // committed <sha>: <paths>") — never asserted by calling an injected IRunObserver double, which
        // would prove only that the RENDERING exists (already true since task 09), not that the
        // production drain actually RAISES the event.
        Assert.Contains("[supplied]", output);
        Assert.Contains(StagedFileName, output);
    }

    // ── driving the real composition root ───────────────────────────────────────────────────────────

    private static async Task<(int ExitCode, string Output)> RunViaCliAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    private static JournalDocument ReadJournal(string planDir) =>
        JournalReader.Read(RunJournal.PathFor(planDir));

    private static string RunIdOf(string planDir) => ReadJournal(planDir).RunId;

    /// <summary>
    /// Write one file directly at the location <c>guardrails supply</c> would have staged it (design 40
    /// §1 — <c>logs/&lt;runId&gt;/supplied/&lt;workspaceRelativePath&gt;</c>). The CLI verb itself (task
    /// 11) has not landed; the staging TREE LAYOUT is already the fixed, shipped contract these drains
    /// read (<c>SuppliedStagingTree</c>), so writing straight to it is equivalent to what the verb will
    /// do, without coupling these boundary tests to that unrelated sibling task.
    /// </summary>
    private static void StageDirectly(string planDir, string runId, string workspaceRelativePath, string content)
    {
        string staged = Path.Combine(
            planDir, "logs", runId, "supplied", workspaceRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllText(staged, content);
    }

    // ── plan fixtures ────────────────────────────────────────────────────────────────────────────────

    private static readonly bool Windows = OperatingSystem.IsWindows();

    private static void WriteGuardrailsJson(string planDir)
    {
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
    }

    private static void WriteTaskJson(string taskDir, string[] writeScope, string[] dependsOn, string description)
    {
        string writeScopeJson = writeScope.Length == 0
            ? "[]" : "[" + string.Join(", ", writeScope.Select(s => $"\"{s}\"")) + "]";
        string dependsJson = dependsOn.Length == 0
            ? "[]" : "[" + string.Join(", ", dependsOn.Select(d => $"\"{d}\"")) + "]";

        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "{{description}}", "writeScope": {{writeScopeJson}}, "dependsOn": {{dependsJson}} }""");
    }

    private static void WriteExecutable(string path, string body)
    {
        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    /// <summary>A task whose ACTION stages <see cref="StagedFileName"/> directly at the location `guardrails supply` would while the run is still executing (design 40 §5a's JIT case).</summary>
    private static void WriteStagingTask(string taskDir, string[] dependsOn)
    {
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        WriteTaskJson(
            taskDir, writeScope: [], dependsOn,
            "stages a file directly at the location guardrails supply would, while its own action runs");

        if (Windows)
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"),
                "$runLogsDir = Split-Path (Split-Path $env:GUARDRAILS_LOG_DIR -Parent) -Parent\r\n" +
                "$suppliedDir = Join-Path $runLogsDir \"supplied\"\r\n" +
                "New-Item -ItemType Directory -Force -Path $suppliedDir | Out-Null\r\n" +
                $"Set-Content -NoNewline -Path (Join-Path $suppliedDir \"{StagedFileName}\") -Value \"{StagedFileContent}\"\r\n" +
                "exit 0\r\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), "exit 0\r\n");
        }
        else
        {
            WriteExecutable(Path.Combine(taskDir, "action.sh"),
                "#!/usr/bin/env bash\n" +
                "run_logs_dir=\"$(dirname \"$(dirname \"$GUARDRAILS_LOG_DIR\")\")\"\n" +
                "mkdir -p \"$run_logs_dir/supplied\"\n" +
                $"printf '%s' \"{StagedFileContent}\" > \"$run_logs_dir/supplied/{StagedFileName}\"\n" +
                "exit 0\n");
            WriteExecutable(Path.Combine(taskDir, "guardrails", "01-check.sh"), "#!/usr/bin/env bash\nexit 0\n");
        }
    }

    /// <summary>A task, depending on the staging task, whose ACTION records whether <see cref="StagedFileName"/> was ALREADY on the base when ITS OWN worktree was created — into `observed.txt` ("found"/"missing"), never gating its own guardrail on it.</summary>
    private static void WriteObservingTask(string taskDir, string[] dependsOn)
    {
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        WriteTaskJson(
            taskDir, writeScope: ["observed.txt"], dependsOn,
            "records into observed.txt whether the staged file was present when this task's own worktree was created");

        if (Windows)
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"),
                $"$target = Join-Path $env:GUARDRAILS_WORKSPACE \"{StagedFileName}\"\r\n" +
                $"if ((Test-Path $target) -and ((Get-Content -Raw -Path $target) -eq \"{StagedFileContent}\")) {{ $observed = \"found\" }} else {{ $observed = \"missing\" }}\r\n" +
                "Set-Content -NoNewline -Path (Join-Path $env:GUARDRAILS_WORKSPACE \"observed.txt\") -Value $observed\r\n" +
                "exit 0\r\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), "exit 0\r\n");
        }
        else
        {
            WriteExecutable(Path.Combine(taskDir, "action.sh"),
                "#!/usr/bin/env bash\n" +
                $"target=\"$GUARDRAILS_WORKSPACE/{StagedFileName}\"\n" +
                $"if [ -f \"$target\" ] && [ \"$(cat \"$target\")\" = \"{StagedFileContent}\" ]; then observed=\"found\"; else observed=\"missing\"; fi\n" +
                "printf '%s' \"$observed\" > \"$GUARDRAILS_WORKSPACE/observed.txt\"\n" +
                "exit 0\n");
            WriteExecutable(Path.Combine(taskDir, "guardrails", "01-check.sh"), "#!/usr/bin/env bash\nexit 0\n");
        }
    }

    /// <summary>An ordinary green no-op task (action and guardrail both exit 0).</summary>
    private static void WriteTrivialTask(string taskDir, string[] dependsOn, string description)
    {
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        WriteTaskJson(taskDir, writeScope: [], dependsOn, description);

        if (Windows)
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"), "exit 0\r\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), "exit 0\r\n");
        }
        else
        {
            WriteExecutable(Path.Combine(taskDir, "action.sh"), "#!/usr/bin/env bash\nexit 0\n");
            WriteExecutable(Path.Combine(taskDir, "guardrails", "01-check.sh"), "#!/usr/bin/env bash\nexit 0\n");
        }
    }

    /// <summary>A task whose GUARDRAIL halts needs-human until <paramref name="markerRelPath"/> (with <paramref name="markerContent"/>) is present on the base — the measured case's blocked task.</summary>
    private static void WriteMarkerGateTask(
        string taskDir, string[] dependsOn, string markerRelPath, string markerContent)
    {
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        WriteTaskJson(
            taskDir, writeScope: [], dependsOn,
            $"halts needs-human until {markerRelPath} lands on the base");

        if (Windows)
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"), "exit 0\r\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"),
                $"$target = Join-Path $env:GUARDRAILS_WORKSPACE \"{markerRelPath}\"\r\n" +
                $"if ((Test-Path $target) -and ((Get-Content -Raw -Path $target) -eq \"{markerContent}\")) {{ exit 0 }} else {{ exit 1 }}\r\n");
        }
        else
        {
            WriteExecutable(Path.Combine(taskDir, "action.sh"), "#!/usr/bin/env bash\nexit 0\n");
            WriteExecutable(Path.Combine(taskDir, "guardrails", "01-check.sh"),
                "#!/usr/bin/env bash\n" +
                $"target=\"$GUARDRAILS_WORKSPACE/{markerRelPath}\"\n" +
                $"if [ -f \"$target\" ] && [ \"$(cat \"$target\")\" = \"{markerContent}\" ]; then exit 0; else exit 1; fi\n");
        }
    }

    /// <summary>01-first stages <see cref="StagedFileName"/> mid-run; 02-second (depending on it) records what it found in its own worktree into `observed.txt` — used by the row-1 task-boundary test.</summary>
    private static string CreateTaskBoundaryObservationPlan(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        WriteGuardrailsJson(planDir);
        WriteStagingTask(Path.Combine(planDir, "tasks", "01-first"), dependsOn: []);
        WriteObservingTask(Path.Combine(planDir, "tasks", "02-second"), dependsOn: ["01-first"]);
        return planDir;
    }

    /// <summary>01-first stages <see cref="StagedFileName"/> mid-run; 02-second (depending on it) is a trivial pass — an unambiguous "next task boundary" for the drain to have fired at, with no observation of its own. Used where only the provenance record / observer announcement is under test.</summary>
    private static string CreateTwoTaskStagingPlan(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        WriteGuardrailsJson(planDir);
        WriteStagingTask(Path.Combine(planDir, "tasks", "01-first"), dependsOn: []);
        WriteTrivialTask(
            Path.Combine(planDir, "tasks", "02-second"), dependsOn: ["01-first"],
            "a trivial pass task — its scheduling is the task boundary the staged file must have been drained before");
        return planDir;
    }

    /// <summary>01-first is a trivial green task; 02-second (depending on it) halts needs-human until <paramref name="markerRelPath"/> lands on the base — the measured incident's shape, used by the row-2 run-start test and the settled-tasks-not-re-run test.</summary>
    private static string CreateHaltThenResumePlan(string repoPath, string markerRelPath, string markerContent)
    {
        string planDir = Path.Combine(repoPath, "plan");
        WriteGuardrailsJson(planDir);
        WriteTrivialTask(Path.Combine(planDir, "tasks", "01-first"), dependsOn: [], "a green task that settles before the halt");
        WriteMarkerGateTask(Path.Combine(planDir, "tasks", "02-second"), dependsOn: ["01-first"], markerRelPath, markerContent);
        return planDir;
    }

    // ── a real git repository, not a fake of one ───────────────────────────────────────────────────

    /// <summary>
    /// A throwaway single-use git repository in a temp directory (mirroring the shared idiom already
    /// duplicated across this project — there is no shared fixture project it references). Hooks are
    /// pointed at an empty directory inside <c>.git</c>, <c>autocrlf</c> is forced off, and
    /// <c>commit.gpgsign</c> is forced off, so a machine-global git config can never affect content
    /// bytes or block a commit in this throwaway repo.
    /// </summary>
    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _rootDir;
        public string RepoPath { get; }

        public TempGitRepo()
        {
            _rootDir = Path.Combine(Path.GetTempPath(), "gr-supply-boundary-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_rootDir, "repo");
            Directory.CreateDirectory(RepoPath);

            Git("init");
            string hooks = Path.Combine(RepoPath, ".git", "no-hooks");
            Directory.CreateDirectory(hooks);
            Git("config", "core.hooksPath", hooks);
            Git("config", "core.autocrlf", "false");
            Git("config", "commit.gpgsign", "false");
            Git("config", "user.email", "test@guardrails.local");
            Git("config", "user.name", "Guardrails Test");

            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# supply-boundary-wiring fixture");
            Git("add", "README.md");
            Git("commit", "-m", "Initial commit");
        }

        /// <summary>The full body (trailers included) of every commit reachable from <paramref name="branch"/>.</summary>
        public string LogBody(string branch) => Git("log", branch, "--format=%B");

        /// <summary>The content of <paramref name="path"/> as committed on <paramref name="branch"/>.</summary>
        public string Show(string branch, string path) => Git("show", $"{branch}:{path}");

        /// <summary><c>git cat-file -t</c> — proves <paramref name="sha"/> is a REAL object in this repo, not a fabricated placeholder string.</summary>
        public string CatFileType(string sha) => Git("cat-file", "-t", sha);

        private string Git(params string[] arguments)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(' ', arguments)} (in {RepoPath}) exited {process.ExitCode}: {stderr.Trim()}");
            }

            return stdout;
        }

        public void Dispose() => SafeDelete.DeleteDirectory(_rootDir);
    }
}
