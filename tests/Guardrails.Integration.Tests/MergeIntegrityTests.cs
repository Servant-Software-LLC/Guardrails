using System.Diagnostics;
using System.Text;
using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Regressions for the union-merge + end-of-run-delivery subsystem — issues #457 and #451, which were
/// found together on one run that corrupted a 388 KB tracked document, delivered it to the user's
/// <c>master</c>, and only THEN ran the gate that caught it.
///
/// <list type="number">
///   <item><b>#457(a) encoding</b> — <see cref="AiMergeResolver"/> captured <c>git show</c> without
///     pinning UTF-8, so the three-way merge inputs were decoded with the host CONSOLE code page and
///     the mojibake was written back over a tracked file.</item>
///   <item><b>#457(b) delivery ordering</b> — the Scheduler delivered on <c>report.AllSucceeded</c>
///     (TASKS ONLY) while the terminal <c>&lt;plan&gt;/guardrails/</c> gate runs afterwards in the CLI.
///     A gate that runs after delivery is not a gate.</item>
///   <item><b>#451(b) union set</b> — the §4.3 integration set was built from the per-task guardrail
///     folders only, so a plan-root guardrail tagged <c>scope:"integration"</c> never ran at any
///     union.</item>
///   <item><b>#451(a) unmerged paths</b> — an AI resolver returning <c>true</c> with an unmerged path
///     left reached <c>git commit</c>, which exited 128 and was read as an INFRASTRUCTURE FAULT that
///     aborted the whole run instead of taking the designed needs-human rollback.</item>
/// </list>
///
/// <para>
/// Every test here is written to FAIL on the pre-fix code, not merely to pass on the post-fix code.
/// The encoding test therefore FORCES a legacy console code page for the duration of the round-trip
/// (see <see cref="LegacyConsoleCodePage"/>) rather than hoping the host happens to have one — which
/// is why this class is in a non-parallel collection.
/// </para>
/// </summary>
[Collection(MergeIntegrityCollection.Name)]
public sealed class MergeIntegrityTests
{
    private static readonly bool Ps = OperatingSystem.IsWindows();

    // The non-ASCII inventory the #457 incident destroyed, in the same character classes.
    private const string SectionSign = "§";   // §
    private const string EmDash = "—";        // —
    private const string BoxDrawing = "─";    // ─
    private const string Arrow = "→";         // →
    private const string Ellipsis = "…";      // …

    private const string SpecA =
        "# Spec A\n\n## " + SectionSign + "1 Overview " + EmDash + " the plan\n\n" +
        "Box " + BoxDrawing + BoxDrawing + BoxDrawing + " Arrow " + Arrow + " Ellipsis " + Ellipsis + "\n";

    private const string SpecB =
        "# Spec B\n\n## " + SectionSign + "2 Details " + EmDash + " the detail\n\n" +
        "Box " + BoxDrawing + BoxDrawing + BoxDrawing + " Arrow " + Arrow + " Ellipsis " + Ellipsis + "\n";

    private const string SpecPath = "docs/spec.md";

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // #457(a) — the AI-merge path must round-trip UTF-8 whatever the host console code page is
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A conflicted merge of two genuinely non-ASCII files must produce byte-identical UTF-8.
    ///
    /// <para>
    /// The fake resolver is a PURE PASS-THROUGH: it reads the harness's own
    /// <c>GUARDRAILS_MERGE_OURS</c> / <c>GUARDRAILS_MERGE_THEIRS</c> files and concatenates them into
    /// <c>GUARDRAILS_MERGE_OUT</c>. It invents no bytes, so whatever ends up in the merged file is
    /// exactly what the harness captured from <c>git show</c> — which is the code path the incident
    /// corrupted. The assertion is on RAW BYTES read from the plan branch (never on a decoded string),
    /// so the test's own capture cannot mask the defect.
    /// </para>
    /// <para>
    /// <b>Why the code page is forced.</b> An unpinned <see cref="ProcessStartInfo"/> falls back to the
    /// console output code page: <c>GetConsoleOutputCP()</c> on Windows, <c>Console.OutputEncoding</c>
    /// on Unix. A developer box or CI runner already sitting at UTF-8 (65001) would decode correctly
    /// with OR without the fix, making the test vacuous exactly where it matters. Forcing Latin-1 for
    /// the duration reproduces the production condition deterministically on all three OSes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AiMerge_NonAsciiConflict_RoundTripsUtf8_UnderLegacyConsoleCodePage()
    {
        using var repo = new TempGitRepo("gr-enc");
        string planDir = CreateSpecConflictPlan(repo.RepoPath);

        var runner = new PassThroughMergeRunner();
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        RunReport report;
        using (LegacyConsoleCodePage.Force())
        {
            (report, _) = await RunSchedulerAsync(
                planDir, provider,
                aiMergeWorker: new AiMergeWorker(runner),
                reVerifier: new AlwaysPassReVerifier(),
                ct: TestContext.Current.CancellationToken);
        }

        Assert.True(report.AllSucceeded,
            "the pass-through resolution is clean, so the union must settle: " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));
        Assert.True(runner.Called, "the AI merge worker must have been invoked for the conflict");

