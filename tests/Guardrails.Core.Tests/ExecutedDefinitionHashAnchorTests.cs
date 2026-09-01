using System.Text;
using System.Text.RegularExpressions;

namespace Guardrails.Core.Tests;

/// <summary>
/// <b>#556 · plan 32 §9 — the repo-lifetime tripwire for the executed-definition pin.</b>
///
/// <para><b>Why this is a committed test and not a plan-folder guardrail.</b> §9 records three successive
/// drafts of this check and each one's defeat; all three were plan-folder guardrails, which evaporate the
/// moment the run that carried them ends. The hazard they were aimed at is Risk 6 — <i>"a seventh site
/// added later by someone who has not read this document"</i> — and that hazard is repo-lifetime. This
/// file outlives the run.</para>
///
/// <para><b>It is GREEN ON ARRIVAL, and that is correct.</b> Stages 3-5 have already produced the state it
/// anchors, so there is no red half to demand of it. The anti-tautology burden therefore sits on the
/// authoring stage's guardrail 03, which reads this file's SHAPE: a passing anchor test cannot tell anyone
/// whether it anchored a SET or a number, and the number is exactly how draft 3 was defeated.</para>
///
/// <para><b>Why a test reads <c>src/</c> as text.</b> The properties below have no runtime observable at
/// all. <i>"Every stamped hash comes from the pin"</i> is a fact about which expression is written where,
/// and a reflection test cannot see which MEMBER a call sits in — which is the whole discriminating power
/// here, since three of the four surviving <c>Scheduler.cs</c> sites are correct and a fourth would be the
/// defect. The repo's anchor idiom (<see cref="SeamDoctrineAnchorTests"/>,
/// <c>ModelAppropriatenessDoctrineAnchorTests</c>) is copied for the row array, the repo-root resolution
/// and the self-hygiene fact; the SUBJECT is new — those two read markdown skill text, and no test in this
/// repo read the C# under <c>src/</c> as text before this one.</para>
///
/// <para><b>The four anchors.</b> (1) the enumerated SET of <c>TaskDefinitionHash.Compute</c> call sites,
/// by file and enclosing member, asserted in BOTH directions — never a count; (2) the declaration shape,
/// so a capture cannot compute itself lazily; (3) no fallback to disk; (4) no identity-rebinding clone.
/// Each closes a hole no behavioral pin reaches.</para>
///
/// <para><b>If one of these fails, do not delete the row.</b> Either the design genuinely moved — in which
/// case re-point the anchor in the same change that moved it, and check
/// <c>docs/plans/32-executed-definition-hash.md</c> §4.3/§5.2/§9 still describe what the code now does —
/// or a write site started recomputing the hash from current disk again, which is the silent false green
/// this test exists to catch.</para>
/// </summary>
public sealed class ExecutedDefinitionHashAnchorTests
{
    // ---- the subject files, repo-relative and forward-slashed ---------------------------------------
    // Two of the enumerated sites live in Guardrails.Cli rather than Guardrails.Core. A set anchored only
    // over Core would miss both, which is a real omission the (file, member) form catches and a count
    // never could. This test project references Guardrails.Core only — every file below is READ AS TEXT,
    // never compiled against, so the Cli rows cost no project reference.
    private const string SchedulerFile = "src/Guardrails.Core/Execution/Scheduler.cs";
    private const string DryRunFile = "src/Guardrails.Cli/Commands/DryRun.cs";
    private const string DriftProbeFile = "src/Guardrails.Cli/Commands/DefinitionDriftProbe.cs";
    private const string RunResetFile = "src/Guardrails.Core/State/RunReset.cs";
    private const string WaveHashFile = "src/Guardrails.Core/Journal/WaveDefinitionHash.cs";
    private const string PlanLoaderFile = "src/Guardrails.Core/Loading/PlanLoader.cs";
    private const string AttemptJournalerFile = "src/Guardrails.Core/Execution/AttemptJournaler.cs";
    private const string TaskExecutorFile = "src/Guardrails.Core/Execution/TaskExecutor.cs";
    private const string TaskNodeFile = "src/Guardrails.Core/Model/TaskNode.cs";
    private const string WaveNodeFile = "src/Guardrails.Core/Model/WaveNode.cs";

    /// <summary>The full-surface aggregate pin the journal records (plan 32 §5.2).</summary>
    private const string PinHash = "DefinitionHashAtLoad";

    /// <summary>The unfiltered per-file map the settle-time gate diffs (plan 32 §5.2).</summary>
    private const string PinFiles = "DefinitionFilesAtLoad";

