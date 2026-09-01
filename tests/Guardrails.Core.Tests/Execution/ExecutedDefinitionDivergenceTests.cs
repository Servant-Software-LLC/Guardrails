using System.Collections.Concurrent;
using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests.Execution;

/// <summary>
/// Plan 32 (#556) milestone C — <b>the settle-time divergence gate</b>. Milestone A makes the NEXT RESUME
/// honest, but §6.1's sentence is the whole reason C exists: <i>a run that goes green to completion never
/// resumes</i>. With <c>mergeOnSuccess</c> ON by default, the headline scenario — an unattended overnight
/// run, a mid-run edit, everything ends green — delivers the stale-definition work and prints a green
/// summary, and the correctly-pinned hash is never read by anybody. So C compares, at every successful
/// settle, the task's LOAD-TIME per-file map against the same walk taken now, and on a non-empty diff
/// blocks DELIVERY while preserving the settle ("record the success, block the delivery", §6.4).
///
/// <para><b>Every assertion here is stated on the SERIALIZED ARTIFACT</b> — <c>state/run.json</c>: its full
/// property-name set, and the <c>boundary</c>/<c>decision</c>/<c>subject</c>/<c>detail</c> STRINGS of its
/// <c>decisions[]</c> entries. <c>RunReport.ExecutedDefinitionDivergence</c> (stage 13) and
/// <c>TaskJournalEntry.DefinitionHashAtSettle</c> (stage 12) do not exist on this tree and are deliberately
/// never named (§15 row 1): naming them would be CS0117, would need a stub stage in front of this one, and
/// would stop the implementation stages from legitimately carrying no <c>tests/**</c> path. It is also what
/// makes P10 a REAL full-list silence pin rather than a check for one absent token
/// (<see cref="TaskNode.DefinitionHashAtLoad"/> and <see cref="TaskNode.DefinitionFilesAtLoad"/> DO exist —
/// stage 3 — and are used freely as positive controls).</para>
///
/// <para><b>The execution shape, once.</b> Every run below drives the REAL <see cref="Scheduler"/> over a
/// real on-disk plan folder with a <see cref="RecordingWorktreeProvider"/> (no git) and a fake executor
/// whose results carry <c>DeferredSettle = true</c> — so every green settle takes the B1 DEFERRED path
/// (<c>Scheduler.SettleAsync</c>, write site <b>W2</b>, the default for a real run) and delivery runs
/// through the ONE seam §6.5 changes. "Delivering" is therefore an OBSERVABLE here
/// (<see cref="RunReport.DeliveredToBranch"/>), not a synonym for green — which matters, because §6.5's
/// single added <c>AllSucceeded</c> term is what gates it.</para>
///
/// <para><b>Two defect pins and three DECLARED EXEMPTIONS.</b>
/// <see cref="AJitBreakdownWritingOutsideItsWave_Diverges_WhileOneInsideItIsSilent"/> (P12) and
/// <see cref="ADivergenceIsReported_EvenAfterTheWatchAlreadyReportedAndReBaselined"/> (P15) FAIL on today's
/// tree — there is no divergence gate at all yet, so a correct pin MUST be red here.
/// <see cref="AnUneditedRun_WritesNoDivergenceKeyAndNoDivergenceDecision"/> (P10),
/// <see cref="AStrayEditorArtifactMidRun_LeavesTheRunGreenAndDelivering"/> (P16) and
/// <see cref="APreExistingEditorArtifact_LeavesTheRunGreenAndDelivering"/> (P16b) are SILENCE pins: true
/// today, and required to STAY true, so a correct test is GREEN on this tree and demanding red would demand
/// a correct implementation fail. They are written, never skipped — the census asserts they EXECUTED.</para>
/// </summary>
public sealed class ExecutedDefinitionDivergenceTests : IDisposable
{
    /// <summary>
    /// The <c>boundary</c> token §6.3 step 3 requires on the divergence entry. A STRING LITERAL on purpose:
    /// there is no constant for it on this tree (stage 13 adds one), and reaching for a name that does not
    /// exist yet is exactly the CS0117 this file must not contain.
    /// </summary>
    private const string DivergenceBoundary = "definition-divergence";

    /// <summary>P15's three-task chain: the task whose action edits the target's <c>task.json</c>.</summary>
    private const string Editor = "01-editor";

    /// <summary>P15's three-task chain: a task that does nothing but put more POLL BOUNDARIES between the
    /// edit and the target's settle, so "the watch already reported and re-baselined" is not a near-miss.</summary>
    private const string Spacer = "02-spacer";

    /// <summary>P15's chain: the task whose definition moves under it while it is in flight, by an edit the
    /// watch REPORTS and then adopts.</summary>
    private const string Target = "03-target";

    /// <summary>P15's chain: the task whose definition moves between the plan's LOAD and the
    /// <see cref="Scheduler"/>'s construction — so the watch's constructor baseline already holds the
    /// post-edit bytes and it is never reported at any boundary, in any form.</summary>
    private const string QuietTarget = "04-quiet-target";

    /// <summary>P16's two-task fixture: the task whose action drops the stray artifact.</summary>
    private const string Dropper = "01-dropper";

