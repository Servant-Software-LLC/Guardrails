using System.Diagnostics;
using System.Text.Json.Nodes;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #554, plan 31 §3 — the escalation path must PRESERVE. Retry salvage (#195/#306) is shipped and
/// works, but <c>TaskExecutor.cs:838-843</c> short-circuits to <c>_journaler.NeedsHuman(...)</c> before
/// any salvage call, so a task whose action emits <c>needsHuman</c> AFTER writing files leaves no patch,
/// no ref and no salvage text. Measured on plan 28's unattended night: $22.02 of work discarded across
/// six tasks.
///
/// <para><b>"Discarded" is not what happens, and the truth is worse (§3.2).</b> The attempt loop returns
/// terminally on <c>NeedsHuman</c> BEFORE the F2 reset, so the escalating attempt's tree is not reset —
/// it is ORPHANED. A resume mints a new runId and a fresh segment at <c>planHead</c>; <c>reuse</c> and
/// <c>fork</c> are intra-run policies; <c>reclaim</c> only deletes after the staleness threshold. The
/// tree is never handed back to anybody. The ref and the patch are the ONLY durable artifacts a resumed
/// agent, a triaging human or a firstmate can be pointed at.</para>
///
/// <para><b>Why these pins live in the INTEGRATION suite (plan §7).</b> The salvage path is worktree-only:
/// it is gated on <c>IsRealGitSegment</c>, which is FALSE for the fake worktree provider (its
/// <c>TaskBase</c> is the all-zeros placeholder). A Core test written against the fake would pass with
/// the feature entirely absent — the #382 archetype. So every fixture here builds a REAL temp git repo
/// and drives a REAL segment through the REAL <see cref="SchedulerFactory.Create"/> composition root.</para>
///
/// <para><b>Naming no new API member (plan §7).</b> #554 needs no stub stage: every assertion below is on
/// an observable artifact — a file on disk, a git ref, a git tree listing, or an emitted string — so
/// these tests compile against today's assemblies and fail because the FEATURE is absent.</para>
///
/// <para><b>Windows-git portability (#116).</b> Mirrors <see cref="RetrySalvageTests"/>: the fixture repo
/// sets <c>core.autocrlf=false</c> so content hashes are deterministic across platforms; every fixture
/// write creates its parent directory first (Git-for-Windows prunes a directory it has just emptied, and
/// the next write then throws <see cref="DirectoryNotFoundException"/>); and teardown strips the
/// read-only attribute git leaves on loose objects under <c>.git/objects</c> before
/// <see cref="Directory.Delete(string, bool)"/>, catching <see cref="UnauthorizedAccessException"/> as
/// well as <see cref="IOException"/>. No fixture here merges, so no rollback verb is needed at all —
/// which is the safe side of the fourth trap (<c>git merge --abort</c> fails rc=128 on a dirtied tracked
/// path; <c>git reset --hard &lt;preHead&gt;</c> is the form to reach for if one is ever added).</para>
///
/// <para><b>Issue #253 containment.</b> These fixtures deliberately provoke <c>needs-human</c>, which is
/// exactly what fires <c>NeedsHumanTriage</c> — an invocation the harness builds with a deliberately
/// EMPTY environment, so the fake CLI would otherwise inherit an ENCLOSING <c>guardrails run</c>'s
/// <c>GUARDRAILS_WORKSPACE</c> and write into that run's worktree. The fake therefore writes nothing at
/// all unless it can positively prove it is THIS fixture's task action. It uses the same two literal
/// filenames <see cref="RetrySalvageTests"/> does, so the shared <see cref="HostRepoCleanlinessGuard"/>
/// tripwire (reused here as an <see cref="IClassFixture{T}"/>) covers this class with full teeth.</para>
/// </summary>
public sealed class EscalationSalvageTests : IClassFixture<HostRepoCleanlinessGuard>, IDisposable
{
    private static readonly bool Windows = OperatingSystem.IsWindows();

    private const string TaskId = "01-implement";

    /// <summary>The DERIVED salvage ref (§3.3) for the first escalating attempt of <see cref="TaskId"/>.</summary>
    private const string RefAttempt1 = "refs/guardrails/01-implement/attempt-1";

    /// <summary>The <c>fnmatch</c> pattern <c>git for-each-ref</c> is given to count one task's salvage refs.</summary>
    private const string RefPattern = "refs/guardrails/01-implement/attempt-*";

    private const string PatchFileName = "prior-attempt.patch";

    /// <summary>IN scope for the fixture task (<c>writeScope: ["src/"]</c>).</summary>
    private const string InScopeRelPath = "src/output.txt";

    /// <summary>OUT of scope — the write §3.4 divergence 3 says must never reach a durable, agent-readable patch.</summary>
    private const string OutOfScopeRelPath = "outside.txt";

    /// <summary>The escalating agent's question. Deliberately free of every word the I9 pins look for.</summary>
    private const string Question = "which storage engine should the widget target";

    /// <summary>
    /// Name of the fixture-private proof-of-origin variable the plan injects into the TASK ACTION's
    /// environment (SSOT §3 <c>action.env</c>). Deliberately OUTSIDE the <c>GUARDRAILS_</c> namespace —
    /// it is fixture plumbing, and it must be a name no real harness (inner or outer) ever sets (#253).
    /// </summary>
    private const string ActionTokenVar = "GR_ESCALATION_SALVAGE_FIXTURE_ACTION";

