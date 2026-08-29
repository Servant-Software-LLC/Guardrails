using System.CommandLine;
using System.Diagnostics;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;

namespace Guardrails.Integration.Tests.Samples;

/// <summary>
/// The COMPOSITION-ROOT wiring tests for the sample-pair step of the pre-DAG plan-preflight phase
/// (plan of record 26, §3/§4 — issue #510). A <c>tasks/&lt;id&gt;/samples/</c> pair asserts exactly two
/// facts — the <c>.valid</c> half's guardrail exits 0, the <c>.invalid</c> half's exits non-zero — and
/// today those facts are recorded in a folder that nothing ever executes. Task 05 wires the verifier
/// into <see cref="PlanPreflightPhase"/> so a bad pair halts a run BEFORE any task spends a token; this
/// file is what makes that wiring provable.
///
/// <para>
/// <b>Why these live in <c>Guardrails.Integration.Tests</c>.</b> <see cref="PlanPreflightPhase"/> is in
/// the <c>Guardrails.Cli</c> assembly, which <c>tests/Guardrails.Core.Tests</c> does not reference — a
/// wiring test there could only construct the verifier by hand, which is the unwired-factory failure
/// with extra steps (#120). This project references both, and is already the home of
/// <c>PlanPreflightPhaseTests</c>, whose <c>TempGitRepo</c> / <c>CreatePlan</c> / <c>WriteScript</c> /
/// <c>RunCliAsync</c> / <c>ReadJournal</c> idiom the helpers below copy (they are private to that class,
/// so these are deliberate local copies).
/// </para>
///
/// <para>
/// <b>Every test drives the REAL seam.</b> Four call <see cref="PlanPreflightPhase.EvaluateAsync"/>
/// directly and assert on what IT returned and journaled; the fifth drives the real <c>run</c> entry.
/// None of them constructs or invokes the verifier itself: a test that runs the verifier and asserts on
/// its own findings is green whether or not the phase was ever changed, and — since the verifier already
/// landed in task 02 — would also be green TODAY, which is the opposite of what this file is for.
/// </para>
///
/// <para>
/// <b>Four of the five are RED against the phase as it stands today, deliberately.</b> The unwired
/// <c>EvaluateAsync</c> never looks at <c>samples/</c>: it returns true at its first line for a plan with
/// no <c>&lt;plan&gt;/preflights/</c> folder, and otherwise returns the verdict of the (green) Full
/// Flight Checks. <see cref="EvaluateAsync_ReturnsTrue_WhenEverySamplePairIsSound"/> is the one declared
/// exception — it legitimately passes today AND after task 05 lands, because a correct implementation
/// must not halt a sound pair. It is the only thing standing between task 05 and a phase that returns
/// false unconditionally, so it is written honestly rather than forced red.
/// </para>
///
/// <para>
/// Tagged <c>[Trait("Category", "BacklogSlate")]</c> (class and method level) so the per-task census can
/// select exactly this class, and so the plan's green baseline — which excludes that trait — cannot
/// mistake these deliberately-red tests for pre-existing breakage.
/// </para>
/// </summary>
[Trait("Category", "BacklogSlate")]
public sealed class SampleVerifierWiringTests
{
    /// <summary>
    /// The base name shared by the guardrail script and its two sample halves. Distinctive on purpose:
    /// <see cref="EvaluateAsync_JournalsTheFailingPair_SoAPostMortemReaderCanSeeWhichPairHalted"/> asserts
    /// this string reaches <c>state/run.json</c>, and nothing else the journal records today contains it.
    /// </summary>
    private const string PairName = "01-polarity-check";