    /// <summary>P16's two-task fixture: the task the stray artifact lands under.</summary>
    private const string Littered = "02-littered";

    /// <summary>P12's already-authored first wave.</summary>
    private const string Wave1 = "wave-01-scaffold";

    /// <summary>P12's UNAUTHORED wave — the empty JIT stub whose breakdown fires at the checkpoint.</summary>
    private const string Wave2 = "wave-02-build";

    /// <summary>P12's already-authored LAST wave — the one the breakdown reaches OUTSIDE its own scope.</summary>
    private const string Wave3 = "wave-03-verify";

    /// <summary>The wave-qualified id of the task the JIT breakdown authors INSIDE its own wave (silent half).</summary>
    private const string InWaveTask = Wave2 + "/01-compile";

    /// <summary>The wave-qualified id of the task the JIT breakdown edits OUTSIDE its wave (firing half).</summary>
    private const string VictimTask = Wave3 + "/01-victim";

    /// <summary>The description P15's mid-run edit writes into the target's <c>task.json</c>.</summary>
    private const string EditedMidRun = "edited mid-run by another task's action";

    /// <summary>The description P15's between-load-and-Scheduler edit writes into <see cref="QuietTarget"/>'s
    /// <c>task.json</c> — the one the watch is structurally blind to.</summary>
    private const string EditedBeforeTheWatchExisted = "edited after the load, before the watch baselined";

    /// <summary>The description P12's out-of-wave breakdown write puts into the victim's <c>task.json</c>.</summary>
    private const string EditedByBreakdown = "rewritten by a JIT breakdown from another wave";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gr32-edd-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ExecutedDefinitionDivergenceTests() => Directory.CreateDirectory(_root);

    // ── P12 — the harness's own writers, ONE pin, TWO-SIDED ──────────────────────────────────────

    /// <summary>
    /// §6.7 P12 + §7. #557's JIT wave breakdown runs rooted at the PLAN directory with <c>acceptEdits</c>,
    /// full authoring tools and no containment hook, while <c>BreakdownInventory</c> is scoped to ONE wave —
    /// the set it can write is strictly larger than the set the harness can revert. This pin is the two
    /// halves of what the load-time pin does about that, and §6.7 does the reachability analysis by hand so
    /// this stays ONE test rather than five vacuous negatives: all five harness writers act at WAVE
    /// BOUNDARIES and none can execute between a task's dispatch and that task's settle within a wave.
    ///
    /// <para><b>Silent half.</b> <c>SpliceAuthoredWave</c> (§7) replaces ONLY the one authored wave, so the
    /// breakdown's own tasks arrive as fresh <see cref="TaskNode"/>s from a fresh <see cref="PlanLoader"/>
    /// load — fresh pins, nothing to diverge from. The breakdown's SANCTIONED work must stay quiet, and it
    /// does so mechanically rather than by a special case.</para>
    ///
    /// <para><b>Firing half.</b> Every OTHER wave's <see cref="WaveNode"/> — and therefore its
    /// <see cref="TaskNode"/>s and their pins — rides through the splice unchanged. So a breakdown writing
    /// OUTSIDE its own wave leaves the victim wave's pins pointing at bytes no longer on disk, and the
    /// victim's settle diverges. That is #557's exact violation, detected. It is also why the silent half is
    /// meaningful rather than vacuous: <b>a gate that never fires at all satisfies a one-sided silence
    /// pin</b>, and fails this one.</para>
    ///
    /// <para><b>RED today</b> on the firing half — nothing on this tree reports a divergence.</para>
    /// </summary>
    [Fact]
    public async Task AJitBreakdownWritingOutsideItsWave_Diverges_WhileOneInsideItIsSilent()
    {
        string planDir = NewPlanDir("p12");
        WriteConfig(planDir);
        WriteTask(Path.Combine(planDir, Wave1, "tasks", "01-config"), "wave-01's own task", dependsOn: null);
        Directory.CreateDirectory(Path.Combine(planDir, Wave2, "tasks")); // the empty JIT stub
        File.WriteAllText(Path.Combine(planDir, Wave2, WaveNode.BriefFileName),
            "# wave-02-build\nBuild the compiled artifact from wave-01's config.\n");
        WriteTask(Path.Combine(planDir, Wave3, "tasks", "01-victim"), "the victim, authored and LOADED",
            dependsOn: null);

        string victimTaskJson = Path.Combine(planDir, Wave3, "tasks", "01-victim", "task.json");

        // review-gate: proceed-unreviewed is what lets the run CONTINUE past the breakdown into wave-03
        // instead of halting for /guardrails-review — without it the victim never settles and there is no
        // settle for the gate to fire at. (The harness still writes no review marker; §5 floor 3.)
        PlanDefinition plan = ProceedUnreviewed(Load(planDir));
        TaskNode victim = plan.Tasks.Single(t => t.Id == VictimTask);

        var stub = new StubBreakdownRunner(inv =>
        {
            // (a) INSIDE its own wave — the sanctioned authoring a breakdown exists to do.
            string authored = Path.Combine(inv.WorkingDirectory, Wave2, "tasks", "01-compile");
            Directory.CreateDirectory(Path.Combine(authored, "guardrails"));
            File.WriteAllText(Path.Combine(authored, "task.json"),
                """{ "description": "compile", "writeScope": [] }""");
            File.WriteAllText(Path.Combine(authored, "action.sh"), "#!/bin/sh\necho hi\n");
            File.WriteAllText(Path.Combine(authored, "guardrails", "01-ok.sh"), "#!/bin/sh\nexit 0\n");

            // (b) OUTSIDE its own wave — #557's violation, into a wave the run already LOADED and pinned.
            File.WriteAllText(victimTaskJson, TaskJson(EditedByBreakdown, dependsOn: null));
        });

        (RunReport report, RunJournal journal) =
            await RunAsync(plan, new ScriptedExecutor(), new WaveBreakdownInvoker(stub));

        // ── positive controls: the scenario really happened ─────────────────────────────────────
        Assert.Equal(1, stub.Invocations);

        Assert.Contains(EditedByBreakdown, File.ReadAllText(victimTaskJson), StringComparison.Ordinal);
        Assert.NotEqual(victim.DefinitionHashAtLoad, TaskDefinitionHash.Compute(victim));

        // The run PROCEEDED past the checkpoint: both the freshly authored in-wave task and the victim in
        // the wave AFTER it settled. Without this the pin below would be about a run that never got there.
        AssertSettledSucceeded(journal, report, InWaveTask);
        AssertSettledSucceeded(journal, report, VictimTask);

        // ── the pin, both sides at once ─────────────────────────────────────────────────────────
        IReadOnlyList<RecordedDecision> all = DecisionsIn(journal);
        IReadOnlyList<RecordedDecision> divergences = DivergencesIn(all);

        Assert.True(divergences.Count == 1,
            $"expected EXACTLY ONE '{DivergenceBoundary}' decision — the out-of-wave victim '{VictimTask}' "
            + $"— but found {divergences.Count}. The breakdown wrote inside its own wave (silent: the "
            + "splice gave that wave fresh TaskNodes and therefore fresh pins) AND outside it (must fire: "
            + $"'{VictimTask}' rode through the splice with its load-time pin intact). Full decisions[]:\n  "
            + Render(all));

        RecordedDecision divergence = divergences[0];
        Assert.Equal(DecisionTokens.Halted, divergence.Decision);
        Assert.Contains(VictimTask, divergence.Subject + "\n" + divergence.Detail, StringComparison.Ordinal);

        // The SILENT half, stated rather than merely implied by the count: the breakdown's own sanctioned
        // in-wave authoring must appear in NO divergence entry.
        Assert.DoesNotContain(InWaveTask, Render(divergences), StringComparison.Ordinal);
    }

