using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;

namespace Guardrails.Integration.Tests.Supply;

/// <summary>
/// The real-path wiring proof for the overwatcher's missing-resource auto-resolve (design 41,
/// <c>docs/plans/41-overwatcher-supply-autoresolve.md</c> §7, issue #712). Every test drives the REAL
/// composition root — <see cref="CommandFactory.BuildRootCommand"/>'s <c>run</c>, in process — against a
/// real git repository in worktree mode (<c>maxParallelism: 2</c>). No test ever constructs a
/// <c>Scheduler</c> or an <c>Overwatch</c>, and none ever calls <c>Certify</c> directly: that is exactly
/// the #120/#382 shape this proof exists to close (a test that injects the seam it claims to verify, green
/// against a factory that was never wired).
/// <para>
/// None of this feature's production types exist on this tree yet — <c>OverwatchSupplyAutoResolve.Certify</c>,
/// <c>MissingResourceFacts</c>, <c>MissingResourceSignal</c>, <c>DecisionTokens.AutoSupplied</c>,
/// <c>DecisionTokens.Advisory</c>, <c>OverwatchTrigger.MissingResource</c>, and the three-argument
/// <c>IRunObserver.SuppliedResourcesCommitted(paths, commit, by)</c> among them — so every assertion below
/// reads the WIRE TOKENS the design settles (design §6/§12: <c>"auto-supplied"</c>, <c>"advisory"</c>,
/// <c>"observed"</c>, <c>"overwatcher"</c>, <c>"missing-resource"</c>, <c>"not-a-candidate"</c>,
/// <c>"doomed"</c>, <c>"no-resource-supply-op"</c>, <c>"produced-by-another-task"</c>,
/// <c>"not-committed-in-checkout"</c>, <c>"supplied-resources-committed"</c>) as string literals out of the
/// artifacts a real run writes — <c>run.json</c>, <c>events.jsonl</c>, the task's <c>overwatch.jsonl</c>,
/// and the <c>--no-ui</c> console transcript — rather than a reference to any not-yet-authored symbol.
/// </para>
/// <para>
/// <b>TDD red, against `master` as of this writing.</b> Seven of the ten tests below must FAIL on this
/// tree, because nothing today consults the overwatcher on an agent-emitted <c>needsHuman</c>, certifies a
/// resource-supply proposal, or commits a supplied file onto the plan branch:
/// <see cref="AtCriticalDial_SuppliesTheMissingResource_AndReArmsTheTask"/> (P1),
/// <see cref="WithMergeOnSuccess_AnAutoSuppliedRun_IsNotDelivered"/> (P2),
/// <see cref="WhenAnotherTaskOwnsThePath_NoBriefIsSent_AndTheStopIsObserved"/> (C2),
/// <see cref="WhenTheProposalCarriesNoFix_CertificationRefuses_NoResourceSupplyOp"/> (C3),
/// <see cref="WhenTheFileIsUncommittedInTheCheckout_NoBriefIsSent_NotCommittedInCheckout"/> (C4),
/// <see cref="WhenTheProposalNamesANonCandidate_CertificationRefuses_NotACandidate"/> (C5), and
/// <see cref="WhenTheProposalIsDoomed_CertificationRefuses_Doomed"/> (C6).
/// </para>
/// <para>
/// <b>Three tests are DECLARED EXEMPT from the red census</b> (design §7): each pins a state that must NOT
/// change, and on this tree today nothing about the missing-resource halt is ever consulted and nothing is
/// ever supplied anywhere, so a correct test is green on arrival, not despite it —
/// <see cref="BelowCritical_TheOverwatcherIsNotConsulted_AndNothingIsSupplied"/> (C1),
/// <see cref="WithAPerGateNeedsHumanFloor_TheDialIsNotEngaged_AndNoBriefIsSent"/> (C7), and
/// <see cref="WhenTheResourceIsAlreadyOnTheRunBase_NothingIsSupplied_AndTheRunIsGreen"/> (N1). They still
/// EXIST and EXECUTE — the census requires all ten observed <c>Passed</c>, none skipped.
/// </para>
/// </summary>
[Trait("Category", "OverwatchSupply")]
public sealed class OverwatchSupplyAutoResolveWiringTests
{
    private const string PlanBranch = "guardrails/" + Fixture.PlanName;
    private const string ResourceSupplyBriefPrefix = "# Overwatch resource supply:";