    /// <summary>
    /// <b>Anchor 1's set</b> — every <c>TaskDefinitionHash.Compute</c> call site in <c>src/</c>, by file
    /// and enclosing member, held as a plain tuple array so the hygiene fact can walk it without depending
    /// on how <c>TheoryData</c> enumerates.
    ///
    /// <para><b>A BARE COUNT IS FORBIDDEN, and the ban is the specification.</b> §9 on the third defeated
    /// draft: <i>"a bare count is a tautology magnet: an agent that meets a wrong number under retry
    /// pressure runs the grep and writes down whatever it says — installing the exact anti-pattern in the
    /// guardrail whose job is to prevent one."</i> The number that draft used was 6 against a true 8. A set
    /// is self-documenting, fails informatively ("Scheduler.SettleAsync is calling Compute again"), and
    /// cannot be satisfied by writing down whatever the grep says.</para>
    ///
    /// <para><b>Why there is a ninth row, and why it is not a loosening.</b> §9's table enumerates EIGHT —
    /// it was derived as "§4.3's twelve, minus the four that become pins", and §4.3 was written against the
    /// PRE-fix tree, where the loader's capture did not exist. §5.2 then prescribes that capture in as many
    /// words (<c>return node with { DefinitionHashAtLoad = TaskDefinitionHash.Compute(node) };</c>), so on
    /// the post-fix tree there are nine. Row 9 is that capture, enumerated by file and member exactly like
    /// the other eight rather than skipped by a path filter — so a SECOND <c>Compute</c> added to
    /// <c>PlanLoader.cs</c> in some other member still fails direction (2), and the set stays a true
    /// two-way equality with <c>src/</c> instead of a set with a hole in it.</para>
    /// </summary>
    private static readonly (string File, string Member, string Why)[] ComputeSiteRows =
    [
        // ---- the eight surviving READS: each recomputes from CURRENT DISK, deliberately (§4.3) --------
        (
            SchedulerFile, "DetectDefinitionDrift",
            "READ — the resume drift pre-pass. It must recompute from current disk: pinning it would make the pre-pass compare a pin against a pin and check nothing (§5.8 P6a)."
        ),
        (
            SchedulerFile, "BuildResolvedTasks",
            "READ — the Part C audit rows. Reports recorded -> CURRENT for a rebuilt descendant, which is only meaningful against disk."
        ),
        (
            SchedulerFile, "ConsumePendingAnswers",
            "READ — the answer-file anti-stale key. §4.4: both sides of that binding read disk and MUST stay on the same side; this is the one surface left keyed on current disk after this plan."
        ),
        (
            SchedulerFile, "ClassifyTaskGateAsync",
            "A durable WRITE of a disk value, deliberately (§4.3 row 12, §4.4) — the escalation record's anti-stale binding, whose consumption half (R3) also reads disk."
        ),
        (
            DryRunFile, "IsDrifted",
            "READ — the --dry-run preview. Advisory, never the gate; a preview of what a real run would find on disk."
        ),
        (
            DriftProbeFile, "Evaluate",
            "READ — the pre-run probe. Note it FEEDS a write (RecordDriftAccepted, §4.2's W6), which is why any enumeration built by grepping for Compute misses W6 by construction."
        ),
        (
            RunResetFile, "SafeComputeHash",
            "READ — the reset audit rows, degrading to a sentinel on an unreadable file. Audit only, never the gate."
        ),
        (
            WaveHashFile, "Compute",
            "READ — the disk form's task fold. §5.4 keeps Compute(wave) unchanged for every wave-level READ and adds a PINNED form beside it for the single wave WRITE."
        ),

        // ---- and the one CAPTURE: the pin's source, created by this plan (§5.2) -----------------------
        (
            PlanLoaderFile, "LoadTask",
            "The CAPTURE, not a settle-time read — the single site where the pin is taken, eagerly, from the bytes the loader has just read. src/ contains exactly one `new TaskNode` and this is it, which is why the pin's lifetime is the TaskNode's lifetime and there is no re-pin hook list to forget (§5.2)."
        )
    ];

    /// <summary>
    /// <b>The four files that must call the hasher NOWHERE</b> (§9: <i>"And zero in AttemptJournaler.cs,
    /// TaskExecutor.cs, TaskNode.cs, WaveNode.cs"</i>). Direction (2) below would already catch a call in
    /// any of them, because none of them carries a row; these are asserted separately so the regression
    /// fails with the reason attached rather than as an anonymous unmapped site.
    /// </summary>
    private static readonly (string File, string Why)[] HashFreeFileRows =
    [
        (
            AttemptJournalerFile,
            "W1, the serial-mode settle. Stage 4 replaced its Compute(task) with task.DefinitionHashAtLoad; a Compute here is the issue's own defect returning to the site the issue named."
        ),
        (
            TaskExecutorFile,
            "W4, the `revalidate` synthetic success. A no-op in practice (§5.8 P4) and fixed anyway, because an exception carved out for the site that 'cannot' hit the window is how the fifth site gets written the old way later."
        ),
        (
            TaskNodeFile,
            "The model type. A property that cannot name the hash function cannot compute it lazily in any syntax — which is what defeats the expression-bodied form that beat draft 2."
        ),
        (
            WaveNodeFile,
            "The model type, and the one milestone B will touch. It carries no capture yet; when the wave twin lands, this row is what keeps its pin a plain auto-property too."
        )
    ];

    // ================================================================================================
    //  Anchor 1 — the enumerated SET of TaskDefinitionHash.Compute call sites, BOTH directions
    // ================================================================================================

    /// <summary>The enumerated call-site set as xUnit theory data, so each site is its own test case.</summary>
    public static TheoryData<string, string, string> ComputeSites()
    {
        TheoryData<string, string, string> data = [];
        foreach ((string file, string member, string why) in ComputeSiteRows)
        {
            data.Add(file, member, why);
        }

        return data;
    }