    // ── P15 — the gate's decision comes from the PIN, not the watch ──────────────────────────────

    /// <summary>
    /// §6.7 P15 — <b>the provenance discriminator, and the pin that decides whether THIS plan shipped or
    /// something else did.</b> §6.7 states the hazard verbatim: milestone C is fully satisfiable without
    /// ever consulting <see cref="TaskNode.DefinitionFilesAtLoad"/> — drive the divergence off
    /// <see cref="LivePlanEditWatch"/>'s already-collected <c>PlanEdit</c>s and P9 through P13 ALL pass,
    /// shipping the watch's MOVING baseline under this plan's name. Asserting the report's payload is not
    /// enough either: a watch-driven implementation can populate both hash fields from the watch's own
    /// before/after snapshot and satisfy a payload pin exactly.
    ///
    /// <para><b>So this pin discriminates on PROVENANCE, and it does so on TWO tasks in one run</b> — the
    /// two ways the watch's baseline is structurally incapable of standing in for the pin:</para>
    /// <list type="number">
    ///   <item><b><see cref="Target"/> — reported, then ADOPTED.</b> <see cref="LivePlanEditWatch.Poll"/> is
    ///     report-THEN-adopt, and the Scheduler polls at task boundaries. The edit is made by
    ///     <see cref="Editor"/>'s action into <see cref="Target"/>'s folder, so the poll that follows
    ///     <see cref="Editor"/> reports it and RE-BASELINES on it; further poll boundaries then pass in
    ///     silence before <see cref="Target"/> settles, and the watch will never mention that file again. A
    ///     gate that reacts to a poll AT the settle therefore sees nothing.</item>
    ///   <item><b><see cref="QuietTarget"/> — NEVER reported at all.</b> Its edit lands between the plan's
    ///     LOAD and the <see cref="Scheduler"/>'s CONSTRUCTION, and the watch baselines in its own
    ///     constructor — so the post-edit bytes ARE its baseline and no poll ever diffs. This one closes the
    ///     residual the first leaves open: an implementation that ACCUMULATES the watch's <c>PlanEdit</c>s
    ///     across the whole run (which is exactly the "already-collected" shape §6.7 names) still satisfies
    ///     row 1, and cannot produce this row at all. The pin, taken by <see cref="PlanLoader"/> a moment
    ///     earlier, sees both.</item>
    /// </list>
    ///
    /// <para>Which is why the assertion that the watch's own advisory names ONE task while the divergence
    /// names TWO is the load-bearing one here, not decoration: it is the direct evidence that the second
    /// verdict cannot have come from the watch in any form.</para>
    ///
    /// <para><b>RED today.</b></para>
    /// </summary>
    [Fact]
    public async Task ADivergenceIsReported_EvenAfterTheWatchAlreadyReportedAndReBaselined()
    {
        string planDir = NewPlanDir("p15");
        WriteConfig(planDir);
        WriteTask(Path.Combine(planDir, "tasks", Editor), "edits the target's task.json mid-run", dependsOn: null);
        WriteTask(Path.Combine(planDir, "tasks", Spacer), "burns poll boundaries", dependsOn: [Editor]);
        WriteTask(Path.Combine(planDir, "tasks", Target), "the task whose definition moves under it",
            dependsOn: [Spacer]);
        WriteTask(Path.Combine(planDir, "tasks", QuietTarget), "edited where the watch can never see it",
            dependsOn: [Target]);

        string targetTaskJson = Path.Combine(planDir, "tasks", Target, "task.json");
        string quietTaskJson = Path.Combine(planDir, "tasks", QuietTarget, "task.json");

        PlanDefinition plan = Load(planDir);
        TaskNode target = plan.Tasks.Single(t => t.Id == Target);
        TaskNode quiet = plan.Tasks.Single(t => t.Id == QuietTarget);

        // Row 2's edit: AFTER the load (so the pin is already taken) and BEFORE RunAsync constructs the
        // Scheduler (so LivePlanEditWatch's constructor baselines these very bytes and never diffs them).
        File.WriteAllText(quietTaskJson, TaskJson(EditedBeforeTheWatchExisted, dependsOn: [Target]));

        // Row 1's edit is sequenced by the DAG, not by a timer: the editor runs FIRST and writes into the
        // real plan folder by absolute path, exactly as an operator's editor would.
        var executor = new ScriptedExecutor(taskId =>
        {
            if (taskId == Editor)
            {
                File.WriteAllText(targetTaskJson, TaskJson(EditedMidRun, dependsOn: [Spacer]));
            }
        });

        (RunReport report, RunJournal journal) = await RunAsync(plan, executor);

        // ── positive controls: both edits really landed, and both tasks really settled ──────────
        Assert.Contains(EditedMidRun, File.ReadAllText(targetTaskJson), StringComparison.Ordinal);
        Assert.Contains(EditedBeforeTheWatchExisted, File.ReadAllText(quietTaskJson), StringComparison.Ordinal);
        AssertSettledSucceeded(journal, report, Target);
        AssertSettledSucceeded(journal, report, QuietTarget);

        // Milestone A landed: the RECORDED hash is the load-time pin, not a settle-time recompute. That is
        // what leaves a pinned baseline available at the settle for the gate to read.
        Assert.Equal(target.DefinitionHashAtLoad, journal.RecordedDefinitionHash(Target));
        Assert.NotEqual(target.DefinitionHashAtLoad, TaskDefinitionHash.Compute(target));
        Assert.Equal(quiet.DefinitionHashAtLoad, journal.RecordedDefinitionHash(QuietTarget));
        Assert.NotEqual(quiet.DefinitionHashAtLoad, TaskDefinitionHash.Compute(quiet));

        IReadOnlyList<RecordedDecision> all = DecisionsIn(journal);

        // ── what the WATCH saw, in full: ONE task, reported ONCE, then adopted ──────────────────
        IReadOnlyList<RecordedDecision> watchReports =
            [.. all.Where(d => d.Boundary == PlanEditDecisions.Boundary)];
        Assert.True(watchReports.Count == 1,
            "the watch must have reported the mid-run edit EXACTLY ONCE for the discriminator to mean "
            + "anything — once proves it reported, and only once proves Poll() then RE-BASELINED on it "
            + $"(further poll boundaries follow before '{Target}' settles). Found {watchReports.Count}. Full "
            + "decisions[]:\n  " + Render(all));
        Assert.Equal(DecisionTokens.Observed, watchReports[0].Decision);
        Assert.Contains(Target, watchReports[0].Subject + "\n" + watchReports[0].Detail, StringComparison.Ordinal);

        // The watch never saw the second edit AT ALL — it was already in the baseline the watch's own
        // constructor took. So nothing the watch collected, at any boundary or in aggregate, names this task.
        Assert.DoesNotContain(QuietTarget, Render(watchReports), StringComparison.Ordinal);

        // ...and the later boundaries really did happen: every task dispatched and settled, so the watch was
        // polled well after it adopted the first edit and stayed quiet every time. Per-task greenness is a
        // safe forward assertion: §6.4 preserves the settle unconditionally and §6.5 changes only the
        // AllSucceeded term, so it is DELIVERY that stops on a divergence run, never a task's own outcome.
        Assert.Equal(4, report.Tasks.Count);
        Assert.All(report.Tasks, t => Assert.True(t.IsGreen, $"{t.TaskId}={t.Outcome}"));

        // ── the pin: BOTH settles diverge, because the baseline is pinned and never adopts ──────
        IReadOnlyList<RecordedDecision> divergences = DivergencesIn(all);
        string named = Render(divergences);

        Assert.True(divergences.Count == 2,
            $"expected EXACTLY TWO '{DivergenceBoundary}' decisions — '{Target}' and '{QuietTarget}' — but "
            + $"found {divergences.Count}. The watch ALREADY reported '{Target}' and re-baselined on it, so "
            + $"it holds the post-edit bytes and will never report that file again; it never saw "
            + $"'{QuietTarget}' at all, because that edit landed before the watch existed. A gate driven off "
            + "the watch — at the boundary OR from its already-collected edits — produces at most one of "
            + "these, and zero of them today. Only a pinned baseline produces both. Full decisions[]:\n  "
            + Render(all));

        Assert.All(divergences, d => Assert.Equal(DecisionTokens.Halted, d.Decision));
        Assert.Contains(Target, named, StringComparison.Ordinal);
        Assert.Contains(QuietTarget, named, StringComparison.Ordinal);
    }