    // ── P1 — the positive path ──────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "OverwatchSupply")]
    public async Task AtCriticalDial_SuppliesTheMissingResource_AndReArmsTheTask()
    {
        using var fx = new Fixture(escalationThreshold: "critical");
        fx.AddOperatorCommitsTask().AddNeedsResourceTask().AddDownstreamTask();

        (int exit, string output) = await fx.RunAsync("--no-merge-on-success");

        // 1. the run exits Success.
        Assert.Equal(ExitCodes.Success, exit);

        JournalDocument journal = fx.Journal();

        // 2. run.json supplied[] has exactly one record, By == "overwatcher", Paths == the resource path,
        //    Bytes equal to the blob size.
        Assert.NotNull(journal.Supplied);
        SuppliedRecord record = Assert.Single(journal.Supplied!);
        Assert.Equal("overwatcher", record.By);
        Assert.Equal(Fixture.ResourcePath, Assert.Single(record.Paths));
        long expectedBytes = fx.Repo.BlobSize(fx.Repo.RevParse("master"), Fixture.ResourcePath);
        Assert.Equal(expectedBytes, record.Bytes);

        // 3. the recorded commit is on the plan branch, not master, and carries both trailers.
        Assert.True(
            fx.Repo.IsAncestor(record.Commit, PlanBranch),
            $"supplied[] commit {record.Commit} is not an ancestor of {PlanBranch}");
        Assert.False(
            fx.Repo.IsAncestor(record.Commit, "master"),
            $"supplied[] commit {record.Commit} must not be reachable from master");
        string commitMessage = fx.Repo.Git("log", "-1", "--format=%B", record.Commit);
        Assert.Contains("Supplied-By: overwatcher", commitMessage);
        Assert.Contains($"Guardrails-Run: {fx.RunId()}", commitMessage);

        // 4. the checkout is untouched by the harness.
        Assert.DoesNotContain("Supplied-By:", fx.Repo.LogBody("master"));
        Assert.Equal("", fx.Repo.StatusPorcelainNoUntracked().Trim());

        // 5. the task was re-armed: a needs-human attempt then a succeeded one; task 03 succeeded, never blocked.
        TaskJournalEntry task02 = journal.Tasks["02-needs-resource"];
        Assert.Equal(2, task02.Attempts.Count);
        Assert.Equal(AttemptOutcome.NeedsHuman, task02.Attempts[0].Outcome);
        Assert.Equal(AttemptOutcome.Succeeded, task02.Attempts[1].Outcome);
        Assert.Equal(Core.Journal.TaskStatus.Succeeded, task02.Status);
        Assert.Equal(Core.Journal.TaskStatus.Succeeded, journal.Tasks["03-downstream"].Status);

        // 6. decisions[] holds exactly one auto-supplied entry for 02-needs-resource, and no
        //    escalated/proceeded-best-guess entry for it.
        IReadOnlyList<DecisionEntry> decisions = journal.Decisions ?? [];
        Assert.Single(decisions, d => d.Decision == "auto-supplied" && d.Subject == "02-needs-resource");
        Assert.DoesNotContain(
            decisions,
            d => d.Subject == "02-needs-resource"
                 && (d.Decision == DecisionTokens.Escalated || d.Decision == DecisionTokens.ProceededBestGuess));

        // 7. task 02's overwatch.jsonl has a missing-resource record whose applied.commit equals the
        //    supplied[] commit.
        JsonObject missingResourceRecord = Assert.Single(
            fx.ReadOverwatchJsonl("02-needs-resource"), r => (string?)r["trigger"] == "missing-resource");
        JsonObject applied = Assert.IsType<JsonObject>(missingResourceRecord["applied"]);
        Assert.Equal(record.Commit, (string?)applied["commit"]);

        // 8. the --no-ui output announces the supply and names the commit.
        Assert.Contains("[supplied] by overwatcher: 1 resource(s) committed", output);
        Assert.Contains(record.Commit, output);

        // 9. events.jsonl has a supplied-resources-committed row with by: "overwatcher" and that commit.
        JsonObject suppliedEvent = Assert.Single(fx.ReadEvents(), e => (string?)e["kind"] == "supplied-resources-committed");
        Assert.Equal("overwatcher", (string?)suppliedEvent["by"]);
        Assert.Equal(record.Commit, (string?)suppliedEvent["commit"]);

        // 10. exactly one resource-supply brief was sent — the positive check behind every "no brief" control.
        Assert.Single(fx.OverwatchBriefLines(), l => l.StartsWith(ResourceSupplyBriefPrefix, StringComparison.Ordinal));
    }

    // ── P2 — the delivery interlock ─────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "OverwatchSupply")]
    public async Task WithMergeOnSuccess_AnAutoSuppliedRun_IsNotDelivered()
    {
        using var fx = new Fixture(escalationThreshold: "critical");
        fx.AddOperatorCommitsTask().AddNeedsResourceTask().AddDownstreamTask();

        (int _, string output) = await fx.RunAsync();

        JournalDocument journal = fx.Journal();

        // Precondition: the auto-supply itself happened — otherwise this test would pass for the boring
        // reason that nothing was ever suppressed.
        Assert.NotNull(journal.Supplied);
        Assert.Contains(journal.Decisions ?? [], d => d.Decision == "auto-supplied" && d.Subject == "02-needs-resource");

        // master gains no commit carrying either trailer.
        Assert.DoesNotContain("Supplied-By:", fx.Repo.LogBody("master"));
        Assert.DoesNotContain("Guardrails-Task:", fx.Repo.LogBody("master"));

        // delivery.outcome is not-attempted.
        Assert.NotNull(journal.Delivery);
        Assert.Equal(DeliveryOutcome.NotAttempted, journal.Delivery!.Outcome);

        // the interlock banner names the cause.
        Assert.Contains("this run recorded 'auto-supplied' at '02-needs-resource'", output);
    }

    // ── C1 — below critical, the dial is not engaged (DECLARED EXEMPT) ─────────────────────────────

    [Fact]
    [Trait("Category", "OverwatchSupply")]
    public async Task BelowCritical_TheOverwatcherIsNotConsulted_AndNothingIsSupplied()
    {
        using var fx = new Fixture(escalationThreshold: "high");
        fx.AddOperatorCommitsTask().AddNeedsResourceTask().AddDownstreamTask();

        await fx.RunAsync("--no-merge-on-success");

        fx.AssertControlSharedFacts();
        AssertNoResourceSupplyBrief(fx);
        AssertNoAutoResolveRecordAtAll(fx);
    }