    /// <summary>
    /// <b>Direction (1)</b> — every enumerated row is still there. This is the easy half and useless on its
    /// own; direction (2) below is the one that carries Risk 6.
    /// </summary>
    [Theory]
    [MemberData(nameof(ComputeSites))]
    public void TheEnumeratedSiteStillCallsTheHasher(string file, string member, string why)
    {
        SourceFile source = SourceOf(file);
        IReadOnlyList<string> callers = MembersCallingCompute(source);

        Assert.True(
            callers.Contains(member, StringComparer.Ordinal),
            $"""
             #556 CALL SITE LOST — {file} no longer calls TaskDefinitionHash.Compute inside '{member}'.

               role : {why}

             The members in that file that DO call it: {(callers.Count == 0 ? "(none)" : string.Join(", ", callers))}

             Either the site moved (re-point this row in the SAME change that moved it, and check plan 32
             §4.3's taxonomy still describes what the code does), or a read site was quietly converted to
             read the load-time pin. The second is the cheapest way to silence this plan's own drift
             reporting: §5.8's P6 was RESPECIFIED precisely because the obvious form of that pin passes
             with a fully-pinned read site.

             Matching tolerates the `Journal.` prefix and whitespace around the dots, so a re-qualification
             or a re-wrap cannot cause this.
             """);
    }

    /// <summary>
    /// <b>Direction (2) — the one that catches Risk 6's seventh site</b>, added later by someone who has
    /// not read the design. Walks every <c>.cs</c> file under <c>src/</c>, attributes each
    /// <c>TaskDefinitionHash.Compute</c> occurrence to its enclosing member, and fails naming the offending
    /// <c>(file, member)</c> for anything the set does not already know about.
    ///
    /// <para>This is the direction a count can never have: <c>Assert.Equal(8, …)</c> is satisfied by ANY
    /// eight sites, including seven correct ones and one new write site that recomputes from disk.</para>
    /// </summary>
    [Fact]
    public void EveryHasherCallInSrcMapsToAnEnumeratedSite()
    {
        HashSet<string> known = [.. ComputeSiteRows.Select(r => $"{r.File}#{r.Member}")];
        var unmapped = new List<string>();

        foreach (SourceFile source in SrcTree)
        {
            foreach (Match match in ComputeInvocation.Matches(source.Code))
            {
                int line = LineIndexOf(source.Code, match.Index);
                string member = EnclosingMember(source.Lines, line);
                if (!known.Contains($"{source.RelativePath}#{member}"))
                {
                    unmapped.Add($"{source.RelativePath}:{line + 1} — inside '{member}'");
                }
            }
        }

        Assert.True(
            unmapped.Count == 0,
            $"""
             #556 UNENUMERATED CALL SITE — src/ calls TaskDefinitionHash.Compute somewhere the plan-32 set
             does not know about:

               {string.Join($"{Environment.NewLine}  ", unmapped)}

             This is Risk 6 exactly: "a seventh site added later by someone who has not read this document".
             Read docs/plans/32-executed-definition-hash.md §4.3 and decide which side the new site is on.

               - Is it a WRITE of the EXECUTED-DEFINITION RECORD (a journal `definitionHash`, a
                 `Guardrails-Task-Hash:` trailer)? Then it must NOT call Compute at all — it stamps
                 `task.{PinHash}`, with no `?? Compute(task)` fallback, which §5.2 calls the cheapest wrong
                 implementation of the entire plan.
               - Is it a READ, or a durable write of a DIFFERENT record with its own contract (§4.3 row 12,
                 §4.4)? Then it legitimately recomputes from current disk — add a row to ComputeSiteRows in
                 the SAME change, saying which and why.

             Do not delete a row or narrow the walk to go green. The rule is: "Reads recompute from disk.
             Writes of the executed-definition record read the pin."
             """);
    }

    /// <summary>The four files that must never call the hasher, as theory data.</summary>
    public static TheoryData<string, string> HashFreeFiles()
    {
        TheoryData<string, string> data = [];
        foreach ((string file, string why) in HashFreeFileRows)
        {
            data.Add(file, why);
        }

        return data;
    }

    /// <summary>
    /// The zero-occurrence half of §9's set, stated positively so a regression at one of the four write
    /// sites this plan CLOSED fails with its own reason rather than as an anonymous unmapped site.
    /// </summary>
    [Theory]
    [MemberData(nameof(HashFreeFiles))]
    public void TheseFilesCallTheHasherNowhere(string file, string why)
    {
        SourceFile source = SourceOf(file);
        var hits = new List<string>();

        foreach (Match match in ComputeInvocation.Matches(source.Code))
        {
            int line = LineIndexOf(source.Code, match.Index);
            hits.Add($"{source.RelativePath}:{line + 1} — inside '{EnclosingMember(source.Lines, line)}'");
        }

        Assert.True(
            hits.Count == 0,
            $"""
             #556 THE DEFECT IS BACK — {file} computes a definition hash from CURRENT DISK:

               {string.Join($"{Environment.NewLine}  ", hits)}

               why this file must not : {why}

             The bytes on disk at settle are not the bytes the attempt executed. Stamping them records a
             certificate for something that never ran, and — because the recompute and the next resume's
             recompute agree — no resume can ever flag it. That is the silent false green of #556.

             Stamp `task.{PinHash}` instead. There is no fallback to disk, at any write site, ever: a null
             pin records a null hash, which is the state SSOT §7.2 already defines and already handles.
             """);
    }

