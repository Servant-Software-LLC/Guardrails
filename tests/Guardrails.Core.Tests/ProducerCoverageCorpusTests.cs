using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// <b>The GR2060 corpus sweep</b> (plan 33 §8.5, doc 19 §10 step 4) — <see cref="ProducerCoverage"/> run
/// over <b>every committed guardrail script in this repository</b>, at more than one point in history, with
/// a per-plan per-commit expectation table. It is wired as a <b>terminal-gate</b> guardrail, so a
/// disagreement withholds delivery rather than merging.
///
/// <para><b>Two things this sweep is built to catch, and they are different failures.</b> A finding where
/// the table says 0 means the extractor learned to fire on a correct plan — doc 19 §5's falsification
/// trigger, not a fixture to adjust. <b>No finding where the table says 1 means the check is mute</b>, and
/// that is the one that looks like success. Plan 33 §11 prohibition 5 forbids re-baselining this table to
/// "≤ N findings" or flattening it back to a blanket zero; <see cref="TheExpectationIsNotABlanketZero"/>
/// pins that prohibition with a test rather than with a sentence.</para>
///
/// <h3>The population, and the two ways the first sweep measured less than it claimed</h3>
///
/// <para><b>(a) It walked 533 of 850 scripts.</b> The hand-run sweep in plan 33 §5.4 enumerated plan folders
/// carrying a <b>top-level <c>tasks/</c></b> directory. Four folders are <b>waved</b> — they nest their tasks
/// under <c>wave-NN-*/tasks/</c> — and a fifth nests a whole example plan one level deeper; all five were
/// silently excluded, and one of them carries the only positive control this check has:</para>
///
/// <list type="table">
///   <listheader><term>folder</term><description>scripts — in the old walk?</description></listheader>
///   <item><term><c>autonomous-mode-impl</c></term><description>100 — no, waved</description></item>
///   <item><term><c>model-tiering-stage-2</c></term><description><b>89 — no, waved, and it carries the positive control</b></description></item>
///   <item><term><c>model-tiering-stage-3</c></term><description>78 — no, waved</description></item>
///   <item><term><c>salvage-advice-provisioning</c></term><description>39 — no, waved</description></item>
///   <item><term><c>09-preflight-first-class</c></term><description>11 — no, neither layout</description></item>
/// </list>
///
/// <para>So the headline <i>"0 findings over 14 plan folders"</i> was computed over a population that
/// structurally excluded the one plan known to fire. This sweep enumerates a plan folder as <b>any directory
/// holding a <c>guardrails.json</c></b>, which is what the loader itself treats as a plan — flat, waved and
/// nested-example layouts all fall out of that one rule.
/// <see cref="TheSweepWalksEveryCommittedScript_IncludingTheWavedFolders"/> reproduces the old walk beside
/// the new one and asserts the difference, so the correction is measured rather than asserted.</para>
///
/// <para><b>(b) The denominator itself was wrong, and it is why enumeration goes through
/// <c>git ls-tree</c> and never through the working tree.</b> 1,271 <c>.ps1</c> exist on disk under
/// <c>docs/plans/</c>, but <b>364 of them are gitignored generated <c>containment-hook.ps1</c> copies</b> and
/// only 850 are committed. A disk walk would hand this sweep 364 copies of one generated hook plus a
/// population its own per-commit method cannot evaluate at all — you cannot
/// <c>git show &lt;commit&gt;:&lt;path&gt;</c> a file git has never tracked.</para>
///
/// <h3>Why each plan is evaluated at its OWN pre-run commit</h3>
///
/// <para>Today's tree is post-merge: the witnesses these plans required are present <i>because the plans
/// ran</i>. A HEAD-only sweep proves the check is silent on <b>satisfied</b> requirements and nothing more —
/// it is structurally incapable of failing, which makes it a gate with no teeth (plan 33 §5.4(a), doc 19's
/// own precedent about GR2062). So every plan is evaluated twice: at <b>HEAD</b>, and at the commit that
/// <b>first authored a task in it</b> — the breakdown commit, the earliest tree state where the plan has
/// anything to say about what it will produce. That rule is mechanical, and
/// <see cref="ThePreRunCommitsAreTheBreakdownCommits"/> re-derives every pinned SHA from git rather than
/// trusting the table.</para>
///
/// <para><b>The rule is keyed on the first <c>task.json</c> and not on the first <c>guardrails.json</c>, and
/// this sweep's own non-vacuity pin is what found the difference.</b> <c>24-plan-source-provenance</c> had a
/// folder holding nothing but a <c>guardrails.json</c> for five days before it was broken down. A plan with
/// no tasks has an empty <c>writeScope</c> union and no script guardrails, so GR2060 is silent on it for
/// reasons that have nothing to do with the tree — a zero that measures nothing. <see cref="Findings"/>
/// therefore refuses any row whose plan presents no PowerShell script guardrail.</para>
///
/// <h3>The expectation table</h3>
///
/// <list type="table">
///   <listheader><term>plan</term><description>commit → expected GR2060 findings</description></listheader>
///   <item>
///     <term><c>model-tiering-stage-2</c> — <c>guardrails/03-dor-section-6-contract-landed.ps1</c></term>
///     <description><c>1b8e681</c> → <b>0</b>. Its own pre-run commit, and the row plan 33 §8.5 and §11
///     prohibition 5 both got wrong: GR2060 <b>cannot</b> fire here. At that commit
///     <c>wave-02-attempt-launch-wiring</c> held zero task manifests, so <c>PlanIsClosed</c> is false and
///     condition 10 correctly suppresses — a future wave might own the file, and one later did. Firing here
///     would require deleting condition 10.</description>
///   </item>
///   <item>
///     <term>the same script</term>
///     <description><c>544f7d5</c> → <b>exactly 1</b>, naming <c>tierSource</c> and
///     <c>docs/plans/02-schemas-and-contracts.md</c>. <b>The required non-zero.</b> Wave 2 is authored by
///     now, so the plan is closed; the SSOT is still <c>tierSource</c>-free and no task declares it.</description>
///   </item>
///   <item>
///     <term>the same script</term>
///     <description><c>5bd29da</c> → <b>0</b> — witness still absent, SSOT bytes byte-identical, but
///     <c>14-land-ssot-schema-deltas</c> now declares that path in its <c>writeScope</c>. This row and the
///     one above differ ONLY in whether a task owns the file, so together they are the only rows that can
///     catch the check firing wrongly AND going mute.</description>
///   </item>
///   <item>
///     <term>the same script</term>
///     <description><c>HEAD</c> → <b>0</b> — the requirement is satisfied now.</description>
///   </item>
///   <item>
///     <term>every other plan folder</term>
///     <description>its own pre-run commit → <b>0</b>, and <c>HEAD</c> → <b>0</b>.</description>
///   </item>
/// </list>
///
/// <para>The table is <see cref="Corpus"/> plus <see cref="Pinned"/>, and it is checked against the corpus
/// in both directions: <see cref="TheExpectationTableCoversEveryPlanFolder"/> fails if a plan folder exists
/// with no row, so adding a plan folder adds a row rather than being averaged away.</para>
///
/// <h3>How a historical commit is evaluated without a checkout</h3>
///
/// <para>Per (plan, commit) the fixture <see cref="CorpusWorkspaces"/> builds a throwaway workspace with
/// <c>git archive</c>: the plan folder at that commit, plus every file at that commit whose workspace-relative
/// path appears verbatim in one of the plan's own <c>.ps1</c> scripts. That second set is a deliberate
/// <b>superset</b> of what the extractor can name — <see cref="ProducerCoverage"/> only ever names a path it
/// read out of a quoted literal, so the path is a substring of the script text by construction — and being a
/// superset is the only property it needs. Narrowing it would produce false silence, which is the failure
/// mode this whole sweep exists to detect.</para>
///
/// <para>Condition 6's oracle is <see cref="CommitTreeGitTrackedFileProbe"/> rather than the production
/// <see cref="GitLsFilesProbe"/>, and that is not a fake standing in for the real thing: a temp workspace has
/// no git index, and "was this path tracked at <c>544f7d5</c>?" is a question the working tree cannot answer
/// at all. The probe reads git's own tree listing for that commit, which is the same fact
/// <c>git ls-files --error-unmatch</c> reports for HEAD. The production adapter's own truthfulness is pinned
/// separately, against a real index, by <c>ProducerCoverageTests.Silent_WhenTheFileIsNotGitTracked</c>.</para>
///
/// <para>Every evaluation still runs the #382 anti-tautology pin: each count is computed once by calling
/// <c>ProducerCoverage.Validate</c> directly and once through <see cref="PlanValidator.Validate"/>, and the
/// two lists must agree. A check that is written but not wired into the composition root passes the first and
/// fails the second.</para>
/// </summary>
[Collection(GitEnvironmentCollection.Name)]
public sealed class ProducerCoverageCorpusTests : IClassFixture<CorpusWorkspaces>
{
    private const string Gr2060 = "GR2060";