        // RAW BYTES off the plan branch — no string decoding anywhere in the assertion path.
        byte[] merged = repo.ShowBytes("guardrails/" + Path.GetFileName(planDir) + ":" + SpecPath);
        string mergedText = new UTF8Encoding(false).GetString(merged);

        // The whole non-ASCII inventory must survive, twice over (once per side of the merge).
        Assert.Equal(2, Count(mergedText, SectionSign));
        Assert.Equal(2, Count(mergedText, EmDash));
        Assert.Equal(6, Count(mergedText, BoxDrawing));
        Assert.Equal(2, Count(mergedText, Arrow));
        Assert.Equal(2, Count(mergedText, Ellipsis));

        // The exact CP437/CP850/Latin-1 mojibake signatures the incident left behind.
        Assert.DoesNotContain('Γ', mergedText);   // Γ — the E2 lead byte through CP437
        Assert.DoesNotContain('Â', mergedText);   // Â — the C2 lead byte through Latin-1/CP1252
        Assert.DoesNotContain('â', mergedText);   // â — the E2 lead byte through Latin-1/CP1252
        Assert.DoesNotContain('�', mergedText);   // the replacement char, for a lossy decode

        // Byte-exact: the merged blob IS ours + theirs, unaltered. Also pins the size — the incident's
        // most visible symptom was a 388 KB → 404 KB inflation from re-encoding mojibake as UTF-8.
        //
        // WHICH side is "ours" is a scheduling race: the two tasks are sibling ROOTS, so whichever
        // integrates first becomes the plan-branch side and the other becomes MERGE_THEIRS. Both
        // orderings are legitimate outputs of a correct harness, so the assertion accepts either — but
        // still demands EXACT equality with one of them, which is what makes it a byte-fidelity check
        // rather than a fuzzy one. (Getting this wrong made the test fail ~1 run in 3 on ordering, not
        // on encoding.)
        var utf8 = new UTF8Encoding(false);
        byte[] aThenB = utf8.GetBytes(SpecA + SpecB);
        byte[] bThenA = utf8.GetBytes(SpecB + SpecA);