    // ── C2 — another task owns the path ─────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "OverwatchSupply")]
    public async Task WhenAnotherTaskOwnsThePath_NoBriefIsSent_AndTheStopIsObserved()
    {
        using var fx = new Fixture(escalationThreshold: "critical");
        fx.AddOperatorCommitsTask().AddNeedsResourceTask().AddDownstreamTask().AddIndependentOwnerTask();

        await fx.RunAsync("--no-merge-on-success");

        fx.AssertControlSharedFacts();
        AssertNoResourceSupplyBrief(fx);

        DecisionEntry observed = Assert.Single(
            fx.Journal().Decisions ?? [], d => d.Subject == "02-needs-resource" && d.Decision == "observed");
        Assert.Contains("produced-by-another-task", observed.Headline + " " + observed.Detail);
    }

    // ── C3 — the proposal carries no fix ────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "OverwatchSupply")]
    public async Task WhenTheProposalCarriesNoFix_CertificationRefuses_NoResourceSupplyOp()
    {
        const string proposal = """{"classification":"retryable","diagnosis":"no fix applies to this halt","fixes":[]}""";
        using var fx = new Fixture(escalationThreshold: "critical", resourceSupplyProposal: proposal);
        fx.AddOperatorCommitsTask().AddNeedsResourceTask().AddDownstreamTask();

        await fx.RunAsync("--no-merge-on-success");

        fx.AssertControlSharedFacts();

        DecisionEntry advisory = Assert.Single(
            fx.Journal().Decisions ?? [], d => d.Subject == "02-needs-resource" && d.Decision == "advisory");
        Assert.Contains("no-resource-supply-op", advisory.Headline + " " + advisory.Detail);
    }

    // ── C4 — the file is uncommitted in the checkout ────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "OverwatchSupply")]
    public async Task WhenTheFileIsUncommittedInTheCheckout_NoBriefIsSent_NotCommittedInCheckout()
    {
        using var fx = new Fixture(escalationThreshold: "critical");
        fx.AddOperatorCommitsTask(commit: false).AddNeedsResourceTask().AddDownstreamTask();

        await fx.RunAsync("--no-merge-on-success");

        fx.AssertControlSharedFacts();
        AssertNoResourceSupplyBrief(fx);

        DecisionEntry observed = Assert.Single(
            fx.Journal().Decisions ?? [], d => d.Subject == "02-needs-resource" && d.Decision == "observed");
        Assert.Contains("not-committed-in-checkout", observed.Headline + " " + observed.Detail);
    }

    // ── C5 — the proposal names a non-candidate ─────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "OverwatchSupply")]
    public async Task WhenTheProposalNamesANonCandidate_CertificationRefuses_NotACandidate()
    {
        const string proposal =
            """{"classification":"retryable","diagnosis":"this file resolves the halt","fixes":[{"kind":"resource-supply","path":"vendor/other.js"}]}""";
        using var fx = new Fixture(escalationThreshold: "critical", resourceSupplyProposal: proposal);
        fx.AddOperatorCommitsTask().AddNeedsResourceTask().AddDownstreamTask();

        await fx.RunAsync("--no-merge-on-success");

        fx.AssertControlSharedFacts();

        DecisionEntry advisory = Assert.Single(
            fx.Journal().Decisions ?? [], d => d.Subject == "02-needs-resource" && d.Decision == "advisory");
        Assert.Contains("not-a-candidate", advisory.Headline + " " + advisory.Detail);
    }

    // ── C6 — the proposal is doomed ─────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "OverwatchSupply")]
    public async Task WhenTheProposalIsDoomed_CertificationRefuses_Doomed()
    {
        const string proposal =
            """{"classification":"doomed","diagnosis":"the task cannot proceed regardless","fixes":[{"kind":"resource-supply","path":"vendor/resource.js"}]}""";
        using var fx = new Fixture(escalationThreshold: "critical", resourceSupplyProposal: proposal);
        fx.AddOperatorCommitsTask().AddNeedsResourceTask().AddDownstreamTask();

        await fx.RunAsync("--no-merge-on-success");

        fx.AssertControlSharedFacts();

        DecisionEntry advisory = Assert.Single(
            fx.Journal().Decisions ?? [], d => d.Subject == "02-needs-resource" && d.Decision == "advisory");
        Assert.Contains("doomed", advisory.Headline + " " + advisory.Detail);
    }

    // ── C7 — a per-gate needs-human floor holds the dial below critical (DECLARED EXEMPT) ──────────

    [Fact]
    [Trait("Category", "OverwatchSupply")]
    public async Task WithAPerGateNeedsHumanFloor_TheDialIsNotEngaged_AndNoBriefIsSent()
    {
        using var fx = new Fixture(escalationThreshold: "critical", needsHumanGateThreshold: "high");
        fx.AddOperatorCommitsTask().AddNeedsResourceTask().AddDownstreamTask();

        await fx.RunAsync("--no-merge-on-success");

        fx.AssertControlSharedFacts();
        AssertNoResourceSupplyBrief(fx);
        AssertNoAutoResolveRecordAtAll(fx);
    }

    // ── N1 — never-weaker: the resource is already on the run base (DECLARED EXEMPT) ───────────────

    [Fact]
    [Trait("Category", "OverwatchSupply")]
    public async Task WhenTheResourceIsAlreadyOnTheRunBase_NothingIsSupplied_AndTheRunIsGreen()
    {
        using var fx = new Fixture(escalationThreshold: "critical");
        fx.PreSeedResourceOnMaster();
        fx.AddNoOpFirstTask().AddNeedsResourceTask().AddDownstreamTask();

        (int exit, _) = await fx.RunAsync("--no-merge-on-success");

        Assert.Equal(ExitCodes.Success, exit);

        JournalDocument journal = fx.Journal();
        Assert.Null(journal.Supplied);
        AssertNoResourceSupplyBrief(fx);

        TaskJournalEntry task02 = journal.Tasks["02-needs-resource"];
        Assert.Equal(Core.Journal.TaskStatus.Succeeded, task02.Status);
        Assert.DoesNotContain(task02.Attempts, a => a.Outcome == AttemptOutcome.NeedsHuman);
    }

