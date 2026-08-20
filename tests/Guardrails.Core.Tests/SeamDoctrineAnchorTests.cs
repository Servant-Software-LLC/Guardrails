namespace Guardrails.Core.Tests;

/// <summary>
/// <b>#382 M3 — the half that goes RED when the doctrine is gutted.</b>
///
/// <para><b>Why a test reads markdown.</b> Decision <b>D1</b> of
/// <c>docs/plans/18-integration-proof-proximity.md</c>: #382 v1 ships <i>no</i> <c>validate</c> code and
/// <i>no</i> GR code, because the defect's carrier does not exist at validate time and the correct and
/// incorrect forms of the prose are identical. The skill text therefore <b>is</b> the deliverable — the
/// review pass is the only gate — and a deliverable with no regression signal is one silent edit away
/// from being retired by accident. Superseded phrasing outliving its replacement in a skill every agent
/// loads is a failure mode this repo has already met (§12's V10 note).</para>
///
/// <para><b>What each anchor is.</b> One load-bearing NORMATIVE clause per row — the T\* definition and
/// its "later is a finding" teeth, the closed four-item N enumeration and the N4 trap, one-real-level,
/// the ledger's heading/header/zero-row contract, the join-check anti-regression clause, the #378
/// boundary, and M2's review-side audit (recompute T\*, D11, D12, absent-heading-is-a-BLOCKER, D13's
/// third state, Probe B operator 20). Deleting or hollowing any one of them turns this red and names
/// what was lost.</para>
///
/// <para><b>Reflow-tolerant, wording-sensitive — on purpose.</b> <see cref="Normalize"/> strips line
/// endings, indentation, markdown blockquote markers and whitespace runs before matching, so re-wrapping
/// a paragraph or re-indenting a list never breaks an anchor. Only changing the words does. That is the
/// intended sensitivity: the words are the contract here, in the narrow way house doctrine allows
/// (assert codes, not message text — except where the message IS the contract).</para>
///
/// <para><b>Two anchors are load-bearing for the sibling test.</b>
/// <see cref="SeamProofProximityTests"/> can only find a real-seam proof in a folder by the two tells
/// the skills emit — the <c>03-real-seam-tests-pass.ps1</c> filename from the ledger example and the
/// <c>(passing-but-blind)</c> token from the catalogue's <c># catches:</c> template. Both are pinned
/// below, so a skill edit that stops emitting them fails HERE rather than silently emptying the
/// placement audit into a vacuous green. Neither tell is a declared contract today; that gap is the
/// honest limit of the folder-observable half and is exactly the kind of evidence §3.4's gate wants.</para>
///
/// <para><b>If one of these fails, do not delete the row.</b> Either the doctrine genuinely moved — in
/// which case re-point the anchor in the same change that moved it — or it was lost, which is the whole
/// reason this test exists.</para>
/// </summary>
public sealed class SeamDoctrineAnchorTests
{
    private const string PlanBreakdown = ".claude/skills/plan-breakdown/SKILL.md";
    private const string Catalogue = ".claude/skills/plan-breakdown/references/guardrail-catalogue.md";
    private const string DotnetStack = ".claude/skills/plan-breakdown/references/stacks/dotnet.md";
    private const string ReviewSkill = ".claude/skills/guardrails-review/SKILL.md";