    /// <summary>
    /// Repeat escalations to drive at I8. The retention cap is a stage-2 constant this suite deliberately
    /// does NOT name; the plan constrains it to "≥ the default retry budget", and
    /// <c>RunConfig.DefaultRetries</c> defaults to 2 — so ten repeat escalations of one task sit far above
    /// any retention a reader would call a cap, and the count observed after them is the cap's behaviour
    /// rather than a number this test asserted into existence.
    /// </summary>
    private const int RepeatEscalationCount = 10;

    /// <summary>The advisory assessment the reserved <c>overwatch</c> runner returns: CRITICAL ≥ the
    /// <c>high</c> dial, so the needs-human gate ESCALATES (writing the record I5 reads) rather than
    /// proceeding on a best guess.</summary>
    private const string AssessCritical =
        "{\"criticality\":\"critical\",\"confidence\":\"high\",\"bestGuess\":\"halt and ask a human\"," +
        "\"rationale\":\"an irreversible storage choice\"}";

    /// <summary>What the escalating attempt writes before it asks.</summary>
    private enum FakeMode
    {
        /// <summary>Writes the IN-scope file, then emits <c>needsHuman</c>.</summary>
        WriteInScopeThenAsk,

        /// <summary>Writes BOTH the in-scope and the out-of-scope file, then emits <c>needsHuman</c>.</summary>
        WriteBothThenAsk,

        /// <summary>Writes ONLY the out-of-scope file, then emits <c>needsHuman</c> — nothing in scope.</summary>
        WriteOutOfScopeOnlyThenAsk
    }