    // ── shared control assertions ───────────────────────────────────────────────────────────────────

    private static void AssertNoResourceSupplyBrief(Fixture fx) =>
        Assert.DoesNotContain(fx.OverwatchBriefLines(), l => l.StartsWith(ResourceSupplyBriefPrefix, StringComparison.Ordinal));

    /// <summary>Tier 0 refused silently (design §2.1: "no record, because nothing was promised") — used by
    /// the two controls that hold the dial below critical (C1, C7), where not even an <c>observed</c> or
    /// <c>advisory</c> entry is written.</summary>
    private static void AssertNoAutoResolveRecordAtAll(Fixture fx) =>
        Assert.DoesNotContain(
            fx.Journal().Decisions ?? [],
            d => d.Subject == "02-needs-resource"
                 && (d.Decision == "auto-supplied" || d.Decision == "observed" || d.Decision == "advisory"));

    // ────────────────────────────────────────────────────────────────────────────────────────────────
    //  Fixture: the operator's checkout (a real git repo, the plan folder committed inside it on master)
    //  plus the two fake prompt-runner CLIs (SchedulerEscalationWiringTests' EscalationPlanBuilder
    //  two-CLI pattern). Everything the fake CLIs live under is a separate temp dir, never the worktree
    //  (#253) — nothing here rides the write-scope git diff.
    // ────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Fixture : IDisposable
    {
        public const string PlanName = "plan";
        public const string ResourcePath = "vendor/resource.js";
        public const string ResourceContent = "vendor-resource-content-v1";

        public const string DefaultResourceSupplyProposal =
            """{"classification":"retryable","diagnosis":"the checkout has the missing file committed at HEAD; supplying it resolves the halt","fixes":[{"kind":"resource-supply","path":"vendor/resource.js"}]}""";

        private const string CriticalAssessmentJson =
            """{"criticality":"critical","confidence":"high","bestGuess":"halt and ask a human","rationale":"an auto-resolve candidate needs a deterministic gate, not a best guess"}""";

        private const string DefaultDiagnoseJson =
            """{"classification":"retryable","diagnosis":"no fix applies","fixes":[]}""";

        private const string BlockedQuestion =
            "Cannot embed the runtime: vendor/resource.js is missing from this worktree; I will not stub or fetch it.";

        private static readonly bool Windows = OperatingSystem.IsWindows();

        private readonly TempGitRepo _repo;
        private readonly string _fakeCliRoot;

        public string PlanDir { get; }
        public string RepoPath => _repo.RepoPath;
        public TempGitRepo Repo => _repo;
        public string OverwatchLogPath { get; }

        public Fixture(
            string escalationThreshold = "critical",
            string? needsHumanGateThreshold = null,
            string resourceSupplyProposal = DefaultResourceSupplyProposal,
            decimal maxCostUsd = 50m)
        {
            _repo = new TempGitRepo();
            PlanDir = Path.Combine(_repo.RepoPath, PlanName);
            Directory.CreateDirectory(Path.Combine(PlanDir, "tasks"));

            _fakeCliRoot = Path.Combine(Path.GetTempPath(), "gr-overwatch-supply-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_fakeCliRoot);
            OverwatchLogPath = Path.Combine(_fakeCliRoot, "overwatch-briefs.log");

            string actionCli = WriteActionCli();
            string overwatchCli = WriteOverwatchCli(resourceSupplyProposal);

            File.WriteAllText(
                Path.Combine(PlanDir, "guardrails.json"),
                BuildGuardrailsJson(escalationThreshold, needsHumanGateThreshold, maxCostUsd, actionCli, overwatchCli));
        }

        // ── task fixtures (design §7) ───────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>01-operator-commits</c> (script): writes <see cref="ResourcePath"/> directly into the
        /// OPERATOR'S CHECKOUT (<see cref="RepoPath"/>, passed via <c>action.env</c> — never its own
        /// worktree) and, unless <paramref name="commit"/> is false, commits it onto <c>master</c>. The
        /// integration branch is always cut before this task is dispatched, so this reproduces the
        /// measured lineage gap: master moves ahead of the run's own base.
        /// </summary>
        public Fixture AddOperatorCommitsTask(bool commit = true)
        {
            string taskDir = Path.Combine(PlanDir, "tasks", "01-operator-commits");
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

            string commitToken = commit ? "true" : "false";
            string taskJson =
                "{\n" +
                "  \"description\": \"commits vendor/resource.js onto master in the operator's checkout, after the integration branch is cut\",\n" +
                "  \"writeScope\": [],\n" +
                "  \"dependsOn\": [],\n" +
                "  \"action\": { \"env\": { \"FAKE_CHECKOUT\": \"" + EscapeJson(RepoPath) + "\", \"FAKE_COMMIT\": \"" + commitToken + "\" } }\n" +
                "}\n";
            File.WriteAllText(Path.Combine(taskDir, "task.json"), taskJson);

            if (Windows)
            {
                File.WriteAllText(
                    Path.Combine(taskDir, "action.ps1"),
                    "$vendorDir = Join-Path $env:FAKE_CHECKOUT 'vendor'\r\n" +
                    "New-Item -ItemType Directory -Force -Path $vendorDir | Out-Null\r\n" +
                    "Set-Content -NoNewline -Path (Join-Path $vendorDir 'resource.js') -Value '" + ResourceContent + "'\r\n" +
                    "if ($env:FAKE_COMMIT -eq 'true') {\r\n" +
                    "    git -C $env:FAKE_CHECKOUT add vendor/resource.js\r\n" +
                    "    git -C $env:FAKE_CHECKOUT commit -m 'vendor: add resource.js' | Out-Null\r\n" +
                    "}\r\n" +
                    "exit 0\r\n");
                File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), "exit 0\r\n");
            }
            else
            {
                WriteExecutable(
                    Path.Combine(taskDir, "action.sh"),
                    "#!/usr/bin/env bash\n" +
                    "mkdir -p \"$FAKE_CHECKOUT/vendor\"\n" +
                    "printf '%s' '" + ResourceContent + "' > \"$FAKE_CHECKOUT/vendor/resource.js\"\n" +
                    "if [ \"$FAKE_COMMIT\" = \"true\" ]; then\n" +
                    "  git -C \"$FAKE_CHECKOUT\" add vendor/resource.js\n" +
                    "  git -C \"$FAKE_CHECKOUT\" commit -m 'vendor: add resource.js' > /dev/null\n" +
                    "fi\n" +
                    "exit 0\n");
                WriteExecutable(Path.Combine(taskDir, "guardrails", "01-check.sh"), "#!/usr/bin/env bash\nexit 0\n");
            }

            return this;
        }