    /// <summary>The plan whose gate script is the one recovered artifact GR2060 fires on.</summary>
    private const string Stage2 = "docs/plans/model-tiering-stage-2";

    /// <summary>The commit plan 33 §8.5 pinned the positive control to, where it cannot in fact fire.</summary>
    private const string Stage2PreRun = "1b8e681";

    /// <summary>The commit where it does fire: wave 2 authored, SSOT still witness-free, nobody owns it.</summary>
    private const string Stage2Firing = "544f7d5";

    /// <summary>One commit later — the only change GR2060 can see is that a task now owns the SSOT.</summary>
    private const string Stage2Owned = "5bd29da";

    private const string SsotPath = "docs/plans/02-schemas-and-contracts.md";

    private const string Witness = "tierSource";

    private readonly CorpusWorkspaces _workspaces;

    public ProducerCoverageCorpusTests(CorpusWorkspaces workspaces) => _workspaces = workspaces;

    // ══ the table ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One plan folder in the corpus: where it lives, the commit that first authored a task in it, and the
    /// GR2060 findings expected at that commit and at HEAD.
    /// </summary>
    /// <param name="Folder">Repo-relative directory holding the plan's <c>guardrails.json</c>.</param>
    /// <param name="PreRunCommit">The breakdown commit — the first tree state in which this plan has tasks.</param>
    /// <param name="AtPreRun">Expected GR2060 findings at <paramref name="PreRunCommit"/>.</param>
    /// <param name="AtHead">Expected GR2060 findings at HEAD.</param>
    /// <param name="Note">What that pre-run commit actually is, where it is not a clean pre-run state.</param>
    private sealed record PlanRow(
        string Folder, string PreRunCommit, int AtPreRun, int AtHead, string Note = "");

    /// <summary>An extra (plan, commit) pair the table pins beyond the two every plan gets.</summary>
    private sealed record PinnedRow(string Folder, string Commit, int Expected, string Why);