    /// <summary>Roots created by this class's fixtures, torn down together.</summary>
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            SafeDeleteTree(root);
        }
    }

    // ── I1/I2 — the pin from #554, verbatim ───────────────────────────────────────────────────────

    [Fact]
    public async Task NeedsHumanAfterWritingFiles_LeavesANonEmptyPriorAttemptPatch()
    {
        // "A task whose action emits needsHuman AFTER writing files must leave a non-empty
        // prior-attempt.patch in that attempt's log dir" (§3.5, quoting #554). Today attempts 5 and 7 of
        // plan 28's task 28 — the two needs-human ones — have no patch while every neighbouring
        // (retry-path) attempt carries 34KB–76KB of one. That asymmetry is the whole defect.
        Fixture fixture = NewFixture(FakeMode.WriteInScopeThenAsk);
        AssertEscalatedAfterWriting(await RunAsync(fixture));
        AssertRanInARealGitSegment(AttemptDir(fixture, attempt: 1));

        string patchPath = Path.Combine(AttemptDir(fixture, attempt: 1), PatchFileName);
        Assert.True(File.Exists(patchPath),
            $"no {PatchFileName} in the escalating attempt's log dir — the needsHuman short-circuit " +
            "(TaskExecutor.cs:838-843) returned before any salvage call, so the work this attempt did is " +
            $"unreachable by construction (§3.2). Looked in: {AttemptDir(fixture, attempt: 1)}");

        string patch = File.ReadAllText(patchPath);
        Assert.False(string.IsNullOrWhiteSpace(patch), "the salvage patch is empty");
        Assert.Contains(InScopeRelPath, patch);
    }

    [Fact]
    public async Task NeedsHumanAfterWritingFiles_LeavesASalvageRefForTheAttempt()
    {
        // …"and a salvage ref at refs/guardrails/<taskId>/attempt-<N>". The ref is the second of the two
        // durable artifacts, and the one the composed prompt's `git show` route reads from. Asserted on
        // the MAIN repo, because refs are shared across worktrees — which is what makes the ref survive
        // the segment worktree the escalating attempt ran in.
        Fixture fixture = NewFixture(FakeMode.WriteInScopeThenAsk);
        AssertEscalatedAfterWriting(await RunAsync(fixture));
        AssertRanInARealGitSegment(AttemptDir(fixture, attempt: 1));

        Assert.True(RefExists(fixture.RepoPath, RefAttempt1),
            $"expected the salvage ref {RefAttempt1} for the escalating attempt");
        Assert.Equal("attempt-output", RunGit(fixture.RepoPath, "show", $"{RefAttempt1}:{InScopeRelPath}").Trim());
    }

    // ── I3 — §3.4 divergence 3: the staged set is filtered to writeScope ───────────────────────────

    [Fact]
    public async Task NeedsHumanWithAnOutOfScopeWrite_ThatWriteIsAbsentFromThePatchAndTheRefTree()
    {
        // §3.4 divergence 3 — a CORRECTNESS fix, not a wording one. The retry path reaches
        // TryStashFailedAttempt only AFTER the write-scope check and ScopedRevert have run, so its tree is
        // already scope-clean before anything is staged. The escalation short-circuit is ~250 lines
        // UPSTREAM of both, and PreserveAttemptToRef stages `add -A` with no writeScope filter at all —
        // so stashing unfiltered would write this attempt's OUT-OF-SCOPE edits into a durable,
        // agent-readable patch that the next attempt is explicitly invited to adopt.
        //
        // Note what does NOT protect this path: the retry path's protected-artifact suppression keys off
        // the FAILED guardrail list, and on the escalation path no guardrail ran, so that list is empty
        // and the suppression is structurally inapplicable (§3.4). The scope filter is what takes its
        // place, which is why it is a deliverable rather than a nicety.
        Fixture fixture = NewFixture(FakeMode.WriteBothThenAsk);
        AssertEscalatedAfterWriting(await RunAsync(fixture));
        AssertRanInARealGitSegment(AttemptDir(fixture, attempt: 1));

        string patchPath = Path.Combine(AttemptDir(fixture, attempt: 1), PatchFileName);
        Assert.True(File.Exists(patchPath), $"no {PatchFileName} was written for the escalating attempt");

        // (a) the patch BYTES — what an agent is told to read and adopt.
        string patch = File.ReadAllText(patchPath);
        Assert.Contains(InScopeRelPath, patch);
        Assert.DoesNotContain(OutOfScopeRelPath, patch);

        // (b) the ref's TREE agrees — the `git show <ref>:<path>` route must not be a back door into the
        //     very edit the patch was filtered to exclude.
        Assert.True(RefExists(fixture.RepoPath, RefAttempt1), $"expected the salvage ref {RefAttempt1}");
        string tree = RunGit(fixture.RepoPath, "ls-tree", "-r", "--name-only", RefAttempt1);
        Assert.Contains(InScopeRelPath, tree);
        Assert.DoesNotContain(OutOfScopeRelPath, tree);
    }

    // ── I4 — §3.4 divergence 1: the guard is IsRealGitSegment, NOT WorktreeWillReset ───────────────

    [Fact]
    public async Task NeedsHumanOnTheFinalAttempt_StillPreserves()
    {
        // The pin that catches an implementation which copied StashIfRollingBack verbatim.
        // StashIfRollingBack asks "will this attempt be RESET?" — and on a FINAL attempt WorktreeWillReset
        // is false, so a verbatim copy preserves nothing here. But a final escalating attempt is precisely
        // the one whose work a human is about to build on: there is no next attempt to hand it to, only a
        // person. §3.4 divergence 1: the escalation path preserves whenever there is a real git segment,
        // REGARDLESS of isFinal.
        //
        // The fixture pins finality structurally: `defaultRetries: 0` makes the budget one attempt, so
        // attempt 1 IS the final attempt (TaskExecutor's `isFinal = attemptIndex == budget`).
        Fixture fixture = NewFixture(FakeMode.WriteInScopeThenAsk, defaultRetries: 0);
        AssertEscalatedAfterWriting(await RunAsync(fixture));
        AssertRanInARealGitSegment(AttemptDir(fixture, attempt: 1));

        Assert.True(File.Exists(Path.Combine(AttemptDir(fixture, attempt: 1), PatchFileName)),
            $"a needsHuman on the FINAL attempt left no {PatchFileName}. WorktreeWillReset is false here — " +
            "if the escalation path was guarded on it (rather than on IsRealGitSegment) this is exactly " +
            "how it fails, and it fails on the case a human most needs (§3.4 divergence 1).");
        Assert.True(RefExists(fixture.RepoPath, RefAttempt1),
            $"a needsHuman on the FINAL attempt left no salvage ref {RefAttempt1}");
    }

    // ── I5 — what a human / firstmate actually reads at the halt ──────────────────────────────────

    [Fact]
    public async Task NeedsHumanEscalation_ContextNamesTheRefAndThePatch()
    {
        // Scheduler.BuildGateContext composes "the full reconstruction context a human/firstmate reads to
        // answer the escalation" (doc 12 §7.1) — today: the gate, the subject, the question and the logs
        // root, and nothing about what is already BUILT. Plan 28's attempt-7 escalation enumerated its
        // completed content work in detail; none of it was reachable, and the record pointed at none of
        // it. The operator deciding how to unblock is told what is wrong and nothing about what exists.
        //
        // Driven through the REAL factory with an `autonomy` block under `autonomyPolicy: auto`, because
        // that is the only wiring that constructs the FileEscalationSink — the escalation record is where
        // Context becomes observable.
        Fixture fixture = NewFixture(FakeMode.WriteInScopeThenAsk, autonomous: true);
        AssertEscalatedAfterWriting(await RunAsync(fixture));
        AssertRanInARealGitSegment(AttemptDir(fixture, attempt: 1));

        string escalationsDir = Path.Combine(fixture.PlanDir, "logs", RunId(fixture), "escalations");
        Assert.True(Directory.Exists(escalationsDir),
            $"no escalations/ directory under {escalationsDir} — the needs-human gate did not escalate, so " +
            "this fixture proved nothing about the Context");
        string recordPath = Assert.Single(Directory.GetFiles(escalationsDir, "*-needs-human.json"));

        string context = (string)JsonNode.Parse(File.ReadAllText(recordPath))!["context"]!;

        Assert.Contains(RefAttempt1, context);
        Assert.Contains(PatchFileName, context);
    }

    // ── I6 — DECLARED EXEMPTION: nothing in scope ⇒ nothing preserved, nothing advertised ──────────

    [Fact]
    public async Task NeedsHumanHavingWrittenNothingInScope_LeavesNoPatchNoRefAndNoSalvageSection()
    {
        // DECLARED EXEMPTION (guardrail 02's manifest records it as Expect='Executed'): today NOTHING is
        // preserved on this path, so "leaves nothing" is green when correct, before the feature and after.
        // It is written and run anyway — a dropped row and an oversight look identical from the outside.
        //
        // The rule it holds (§3.4): the empty-diff guard stays exactly as-is. An agent that escalates
        // having written nothing has nothing to salvage, and offering "recover your work" for an empty
        // patch is worse than silence (§11) — the signal that fires when nothing is wrong is the one that
        // gets muted. The fixture writes ONLY an out-of-scope file, which is the case §3.4 says the SCOPE
        // FILTER itself can create: every write filtered out ⇒ an empty patch ⇒ correctly offered nothing.
        //
        // NOTE for the implementing stage: the shipped retry helper calls PreserveAttemptToRef BEFORE it
        // checks the diff for emptiness, so it leaves an (empty) ref even when it returns null. Plan §8
        // requires "no patch, no ref, and no salvage section" here, so the emptiness decision has to be
        // reached without leaving a ref behind — the one place the escalation path cannot mirror the retry
        // helper's ORDER even though it mirrors its rule.
        Fixture fixture = NewFixture(FakeMode.WriteOutOfScopeOnlyThenAsk);
        AssertEscalatedAfterWriting(await RunAsync(fixture));

        string attemptDir = AttemptDir(fixture, attempt: 1);
        AssertRanInARealGitSegment(attemptDir);
        Assert.False(File.Exists(Path.Combine(attemptDir, PatchFileName)),
            $"a {PatchFileName} was written for an attempt with no in-scope work to salvage");
        Assert.False(RefExists(fixture.RepoPath, RefAttempt1),
            $"a salvage ref {RefAttempt1} was left for an attempt with no in-scope work to salvage");
        AssertNoSalvageSection(EmittedText(attemptDir));
    }

    // ── I7 — DECLARED EXEMPTION: serial mode is byte-identical to today ───────────────────────────

    [Fact]
    public async Task SerialMode_EscalationPathPreservesNothing()
    {
        // DECLARED EXEMPTION, same structural reason as I6. `IsRealGitSegment` is false with no worktree
        // provider, so nothing is preserved and nothing is advertised — and that is CORRECT, not a gap:
        // in serial mode the escalating attempt's files are still on disk in the shared workspace, so
        // there is nothing to recover from and no orphaned tree to point at. Its job is to stay green
        // while I1–I5 go from red to green around it.
        Fixture fixture = NewFixture(FakeMode.WriteInScopeThenAsk, maxParallelism: 1);
        AssertEscalatedAfterWriting(await RunAsync(fixture));

        string attemptDir = AttemptDir(fixture, attempt: 1);
        AssertRanWithoutAGitSegment(attemptDir);
        Assert.False(File.Exists(Path.Combine(attemptDir, PatchFileName)),
            $"serial mode wrote a {PatchFileName} — there is no segment to preserve and the files are on disk");
        Assert.False(RefExists(fixture.RepoPath, RefAttempt1),
            $"serial mode created the salvage ref {RefAttempt1}");
        AssertNoSalvageSection(EmittedText(attemptDir));
    }

    // ── I8 — refs are bounded, and bounded is not zero ────────────────────────────────────────────

    [Fact]
    public async Task RepeatEscalations_SalvageRefsAreCappedButNotEmpty()
    {
        // §3.4, "Ref growth is bounded". Salvage refs are pruned only when a task's final settle is
        // `succeeded`, or wholesale on --fresh/reset. #554 adds refs on precisely the tasks that BY
        // DEFINITION never succeed, so a task escalating repeatedly across resumes accumulates them
        // forever unless the writer caps them. A resume keeps the journal's attempt numbering
        // (`needs-human` → pending, attempts preserved), so each run here escalates as attempt N+1 and
        // asks for one more ref.
        //
        // Both halves matter and they fail in opposite directions. "AT LEAST ONE" is what makes this RED
        // today — zero refs, because nothing is preserved at all — and it is also what stops a cap from
        // being implemented as "prune everything". "AT MOST the cap's worth" is what stops unbounded
        // growth. The cap's VALUE is deliberately not named here (it is a stage-2 constant); what is
        // pinned is that ten repeat escalations do not produce ten refs.
        Fixture fixture = NewFixture(FakeMode.WriteInScopeThenAsk, defaultRetries: 0);
        for (int i = 0; i < RepeatEscalationCount; i++)
        {
            AssertEscalatedAfterWriting(await RunAsync(fixture));
            AssertRanInARealGitSegment(AttemptDir(fixture, attempt: i + 1));
        }

        string[] refs = RunGit(fixture.RepoPath, "for-each-ref", "--format=%(refname)", RefPattern)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        Assert.True(refs.Length >= 1,
            $"after {RepeatEscalationCount} repeat escalations the task has NO salvage ref at all. Nothing " +
            "is preserved on the escalation path today; a retention cap that prunes down to zero would be " +
            "the same defect wearing a bound.");
        Assert.True(refs.Length < RepeatEscalationCount,
            $"{RepeatEscalationCount} repeat escalations left {refs.Length} salvage refs — one per attempt, " +
            "so nothing is capping them. These refs are throwaway bookkeeping on tasks that by definition " +
            "never succeed (the settle-prune never fires), and the per-attempt patches in the log dirs " +
            $"remain the durable record. Refs seen: {string.Join(", ", refs)}");
    }

    // ── I9 — the honest framing: ORPHANED, never "rolled back and saved" ──────────────────────────

    [Fact]
    public async Task NeedsHumanEscalation_SalvageTextSaysOrphanedAndNeverClaimsARollback()
    {
        // §3.4 divergence 2. The Retry framing says the work was rolled back to a clean base but SAVED.
        // On this path nothing was rolled back — the attempt loop returns terminally BEFORE the F2 reset
        // (§3.2) — so reusing those bytes tells the human deciding how to unblock something actively
        // false about the state of the tree. The honest sentence is that the tree which produced this
        // work is ORPHANED, and that the ref and the patch are the only durable copies of it.
        //
        // Both halves are asserted, and the positive one is load-bearing: a pin that only BANS the
        // rollback claim is satisfied by an implementation that emits no salvage text at all, which is
        // exactly today's behaviour.
        Fixture fixture = NewFixture(FakeMode.WriteInScopeThenAsk);
        AssertEscalatedAfterWriting(await RunAsync(fixture));
        AssertRanInARealGitSegment(AttemptDir(fixture, attempt: 1));

        string emitted = EmittedText(AttemptDir(fixture, attempt: 1));

        // (a) POSITIVE — the salvage text exists and names the disposition honestly.
        Assert.True(
            OrphanedPhrases.Any(p => emitted.Contains(p, StringComparison.OrdinalIgnoreCase)),
            "The escalating attempt's text never says what actually happened to the tree that produced " +
            "this work: it was not reset, it was ORPHANED — no resume ever takes it, and the ref plus the " +
            "patch are the only durable copies. The wording is yours, but it must contain at least one " +
            $"of: {string.Join(", ", OrphanedPhrases.Select(p => $"\"{p}\""))}.\n{Emitted(emitted)}");

        // (b) NEGATIVE — and it never claims the rollback that did not happen. The banned strings are
        //     CLAIM shapes lifted from the Retry framing (RetryPolicy's header and salvage section), not
        //     bare words: an honest sentence such as "nothing was rolled back" is deliberately still
        //     allowed, because that is the true statement this path needs to be able to make.
        List<string> claims = RollbackClaims
            .Where(c => emitted.Contains(c, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(
            claims.Count == 0,
            "The escalating attempt's text reuses the Retry framing's rollback claim, which is false on " +
            "this path — nothing was rolled back, the attempt returned terminally before the reset " +
            $"(§3.2). Claim(s) found: {string.Join(" | ", claims)}.\n{Emitted(emitted)}");
    }

    // ── phrase sets ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Ways of saying the tree was abandoned rather than reset. The wording stays the implementer's call.</summary>
    private static readonly string[] OrphanedPhrases = ["orphan", "abandoned", "unreachable", "not reachable"];

    /// <summary>
    /// The Retry framing's rollback CLAIMS, quoted from <c>RetryPolicy</c>. Each requires an object
    /// ("… to a clean base", "before the reset …"), so a negated, honest sentence cannot trip them.
    /// </summary>
    private static readonly string[] RollbackClaims = ["rolled back to", "before the reset", "saved, not lost"];

    // ── assertions ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// No recovery routing anywhere in the text an escalating attempt emits. Defined as a positive marker
    /// list rather than a heading lookup, so the negative pin cannot silently pass because a heading was
    /// renamed.
    /// </summary>
    private static void AssertNoSalvageSection(string emitted)
    {
        string[] markers = [PatchFileName, "refs/guardrails/", "salvageable"];
        List<string> present = markers.Where(m => emitted.Contains(m, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(
            present.Count == 0,
            "A salvage section was advertised for an attempt with nothing to salvage. Marker(s) found: " +
            $"{string.Join(", ", present)}.\n{Emitted(emitted)}");
    }

    private static string Emitted(string text) =>
        "----- emitted attempt text -----\n" + text + "\n--------------------------------";

    /// <summary>
    /// The fixture's own premise, asserted rather than assumed: the action really did run as THIS task's
    /// action and really did escalate. The fake CLI's #253 containment gate means a non-action invocation
    /// writes nothing and emits no <c>needsHuman</c> — so a settle carrying this question proves the gate
    /// passed, and the gate is the first thing the script does, straight-line before its file writes.
    /// Without this a fixture that silently stopped writing would leave every pin below red forever and
    /// no implementation could turn them green.
    /// </summary>
    private static void AssertEscalatedAfterWriting(RunReport report)
    {
        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.NeedsHuman, task.Outcome);
        Assert.Contains(Question, task.Summary);
    }

    /// <summary>
    /// The OTHER half of the premise (plan §7): the attempt ran in a REAL git segment. <c>attempt-
    /// provenance.json</c> records the segment branch, the worktree path and the base commit the segment
    /// forked from, and all three are null in serial mode — which is exactly the state that makes
    /// <c>IsRealGitSegment</c> false and the whole salvage path a no-op. A fixture that quietly degraded
    /// to serial would keep every worktree pin red for a reason no implementation can fix.
    /// </summary>
    private static void AssertRanInARealGitSegment(string attemptDir)
    {
        JsonNode? provenance = ReadProvenance(attemptDir);
        Assert.True(provenance is not null,
            $"no attempt-provenance.json in {attemptDir} — this attempt ran with no segment at all, so " +
            "IsRealGitSegment was false and the salvage path was a no-op regardless of what is implemented");
        Assert.False(string.IsNullOrEmpty((string?)provenance!["worktreePath"]),
            "the attempt recorded no segment worktree — it did not run in a real git segment");
        Assert.False(string.IsNullOrEmpty((string?)provenance["baseCommit"]),
            "the attempt recorded no base commit — it did not run in a real git segment");
    }

    /// <summary>The serial mirror of <see cref="AssertRanInARealGitSegment"/>: no segment was allocated.</summary>
    private static void AssertRanWithoutAGitSegment(string attemptDir)
    {
        JsonNode? provenance = ReadProvenance(attemptDir);
        Assert.True(
            provenance is null || string.IsNullOrEmpty((string?)provenance["worktreePath"]),
            "this fixture was supposed to run SERIALLY (maxParallelism 1, no worktree provider) but the " +
            "attempt recorded a segment worktree — so it proves nothing about the serial path");
    }

    private static JsonNode? ReadProvenance(string attemptDir)
    {
        string path = Path.Combine(attemptDir, "attempt-provenance.json");
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
    }

    // ── driving the real composition root ─────────────────────────────────────────────────────────

    private static async Task<RunReport> RunAsync(Fixture fixture)
    {
        PlanLoadResult load = new PlanLoader().Load(fixture.PlanDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        // The REAL factory (the recurring #120 lesson): the worktree provider and the escalation sink must
        // be reachable from HERE, never constructed by the test.
        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);
        return await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);
    }

    /// <summary>The run id of the LATEST run recorded in the plan's journal.</summary>
    private static string RunId(Fixture fixture) =>
        JournalReader.Read(RunJournal.PathFor(fixture.PlanDir)).RunId;

    private static string AttemptDir(Fixture fixture, int attempt) =>
        Path.Combine(fixture.PlanDir, "logs", RunId(fixture), TaskId, $"attempt-{attempt}");

    /// <summary>
    /// The text an escalating attempt emits into its own log dir — <c>feedback.md</c>, which
    /// <c>AttemptJournaler.NeedsHuman</c> already composes and writes there today (it merely returns
    /// <c>FeedbackPath: null</c>, because on this path there is no next attempt to inline it into).
    /// Returns "" when the file is absent, so a missing artifact reads as "said nothing" rather than
    /// throwing over the assertion that was about to explain itself.
    /// </summary>
    private static string EmittedText(string attemptDir)
    {
        string path = Path.Combine(attemptDir, "feedback.md");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    // ── the fixture: a real git repo + a real plan folder ─────────────────────────────────────────

    /// <summary>One fixture: a real git repo, a plan folder inside it, and this instance's #253 token.</summary>
    private sealed class Fixture
    {
        public required string Root { get; init; }

        public required string RepoPath { get; init; }

        public required string PlanDir { get; init; }

        /// <summary>
        /// An unguessable, per-fixture value for <see cref="ActionTokenVar"/>, so the ambient environment
        /// of an enclosing <c>guardrails run</c> can never match it (issue #253).
        /// </summary>
        public string ActionToken { get; } = Guid.NewGuid().ToString("N");
    }

    private Fixture NewFixture(
        FakeMode mode, int maxParallelism = 2, int defaultRetries = 1, bool autonomous = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "gr-esc-salvage-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);

        string repoPath = Path.Combine(root, "repo");
        Directory.CreateDirectory(repoPath);
        InitRepo(repoPath);

        // The plan lives INSIDE the repo with `workspace: ".."`, exactly as RetrySalvageTests does, so a
        // parallel run resolves a real git top-level and the factory wires a real GitWorktreeProvider.
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));

        var fixture = new Fixture { Root = root, RepoPath = repoPath, PlanDir = planDir };
        WritePlan(fixture, mode, maxParallelism, defaultRetries, autonomous);
        return fixture;
    }

    private static void WritePlan(Fixture fixture, FakeMode mode, int maxParallelism, int defaultRetries, bool autonomous)
    {
        string actionCommand = WriteFakeActionCli(fixture, mode).Replace("\\", "\\\\");

        // The reserved `overwatch` profile is the criticality judge's advisory channel: a CRITICAL
        // assessment at the `high` dial makes the needs-human gate ESCALATE (writing the record I5 reads)
        // rather than proceed on a best guess. Built only for the autonomous fixture — absent an
        // `autonomy` block under `autonomyPolicy: auto` the factory constructs no sink and the run is
        // byte-identical to a non-autonomous one, which is what every other test here wants.
        string autonomyBlock = autonomous
            ? "  \"autonomyPolicy\": \"auto\",\n  \"autonomy\": { \"escalationThreshold\": \"high\" },\n"
            : "";

        string overwatchProfile = autonomous
            ? ",\n    \"overwatch\": {\n" +
              $"      \"command\": \"{WriteFakeOverwatchCli(fixture).Replace("\\", "\\\\")}\",\n" +
              "      \"permissionMode\": \"default\",\n" +
              "      \"allowedTools\": [\"Read\"],\n" +
              "      \"maxTurns\": 5\n" +
              "    }"
            : "";

        File.WriteAllText(Path.Combine(fixture.PlanDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": {{defaultRetries}},
              "maxParallelism": {{maxParallelism}},
              "defaultTimeoutSeconds": 120,
              "transientPauseBudgetSeconds": 30,
            {{autonomyBlock}}  "promptRunners": {
                "default": "claude",
                "claude": {
                  "command": "{{actionCommand}}",
                  "permissionMode": "acceptEdits",
                  "allowedTools": ["Read", "Write"],
                  "maxTurns": 5
                }{{overwatchProfile}}
              }
            }
            """);

        string taskDir = Path.Combine(fixture.PlanDir, "tasks", TaskId);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        // action.env carries the #253 proof-of-origin token. TaskExecutor.BuildEnvironment folds
        // task.Action.Env into the ACTION process env, so the token reaches the fake CLI for a real
        // attempt — and ONLY for a real attempt (NeedsHumanTriage builds its own empty env and never
        // sees it).
        WriteFixtureFile(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "fake prompt task that escalates after writing",
              "dependsOn": [],
              "writeScope": ["src/"],
              "action": {
                "path": "action.prompt.md",
                "env": { "{{ActionTokenVar}}": "{{fixture.ActionToken}}" }
              }
            }
            """);
        WriteFixtureFile(Path.Combine(taskDir, "action.prompt.md"), "Implement the widget.\n");

        // Never reached: the needsHuman short-circuit returns before any guardrail runs. Present because
        // a task must declare at least one.
        WriteExecutable(
            Path.Combine(taskDir, "guardrails", Windows ? "01-ok.cmd" : "01-ok.sh"),
            Windows ? "@echo off\r\nexit /b 0\r\n" : "#!/usr/bin/env bash\nexit 0\n");
    }

    // ── the fake Claude CLI ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The action fake: write, then ask. Every write target is derived from the CHILD's environment
    /// (<c>$GUARDRAILS_WORKSPACE</c>) or its cwd, both of which a child INHERITS whenever the harness does
    /// not populate them — and <c>NeedsHumanTriage</c>, which every test in this class provokes, is
    /// exactly such an invocation. So the first thing the script does is a POSITIVE identification: unless
    /// <see cref="ActionTokenVar"/> holds this fixture's own token, it touches nothing anywhere and
    /// returns a benign result (issue #253). A path denylist cannot serve here, because the segment
    /// worktree the fake must legitimately write to lives under the harness's own worktree root, not
    /// under this fixture's temp root.
    /// </summary>
    private static string WriteFakeActionCli(Fixture fixture, FakeMode mode)
    {
        bool writesInScope = mode is FakeMode.WriteInScopeThenAsk or FakeMode.WriteBothThenAsk;
        bool writesOutOfScope = mode is FakeMode.WriteBothThenAsk or FakeMode.WriteOutOfScopeOnlyThenAsk;

        if (Windows)
        {
            string ps1 = Path.Combine(fixture.Root, "fake-claude.ps1");
            WriteFixtureFile(ps1,
                $$"""
                $null = [Console]::In.ReadToEnd()

                # Issue #253 containment gate — see WriteFakeActionCli's remarks.
                if ($env:{{ActionTokenVar}} -cne '{{fixture.ActionToken}}') {
                    Write-Output '{"type":"result","is_error":false,"result":"fake claude: not this fixture task action - no files written","total_cost_usd":0,"num_turns":1}'
                    exit 0
                }
                if ([string]::IsNullOrWhiteSpace($env:GUARDRAILS_WORKSPACE)) {
                    [Console]::Error.WriteLine('fake claude: GUARDRAILS_WORKSPACE unset for a task action')
                    exit 9
                }

                if ({{Bool(writesInScope)}}) {
                    $srcDir = Join-Path $env:GUARDRAILS_WORKSPACE 'src'
                    New-Item -ItemType Directory -Force -Path $srcDir | Out-Null
                    Set-Content -NoNewline -Path (Join-Path $srcDir 'output.txt') -Value 'attempt-output'
                }
                if ({{Bool(writesOutOfScope)}}) {
                    Set-Content -NoNewline -Path (Join-Path $env:GUARDRAILS_WORKSPACE 'outside.txt') -Value 'out of scope'
                }

                if ($env:GUARDRAILS_STATE_OUT) {
                    Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value '{"needsHuman": "{{Question}}"}'
                }
                Write-Output '{"type":"result","is_error":false,"result":"asked a human","total_cost_usd":0.01,"num_turns":2}'
                """);

            string cmd = Path.Combine(fixture.Root, "fake-claude.cmd");
            WriteFixtureFile(cmd, $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" %*\r\n");
            return cmd;
        }

        string sh = Path.Combine(fixture.Root, "fake-claude.sh");
        string body =
            "#!/usr/bin/env bash\n" +
            "cat > /dev/null\n" +
            // Issue #253 containment gate — twin of the .ps1 branch.
            $"if [ \"${ActionTokenVar}\" != \"{fixture.ActionToken}\" ]; then\n" +
            "  printf '{\"type\":\"result\",\"is_error\":false,\"result\":\"fake claude: not this fixture task action - no files written\",\"total_cost_usd\":0,\"num_turns\":1}\\n'\n" +
            "  exit 0\n" +
            "fi\n" +
            "if [ -z \"$GUARDRAILS_WORKSPACE\" ]; then\n" +
            "  echo 'fake claude: GUARDRAILS_WORKSPACE unset for a task action' >&2\n" +
            "  exit 9\n" +
            "fi\n" +
            (writesInScope
                ? "mkdir -p \"$GUARDRAILS_WORKSPACE/src\"\n" +
                  "printf 'attempt-output' > \"$GUARDRAILS_WORKSPACE/src/output.txt\"\n"
                : "") +
            (writesOutOfScope
                ? "printf 'out of scope' > \"$GUARDRAILS_WORKSPACE/outside.txt\"\n"
                : "") +
            "if [ -n \"$GUARDRAILS_STATE_OUT\" ]; then\n" +
            $"  printf '{{\"needsHuman\": \"{Question}\"}}' > \"$GUARDRAILS_STATE_OUT\"\n" +
            "fi\n" +
            "printf '{\"type\":\"result\",\"is_error\":false,\"result\":\"asked a human\",\"total_cost_usd\":0.01,\"num_turns\":2}\\n'\n";
        WriteExecutable(sh, body);
        return sh;
    }

    /// <summary>
    /// The overwatch fake: drain stdin, echo one canned stream-json <c>result</c> line whose text is the
    /// advisory assessment. It writes no files at all, so it needs no containment gate.
    /// </summary>
    private static string WriteFakeOverwatchCli(Fixture fixture)
    {
        string streamLine =
            "{\"type\":\"result\",\"is_error\":false,\"result\":\"" +
            AssessCritical.Replace("\\", "\\\\").Replace("\"", "\\\"") +
            "\",\"total_cost_usd\":0,\"num_turns\":1}";

        if (Windows)
        {
            string ps1 = Path.Combine(fixture.Root, "fake-overwatch.ps1");
            WriteFixtureFile(ps1, "$null = [Console]::In.ReadToEnd()\r\nWrite-Output '" + streamLine + "'\r\n");
            string cmd = Path.Combine(fixture.Root, "fake-overwatch.cmd");
            WriteFixtureFile(cmd, $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" %*\r\n");
            return cmd;
        }

        string sh = Path.Combine(fixture.Root, "fake-overwatch.sh");
        WriteExecutable(sh, "#!/usr/bin/env bash\ncat > /dev/null\nprintf '%s\\n' '" + streamLine + "'\n");
        return sh;
    }

    private static string Bool(bool value) => value ? "$true" : "$false";

    // ── git + IO plumbing ─────────────────────────────────────────────────────────────────────────

    private static void InitRepo(string repoPath)
    {
        RunGit(repoPath, "init");
        RunGit(repoPath, "config", "user.email", "test@guardrails.local");
        RunGit(repoPath, "config", "user.name", "Guardrails Test");
        RunGit(repoPath, "config", "commit.gpgsign", "false");
        // #116: without this, Git-for-Windows rewrites line endings on checkout and the fixture's content
        // hashes stop matching across platforms.
        RunGit(repoPath, "config", "core.autocrlf", "false");
        WriteFixtureFile(Path.Combine(repoPath, "README.md"), "# escalation-salvage-test");
        RunGit(repoPath, "add", ".");
        RunGit(repoPath, "commit", "-m", "Initial commit");
    }

    /// <summary>Ref existence, asked of the MAIN repo — refs are shared across worktrees, which is what
    /// makes a salvage ref outlive the segment the attempt ran in.</summary>
    private static bool RefExists(string repoPath, string refName) =>
        Git(repoPath, ["rev-parse", "--verify", "--quiet", refName]).ExitCode == 0;

    private static string RunGit(string workingDir, params string[] args)
    {
        (string stdout, int exitCode, string stderr) = Git(workingDir, args);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} (in {workingDir}) exited {exitCode}: {stderr.Trim()}");
        }

        return stdout;
    }

    private static (string Stdout, int ExitCode, string Stderr) Git(string workingDir, string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (stdout, proc.ExitCode, stderr);
    }

    /// <summary>
    /// Write a fixture file, CREATING ITS PARENT DIRECTORY FIRST. #116: Git-for-Windows removes a
    /// directory it has just emptied, so a later write into what looks like an existing tree throws
    /// <see cref="DirectoryNotFoundException"/> on Windows and nowhere else.
    /// </summary>
    private static void WriteFixtureFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WriteExecutable(string path, string content)
    {
        WriteFixtureFile(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    /// <summary>
    /// Windows-safe recursive delete. #116: git marks loose objects under <c>.git/objects</c> READ-ONLY on
    /// Windows, and <see cref="Directory.Delete(string, bool)"/> then throws
    /// <see cref="UnauthorizedAccessException"/> — NOT <see cref="IOException"/> — so the attribute is
    /// stripped first and BOTH exceptions are caught.
    /// </summary>
    private static void SafeDeleteTree(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
        catch (IOException) { /* best-effort teardown */ }
        catch (UnauthorizedAccessException) { /* best-effort teardown */ }
    }
}