    // ── P10 — no divergence, no change (DECLARED EXEMPTION: green today, must STAY green) ────────

    /// <summary>
    /// §6.7 P10, and <b>Risk 3's only mitigation</b>. <c>AllSucceeded</c> gates delivery, the green summary
    /// and the exit code for EVERY run, so a defect in the new term silently stops the product delivering
    /// anything at all. This pin is the tripwire on that: an unedited run still delivers, and its
    /// <c>run.json</c> gains NO new key and NO new <c>decisions[]</c> entry — §6.3's "gate silent ⇒ field
    /// absent, and an unedited run's <c>run.json</c> is byte-identical".
    ///
    /// <para><b>Asserted on the FULL lists, never on the absence of one token</b> — plan 31 §8's lesson is
    /// that a silence pin scoped to one token passes trivially when the mechanism is broken. So: the whole
    /// sorted property-name set of <c>run.json</c> against a written-down literal, and the whole
    /// <c>decisions[]</c> list against the empty list. A stage-12 <c>definitionHashAtSettle</c> written on
    /// hash inequality rather than on the GATE VERDICT (§6.3's distinction, which an earlier draft got wrong
    /// three ways) shows up here as a new name in that set.</para>
    ///
    /// <para><b>DECLARED EXEMPTION from the red census.</b> Nothing emits the key or the decision today, so
    /// a CORRECT test is green on this tree; its job is to stay green after stage 13.</para>
    /// </summary>
    [Fact]
    public async Task AnUneditedRun_WritesNoDivergenceKeyAndNoDivergenceDecision()
    {
        string planDir = NewPlanDir("p10");
        WriteConfig(planDir);
        WriteTask(Path.Combine(planDir, "tasks", "01-first"), "an ordinary task nobody touches", dependsOn: null);
        WriteTask(Path.Combine(planDir, "tasks", "02-second"), "another one", dependsOn: ["01-first"]);

        PlanDefinition plan = Load(planDir);

        (RunReport report, RunJournal journal) = await RunAsync(plan, new ScriptedExecutor());

        // The run is green AND it delivers — the two things §6.5's single added AllSucceeded term can break.
        AssertGreenAndDelivering(report);

        // The FULL decisions list, not a filtered probe: rendered in its entirety so a spurious entry names
        // itself in the failure rather than hiding behind a `DoesNotContain`.
        Assert.Equal([], DecisionsIn(journal).Select(d => d.ToString()).ToArray());

        // The FULL property-name set of run.json — written down, never derived. Both surfaces §6.3 adds land
        // inside it: `definitionHashAtSettle` sits on the TASK ENTRY (beside `definitionHash` below) and the
        // divergence entry sits in the TOP-LEVEL `decisions[]`, so a key written on hash inequality rather
        // than on the GATE VERDICT — §6.3's distinction, which an earlier draft got wrong three different
        // ways — shows up here as a new name. (`attempts` is empty on this fixture: a deferred B1 settle
        // journals the settle, not an attempt record, so the set spans the top level and the task entry.)
        Assert.Equal(
            [
                "attempts",
                "definitionHash",
                "mergeSequence",
                "nextMergeSequence",
                "planHash",
                "runId",
                "status",
                "tasks",
                "version"
            ],
            KeyNamesIn(journal));
    }

