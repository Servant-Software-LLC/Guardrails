using System.Diagnostics;
using System.Text;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Red-bar integration tests for plan 08 §4 / §9.1 / Stage-2 AI-merge worker.
/// Authored BEFORE the worker exists — all tests reference not-yet-existing types so the
/// project will NOT compile against current code. That compile failure IS the red-bar signal.
/// Do NOT implement the worker here; tests only.
///
/// Compile-fail couplings (not yet existing):
///   • <see cref="IAiMergeWorker"/> — interface consumed by the Scheduler
///   • <see cref="AiMergeWorker"/> — concrete class wrapping <see cref="IPromptRunner"/>
///   • <c>aiMergeWorker:</c> — named parameter on the <see cref="Scheduler"/> constructor
///
/// Fake AI runner pattern: thin <see cref="IPromptRunner"/> implementations that write
/// canned bytes to the path in <c>GUARDRAILS_MERGE_OUT</c> (read from
/// <see cref="PromptInvocation.Environment"/>) — no real claude process needed.
///
/// Five scenarios:
/// <list type="bullet">
///   <item><b>(i) CleanResolution_MergeEnvContract_ReVerifyIsVerdict_UnionSettles</b> —
///     worker receives MERGE_BASE/OURS/THEIRS on disk, writes clean bytes to MERGE_OUT,
///     returns IsError=true; harness reads MERGE_OUT (not IsError) and re-verify passes.</item>
///   <item><b>(ii) OutOfBoundsWrite_DetectedViaGitStatusPorcelain_Discarded_NeedsHuman</b> —
///     AI writes a file outside the conflicted set; harness detects via git status --porcelain.</item>
///   <item><b>(iii) ConflictMarkersLeft_DetectedViaGitDiffCheck_NeedsHuman</b> —
///     AI leaves conflict markers; harness detects via git diff --check.</item>
///   <item><b>(iv) BudgetExhausted_After1Retry_NeedsHuman</b> —
///     both attempts fail; runner called exactly twice; needs-human after budget exhausted.</item>
///   <item><b>(v) AiDeletedHunk_B3_CollidingSiblingReVerifyCatchesIt</b> —
///     AI drops colliding sibling's hunk; sibling's LOCAL guardrail re-runs UNCONDITIONALLY
///     and catches the drop; B-3 split assertion pins that wrong filter would miss it.</item>
/// </list>
/// </summary>
public sealed class AiMergeWorkerTests
{
    // ─── TempGitRepo ────────────────────────────────────────────────────────────────────────────
    // Windows-safe disposable git repo. Strips read-only bits before delete (.git/objects are
    // read-only on Windows → UnauthorizedAccessException without this). Pattern from
    // WriteScopeCheckTests.cs / MergeLockAndSettleTests.cs.

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-amw-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);
            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# ai-merge-test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public string CurrentBranch() =>
            Git(RepoPath, "rev-parse", "--abbrev-ref", "HEAD").Trim();

        public string HeadSha() =>
            Git(RepoPath, "rev-parse", "HEAD").Trim();

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
                    $"git {string.Join(" ", args)} (cwd={workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
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

    // ─── Fake IPromptRunner variants — byte producers for GUARDRAILS_MERGE_OUT ──────────────────
    // All fake runners write to GUARDRAILS_MERGE_OUT from invocation.Environment.
    // PromptResult.IsError is NEVER the verdict (SSOT §9.1) — the harness reads MERGE_OUT instead.

    /// <summary>
    /// Writes a canned merged resolution to GUARDRAILS_MERGE_OUT and returns IsError=true
    /// deliberately — proves the harness NEVER reads PromptResult.IsError as the verdict (SSOT §9.1).
    /// Also asserts the merge-env contract: MERGE_BASE, MERGE_OURS, MERGE_THEIRS, and MERGE_OUT
    /// must all be present in invocation.Environment.
    /// </summary>
    private sealed class CannedResolutionRunner : IPromptRunner
    {
        private readonly string _mergedContent;
        public List<PromptInvocation> Calls { get; } = new();

        public CannedResolutionRunner(string mergedContent) => _mergedContent = mergedContent;

        public string Name => "ai-merge-canned";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken ct)
        {
            Calls.Add(invocation);

            // Merge-env contract (SSOT §5.1 / §9.1): all four vars must be present
            Assert.True(invocation.Environment.ContainsKey("GUARDRAILS_MERGE_BASE"),
                "AiMergeWorker must set GUARDRAILS_MERGE_BASE in the prompt invocation environment.");
            Assert.True(invocation.Environment.ContainsKey("GUARDRAILS_MERGE_OURS"),
                "AiMergeWorker must set GUARDRAILS_MERGE_OURS in the prompt invocation environment.");
            Assert.True(invocation.Environment.ContainsKey("GUARDRAILS_MERGE_THEIRS"),
                "AiMergeWorker must set GUARDRAILS_MERGE_THEIRS in the prompt invocation environment.");
            Assert.True(invocation.Environment.ContainsKey("GUARDRAILS_MERGE_OUT"),
                "AiMergeWorker must set GUARDRAILS_MERGE_OUT in the prompt invocation environment.");

            // Write the resolution to MERGE_OUT (not PromptResult bytes — there are none)
            string mergeOut = invocation.Environment["GUARDRAILS_MERGE_OUT"];
            File.WriteAllText(mergeOut, _mergedContent);

            // IsError=true deliberately: the harness MUST NOT read this as failure (SSOT §9.1)
            return Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = true,
                Summary = "ai-merge-canned: IsError=true proves it is never the verdict"
            });
        }
    }

    /// <summary>
    /// Writes a clean resolution to GUARDRAILS_MERGE_OUT AND writes an extra file in the worker's
    /// working directory that was not in the git-conflicted set — simulating an out-of-bounds
    /// blast-radius violation. The harness must detect this via <c>git status --porcelain</c>.
    /// </summary>
    private sealed class OutOfBoundsWriter : IPromptRunner
    {
        private readonly string _mergedContent;
        private readonly string _extraFileName;   // relative to invocation.WorkingDirectory

        public OutOfBoundsWriter(string mergedContent, string extraFileName)
        {
            _mergedContent = mergedContent;
            _extraFileName = extraFileName;
        }

        public string Name => "ai-merge-out-of-bounds";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken ct)
        {
            // Write the in-bounds resolution
            string mergeOut = invocation.Environment["GUARDRAILS_MERGE_OUT"];
            File.WriteAllText(mergeOut, _mergedContent);

            // Also write an extra out-of-bounds file in the working dir (blast-radius violation)
            string extraPath = Path.Combine(invocation.WorkingDirectory, _extraFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(extraPath)!);
            File.WriteAllText(extraPath, "// out-of-bounds file written by AI — harness must detect");

            return Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                Summary = "ai-merge-out-of-bounds: wrote resolution + extra out-of-bounds file"
            });
        }
    }

    /// <summary>
    /// Always writes content containing <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> conflict markers
    /// to GUARDRAILS_MERGE_OUT, even on retry. Used for both the markers test (iii) and the
    /// budget-exhaustion test (iv).
    /// </summary>
    private sealed class MarkerLeavingRunner : IPromptRunner
    {
        public int CallCount { get; private set; }
        public string Name => "ai-merge-markers";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken ct)
        {
            CallCount++;
            string mergeOut = invocation.Environment["GUARDRAILS_MERGE_OUT"];
            // Write content with conflict markers — the harness must reject this
            File.WriteAllText(mergeOut,
                "// AI failed to resolve\n" +
                "<<<<<<< HEAD\n" +
                "class Shared { static string Get() => \"FuncA\"; }\n" +
                "=======\n" +
                "class Shared { static string Get() => \"FuncB\"; }\n" +
                ">>>>>>> sibling\n");

            // IsError=false (irrelevant) — the marker check is the mechanism, not IsError
            return Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                Summary = "ai-merge-markers: deliberately left conflict markers"
            });
        }
    }

    /// <summary>
    /// Writes a clean resolution to GUARDRAILS_MERGE_OUT but DROPS the colliding sibling's
    /// source hunk — keeps only task B's FuncB, silently removing task A's FuncA.
    /// No markers. In-bounds (only writes to MERGE_OUT, not the working directory).
    /// Used for the B-3 load-bearing test (v).
    /// </summary>
    private sealed class HunkDropperRunner : IPromptRunner
    {
        private readonly string _keptContent;
        public HunkDropperRunner(string keptContent) => _keptContent = keptContent;
        public string Name => "ai-merge-hunk-dropper";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken ct)
        {
            string mergeOut = invocation.Environment["GUARDRAILS_MERGE_OUT"];
            // Clean resolution: no markers, only writes to MERGE_OUT
            // BUT drops A's FuncA — keeps only B's FuncB
            File.WriteAllText(mergeOut, _keptContent);

            return Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                Summary = "ai-merge-hunk-dropper: clean resolution, sibling FuncA silently dropped"
            });
        }
    }

    /// <summary>
    /// Degenerate resolver (defect #120-followup gate iii): writes EMPTY/whitespace bytes to
    /// GUARDRAILS_MERGE_OUT on every call — exactly what an unreachable sandbox (the runner cannot
    /// write the file) or a no-op agent leaves behind. The harness must treat this as a FAILED
    /// attempt: an empty MERGE_OUT produces no markers (gate i) and no out-of-bounds write (gate ii),
    /// so without the explicit empty-out gate both prior gates pass vacuously and the conflicted
    /// file would be silently blanked. Used by EmptyMergeOut_IsRejected_NeedsHuman.
    /// </summary>
    private sealed class EmptyOutRunner : IPromptRunner
    {
        private readonly string _content;
        public int CallCount { get; private set; }

        /// <param name="content">Bytes written to MERGE_OUT — "" or whitespace only.</param>
        public EmptyOutRunner(string content) => _content = content;

        public string Name => "ai-merge-empty-out";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken ct)
        {
            CallCount++;
            string mergeOut = invocation.Environment["GUARDRAILS_MERGE_OUT"];
            File.WriteAllText(mergeOut, _content);
            return Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                Summary = "ai-merge-empty-out: degenerate (empty) resolution"
            });
        }
    }

    // ─── SpyReVerifier ────────────────────────────────────────────────────────────────────────────

    private sealed class SpyReVerifier : IReVerifier
    {
        public int CallCount { get; private set; }
        public bool AlwaysPass { get; init; }

        public Task<ReVerifyResult> ReVerifyAsync(
            string worktreePath,
            IReadOnlyList<GuardrailDefinition> guardrails,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(AlwaysPass
                ? new ReVerifyResult { Passed = true }
                : new ReVerifyResult
                {
                    Passed = false,
                    FailedGuardrails = [new GuardrailResult
                    {
                        Name = "spy",
                        Passed = false,
                        Reason = "spy: forced failure"
                    }]
                });
        }
    }

    // ─── Plan helpers ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a plan inside <paramref name="repoPath"/> with two sibling tasks (01-task-a,
    /// 02-task-b) that both write <c>src/conflict.cs</c> with different content, producing a
    /// git conflict when the second task integrates. maxParallelism: 2 enables worktree mode.
    /// </summary>
    private static string CreateConflictPlan(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(planDir);
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));

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

        WriteConflictTask(planDir, "01-task-a", "FuncA", b3SiblingGuardrail: false);
        WriteConflictTask(planDir, "02-task-b", "FuncB", b3SiblingGuardrail: false);
        return planDir;
    }

    /// <summary>
    /// Creates the B-3 conflict plan: same two-sibling conflict as
    /// <see cref="CreateConflictPlan"/> but task A additionally writes <c>src/a_tests.cs</c>
    /// and carries a <c>02-sibling-tests</c> guardrail that enforces cross-file consistency —
    /// failing when the AI drops A's FuncA from conflict.cs while a_tests.cs still references it.
    ///
    /// <paramref name="siblingGuardrailScope"/> controls whether the cross-file consistency
    /// guardrail is in the integration set: <c>"integration"</c> (the v1 B-3 net — it runs at
    /// the union re-verify and catches the dropped hunk) or <c>null</c> / <c>"local"</c> (the
    /// accepted v1 residual — a purely-local detector that the integration-set-only union
    /// re-verify does NOT run, so the drop settles green).
    /// </summary>
    private static string CreateB3ConflictPlan(string repoPath, string? siblingGuardrailScope)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(planDir);
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));

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

        WriteConflictTask(planDir, "01-task-a", "FuncA", b3SiblingGuardrail: true,
            siblingGuardrailScope: siblingGuardrailScope);
        WriteConflictTask(planDir, "02-task-b", "FuncB", b3SiblingGuardrail: false);
        return planDir;
    }

    private static void WriteConflictTask(
        string planDir, string taskId, string funcName, bool b3SiblingGuardrail,
        string? siblingGuardrailScope = null)
    {
        string taskDir = Path.Combine(planDir, "tasks", taskId);
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{"description": "ai-merge conflict test {{taskId}}", "dependsOn": []}""");

        string fragmentJson = "{\"" + taskId + "\": {\"done\": true}}";
        string safeName = taskId.Replace("-", "_");

        // Both tasks write src/conflict.cs with different content ("both added" git conflict).
        // Each also writes a unique file (no conflict) so the commit has unambiguous identity.
        string conflictContent = $"class Shared {{ static string Get() => \"{funcName}\"; }}";

        // Only task-a (when b3SiblingGuardrail=true) writes src/a_tests.cs.
        // This is the "file the merge did NOT touch" in the B-3 scenario.
        string aTestsContent = $"// a_tests: calls Shared.Get() — expects {funcName}";

        if (OperatingSystem.IsWindows())
        {
            var ps = new StringBuilder();
            ps.AppendLine($"Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value '{fragmentJson}'");
            ps.AppendLine($"New-Item -Path \"$env:GUARDRAILS_WORKSPACE\\src\\{taskId}.cs\" -Force -Value 'class {safeName} {{}}' | Out-Null");
            ps.AppendLine($"New-Item -Path \"$env:GUARDRAILS_WORKSPACE\\src\\conflict.cs\" -Force -Value '{conflictContent}' | Out-Null");
            if (b3SiblingGuardrail)
            {
                ps.AppendLine($"New-Item -Path \"$env:GUARDRAILS_WORKSPACE\\src\\a_tests.cs\" -Force -Value '{aTestsContent}' | Out-Null");
            }
            ps.AppendLine("exit 0");
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"), ps.ToString());

            // Guardrail 01: always passes
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), "exit 0\n");

            if (b3SiblingGuardrail)
            {
                // Guardrail 02: B-3 load-bearing cross-file consistency check.
                // Reads src/a_tests.cs AND src/conflict.cs using paths relative to cwd (= workspace
                // during both task-exec and re-verify, so GUARDRAILS_WORKSPACE is not needed).
                // PASSES: a_tests.cs references FuncA AND conflict.cs has FuncA (A's own segment).
                // FAILS:  a_tests.cs references FuncA BUT conflict.cs has only FuncB (AI dropped A).
                File.WriteAllText(Path.Combine(taskDir, "guardrails", "02-sibling-tests.ps1"),
                    "$testFile = 'src\\a_tests.cs'\n" +
                    "$srcFile  = 'src\\conflict.cs'\n" +
                    "$testRefsFuncA = (Select-String -Path $testFile -Pattern 'FuncA' -Quiet) -eq $true\n" +
                    "$srcHasFuncA   = (Select-String -Path $srcFile  -Pattern 'FuncA' -Quiet) -eq $true\n" +
                    "if ($testRefsFuncA -and (-not $srcHasFuncA)) {\n" +
                    "    Write-Output 'a_tests.cs references FuncA but conflict.cs is missing it — AI dropped sibling hunk'\n" +
                    "    exit 1\n" +
                    "}\n" +
                    "exit 0\n");
                WriteSiblingGuardrailSidecar(taskDir, siblingGuardrailScope);
            }
        }
        else
        {
            var sh = new StringBuilder("#!/usr/bin/env bash\n");
            sh.AppendLine($"printf '%s' '{fragmentJson}' > \"$GUARDRAILS_STATE_OUT\"");
            sh.AppendLine("mkdir -p \"$GUARDRAILS_WORKSPACE/src\"");
            sh.AppendLine($"printf '%s' '{conflictContent}' > \"$GUARDRAILS_WORKSPACE/src/conflict.cs\"");
            sh.AppendLine($"printf '%s' 'class {safeName} {{}}' > \"$GUARDRAILS_WORKSPACE/src/{taskId}.cs\"");
            if (b3SiblingGuardrail)
            {
                sh.AppendLine($"printf '%s' '{aTestsContent}' > \"$GUARDRAILS_WORKSPACE/src/a_tests.cs\"");
            }
            sh.AppendLine("exit 0");
            string ap = Path.Combine(taskDir, "action.sh");
            File.WriteAllText(ap, sh.ToString());
            SetExecutable(ap);

            string g1 = Path.Combine(taskDir, "guardrails", "01-check.sh");
            File.WriteAllText(g1, "#!/usr/bin/env bash\nexit 0\n");
            SetExecutable(g1);

            if (b3SiblingGuardrail)
            {
                // Paths relative to cwd (= workspace during both task-exec and re-verify)
                string g2 = Path.Combine(taskDir, "guardrails", "02-sibling-tests.sh");
                File.WriteAllText(g2,
                    "#!/usr/bin/env bash\n" +
                    "testFile='src/a_tests.cs'\n" +
                    "srcFile='src/conflict.cs'\n" +
                    "if grep -q 'FuncA' \"$testFile\" && ! grep -q 'FuncA' \"$srcFile\"; then\n" +
                    "    echo 'a_tests.cs references FuncA but conflict.cs is missing it — AI dropped sibling hunk'\n" +
                    "    exit 1\n" +
                    "fi\n" +
                    "exit 0\n");
                SetExecutable(g2);
                WriteSiblingGuardrailSidecar(taskDir, siblingGuardrailScope);
            }
        }
    }

    /// <summary>
    /// Writes the metadata sidecar for the <c>02-sibling-tests</c> guardrail so it declares the
    /// requested <paramref name="scope"/> (§4.1). When <paramref name="scope"/> is null no sidecar
    /// is written, leaving the guardrail at the default <c>local</c> scope (the accepted-residual
    /// case: a purely-local detector that the integration-set-only union re-verify never runs).
    /// </summary>
    private static void WriteSiblingGuardrailSidecar(string taskDir, string? scope)
    {
        if (scope is null)
        {
            return;
        }

        // The sidecar pairs with the guardrail by basename (02-sibling-tests.json next to
        // 02-sibling-tests.ps1/.sh), independent of the script extension (§4.1).
        File.WriteAllText(
            Path.Combine(taskDir, "guardrails", "02-sibling-tests.json"),
            $$"""{ "scope": "{{scope}}" }""");
    }

    private static void SetExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    // ─── Run helper ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads <paramref name="planDir"/>, wires the AI merge worker and re-verifier into the
    /// <see cref="Scheduler"/>, and runs to completion.
    ///
    /// The <c>aiMergeWorker:</c> named argument IS the compile-fail coupling —
    /// <see cref="IAiMergeWorker"/> and the <c>Scheduler(aiMergeWorker:)</c> parameter do not
    /// yet exist on current code.
    /// </summary>
    private static async Task<(RunReport report, RunJournal journal)> RunWithAiMergeAsync(
        string planDir,
        IWorktreeProvider worktreeProvider,
        IAiMergeWorker aiMergeWorker,   // COMPILE ERROR: IAiMergeWorker does not yet exist
        IReVerifier reVerifier,
        CancellationToken ct = default)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();

        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);

        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);
        var registry = PromptRunnerRegistry.Build(load.Plan!.Config,
            _ => throw new InvalidOperationException("No prompt runners in AI-merge worker tests."));

        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap,
            stateManager, journal, IRunObserver.Null, registry);

        // ── COMPILE ERROR on current code: Scheduler has no 'aiMergeWorker' parameter ──────────
        var scheduler = new Scheduler(
            load.Plan!, executor, journal,
            worktreeProvider: worktreeProvider,
            reVerifier: reVerifier,
            aiMergeWorker: aiMergeWorker);

        RunReport report = await scheduler.RunAsync(load.Plan!, ct);
        return (report, journal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // (i) byte-producer + merge-env-contract + re-verify is the verdict
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 §9.1 byte-producer + merge-env-contract: the worker receives
    /// GUARDRAILS_MERGE_BASE / MERGE_OURS / MERGE_THEIRS on disk and writes its resolution
    /// to GUARDRAILS_MERGE_OUT only. The harness reads MERGE_OUT — NOT PromptResult bytes
    /// (there are none). PromptResult.IsError is deliberately true to prove it is NEVER the
    /// verdict (SSOT §9.1: "the re-verify is the verdict").
    ///
    /// After a clean AI resolution (no markers, in-bounds), the union re-verify passes and
    /// both tasks succeed.
    /// </summary>
    [Fact]
    public async Task CleanResolution_MergeEnvContract_ReVerifyIsVerdict_UnionSettles()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateConflictPlan(repo.RepoPath);

        // CannedResolutionRunner returns IsError=true — harness must ignore this, read MERGE_OUT
        var runner = new CannedResolutionRunner(
            mergedContent: "class Shared { static string Get() => \"FuncA+FuncB\"; }");
        var spyReVerifier = new SpyReVerifier { AlwaysPass = true };

        // ── COMPILE ERROR: AiMergeWorker and Scheduler(aiMergeWorker:) do not yet exist ────────
        var aiMergeWorker = new AiMergeWorker(runner);
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        var (report, _) = await RunWithAiMergeAsync(
            planDir, provider, aiMergeWorker, spyReVerifier,
            TestContext.Current.CancellationToken);

        // Union settled: IsError=true on the runner did NOT cause failure
        Assert.True(report.AllSucceeded,
            "Clean AI resolution with passing re-verify must settle. " +
            "PromptResult.IsError=true must be ignored — MERGE_OUT is the only read channel (SSOT §9.1). " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        // Re-verify was called for the non-FF union (the second sibling's integration)
        Assert.True(spyReVerifier.CallCount > 0,
            "Re-verify must be invoked for the non-FF union — it is the verdict, not PromptResult.IsError.");

        // Merge-env contract was enforced (asserted inside CannedResolutionRunner.RunAsync)
        Assert.True(runner.Calls.Count > 0,
            "The AI merge worker must have called the prompt runner for the non-FF conflict.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // (ii) out-of-bounds write → detected via git status --porcelain → needs-human
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 §9.1 blast-radius check: when the AI writes any file OUTSIDE the git-conflicted
    /// set, the harness detects it via <c>git status --porcelain</c>, discards the resolution
    /// via <c>reset --hard</c>, and escalates to needs-human. The blast-radius check fires
    /// BEFORE re-verify (SpyReVerifier call count must remain zero).
    /// </summary>
    [Fact]
    public async Task OutOfBoundsWrite_DetectedViaGitStatusPorcelain_Discarded_NeedsHuman()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateConflictPlan(repo.RepoPath);
        string initialHead = repo.HeadSha();

        var writer = new OutOfBoundsWriter(
            mergedContent: "class Shared { static string Get() => \"FuncA+FuncB\"; }",
            extraFileName: "ai-wrote-this-out-of-bounds.tmp");  // not in the conflicted set

        var spyReVerifier = new SpyReVerifier { AlwaysPass = true };

        // ── COMPILE ERROR: AiMergeWorker and Scheduler(aiMergeWorker:) do not yet exist ────────
        var aiMergeWorker = new AiMergeWorker(writer);
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        var (report, _) = await RunWithAiMergeAsync(
            planDir, provider, aiMergeWorker, spyReVerifier,
            TestContext.Current.CancellationToken);

        // One task NeedsHuman (the second-to-integrate whose AI merge went out-of-bounds)
        Assert.Single(report.Tasks, t => t.Outcome == TaskOutcome.NeedsHuman);
        Assert.Single(report.Tasks, t => t.Outcome == TaskOutcome.Succeeded);

        // Blast-radius check fires BEFORE re-verify — spy call count must stay zero
        Assert.Equal(0, spyReVerifier.CallCount);

        // Rollback must not alter the user's branch
        Assert.Equal(initialHead, repo.HeadSha());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // (iii) conflict markers left → detected via git diff --check → needs-human
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 §9.1 conflict-marker check: when GUARDRAILS_MERGE_OUT contains
    /// <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> conflict markers, the harness detects them via
    /// <c>git diff --check</c> and escalates to needs-human. PromptResult.IsError=false
    /// is irrelevant — the marker check is the mechanism, not IsError.
    /// </summary>
    [Fact]
    public async Task ConflictMarkersLeft_DetectedViaGitDiffCheck_NeedsHuman()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateConflictPlan(repo.RepoPath);
        string initialHead = repo.HeadSha();

        var markerRunner = new MarkerLeavingRunner();
        var spyReVerifier = new SpyReVerifier { AlwaysPass = true };

        // ── COMPILE ERROR: AiMergeWorker and Scheduler(aiMergeWorker:) do not yet exist ────────
        var aiMergeWorker = new AiMergeWorker(markerRunner);
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        var (report, _) = await RunWithAiMergeAsync(
            planDir, provider, aiMergeWorker, spyReVerifier,
            TestContext.Current.CancellationToken);

        // Conflict markers detected — one task NeedsHuman
        Assert.Single(report.Tasks, t => t.Outcome == TaskOutcome.NeedsHuman);
        Assert.Single(report.Tasks, t => t.Outcome == TaskOutcome.Succeeded);

        // Marker check fires before (or instead of) re-verify
        Assert.Equal(0, spyReVerifier.CallCount);

        // Rollback must not alter the user's branch
        Assert.Equal(initialHead, repo.HeadSha());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // (iv) budget exhausted after 1 retry → needs-human
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 §9.1 retry budget: the AI merge worker gets exactly 1 retry (2 total attempts).
    /// When BOTH attempts leave conflict markers, the harness escalates to needs-human after
    /// the second failure — no third attempt. The runner is called exactly twice.
    /// </summary>
    [Fact]
    public async Task BudgetExhausted_After1Retry_NeedsHuman()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateConflictPlan(repo.RepoPath);

        // MarkerLeavingRunner always fails — both attempt 1 and the 1 retry produce markers
        var markerRunner = new MarkerLeavingRunner();
        var spyReVerifier = new SpyReVerifier { AlwaysPass = true };

        // ── COMPILE ERROR: AiMergeWorker and Scheduler(aiMergeWorker:) do not yet exist ────────
        var aiMergeWorker = new AiMergeWorker(markerRunner);
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        var (report, _) = await RunWithAiMergeAsync(
            planDir, provider, aiMergeWorker, spyReVerifier,
            TestContext.Current.CancellationToken);

        // Budget exhausted → NeedsHuman
        Assert.Single(report.Tasks, t => t.Outcome == TaskOutcome.NeedsHuman);

        // Exactly 2 calls: attempt 1 + 1 retry. No third attempt (SSOT §9.1: "1 retry").
        Assert.Equal(2, markerRunner.CallCount);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // (iv-b) degenerate resolution: empty MERGE_OUT → rejected (3rd gate) → needs-human
    // Regression for defect #120-followup gate iii. Method name greppable.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Defect #120-followup, third deterministic gate: an empty (or whitespace-only) MERGE_OUT is
    /// a FAILED merge attempt, never a vacuous pass. The runner writes "" to MERGE_OUT (what an
    /// unreachable sandbox or a no-op agent leaves). Without the empty-out gate, overwriting the
    /// conflicted file with "" yields no markers (gate i) and no extra files (gate ii), so both
    /// prior gates would pass and the file's content would be silently deleted. The resolver must
    /// instead reject the attempt; after the 1-retry budget the union halts to needs-human with a
    /// clean rollback, and the conflicted file is NOT blanked on the plan branch.
    /// </summary>
    [Fact]
    public async Task EmptyMergeOut_IsRejected_NeedsHuman()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateConflictPlan(repo.RepoPath);
        string initialHead = repo.HeadSha();

        // Writes EMPTY bytes — the precise degenerate case the third gate defends: a truly empty
        // MERGE_OUT blanks the conflict file, which then passes git diff --check (no markers, no
        // whitespace errors) and the blast-radius check — a vacuous pass WITHOUT the empty-out gate.
        var emptyRunner = new EmptyOutRunner("");
        var spyReVerifier = new SpyReVerifier { AlwaysPass = true };

        var aiMergeWorker = new AiMergeWorker(emptyRunner);
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        var (report, _) = await RunWithAiMergeAsync(
            planDir, provider, aiMergeWorker, spyReVerifier,
            TestContext.Current.CancellationToken);

        // Empty resolution rejected on both attempts → needs-human (one task succeeds, the colliding
        // sibling halts).
        _ = Assert.Single(report.Tasks, t => t.Outcome == TaskOutcome.NeedsHuman);
        _ = Assert.Single(report.Tasks, t => t.Outcome == TaskOutcome.Succeeded);

        // 1-retry budget: exactly two degenerate attempts, no third.
        Assert.Equal(2, emptyRunner.CallCount);

        // The empty-out gate fires BEFORE re-verify — an empty resolution never reaches it.
        Assert.Equal(0, spyReVerifier.CallCount);

        // Safety: rollback left the user's branch untouched, and the first sibling's conflict.cs
        // content survives on the plan branch (the file was NOT silently blanked).
        Assert.Equal(initialHead, repo.HeadSha());
        string planName = Path.GetFileName(planDir);
        string conflictOnPlan = TempGitRepo.Git(
            repo.RepoPath, "show", $"guardrails/{planName}:src/conflict.cs");
        Assert.False(string.IsNullOrWhiteSpace(conflictOnPlan),
            "An empty MERGE_OUT must never blank the conflicted file: the first-integrated sibling's " +
            "content must survive on the plan branch.");
        Assert.Contains("Shared", conflictOnPlan, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // (v) B-3 v1 doctrine: AI-deleted-hunk → INTEGRATION-set re-verify catches it
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 §4.3 B-3, v1 integration-set-only union re-verify contract: the AI resolves the
    /// conflict by DROPPING the colliding sibling's (task A's) source hunk — keeping only task B's
    /// FuncB. The resolution has no conflict markers and is in-bounds. Task B's own guardrails pass
    /// on the merged bytes.
    ///
    /// <para>Under the v1 contract the AI-merge re-verify runs ONLY the run's integration-guardrail
    /// set (§4.3) — the SAME set as the non-AI-merge union path — NOT every colliding sibling's full
    /// local guardrail set. The dropped hunk is therefore caught only when the cross-file consistency
    /// detector is in the integration set. Here task A's <c>02-sibling-tests</c> guardrail is marked
    /// <c>scope:"integration"</c> (via its sidecar), so it IS in the integration set and runs at the
    /// union: it detects that <c>src/a_tests.cs</c> still references FuncA while <c>src/conflict.cs</c>
    /// no longer contains FuncA, FAILS, and the union settles NeedsHuman.</para>
    ///
    /// <para>This is the real v1 B-3 net being pinned: a well-authored integration/union-verify
    /// guardrail catches a dropped hunk. The companion
    /// <see cref="AiDeletedHunk_LocalOnlyCoverage_IsAcceptedResidual_SettlesGreen"/> pins the honest
    /// residual — a purely-local detector is NOT run at the union, so the same drop settles green.
    /// The dormant <see cref="GuardrailScopeFilter.ShouldRunAtUnion"/> predicate logic is unit-pinned
    /// in <c>GuardrailScopeTests</c>, not here.</para>
    /// </summary>
    [Fact]
    public async Task AiDeletedHunk_B3_IntegrationSetReVerifyCatchesIt()
    {
        using var repo = new TempGitRepo();
        // 02-sibling-tests is integration-scoped → in the integration set → runs at the union.
        string planDir = CreateB3ConflictPlan(repo.RepoPath, siblingGuardrailScope: "integration");
        string initialHead = repo.HeadSha();

        // AI drops A's FuncA, keeps B's FuncB. No markers. In-bounds (only writes to MERGE_OUT).
        var hunkDropper = new HunkDropperRunner(
            keptContent: "class Shared { static string Get() => \"FuncB\"; }");

        // Use the REAL re-verifier so that 02-sibling-tests.ps1/.sh actually executes.
        // A SpyReVerifier would not run the script and would not catch the hunk drop.
        var reVerifier = new GuardrailReVerifier(
            new ProcessRunner(),
            new InterpreterMap(new PathExecutableProbe()));

        var aiMergeWorker = new AiMergeWorker(hunkDropper);
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        var (report, _) = await RunWithAiMergeAsync(
            planDir, provider, aiMergeWorker, reVerifier,
            TestContext.Current.CancellationToken);

        // B-3 caught the hunk drop via the integration set: the union is NeedsHuman.
        _ = Assert.Single(report.Tasks, t => t.Outcome == TaskOutcome.NeedsHuman);
        _ = Assert.Single(report.Tasks, t => t.Outcome == TaskOutcome.Succeeded);

        // Rollback must not alter the user's branch.
        Assert.Equal(initialHead, repo.HeadSha());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // (v-b) B-3 accepted residual: a LOCAL-only dropped-hunk detector is NOT run at the union
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plan 08 §4.3 accepted v1 residual (#132): the AI-merge re-verify runs ONLY the integration
    /// set. A cross-file consistency detector left at the default <c>local</c> scope (no sidecar,
    /// not in the integration set) is NOT re-run at the union, so an AI-dropped colliding-sibling
    /// hunk that ONLY a local guardrail would catch settles GREEN.
    ///
    /// <para>This is the honest counterpart of
    /// <see cref="AiDeletedHunk_B3_IntegrationSetReVerifyCatchesIt"/>: it makes the v1 weakening
    /// visible and asserted rather than silent. The fixture is identical EXCEPT task A's
    /// <c>02-sibling-tests</c> guardrail is local (the dropped-hunk detector that would have caught
    /// the drop is purely local), and there is no integration-scoped guardrail covering the dropped
    /// code and no downstream dependent — so all three v1 B-3 nets miss it. When the deferred
    /// union-safe colliding-sibling re-verify (#132) is built, this test flips to NeedsHuman and
    /// becomes the red-bar for that work.</para>
    /// </summary>
    [Fact]
    public async Task AiDeletedHunk_LocalOnlyCoverage_IsAcceptedResidual_SettlesGreen()
    {
        using var repo = new TempGitRepo();
        // 02-sibling-tests stays LOCAL (siblingGuardrailScope: null → no sidecar) → NOT in the
        // integration set → NOT run at the union re-verify. No other integration coverage exists.
        string planDir = CreateB3ConflictPlan(repo.RepoPath, siblingGuardrailScope: null);

        // AI drops A's FuncA, keeps B's FuncB. No markers. In-bounds (only writes to MERGE_OUT).
        var hunkDropper = new HunkDropperRunner(
            keptContent: "class Shared { static string Get() => \"FuncB\"; }");

        // REAL re-verifier — but with no integration-scoped guardrail it has nothing to run that
        // would catch the drop; the LOCAL 02-sibling-tests is deliberately NOT in the set.
        var reVerifier = new GuardrailReVerifier(
            new ProcessRunner(),
            new InterpreterMap(new PathExecutableProbe()));

        var aiMergeWorker = new AiMergeWorker(hunkDropper);
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        var (report, _) = await RunWithAiMergeAsync(
            planDir, provider, aiMergeWorker, reVerifier,
            TestContext.Current.CancellationToken);

        // Accepted v1 residual: the local-only dropped-hunk detector is NOT run at the union, so
        // the drop is NOT caught — both tasks settle green. This documents the known weakening.
        Assert.True(report.AllSucceeded,
            "Accepted v1 residual (#132): the integration-set-only union re-verify does not run a " +
            "purely-local dropped-hunk detector, so an AI-dropped colliding-sibling hunk covered " +
            "ONLY by a local guardrail settles green. " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));
    }
}