    /// <summary>
    /// The anchor set: (skill file, normative clause, the doctrine it carries). Held as a plain tuple
    /// list so the hygiene test can walk it without depending on how <c>TheoryData</c> enumerates.
    /// </summary>
    private static readonly (string SkillFile, string Clause, string Doctrine)[] AnchorRows =
    [
        // ---- M1: plan-breakdown Step 4 — the authoring rule ------------------------------------
        (
            PlanBreakdown,
            """the proof is owed at **T\*** — the **earliest task in the DAG at which BOTH (i) the component's production type and (ii) the seam's production type exist**""",
            "§1.4 — the DEFINITION of T*. Without it 'prove it early' is back to being advice."
        ),
        (
            PlanBreakdown,
            """**A proof placed LATER than T\* is a finding.**""",
            "§1.4 — the teeth. A definition with no verdict attached is a description, and #382's whole defect is a proof that exists but sits downstream."
        ),
        (
            PlanBreakdown,
            "a type exists at a task when that task's `writeScope` contains the file declaring it, or an ancestor's does",
            "§1.4 — the COMPUTABILITY clause. It is what makes T* falsifiable rather than a judgement nobody can contradict, and it is the rule SeamProofPlacement implements."
        ),
        (
            PlanBreakdown,
            "N is a **CLOSED ENUMERATION OF FOUR ITEMS, NOT A CATEGORY**",
            "§1.2 / D2 — a category is a hiding place; a closed list can be checked. Softening this back to a category re-opens 'where feasible' under a new name."
        ),
        (
            PlanBreakdown,
            "**N1** a clock / time source; **N2** a randomness source (an RNG, a GUID factory); **N3** an ambient environment reader (env vars, machine name, current directory, an OS probe); **N4** a **wait primitive**",
            "§1.2 — the four items themselves. The list IS the exemption; a fifth item added quietly is an exemption added quietly."
        ),
        (
            PlanBreakdown,
            """**The N4 trap — fake the WAIT, never the WAITER. If the substitute contains a DECISION, it is not N4.**""",
            "§1.2 / D2 — written from a shipped bug (the silently-swallowed transient). The likeliest place the taxonomy gets abused."
        ),
        (
            PlanBreakdown,
            "The component under test is constructed with the **REAL implementation of the seam under test**.",
            "§1.3 / D3 — one real level down. The precise replacement for 'fake the process, never the in-process seam', and the induction the whole design rests on."
        ),
        (
            PlanBreakdown,
            "printed in the Step 7.4 report under a bolded line reading `Seam ledger (#382)`",
            "§2 — the ledger's HOME and its heading. M2 keys its finding on the heading, so this string is a cross-skill contract."
        ),
        (
            PlanBreakdown,
            "| seam (component → declared dependency) | bucket | production type | faked underneath | T* | proof |",
            "§2 — the six-column header, fixed text in that order. /guardrails-review parses it."
        ),
        (
            PlanBreakdown,
            "**The heading is emitted EVEN WHEN THERE ARE NO ROWS**",
            "M1 ruling — a clean plan and a skipped analysis must be distinguishable. Drop this and every zero-row plan reads as 'the analysis never ran'."
        ),
        (
            PlanBreakdown,
            "_No in-process seam is substituted by this breakdown's tests._",
            "M1 ruling — the zero-row sentinel. A claim that can be checked, rather than an absence that cannot."
        ),
        (
            PlanBreakdown,
            "name a defect that **SURVIVES every upstream real-seam proof passing**",
            "§1.5 / D5 — the ANTI-REGRESSION clause. Without it an author satisfies the design by writing a ledger and leaving all the proof in the sink anyway."
        ),
        (
            PlanBreakdown,
            "03-real-seam-tests-pass.ps1",
            "The proof filename the ledger's `proof` column emits — one of the two folder-observable tells SeamProofPlacement keys on."
        ),
        (
            PlanBreakdown,
            "reading `writeScope` as a lookup or a coverage set",
            "§6 / D9 — the #378 boundary is VERDICT-based, not field-based. This clause is what permits the T* audit to read writeScope at all."
        ),

        // ---- M1: the catalogue — the guardrail's SHAPE -------------------------------------------
        (
            Catalogue,
            "## Drive-the-real-seam — the component is proven through the ACTUAL seam, not a fake of it (#382)",
            "D6 — the archetype stays in the catalogue, restructured IN PLACE. A second section would be the duplication this design exists to prevent."
        ),
        (
            Catalogue,
            "(passing-but-blind)",
            "The `# catches:` template token — the second folder-observable tell SeamProofPlacement keys on."
        ),
        (
            Catalogue,
            "**Rung 1 under the demotion ordering, and there is NO rung-3 form (#468).**",
            "D6 / §5.1 — no source-grep rung. A regex for `new ClaudePromptRunner(` certifies vocabulary, not capability."
        ),
        (
            Catalogue,
            "**The assertion requirement — an effect ONLY the production implementation emits.**",
            "D6 — the clause that makes the archetype survive Probe B operator 20."
        ),
        (
            Catalogue,
            """***"The seam was called" is NOT an assertion***""",
            "D6 — the fake satisfies 'was called', which is exactly how the two motivating bugs shipped green."
        ),
        (
            Catalogue,
            """**`scope`: `"local"` — omit the key.**""",
            "The #250 conclusion: a real-seam proof cannot pass before its implement task has run, so it fails the #125 union-safe test and must never be tagged integration."
        ),

        // ---- M1: the .NET realization ------------------------------------------------------------
        (
            DotnetStack,
            "### 10e. Drive-the-real-seam contract test",
            "V-item — the stack-specific realization. Without it the rule has no worked C# form and degrades to prose."
        ),
        (
            DotnetStack,
            "**`new InstantDelay()` is the N4 line drawn in code.**",
            "The N4 trap made concrete: substituting IDelay is exempt, substituting ITransientBackoff is the bug."
        ),
        (
            DotnetStack,
            """**Distribute** these across the component tasks (one real-seam test at each component's T\*)""",
            "The anti-concentration instruction — deferring all real-path proof to one terminal factory-driving sink is the #378 over-scope fingerprint."
        ),

        // ---- M2: guardrails-review — the audit ---------------------------------------------------
        (
            ReviewSkill,
            """**3. Recompute T\* for every E and C row from the DAG — do not accept the cell.**""",
            "M2 — the reviewer RE-DERIVES T*. Accepting the cell gives the column back to the author who wrote it."
        ),
        (
            ReviewSkill,
            """A proof placed **later** than T\* is a finding **even when the proof exists and passes**""",
            "M2 — the green-but-mis-placed case. This is the exact state #382's dogfood was in."
        ),
        (
            ReviewSkill,
            "**4. An E row may NEVER invoke the construction bound (D11).**",
            "D11 — the escape hatch an author reaches for first. E is always feasible by definition, so 'I could not construct it' on an E row is a finding."
        ),
        (
            ReviewSkill,
            "an ABSENT heading is a BLOCKER (the Step 4 analysis never ran)",
            "M1 ruling + M2 — the finding keys on the missing HEADING, not the missing table."
        ),
        (
            ReviewSkill,
            "a ledger **not produced to this pass** is an unchecked-gap line in the report, never a finding",
            "D13 — the third state. Reporting 'not produced' as 'absent' would manufacture a BLOCKER out of a missing attachment."
        ),
        (
            ReviewSkill,
            "satisfy a **real-seam / composition-root `--filter`** with a test that **constructs the FAKE**",
            "D7 — Probe B operator 20, the only MECHANICAL check separating a real-seam test from one that is real-seam in name only."
        ),
        (
            ReviewSkill,
            "Do NOT flag a correct real-seam test as a #120 violation (D12).",
            "D12 — same verb, different slot. Without it a reader meets flatly opposite instructions in adjacent sections."
        )
    ];