        /// <summary>N1's <c>01-operator-commits</c>: a genuine no-op, used only when the resource is
        /// already committed on the run's base before the run starts.</summary>
        public Fixture AddNoOpFirstTask()
        {
            string taskDir = Path.Combine(PlanDir, "tasks", "01-operator-commits");
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
            File.WriteAllText(
                Path.Combine(taskDir, "task.json"),
                """{ "description": "no-op: the resource is already committed on the run base", "writeScope": [], "dependsOn": [] }""");

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

            return this;
        }

        /// <summary>
        /// <c>02-needs-resource</c> (prompt, depends on 01, <c>writeScope: ["02-done.txt"]</c> — the shape
        /// of a task that EMBEDS a resource rather than owning it). The fake action CLI checks its own
        /// worktree for <see cref="ResourcePath"/>: absent ⇒ the exact blocked-work halt design §7 pins;
        /// present ⇒ writes <c>02-done.txt</c> and a fragment. Its guardrail passes only when both exist
        /// and the resource has the fixture content.
        /// </summary>
        public Fixture AddNeedsResourceTask()
        {
            string taskDir = Path.Combine(PlanDir, "tasks", "02-needs-resource");
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
            File.WriteAllText(
                Path.Combine(taskDir, "task.json"),
                """
                {
                  "description": "embeds vendor/resource.js when present in its own worktree; otherwise halts needs-human naming the missing path",
                  "writeScope": ["02-done.txt"],
                  "dependsOn": ["01-operator-commits"],
                  "action": { "path": "action.prompt.md" }
                }
                """);
            File.WriteAllText(
                Path.Combine(taskDir, "action.prompt.md"), "Embed vendor/resource.js, or ask a human if it is missing.\n");

            if (Windows)
            {
                File.WriteAllText(
                    Path.Combine(taskDir, "guardrails", "01-check.ps1"),
                    "$vendor = Join-Path $env:GUARDRAILS_WORKSPACE 'vendor/resource.js'\r\n" +
                    "$done = Join-Path $env:GUARDRAILS_WORKSPACE '02-done.txt'\r\n" +
                    "if ((Test-Path $vendor) -and ((Get-Content -Raw -Path $vendor) -eq '" + ResourceContent + "') -and (Test-Path $done)) { exit 0 } else { exit 1 }\r\n");
            }
            else
            {
                WriteExecutable(
                    Path.Combine(taskDir, "guardrails", "01-check.sh"),
                    "#!/usr/bin/env bash\n" +
                    "vendor=\"$GUARDRAILS_WORKSPACE/vendor/resource.js\"\n" +
                    "done_marker=\"$GUARDRAILS_WORKSPACE/02-done.txt\"\n" +
                    "if [ -f \"$vendor\" ] && [ \"$(cat \"$vendor\")\" = \"" + ResourceContent + "\" ] && [ -f \"$done_marker\" ]; then exit 0; else exit 1; fi\n");
            }

            return this;
        }

        /// <summary><c>03-downstream</c> (script, depends on 02). Its guardrail passes only if
        /// <see cref="ResourcePath"/> is present in its OWN worktree.</summary>
        public Fixture AddDownstreamTask()
        {
            string taskDir = Path.Combine(PlanDir, "tasks", "03-downstream");
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
            File.WriteAllText(
                Path.Combine(taskDir, "task.json"),
                """{ "description": "checks vendor/resource.js is present in its own worktree", "writeScope": [], "dependsOn": ["02-needs-resource"] }""");

            if (Windows)
            {
                File.WriteAllText(Path.Combine(taskDir, "action.ps1"), "exit 0\r\n");
                File.WriteAllText(
                    Path.Combine(taskDir, "guardrails", "01-check.ps1"),
                    "$vendor = Join-Path $env:GUARDRAILS_WORKSPACE 'vendor/resource.js'\r\n" +
                    "if ((Test-Path $vendor) -and ((Get-Content -Raw -Path $vendor) -eq '" + ResourceContent + "')) { exit 0 } else { exit 1 }\r\n");
            }
            else
            {
                WriteExecutable(Path.Combine(taskDir, "action.sh"), "#!/usr/bin/env bash\nexit 0\n");
                WriteExecutable(
                    Path.Combine(taskDir, "guardrails", "01-check.sh"),
                    "#!/usr/bin/env bash\n" +
                    "vendor=\"$GUARDRAILS_WORKSPACE/vendor/resource.js\"\n" +
                    "if [ -f \"$vendor\" ] && [ \"$(cat \"$vendor\")\" = \"" + ResourceContent + "\" ]; then exit 0; else exit 1; fi\n");
            }

            return this;
        }