    // ================================================================================================
    //  Anchor 2 — the declaration shape
    // ================================================================================================

    /// <summary>
    /// <b>The two model types name no hasher, and every load-time capture is a bodiless auto-property.</b>
    ///
    /// <para>This is what defeats the form that beat draft 2. That draft asked only that the write-site
    /// expressions read <c>.DefinitionHashAtLoad</c> — satisfied verbatim by
    /// <c>public string DefinitionHashAtLoad =&gt; TaskDefinitionHash.Compute(this);</c>, with every site
    /// reading the identifier and the defect 100% intact. A property that cannot NAME the hash function
    /// cannot compute it lazily in any syntax, so the name-level ban is the load-bearing half and the
    /// auto-property shape is the belt beside it.</para>
    ///
    /// <para><b>Comments are stripped first, and that is not incidental.</b> Both files carry
    /// <c>&lt;see cref="Journal.WaveDefinitionHash"/&gt;</c>-style doc references to the hashers today —
    /// correct, useful prose — and an unstripped check would false-red a correct file on arrival, which is
    /// how a check gets deleted rather than fixed.</para>
    /// </summary>
    [Fact]
    public void TheModelTypesNameNoHasherAndCaptureOnlyInBodilessAutoProperties()
    {
        var failures = new List<string>();
        string[] modelFiles = [TaskNodeFile, WaveNodeFile];
        string[] captures = [PinHash, PinFiles];

        foreach (string file in modelFiles)
        {
            SourceFile source = SourceOf(file);

            foreach (Match match in HasherName.Matches(source.Code))
            {
                failures.Add(
                    $"{source.RelativePath}:{LineIndexOf(source.Code, match.Index) + 1} names '{match.Value}' " +
                    "in CODE (not in a doc comment). A model record that can reach the hasher can compute " +
                    "its pin lazily, from disk, at first access — which is the defect wearing this plan's name.");
            }

            foreach (string capture in captures)
            {
                foreach (Match match in Occurrence(capture).Matches(source.Code))
                {
                    string tail = source.Code[match.Index..Math.Min(source.Code.Length, match.Index + 160)];
                    if (!BodilessAutoProperty(capture).IsMatch(tail))
                    {
                        failures.Add(
                            $"{source.RelativePath}:{LineIndexOf(source.Code, match.Index) + 1} — '{capture}' is " +
                            "not a bodiless `{ get; init; }` auto-property here. An expression-bodied or " +
                            "accessor-bodied capture can read disk at access time, which is exactly the lazy " +
                            "evaluation §5.2's correctness floor forbids.");
                    }
                }
            }
        }

        // Anti-vacuity: "every capture is a bodiless auto-property" is trivially true of a type carrying no
        // capture at all, and TaskNode is where stage 3 put both. WaveNode is deliberately NOT required to
        // carry one — milestone B, the wave twin, has not landed; the rows above are what will hold its pin
        // to the same shape when it does.
        SourceFile taskNode = SourceOf(TaskNodeFile);
        foreach (string capture in captures)
        {
            if (!BodilessAutoProperty(capture).IsMatch(taskNode.Code))
            {
                failures.Add(
                    $"{TaskNodeFile} declares no bodiless `{capture} {{ get; init; }}` auto-property at all. " +
                    "Plan 32 §5.2 puts BOTH captures on TaskNode — the full-surface aggregate the journal " +
                    "records, and the unfiltered per-file map the settle-time gate diffs. Without the second, " +
                    "milestone C's gate has no per-file load-time state and all three ways out of that are " +
                    "worse than the defect.");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"""
             #556 DECLARATION SHAPE BROKEN — {failures.Count} problem(s) in the two model types:

               {string.Join($"{Environment.NewLine}  ", failures)}

             Comments are stripped before matching, so a doc comment that mentions a hasher by name — and
             both files carry one today, deliberately — cannot cause this. Only code can.
             """);
    }

    // ================================================================================================
    //  Anchor 3 — no fallback to disk
    // ================================================================================================

    /// <summary>
    /// <b>No line pairs a capture with a <c>Compute(</c> call — except in the loader, where the pairing IS
    /// the capture.</b>
    ///
    /// <para>A <c>?? TaskDefinitionHash.Compute(task)</c> beside the pin is, in §5.2's words, <i>"the
    /// cheapest wrong implementation of this entire plan"</i>: it passes every behavioral pin, reads like
    /// defensive coding, and silently restores the defect for any node the loader did not build.</para>
    ///
    /// <para><b>THE PLAN LOADER EXCLUSION IS DELIBERATE — DO NOT "FIX" IT AWAY.</b> §9 states this anchor
    /// as <i>"no line in src contains both"</i>, and as literally written that is UNSATISFIABLE: §5.2's own
    /// prescribed implementation is <c>return node with { DefinitionHashAtLoad = TaskDefinitionHash.Compute(node) };</c>
    /// — one line carrying both. <c>PlanLoader.cs</c> is the single capture site and the one place in the
    /// repo where that pairing is correct. Removing the exclusion would false-red a correct tree forever;
    /// widening it to a second file would re-open the fallback. Everywhere else, the pairing is the
    /// fallback.</para>
    /// </summary>
    [Fact]
    public void NoWriteSiteFallsBackToDisk()
    {
        var offenders = new List<string>();

        foreach (SourceFile source in SrcTree)
        {
            if (string.Equals(source.RelativePath, PlanLoaderFile, StringComparison.Ordinal))
            {
                continue;
            }

            for (int i = 0; i < source.Lines.Count; i++)
            {
                string line = source.Lines[i];
                bool namesACapture =
                    line.Contains(PinHash, StringComparison.Ordinal) ||
                    line.Contains(PinFiles, StringComparison.Ordinal);

                if (namesACapture && AnyComputeCall.IsMatch(line))
                {
                    offenders.Add($"{source.RelativePath}:{i + 1} — {line.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"""
             #556 FALLBACK TO DISK — a load-time capture is paired with a Compute( call outside the loader:

               {string.Join($"{Environment.NewLine}  ", offenders)}

             This is the cheapest wrong implementation of plan 32 (§5.2). `task.{PinHash} ?? Compute(task)`
             passes P1, P2, P3, P4 and P5 — in production the loader is the only constructor, so the
             fallback never fires under test — and then restores the exact silent false green for any node
             built any other way.

             A null pin records a null hash. SSOT §7.2 already defines that state ("recorded hash absent =>
             unknown, assume unchanged => match"), it is the same path a pre-#274 journal entry takes, and
             it is unreachable in production.

             The single legitimate pairing is {PlanLoaderFile} — the CAPTURE, excluded above on purpose.
             `Compute(` is matched unqualified, so dropping the `TaskDefinitionHash.` prefix (or a
             `using static`) does not evade this.
             """);
    }

    // ================================================================================================
    //  Anchor 4 — no identity-rebinding clone
    // ================================================================================================

    /// <summary>
    /// <b>No record <c>with</c>-expression rebinds <c>Directory</c> or <c>Action</c>.</b> §5.2 corrected an
    /// earlier draft's "one construction site, no with-clone anywhere" — <c>PlanLoader.QualifyWaveDependencies</c>
    /// clones both node types. The conclusion survived (a <c>with</c>-expression copies every property it
    /// does not name, so both captures ride through, and <c>DependsOn</c> lives inside <c>task.json</c> and
    /// is therefore already inside the hash), but it sharpened the real requirement: <b>a clone that
    /// rebound <c>Directory</c> or <c>Action</c> would carry a pin describing a different folder.</b>
    ///
    /// <para><b>Deliberately broader than "on a TaskNode or WaveNode".</b> A text-level anchor cannot infer
    /// the receiver's type, and the two failure directions are not symmetric: a miss is silent, an
    /// over-broad red is loud and one comment away from resolution. Measured on this tree: <c>src/</c>
    /// contains no <c>with</c>-expression rebinding either name, so the breadth costs nothing today. If a
    /// future unrelated record legitimately rebinds one, carve THAT receiver out here by name with its
    /// reason — do not delete the check.</para>
    /// </summary>
    [Fact]
    public void NoCloneRebindsATasksFolderOrItsAction()
    {
        var offenders = new List<string>();

        foreach (SourceFile source in SrcTree)
        {
            foreach (Match clone in WithExpression.Matches(source.Code))
            {
                if (!RebindsIdentity.IsMatch(clone.Value))
                {
                    continue;
                }

                int line = LineIndexOf(source.Code, clone.Index);
                int from = Math.Max(0, clone.Index - 60);
                string receiver = source.Code[from..clone.Index].Trim().Replace('\n', ' ').Replace('\r', ' ');
                string initializer = string.Join(' ',
                    clone.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                offenders.Add($"{source.RelativePath}:{line + 1} — …{receiver} {initializer}…");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"""
             #556 IDENTITY-REBINDING CLONE — a record `with`-expression rebinds Directory or Action:

               {string.Join($"{Environment.NewLine}  ", offenders)}

             The pin's lifetime is the node's lifetime BY CONSTRUCTION, and that single fact is what removes
             the entire re-baselining problem LivePlanEditWatch had to solve with six call sites (§5.2). A
             clone that rebinds Directory or Action breaks it in the quietest possible way: the copy carries
             a `{PinHash}` computed over a DIFFERENT folder's bytes, and every downstream check agrees with
             itself.

             The two clones that exist today are in {PlanLoaderFile} (QualifyWaveDependencies) and rebind
             only DependsOn and Tasks. DependsOn lives inside task.json and is therefore already inside the
             hash, so both are safe — and both stay green here.

             If the receiver above is NOT a TaskNode or a WaveNode, this check is broader than §5.2's rule
             on purpose (a text anchor cannot infer the type). Carve that receiver out by name, with the
             reason, rather than deleting the check.
             """);
    }

    // ================================================================================================
    //  The anchor set's own hygiene
    // ================================================================================================

    /// <summary>
    /// The anchor SET's own hygiene, asked of itself the way this repo asks it of a guardrail: what wrong
    /// edit would this still pass? Three ways the set could rot into ceremony — two rows pinning the same
    /// <c>(file, member)</c> twice (which reads as broader coverage than it is), two rows sharing a reason
    /// (a copy-paste twin), and a row naming a file that is not on disk, which would make the walk in
    /// direction (2) look complete while addressing nothing.
    ///
    /// <para>The last clause is the important one, and it is this test's own anti-vacuity guard: direction
    /// (2) iterates whatever <c>src/</c> enumeration it was given, so an empty or mis-rooted tree would
    /// pass it silently. Requiring every enumerated row to resolve against the loaded tree makes that
    /// impossible.</para>
    /// </summary>
    [Fact]
    public void TheAnchorSetIsEvidence_NotCeremony()
    {
        var failures = new List<string>();
        var seenSites = new HashSet<string>(StringComparer.Ordinal);
        var seenReasons = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach ((string file, string member, string why) in ComputeSiteRows)
        {
            if (!seenSites.Add($"{file}#{member}"))
            {
                failures.Add(
                    $"Two rows pin the same site '{file}' / '{member}'. The set would look broader than it " +
                    "is; give one of them its own site or drop it.");
            }

            if (seenReasons.TryGetValue(why, out string? twin))
            {
                failures.Add($"Two rows share one reason — '{member}' and '{twin}'. A row whose reason is a " +
                             "copy of another row's is evidence of nothing in particular.");
            }

            seenReasons[why] = member;

            if (!SrcByPath.ContainsKey(file))
            {
                failures.Add(
                    $"Row '{file}' / '{member}' names a file that is not in the loaded src/ tree " +
                    $"(resolved from {SrcRootAbsolute}). Either the path is a typo or the walk this test " +
                    "does in both directions is not addressing src/ at all.");
            }
        }

        foreach ((string file, string why) in HashFreeFileRows)
        {
            if (!SrcByPath.ContainsKey(file))
            {
                failures.Add($"Zero-occurrence row '{file}' ({why}) is not in the loaded src/ tree.");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"""
             ANCHOR SET UNSOUND — {failures.Count} problem(s) in the row list itself:

               {string.Join($"{Environment.NewLine}  ", failures)}

             This fact reads no source text, so it is green the moment the rows are well-formed — which is
             the point. Without it, a mis-rooted or empty src/ walk would satisfy direction (2) vacuously,
             and "every Compute occurrence maps to a known row" would be true of no occurrences at all.
             """);
    }

    // ================================================================================================
    //  Reading src/ as text
    // ================================================================================================

    /// <summary>One source file: its repo-relative path, its comment-stripped text, and that text's lines.</summary>
    private sealed record SourceFile(string RelativePath, string Code, IReadOnlyList<string> Lines);

    /// <summary>
    /// <c>TaskDefinitionHash.Compute(</c>, tolerating the <c>Journal.</c> qualification and whitespace
    /// around the dots and before the parenthesis.
    ///
    /// <para><b>This tolerance is draft 1's defeat, encoded.</b> That draft matched one literal expression
    /// — <c>handle.DefinitionHash = Journal.TaskDefinitionHash.Compute</c> — which matched ONCE on the
    /// unfixed tree and ZERO times at three of the four write sites, because <c>SettleAsync</c> hoisted to
    /// a local, <c>AttemptJournaler</c> carried no <c>Journal.</c> prefix and <c>TaskExecutor</c> used a
    /// named argument. Matching the INVOCATION rather than an expression is what makes the set complete.
    /// <c>WaveDefinitionHash.Compute</c> and <c>PlanDefinitionHash.Compute</c> are different functions with
    /// their own contracts and are deliberately not matched.</para>
    /// </summary>
    private static readonly Regex ComputeInvocation =
        new(@"\b(?:Journal\s*\.\s*)?TaskDefinitionHash\s*\.\s*Compute\s*\(", RegexOptions.CultureInvariant);

    /// <summary>Any <c>Compute(</c> call, qualified or not — anchor 3's half of the fallback pairing.</summary>
    private static readonly Regex AnyComputeCall = new(@"\bCompute\s*\(", RegexOptions.CultureInvariant);

    /// <summary>Either hasher named in code — anchor 2's ban on the model types.</summary>
    private static readonly Regex HasherName =
        new(@"\b(?:Task|Wave)DefinitionHash\b", RegexOptions.CultureInvariant);

    /// <summary>A record <c>with</c>-expression, up to the first closing brace of its initializer.</summary>
    private static readonly Regex WithExpression = new(@"\bwith\s*\{[^}]*", RegexOptions.CultureInvariant);

    /// <summary>An initializer clause rebinding a node's folder or its resolved action.</summary>
    private static readonly Regex RebindsIdentity =
        new(@"(?<![.\w])(?:Directory|Action)\s*=(?!=)", RegexOptions.CultureInvariant);

    /// <summary>
    /// A class-level member declaration: exactly four spaces of indent, then an identifier or an attribute.
    /// This repo file-scopes its namespaces and indents member bodies by eight, so four is the member
    /// level; a continuation line of a multi-line signature is indented deeper and correctly skipped.
    /// </summary>
    private static readonly Regex MemberDeclarationLine = new(@"^ {4}[A-Za-z\[]", RegexOptions.CultureInvariant);

    /// <summary>An identifier immediately followed by an argument or parameter list.</summary>
    private static readonly Regex ParameterisedName =
        new(@"([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^<>()]*>)?\s*\(", RegexOptions.CultureInvariant);

    /// <summary>Any identifier.</summary>
    private static readonly Regex AnyIdentifier = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant);

    /// <summary>Keywords a member-name extractor must never mistake for the member's own name.</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "static", "readonly", "const", "sealed", "abstract",
        "virtual", "override", "async", "partial", "extern", "unsafe", "file", "required", "volatile",
        "new", "return", "throw", "if", "else", "while", "do", "for", "foreach", "switch", "case",
        "try", "catch", "finally", "lock", "using", "fixed", "checked", "unchecked", "typeof", "nameof",
        "sizeof", "default", "this", "base", "is", "as", "in", "out", "ref", "params", "when", "where",
        "yield", "await", "operator", "implicit", "explicit", "record", "class", "struct", "interface",
        "enum", "delegate", "event", "get", "set", "init", "add", "remove", "void", "var", "stackalloc"
    };

    /// <summary>Repo root, resolved from this test file's own location — never AppContext.BaseDirectory
    /// (which is the build output) and never a walk-up search for <c>.git</c> (which finds the wrong root
    /// inside a worktree or a submodule). <see cref="TestPaths.ProjectDir"/> uses
    /// <c>[CallerFilePath]</c>; this is the repo's own idiom, shared with the sibling anchor tests.</summary>
    private static string RepoRoot => Path.GetFullPath(Path.Combine(TestPaths.ProjectDir, "..", ".."));

    /// <summary>The absolute <c>src/</c> root this test walks.</summary>
    private static string SrcRootAbsolute => Path.Combine(RepoRoot, "src");

    /// <summary>Every committed C# source file under <c>src/</c>, comment-stripped, read once.</summary>
    private static readonly IReadOnlyList<SourceFile> SrcTree = LoadSrcTree();

    private static readonly IReadOnlyDictionary<string, SourceFile> SrcByPath =
        SrcTree.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);

    private static IReadOnlyList<SourceFile> LoadSrcTree()
    {
        string root = SrcRootAbsolute;
        if (!Directory.Exists(root))
        {
            // Reported by the hygiene fact, which requires every enumerated row to resolve against this
            // tree — so a mis-rooted walk fails loudly there instead of passing direction (2) vacuously.
            return [];
        }

        var files = new List<SourceFile>();
        foreach (string absolute in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            // bin/ and obj/ hold generated and copied sources (AssemblyInfo, GlobalUsings, the packed
            // tool's own inputs). Including them would make this anchor's verdict depend on whether the
            // tree happens to have been built, which is the least defensible kind of flake.
            if (IsBuildOutput(root, absolute))
            {
                continue;
            }

            string code = StripComments(File.ReadAllText(absolute));
            files.Add(new SourceFile(
                Path.GetRelativePath(RepoRoot, absolute).Replace('\\', '/'),
                code,
                code.Split('\n')));
        }

        return files;
    }

    private static bool IsBuildOutput(string root, string absolute) =>
        Path.GetRelativePath(root, absolute)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                         || segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static SourceFile SourceOf(string relativePath)
    {
        Assert.True(
            SrcByPath.TryGetValue(relativePath, out SourceFile? source),
            $"Source file not found under the repo's src/ tree: {relativePath} " +
            $"(searched {SrcRootAbsolute}). Re-point the constant in the same change that moved the file.");

        return source!;
    }

    /// <summary>The distinct enclosing members of every hasher call in one file, in source order.</summary>
    private static IReadOnlyList<string> MembersCallingCompute(SourceFile source)
    {
        var members = new List<string>();
        foreach (Match match in ComputeInvocation.Matches(source.Code))
        {
            string member = EnclosingMember(source.Lines, LineIndexOf(source.Code, match.Index));
            if (!members.Contains(member, StringComparer.Ordinal))
            {
                members.Add(member);
            }
        }

        return members;
    }

    private static int LineIndexOf(string code, int offset)
    {
        int line = 0;
        for (int i = 0; i < offset; i++)
        {
            if (code[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <summary>
    /// The member a line sits inside: the nearest class-level declaration above it. A lexical walk rather
    /// than a parser — §9 asks for ordinal matching over normalized text, and the alternative (hosting a
    /// C# parser in a test to answer "which member is this call in") buys precision this anchor does not
    /// need. A call inside a lambda or a local function correctly attributes to the enclosing MEMBER, which
    /// is the granularity plan 32 §4.3's taxonomy is stated at.
    /// </summary>
    private static string EnclosingMember(IReadOnlyList<string> lines, int lineIndex)
    {
        for (int i = Math.Min(lineIndex, lines.Count - 1); i >= 0; i--)
        {
            if (MemberDeclarationLine.IsMatch(lines[i]))
            {
                return MemberNameFrom(lines, i);
            }
        }

        return "(no enclosing member)";
    }

    /// <summary>
    /// The declared name in a signature that may span several lines. Reads forward from the declaration
    /// until a top-level <c>{</c>, <c>=&gt;</c> or <c>;</c> closes it, then takes the first non-keyword
    /// identifier introducing a parameter list — which is the member name even when the RETURN TYPE is a
    /// parenthesised tuple on its own line, as <c>Scheduler.DetectDefinitionDrift</c>'s is. A member with
    /// no parameter list (a property) falls back to the last non-keyword identifier in the signature.
    /// </summary>
    private static string MemberNameFrom(IReadOnlyList<string> lines, int declarationLine)
    {
        var signature = new StringBuilder();
        int parens = 0;

        for (int i = declarationLine; i < lines.Count && i - declarationLine < 12; i++)
        {
            string line = lines[i];
            for (int c = 0; c < line.Length; c++)
            {
                char ch = line[c];
                bool arrow = ch == '=' && c + 1 < line.Length && line[c + 1] == '>';
                if (parens == 0 && (ch == '{' || ch == ';' || arrow))
                {
                    return NameFromSignature(signature.ToString());
                }

                if (ch == '(')
                {
                    parens++;
                }
                else if (ch == ')')
                {
                    parens--;
                }

                signature.Append(ch);
            }

            signature.Append(' ');
        }

        return NameFromSignature(signature.ToString());
    }

    private static string NameFromSignature(string signature)
    {
        foreach (Match candidate in ParameterisedName.Matches(signature))
        {
            if (!Keywords.Contains(candidate.Groups[1].Value))
            {
                return candidate.Groups[1].Value;
            }
        }

        string? last = null;
        foreach (Match identifier in AnyIdentifier.Matches(signature))
        {
            if (!Keywords.Contains(identifier.Value))
            {
                last = identifier.Value;
            }
        }

        return last ?? "(unnamed member)";
    }

    private static Regex Occurrence(string identifier) =>
        new($@"\b{identifier}\b", RegexOptions.CultureInvariant);

    private static Regex BodilessAutoProperty(string identifier) =>
        new($@"\b{identifier}\s*\{{\s*get\s*;\s*init\s*;\s*\}}", RegexOptions.CultureInvariant);

    /// <summary>
    /// Comments blanked to spaces, newlines preserved so every line number this test reports is the real
    /// one. String and character literals are SKIPPED but left intact: skipping them is what stops a
    /// <c>"https:</c>-style literal from swallowing the rest of its line as a line comment, and leaving
    /// them intact means a hasher call hidden in a string would still be reported — a loud false red rather
    /// than a silent miss, which is the correct direction for a check whose whole subject is a mechanism
    /// that fails quietly.
    /// </summary>
    private static string StripComments(string source)
    {
        char[] stripped = source.ToCharArray();
        int i = 0;

        while (i < source.Length)
        {
            char c = source[i];

            if (c == '/' && At(source, i + 1) == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    stripped[i] = Blank(source[i]);
                    i++;
                }

                continue;
            }

            if (c == '/' && At(source, i + 1) == '*')
            {
                stripped[i] = ' ';
                stripped[i + 1] = ' ';
                i += 2;

                while (i < source.Length && !(source[i] == '*' && At(source, i + 1) == '/'))
                {
                    stripped[i] = Blank(source[i]);
                    i++;
                }

                if (i < source.Length)
                {
                    stripped[i] = ' ';
                    stripped[i + 1] = ' ';
                    i += 2;
                }

                continue;
            }

            if (c == '"')
            {
                int quotes = RunLength(source, i, '"');
                i = quotes >= 3 ? SkipRawString(source, i, quotes) : SkipQuoted(source, i);
                continue;
            }

            if (c == '\'')
            {
                i = SkipCharLiteral(source, i);
                continue;
            }

            i++;
        }

        return new string(stripped);
    }

    private static char Blank(char c) => c is '\n' or '\r' ? c : ' ';

    private static char At(string text, int index) => index < text.Length ? text[index] : '\0';

    private static int RunLength(string text, int index, char c)
    {
        int run = 0;
        while (index + run < text.Length && text[index + run] == c)
        {
            run++;
        }

        return run;
    }

    private static int SkipQuoted(string text, int start)
    {
        bool verbatim = start > 0 &&
            (text[start - 1] == '@' || (start > 1 && text[start - 1] == '$' && text[start - 2] == '@'));

        int i = start + 1;
        while (i < text.Length)
        {
            char c = text[i];
            if (verbatim)
            {
                if (c != '"')
                {
                    i++;
                    continue;
                }

                if (At(text, i + 1) == '"')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            if (c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == '"')
            {
                return i + 1;
            }

            if (c == '\n')
            {
                return i;
            }

            i++;
        }

        return i;
    }

    private static int SkipRawString(string text, int start, int openQuotes)
    {
        int i = start + openQuotes;
        while (i < text.Length)
        {
            if (text[i] != '"')
            {
                i++;
                continue;
            }

            int run = RunLength(text, i, '"');
            if (run >= openQuotes)
            {
                return i + run;
            }

            i += run;
        }

        return i;
    }

    private static int SkipCharLiteral(string text, int start)
    {
        int i = start + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (text[i] == '\'')
            {
                return i + 1;
            }

            if (text[i] == '\n')
            {
                return i;
            }

            i++;
        }

        return i;
    }
}