        Assert.Equal(aThenB.Length, merged.Length);
        Assert.True(
            merged.SequenceEqual(aThenB) || merged.SequenceEqual(bThenA),
            "the merged blob must be byte-identical to ours+theirs in one of the two integration "
            + "orders; got: " + mergedText);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // #457(b) — nothing may reach the user's branch until the terminal gate has PASSED
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The DAG drains green, <c>mergeOnSuccess</c> is ON by default (#340), and the terminal
    /// <c>&lt;plan&gt;/guardrails/</c> gate FAILS. The user's branch must be untouched.
    ///
    /// <para>
    /// Pre-fix the Scheduler delivered on <c>report.AllSucceeded</c> — tasks only — so the user's
    /// branch had already advanced by the time the CLI evaluated the gate and printed a terminal halt
    /// for work that had shipped. The plan branch assertion is the other half: the verified work must
    /// still be durable where the halt message says it is.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TerminalGateFails_DeliversNothingToUserBranch_WorkStaysOnPlanBranch()
    {
        using var plan = new TerminalGatePlan(gatePasses: false);

        string userBranch = plan.CurrentBranch();
        string headBeforeRun = plan.HeadSha();

        int exit = await RunCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.TaskFailed, exit);

        JournalDocument doc = JournalReader.Read(RunJournal.PathFor(plan.PlanDir));
        Assert.Equal(PlanPhaseStatus.PlanGuardrailFailed, doc.PlanGuardrails!.Status);

        // THE REGRESSION: the user's branch must not have moved. Pre-fix it carried the run's work.
        Assert.Equal(userBranch, plan.CurrentBranch());
        Assert.Equal(headBeforeRun, plan.HeadSha());
        Assert.False(File.Exists(Path.Combine(plan.RepoPath, "src", "01-a.cs")),
            "a run whose terminal gate FAILED must deliver nothing into the user's checkout");

        // ...and the verified work is still durable on the plan branch, exactly as the halt claims.
        Assert.Contains("01-a", plan.PlanBranchFileList());
        Assert.Contains("02-b", plan.PlanBranchFileList());
    }