        /// <summary>C2's independent <c>04-owns-vendor</c>: declares <c>writeScope: ["vendor/**"]</c> so
        /// the candidate-scope check (§2.2 fact 4) refuses the path to the auto-resolve. Trivial action and
        /// guardrail — it never actually needs to write anything; only the DECLARATION matters.</summary>
        public Fixture AddIndependentOwnerTask()
        {
            string taskDir = Path.Combine(PlanDir, "tasks", "04-owns-vendor");
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
            File.WriteAllText(
                Path.Combine(taskDir, "task.json"),
                """{ "description": "declares ownership of vendor/** so the candidate-scope check refuses it", "writeScope": ["vendor/**"], "dependsOn": [] }""");

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

            // Nothing depends on this task, and nothing depends on 01-operator-commits either, so adding
            // it gives the plan TWO leaves (03-downstream and 04-owns-vendor) instead of one chain. That
            // is a parallel topology, and GR2028 then REQUIRES the terminal '<plan>/guardrails/' folder to
            // carry a real integration re-run. Without one the plan fails VALIDATION, the run exits before
            // RunJournal.LoadOrCreateForRun ever writes state/run.json, and this test dies reading a
            // journal that was never created — a failure that reads as "the wiring is broken".
            WriteTerminalUnionGate();

            return this;
        }

        /// <summary>
        /// The GR2028 terminal gate, needed only once a test gives the plan a parallel topology (today
        /// only <see cref="AddIndependentOwnerTask"/> does). A conflict-marker scan is the accepted
        /// union-invariant form for a plan with no toolchain to invoke, and is the canonical shape used by
        /// this repo's own union-safe guardrails. It deliberately is NOT a tautological <c>exit 0</c> —
        /// that is the precise thing GR2028 rejects.
        /// </summary>
        private void WriteTerminalUnionGate()
        {
            string gateDir = Path.Combine(PlanDir, "guardrails");
            Directory.CreateDirectory(gateDir);

            if (Windows)
            {
                File.WriteAllText(
                    Path.Combine(gateDir, "01-union-conflict-marker-free.ps1"),
                    """
                    # catches: a merge that left git conflict markers in the union, or dropped a
                    #          contribution entirely - the terminal union-soundness boundary GR2028
                    #          requires once this plan has more than one leaf.
                    $failures = @()
                    foreach ($f in @('02-done.txt', 'vendor/resource.js')) {
                        if (-not (Test-Path $f)) { continue }
                        $content = Get-Content -Raw -Path $f
                        if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
                            $failures += "$f contains git conflict markers - the union did not cleanly integrate"
                        }
                    }
                    if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Output $_ }; exit 1 }
                    exit 0
                    """);
            }
            else
            {
                WriteExecutable(
                    Path.Combine(gateDir, "01-union-conflict-marker-free.sh"),
                    """
                    #!/usr/bin/env bash
                    # catches: a merge that left git conflict markers in the union, or dropped a
                    #          contribution entirely - the terminal union-soundness boundary GR2028
                    #          requires once this plan has more than one leaf.
                    rc=0
                    for f in 02-done.txt vendor/resource.js; do
                      [ -f "$f" ] || continue
                      if grep -qE '^<<<<<<<' "$f" || grep -qE '^>>>>>>>' "$f"; then
                        echo "$f contains git conflict markers - the union did not cleanly integrate"
                        rc=1
                      fi
                    done
                    exit $rc
                    """);
            }
        }

        /// <summary>N1: the resource is committed onto master BEFORE the run — so the integration branch,
        /// cut at run start, already carries it.</summary>
        public Fixture PreSeedResourceOnMaster()
        {
            string vendorDir = Path.Combine(RepoPath, "vendor");
            Directory.CreateDirectory(vendorDir);
            File.WriteAllText(Path.Combine(vendorDir, "resource.js"), ResourceContent);
            _repo.AddAndCommit("vendor/resource.js", "vendor: pre-seed resource.js");
            return this;
        }

        // ── driving the real composition root ───────────────────────────────────────────────────────

        /// <summary>Commits the assembled plan folder onto master (design §7: "the plan folder is
        /// committed inside that repo, on master"), then drives <c>CommandFactory.BuildRootCommand</c>'s
        /// <c>run</c> in process — the #120 rule: the real factory, never an injected seam.</summary>
        public async Task<(int ExitCode, string Output)> RunAsync(params string[] extraArgs)
        {
            _repo.AddAndCommit("plan", "add plan folder");

            var io = new StringConsoleIo();
            var root = CommandFactory.BuildRootCommand(io);
            string[] args = ["run", PlanDir, "--no-ui", "--no-log-server", .. extraArgs];
            int exit = await root.Parse(args).InvokeAsync();
            return (exit, io.OutText);
        }

        public JournalDocument Journal() => JournalReader.Read(RunJournal.PathFor(PlanDir));

        public string RunId() => Journal().RunId;

        public List<JsonObject> ReadEvents() =>
            ReadJsonLines(Path.Combine(PlanDir, "logs", RunId(), "events.jsonl"));

        public List<JsonObject> ReadOverwatchJsonl(string taskId) =>
            ReadJsonLines(Path.Combine(PlanDir, "logs", RunId(), taskId, "overwatch.jsonl"));