    /// <summary>
    /// Every plan folder in the repository, with its own pre-run commit. A plan folder is any directory
    /// holding a <c>guardrails.json</c>, so this list must equal what
    /// <see cref="CorpusWorkspaces.PlanFolders"/> enumerates from git —
    /// <see cref="TheExpectationTableCoversEveryPlanFolder"/> asserts exactly that in both directions.
    ///
    /// <para><b>Four of these pre-run commits are not a clean pre-run state, and saying so is the point.</b>
    /// A plan whose folder was first committed only after its run had already produced work has no tree state
    /// where the plan exists and its deliverables do not. Its 0 is then the same structurally-guaranteed
    /// silence §5.4(a) warns about, which is exactly why the sweep does not rest on those rows: the rows that
    /// carry the evidence are <see cref="Pinned"/>.</para>
    /// </summary>
    private static readonly PlanRow[] Corpus =
    [
        new("docs/plans/04-dogfood-cost-cap", "8012572", 0, 0,
            "committed with M7 packaging, alongside the work — not a clean pre-run tree"),
        new("docs/plans/08-parallel-execution", "e648768", 0, 0),
        new("docs/plans/09-preflight-first-class/example/example-plan", "382ed99", 0, 0,
            "an illustrative example plan, one level deeper than either layout; its gate bodies are simulated"),
        new("docs/plans/228-escalation-ladder", "adc4cd2", 0, 0,
            "broken down but NOT yet run, so its pre-run commit and HEAD are the same tree. The FIFTH " +
            "consecutive plan to trip this table, and the second where it was PREDICTED: plan 36's row " +
            "below says the fix belongs in /plan-breakdown (#587), and #587's quality-bar item now spells " +
            "out the two-commit dance those rows discovered the hard way. This breakdown ran the tripwire " +
            "BEFORE committing (green, because PlanFolders enumerates from the git TREE and an untracked " +
            "folder is invisible to it - so unlike plan 35 the mere directory was NOT enough), watched it " +
            "go red on the commit that added the folder, and added this row in the next one. That is the " +
            "catch-22 stated plainly: the row needs the breakdown commit's own sha, which cannot exist " +
            "until the commit that breaks the test has been made. #601 tracks graduating this class of " +
            "tripwire into a deterministic check"),
        new("docs/plans/24-plan-source-provenance", "1d3e1ce", 0, 0,
            "its folder existed five days earlier at 3d9835f holding only a guardrails.json; a plan with no tasks is a vacuous row"),
        new("docs/plans/26-guardrail-quality-gate", "2d15108", 0, 0),
        new("docs/plans/27-operator-visibility", "2d15108", 0, 0,
            "cut from plan 25 in the same commit as 26, so the two share a pre-run tree"),
        new("docs/plans/28-local-inference-runner", "553a8b0", 0, 0),
        new("docs/plans/30-telemetry-phase-1", "10816fb", 0, 0),
        new("docs/plans/31-unattended-run-hardening", "e48eebd", 0, 0),
        new("docs/plans/32-executed-definition-hash", "4a308ab", 0, 0),
        new("docs/plans/33-unproducible-requirements", "c04c3d1", 0, 0,
            "this plan's own breakdown; §11 prohibition 9 requires GR2060 to be silent on it, and rows 7 and 8 own the SSOT so it is"),
        new("docs/plans/34-run-event-stream-and-attach", "b33dd1a", 0, 0,
            "broken down but NOT yet run, so its pre-run commit and HEAD are the same tree; it is also the " +
            "row that proves the point of this table - merging its folder (PR #589) broke " +
            "TheExpectationTableCoversEveryPlanFolder, which is the tripwire-no-task-owns defect of #587 " +
            "arriving for the second time in two plans"),
        new("docs/plans/35-event-vocabulary", "4e4785e", 0, 0,
            "broken down but NOT yet run, so its pre-run commit and HEAD are the same tree. It is the row " +
            "above's defect arriving a THIRD time, and one step earlier: plan 33 broke " +
            "BreakdownSalvageAllowListTests, plan 34 broke this test when its folder MERGED (PR #589), and " +
            "plan 35 broke it at BREAKDOWN - creating the folder was enough, before any task ran. Its own " +
            "terminal gate was red on arrival and no task in it could have fixed that, because this file is " +
            "in no plan's writeScope by construction. The row is added by hand because it needs the " +
            "breakdown commit's own sha, which does not exist when the plan's tasks are authored - which is " +
            "precisely why it keeps being forgotten, and why the fix belongs in /plan-breakdown (#587)"),
        new("docs/plans/36-onevent-webhooks", "aecfd3e", 0, 0,
            "broken down but NOT yet run, so its pre-run commit and HEAD are the same tree. The FOURTH " +
            "consecutive plan to trip this table, and the first where that was PREDICTED rather than " +
            "discovered: the row above says the fix belongs in /plan-breakdown (#587), so plan 36's " +
            "breakdown checked the tripwire before committing, watched it go red on the commit that " +
            "created the folder, and added this row in the next one. That is the whole catch-22 stated " +
            "plainly - the row needs the breakdown commit's own sha, which cannot exist until the commit " +
            "that breaks the test has been made. Knowing it in advance turned a red terminal gate into " +
            "two commits; it did not make the row automatic, which is why #601 tracks graduating this " +
            "class of tripwire into a deterministic check"),
        new("docs/plans/autonomous-mode-impl", "7cb0bfa", 0, 0,
            "waved, and stubbed for JIT: wave 3 is declared empty, so PlanIsClosed is false at the pre-run commit"),
        new("docs/plans/diagram-live-status-and-search", "d9c006d", 0, 0,
            "committed with the feature it produced — not a clean pre-run tree"),
        new("docs/plans/harden-flaky-worktree-test", "d9c006d", 0, 0,
            "committed with the feature it produced — not a clean pre-run tree"),
        new("docs/plans/model-evidence-and-graduation", "0bddd55", 0, 0),
        new("docs/plans/model-tiering-stage-1", "728e7a7", 0, 0),
        new(Stage2, Stage2PreRun, 0, 0,
            "waved; at this commit wave 2 holds zero tasks, so condition 10 suppresses — see Pinned for the commits that carry the evidence"),
        new("docs/plans/model-tiering-stage-3", "34ec050", 0, 0,
            "waved, wave 2 stubbed for JIT at the pre-run commit"),
        new("docs/plans/preflights-impl", "382ed99", 0, 0),
        new("docs/plans/salvage-advice-provisioning", "d9c006d", 0, 0,
            "waved, and committed with the feature it produced — not a clean pre-run tree"),
        new("examples/hello-guardrails/hello-guardrails", "5ecc02c", 0, 0),
        new("examples/parallel-hello/parallel-hello", "23897c2", 0, 0),
        new("examples/waved-hello/waved-hello", "dba30eb", 0, 0)
    ];

    /// <summary>
    /// The rows that carry the evidence. Every other row in <see cref="Corpus"/> is a 0, and a table of
    /// nothing but zeroes cannot tell a working check from a mute one — these three are the discrimination.
    /// All three read the SAME gate script, whose blob is byte-identical at all three commits.
    /// </summary>
    private static readonly PinnedRow[] Pinned =
    [
        new(Stage2, Stage2Firing, 1,
            "THE REQUIRED NON-ZERO. Wave 2 is authored, so the plan is closed; the SSOT is tracked and " +
            "carries zero occurrences of 'tierSource'; no task's writeScope names it. Nothing the plan can " +
            "do makes this gate pass. A 0 here means GR2060 has gone MUTE."),
        new(Stage2, Stage2Owned, 0,
            "One commit later. Gate script byte-identical, SSOT byte-identical and still witness-free - the " +
            "ONLY thing GR2060 can see that moved is that 14-land-ssot-schema-deltas now declares " +
            "docs/plans/02-schemas-and-contracts.md in its writeScope. A 1 here means condition 8 is not " +
            "being consulted."),
        new(Stage2, "HEAD", 0,
            "The same script against today's tree, where the SSOT now carries 'tierSource'. A 1 here means " +
            "the check fires on the clause's TEXT rather than on the tree - doc 19 §3.2's wolf.")
    ];

