using System.CommandLine;
using System.Diagnostics;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Plan 32 (#556) §6.7 — <b>milestone C's delivery gate, end to end</b>. Milestone A makes the next resume
/// honest; it changes nothing an operator will ever see on the headline scenario, because <i>a run that goes
/// green to completion never resumes</i> (§6.1). <c>mergeOnSuccess</c> defaults ON, so an unattended
/// overnight run with a mid-run definition edit DELIVERS the stale-definition work to the user's branch and
/// prints a green summary. Milestone C is the gate that stops it, and these are the pins that say so.
///
/// <list type="bullet">
///   <item><b>P9</b> — <see cref="AGreenRunWithAMidRunDefinitionEdit_DoesNotDeliver_AndExitsTwo"/>:
///     <b>milestone C's ACCEPTANCE CRITERION.</b> §6.7: <i>"An implementation that passes every other bullet
///     and still merges has not fixed the reported defect."</i> All three of its facts are asserted — the
///     user's branch did not move, the plan branch retains the work, the exit code is <b>2</b> — plus the two
///     §6.5 corrections. <b>RED today</b>: the run delivers and exits 0.</item>
///   <item><b>P11</b> — <see cref="TheInRunDivergenceAndTheNextResumesDrift_NameTheSameTaskSet"/>: §6.6,
///     <i>"C is A's finding delivered one run earlier"</i>. The gate carries no remediation vocabulary of its
///     own — it points at §7.2's — so an implementation in which the in-run halt and the next resume's
///     <c>DefinitionDrift</c> disagree about the task set is wrong. <b>RED today</b>: there is no in-run
///     divergence report at all.</item>
///   <item><b>P13</b> — <see cref="AfterADivergenceHalt_TheWorkSurvivesOnThePlanBranch"/>: the diverged
///     task's integration commit is on the plan branch, its journal entry reads <c>succeeded</c>, and the
///     branch stays Part-C-corroborable. <b>DECLARED EXEMPTION</b> (see below).</item>
/// </list>
///
/// <para><b>P13 IS A DECLARED EXEMPTION, and it is the pin standing against candidate (3).</b> Today a
/// mid-run-edited run goes green, so the commit and the journal entry are both there and a CORRECT test is
/// GREEN — demanding red would demand a correct implementation fail. Its job is to STAY green after stage 13.
/// §6.4 re-specifies #556's own <i>"refuse to record a success"</i> as <b>record the success, block the
/// delivery</b>, because refusing (a) discards paid work — #554's defect, fixed hours before this plan was
/// written — and (b) leaves a plan-branch commit carrying a <c>Guardrails-Task:</c> trailer whose journal says
/// otherwise, which is precisely the present-but-uncorroborated state §7.2 Part C rule 3 REFUSES to rewind
/// past (<c>SafeSuffixEvaluator</c> corroborates every trailer in the removed range against the journal's
/// recorded settle hashes). That turns a recoverable drift into a mandatory full <c>guardrails reset -y</c> —
/// a remediation path strictly worse than the bug. So P13 asserts corroborability itself, not just presence.
/// It stays in the file rather than being dropped: a dropped row and an oversight look identical from the
/// outside.</para>
///
/// <para><b>Why the real CLI, over a real git repository, in worktree mode.</b> §8: these pins <i>"cannot be
/// faked — #382's lesson is that a fake-masked unit guardrail certifies green while the real
/// composition-root path is broken, and the default execution mode for a real run is worktree mode."</i>
/// Every run below goes through <c>RunCommand</c> itself, so the exit code, the delivery seam and the
/// terminal plan-guardrail phase are the real ones; <c>maxParallelism: 2</c> over a real repo puts it in
/// worktree mode. A P9 asserted against a fake worktree provider proves nothing about the seam that actually
/// delivers.</para>
///
/// <para><b>Sequencing the mid-run edit deterministically.</b> The edit must land after the plan loads and
/// before the target settles, which is a timing problem. Plan 31 already shipped the answer and this file
/// reuses the MECHANISM in its own file rather than inventing a second one (§8): the FIRST task's action
/// overwrites the SECOND task's <c>task.json</c> in the live plan folder by absolute path, so the edit is
/// sequenced by the <b>DAG</b> rather than by a timer — exactly as
/// <c>PlanEditedDuringRunTests.CreateMidRunEditPlan</c> does. <b>The edit IS the fixture</b> (§11): it is
/// never made conditional, retimed or removed to reach green.</para>
///
/// <para><b>The fixture declares a terminal <c>&lt;plan&gt;/guardrails/</c> gate, and that is load-bearing
/// for §6.5 correction 1.</b> Without one there is no terminal gate to report on and the correction would be
/// vacuous. The gate is a linear chain's (one leaf, no fan-in), so GR2028's integration-re-run obligation is
/// exempt and a plain <c>exit 0</c> check validates clean. It APPENDS to a log file outside the repo, which
/// is how "not evaluated" is asserted as a FACT rather than as a wording.</para>
///
/// <para><b><c>mergeOnSuccess</c> is set EXPLICITLY true, which is a stronger pin than omitting it.</b> It
/// removes the #340 default from the picture entirely, and the fixture's <c>autonomyPolicy: "halt"</c>
/// records no <c>proceeded-best-guess</c> / <c>proceeded-unreviewed</c> decision, so
/// <c>RunOutcomePolicy.SuppressingDecision</c> is null and the #361 delivery interlock never engages.
/// (Issue #597 note: the interlock's override is <c>RunConfig.MergeOnSuccessForcedByOperator</c>, set only
/// by the CLI <c>--merge-on-success</c> flag — a manifest key deliberately cannot lift it, per SSOT §5.3.
/// That does not change this fixture, which has no suppressing decision to lift.)
/// So the ONLY thing that can hold the merge back is <c>RunReport.AllSucceeded</c>
/// — §6.5's one seam, <i>"no new delivery path is introduced"</i>. An implementation that blocks delivery by
/// recording a delivery-suppressing decision token instead of by adding the <c>AllSucceeded</c> term does not
/// pass P9.</para>
///
/// <para><b><c>autonomyPolicy: "halt"</c> is for determinism, not for the scenario.</b> P11's second run is a
/// resume onto a provably-safe drifted suffix, and under the DEFAULT <c>prompt</c> policy
/// <c>RunCommand.ConfirmSafeDriftIfInteractive</c> would <c>Console.ReadLine()</c> on any host whose stdin is
/// not redirected. <c>halt</c> skips the prompt on every host and is exactly how the unattended pipeline §6.1
/// is about behaves. It changes nothing else here: no task fails, and the divergence gate is unconditional
/// (§6.4).</para>
///
/// <para><b>NO ASSERTION NAMES AN API MEMBER THIS PLAN HAS NOT WRITTEN YET.</b>
/// <c>RunReport.ExecutedDefinitionDivergence</c> (stage 13) and <c>definitionHashAtSettle</c> (stage 12) do
/// not exist on this tree and are not referenced. Everything above is observable without them: the CLI exit
/// code, whether the user's branch moved, what is on the plan branch, and what <c>state/run.json</c> says.
/// The <c>definition-divergence</c> boundary is matched as the WIRE TOKEN (§6.3/§14) rather than through the
/// constant stage 12 adds.</para>
/// </summary>
public sealed class DivergenceDeliveryGateTests : IClassFixture<HostRepoCleanlinessGuard>
{
    /// <summary>The first task: its action edits <see cref="Target"/>'s <c>task.json</c> mid-run.</summary>
    private const string Editor = "01-edit";

    /// <summary>The second task: the one whose definition moves under it while it is in flight.</summary>
    private const string Target = "02-target";

    /// <summary>Every task id the fixture declares — the universe both halts' task sets are read against.</summary>
    private static readonly string[] AllTaskIds = [Editor, Target];

    /// <summary>The description the mid-run edit writes into <see cref="Target"/>'s <c>task.json</c>.</summary>
    private const string EditedMidRun = "edited mid-run";

    /// <summary>
    /// The §6.3 <c>decisions[]</c> boundary token for the in-run divergence halt. Matched as the literal WIRE
    /// token, not through the constant stage 12 adds to <c>DecisionEntry.cs</c> — this file must compile on a
    /// tree where that constant does not exist.
    /// </summary>
    private const string DivergenceBoundary = "definition-divergence";

    /// <summary>The literal headline <c>RunCommand.PrintDefinitionDrift</c> opens the §7.2 halt with.</summary>
    private const string DriftHeadline = "DEFINITION DRIFT";

    /// <summary>The self-contradicting §6.5 reason <c>RunCommand.DescribeDelivery</c> would otherwise write.</summary>
    private const string NotWhollyGreenReason = "not wholly green";

    private static readonly bool Ps = OperatingSystem.IsWindows();

    // ── P9 — the acceptance criterion: a divergence run does NOT deliver, and exits 2 ────────────

    /// <summary>
    /// §6.7's P9, stated without hedging: a run with a mid-run <c>task.json</c> edit, <c>mergeOnSuccess</c>
    /// ON and every task green must merge <b>nothing</b> to the user's branch, must leave the work on the
    /// plan branch, and must exit <b>2</b> (actionable/needs-human — never 1, which is reserved for
    /// infrastructure faults, §6.5).
    ///
    /// <para><b>All three facts are asserted, because two of them are individually satisfiable by a wrong
    /// implementation.</b> A gate that exits 2 but still merges has not fixed the reported defect; a gate
    /// that blocks the merge by DISCARDING the work has broken §6.4 and P13 instead.</para>
    ///
    /// <para><b>Plus the two §6.5 corrections, which need WORK rather than acceptance.</b>
    /// (1) <c>RunCommand</c>'s <c>planGuardrailsPassed</c> is
    /// <c>!report.AllSucceeded || await PlanGuardrailPhase.EvaluateAsync(…)</c>, so a divergence run does not
    /// merely skip the terminal gate — it records that the gate PASSED. The gate must report <i>not
    /// evaluated</i>: asserted as the two facts observable without a new API member — the gate did not run at
    /// all (its side-effect log is empty), and nothing durable claims it passed.
    /// (2) <c>DescribeDelivery</c> would write <i>"the run was not wholly green, so delivery was never
    /// attempted"</i> into <c>run.json</c> for a run whose <c>tasks{}</c> shows every task <c>succeeded</c>.
    /// That record exists (#542) so an unattended pipeline with no console has a machine-readable answer, and
    /// <b>a wrong one is worse than none</b>. Both are stage 15's to implement and both are red until then,
    /// which is correct.</para>
    ///
    /// <para><b>RED on this tree, for the right reason.</b> There is no divergence gate here: the run drains
    /// wholly green, the terminal gate evaluates and passes, the deferred delivery completes, the user's
    /// branch fast-forwards and the process exits 0.</para>
    /// </summary>
    [Fact]
    public async Task AGreenRunWithAMidRunDefinitionEdit_DoesNotDeliver_AndExitsTwo()
    {
        using var repo = new TempGitRepo("gr32-ddg-p9");
        string planDir = CreateMidRunEditPlan(repo);

        string userBranch = repo.CurrentBranch();
        string userHead = repo.HeadSha();

        (int exit, string output) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server");

        JournalDocument doc = JournalOf(planDir);

        // ── positive controls: the scenario really happened ─────────────────────────────────────
        // Without these, "nothing was delivered" would also be satisfied by a run that never ran, and the
        // §6.5 correction below would have no antecedent to contradict.
        Assert.Contains(EditedMidRun,
            File.ReadAllText(Path.Combine(planDir, "tasks", Target, "task.json")), StringComparison.Ordinal);

        Assert.Equal(AllTaskIds.Length, doc.Tasks.Count);
        foreach (string taskId in AllTaskIds)
        {
            Assert.True(doc.Tasks.TryGetValue(taskId, out TaskJournalEntry? entry),
                $"'{taskId}' has no journal entry at all — the run did not reach it.\n{output}");
            Assert.True(entry!.Status == JournalTaskStatus.Succeeded,
                $"every task must SETTLE succeeded (§6.4: record the success, block the delivery), but " +
                $"'{taskId}' is '{entry.Status}'.\n{output}");
        }

        // ── the acceptance criterion, fact 1: NOTHING reached the user's branch ──────────────────
        Assert.Equal(userBranch, repo.CurrentBranch());
        Assert.True(userHead == repo.HeadSha(),
            "P9 (§6.7): a run carrying a mid-run definition edit must merge NOTHING to the user's branch, " +
            $"but it moved {Short(userHead)} -> {Short(repo.HeadSha())}. An implementation that passes " +
            "every other bullet and still merges has not fixed the reported defect.\n" + output);
        Assert.False(File.Exists(Path.Combine(repo.RepoPath, "src", "second.txt")),
            "the diverged task's output reached the user's CHECKOUT, so the work was delivered.\n" + output);

        // ── fact 2: the plan branch RETAINS the work (§6.4 — nothing is discarded) ───────────────
        string tree = TempGitRepo.Git(repo.RepoPath, "ls-tree", "-r", "--name-only", PlanBranchOf(planDir));
        Assert.Contains("src/first.txt", tree, StringComparison.Ordinal);
        Assert.Contains("src/second.txt", tree, StringComparison.Ordinal);

        // ── fact 3: exit 2 — actionable/needs-human, following DefinitionDrift's precedent (§6.5) ─
        Assert.Equal(ExitCodes.TaskFailed, exit);

        // ── §6.5 correction 1: the terminal gate is NOT EVALUATED, and never reported as PASSED ──
        // Both halves matter. The gate must not RUN — §6.5: "evaluating a gate whose result cannot change
        // the outcome spends real money for a number nobody acts on" — and the durable record must not
        // claim it passed. An ABSENT planGuardrails section satisfies the second half (the phase wrote
        // nothing because it never ran); the token this tree writes today, `passed`, never does.
        Assert.Equal(0, TerminalGateRunCount(repo));
        Assert.True(doc.PlanGuardrails is null || doc.PlanGuardrails.Status != PlanPhaseStatus.Passed,
            "the durable terminal-gate record says the gate PASSED on a run where it never looked at " +
            "anything (§6.5 correction 1: `planGuardrailsPassed` short-circuits to true). It must report " +
            "NOT EVALUATED.\n" + output);

        // ── §6.5 correction 2: run.json's delivery reason must not contradict its own tasks{} ────
        DeliverySection? delivery = doc.Delivery;
        Assert.NotNull(delivery);
        Assert.False(delivery!.Delivered,
            "run.json records this run as DELIVERED; reason: " + (delivery.Reason ?? "(none)"));
        Assert.False(string.IsNullOrWhiteSpace(delivery.Reason),
            "#542: the durable delivery record exists so an unattended pipeline with no console has a " +
            "machine-readable answer to 'did this run deliver?'. An undelivered run must say WHY.");
        Assert.DoesNotContain(NotWhollyGreenReason, delivery.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    // ── P11 — the two halts name the same task set ───────────────────────────────────────────────

    /// <summary>
    /// §6.6's P11. After the in-run divergence halt the operator runs <c>guardrails run &lt;folder&gt;</c>
    /// again; the §7.2 resume pre-pass compares current disk against the recorded PIN, mismatches on exactly
    /// the diverged tasks, and halts with the existing <c>DefinitionDrift</c> report — same set, same
    /// remediations. <i>"C is A's finding delivered one run earlier"</i>, and the gate therefore carries no
    /// remediation vocabulary of its own: its message points at §7.2's. <b>An implementation in which the two
    /// disagree about the set is wrong</b>, and that is this pin.
    ///
    /// <para><b>Both sets are read from surfaces that exist today.</b> The in-run set comes from the durable
    /// <c>decisions[]</c> entry §6.3 step 3 specifies (<c>boundary: "definition-divergence"</c>), whose
    /// <c>Subject</c> is "the unit the decision concerned" — the comma-joined task ids, the convention
    /// <c>DriftDecisions.Build</c> and <c>PlanEditDecisions.Observed</c> both already follow. The resume's set
    /// comes from the rendered <c>DEFINITION DRIFT</c> block, which this plan does not change. Neither reads
    /// <c>RunReport.ExecutedDefinitionDivergence</c>, which does not exist yet.</para>
    ///
    /// <para><b>Neither set may be empty.</b> A set comparison between two empty sets is the vacuous pass this
    /// pin exists to prevent — and is exactly what today's tree would produce for the in-run half.</para>
    ///
    /// <para><b>RED on this tree.</b> Nothing emits a <c>definition-divergence</c> decision at all, so the
    /// in-run set is empty at the first assertion.</para>
    /// </summary>
    [Fact]
    public async Task TheInRunDivergenceAndTheNextResumesDrift_NameTheSameTaskSet()
    {
        using var repo = new TempGitRepo("gr32-ddg-p11");
        string planDir = CreateMidRunEditPlan(repo);

        // ── run 1: drains to completion (§6.4 — no dispatch is stopped) and halts on the divergence ──
        (int firstExit, string firstOutput) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server");

        string[] inRun = TaskIdsNamedByDivergence(JournalOf(planDir).Decisions ?? []);
        Assert.True(inRun.Length > 0,
            $"the run recorded no '{DivergenceBoundary}' decision naming any task, so there is no in-run " +
            "divergence report for the resume to agree with (§6.3 step 3).\n" + firstOutput);
        Assert.Equal(ExitCodes.TaskFailed, firstExit);

        // ── run 2: the operator resumes. The §7.2 pre-pass halts on the same tasks ──────────────
        (int resumeExit, string resumeOutput) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.TaskFailed, resumeExit);
        Assert.Contains(DriftHeadline, resumeOutput, StringComparison.Ordinal);

        string[] resumed = TaskIdsNamedByDriftReport(resumeOutput);
        Assert.True(resumed.Length > 0,
            "the resume halted but its DEFINITION DRIFT report named no task at all.\n" + resumeOutput);

        // ── the pin: the same task ids, from both halts ─────────────────────────────────────────
        Assert.Equal(inRun, resumed);
    }

    // ── P13 — the work survives (DECLARED EXEMPTION: green today, must STAY green) ───────────────

    /// <summary>
    /// §6.7's P13. After a divergence halt the diverged task's integration commit is on the plan branch and
    /// its journal entry reads <c>succeeded</c>. Nothing is discarded, and the branch stays
    /// <b>Part-C-corroborable</b>.
    ///
    /// <para><b>Corroboration is asserted, not just presence, because that is what candidate (3) breaks.</b>
    /// <c>SafeSuffixEvaluator</c> refuses a Part C rewind over any commit whose <c>Guardrails-Task-Hash:</c>
    /// trailer the journal never recorded (a present-but-uncorroborated commit is honest-halt-over-destroy).
    /// An implementation that "refuses to record a success" on divergence leaves exactly that: the integration
    /// commit already landed — in worktree mode it lands BEFORE the journal settle — while the journal says
    /// otherwise, turning a recoverable drift into a mandatory full <c>guardrails reset -y</c>. So this row
    /// walks EVERY trailer on the plan branch and requires the journal to recognize it.</para>
    ///
    /// <para><b>DECLARED EXEMPTION from the red census.</b> Today a mid-run-edited run goes green, so the
    /// commit and the journal entry are both there and a CORRECT test is GREEN; demanding red would demand a
    /// correct implementation fail. Its job is to STAY green after stage 13.</para>
    ///
    /// <para><b>It deliberately does not assert the exit code.</b> That is P9's fact, and asserting it here
    /// would turn a regression pin into a second defect pin — the one thing an exempt row may not be.</para>
    /// </summary>
    [Fact]
    public async Task AfterADivergenceHalt_TheWorkSurvivesOnThePlanBranch()
    {
        using var repo = new TempGitRepo("gr32-ddg-p13");
        string planDir = CreateMidRunEditPlan(repo);

        (_, string output) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server");

        JournalDocument doc = JournalOf(planDir);

        // ── positive control: the definition really did move under the run ──────────────────────
        Assert.Contains(EditedMidRun,
            File.ReadAllText(Path.Combine(planDir, "tasks", Target, "task.json")), StringComparison.Ordinal);

        // ── 1. the journal entry reads succeeded — the settle was never refused (§6.4) ───────────
        Assert.True(doc.Tasks.TryGetValue(Target, out TaskJournalEntry? target),
            $"'{Target}' has no journal entry at all.\n{output}");
        Assert.True(target!.Status == JournalTaskStatus.Succeeded,
            $"'{Target}' must still SETTLE succeeded on a divergence run — refusing the settle discards paid " +
            $"work (#554) — but its journal status is '{target.Status}'.\n{output}");

        // ── 2. its integration commit, and its work, are on the plan branch ──────────────────────
        IReadOnlyDictionary<string, string> trailers = TaskHashTrailers(repo, planDir);
        Assert.True(trailers.ContainsKey(Target),
            $"'{Target}' has no Guardrails-Task: commit on the plan branch, so its paid work was discarded; " +
            "trailers found: " + string.Join(", ", trailers.Keys) + "\n" + output);

        string tree = TempGitRepo.Git(repo.RepoPath, "ls-tree", "-r", "--name-only", PlanBranchOf(planDir));
        Assert.Contains("src/second.txt", tree, StringComparison.Ordinal);

        // ── 3. Part-C-corroborable: every trailer on the branch is recognized by the journal ─────
        foreach (KeyValuePair<string, string> pair in trailers)
        {
            Assert.True(doc.Tasks.TryGetValue(pair.Key, out TaskJournalEntry? entry),
                $"the plan branch carries a commit for '{pair.Key}' that the journal knows nothing about — " +
                "a Part C rewind covering it would REFUSE (SafeSuffixEvaluator's trailer corroboration).");
            Assert.True(entry!.Status == JournalTaskStatus.Succeeded,
                $"'{pair.Key}' has an integration commit on the plan branch but its journal status is " +
                $"'{entry.Status}' — the present-but-uncorroborated state §7.2 Part C rule 3 refuses to " +
                "rewind past, which is why §6.4 records the success and blocks the delivery instead.");
            Assert.Equal(entry.DefinitionHash, pair.Value);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Drivers
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The REAL <c>run</c> command in-process, over a per-invocation console (parallel-safe). The exit code
    /// and the delivery seam are what P9 is about, so nothing here stands in for <c>RunCommand</c>.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunViaCliAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("divergence-delivery-gate test root");
        root.Add(RunCommand.Create(io));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    /// <summary>Read <c>state/run.json</c> from disk WITHOUT the resume normalization a reload applies.</summary>
    private static JournalDocument JournalOf(string planDir) =>
        JournalReader.Read(RunJournal.PathFor(planDir));

    /// <summary>The plan branch a worktree run integrates onto.</summary>
    private static string PlanBranchOf(string planDir) => "guardrails/" + Path.GetFileName(planDir);

    private static string Short(string sha) => sha.Length <= 8 ? sha : sha[..8];

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // The two task sets P11 compares
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The task ids the in-run divergence report NAMES, from the durable <c>decisions[]</c> surface.
    /// <para>Read as "which of the plan's task ids does a <c>definition-divergence</c> entry's
    /// <c>Subject</c> name", rather than by splitting <c>Subject</c> on a separator this file would then be
    /// pinning: the surrounding punctuation is the implementer's, the ids are the contract. No fixture id is
    /// a substring of another, so the membership test is exact.</para>
    /// </summary>
    private static string[] TaskIdsNamedByDivergence(IReadOnlyList<DecisionEntry> decisions)
    {
        DecisionEntry[] divergence = decisions
            .Where(d => string.Equals(d.Boundary, DivergenceBoundary, StringComparison.Ordinal))
            .ToArray();

        return AllTaskIds
            .Where(id => divergence.Any(d => d.Subject.Contains(id, StringComparison.Ordinal)))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The task ids the resume's <c>DefinitionDrift</c> report NAMES, parsed from the rendered halt.
    /// <para>Anchored on <c>RunCommand.PrintDefinitionDrift</c>'s per-task heading — a line that is EXACTLY
    /// two spaces plus the id — so neither the <c>full diff:</c> command (which contains the task's folder
    /// path) nor the remediation block (whose <c>&lt;taskId&gt;</c> is a literal placeholder) can be mistaken
    /// for a drifted task. Scoped to the text from the halt's own headline onward for the same reason.</para>
    /// </summary>
    private static string[] TaskIdsNamedByDriftReport(string output)
    {
        int start = output.IndexOf(DriftHeadline, StringComparison.Ordinal);
        string block = start < 0 ? "" : output[start..];
        string[] lines = block.Replace("\r\n", "\n").Split('\n');

        return AllTaskIds
            .Where(id => lines.Any(line => string.Equals(line, "  " + id, StringComparison.Ordinal)))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // The plan branch's Guardrails-Task-Hash: trailers, parsed independently of production
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The literal that separates one commit's message from the next in the log below.</summary>
    private const string CommitMarker = "@@gr-commit@@";

    /// <summary>
    /// <c>task id → Guardrails-Task-Hash:</c> read straight off the plan branch's commit messages. Parsed
    /// here rather than through <c>GitWorktreeProvider.ReconcileFromPlanBranch</c> on purpose: asking
    /// production to read back what production wrote is an echo, and P13's claim is that the bytes on the
    /// branch corroborate the bytes in the journal.
    /// <para><c>git log</c> is newest-first and the MOST RECENT integration per task wins, mirroring
    /// <c>GitWorktreeProvider.cs:864</c> — a task re-integrated by a later run must not be read at its stale
    /// first commit.</para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> TaskHashTrailers(TempGitRepo repo, string planDir)
    {
        string log = TempGitRepo.Git(repo.RepoPath,
            "log", "--format=" + CommitMarker + "%n%B", PlanBranchOf(planDir));

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string message in log.Split(CommitMarker, StringSplitOptions.RemoveEmptyEntries))
        {
            string? taskId = null;
            string? hash = null;
            foreach (string raw in message.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("Guardrails-Task: ", StringComparison.Ordinal))
                {
                    taskId = line["Guardrails-Task: ".Length..];
                }
                else if (line.StartsWith("Guardrails-Task-Hash: ", StringComparison.Ordinal))
                {
                    hash = line["Guardrails-Task-Hash: ".Length..];
                }
            }

            if (taskId is not null && hash is not null && !map.ContainsKey(taskId))
            {
                map[taskId] = hash;
            }
        }

        return map;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // The fixture — a two-task plan whose FIRST task edits the SECOND task's task.json mid-run,
    // plus a terminal <plan>/guardrails/ gate that records whether it was evaluated
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build <c>&lt;repo&gt;/plan</c>. <see cref="Editor"/> runs first and overwrites <see cref="Target"/>'s
    /// <c>task.json</c> in the REAL plan folder by absolute path — the plan folder is untracked, so it is in
    /// no segment worktree and this is genuinely an out-of-band write, exactly like an operator's editor.
    /// <see cref="Target"/> depends on it, so the DAG (not a timer) guarantees the write lands before the
    /// target settles. The rewrite keeps the same <c>writeScope</c> and <c>dependsOn</c> and moves only the
    /// description, so the task's BEHAVIOUR is identical and only its definition bytes change.
    /// </summary>
    private static string CreateMidRunEditPlan(TempGitRepo repo)
    {
        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        Write(Path.Combine(planDir, "guardrails.json"), Config);

        string targetTaskJson = Path.Combine(planDir, "tasks", Target, "task.json");

        WriteScriptTask(Path.Combine(planDir, "tasks", Editor), "first.txt", dependsOn: null,
            actionExtra: OverwriteFileLine(
                targetTaskJson, TaskJson(EditedMidRun, "src/second.txt", dependsOn: Editor)));
        WriteScriptTask(Path.Combine(planDir, "tasks", Target), "second.txt", dependsOn: Editor);

        // The terminal <plan>/guardrails/ gate (§6.5's fourth consumer). A single linear chain forms no
        // union, so GR2028's integration-re-run obligation is exempt and this validates clean. It APPENDS
        // to a log OUTSIDE the repo, so "the gate was not evaluated" is a fact this test can read.
        WriteExecutable(Path.Combine(planDir, "guardrails", Script("01-terminal")),
            Shebang
            + "# catches: the terminal plan gate being evaluated on a run whose outcome it cannot change\n"
            + AppendLine(TerminalGateLogOf(repo), "terminal gate evaluated") + "\n"
            + "exit 0\n");

        return planDir;
    }

    /// <summary>
    /// <c>maxParallelism: 2</c> is what puts this run in worktree mode. <c>mergeOnSuccess</c> is set
    /// EXPLICITLY — see the class remarks: it forces the decision-driven delivery suppression off, leaving
    /// <c>AllSucceeded</c> as the only seam that can hold the merge back. <c>autonomyPolicy: "halt"</c> keeps
    /// P11's resume deterministic on a host with an interactive stdin.
    /// </summary>
    private const string Config =
        """
        {
          "version": 1,
          "guardrailMode": "failFast",
          "workspace": "..",
          "defaultRetries": 0,
          "maxParallelism": 2,
          "mergeOnSuccess": true,
          "autonomyPolicy": "halt"
        }
        """;

    private static string TaskJson(string description, string writeScope, string? dependsOn)
    {
        string depends = dependsOn is null ? "[]" : $"[\"{dependsOn}\"]";
        return $$"""{ "description": "{{description}}", "writeScope": ["{{writeScope}}"], "dependsOn": {{depends}} }""";
    }

    /// <summary>
    /// A green script task that writes <c>src/&lt;file&gt;</c> into its segment worktree (its whole
    /// <c>writeScope</c>). <paramref name="actionExtra"/> is one extra line the action runs before
    /// <c>exit 0</c> — the out-of-band write that sequences the mid-run edit by the DAG.
    /// </summary>
    private static void WriteScriptTask(
        string taskDir, string file, string? dependsOn, string? actionExtra = null)
    {
        Write(Path.Combine(taskDir, "task.json"),
            TaskJson(Path.GetFileName(taskDir), "src/" + file, dependsOn));

        string extra = actionExtra is null ? "" : actionExtra + "\n";
        string action = Ps
            ? "New-Item -ItemType Directory -Force -Path \"$env:GUARDRAILS_WORKSPACE\\src\" | Out-Null\n"
              + $"Set-Content -NoNewline -Path \"$env:GUARDRAILS_WORKSPACE\\src\\{file}\" -Value 'written'\n"
              + extra
              + "exit 0\n"
            : "#!/usr/bin/env bash\n"
              + "mkdir -p \"$GUARDRAILS_WORKSPACE/src\"\n"
              + $"printf '%s' 'written' > \"$GUARDRAILS_WORKSPACE/src/{file}\"\n"
              + extra
              + "exit 0\n";
        WriteExecutable(Path.Combine(taskDir, Script("action")), action);

        WriteExecutable(Path.Combine(taskDir, "guardrails", Script("01-check")),
            Shebang
            + $"# catches: src/{file} missing from the workspace\n"
            + "exit 0\n");
    }

    /// <summary>One line that overwrites <paramref name="path"/> with <paramref name="content"/>.</summary>
    private static string OverwriteFileLine(string path, string content) => Ps
        ? $"Set-Content -NoNewline -Path '{path}' -Value '{content}'"
        : $"printf '%s' '{content}' > '{path}'";

    /// <summary>One line that APPENDS <paramref name="content"/> to <paramref name="path"/>.</summary>
    private static string AppendLine(string path, string content) => Ps
        ? $"Add-Content -Path '{path}' -Value '{content}'"
        : $"printf '%s\\n' '{content}' >> '{path}'";

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // The terminal gate's evaluated-or-not record
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The append-log the terminal <c>&lt;plan&gt;/guardrails/</c> check writes one line to each time it
    /// runs. It lives beside the repo rather than inside it, so it can never dirty the checkout the delivery
    /// merges into.
    /// </summary>
    private static string TerminalGateLogOf(TempGitRepo repo) =>
        Path.Combine(repo.Root, "terminal-gate-ran.log");

    /// <summary>How many times the terminal plan gate actually evaluated (0 = never — §6.5's decision).</summary>
    private static int TerminalGateRunCount(TempGitRepo repo)
    {
        string path = TerminalGateLogOf(repo);
        return File.Exists(path)
            ? File.ReadAllLines(path).Count(line => line.Trim().Length > 0)
            : 0;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // File helpers
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private static string Script(string stem) => Ps ? stem + ".ps1" : stem + ".sh";

    private static string Shebang => Ps ? "" : "#!/usr/bin/env bash\n";

    /// <summary>Write <paramref name="content"/>, creating the parent directory as needed.</summary>
    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WriteExecutable(string path, string content)
    {
        Write(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Windows-safe temp git repo (issue #116)
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A throwaway git repo under the system temp root — never this task's own worktree, so nothing these
    /// tests write can reach the checkout hosting the run (#253). Teardown strips read-only attributes
    /// FIRST: git marks loose objects under <c>.git/objects</c> read-only on Windows and
    /// <see cref="Directory.Delete(string, bool)"/> then throws <see cref="UnauthorizedAccessException"/>
    /// — which is NOT an <see cref="IOException"/>, so the usual catch does not catch it.
    /// <c>core.autocrlf=false</c> keeps fixture content bytes (and therefore definition hashes) identical
    /// across platforms.
    /// <para>Copy-pasted rather than shared: <c>TempGitRepo</c> is a private nested helper in ~32 files of
    /// this project (this is the house style), and extracting one is a different change touching files
    /// outside this task's write scope.</para>
    /// </summary>
    private sealed class TempGitRepo : IDisposable
    {
        /// <summary>The temp root holding the repo — the one place a fixture artifact can live OUTSIDE it.</summary>
        public string Root { get; }

        public string RepoPath { get; }

        public TempGitRepo(string prefix)
        {
            Root = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(Root, "repo");
            Directory.CreateDirectory(RepoPath);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            Git(RepoPath, "config", "core.autocrlf", "false");
            Write(Path.Combine(RepoPath, "README.md"), "# divergence-delivery-gate test\n");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public string CurrentBranch() => Git(RepoPath, "rev-parse", "--abbrev-ref", "HEAD").Trim();

        public string HeadSha() => Git(RepoPath, "rev-parse", "HEAD").Trim();

        public static string Git(string workingDir, params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            using Process proc = Process.Start(psi)!;
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

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    foreach (string f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(f, FileAttributes.Normal);
                    }

                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // best-effort teardown
            }
        }
    }
}