        public IReadOnlyList<string> OverwatchBriefLines() =>
            File.Exists(OverwatchLogPath) ? File.ReadAllLines(OverwatchLogPath) : [];

        /// <summary>The three facts every control (C1–C7) shares (design §7): no supply happened, no
        /// Supplied-By: commit landed on the plan branch, and task 02 ended needs-human.</summary>
        public void AssertControlSharedFacts()
        {
            JournalDocument journal = Journal();
            Assert.Null(journal.Supplied);
            Assert.DoesNotContain("Supplied-By:", Repo.LogBody(PlanBranch));
            Assert.Equal(Core.Journal.TaskStatus.NeedsHuman, journal.Tasks["02-needs-resource"].Status);
        }

        private static List<JsonObject> ReadJsonLines(string path) =>
            File.Exists(path)
                ? [.. File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => (JsonObject)JsonNode.Parse(l)!)]
                : [];

        public void Dispose()
        {
            _repo.Dispose();
            SafeDelete.DeleteDirectory(_fakeCliRoot);
        }

        // ── guardrails.json ──────────────────────────────────────────────────────────────────────────

        private static string BuildGuardrailsJson(
            string escalationThreshold, string? needsHumanGateThreshold, decimal maxCostUsd,
            string actionCmd, string overwatchCmd)
        {
            string gateThresholds = needsHumanGateThreshold is null
                ? ""
                : $",\n    \"gateThresholds\": {{ \"needs-human\": \"{needsHumanGateThreshold}\" }}";

            string actionCmdEscaped = actionCmd.Replace("\\", "\\\\");
            string overwatchCmdEscaped = overwatchCmd.Replace("\\", "\\\\");

            return $$"""
                {
                  "version": 1,
                  "guardrailMode": "failFast",
                  "workspace": "..",
                  "defaultRetries": 0,
                  "maxParallelism": 2,
                  "defaultTimeoutSeconds": 60,
                  "maxCostUsd": {{maxCostUsd}},
                  "autonomyPolicy": "auto",
                  "autonomy": {
                    "escalationThreshold": "{{escalationThreshold}}"{{gateThresholds}}
                  },
                  "promptRunners": {
                    "default": "claude",
                    "claude": {
                      "command": "{{actionCmdEscaped}}",
                      "permissionMode": "acceptEdits",
                      "allowedTools": ["Read", "Write"],
                      "maxTurns": 5
                    },
                    "overwatch": {
                      "command": "{{overwatchCmdEscaped}}",
                      "permissionMode": "default",
                      "allowedTools": ["Read"],
                      "maxTurns": 5
                    }
                  }
                }
                """;
        }

        // ── the fake `claude` (default/action) CLI ──────────────────────────────────────────────────

        private string WriteActionCli()
        {
            string psBody =
                "$null = [Console]::In.ReadToEnd()\r\n" +
                "$target = Join-Path $env:GUARDRAILS_WORKSPACE 'vendor/resource.js'\r\n" +
                "if (Test-Path $target) {\r\n" +
                "    Set-Content -NoNewline -Path (Join-Path $env:GUARDRAILS_WORKSPACE '02-done.txt') -Value 'done'\r\n" +
                "    if ($env:GUARDRAILS_STATE_OUT) {\r\n" +
                "        Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value ('{\"' + $env:GUARDRAILS_TASK_ID + '\": {\"embedded\": true}}')\r\n" +
                "    }\r\n" +
                "    Write-Output '{\"type\":\"result\",\"is_error\":false,\"result\":\"embedded the runtime\",\"num_turns\":1}'\r\n" +
                "} else {\r\n" +
                "    if ($env:GUARDRAILS_STATE_OUT) {\r\n" +
                "        Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value '{\"needsHuman\": {\"question\": \"" +
                BlockedQuestion +
                "\", \"kind\": \"blocked-work\"}}'\r\n" +
                "    }\r\n" +
                "    Write-Output '{\"type\":\"result\",\"is_error\":false,\"result\":\"asked a human\",\"num_turns\":1}'\r\n" +
                "}\r\n";

            string bashBody =
                "#!/usr/bin/env bash\n" +
                "cat > /dev/null\n" +
                "target=\"$GUARDRAILS_WORKSPACE/vendor/resource.js\"\n" +
                "if [ -f \"$target\" ]; then\n" +
                "  printf '%s' 'done' > \"$GUARDRAILS_WORKSPACE/02-done.txt\"\n" +
                "  if [ -n \"$GUARDRAILS_STATE_OUT\" ]; then\n" +
                "    printf '{\"%s\": {\"embedded\": true}}' \"$GUARDRAILS_TASK_ID\" > \"$GUARDRAILS_STATE_OUT\"\n" +
                "  fi\n" +
                "  printf '{\"type\":\"result\",\"is_error\":false,\"result\":\"embedded the runtime\",\"num_turns\":1}\\n'\n" +
                "else\n" +
                "  if [ -n \"$GUARDRAILS_STATE_OUT\" ]; then\n" +
                "    printf '%s' '{\"needsHuman\": {\"question\": \"" + BlockedQuestion + "\", \"kind\": \"blocked-work\"}}' > \"$GUARDRAILS_STATE_OUT\"\n" +
                "  fi\n" +
                "  printf '{\"type\":\"result\",\"is_error\":false,\"result\":\"asked a human\",\"num_turns\":1}\\n'\n" +
                "fi\n";

            return WriteFakeCli("action", psBody, bashBody);
        }

        // ── the fake `overwatch` CLI — routes on stdin's FIRST LINE (design §2.3/§7) ────────────────

        private string WriteOverwatchCli(string resourceSupplyProposal)
        {
            string resourceSupplyLine = BuildStreamLine(resourceSupplyProposal);
            string criticalityLine = BuildStreamLine(CriticalAssessmentJson);
            string defaultLine = BuildStreamLine(DefaultDiagnoseJson);

            string psBody =
                "$stdinText = [Console]::In.ReadToEnd()\r\n" +
                "$firstLine = ($stdinText -split \"\\r?\\n\")[0]\r\n" +
                "Add-Content -Path '" + OverwatchLogPath + "' -Value $firstLine\r\n" +
                "if ($firstLine.StartsWith('# Overwatch resource supply:')) {\r\n" +
                "    Write-Output '" + resourceSupplyLine + "'\r\n" +
                "} elseif ($firstLine.StartsWith('# Criticality assessment:')) {\r\n" +
                "    Write-Output '" + criticalityLine + "'\r\n" +
                "} else {\r\n" +
                "    Write-Output '" + defaultLine + "'\r\n" +
                "}\r\n";

            string bashBody =
                "#!/usr/bin/env bash\n" +
                "input=\"$(cat)\"\n" +
                "first_line=\"$(printf '%s\\n' \"$input\" | head -n 1)\"\n" +
                "printf '%s\\n' \"$first_line\" >> '" + OverwatchLogPath + "'\n" +
                "case \"$first_line\" in\n" +
                "  \"# Overwatch resource supply:\"*)\n" +
                "    printf '%s\\n' '" + resourceSupplyLine + "' ;;\n" +
                "  \"# Criticality assessment:\"*)\n" +
                "    printf '%s\\n' '" + criticalityLine + "' ;;\n" +
                "  *)\n" +
                "    printf '%s\\n' '" + defaultLine + "' ;;\n" +
                "esac\n";

            return WriteFakeCli("overwatch", psBody, bashBody);
        }

        /// <summary>Wraps a raw verdict JSON string in the stream-json <c>result</c> line
        /// <c>ClaudePromptRunner</c> parses (mirrors <c>SchedulerEscalationWiringTests.EscalationPlanBuilder</c>'s
        /// <c>OverwatchPsBody</c>/<c>OverwatchBashBody</c> wrapper).</summary>
        private static string BuildStreamLine(string resultJson) => JsonSerializer.Serialize(new
        {
            type = "result",
            is_error = false,
            result = resultJson,
            total_cost_usd = 0,
            num_turns = 1
        });

        // ── shared fake-CLI plumbing (mirrors EscalationPlanBuilder.WriteFakeCli/WriteExecutable) ────

        private string WriteFakeCli(string name, string psBody, string bashBody)
        {
            if (Windows)
            {
                string cmdPath = Path.Combine(_fakeCliRoot, name + ".cmd");
                string ps1Path = Path.Combine(_fakeCliRoot, name + ".ps1");
                File.WriteAllText(ps1Path, psBody);
                File.WriteAllText(cmdPath, $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1Path}\"\r\n");
                return cmdPath;
            }

            string shPath = Path.Combine(_fakeCliRoot, name + ".sh");
            WriteExecutable(shPath, bashBody);
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

        private static string EscapeJson(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    // ── a real git repository, not a fake of one ───────────────────────────────────────────────────
    // Copied per the task's own instruction (there is no shared TempGitRepo helper; it is a private
    // nested class duplicated per test file — 45 copies measured across tests/). Nearest sibling:
    // SuppliedBoundaryWiringTests.cs — kept: hooks isolated to an empty dir inside .git, autocrlf and
    // gpgsign off, Windows-safe deletion via SafeDelete (git marks loose objects read-only), and rollback
    // via `git reset --hard` (never `git merge --abort`, rc=128 on a dirtied tracked path).

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _rootDir;
        public string RepoPath { get; }

        public TempGitRepo()
        {
            _rootDir = Path.Combine(Path.GetTempPath(), "gr-overwatch-supply-repo-" + Guid.NewGuid().ToString("N"));
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

            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# overwatch-supply-autoresolve wiring fixture");
            Git("add", "README.md");
            Git("commit", "-m", "Initial commit");
        }

        /// <summary>The full body (trailers included) of every commit reachable from <paramref name="branch"/>.</summary>
        public string LogBody(string branch) => Git("log", branch, "--format=%B");

        /// <summary><c>git cat-file -s &lt;sha&gt;:&lt;path&gt;</c> — the blob's own byte size.</summary>
        public long BlobSize(string sha, string path) => long.Parse(Git("cat-file", "-s", $"{sha}:{path}").Trim());

        public string RevParse(string @ref) => Git("rev-parse", @ref).Trim();

        /// <summary><c>git merge-base --is-ancestor</c> — a real true/false, never a thrown exception on the false case.</summary>
        public bool IsAncestor(string ancestorSha, string @ref) => GitTry("merge-base", "--is-ancestor", ancestorSha, @ref).ExitCode == 0;

        public string StatusPorcelainNoUntracked() => Git("status", "--porcelain", "--untracked-files=no");

        public void AddAndCommit(string relPath, string message)
        {
            Git("add", relPath);
            Git("commit", "-m", message);
        }

        public string Git(params string[] arguments)
        {
            (int exitCode, string stdout, string stderr) = GitTry(arguments);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(' ', arguments)} (in {RepoPath}) exited {exitCode}: {stderr.Trim()}");
            }

            return stdout;
        }

        private (int ExitCode, string Stdout, string Stderr) GitTry(params string[] arguments)
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
            return (process.ExitCode, stdout, stderr);
        }

        public void Dispose() => SafeDelete.DeleteDirectory(_rootDir);
    }
}