    /// <summary>
    /// Every (plan, commit, expected) triple the sweep runs: two per plan, plus the pinned rows.
    ///
    /// <para>The two tables overlap by exactly one pair — <c>model-tiering-stage-2</c> at HEAD is both that
    /// plan's HEAD row and the pinned silence control — so a pair is emitted once. Deduplicating here rather
    /// than deleting one of them keeps both tables readable on their own terms; that they AGREE is asserted
    /// by <see cref="TheTwoTablesDoNotContradictEachOther"/> instead of assumed.</para>
    /// </summary>
    public static TheoryData<string, string, int> Rows
    {
        get
        {
            var rows = new TheoryData<string, string, int>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach ((string folder, string commit, int expected) in AllRows())
            {
                if (seen.Add(folder + "@" + commit))
                {
                    rows.Add(folder, commit, expected);
                }
            }

            return rows;
        }
    }

    /// <summary>Both tables flattened, in table order and with duplicates intact.</summary>
    private static IEnumerable<(string Folder, string Commit, int Expected)> AllRows()
    {
        foreach (PlanRow plan in Corpus)
        {
            yield return (plan.Folder, plan.PreRunCommit, plan.AtPreRun);
            yield return (plan.Folder, "HEAD", plan.AtHead);
        }

        foreach (PinnedRow pinned in Pinned)
        {
            yield return (pinned.Folder, pinned.Commit, pinned.Expected);
        }
    }

    // ══ 1. The sweep, row by row ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One row of the table: <see cref="ProducerCoverage"/> over one plan folder as it stood at one commit.
    /// A row that disagrees fails on its own rather than being averaged into a total, and the failure message
    /// names the plan, the commit and every finding — because the only two legal responses to a surprise here
    /// are "back the extractor out" and "escalate", and both need to know which plan.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public void TheCorpusSweepMatchesTheExpectationTable(string planFolder, string commit, int expected)
    {
        SkipUnlessAvailable(commit);

        IReadOnlyList<Diagnostic> findings = Findings(planFolder, commit);

        Assert.True(findings.Count == expected,
            $"GR2060 over '{planFolder}' at {commit}: expected {expected} finding(s), got {findings.Count}." +
            (findings.Count > expected
                ? " A finding on a plan the table expects to be silent is a RESULT, not a licence to " +
                  "re-baseline: plan 33 §11 prohibition 5 forbids flattening this expectation to a tolerance " +
                  "or a blanket zero, and doc 19 §5 makes an unexplained finding the trigger for backing the " +
                  "extractor OUT. Escalate naming the plan and the finding."
                : " A MISSING finding is the failure that looks like success: the check has gone mute over " +
                  "the corpus while every other test in the suite still passes.") +
            Detail(findings));
    }

    // ══ 2. The population ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The measurement error this sweep exists to correct, measured.</b> The old walk is reproduced beside
    /// the new one — plan folders carrying a top-level <c>tasks/</c> directory, exactly as plan 33 §5.4
    /// describes it — and the two are compared, so the claim "this one covers the waved folders" is a
    /// difference this test computes rather than a sentence in a comment.
    ///
    /// <para>Four assertions, and each closes a distinct way this could go hollow: no committed script falls
    /// outside an enumerated plan folder (so the walk has no blind spot); the population is the whole
    /// committed corpus rather than 533 of it; every one of the five folders the old walk dropped is present,
    /// with at least the script count §5.4 measured; and the old walk provably drops all five, including the
    /// one carrying the positive control.</para>
    /// </summary>
    [Fact]
    public void TheSweepWalksEveryCommittedScript_IncludingTheWavedFolders()
    {
        SkipUnlessAvailable("HEAD");

        IReadOnlyList<string> tree = _workspaces.TreePaths("HEAD");
        string[] scripts =
        [
            .. tree.Where(p => p.StartsWith("docs/plans/", StringComparison.Ordinal)
                               && p.EndsWith(".ps1", StringComparison.Ordinal))
        ];

        IReadOnlyList<string> planFolders = _workspaces.PlanFolders("HEAD");

        // 1. No blind spot: every committed script under docs/plans/ lies inside an enumerated plan folder.
        //    An orphan would be a script this sweep can never evaluate, silently.
        string[] orphans = [.. scripts.Where(s => !planFolders.Any(f => IsUnder(s, f)))];
        Assert.True(orphans.Length == 0,
            "committed guardrail scripts that no enumerated plan folder contains, so the sweep would never " +
            "evaluate them:\n  " + string.Join("\n  ", orphans.Take(20)));

        // 2. The population is the committed corpus, not the 533 the hand-run sweep reached.
        Assert.True(scripts.Length >= 850,
            $"only {scripts.Length} committed .ps1 under docs/plans/; plan 33 §5.4 measured 850 and the " +
            "corpus only grows. A smaller number means the enumeration lost a folder.");

        // 3. The waved layout is IN. These are the folders whose tasks nest under wave-NN-*/tasks/.
        var wavedTaskPath = new Regex(@"/wave-\d\d-[^/]*/tasks/", RegexOptions.CultureInvariant);
        string[] wavedScripts = [.. scripts.Where(s => wavedTaskPath.IsMatch(s))];
        Assert.True(wavedScripts.Length > 0,
            "no committed script sits under a wave-NN-*/tasks/ path, so the waved layout is not being " +
            "enumerated at all — the exact defect plan 33 §5.4(b) records.");
        Assert.All(wavedScripts, s => Assert.Contains(planFolders, f => IsUnder(s, f)));

        // 4. The old walk, reproduced: top-level folders under docs/plans/ that carry a `tasks/` directory.
        string[] oldWalkFolders =
        [
            .. TopLevelFolders(tree).Where(f => tree.Any(p => p.StartsWith(f + "/tasks/", StringComparison.Ordinal)))
        ];
        string[] oldWalkScripts = [.. scripts.Where(s => oldWalkFolders.Any(f => IsUnder(s, f)))];

        // Each folder the old walk dropped is present here, with at least the count §5.4 measured.
        foreach ((string folder, int measured) in ExcludedFromTheOldWalk)
        {
            string prefix = "docs/plans/" + folder;
            int walked = scripts.Count(s => IsUnder(s, prefix));
            Assert.True(walked >= measured,
                $"'{folder}' contributes {walked} scripts to this sweep; plan 33 §5.4 measured {measured}.");
            Assert.DoesNotContain(prefix, oldWalkFolders);
        }

        int excluded = scripts.Count(s => ExcludedFromTheOldWalk.Any(e => IsUnder(s, "docs/plans/" + e.Folder)));
        Assert.Equal(scripts.Length - excluded, oldWalkScripts.Length);
        Assert.True(excluded >= 317,
            $"the old walk dropped {excluded} scripts; §5.4's table totals 317, and these folders are " +
            "finished plans whose script counts only grow.");

        // And the one that matters: the plan carrying the positive control was outside the old walk.
        Assert.DoesNotContain(Stage2, oldWalkFolders);
        Assert.Contains(Stage2, planFolders);
    }