    /// <summary>The anchor set as xUnit theory data.</summary>
    public static TheoryData<string, string, string> Anchors()
    {
        TheoryData<string, string, string> data = [];
        foreach ((string skillFile, string clause, string doctrine) in AnchorRows)
        {
            data.Add(skillFile, clause, doctrine);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Anchors))]
    public void TheSkillStillCarriesTheClause(string skillFile, string clause, string doctrine)
    {
        string path = SkillPath(skillFile);
        Assert.True(File.Exists(path), $"Skill file not found: {path}");

        Assert.True(
            Normalize(File.ReadAllText(path)).Contains(Normalize(clause), StringComparison.Ordinal),
            $"""
             #382 DOCTRINE LOST — {skillFile} no longer carries a load-bearing clause.

               doctrine : {doctrine}
               clause   : {clause}

             #382 v1 ships NO validate lint and NO GR code (design 18 §3, D1), so this anchor and the
             folder-observable placement audit in SeamProofProximityTests are the ONLY durable evidence
             the rule was ever applied. Do not delete this row to go green.

               - If the clause genuinely MOVED or was reworded deliberately, re-point the anchor in the
                 SAME change that moved it, and check docs/plans/18-integration-proof-proximity.md still
                 describes what the skill now says.
               - If it was lost, restore it. A retired rule outliving its replacement in a skill every
                 agent loads is the failure this test exists to catch.

             Matching ignores line endings, indentation, markdown blockquote markers and whitespace runs,
             so a re-wrap cannot cause this — only a wording change can.
             """);
    }

    /// <summary>
    /// The anchor SET's own hygiene, asked of itself the way this repo asks it of a guardrail: what
    /// wrong edit would this still pass? Two ways the set could rot into ceremony — a clause too short
    /// to be evidence of anything, and two rows pinning the same sentence twice (which reads as broader
    /// coverage than it is). Both are cheap to check and would otherwise be invisible.
    /// </summary>
    [Fact]
    public void TheAnchorSetIsEvidence_NotCeremony()
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach ((string skillFile, string clause, string doctrine) in AnchorRows)
        {
            string normalized = Normalize(clause);

            // Short enough to hit by accident is short enough to survive a gutted skill. The two
            // deliberately-terse rows are the emitted MARKERS, and they are the reason this floor is a
            // length rather than a uniqueness rule: `03-real-seam-tests-pass.ps1` legitimately appears
            // once per ledger example row.
            Assert.True(normalized.Length >= 19,
                $"Anchor for {skillFile} ({doctrine}) is too short to be evidence: '{normalized}'");

            Assert.False(seen.TryGetValue(normalized, out string? already),
                $"Two anchor rows pin the same clause — '{doctrine}' and '{already}'. The set would " +
                "look broader than it is; give one of them its own distinct clause or drop it.");
            seen[normalized] = doctrine;
        }
    }

    /// <summary>
    /// The wire between the two halves of M3. <see cref="SeamProofPlacement"/> can only recognise a
    /// real-seam proof in an emitted folder by the tells the skills actually put there. If a skill edit
    /// stops emitting them, the placement audit would quietly find nothing to check and report a
    /// vacuous green — the same passing-but-blind shape #382 exists to remove, reproduced inside its own
    /// meta-test. This fails HERE instead, and names which tell went missing.
    ///
    /// <para>Neither tell is a declared contract today (the filename is a ledger EXAMPLE, the token is
    /// part of a <c># catches:</c> TEMPLATE). That is the honest limit of the folder-observable half,
    /// and per D13 the fix is a declared field plus GR2061 behind §3.4's gate — never a marker invented
    /// here.</para>
    /// </summary>
    [Fact]
    public void TheAuditsMarkersAreTheOnesTheSkillsActuallyEmit()
    {
        Assert.Contains(
            SeamProofPlacement.RealSeamProofMarkers.NameFragment,
            Normalize(File.ReadAllText(SkillPath(PlanBreakdown))),
            StringComparison.Ordinal);

        Assert.Contains(
            SeamProofPlacement.RealSeamProofMarkers.CatchesToken,
            Normalize(File.ReadAllText(SkillPath(Catalogue))),
            StringComparison.Ordinal);
    }

    // ---- normalization ----------------------------------------------------------------------------

    /// <summary>
    /// Line endings, indentation, markdown blockquote markers and whitespace runs collapsed away, so an
    /// anchor survives re-wrapping, re-indenting and a CRLF checkout (<c>.claude/skills/**</c> is not
    /// <c>eol=lf</c>-pinned) but not a change of words.
    /// </summary>
    private static string Normalize(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var stripped = new List<string>(lines.Length);

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            while (trimmed.StartsWith('>'))
            {
                trimmed = trimmed[1..].Trim();
            }

            stripped.Add(trimmed);
        }

        return string.Join(' ',
            string.Join(' ', stripped).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The skill file, addressed from the repo root by its forward-slashed relative path.</summary>
    private static string SkillPath(string relative) =>
        Path.GetFullPath(Path.Combine(RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot => Path.GetFullPath(Path.Combine(TestPaths.ProjectDir, "..", ".."));
}
