using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;

namespace Guardrails.Integration.Tests.Supply;

/// <summary>
/// Design 41 (<c>docs/plans/41-overwatcher-supply-autoresolve.md</c>) §7 — the REAL-PATH WIRING PROOF for
/// the overwatcher's missing-resource auto-resolve. §7 is the specification this file implements; §2.1,
/// §3.3, §4, §5 and §6 are the behaviour it proves.
/// <para>
/// <b>Driver.</b> Every test invokes <see cref="CommandFactory.BuildRootCommand"/>'s <c>run</c> command
/// in-process (the <see cref="SuppliedBoundaryWiringTests"/> driver) — never a hand-built
/// <c>RootCommand</c>, never a directly-constructed <c>Scheduler</c> or <c>Overwatch</c>, and never a call
/// to <c>Certify</c>. A test that injects the seam it claims to verify is green against a factory that was
/// never wired — the exact #120 failure this proof exists to close.
/// </para>
/// <para>
/// <b>None of design 41's production types exist on this tree yet</b> (<c>DecisionTokens.AutoSupplied</c>,
/// <c>OverwatchTrigger.MissingResource</c>, <c>SupplyCertification</c>,
/// <c>OverwatchSupplyAutoResolve.Certify</c>, <c>MissingResourceSignal</c>, <c>MissingResourceFacts</c>,
/// <c>GateThreshold</c>, <c>SuppliedDrain.CommitPaths</c>, the three-argument
/// <c>IRunObserver.SuppliedResourcesCommitted</c>), so this file asserts on the WIRE TOKENS as string
/// literals, read out of the artifacts a real run writes: <c>run.json</c> (<c>supplied[]</c>,
/// <c>decisions[]</c>, <c>delivery</c>), <c>events.jsonl</c>, the task's own <c>overwatch.jsonl</c>, and the
/// <c>--no-ui</c> console transcript.
/// </para>
/// <para>
/// <b>TDD red, against <c>fed690f2</c> (plan 41's baseline).</b> Seven of the ten tests below MUST fail on
/// this tree: <see cref="AtCriticalDial_SuppliesTheMissingResource_AndReArmsTheTask"/> (P1),
/// <see cref="WithMergeOnSuccess_AnAutoSuppliedRun_IsNotDelivered"/> (P2),
/// <see cref="WhenAnotherTaskOwnsThePath_NoBriefIsSent_AndTheStopIsObserved"/> (C2),
/// <see cref="WhenTheProposalCarriesNoFix_CertificationRefuses_NoResourceSupplyOp"/> (C3),
/// <see cref="WhenTheFileIsUncommittedInTheCheckout_NoBriefIsSent_NotCommittedInCheckout"/> (C4),
/// <see cref="WhenTheProposalNamesANonCandidate_CertificationRefuses_NotACandidate"/> (C5), and
/// <see cref="WhenTheProposalIsDoomed_CertificationRefuses_Doomed"/> (C6) — because nothing today reaches
/// <c>OverwatchSupplyAutoResolve</c> on this halt at all (design 41 fact 2): an agent-emitted
/// <c>needsHuman</c> settles at once and, at <c>dial:critical</c>, the halt goes to
/// <c>ClassifyTaskGateAsync</c> / <c>CriticalityJudge</c> instead, which this fixture's fake
/// <c>overwatch</c> runner always assesses <c>critical</c> — so every one of these runs ESCALATES task
/// <c>02-needs-resource</c> instead of resolving it.
/// </para>
/// <para>
/// <b>Three rows are DECLARED EXEMPT</b> from that red census —
/// <see cref="BelowCritical_TheOverwatcherIsNotConsulted_AndNothingIsSupplied"/> (C1),
/// <see cref="WithAPerGateNeedsHumanFloor_TheDialIsNotEngaged_AndNoBriefIsSent"/> (C7), and
/// <see cref="WhenTheResourceIsAlreadyOnTheRunBase_NothingIsSupplied_AndTheRunIsGreen"/> (N1) — each pins a
/// state that must NOT change, and on this tree nothing is consulted and nothing is supplied anywhere, so a
/// correct test is green on arrival BY CONSTRUCTION, not despite it. They still EXECUTE and are asserted
/// here exactly as strictly as the other seven.
/// </para>
/// <para>
/// <b>Fixture.</b> A private nested <see cref="TempGitRepo"/> stands in for the operator's checkout, on
/// <c>master</c>, with hooks isolated and <c>autocrlf</c>/signing off (mirrors
/// <see cref="SuppliedBoundaryWiringTests.TempGitRepo"/>). <see cref="OverwatchAutoResolvePlanBuilder"/>
/// writes the design §7 three-task plan (<c>01-operator-commits</c> commits <c>vendor/resource.js</c>
/// directly onto the checkout's <c>master</c>, outside its own worktree, which is what reproduces the
/// measured lineage gap against the run's already-cut integration branch; <c>02-needs-resource</c> is a
/// prompt task that halts <c>needs-human</c> when the file is absent from its own worktree and embeds it
/// when present; <c>03-downstream</c> proves the file propagated) plus the two-CLI <c>promptRunners</c>
/// pattern from <see cref="SchedulerEscalationWiringTests.EscalationPlanBuilder"/>: a default <c>action</c>
/// runner, and a reserved <c>overwatch</c> runner that routes on stdin's FIRST LINE
/// (<c># Overwatch resource supply:</c> / <c># Criticality assessment:</c> / anything else) and logs every
/// first line it sees — the log <see cref="AtCriticalDial_SuppliesTheMissingResource_AndReArmsTheTask"/>'s
/// assertion 10 and every "no brief was sent" control read.
/// </para>
/// </summary>
[Trait("Category", "OverwatchSupply")]
public sealed class OverwatchSupplyAutoResolveWiringTests
{
    // ── P1: the positive path ───────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "OverwatchSupply")]
    public async Task AtCriticalDial_SuppliesTheMissingResource_AndReArmsTheTask()
    {
        using OverwatchAutoResolvePlanBuilder plan = new();

        (int exit, string output) = await RunViaCliAsync(
            "run", plan.PlanDir, "--no-ui", "--no-log-server", "--no-merge-on-success");

        // 1. the run exits Success.
        Assert.Equal(ExitCodes.Success, exit);

        JournalDocument journal = ReadJournal(plan.PlanDir);
        string runId = journal.RunId;

        // 2. run.json supplied[] has exactly one record.
        Assert.NotNull(journal.Supplied);
        SuppliedRecord record = Assert.Single(journal.Supplied!);
        Assert.Equal("overwatcher", record.By);
        Assert.Equal(new[] { OverwatchAutoResolvePlanBuilder.ResourceRelativePath }, record.Paths);
        Assert.Equal(Encoding.UTF8.GetByteCount(OverwatchAutoResolvePlanBuilder.ResourceContent), record.Bytes);

        // 3. the recorded commit is on the plan branch, and never on master.
        Assert.True(
            plan.Repo.TryIsAncestor(record.Commit, plan.PlanBranch),
            $"supplied commit {record.Commit} must be an ancestor of {plan.PlanBranch}");
        Assert.False(
            plan.Repo.TryIsAncestor(record.Commit, "master"),
            $"supplied commit {record.Commit} must never reach master");
        string planBranchLog = plan.Repo.LogBody(plan.PlanBranch);
        Assert.Contains("Supplied-By: overwatcher", planBranchLog);
        Assert.Contains($"Guardrails-Run: {runId}", planBranchLog);

        // 4. the checkout is untouched by the harness.
        Assert.DoesNotContain("Supplied-By:", plan.Repo.LogBody("master"));
        Assert.Empty(plan.Repo.PorcelainStatus().Trim());

        // 5. task 02 was re-armed (needs-human, then succeeded); task 03 succeeded, never blocked.
        TaskJournalEntry task02 = journal.Tasks[OverwatchAutoResolvePlanBuilder.Task02Id];
        Assert.True(
            task02.Attempts.Count >= 2,
            "task 02 must show a needs-human attempt followed by a re-armed, successful one");
        Assert.Equal(AttemptOutcome.NeedsHuman, task02.Attempts[0].Outcome);
        Assert.Equal(AttemptOutcome.Succeeded, task02.Attempts[^1].Outcome);
        Assert.Equal(Core.Journal.TaskStatus.Succeeded, task02.Status);
        Assert.Equal(
            Core.Journal.TaskStatus.Succeeded,
            journal.Tasks[OverwatchAutoResolvePlanBuilder.Task03Id].Status);

        // 6. decisions[] holds exactly one auto-supplied entry for task 02, and no escalated /
        //    proceeded-best-guess entry for it.
        List<DecisionEntry> decisions = Decisions(plan.PlanDir).ToList();
        Assert.Single(
            decisions,
            d => d.Decision == "auto-supplied" && d.Subject == OverwatchAutoResolvePlanBuilder.Task02Id);
        Assert.DoesNotContain(
            decisions,
            d => d.Subject == OverwatchAutoResolvePlanBuilder.Task02Id
                 && (d.Decision == "escalated" || d.Decision == "proceeded-best-guess"));

        // 7. task 02's overwatch.jsonl carries a missing-resource record whose applied.commit matches.
        JsonNode? overwatchRecord = ReadJsonLines(
                OverwatchJsonlPath(plan.PlanDir, runId, OverwatchAutoResolvePlanBuilder.Task02Id))
            .FirstOrDefault(n => (string?)n!["trigger"] == "missing-resource");
        Assert.NotNull(overwatchRecord);
        Assert.Equal(record.Commit, (string?)overwatchRecord!["applied"]?["commit"]);

        // 8. the --no-ui output names the supplier and the commit.
        Assert.Contains($"[supplied] by overwatcher: 1 resource(s) committed {record.Commit}", output);

        // 9. events.jsonl carries the attribution.
        JsonNode? eventRow = ReadJsonLines(EventsPath(plan.PlanDir, runId))
            .FirstOrDefault(n => (string?)n!["kind"] == "supplied-resources-committed");
        Assert.NotNull(eventRow);
        Assert.Equal("overwatcher", (string?)eventRow!["by"]);
        Assert.Equal(record.Commit, (string?)eventRow!["commit"]);

        // 10. exactly one resource-supply brief was sent — without this a mistyped heading would make every
        //     "no brief was sent" control below pass while checking nothing.
        Assert.Single(
            plan.HeadingLines(),
            l => l.StartsWith(OverwatchAutoResolvePlanBuilder.ResourceSupplyHeadingPrefix, StringComparison.Ordinal));
    }