    // ── P16 — the gate is QUIETER than the recorded hash (DECLARED EXEMPTION) ────────────────────

    /// <summary>
    /// §6.2's tripwire, at Core level. <c>HashText.EnumerateFolderFiles</c> globs <c>"*"</c> and filters
    /// nothing, so an editor or OS artifact IS part of a task's recorded definition today — and must stay
    /// that way, because changing that file set would move every recorded definition hash in every plan and
    /// turn the next resume of each into a drift halt. The GATE, by contrast, compares the
    /// IGNORE-LIST-FILTERED surface, so it never fires on a <c>.DS_Store</c>, a <c>Thumbs.db</c>, a
    /// <c>.swp</c>, or a <c>.orig</c>/<c>.rej</c>.
    ///
    /// <para><b>Both halves in one test</b>, exactly as §6.2 pairs them: the run stays green and DELIVERING,
    /// while that task's RECORDED hash still differs from its current on-disk recompute. A whole-surface
    /// gate turns this red — and §6.2 is blunt that a delivery gate which blocks on a stray file "is
    /// disabled within a week, and then the real signal is gone too" (#229).</para>
    ///
    /// <para><b>DECLARED EXEMPTION from the red census</b>: green today, required to be green after.</para>
    /// </summary>
    [Fact]
    public async Task AStrayEditorArtifactMidRun_LeavesTheRunGreenAndDelivering()
    {
        string planDir = NewPlanDir("p16");
        WriteConfig(planDir);
        WriteTask(Path.Combine(planDir, "tasks", Dropper), "drops a stray artifact mid-run", dependsOn: null);
        WriteTask(Path.Combine(planDir, "tasks", Littered), "the task it lands under", dependsOn: [Dropper]);

        string stray = Path.Combine(planDir, "tasks", Littered, "guardrails", ".DS_Store");

        PlanDefinition plan = Load(planDir);
        TaskNode littered = plan.Tasks.Single(t => t.Id == Littered);

        // ABSENT at load and PRESENT at settle — the asymmetry that makes this pin structurally unable to
        // see the load-side filtering bug, which is why P16b exists alongside it.
        Assert.DoesNotContain(".DS_Store", Render(littered.DefinitionFilesAtLoad!.Keys));

        var executor = new ScriptedExecutor(taskId =>
        {
            if (taskId == Dropper)
            {
                File.WriteAllText(stray, "Mac Finder metadata, not a definition change\n");
            }
        });

        (RunReport report, RunJournal journal) = await RunAsync(plan, executor);

        // ── positive control: the artifact really is on disk, and really is in the hashed surface ─
        Assert.True(File.Exists(stray), "the stray artifact was never written, so this pin is vacuous");

        // ── half 1: the RECORDED hash still differs from disk (HashText is untouched, §5.5/§6.2) ─
        Assert.NotEqual(TaskDefinitionHash.Compute(littered), journal.RecordedDefinitionHash(Littered));

        // ── half 2: and the run is nonetheless GREEN AND DELIVERING ─────────────────────────────
        AssertGreenAndDelivering(report);

        // The FULL decisions list, on the same terms as P10: the WATCH shares this ignore predicate (§6.2),
        // so an artifact must be silent on BOTH surfaces that speak to humans, not merely on the gate's.
        Assert.Equal([], DecisionsIn(journal).Select(d => d.ToString()).ToArray());
    }