    /// <summary>
    /// The #340 delivered-by-default behaviour is UNCHANGED for a genuinely green run: DAG green AND
    /// terminal gate green still delivers to the user's branch, with the same
    /// <see cref="MergeOnSuccessResult"/> vocabulary. Deferring the delivery must not quietly turn
    /// "green means delivered" into "green means stranded".
    /// </summary>
    [Fact]
    public async Task TerminalGatePasses_StillDeliversToUserBranch_ByDefault()
    {
        using var plan = new TerminalGatePlan(gatePasses: true);

        string userBranch = plan.CurrentBranch();
        string headBeforeRun = plan.HeadSha();

        int exit = await RunCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(userBranch, plan.CurrentBranch());
        Assert.NotEqual(headBeforeRun, plan.HeadSha());
        Assert.True(File.Exists(Path.Combine(plan.RepoPath, "src", "01-a.cs")),
            "a wholly-green, gate-passed run must still deliver by default (#340)");
        Assert.True(File.Exists(Path.Combine(plan.RepoPath, "src", "02-b.cs")));
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // #451(b) — the union re-verify set must include the plan-root <plan>/guardrails/ folder
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Two sibling roots form a NON-FF union. The plan's only <c>scope:"integration"</c> guardrail
    /// lives in the plan-root <c>&lt;plan&gt;/guardrails/</c> folder — where the four-folder model says
    /// a union invariant belongs — and it FAILS on the merged bytes. The union must therefore roll back
    /// (B1) and settle needs-human.
    ///
    /// <para>
    /// Pre-fix the set was <c>IntegrationSet(plan.Tasks…)</c>, which for this plan is EMPTY: the
    /// re-verify ran nothing, passed vacuously, and both tasks settled green. The recorded set is
    /// asserted too, so the failure names the actual defect (membership) rather than only its
    /// consequence.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PlanRootIntegrationGuardrail_RunsAtUnion_AndItsFailureRollsBack()
    {
        using var repo = new TempGitRepo("gr-union");
        string planDir = CreateSiblingUnionPlanWithPlanRootGuardrail(repo.RepoPath);

        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        var interpreterMap = new InterpreterMap(new PathExecutableProbe());
        var recording = new RecordingReVerifier(new GuardrailReVerifier(new ProcessRunner(), interpreterMap));

        var (report, _) = await RunSchedulerAsync(
            planDir, provider, aiMergeWorker: null, reVerifier: recording,
            ct: TestContext.Current.CancellationToken);

        // THE REGRESSION (membership): the plan-root guardrail must be in the set handed to the union.
        Assert.NotEmpty(recording.Sets);
        Assert.Contains(recording.Sets, set => set.Any(g => g.Name.Contains("union-intact", StringComparison.Ordinal)));

        // ...and (consequence) its failure must take the designed B1 rollback, not settle green.
        Assert.Single(report.Tasks, t => t.Outcome == TaskOutcome.NeedsHuman);
        Assert.Single(report.Tasks, t => t.Outcome == TaskOutcome.Succeeded);
        Assert.False(report.AllSucceeded);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // #451(a) — a resolver's "true" is not the authority on the index
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A resolver that returns <c>true</c> while leaving the index unmerged must take the designed
    /// needs-human rollback — NOT abort the run.
    ///
    /// <para>
    /// Pre-fix, nothing between the resolver returning and <c>CommitStagedMerge</c> checked the one
    /// post-condition that matters, so <c>git commit</c> exited 128 ("Committing is not possible
    /// because you have unmerged files") from inside a <c>try</c> that treats any git failure as an
    /// environment fault. The whole run aborted — after four tasks had gone green and $48 of prompt
    /// spend — for a condition with a designed handler.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ResolverReportsSuccessButLeavesUnmergedPath_NeedsHuman_NotRunAbort()
    {
        using var repo = new TempGitRepo("gr-unmerged");
        string planDir = CreateSpecConflictPlan(repo.RepoPath);
        string initialHead = repo.HeadSha();

        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        var (report, _) = await RunSchedulerAsync(
            planDir, provider,
            aiMergeWorker: new LiarMergeWorker(),        // returns true, touches nothing
            reVerifier: new AlwaysPassReVerifier(),
            ct: TestContext.Current.CancellationToken);

        // THE REGRESSION: a known state with a designed handler must never surface as an infra abort.
        Assert.Null(report.Abort);

        TaskResult needsHuman = Assert.Single(report.Tasks, t => t.Outcome == TaskOutcome.NeedsHuman);
        Assert.Contains("unmerged", needsHuman.Summary ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SpecPath, needsHuman.Summary ?? string.Empty, StringComparison.Ordinal);

        // The B1 rollback left the user's branch untouched.
        Assert.Equal(initialHead, repo.HeadSha());
    }

    /// <summary>
    /// The same protection at its source, on the REAL <see cref="AiMergeResolver"/>, in the shape that
    /// actually produced #451: a union that conflicts in TWO files.
    ///
    /// <para>
    /// An attempt resolves exactly ONE file (the prompt is single-file by contract §9.1), so the second
    /// conflicted file is left at <c>UU</c> — and the pre-existing gates are structurally blind to it:
    /// <c>git diff --cached</c> skips unmerged entries, so the marker gate sees no markers, and the
    /// leftover was already in the pre-runner status, so the blast-radius gate sees nothing out of
    /// bounds. Both gates passed on a half-resolved merge and the attempt reported success. Gate (iv)
    /// is what makes the attempt honest.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RealResolver_TwoFileConflict_FailsHonestly_NeedsHuman_NotRunAbort()
    {
        using var repo = new TempGitRepo("gr-twofile");
        string planDir = CreateSpecConflictPlan(repo.RepoPath, secondConflictFile: "docs/notes.md");
        string initialHead = repo.HeadSha();

        var runner = new PassThroughMergeRunner();
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        var (report, _) = await RunSchedulerAsync(
            planDir, provider,
            aiMergeWorker: new AiMergeWorker(runner),
            reVerifier: new AlwaysPassReVerifier(),
            ct: TestContext.Current.CancellationToken);

        Assert.Null(report.Abort);
        Assert.Single(report.Tasks, t => t.Outcome == TaskOutcome.NeedsHuman);
        Assert.Equal(initialHead, repo.HeadSha());

        // The budget was spent honestly: both attempts ran and both were rejected by gate (iv).
        Assert.Equal(2, runner.CallCount);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Console-code-page forcing
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Temporarily forces the process's console output encoding to Latin-1 (CP28591) — the fallback an
    /// UNPINNED <see cref="ProcessStartInfo"/> decodes a child's stdout with
    /// (<c>GetConsoleOutputCP()</c> on Windows, <see cref="Console.OutputEncoding"/> on Unix).
    /// <para>
    /// Latin-1 is used rather than the incident's CP437 because .NET Core carries it in-box (CP437
    /// needs the <c>System.Text.Encoding.CodePages</c> provider) and it damages UTF-8 identically for
    /// this purpose: every multi-byte sequence becomes one mojibake char per byte. Restoring in
    /// <see cref="Dispose"/> keeps the mutation scoped; the enclosing non-parallel collection keeps it
    /// off every other test.
    /// </para>
    /// <para>
    /// If the host refuses the change (no console attached — some CI shells), the scope is INERT and
    /// the round-trip still runs as a plain correctness assertion.
    /// </para>
    /// </summary>
    private sealed class LegacyConsoleCodePage : IDisposable
    {
        private readonly Encoding? _saved;

        private LegacyConsoleCodePage(Encoding? saved) => _saved = saved;

        public static LegacyConsoleCodePage Force()
        {
            try
            {
                Encoding saved = Console.OutputEncoding;
                Console.OutputEncoding = Encoding.Latin1;
                return new LegacyConsoleCodePage(saved);
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or NotSupportedException)
            {
                return new LegacyConsoleCodePage(saved: null); // inert
            }
        }

        public void Dispose()
        {
            if (_saved is null)
            {
                return;
            }

            try { Console.OutputEncoding = _saved; } catch (IOException) { /* best-effort restore */ }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Fakes
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A resolver that invents NO bytes: it concatenates the harness's own MERGE_OURS + MERGE_THEIRS
    /// into MERGE_OUT. Any corruption in the merged file therefore came from the harness's capture of
    /// <c>git show</c>, which is precisely what #457 is about.
    /// </summary>
    private sealed class PassThroughMergeRunner : IPromptRunner
    {
        public int CallCount { get; private set; }
        public bool Called => CallCount > 0;

        public string Name => "ai-merge-pass-through";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken ct)
        {
            CallCount++;

            string ours = File.ReadAllText(invocation.Environment["GUARDRAILS_MERGE_OURS"]);
            string theirs = File.ReadAllText(invocation.Environment["GUARDRAILS_MERGE_THEIRS"]);
            File.WriteAllText(invocation.Environment["GUARDRAILS_MERGE_OUT"], ours + theirs);

            return Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                Summary = "pass-through: MERGE_OURS + MERGE_THEIRS verbatim"
            });
        }
    }

    /// <summary>Claims success, resolves nothing — the #451 defect-A shape, in one class.</summary>
    private sealed class LiarMergeWorker : IAiMergeWorker
    {
        public Task<bool> TryResolveAsync(
            string worktreePath, string segmentBranch, string planDirectory,
            ISchedulerJournal journal, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class AlwaysPassReVerifier : IReVerifier
    {
        public Task<ReVerifyResult> ReVerifyAsync(
            string worktreePath, IReadOnlyList<GuardrailDefinition> guardrails,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReVerifyResult { Passed = true });
    }

    /// <summary>Delegates to a REAL re-verifier while recording every guardrail set it was handed.</summary>
    private sealed class RecordingReVerifier(IReVerifier inner) : IReVerifier
    {
        public List<IReadOnlyList<GuardrailDefinition>> Sets { get; } = [];

        public Task<ReVerifyResult> ReVerifyAsync(
            string worktreePath, IReadOnlyList<GuardrailDefinition> guardrails,
            CancellationToken cancellationToken = default)
        {
            Sets.Add(guardrails);
            return inner.ReVerifyAsync(worktreePath, guardrails, cancellationToken);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Harness helpers
    // ═════════════════════════════════════════════════════════════════════════════════════════

    private static async Task<(RunReport Report, RunJournal Journal)> RunSchedulerAsync(
        string planDir,
        IWorktreeProvider worktreeProvider,
        IAiMergeWorker? aiMergeWorker,
        IReVerifier reVerifier,
        CancellationToken ct)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();

        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);
        var registry = PromptRunnerRegistry.Build(
            load.Plan!.Config, _ => throw new InvalidOperationException("no prompt runners in these tests"));

        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);

        var scheduler = new Scheduler(
            load.Plan!, executor, journal,
            worktreeProvider: worktreeProvider,
            reVerifier: reVerifier,
            aiMergeWorker: aiMergeWorker);

        return (await scheduler.RunAsync(load.Plan!, ct), journal);
    }

    /// <summary>Drive the REAL root command with a captured console; return the exit code.</summary>
    private static async Task<int> RunCliAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        return await root.Parse(args).InvokeAsync();
    }

    private static int Count(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Plan fixtures
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Two sibling root tasks that both create <paramref name="secondConflictFile"/> (when given) and
    /// <see cref="SpecPath"/> with DIFFERENT non-ASCII content — a "both added" conflict at whichever
    /// task integrates second, i.e. a real AI-merge union.
    /// </summary>
    private static string CreateSpecConflictPlan(string repoPath, string? secondConflictFile = null)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 2,
              "mergeOnSuccess": false
            }
            """);

        WriteSpecTask(planDir, "01-task-a", SpecA, secondConflictFile, "note A " + EmDash + " first");
        WriteSpecTask(planDir, "02-task-b", SpecB, secondConflictFile, "note B " + EmDash + " second");
        return planDir;
    }

    private static void WriteSpecTask(
        string planDir, string taskId, string specContent, string? extraFile, string extraContent)
    {
        string taskDir = Path.Combine(planDir, "tasks", taskId);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "spec writer {{taskId}}", "writeScope": ["docs/**"], "dependsOn": [] }""");

        var body = new StringBuilder();
        body.Append(WriteBytesFragment(SpecPath, specContent));
        if (extraFile is not null)
        {
            body.Append(WriteBytesFragment(extraFile, extraContent));
        }

        WriteScript(Path.Combine(taskDir, Ps ? "action.ps1" : "action.sh"), ScriptFrom(body.ToString()));
        WriteScript(Path.Combine(taskDir, "guardrails", Ps ? "01-check.ps1" : "01-check.sh"), GreenScript());
    }