    /// <summary>
    /// The five folders plan 33 §5.4(b) names, with the script counts it measured. Four are waved; the fifth
    /// nests an entire example plan under <c>example/example-plan</c>, so it matched neither layout.
    /// </summary>
    private static readonly (string Folder, int Measured)[] ExcludedFromTheOldWalk =
    [
        ("autonomous-mode-impl", 100),
        ("model-tiering-stage-2", 89),
        ("model-tiering-stage-3", 78),
        ("salvage-advice-provisioning", 39),
        ("09-preflight-first-class", 11)
    ];

    /// <summary>
    /// The table and the corpus must be the same set, in both directions. A plan folder with no row would be
    /// swept and then never asserted about; a row naming a folder that no longer exists would be a stale
    /// expectation that can never fail. Adding a plan folder therefore adds a row — plan 33 §8.5's stated
    /// reason for making the expectation a table rather than a single assertion.
    /// </summary>
    [Fact]
    public void TheExpectationTableCoversEveryPlanFolder()
    {
        SkipUnlessAvailable("HEAD");

        string[] onDisk = [.. _workspaces.PlanFolders("HEAD").Order(StringComparer.Ordinal)];
        string[] inTable = [.. Corpus.Select(r => r.Folder).Order(StringComparer.Ordinal)];

        Assert.Equal(inTable, onDisk);
        Assert.Equal(Corpus.Length, Corpus.Select(r => r.Folder).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Every pinned pre-run commit re-derived from git by the rule that produced it — <i>the commit that
    /// first authored a task in this plan</i> — so the table cannot quietly drift onto a convenient commit.
    /// A SHA that no longer resolves, or resolves to something else, fails here rather than turning a row
    /// into an unexplained zero.
    /// </summary>
    [Fact]
    public void ThePreRunCommitsAreTheBreakdownCommits()
    {
        SkipUnlessAvailable("HEAD");

        // This test is the one that DERIVES rather than reads, so it needs the whole history and not just
        // the presence of each pinned commit — see CorpusWorkspaces.HistoryIsComplete for the false red
        // this guard exists to prevent.
        Assert.SkipUnless(CorpusWorkspaces.HistoryIsComplete,
            "this checkout is SHALLOW, so 'the first commit adding a path' cannot be derived - every path " +
            "looks added at the tip, which turns a correct pin into a false failure. Run " +
            "`git fetch --unshallow` (or set `fetch-depth: 0`) to restore the corpus.");

        foreach (PlanRow row in Corpus)
        {
            string derived = _workspaces.FirstCommitAdding(row.Folder + "/*task.json");
            Assert.True(derived.StartsWith(row.PreRunCommit, StringComparison.Ordinal),
                $"'{row.Folder}' pins pre-run commit {row.PreRunCommit}, but the commit that first added " +
                $"a task.json to it is {derived}.");
        }
    }

    // ══ 3. The required non-zero, and the three rows around it ═══════════════════════════════════════

    /// <summary>
    /// <b>The row that proves the sweep can fail in the FIRING direction.</b> At <c>544f7d5</c> the plan is
    /// closed, the SSOT is tracked and carries zero occurrences of <c>tierSource</c>, and none of the plan's
    /// 19 task manifests declares that path. Exactly one finding, naming both facts an author would need.
    ///
    /// <para>Asserted here by name as well as in the table because it is the single load-bearing measurement
    /// in this file: a sweep that expects zero everywhere cannot distinguish a working check from a mute one,
    /// and everything else in <see cref="Corpus"/> is a zero.</para>
    /// </summary>
    [Fact]
    public void ThePositiveControl_FiresExactlyOnce_At544f7d5()
    {
        SkipUnlessAvailable(Stage2Firing);

        Diagnostic finding = Assert.Single(Findings(Stage2, Stage2Firing));

        Assert.Equal(Gr2060, finding.Code);
        Assert.Equal(DiagnosticSeverity.Error, finding.Severity);
        Assert.Contains(Witness, finding.Message, StringComparison.Ordinal);
        Assert.Contains(SsotPath, finding.Message, StringComparison.Ordinal);
        Assert.EndsWith("03-dor-section-6-contract-landed.ps1", finding.Path, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The row that proves the sweep can fail in the SILENCE direction, twice over.</b> The same gate
    /// script, whose blob is byte-identical at all three commits, goes quiet for two different reasons — and
    /// a check that had learned to fire on the clause's text rather than on the tree would fail both.
    ///
    /// <list type="bullet">
    ///   <item><c>1b8e681</c>: wave 2 holds zero tasks, so <c>PlanIsClosed</c> is false (condition 10).</item>
    ///   <item><c>5bd29da</c>: a task now declares the SSOT path (condition 8) — the SSOT bytes have not
    ///   moved, so this is the only difference from the firing row.</item>
    ///   <item><c>HEAD</c>: the SSOT carries <c>tierSource</c> now, so the requirement is satisfied
    ///   (condition 5).</item>
    /// </list>
    /// </summary>
    [Fact]
    public void TheSameScript_IsSilent_AtTheThreeCommitsWhereItCannotFire()
    {
        SkipUnlessAvailable(Stage2PreRun, Stage2Owned, "HEAD");

        Assert.Empty(Findings(Stage2, Stage2PreRun));
        Assert.Empty(Findings(Stage2, Stage2Owned));
        Assert.Empty(Findings(Stage2, "HEAD"));
    }

    /// <summary>
    /// <b>Plan 33 §11 prohibition 5, pinned as a test rather than as a sentence.</b> The prohibition is not
    /// "do not weaken the sweep" in the abstract — it is that a run which flattens this table to a blanket
    /// zero, or re-baselines it to "≤ N findings", has inverted the gate while leaving every other test
    /// green. So the table's own shape is asserted: at least one row demands a finding, that row is the
    /// recovered positive control, and it demands an exact count rather than a floor.
    /// </summary>
    [Fact]
    public void TheExpectationIsNotABlanketZero()
    {
        PinnedRow[] nonZero = [.. Pinned.Where(r => r.Expected > 0)];

        Assert.NotEmpty(nonZero);
        PinnedRow required = Assert.Single(nonZero);
        Assert.Equal(Stage2, required.Folder);
        Assert.Equal(Stage2Firing, required.Commit);
        Assert.Equal(1, required.Expected);

        // Both directions of the pair are present, or the non-zero alone proves only that SOMETHING fires.
        Assert.Contains(Pinned, r => r.Folder == Stage2 && r.Commit == Stage2Owned && r.Expected == 0);
        Assert.Contains(Pinned, r => r.Folder == Stage2 && r.Commit == "HEAD" && r.Expected == 0);
    }

    /// <summary>
    /// Where <see cref="Corpus"/> and <see cref="Pinned"/> name the same (plan, commit) pair they must expect
    /// the same count. <see cref="Rows"/> emits such a pair once — xUnit refuses two theory cases with
    /// identical arguments — so without this the losing table could quietly carry a number nothing ever runs.
    /// </summary>
    [Fact]
    public void TheTwoTablesDoNotContradictEachOther()
    {
        foreach ((string folder, string commit, int expected) in AllRows())
        {
            int[] all =
            [
                .. AllRows().Where(r => r.Folder == folder && r.Commit == commit).Select(r => r.Expected)
            ];

            Assert.All(all, other => Assert.Equal(expected, other));
        }
    }

    // ══ the check, driven two ways ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every GR2060 finding for one plan folder at one commit, computed TWICE and asserted to agree: once by
    /// calling <c>ProducerCoverage.Validate</c> directly, and once through the real
    /// <see cref="PlanValidator.Validate"/> — plan 33 §8.4's anti-tautology pin, applied to every row of the
    /// sweep rather than only to the hand-built fixtures. A check that exists but is not wired into the
    /// composition root passes the first and fails the second, which is #382 in miniature.
    /// </summary>
    private IReadOnlyList<Diagnostic> Findings(string planFolder, string commit)
    {
        CorpusPlan subject = _workspaces.Materialize(planFolder, commit);

        // The row is not vacuous. Almost every expectation in this table is a ZERO, and an empty plan folder
        // — an archive that extracted nothing, a loader that dropped every task — produces that same zero
        // while measuring nothing at all. Condition 1 reads PowerShell script guardrails, so a row with none
        // is a row where GR2060 was never asked a question, and it must say so rather than pass.
        int scripts = PlanValidator.FourFolderScriptGuardrails(subject.Plan)
            .Count(g => g.Path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase));
        Assert.True(scripts > 0,
            $"'{planFolder}' at {commit} presented no PowerShell script guardrail, so its expectation is " +
            "satisfied by a plan the sweep never actually read.");

        var direct = new List<Diagnostic>();
        ProducerCoverage.Validate(subject.Plan, subject.Probe, direct);

        List<Diagnostic> wired =
        [
            .. new PlanValidator(
                    FakeExecutableProbe.All,
                    BannedPatternRegistry.Load(),
                    NullScriptSyntaxProbe.Instance,
                    subject.Probe)
                .Validate(subject.Plan)
                .Where(d => d.Code == Gr2060)
        ];

        Assert.Equal(direct, wired);
        return wired;
    }

    private static string Detail(IReadOnlyList<Diagnostic> findings) =>
        findings.Count == 0
            ? string.Empty
            : "\n" + string.Join("\n", findings.Select(f => $"  {f.Code} {f.Path}: {f.Message}"));

    private static bool IsUnder(string path, string folder) =>
        path.StartsWith(folder + "/", StringComparison.Ordinal);

    /// <summary>Every immediate child directory of <c>docs/plans/</c> — the level the old walk enumerated.</summary>
    private static IEnumerable<string> TopLevelFolders(IEnumerable<string> tree) =>
        tree.Where(p => p.StartsWith("docs/plans/", StringComparison.Ordinal))
            .Select(p => p.IndexOf('/', "docs/plans/".Length) is var slash && slash > 0 ? p[..slash] : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// A shallow clone — <c>actions/checkout</c> defaults to <c>fetch-depth: 1</c> — holds none of the
    /// pinned commits, and a sweep that silently evaluated nothing would report the same green as a sweep
    /// that evaluated everything. Skip with the reason and the fix instead.
    /// </summary>
    private void SkipUnlessAvailable(params string[] commits)
    {
        Assert.SkipUnless(CorpusWorkspaces.GitIsUsable,
            "git is not runnable here, so the corpus cannot be read at any commit.");

        foreach (string commit in commits)
        {
            Assert.SkipUnless(_workspaces.CommitIsPresent(commit),
                $"commit {commit} is not in this checkout — a shallow clone cannot read it. Run " +
                "`git fetch --unshallow` (or set `fetch-depth: 0`) to restore the corpus.");
        }
    }
}

/// <summary>
/// One plan folder materialized as it stood at one commit: the loaded plan, and the tracked-file oracle for
/// that same commit.
/// </summary>
/// <param name="Plan">The plan, loaded from the throwaway workspace.</param>
/// <param name="Probe">Condition 6's oracle, answering for the commit rather than for the working tree.</param>
public sealed record CorpusPlan(PlanDefinition Plan, IGitTrackedFileProbe Probe);

/// <summary>
/// Builds — and caches for the lifetime of the test class — one throwaway workspace per (plan, commit) pair
/// the sweep needs. Shared as a class fixture because the sweep asks about the same pair from more than one
/// test, and <c>git archive</c> of a hundred-task plan folder is the expensive part.
///
/// <para><b>Everything here reads git, never the working tree.</b> 364 of the 1,271 <c>.ps1</c> on disk under
/// <c>docs/plans/</c> are gitignored generated <c>containment-hook.ps1</c> copies; only 850 are committed. A
/// disk walk would sweep 364 copies of one generated hook and would hand the sweep a population no
/// <c>git show &lt;commit&gt;:&lt;path&gt;</c> can reach.</para>
/// </summary>
public sealed class CorpusWorkspaces : IDisposable
{
    /// <summary>
    /// The repository this test file was compiled from. Every git call runs there rather than in the process
    /// working directory, so the reads do not depend on where the runner was launched.
    /// </summary>
    private static readonly string RepositoryRoot =
        Path.GetFullPath(Path.Combine(TestPaths.ProjectDir, "..", ".."));

    /// <summary>Can git run here at all? Its absence skips the sweep rather than passing it vacuously.</summary>
    internal static readonly bool GitIsUsable = RunGit("--version").ExitCode == 0;

    /// <summary>
    /// False in a SHALLOW clone. <see cref="CommitIsPresent"/> is not a sufficient guard for anything that
    /// derives an answer from the whole history of a path: a truncated history does not merely hide old
    /// commits, it MOVES the answer to the tip, because every path looks added there. That is a false red
    /// rather than a silence, and a false red dead-ends work that was already right.
    ///
    /// <para>Measured, not hypothetical: CI ran <c>actions/checkout@v6</c> at its default
    /// <c>fetch-depth: 1</c> and reported 04-dogfood-cost-cap's pre-run commit as the plan-33 MERGE
    /// (<c>5124857</c>) rather than the correct <c>8012572</c>, failing master for four consecutive runs
    /// against a table that was right the whole time. The fix is <c>fetch-depth: 0</c> in
    /// <c>.github/workflows/ci.yml</c>; this flag is what stops the same false red returning anywhere
    /// else a shallow clone is used, by turning it back into an honest skip.</para>
    /// </summary>
    internal static readonly bool HistoryIsComplete =
        GitIsUsable && !string.Equals(
            RunGit("rev-parse", "--is-shallow-repository").Stdout.Trim(), "true", StringComparison.Ordinal);

    /// <summary>
    /// Short, because these workspaces hold repo paths up to 174 characters and Windows still has a 260-char
    /// habit; a chatty temp root would push the deepest plan folder over it.
    /// </summary>
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gc-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly ConcurrentDictionary<string, CorpusPlan> _plans = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _trees = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, bool> _commits = new(StringComparer.Ordinal);

    private int _next;

    public CorpusWorkspaces() => Directory.CreateDirectory(_root);

    public void Dispose() => DeleteTree(_root);

    /// <summary>Is this commit in the checkout? False in a shallow clone, which skips rather than fails.</summary>
    internal bool CommitIsPresent(string commit) =>
        _commits.GetOrAdd(commit, static c => RunGit("cat-file", "-e", c + "^{commit}").ExitCode == 0);

    /// <summary>Every path in the tree at <paramref name="commit"/>, cached — history does not move.</summary>
    internal IReadOnlyList<string> TreePaths(string commit) =>
        _trees.GetOrAdd(commit, static c =>
        {
            (int exitCode, string stdout, string stderr) = RunGit("ls-tree", "-r", "--name-only", c);
            Assert.True(exitCode == 0, $"git ls-tree {c} exited {exitCode}: {stderr}");
            return [.. stdout.Split('\n').Select(l => l.Trim('\r', ' ')).Where(l => l.Length > 0)];
        });

    /// <summary>
    /// The two roots plan 33 §8.5 names as the population: <i>every <c>.ps1</c> under <c>docs/plans/</c> …
    /// plus <c>examples/</c></i>. The loader's own fixtures under <c>tests/…/TestData/</c> also hold a
    /// <c>guardrails.json</c> each, but they are hand-built malformed shapes authored to make the LOADER
    /// fail, so sweeping them would measure this suite rather than the corpus.
    /// </summary>
    private static readonly string[] CorpusRoots = ["docs/plans/", "examples/"];

    /// <summary>
    /// Every plan folder at <paramref name="commit"/> — every directory holding a <c>guardrails.json</c>,
    /// which is what the loader itself treats as a plan. Flat, waved and nested-example layouts all fall out
    /// of that one rule, which is precisely what the layout-shaped walk in plan 33 §5.4 did not.
    /// </summary>
    internal IReadOnlyList<string> PlanFolders(string commit) =>
    [
        .. TreePaths(commit)
            .Where(p => p.EndsWith("/guardrails.json", StringComparison.Ordinal)
                        && CorpusRoots.Any(r => p.StartsWith(r, StringComparison.Ordinal)))
            .Select(p => p[..^"/guardrails.json".Length])
    ];

    /// <summary>
    /// The first commit that ADDS a file matching <paramref name="pathspec"/>, abbreviated as git
    /// abbreviates it. Git's default pathspec wildcards match <c>/</c>, so a trailing <c>*task.json</c>
    /// reaches a task manifest at any depth — flat or waved.
    /// </summary>
    internal string FirstCommitAdding(string pathspec)
    {
        (int exitCode, string stdout, string stderr) =
            RunGit("log", "--reverse", "--diff-filter=A", "--format=%h", "--", pathspec);
        Assert.True(exitCode == 0, $"git log for '{pathspec}' exited {exitCode}: {stderr}");

        string[] commits = [.. stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];
        Assert.True(commits.Length > 0, $"no commit in this checkout adds '{pathspec}'.");
        return commits[0];
    }

    /// <summary>
    /// The plan folder at <paramref name="commit"/>, rebuilt in a throwaway workspace and loaded.
    ///
    /// <para>Two <c>git archive</c> passes. The first extracts the plan folder itself, which is enough to
    /// LOAD the plan and therefore enough to learn where its <c>workspace</c> resolves to. The second
    /// extracts the workspace files the plan's gates could be reading — every path in the commit's tree whose
    /// workspace-relative form appears verbatim in one of the plan's own <c>.ps1</c> scripts.</para>
    ///
    /// <para><b>That second filter is a superset of what the extractor can name, and it has to be.</b>
    /// <see cref="ProducerCoverage"/> only ever names a path it read out of a quoted literal, so the path is
    /// a substring of the script text by construction — both separators are tried, because the extractor
    /// normalises <c>\</c> to <c>/</c>. A file left unmaterialized would read as unreadable and go silent,
    /// and false silence is the failure this sweep exists to catch, so the filter errs wide on purpose. (Its
    /// one blind spot is a path containing a doubled quote, which no guardrail in this corpus writes.)</para>
    /// </summary>
    internal CorpusPlan Materialize(string planFolder, string commit) =>
        _plans.GetOrAdd(planFolder + "@" + commit, _ => Build(planFolder, commit));

    private CorpusPlan Build(string planFolder, string commit)
    {
        string workspaceRoot = Path.Combine(_root, Interlocked.Increment(ref _next).ToString());
        Directory.CreateDirectory(workspaceRoot);
        Extract(commit, [planFolder], workspaceRoot);

        string planDirectory = Path.Combine(
            workspaceRoot, planFolder.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(planDirectory),
            $"'{planFolder}' does not exist at {commit}, so the sweep cannot evaluate that row.");

        PlanLoadResult loaded = new PlanLoader().Load(planDirectory);
        Assert.True(loaded.Plan is not null,
            $"the loader could not build a plan from '{planFolder}' at {commit}, so GR2060 was never asked " +
            "anything about it. That is a hole in the sweep, not a zero:\n  " +
            string.Join("\n  ", loaded.Diagnostics.Select(d => d.Code + " " + d.Message)));
        PlanDefinition plan = loaded.Plan!;

        // Where the plan's workspace lands inside this throwaway tree, as a repo-relative prefix. Almost
        // always the repository root (""), but the preflight example plan sets `workspace: ".."`.
        string workspacePrefix = Path.GetRelativePath(workspaceRoot, plan.Workspace)
            .Replace('\\', '/')
            .Trim('/');
        workspacePrefix = workspacePrefix is "" or "." ? string.Empty : workspacePrefix + "/";
        Assert.False(workspacePrefix.StartsWith("..", StringComparison.Ordinal),
            $"'{planFolder}' resolves its workspace outside the repository, which this sweep cannot rebuild.");

        Extract(commit, WorkspaceFilesTheGatesCouldRead(planDirectory, planFolder, commit, workspacePrefix),
            workspaceRoot);

        return new CorpusPlan(plan, new CommitTreeGitTrackedFileProbe(TreePaths(commit), workspacePrefix));
    }

    private IReadOnlyList<string> WorkspaceFilesTheGatesCouldRead(
        string planDirectory, string planFolder, string commit, string workspacePrefix)
    {
        var scripts = new StringBuilder();
        foreach (string script in Directory.EnumerateFiles(planDirectory, "*.ps1", SearchOption.AllDirectories))
        {
            scripts.Append(File.ReadAllText(script)).Append('\n');
        }

        string text = scripts.ToString();
        var wanted = new List<string>();
        foreach (string repoPath in TreePaths(commit))
        {
            if (repoPath.StartsWith(planFolder + "/", StringComparison.Ordinal)
                || !repoPath.StartsWith(workspacePrefix, StringComparison.Ordinal))
            {
                continue;   // already extracted, or outside the workspace and therefore unnameable
            }

            string relative = repoPath[workspacePrefix.Length..];
            if (text.Contains(relative, StringComparison.Ordinal)
                || text.Contains(relative.Replace('/', '\\'), StringComparison.Ordinal))
            {
                wanted.Add(repoPath);
            }
        }

        return wanted;
    }

    /// <summary>
    /// Extract <paramref name="paths"/> at <paramref name="commit"/> into <paramref name="destination"/>.
    /// <c>git archive</c> because the alternative — one <c>git show</c> per file — is a child process per
    /// file, and this sweep touches a few thousand of them. Chunked so no command line grows unbounded, and
    /// the archive itself is written OUTSIDE the destination so it never becomes part of the workspace.
    /// </summary>
    private void Extract(string commit, IReadOnlyList<string> paths, string destination)
    {
        const int chunk = 100;
        for (int i = 0; i < paths.Count; i += chunk)
        {
            string[] arguments =
            [
                "archive", "--format=zip", "-o", Path.Combine(_root, "chunk.zip"), commit,
                .. paths.Skip(i).Take(chunk)
            ];

            (int exitCode, _, string stderr) = RunGit(arguments);
            Assert.True(exitCode == 0, $"git archive {commit} exited {exitCode}: {stderr}");

            ZipFile.ExtractToDirectory(Path.Combine(_root, "chunk.zip"), destination, overwriteFiles: true);
            File.Delete(Path.Combine(_root, "chunk.zip"));
        }
    }

    /// <summary>
    /// Condition 6's oracle for a commit the working tree does not hold: a path was tracked at that commit
    /// exactly when it is in that commit's tree. This is git's own answer — the same fact
    /// <c>git ls-files --error-unmatch</c> reports for HEAD — not a stand-in for one. The production
    /// <see cref="GitLsFilesProbe"/> is anchored to the ambient repository and can only answer about the
    /// working tree, so it cannot be asked this question at all; its own truthfulness is pinned against a
    /// real index by <c>ProducerCoverageTests.Silent_WhenTheFileIsNotGitTracked</c>.
    /// </summary>
    private sealed class CommitTreeGitTrackedFileProbe(IReadOnlyList<string> tree, string workspacePrefix)
        : IGitTrackedFileProbe
    {
        private readonly HashSet<string> _tracked = new(tree, StringComparer.Ordinal);

        public IReadOnlyDictionary<string, bool?> AreTracked(IReadOnlyList<string> workspaceRelativePaths)
        {
            var answers = new Dictionary<string, bool?>(StringComparer.Ordinal);
            foreach (string path in workspaceRelativePaths)
            {
                answers[path] = _tracked.Contains(workspacePrefix + path);
            }

            return answers;
        }
    }

    /// <summary>
    /// Run git against the repository. <c>GIT_DIR</c> and <c>GIT_WORK_TREE</c> are stripped from the child's
    /// environment rather than trusted: <c>ProducerCoverageTests</c> sets both process-wide for the duration
    /// of its real-seam pin, and an overlap would silently re-point these reads at a throwaway repository —
    /// which would answer every question about the corpus with a wrong but plausible-looking zero.
    /// </summary>
    private static (int ExitCode, string Stdout, string Stderr) RunGit(params string[] arguments)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        psi.Environment.Remove("GIT_DIR");
        psi.Environment.Remove("GIT_WORK_TREE");

        try
        {
            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return (-1, string.Empty, "git could not be started.");
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdout, stderr);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (-1, string.Empty, e.Message);
        }
        catch (IOException e)
        {
            return (-1, string.Empty, e.Message);
        }
    }

    /// <summary>
    /// Best-effort recursive delete that first clears read-only attributes — <c>git archive</c> preserves the
    /// executable bit but not read-only flags, while extraction can still leave attributes a plain
    /// <see cref="Directory.Delete(string, bool)"/> refuses.
    /// </summary>
    private static void DeleteTree(string root)
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
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