    // ── P16b — the gate filters the LOAD side too (DECLARED EXEMPTION) ───────────────────────────

    /// <summary>
    /// §6.7 P16b — the half P16 structurally CANNOT cover, and the reachable one. P16's artifact appears
    /// MID-RUN, so it is absent from the load-time map and present in the settle walk; an implementation
    /// that filters only the SETTLE side still passes it. An artifact present AT LOAD is the case that
    /// bites: filtered on one side only, its label sits in <i>before</i> and not in <i>after</i>, reads as a
    /// <b>vanished</b> label, and blocks delivery on a run NOBODY EDITED.
    ///
    /// <para>Every trigger is ordinary and needs no one to touch the plan folder during the run: a
    /// <c>.DS_Store</c> already in the checkout, an operator's <c>.swp</c> from opening a guardrail to READ
    /// it, a <c>.orig</c>/<c>.rej</c> left by any pre-run git operation. All three are in the fixture, and
    /// the positive control asserts they are in <see cref="TaskNode.DefinitionFilesAtLoad"/> — because the
    /// capture is deliberately UNFILTERED (§5.2: the ignore predicate is private until stage 5, downstream
    /// of stage 3), which is precisely why §6.3 requires the GATE to filter BOTH sides.</para>
    ///
    /// <para><b>DECLARED EXEMPTION from the red census</b>, on the same terms as P16: together the two make
    /// the gate's quietness a two-sided property rather than a one-sided one.</para>
    /// </summary>
    [Fact]
    public async Task APreExistingEditorArtifact_LeavesTheRunGreenAndDelivering()
    {
        string planDir = NewPlanDir("p16b");
        WriteConfig(planDir);
        string taskDir = Path.Combine(planDir, "tasks", "01-solo");
        WriteTask(taskDir, "a task nobody edits, in a checkout that was not pristine", dependsOn: null);

        // ALREADY THERE when the plan loads. Nothing below edits anything, ever.
        File.WriteAllText(Path.Combine(taskDir, "guardrails", ".DS_Store"), "Finder metadata\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh.swp"), "vim swap\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh.orig"), "#!/bin/sh\nexit 0\n");

        PlanDefinition plan = Load(planDir);
        TaskNode solo = plan.Tasks.Single();

        // ── positive control: all three are in the UNFILTERED load-time map (§5.2) ───────────────
        // This is the state that makes the one-sided-filter bug reachable: filtered out of the settle walk
        // but not out of THIS map, each label reads as VANISHED and blocks delivery on a quiet run.
        string labelsAtLoad = Render(solo.DefinitionFilesAtLoad!.Keys);
        Assert.Contains(".DS_Store", labelsAtLoad, StringComparison.Ordinal);
        Assert.Contains("01-ok.sh.swp", labelsAtLoad, StringComparison.Ordinal);
        Assert.Contains("01-ok.sh.orig", labelsAtLoad, StringComparison.Ordinal);

        (RunReport report, RunJournal journal) = await RunAsync(plan, new ScriptedExecutor());

        // ── the pin: a run nobody edited is green AND delivering ────────────────────────────────
        AssertGreenAndDelivering(report);
        Assert.Equal([], DecisionsIn(journal).Select(d => d.ToString()).ToArray());

        // Nothing moved on disk either, so the recorded pin and a post-run recompute agree — which is what
        // makes "the gate fired" the ONLY possible explanation for a red here.
        Assert.Equal(TaskDefinitionHash.Compute(solo), journal.RecordedDefinitionHash(solo.Id));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Drivers
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A fake executor: every task succeeds and DEFERS its settle to the Scheduler's B1
    /// (<c>Scheduler.SettleAsync</c> — write site W2, the default for a real run), which is where milestone
    /// C's gate lives. <paramref name="onExecute"/> is the test's mid-run side effect, sequenced by the DAG:
    /// it runs while the task is in flight, so anything it writes lands strictly between the plan's load and
    /// the DEPENDENT task's settle.
    /// </summary>
    private sealed class ScriptedExecutor(Action<string>? onExecute = null) : ITaskExecutor
    {
        public ConcurrentQueue<string> Started { get; } = [];

        public Task<TaskResult> ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken ct)
        {
            Started.Enqueue(task.Id);
            onExecute?.Invoke(task.Id);
            return Task.FromResult(new TaskResult
            {
                TaskId = task.Id,
                Outcome = TaskOutcome.Succeeded,
                Summary = "scripted success",
                DeferredSettle = true
            });
        }
    }

    /// <summary>
    /// A STUB breakdown prompt runner: on invocation it runs <paramref name="author"/> — the test's
    /// simulated authoring — and returns a canned success. NO real Claude process is spawned, and
    /// <c>WorkingDirectory</c> is the PLAN directory, which is exactly the plan-wide write authority #557
    /// is about.
    /// </summary>
    private sealed class StubBreakdownRunner(Action<PromptInvocation> author) : IPromptRunner
    {
        public int Invocations { get; private set; }

        public string Name => "breakdown";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Invocations++;
            author(invocation);
            return Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                ResultText = "authored the wave",
                CostUsd = 0.42m,
                Summary = "breakdown authored the wave"
            });
        }
    }

    private static PlanDefinition Load(string planDir)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        Assert.NotNull(load.Plan);
        return load.Plan!;
    }

    /// <summary>
    /// The plan under <c>autonomyPolicy: auto</c> (so the JIT checkpoint auto-invokes the breakdown) with
    /// the review gate at <c>proceed-unreviewed</c> — the ONE setting under which the run continues into the
    /// waves AFTER a freshly authored one instead of halting for <c>/guardrails-review</c>. P12 needs that
    /// continuation because its victim settles in a LATER wave.
    /// </summary>
    private static PlanDefinition ProceedUnreviewed(PlanDefinition plan) =>
        plan with
        {
            Config = plan.Config with
            {
                AutonomyPolicy = AutonomyPolicy.Auto,
                Autonomy = new AutonomyConfig
                {
                    GateThresholds = new GateThresholds { ReviewGate = ReviewGateDecision.ProceedUnreviewed }
                }
            }
        };

    /// <summary>
    /// Drive the REAL <see cref="Scheduler"/> over <paramref name="plan"/> with a no-git
    /// <see cref="RecordingWorktreeProvider"/>. The <see cref="PlanDefinition"/> is passed IN and never
    /// re-loaded: the whole subject here is that the run executes the definition the caller already pinned,
    /// and a re-load would re-read <c>task.json</c> and destroy the load-vs-settle distinction under test.
    /// </summary>
    private static async Task<(RunReport Report, RunJournal Journal)> RunAsync(
        PlanDefinition plan, ITaskExecutor executor, WaveBreakdownInvoker? breakdownInvoker = null)
    {
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var scheduler = new Scheduler(
            plan, executor, journal,
            worktreeProvider: new RecordingWorktreeProvider(),
            observer: IRunObserver.Null,
            maxParallelism: 4,
            reVerifier: null,
            breakdownInvoker: breakdownInvoker);

        RunReport report = await scheduler.RunAsync(plan, Ct);
        return (report, journal);
    }

    /// <summary>
    /// The two things §6.5's single added <c>AllSucceeded</c> term is capable of breaking, asserted
    /// together: the run is wholly green, and its work actually REACHED the user's branch. "Green" alone is
    /// not the pin — P10/P16/P16b are about DELIVERY, and a term that goes false silently stops the product
    /// delivering anything while every task still reads succeeded.
    /// </summary>
    private static void AssertGreenAndDelivering(RunReport report)
    {
        Assert.True(report.AllSucceeded,
            "the run must stay wholly green; outcomes: "
            + string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        Assert.False(string.IsNullOrEmpty(report.DeliveredToBranch),
            "the run must still DELIVER — mergeOnSuccess is ON by default and AllSucceeded is the single "
            + "predicate that gates the merge-back (§6.5). A green run that stops delivering is Risk 3.");
    }

    /// <summary>
    /// The positive control every pin here shares: the task reached a SUCCESSFUL SETTLE, so there is a
    /// recorded definition and a settle for a gate to have run at.
    /// <para>Asserted on the JOURNAL rather than on <c>report.AllSucceeded</c> deliberately: milestone C
    /// blocks DELIVERY on a run carrying a definition edit while preserving the settle unconditionally
    /// (§6.4, "record the success, block the delivery"), so <c>AllSucceeded</c> is EXPECTED to go false on
    /// P12's and P15's runs while <c>status: succeeded</c> is required to stay.</para>
    /// </summary>
    private static void AssertSettledSucceeded(RunJournal journal, RunReport report, string taskId)
    {
        Assert.True(journal.Document.Tasks.TryGetValue(taskId, out TaskJournalEntry? entry),
            $"'{taskId}' has no journal entry at all; outcomes: "
            + string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        Assert.True(entry!.Status == JournalTaskStatus.Succeeded,
            $"'{taskId}' must have SETTLED for a settle-time gate to have anything to fire at, but its "
            + $"journal status is '{entry.Status}'; outcomes: "
            + string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // The serialized artifact: state/run.json
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One <c>decisions[]</c> entry as it is SERIALIZED — the four strings this plan's pins are stated on.
    /// Read off <c>run.json</c> rather than off <see cref="RunJournal.Document"/> on purpose: the durable
    /// artifact is what a post-mortem, the log site and the next resume all read, and stating the pins on it
    /// needs no API member stage 12/13 has not written yet.
    /// </summary>
    private sealed record RecordedDecision(string Boundary, string Decision, string Subject, string Detail)
    {
        public override string ToString() =>
            $"{Boundary}/{Decision} [{Subject}] {Detail.ReplaceLineEndings(" ")}";
    }

    /// <summary>Every <c>decisions[]</c> entry in <c>state/run.json</c>, in order. Empty when the key is absent.</summary>
    private static IReadOnlyList<RecordedDecision> DecisionsIn(RunJournal journal)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(journal.JournalPath));
        if (!doc.RootElement.TryGetProperty("decisions", out JsonElement decisions)
            || decisions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. decisions.EnumerateArray().Select(d => new RecordedDecision(
                Text(d, "boundary"), Text(d, "decision"), Text(d, "subject"), Text(d, "detail")))
        ];
    }

    private static IReadOnlyList<RecordedDecision> DivergencesIn(IReadOnlyList<RecordedDecision> all) =>
        [.. all.Where(d => d.Boundary == DivergenceBoundary)];

    private static string Render(IEnumerable<RecordedDecision> decisions) =>
        string.Join("\n  ", decisions.Select(d => d.ToString()).DefaultIfEmpty("(none)"));

    private static string Render(IEnumerable<string> lines) =>
        string.Join("\n  ", lines.DefaultIfEmpty("(none)"));

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>
    /// Every property NAME anywhere in <c>state/run.json</c>, sorted and de-duplicated — the full key set
    /// P10 pins. The <c>tasks</c> / <c>waves</c> objects are MAPS keyed by task id / wave dir, so their
    /// immediate child names are DATA, not schema: those are skipped and each entry is walked instead.
    /// </summary>
    private static IReadOnlyList<string> KeyNamesIn(RunJournal journal)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(journal.JournalPath));
        var names = new SortedSet<string>(StringComparer.Ordinal);
        CollectKeyNames(doc.RootElement, names);
        return [.. names];
    }

    private static void CollectKeyNames(JsonElement element, SortedSet<string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    into.Add(property.Name);

                    if (property.Value.ValueKind == JsonValueKind.Object
                        && property.Name is "tasks" or "waves")
                    {
                        foreach (JsonProperty entry in property.Value.EnumerateObject())
                        {
                            CollectKeyNames(entry.Value, into);
                        }
                    }
                    else
                    {
                        CollectKeyNames(property.Value, into);
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    CollectKeyNames(item, into);
                }

                break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Fixture: a real, loadable plan folder on disk
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private string NewPlanDir(string name)
    {
        string planDir = Path.Combine(_root, name);
        Directory.CreateDirectory(planDir);
        return planDir;
    }

    /// <summary>
    /// <c>maxParallelism: 1</c> in the manifest so the plan LOADS and validates without a git workspace; the
    /// Scheduler is constructed with a higher one, which is how every sibling scheduler test here works.
    /// <c>mergeOnSuccess</c> is left OMITTED on purpose — the #340 default (ON) is the configuration the
    /// headline scenario runs under, and P10/P16/P16b are about what that default does.
    /// </summary>
    private static void WriteConfig(string planDir) =>
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """{ "version": 1, "maxParallelism": 1 }""");

    private static string TaskJson(string description, string[]? dependsOn)
    {
        string depends = dependsOn is { Length: > 0 }
            ? ", \"dependsOn\": [" + string.Join(", ", dependsOn.Select(d => $"\"{d}\"")) + "]"
            : "";
        return $$"""{ "description": "{{description}}", "writeScope": []{{depends}} }""";
    }

    /// <summary>
    /// A green script task folder: <c>task.json</c> + an action discovered by convention + one guardrail.
    /// Nothing here is ever EXECUTED — the executor is a fake — so these files exist to be HASHED, which is
    /// the whole definition surface these pins are about.
    /// </summary>
    private static void WriteTask(string taskDir, string description, string[]? dependsOn)
    {
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), TaskJson(description, dependsOn));
        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/bin/sh\necho hi\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "#!/bin/sh\nexit 0\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }
}