    /// <summary>
    /// Two sibling roots writing DISJOINT files (a clean, marker-free NON-FF union — no AI merge
    /// involved) plus a plan-root <c>&lt;plan&gt;/guardrails/</c> folder holding ONE
    /// <c>scope:"integration"</c> union invariant that FAILS once both siblings' files are present.
    /// The conflict-marker scan in the same script is what satisfies GR2028's content teeth, exactly
    /// as the real plan's <c>03-union-intact</c> did.
    /// </summary>
    private static string CreateSiblingUnionPlanWithPlanRootGuardrail(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 2,
              "mergeOnSuccess": false
            }
            """);

        WriteDisjointTask(planDir, "01-task-a");
        WriteDisjointTask(planDir, "02-task-b");

        string planGuardrailDir = Path.Combine(planDir, "guardrails");
        Directory.CreateDirectory(planGuardrailDir);
        WriteScript(
            Path.Combine(planGuardrailDir, Ps ? "01-union-intact.ps1" : "01-union-intact.sh"),
            UnionIntactScript());
        File.WriteAllText(
            Path.Combine(planGuardrailDir, "01-union-intact.json"),
            """{ "scope": "integration" }""");

        return planDir;
    }

    private static void WriteDisjointTask(string planDir, string taskId)
    {
        string taskDir = Path.Combine(planDir, "tasks", taskId);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "disjoint writer {{taskId}}", "writeScope": ["src/**"], "dependsOn": [] }""");

        WriteScript(Path.Combine(taskDir, Ps ? "action.ps1" : "action.sh"),
            ScriptFrom(WriteBytesFragment("src/" + taskId + ".cs", "class " + taskId.Replace("-", "_") + " {}\n")));
        WriteScript(Path.Combine(taskDir, "guardrails", Ps ? "01-check.ps1" : "01-check.sh"), GreenScript());
    }

    /// <summary>
    /// The plan-root union invariant. Its first check is a conflict-marker scan (which is also what
    /// gives it GR2028 content teeth); its second FAILS once BOTH siblings' files are present — i.e.
    /// exactly at the union, and nowhere else. Opens with the GR2027 <c>catches:</c> declaration.
    /// </summary>
    private static string UnionIntactScript()
    {
        if (Ps)
        {
            return
                "# catches: a union re-verify set that omits the plan-root <plan>/guardrails/ folder, so this\n" +
                "#          union invariant never runs at a union point and a bad merge settles green.\n" +
                "$bad = Get-ChildItem -Path 'src' -Filter '*.cs' -ErrorAction SilentlyContinue |\n" +
                "    Where-Object { Select-String -Path $_.FullName -Pattern '<<<<<<<' -Quiet }\n" +
                "if ($bad) { Write-Output 'conflict markers survived the union'; exit 1 }\n" +
                "if ((Test-Path 'src/01-task-a.cs') -and (Test-Path 'src/02-task-b.cs')) {\n" +
                "    Write-Output 'union invariant violated: both siblings landed'\n" +
                "    exit 1\n" +
                "}\n" +
                "exit 0\n";
        }

        return
            "#!/usr/bin/env bash\n" +
            "# catches: a union re-verify set that omits the plan-root <plan>/guardrails/ folder, so this\n" +
            "#          union invariant never runs at a union point and a bad merge settles green.\n" +
            "if grep -rq '<<<<<<<' src 2>/dev/null; then\n" +
            "    echo 'conflict markers survived the union'\n" +
            "    exit 1\n" +
            "fi\n" +
            "if [ -f 'src/01-task-a.cs' ] && [ -f 'src/02-task-b.cs' ]; then\n" +
            "    echo 'union invariant violated: both siblings landed'\n" +
            "    exit 1\n" +
            "fi\n" +
            "exit 0\n";
    }

    /// <summary>
    /// A linear two-task plan in a real git repo, worktree mode, <c>mergeOnSuccess</c> left at its
    /// #340 default (ON), with a plan-root terminal gate whose verdict is fixed by the constructor. A
    /// single linear chain forms no union, so GR2028's content-teeth rule is exempt and a plain
    /// exit-code check validates.
    /// </summary>
    private sealed class TerminalGatePlan : IDisposable
    {
        private readonly string _root;

        public string PlanDir { get; }
        public string RepoPath { get; }

        public TerminalGatePlan(bool gatePasses)
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-deliver-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            Directory.CreateDirectory(RepoPath);
            InitRepo(RepoPath);

            PlanDir = Path.Combine(RepoPath, "plan");
            Directory.CreateDirectory(Path.Combine(PlanDir, "state"));
            Directory.CreateDirectory(Path.Combine(PlanDir, "tasks"));

            File.WriteAllText(Path.Combine(PlanDir, "guardrails.json"),
                """
                {
                  "version": 1,
                  "guardrailMode": "failFast",
                  "workspace": "..",
                  "defaultRetries": 0,
                  "maxParallelism": 2
                }
                """);

            WriteChainTask("01-a");
            WriteChainTask("02-b", "01-a");

            string gateDir = Path.Combine(PlanDir, "guardrails");
            Directory.CreateDirectory(gateDir);
            WriteScript(Path.Combine(gateDir, Ps ? "01-terminal.ps1" : "01-terminal.sh"),
                TerminalGateScript(gatePasses));
        }

        public string CurrentBranch() => RunGit(RepoPath, "rev-parse", "--abbrev-ref", "HEAD").Trim();

        public string HeadSha() => RunGit(RepoPath, "rev-parse", "HEAD").Trim();

        /// <summary>Every path the plan branch's tip carries (proves the work is durable there).</summary>
        public string PlanBranchFileList() =>
            RunGit(RepoPath, "ls-tree", "-r", "--name-only", "guardrails/" + Path.GetFileName(PlanDir));

        private void WriteChainTask(string id, params string[] dependsOn)
        {
            string taskDir = Path.Combine(PlanDir, "tasks", id);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

            string deps = dependsOn.Length == 0
                ? "[]"
                : "[" + string.Join(", ", dependsOn.Select(d => "\"" + d + "\"")) + "]";
            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                "{ \"description\": \"delivery-ordering task " + id + "\", " +
                "\"writeScope\": [\"src/**\"], \"dependsOn\": " + deps + " }");

            WriteScript(Path.Combine(taskDir, Ps ? "action.ps1" : "action.sh"),
                ScriptFrom(WriteBytesFragment("src/" + id + ".cs", "class " + id.Replace("-", "_") + " {}\n")));
            WriteScript(Path.Combine(taskDir, "guardrails", Ps ? "01-check.ps1" : "01-check.sh"), GreenScript());
        }

        private static string TerminalGateScript(bool passes)
        {
            int code = passes ? 0 : 1;
            string verdict = passes ? "green" : "RED (deliberate)";
            string header =
                "# catches: end-of-run delivery that fires on tasks-green alone, before this terminal gate\n" +
                "#          has certified the merged HEAD (#457).\n";
            return Ps
                ? header + "Write-Output 'terminal gate " + verdict + "'\nexit " + code + "\n"
                : "#!/usr/bin/env bash\n" + header + "echo 'terminal gate " + verdict + "'\nexit " + code + "\n";
        }

        public void Dispose() => SafeDeleteTree(_root);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Script emission — pure-ASCII sources that write byte-exact UTF-8, so nothing depends on how
    // the shell itself decodes its own script file.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    private static string ScriptFrom(string body) =>
        (Ps ? string.Empty : "#!/usr/bin/env bash\n") + body + "exit 0\n";

    private static string WriteBytesFragment(string relativePath, string content)
    {
        byte[] bytes = new UTF8Encoding(false).GetBytes(content);

        if (Ps)
        {
            string list = string.Join(",", bytes.Select(b => "0x" + b.ToString("X2")));
            return
                "$p = Join-Path $env:GUARDRAILS_WORKSPACE '" + relativePath + "'\n" +
                "New-Item -ItemType Directory -Force -Path (Split-Path -Parent $p) | Out-Null\n" +
                "[IO.File]::WriteAllBytes($p, [byte[]](" + list + "))\n";
        }

        string octal = string.Concat(bytes.Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0')));
        return
            "p=\"$GUARDRAILS_WORKSPACE/" + relativePath + "\"\n" +
            "mkdir -p \"$(dirname \"$p\")\"\n" +
            "printf '" + octal + "' > \"$p\"\n";
    }

    private static string GreenScript() => Ps ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n";

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

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Temp git repo
    // ═════════════════════════════════════════════════════════════════════════════════════════

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo(string prefix)
        {
            _root = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);
            InitRepo(RepoPath);
        }

        public string HeadSha() => RunGit(RepoPath, "rev-parse", "HEAD").Trim();

        /// <summary>
        /// <c>git show &lt;rev&gt;:&lt;path&gt;</c> as RAW BYTES. Deliberately never decodes: the test's
        /// own capture must not be able to hide (or invent) the very corruption under test.
        /// </summary>
        public byte[] ShowBytes(string revPath)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("show");
            psi.ArgumentList.Add(revPath);

            using var proc = Process.Start(psi)!;
            using var buffer = new MemoryStream();
            proc.StandardOutput.BaseStream.CopyTo(buffer);
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"git show {revPath} exited {proc.ExitCode}: {stderr.Trim()}");
            }

            return buffer.ToArray();
        }

        public void Dispose() => SafeDeleteTree(_root);
    }

    private static void InitRepo(string repoPath)
    {
        RunGit(repoPath, "init");
        RunGit(repoPath, "config", "user.email", "test@guardrails.local");
        RunGit(repoPath, "config", "user.name", "Guardrails Test");
        RunGit(repoPath, "config", "commit.gpgsign", "false");
        RunGit(repoPath, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# merge-integrity-test");
        RunGit(repoPath, "add", ".");
        RunGit(repoPath, "commit", "-m", "Initial commit");
    }

    private static string RunGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // The tests may be running under a forced legacy console code page; pin the harness-side
            // reads too so a test helper never mis-decodes what it is asserting about.
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
        }

        return stdout;
    }

    /// <summary>Windows-safe recursive delete (strips the read-only bit git leaves on loose objects).</summary>
    private static void SafeDeleteTree(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
        catch (IOException) { /* best-effort teardown */ }
        catch (UnauthorizedAccessException) { /* best-effort teardown */ }
    }
}

/// <summary>
/// <see cref="MergeIntegrityTests"/> mutates the PROCESS-WIDE console output code page to reproduce the
/// #457 condition deterministically. Disabling parallelization keeps that mutation from reaching any
/// other test that spawns a child process.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MergeIntegrityCollection
{
    public const string Name = "merge-integrity (serialized: mutates the console code page)";
}