    // ── P2: the delivery interlock ──────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "OverwatchSupply")]
    public async Task WithMergeOnSuccess_AnAutoSuppliedRun_IsNotDelivered()
    {
        // P1's fixture WITHOUT --no-merge-on-success. The positive path alone passes with or without the
        // interlock, so without this test nothing proves the interlock is wired.
        using OverwatchAutoResolvePlanBuilder plan = new();

        (int exit, string output) = await RunViaCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.Success, exit);

        string masterLog = plan.Repo.LogBody("master");
        Assert.DoesNotContain("Supplied-By:", masterLog);
        Assert.DoesNotContain("Guardrails-Task:", masterLog);

        JournalDocument journal = ReadJournal(plan.PlanDir);
        Assert.NotNull(journal.Delivery);
        Assert.Equal(DeliveryOutcome.NotAttempted, journal.Delivery!.Outcome);

        Assert.Contains(
            $"this run recorded 'auto-supplied' at '{OverwatchAutoResolvePlanBuilder.Task02Id}'", output);
    }

    // ── C1: below critical, the overwatcher is never consulted ─────────────────────────────────────

    [Fact]
    [Trait("Category", "OverwatchSupply")]
    public async Task BelowCritical_TheOverwatcherIsNotConsulted_AndNothingIsSupplied()
    {
        // DECLARED EXEMPT from the red census (design 41 §7): pins a state that must not change, and on
        // this tree nothing is consulted anywhere, so this is green by construction.
        using OverwatchAutoResolvePlanBuilder plan = new(escalationThreshold: "high");

        await RunViaCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server", "--no-merge-on-success");

        AssertControlSharedFacts(plan);
        AssertNoResourceSupplyBriefSent(plan);

        List<DecisionEntry> decisions = Decisions(plan.PlanDir).ToList();
        Assert.DoesNotContain(
            decisions,
            d => d.Subject == OverwatchAutoResolvePlanBuilder.Task02Id
                 && (d.Decision == "auto-supplied" || d.Decision == "observed" || d.Decision == "advisory"));
    }

    // ── C2: another task owns the candidate path ────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "OverwatchSupply")]
    public async Task WhenAnotherTaskOwnsThePath_NoBriefIsSent_AndTheStopIsObserved()
    {
        using OverwatchAutoResolvePlanBuilder plan = new(includeOwnerTask: true);

        await RunViaCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server", "--no-merge-on-success");

        AssertControlSharedFacts(plan);
        AssertNoResourceSupplyBriefSent(plan);

        List<DecisionEntry> decisions = Decisions(plan.PlanDir).ToList();
        Assert.Contains(
            decisions,
            d => d.Subject == OverwatchAutoResolvePlanBuilder.Task02Id
                 && d.Decision == "observed" && Mentions(d, "produced-by-another-task"));
    }

    // ── C3: the proposal carries no resource-supply fix ─────────────────────────────────────────────

    [Fact]
    [Trait("Category", "OverwatchSupply")]
    public async Task WhenTheProposalCarriesNoFix_CertificationRefuses_NoResourceSupplyOp()
    {
        // Proves Certify is on the real path: a wiring that supplied on any resource-supply op, never
        // calling the gate, would pass every OTHER test in this file.
        using OverwatchAutoResolvePlanBuilder plan =
            new(proposalJson: OverwatchAutoResolvePlanBuilder.NoFixProposalJson);

        await RunViaCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server", "--no-merge-on-success");

        AssertControlSharedFacts(plan);

        List<DecisionEntry> decisions = Decisions(plan.PlanDir).ToList();
        Assert.Contains(
            decisions,
            d => d.Subject == OverwatchAutoResolvePlanBuilder.Task02Id
                 && d.Decision == "advisory" && Mentions(d, "no-resource-supply-op"));
    }

    // ── C4: the file is present but never committed in the checkout ────────────────────────────────

    [Fact]
    [Trait("Category", "OverwatchSupply")]
    public async Task WhenTheFileIsUncommittedInTheCheckout_NoBriefIsSent_NotCommittedInCheckout()
    {
        using OverwatchAutoResolvePlanBuilder plan = new(commitResource: false);

        await RunViaCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server", "--no-merge-on-success");

        AssertControlSharedFacts(plan);
        AssertNoResourceSupplyBriefSent(plan);

        List<DecisionEntry> decisions = Decisions(plan.PlanDir).ToList();
        Assert.Contains(
            decisions,
            d => d.Subject == OverwatchAutoResolvePlanBuilder.Task02Id
                 && d.Decision == "observed" && Mentions(d, "not-committed-in-checkout"));
    }

    // ── C5: the proposal names a path the facts never established ──────────────────────────────────

    [Fact]
    [Trait("Category", "OverwatchSupply")]
    public async Task WhenTheProposalNamesANonCandidate_CertificationRefuses_NotACandidate()
    {
        // Proves Certify is on the real path (design 41 §7): the model cannot introduce a path the harness
        // never established as a candidate.
        using OverwatchAutoResolvePlanBuilder plan =
            new(proposalJson: OverwatchAutoResolvePlanBuilder.NonCandidateProposalJson);

        await RunViaCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server", "--no-merge-on-success");

        AssertControlSharedFacts(plan);

        List<DecisionEntry> decisions = Decisions(plan.PlanDir).ToList();
        Assert.Contains(
            decisions,
            d => d.Subject == OverwatchAutoResolvePlanBuilder.Task02Id
                 && d.Decision == "advisory" && Mentions(d, "not-a-candidate"));
    }

    // ── C6: the proposal is doomed ───────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "OverwatchSupply")]
    public async Task WhenTheProposalIsDoomed_CertificationRefuses_Doomed()
    {
        // Proves Certify is on the real path (design 41 §7 / §3.1 check 3).
        using OverwatchAutoResolvePlanBuilder plan =
            new(proposalJson: OverwatchAutoResolvePlanBuilder.DoomedProposalJson);

        await RunViaCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server", "--no-merge-on-success");

        AssertControlSharedFacts(plan);

        List<DecisionEntry> decisions = Decisions(plan.PlanDir).ToList();
        Assert.Contains(
            decisions,
            d => d.Subject == OverwatchAutoResolvePlanBuilder.Task02Id
                 && d.Decision == "advisory" && Mentions(d, "doomed"));
    }

    // ── C7: a per-gate needs-human floor below critical overrides the run-wide dial ────────────────

    [Fact]
    [Trait("Category", "OverwatchSupply")]
    public async Task WithAPerGateNeedsHumanFloor_TheDialIsNotEngaged_AndNoBriefIsSent()
    {
        // DECLARED EXEMPT from the red census (design 41 §7): proves the EFFECTIVE per-gate threshold, not
        // merely the run-wide escalationThreshold, gates the dial. Green by construction on this tree.
        using OverwatchAutoResolvePlanBuilder plan =
            new(escalationThreshold: "critical", needsHumanThreshold: "high");

        await RunViaCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server", "--no-merge-on-success");

        AssertControlSharedFacts(plan);
        AssertNoResourceSupplyBriefSent(plan);

        List<DecisionEntry> decisions = Decisions(plan.PlanDir).ToList();
        Assert.DoesNotContain(
            decisions,
            d => d.Subject == OverwatchAutoResolvePlanBuilder.Task02Id
                 && (d.Decision == "auto-supplied" || d.Decision == "observed" || d.Decision == "advisory"));
    }

    // ── N1: never-weaker — the resource is already on the run's base ───────────────────────────────

    [Fact]
    [Trait("Category", "OverwatchSupply")]
    public async Task WhenTheResourceIsAlreadyOnTheRunBase_NothingIsSupplied_AndTheRunIsGreen()
    {
        // DECLARED EXEMPT from the red census (design 41 §7): the never-weaker floor. Green by
        // construction on this tree — nothing needs supplying, so nothing is.
        using OverwatchAutoResolvePlanBuilder plan = new(preExisting: true);

        (int exit, _) = await RunViaCliAsync(
            "run", plan.PlanDir, "--no-ui", "--no-log-server", "--no-merge-on-success");

        Assert.Equal(ExitCodes.Success, exit);

        JournalDocument journal = ReadJournal(plan.PlanDir);
        Assert.Null(journal.Supplied);
        Assert.Equal(
            Core.Journal.TaskStatus.Succeeded,
            journal.Tasks[OverwatchAutoResolvePlanBuilder.Task02Id].Status);

        AssertNoResourceSupplyBriefSent(plan);
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

    private static IReadOnlyList<DecisionEntry> Decisions(string planDir) =>
        ReadJournal(planDir).Decisions ?? Array.Empty<DecisionEntry>();

    /// <summary>Whether <paramref name="entry"/>'s headline or detail mentions the reason <paramref name="token"/> (its exact rendering is not yet wired, so this checks both free-text surfaces rather than one fixed format).</summary>
    private static bool Mentions(DecisionEntry entry, string token) =>
        entry.Headline.Contains(token, StringComparison.Ordinal) ||
        entry.Detail.Contains(token, StringComparison.Ordinal);

    private static string EventsPath(string planDir, string runId) =>
        Path.Combine(planDir, "logs", runId, "events.jsonl");

    private static string OverwatchJsonlPath(string planDir, string runId, string taskId) =>
        Path.Combine(planDir, "logs", runId, taskId, "overwatch.jsonl");

    private static IReadOnlyList<JsonNode> ReadJsonLines(string path) =>
        File.Exists(path)
            ? File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!).ToList()
            : Array.Empty<JsonNode>();

    /// <summary>The three facts every control (C1-C7) shares (design 41 §7): nothing was supplied, no supply commit reached the plan branch, and task 02 stayed needs-human.</summary>
    private static void AssertControlSharedFacts(OverwatchAutoResolvePlanBuilder plan)
    {
        JournalDocument journal = ReadJournal(plan.PlanDir);
        Assert.Null(journal.Supplied);
        Assert.DoesNotContain("Supplied-By:", plan.Repo.LogBody(plan.PlanBranch));
        Assert.Equal(
            Core.Journal.TaskStatus.NeedsHuman,
            journal.Tasks[OverwatchAutoResolvePlanBuilder.Task02Id].Status);
    }

    private static void AssertNoResourceSupplyBriefSent(OverwatchAutoResolvePlanBuilder plan) =>
        Assert.DoesNotContain(
            plan.HeadingLines(),
            l => l.StartsWith(OverwatchAutoResolvePlanBuilder.ResourceSupplyHeadingPrefix, StringComparison.Ordinal));

    // ── the fixture: plan, fake runners, and the operator's checkout ───────────────────────────────

    /// <summary>
    /// Builds design 41 §7's fixture plan inside a fresh <see cref="TempGitRepo"/>: the three-task plan
    /// (<c>01-operator-commits</c> / <c>02-needs-resource</c> / <c>03-downstream</c>, plus an optional
    /// <c>04-owns-vendor</c> for C2) and the two <c>promptRunners</c> fakes (<c>action</c>, <c>overwatch</c>)
    /// from <see cref="SchedulerEscalationWiringTests.EscalationPlanBuilder"/>'s pattern. Deliberately does
    /// NOT reuse <c>FakeClaudePlanBuilder</c>: its #253 containment gate answers every supervisory call with
    /// a non-verdict, which would make every overwatcher consult here prove nothing.
    /// </summary>
    private sealed class OverwatchAutoResolvePlanBuilder : IDisposable
    {
        public const string ResourceRelativePath = "vendor/resource.js";
        public const string ResourceContent = "vendor bundle fixture content for design 41 real-path proof";
        public const string DoneFileName = "02-done.txt";
        public const string ResourceSupplyHeadingPrefix = "# Overwatch resource supply:";
        public const string CriticalityHeadingPrefix = "# Criticality assessment:";

        public const string PlanFolderName = "plan";
        public const string Task01Id = "01-operator-commits";
        public const string Task02Id = "02-needs-resource";
        public const string Task03Id = "03-downstream";
        public const string Task04Id = "04-owns-vendor";

        private const string NeedsHumanQuestion =
            "Cannot embed the runtime: vendor/resource.js is missing from this worktree; " +
            "I will not stub or fetch it.";

        // ── the four proposal shapes design 41 §7's controls exercise ──────────────────────────────

        private const string FixProposalJson =
            """{"classification": "retryable", "diagnosis": "the task cannot embed a runtime it does not have", "fixes": [{"kind": "resource-supply", "path": "vendor/resource.js"}]}""";

        public const string NoFixProposalJson =
            """{"classification": "retryable", "diagnosis": "no candidate file resolves this question", "fixes": []}""";

        public const string NonCandidateProposalJson =
            """{"classification": "retryable", "diagnosis": "the task cannot embed a runtime it does not have", "fixes": [{"kind": "resource-supply", "path": "vendor/other.js"}]}""";

        public const string DoomedProposalJson =
            """{"classification": "doomed", "diagnosis": "no candidate file resolves this question", "fixes": [{"kind": "resource-supply", "path": "vendor/resource.js"}]}""";

        private const string CriticalAssessmentJson =
            """{"criticality": "critical", "confidence": "high", "bestGuess": "halt and ask a human", "rationale": "an irreversible resource substitution"}""";

        private const string UnrelatedDiagnoseJson =
            """{"classification": "retryable", "diagnosis": "unrelated", "fixes": []}""";

        // ── static script bodies (no per-test parameters — everything varies through env or the file
        //    system, never through template substitution, so these are compile-time constants) ───────

        private const string Task01ActionPs1 =
            """
            $mode = $env:COMMIT_MODE
            if ($mode -eq 'skip') {
                exit 0
            }
            $vendorDir = Join-Path $env:CHECKOUT_PATH 'vendor'
            New-Item -ItemType Directory -Force -Path $vendorDir | Out-Null
            Set-Content -NoNewline -Path (Join-Path $vendorDir 'resource.js') -Value 'vendor bundle fixture content for design 41 real-path proof'
            if ($mode -eq 'commit') {
                git -C $env:CHECKOUT_PATH add vendor/resource.js
                git -C $env:CHECKOUT_PATH commit -m 'operator commits the vendored runtime' --quiet
            }
            exit 0
            """;

        private const string Task01ActionSh =
            """
            #!/usr/bin/env bash
            mode="$COMMIT_MODE"
            if [ "$mode" = "skip" ]; then
              exit 0
            fi
            mkdir -p "$CHECKOUT_PATH/vendor"
            printf '%s' 'vendor bundle fixture content for design 41 real-path proof' > "$CHECKOUT_PATH/vendor/resource.js"
            if [ "$mode" = "commit" ]; then
              git -C "$CHECKOUT_PATH" add vendor/resource.js
              git -C "$CHECKOUT_PATH" commit -m 'operator commits the vendored runtime' --quiet
            fi
            exit 0
            """;

        private const string Task02ActionPs1 =
            """
            $null = [Console]::In.ReadToEnd()
            $target = Join-Path $env:GUARDRAILS_WORKSPACE 'vendor/resource.js'
            $expected = 'vendor bundle fixture content for design 41 real-path proof'
            if ((Test-Path $target) -and ((Get-Content -Raw -Path $target) -eq $expected)) {
                Set-Content -NoNewline -Path (Join-Path $env:GUARDRAILS_WORKSPACE '02-done.txt') -Value 'embedded'
                if ($env:GUARDRAILS_STATE_OUT) {
                    Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value ('{"' + $env:GUARDRAILS_TASK_ID + '": {"produced": true}}')
                }
                Write-Output '{"type":"result","is_error":false,"result":"embedded the runtime","num_turns":1}'
            } else {
                if ($env:GUARDRAILS_STATE_OUT) {
                    Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value '{"needsHuman": {"question": "Cannot embed the runtime: vendor/resource.js is missing from this worktree; I will not stub or fetch it.", "kind": "blocked-work"}}'
                }
                Write-Output '{"type":"result","is_error":false,"result":"asked a human","num_turns":1}'
            }
            """;

        private const string Task02ActionSh =
            """
            #!/usr/bin/env bash
            cat > /dev/null
            target="$GUARDRAILS_WORKSPACE/vendor/resource.js"
            expected='vendor bundle fixture content for design 41 real-path proof'
            if [ -f "$target" ] && [ "$(cat "$target")" = "$expected" ]; then
              printf '%s' 'embedded' > "$GUARDRAILS_WORKSPACE/02-done.txt"
              if [ -n "$GUARDRAILS_STATE_OUT" ]; then
                printf '{"%s": {"produced": true}}' "$GUARDRAILS_TASK_ID" > "$GUARDRAILS_STATE_OUT"
              fi
              printf '{"type":"result","is_error":false,"result":"embedded the runtime","num_turns":1}\n'
            else
              if [ -n "$GUARDRAILS_STATE_OUT" ]; then
                printf '%s' '{"needsHuman": {"question": "Cannot embed the runtime: vendor/resource.js is missing from this worktree; I will not stub or fetch it.", "kind": "blocked-work"}}' > "$GUARDRAILS_STATE_OUT"
              fi
              printf '{"type":"result","is_error":false,"result":"asked a human","num_turns":1}\n'
            fi
            """;

        private const string Task02GuardrailPs1 =
            """
            $vendor = Join-Path $env:GUARDRAILS_WORKSPACE 'vendor/resource.js'
            $done = Join-Path $env:GUARDRAILS_WORKSPACE '02-done.txt'
            $expected = 'vendor bundle fixture content for design 41 real-path proof'
            if ((Test-Path $vendor) -and ((Get-Content -Raw -Path $vendor) -eq $expected) -and (Test-Path $done)) {
                exit 0
            } else {
                exit 1
            }
            """;

        private const string Task02GuardrailSh =
            """
            #!/usr/bin/env bash
            vendor="$GUARDRAILS_WORKSPACE/vendor/resource.js"
            done_file="$GUARDRAILS_WORKSPACE/02-done.txt"
            expected='vendor bundle fixture content for design 41 real-path proof'
            if [ -f "$vendor" ] && [ "$(cat "$vendor")" = "$expected" ] && [ -f "$done_file" ]; then
              exit 0
            else
              exit 1
            fi
            """;

        private const string Task03GuardrailPs1 =
            """
            $vendor = Join-Path $env:GUARDRAILS_WORKSPACE 'vendor/resource.js'
            $expected = 'vendor bundle fixture content for design 41 real-path proof'
            if ((Test-Path $vendor) -and ((Get-Content -Raw -Path $vendor) -eq $expected)) {
                exit 0
            } else {
                exit 1
            }
            """;

        private const string Task03GuardrailSh =
            """
            #!/usr/bin/env bash
            vendor="$GUARDRAILS_WORKSPACE/vendor/resource.js"
            expected='vendor bundle fixture content for design 41 real-path proof'
            if [ -f "$vendor" ] && [ "$(cat "$vendor")" = "$expected" ]; then
              exit 0
            else
              exit 1
            fi
            """;

        private const string TrivialActionPs1 = "exit 0\r\n";
        private const string TrivialActionSh = "#!/usr/bin/env bash\nexit 0\n";
        private const string TrivialGuardrailPs1 = "exit 0\r\n";
        private const string TrivialGuardrailSh = "#!/usr/bin/env bash\nexit 0\n";

        private readonly bool _windows = OperatingSystem.IsWindows();
        private readonly string _fixtureRoot;
        private readonly string _headingLogPath;

        public TempGitRepo Repo { get; }
        public string PlanDir { get; }
        public string PlanBranch { get; }

        public OverwatchAutoResolvePlanBuilder(
            string escalationThreshold = "critical",
            string? needsHumanThreshold = null,
            string proposalJson = FixProposalJson,
            bool commitResource = true,
            bool preExisting = false,
            bool includeOwnerTask = false)
        {
            Repo = new TempGitRepo();
            PlanDir = Path.Combine(Repo.RepoPath, PlanFolderName);
            PlanBranch = $"guardrails/{PlanFolderName}";
            Directory.CreateDirectory(Path.Combine(PlanDir, "tasks"));

            // Untracked (never `git add`ed): the fixture's own scaffolding — fake CLIs and their shared
            // heading log — sits outside the committed plan, exactly like the operator's own tooling would.
            _fixtureRoot = Path.Combine(Repo.RepoPath, "_fixture");
            Directory.CreateDirectory(_fixtureRoot);
            _headingLogPath = Path.Combine(_fixtureRoot, "overwatch-headings.log");
            File.WriteAllText(_headingLogPath, "");

            if (preExisting)
            {
                // N1 (never-weaker): the file is committed to master BEFORE the run starts, so the
                // integration branch is cut from a base that already has it — nothing to supply.
                string vendorDir = Path.Combine(Repo.RepoPath, "vendor");
                Directory.CreateDirectory(vendorDir);
                File.WriteAllText(Path.Combine(vendorDir, "resource.js"), ResourceContent);
                Repo.Git("add", "vendor/resource.js");
                Repo.Git("commit", "-m", "operator commits the vendored runtime ahead of the run");
            }

            string actionCliPath = WriteActionCli();
            string overwatchCliPath = WriteOverwatchCli(proposalJson);

            WriteGuardrailsJson(escalationThreshold, needsHumanThreshold, actionCliPath, overwatchCliPath);

            string commitMode = preExisting ? "skip" : (commitResource ? "commit" : "write-only");
            WriteTask01(commitMode);
            WriteTask02();
            WriteTask03();
            if (includeOwnerTask)
            {
                WriteTask04Owner();
            }

            Repo.Git("add", PlanFolderName);
            Repo.Git("commit", "-m", "add the design-41 real-path proof fixture plan");
        }

        /// <summary>Every first line the fake <c>overwatch</c> runner has seen, in invocation order.</summary>
        public IReadOnlyList<string> HeadingLines() =>
            File.ReadAllLines(_headingLogPath).Where(l => l.Length > 0).ToList();

        public void Dispose() => Repo.Dispose();

        // ── the two fake prompt runners (SchedulerEscalationWiringTests.EscalationPlanBuilder's pattern) ──

        private string WriteActionCli() => WriteFakeCli("action", Task02ActionPs1, Task02ActionSh);

        private string WriteOverwatchCli(string proposalJson)
        {
            string resourceSupplyLine = WrapResult(proposalJson);
            string criticalityLine = WrapResult(CriticalAssessmentJson);
            string otherLine = WrapResult(UnrelatedDiagnoseJson);

            string ps1 = OverwatchPsBody(_headingLogPath, resourceSupplyLine, criticalityLine, otherLine);
            string sh = OverwatchShBody(_headingLogPath, resourceSupplyLine, criticalityLine, otherLine);
            return WriteFakeCli("overwatch", ps1, sh);
        }

        /// <summary>Wraps <paramref name="resultJson"/> in the stream-json <c>result</c> line <c>ClaudePromptRunner</c> parses.</summary>
        private static string WrapResult(string resultJson) =>
            JsonSerializer.Serialize(new
            {
                type = "result",
                is_error = false,
                result = resultJson,
                total_cost_usd = 0,
                num_turns = 1
            });

        /// <summary>Reads stdin's first line, logs it, drains the rest, then routes on it (design 41 §7).</summary>
        private static string OverwatchPsBody(
            string logPath, string resourceSupplyLine, string criticalityLine, string otherLine) =>
            "$firstLine = [Console]::In.ReadLine()\r\n" +
            "[void][Console]::In.ReadToEnd()\r\n" +
            "Add-Content -Path '" + logPath + "' -Value $firstLine\r\n" +
            "if ($firstLine -and $firstLine.StartsWith('" + ResourceSupplyHeadingPrefix + "')) {\r\n" +
            "    Write-Output '" + resourceSupplyLine + "'\r\n" +
            "} elseif ($firstLine -and $firstLine.StartsWith('" + CriticalityHeadingPrefix + "')) {\r\n" +
            "    Write-Output '" + criticalityLine + "'\r\n" +
            "} else {\r\n" +
            "    Write-Output '" + otherLine + "'\r\n" +
            "}\r\n";

        private static string OverwatchShBody(
            string logPath, string resourceSupplyLine, string criticalityLine, string otherLine) =>
            "#!/usr/bin/env bash\n" +
            "IFS= read -r firstLine\n" +
            "cat > /dev/null\n" +
            "printf '%s\\n' \"$firstLine\" >> '" + logPath + "'\n" +
            "case \"$firstLine\" in\n" +
            "  '" + ResourceSupplyHeadingPrefix + "'*) printf '%s\\n' '" + resourceSupplyLine + "' ;;\n" +
            "  '" + CriticalityHeadingPrefix + "'*) printf '%s\\n' '" + criticalityLine + "' ;;\n" +
            "  *) printf '%s\\n' '" + otherLine + "' ;;\n" +
            "esac\n";

        /// <summary>Windows: a <c>.cmd</c> shim invoking a <c>.ps1</c>. Elsewhere: a <c>chmod</c>-ed <c>.sh</c> (mirrors <c>EscalationPlanBuilder.WriteFakeCli</c>).</summary>
        private string WriteFakeCli(string name, string psBody, string shBody)
        {
            if (_windows)
            {
                string cmdPath = Path.Combine(_fixtureRoot, name + ".cmd");
                string ps1Path = Path.Combine(_fixtureRoot, name + ".ps1");
                File.WriteAllText(ps1Path, psBody);
                File.WriteAllText(
                    cmdPath, $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1Path}\"\r\n");
                return cmdPath;
            }

            string shPath = Path.Combine(_fixtureRoot, name + ".sh");
            WriteExecutable(shPath, shBody);
            return shPath;
        }

        private static void WriteExecutable(string path, string content)
        {
            File.WriteAllText(path, content);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }

        // ── guardrails.json ──────────────────────────────────────────────────────────────────────────

        private void WriteGuardrailsJson(
            string escalationThreshold, string? needsHumanThreshold, string actionCliPath, string overwatchCliPath)
        {
            var autonomy = new Dictionary<string, object?> { ["escalationThreshold"] = escalationThreshold };
            if (needsHumanThreshold is not null)
            {
                autonomy["gateThresholds"] = new Dictionary<string, string>
                {
                    ["needs-human"] = needsHumanThreshold
                };
            }

            var config = new Dictionary<string, object?>
            {
                ["version"] = 1,
                ["guardrailMode"] = "failFast",
                ["workspace"] = "..",
                ["defaultRetries"] = 0,
                ["maxParallelism"] = 2,
                ["defaultTimeoutSeconds"] = 60,
                ["transientPauseBudgetSeconds"] = 30,
                ["maxCostUsd"] = 50,
                ["autonomyPolicy"] = "auto",
                ["autonomy"] = autonomy,
                ["promptRunners"] = new Dictionary<string, object?>
                {
                    ["default"] = "claude",
                    ["claude"] = new Dictionary<string, object?>
                    {
                        ["command"] = actionCliPath,
                        ["permissionMode"] = "acceptEdits",
                        ["allowedTools"] = new[] { "Read", "Write" },
                        ["maxTurns"] = 5
                    },
                    ["overwatch"] = new Dictionary<string, object?>
                    {
                        ["command"] = overwatchCliPath,
                        ["permissionMode"] = "default",
                        ["allowedTools"] = new[] { "Read" },
                        ["maxTurns"] = 5
                    }
                }
            };

            File.WriteAllText(Path.Combine(PlanDir, "guardrails.json"), JsonSerializer.Serialize(config));
        }

        // ── the four task folders ────────────────────────────────────────────────────────────────────

        private void WriteTask01(string commitMode)
        {
            string taskDir = Path.Combine(PlanDir, "tasks", Task01Id);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

            var task = new Dictionary<string, object?>
            {
                ["description"] =
                    "commits the vendored runtime directly onto the operator's checkout, outside its own " +
                    "worktree — the measured lineage gap against the run's already-cut integration branch",
                ["writeScope"] = Array.Empty<string>(),
                ["dependsOn"] = Array.Empty<string>(),
                ["action"] = new Dictionary<string, object?>
                {
                    ["env"] = new Dictionary<string, string>
                    {
                        ["CHECKOUT_PATH"] = Repo.RepoPath,
                        ["COMMIT_MODE"] = commitMode
                    }
                }
            };
            File.WriteAllText(Path.Combine(taskDir, "task.json"), JsonSerializer.Serialize(task));

            if (_windows)
            {
                File.WriteAllText(Path.Combine(taskDir, "action.ps1"), Task01ActionPs1);
                File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.ps1"), TrivialGuardrailPs1);
            }
            else
            {
                WriteExecutable(Path.Combine(taskDir, "action.sh"), Task01ActionSh);
                WriteExecutable(Path.Combine(taskDir, "guardrails", "01-ok.sh"), TrivialGuardrailSh);
            }
        }

        private void WriteTask02()
        {
            string taskDir = Path.Combine(PlanDir, "tasks", Task02Id);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

            var task = new Dictionary<string, object?>
            {
                ["description"] =
                    "embeds the vendored runtime into its own artifact; halts needs-human when the runtime " +
                    "is not on its own worktree, the shape of a task that embeds rather than owns a resource",
                ["writeScope"] = new[] { DoneFileName },
                ["dependsOn"] = new[] { Task01Id }
            };
            File.WriteAllText(Path.Combine(taskDir, "task.json"), JsonSerializer.Serialize(task));
            File.WriteAllText(
                Path.Combine(taskDir, "action.prompt.md"),
                "Embed vendor/resource.js into this task's own artifact.\n");

            if (_windows)
            {
                File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), Task02GuardrailPs1);
            }
            else
            {
                WriteExecutable(Path.Combine(taskDir, "guardrails", "01-check.sh"), Task02GuardrailSh);
            }
        }

        private void WriteTask03()
        {
            string taskDir = Path.Combine(PlanDir, "tasks", Task03Id);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

            var task = new Dictionary<string, object?>
            {
                ["description"] =
                    "downstream task — its guardrail passes only if the vendored runtime propagated to its " +
                    "own worktree",
                ["writeScope"] = Array.Empty<string>(),
                ["dependsOn"] = new[] { Task02Id }
            };
            File.WriteAllText(Path.Combine(taskDir, "task.json"), JsonSerializer.Serialize(task));

            if (_windows)
            {
                File.WriteAllText(Path.Combine(taskDir, "action.ps1"), TrivialActionPs1);
                File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), Task03GuardrailPs1);
            }
            else
            {
                WriteExecutable(Path.Combine(taskDir, "action.sh"), TrivialActionSh);
                WriteExecutable(Path.Combine(taskDir, "guardrails", "01-check.sh"), Task03GuardrailSh);
            }
        }

        /// <summary>C2 only: an independent task that owns <c>vendor/**</c>, so <c>vendor/resource.js</c> fails candidate check 4 ("produced-by-another-task").</summary>
        private void WriteTask04Owner()
        {
            string taskDir = Path.Combine(PlanDir, "tasks", Task04Id);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

            var task = new Dictionary<string, object?>
            {
                ["description"] = "an independent task that owns the vendor/** path",
                ["writeScope"] = new[] { "vendor/**" },
                ["dependsOn"] = Array.Empty<string>()
            };
            File.WriteAllText(Path.Combine(taskDir, "task.json"), JsonSerializer.Serialize(task));

            if (_windows)
            {
                File.WriteAllText(Path.Combine(taskDir, "action.ps1"), TrivialActionPs1);
                File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.ps1"), TrivialGuardrailPs1);
            }
            else
            {
                WriteExecutable(Path.Combine(taskDir, "action.sh"), TrivialActionSh);
                WriteExecutable(Path.Combine(taskDir, "guardrails", "01-ok.sh"), TrivialGuardrailSh);
            }
        }
    }

    // ── a real git repository standing in for the operator's checkout ─────────────────────────────

    /// <summary>
    /// A throwaway single-use git repository in a temp directory, copied from
    /// <see cref="SuppliedBoundaryWiringTests.TempGitRepo"/> (there is no shared fixture project — this
    /// shape is duplicated per test file across <c>tests/</c>). Hooks are pointed at an empty directory
    /// inside <c>.git</c>, <c>autocrlf</c> and <c>commit.gpgsign</c> are forced off, so a machine-global git
    /// config can never affect content bytes or block a commit in this throwaway repo. <c>HEAD</c> is
    /// pinned to <c>refs/heads/master</c> explicitly (before the first commit, so it costs nothing) rather
    /// than trusting the ambient <c>init.defaultBranch</c>, because this file's assertions name
    /// <c>master</c> literally.
    /// </summary>
    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _rootDir;
        public string RepoPath { get; }

        public TempGitRepo()
        {
            _rootDir = Path.Combine(Path.GetTempPath(), "gr-overwatch-supply-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_rootDir, "repo");
            Directory.CreateDirectory(RepoPath);

            Git("init");
            Git("symbolic-ref", "HEAD", "refs/heads/master");
            string hooks = Path.Combine(RepoPath, ".git", "no-hooks");
            Directory.CreateDirectory(hooks);
            Git("config", "core.hooksPath", hooks);
            Git("config", "core.autocrlf", "false");
            Git("config", "commit.gpgsign", "false");
            Git("config", "user.email", "test@guardrails.local");
            Git("config", "user.name", "Guardrails Test");

            File.WriteAllText(
                Path.Combine(RepoPath, "README.md"), "# design-41 overwatch supply auto-resolve fixture");
            Git("add", "README.md");
            Git("commit", "-m", "Initial commit");
        }

        /// <summary>The full body (trailers included) of every commit reachable from <paramref name="branch"/>.</summary>
        public string LogBody(string branch) => Git("log", branch, "--format=%B");

        /// <summary>The content of <paramref name="path"/> as committed on <paramref name="branch"/>.</summary>
        public string Show(string branch, string path) => Git("show", $"{branch}:{path}");

        /// <summary>Whether <paramref name="sha"/> is an ancestor of <paramref name="branch"/> — never throws on the ordinary "no" answer (exit 1), unlike <see cref="Git"/>.</summary>
        public bool TryIsAncestor(string sha, string branch)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("merge-base");
            psi.ArgumentList.Add("--is-ancestor");
            psi.ArgumentList.Add(sha);
            psi.ArgumentList.Add(branch);

            using var process = Process.Start(psi)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }

        /// <summary>The checkout's working-tree status, ignoring untracked files — the flag that ignores the runtime-state <c>.gitignore</c> the harness scaffolds on first run (design 41 §7 P1 assertion 4).</summary>
        public string PorcelainStatus() => Git("status", "--porcelain", "--untracked-files=no");

        /// <summary>Runs an arbitrary git command in this repo. Public: the fixture builder uses this directly for its own <c>add</c>/<c>commit</c> setup.</summary>
        public string Git(params string[] arguments)
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