    private const string TaskId = "01-authored";

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #1 — a reversed committed pair HALTS the pre-DAG phase.
    //
    // The plan here DOES declare <plan>/preflights/, and those checks are GREEN, so the only thing that
    // can make EvaluateAsync return false is the sample pair. Against today's phase the green Full Flight
    // Checks are the only thing evaluated and it returns true → RED, which is the intent.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task EvaluateAsync_ReturnsFalse_WhenACommittedSamplePairIsReversed()
    {
        using var fixture = new SamplePlanFixture();
        fixture.AddGreenPlanPreflight();
        fixture.AddTaskWithSamplePair(TaskId, PairName, reversed: true);

        PlanDefinition plan = fixture.Load();

        // Fixture sanity: the plan really did opt into Full Flight Checks, so "returned true" cannot be
        // explained by the no-preflights short-circuit — the pair is the only red thing in this plan.
        Assert.NotEmpty(plan.PlanPreflights);

        RunJournal journal = RunJournal.LoadOrCreate(plan);

        bool proceed = await PlanPreflightPhase.EvaluateAsync(
            plan, journal, new ProcessRunner(), heartbeatOut: null, CancellationToken.None);

        Assert.False(
            proceed,
            $"the committed pair '{PairName}' is REVERSED — its .valid half exits non-zero and its " +
            ".invalid half exits 0 — so the pre-DAG phase must refuse to schedule the DAG. It returned " +
            "true, which means PlanPreflightPhase.EvaluateAsync never executed the pair: the sample " +
            "folder is still a claim recorded on disk that nothing runs (#510).");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #2 — THE PLACEMENT TRAP, pinned. `if (plan.PlanPreflights.Count == 0) { return true; }` is the
    // FIRST statement of EvaluateAsync, and most plans in this repo declare no preflights/ folder at
    // all. A sample-verification step placed after that early return would protect only the plans that
    // already opted into Full Flight Checks — the plans least likely to need it. This is the one test
    // that stays red if task 05 gets the placement wrong while everything else goes green.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task EvaluateAsync_HaltsOnABadSamplePair_EvenWhenThePlanDeclaresNoPreflightsFolder()
    {
        using var fixture = new SamplePlanFixture();
        fixture.AddTaskWithSamplePair(TaskId, PairName, reversed: true);

        PlanDefinition plan = fixture.Load();

        // Fixture sanity: this is the no-preflights shape, i.e. the one the first short-circuit returns
        // true for. If this were non-empty the test would silently degrade into a copy of #1.
        Assert.Empty(plan.PlanPreflights);

        RunJournal journal = RunJournal.LoadOrCreate(plan);

        bool proceed = await PlanPreflightPhase.EvaluateAsync(
            plan, journal, new ProcessRunner(), heartbeatOut: null, CancellationToken.None);

        Assert.False(
            proceed,
            "this plan declares NO <plan>/preflights/ folder — the shape most plans in this repo have — " +
            $"and its committed pair '{PairName}' is reversed. EvaluateAsync returned true, so the " +
            "sample-pair step is either absent or sits AFTER the `plan.PlanPreflights.Count == 0` early " +
            "return, which would gate only the plans that already opted into Full Flight Checks. Verify " +
            "the pairs BEFORE both short-circuits.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #3 — THE DECLARED EXCEPTION. A sound pair must NOT halt. This passes against today's unwired
    // phase (no preflights/ folder ⇒ true at the first line) and must still pass once task 05 lands;
    // demanding it be red would demand that a correct implementation fail. Without it, task 05 could
    // return false unconditionally and every other test here would still be satisfied.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task EvaluateAsync_ReturnsTrue_WhenEverySamplePairIsSound()
    {
        using var fixture = new SamplePlanFixture();
        fixture.AddTaskWithSamplePair(TaskId, PairName, reversed: false);

        PlanDefinition plan = fixture.Load();
        Assert.Empty(plan.PlanPreflights);

        RunJournal journal = RunJournal.LoadOrCreate(plan);

        bool proceed = await PlanPreflightPhase.EvaluateAsync(
            plan, journal, new ProcessRunner(), heartbeatOut: null, CancellationToken.None);

        Assert.True(
            proceed,
            $"the committed pair '{PairName}' is genuinely two-sided — the SAME guardrail exits 0 for " +
            "the .valid half and non-zero for the .invalid half — so the pre-DAG phase must let the DAG " +
            "schedule. A phase that halts here halts every correctly-authored plan in the repo.");

        // A sound plan must not be journaled as halted either: the failure record belongs to a run that
        // actually failed a gate, and a spurious one would read as a halt to every post-mortem tool.
        JournalDocument document = ReadJournal(plan.PlanDirectory);
        Assert.Null(document.Halt);
        Assert.NotEqual(PlanPhaseStatus.PlanPreflightFailed, document.PlanPreflights?.Status);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #4 — the halt has to survive the operator's scrollback. A pre-DAG halt settles NO task, so
    // tasks{} is a wall of silent `pending` entries with nothing explaining why (#432). The durable
    // record must NAME the offending pair, or a post-mortem reader who never saw the console cannot
    // tell which of a plan's pairs stopped the run.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task EvaluateAsync_JournalsTheFailingPair_SoAPostMortemReaderCanSeeWhichPairHalted()
    {
        using var fixture = new SamplePlanFixture();
        fixture.AddTaskWithSamplePair(TaskId, PairName, reversed: true);

        PlanDefinition plan = fixture.Load();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        bool proceed = await PlanPreflightPhase.EvaluateAsync(
            plan, journal, new ProcessRunner(), heartbeatOut: null, CancellationToken.None);

        string journalPath = RunJournal.PathFor(plan.PlanDirectory);
        Assert.True(File.Exists(journalPath), $"{journalPath} was never written");

        // "Records the failure" is asserted against the journal's own machinery rather than a spelling:
        // the phase's existing failure posture is a plan-preflight-failed section plus the uniform
        // top-level `halt` record, and either one is a durable, machine-readable statement that this run
        // stopped at a gate. What is NOT acceptable is a journal that says nothing at all, which is what
        // the unwired phase leaves behind.
        JournalDocument document = JournalReader.Read(journalPath);
        bool recordsTheHalt =
            document.PlanPreflights?.Status == PlanPhaseStatus.PlanPreflightFailed
            || document.Halt is not null;
        Assert.True(
            recordsTheHalt,
            "the run was halted by a bad sample pair but state/run.json records no failure — neither a " +
            "plan-preflight-failed planPreflights section nor a top-level halt. A halt whose only trace " +
            "is the operator's scrollback is the #432 failure repeating: every task is left `pending` " +
            "with nothing saying why.");

        // ...and the record must NAME the pair. A generic "a sample pair failed" cannot tell an operator
        // which of a plan's pairs to open, which is the whole point of journaling it.
        string recorded = File.ReadAllText(journalPath);
        Assert.Contains(PairName, recorded, StringComparison.Ordinal);

        Assert.False(proceed, "a reversed pair must also stop the run, not merely be written down");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #5 — the plan's actual Done-when, observed end-to-end: drive the REAL `run` entry (which builds
    // its scheduler through the production SchedulerFactory) over a temp git repo and prove the halt
    // lands BEFORE the DAG. Exit 2 alone is not enough — a run that failed the task's guardrail also
    // exits 2 — so the zero-attempts assertion is what says "no task spent a token".
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task Run_HaltsBeforeSchedulingAnyTask_WhenAPlansCommittedSamplePairIsReversed()
    {
        using var repo = new TempGitRepo();
        string planDir = CreatePlan(repo.RepoPath);
        WriteTaskWithSamplePair(planDir, TaskId, PairName, reversed: true);

        int exit = await RunCliAsync(planDir);

        Assert.Equal(ExitCodes.TaskFailed, exit);

        JournalDocument journal = ReadJournal(planDir);

        // The plan's tasks are seeded into the journal by LoadOrCreate, so an empty tasks{} would mean
        // the fixture never loaded rather than that nothing ran.
        Assert.NotEmpty(journal.Tasks);

        // Zero-token halt: the run stopped before the Scheduler built a wave, so not one attempt was
        // journaled for any task. On today's unwired phase the DAG runs to completion instead.
        Assert.All(journal.Tasks.Values, entry => Assert.Empty(entry.Attempts));
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Fixtures. Everything is built in a temp directory and deleted on Dispose; nothing is ever written
    // into the repository tree and no fixture points at a real plan folder.
    //
    // The guardrail's exit code is a function of the SUBJECT it is handed, never a hard-coded exit line
    // — so a pair's polarity is a property of the two SAMPLE FILES and "sound" and "reversed" are the
    // same script over swapped content. A guardrail that ignored the subject would give both halves the
    // same exit code, and the sound-pair test and the reversed-pair test would be one test written
    // twice.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    private static readonly bool Ps = OperatingSystem.IsWindows();

    private static string ScriptExtension => Ps ? ".ps1" : ".sh";

    /// <summary>The marker the guardrail rejects. Present in exactly one half of every pair.</summary>
    private const string DefectMarker = "DEFECT";

    private const string CleanSample = "a representative correct artifact - nothing wrong with it";
    private const string DefectiveSample = "an artifact carrying the " + DefectMarker + " this guardrail exists to reject";

    /// <summary>
    /// Reads the subject BOTH ways the committed corpus binds one — the <c>GR_SUBJECT</c> environment
    /// variable, and the run's first positional argument — and exits non-zero exactly when that subject
    /// carries the defect marker. With NO subject bound (a normal task-guardrail run, which passes
    /// neither) it exits 0, so this fixture is a perfectly ordinary green guardrail outside sample
    /// verification and cannot fail a run for an unrelated reason.
    /// </summary>
    private const string MarkerGuardrailPs = """
        param([string]$SubjectPath = '')
        if ($env:GR_SUBJECT) { $SubjectPath = $env:GR_SUBJECT }
        if (-not $SubjectPath) { exit 0 }
        if (-not (Test-Path $SubjectPath)) { exit 0 }
        if ((Get-Content $SubjectPath -Raw) -cmatch 'DEFECT') { exit 1 }
        exit 0

        """;

    private const string MarkerGuardrailBash = """
        #!/usr/bin/env bash
        set -u
        SUBJECT="${GR_SUBJECT:-${1:-}}"
        [ -n "$SUBJECT" ] || exit 0
        [ -f "$SUBJECT" ] || exit 0
        grep -q DEFECT "$SUBJECT" && exit 1
        exit 0

        """;

    /// <summary>
    /// A plan folder in a temp directory whose workspace is the plan folder itself — enough to load a
    /// <see cref="PlanDefinition"/> and drive <see cref="PlanPreflightPhase.EvaluateAsync"/> directly.
    /// <c>maxParallelism: 1</c> pins serial mode, so the phase resolves its evaluation workspace to the
    /// plan workspace and never reaches for a git worktree.
    /// </summary>
    private sealed class SamplePlanFixture : IDisposable
    {
        public string PlanDir { get; }

        public SamplePlanFixture()
        {
            PlanDir = Path.Combine(Path.GetTempPath(), "gr-samplewiring-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(PlanDir, "tasks"));
            File.WriteAllText(Path.Combine(PlanDir, "guardrails.json"),
                """
                {
                  "version": 1,
                  "guardrailMode": "failFast",
                  "workspace": ".",
                  "defaultRetries": 0,
                  "maxParallelism": 1
                }
                """);
        }

        /// <summary>Opt this plan into Full Flight Checks with a check that always passes.</summary>
        public void AddGreenPlanPreflight() => WriteGreenPlanPreflight(PlanDir);

        public void AddTaskWithSamplePair(string taskId, string pairName, bool reversed) =>
            WriteTaskWithSamplePair(PlanDir, taskId, pairName, reversed);

        public PlanDefinition Load() => LoadPlan(PlanDir);

        public void Dispose()
        {
            try { SafeDelete.DeleteDirectory(PlanDir); }
            catch { /* best-effort teardown */ }
        }
    }

    /// <summary>
    /// The runnable plan for the end-to-end test: <c>&lt;repo&gt;/plan/</c> with <c>workspace: ".."</c>
    /// (the git repo root) and <c>maxParallelism: 1</c>, mirroring <c>PlanPreflightPhaseTests</c>.
    /// </summary>
    private static string CreatePlan(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 1
            }
            """);

        return planDir;
    }

    /// <summary>
    /// A plan-level Full Flight Check that always passes. Guardrail-shaped and opening with the
    /// <c>catches:</c> comment the four-folder loader requires.
    /// </summary>
    private static void WriteGreenPlanPreflight(string planDir)
    {
        string dir = Path.Combine(planDir, "preflights");
        Directory.CreateDirectory(dir);

        const string catches =
            "# catches: a pre-DAG phase that skips the plan's own Full Flight Checks";
        WriteScript(
            Path.Combine(dir, "01-plan-baseline" + ScriptExtension),
            catches + "\nexit 0\n",
            "#!/usr/bin/env bash\n" + catches + "\nexit 0\n");
    }

    /// <summary>
    /// Write a task carrying ONE committed sample pair: <c>task.json</c>, an action that writes a marker
    /// into the workspace (a real change, so a green run settles normally), the pair's guardrail, and the
    /// two sample halves. <paramref name="reversed"/> swaps which half carries the defect: sound puts it
    /// in <c>.invalid</c> (guardrail exits non-zero, as the contract demands), reversed puts it in
    /// <c>.valid</c> so the guardrail rejects its own valid sample and passes its own invalid one.
    /// </summary>
    private static void WriteTaskWithSamplePair(string planDir, string taskId, string pairName, bool reversed)
    {
        string taskDir = Path.Combine(planDir, "tasks", taskId);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        Directory.CreateDirectory(Path.Combine(taskDir, "samples"));

        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "sample-pair wiring fixture {{taskId}}",
              "writeScope": ["**"],
              "dependsOn": []
            }
            """);

        WriteScript(
            Path.Combine(taskDir, "action" + ScriptExtension),
            $"New-Item -Path (Join-Path $env:GUARDRAILS_WORKSPACE '{taskId}.out') -Force -Value 'ran' | Out-Null\nexit 0\n",
            $"#!/usr/bin/env bash\nprintf 'ran' > \"$GUARDRAILS_WORKSPACE/{taskId}.out\"\nexit 0\n");

        // The guardrail the pair belongs to. Matched to the samples by base name, so all three files
        // share `pairName`.
        WriteScript(
            Path.Combine(taskDir, "guardrails", pairName + ScriptExtension),
            MarkerGuardrailPs,
            MarkerGuardrailBash);

        string samplesDir = Path.Combine(taskDir, "samples");
        File.WriteAllText(
            Path.Combine(samplesDir, pairName + ".valid.txt"),
            reversed ? DefectiveSample : CleanSample);
        File.WriteAllText(
            Path.Combine(samplesDir, pairName + ".invalid.txt"),
            reversed ? CleanSample : DefectiveSample);
    }

    /// <summary>Write an OS-appropriate script (the bash bodies carry their own shebang + exec bit).</summary>
    private static void WriteScript(string path, string psBody, string bashBody)
    {
        File.WriteAllText(path, Ps ? psBody : bashBody);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static PlanDefinition LoadPlan(string planDir)
    {
        PlanLoadResult loaded = new PlanLoader().Load(planDir);
        Assert.NotNull(loaded.Plan);
        return loaded.Plan!;
    }

    /// <summary>Read <c>state/run.json</c> exactly as it stands on disk (no resume normalization).</summary>
    private static JournalDocument ReadJournal(string planDir) =>
        JournalReader.Read(RunJournal.PathFor(planDir));

    /// <summary>
    /// Drive the real <c>run</c> command pipeline — the same entry the CLI uses. The invocation lives in
    /// this helper rather than in the test body on purpose: xUnit1051 (an error in this repo) fires on a
    /// defaulted <c>CancellationToken</c> inside a test method, and the analyzer inspects test bodies
    /// only — the same reason <c>PlanPreflightPhaseTests</c> shapes it this way. Output goes to a
    /// discarded <see cref="StringConsoleIo"/> so nothing touches the process-global console;
    /// <c>--no-ui --no-log-server</c> keep the run headless.
    /// </summary>
    private static async Task<int> RunCliAsync(string planDir)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("sample-verifier wiring cli test root");
        root.Add(RunCommand.Create(io));

        return await root.Parse(["run", planDir, "--no-ui", "--no-log-server"]).InvokeAsync();
    }

    /// <summary>
    /// Windows-safe temp git repo (SafeDelete strips read-only bits before delete), copied from
    /// <c>PlanPreflightPhaseTests</c>. The plan folder is created inside <see cref="RepoPath"/> and left
    /// uncommitted; the run's workspace is the repo root.
    /// </summary>
    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-samplewiring-run-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            Directory.CreateDirectory(RepoPath);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# sample-verifier-wiring-test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        private static void Git(string workingDir, params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi)!;
            proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
        }

        public void Dispose()
        {
            try { SafeDelete.DeleteDirectory(_root); }
            catch { /* best-effort teardown */ }
        }
    }
}
