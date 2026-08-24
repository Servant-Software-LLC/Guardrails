namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// <b>#229 · stage-3 charter §C — the half that goes RED when the model-appropriateness probe is gutted.</b>
///
/// <para><b>Why a test reads markdown.</b> This wave's deliverable is SKILL TEXT and nothing else: per DoR
/// §12.6 neither of the probe's two findings gets a GR code, so there is no <c>validate</c> lint and no
/// diagnostic code to regress. The skill text therefore <b>is</b> the deliverable — the review pass is the
/// only gate — and a deliverable with no regression signal is one silent edit away from being retired by
/// accident. That is the same position #382 was in, and <see cref="SeamDoctrineAnchorTests"/> is the shape
/// copied here for the same stated reason.</para>
///
/// <para><b>What each anchor is.</b> One load-bearing NORMATIVE clause per row — the probe's existence and
/// provenance, the configured-only gate and the absolute silence behind it, both findings' definitions, the
/// resolved-tier trap and the field to read instead, the judge population's inheritance rule, the
/// candidate-SET ruling, the no-code ruling with its reason, the boundary against a diagnostic that already
/// exists, the report line and the quality-bar item. Deleting or hollowing any one of them turns this red
/// and names what was lost.</para>
///
/// <para><b>Reflow-tolerant, wording-sensitive — on purpose.</b> <see cref="Normalize"/> strips line
/// endings, indentation, markdown blockquote markers and whitespace runs before matching, so re-wrapping a
/// paragraph or re-indenting a list never breaks an anchor. Only changing the words does. An anchor set that
/// breaks on a re-wrap is an anchor set someone deletes for being noisy.</para>
///
/// <para><b>Presence is not placement.</b> A probe pasted at the END of the file satisfies every clause row
/// below, which is why <see cref="TheThreeInsertionsLandInTheirOwnSections"/> exists as a separate fact: the
/// probe belongs in section 2's probe list beside its siblings, the report line in section 6, the checklist
/// item under the Quality bar.</para>
///
/// <para><b>If one of these fails, do not delete the row.</b> Either the doctrine genuinely moved — in which
/// case re-point the anchor in the same change that moved it — or it was lost, which is the whole reason
/// this test exists.</para>
/// </summary>
public sealed class ModelAppropriatenessDoctrineAnchorTests
{
    private const string ReviewSkill = ".claude/skills/guardrails-review/SKILL.md";

    // ---- the section headings the placement fact navigates by ------------------------------------------
    // Prefixes, deliberately: the live headings carry trailing issue references that are none of this
    // test's business, and pinning them here would make an unrelated edit look like a restructure.
    private const string Section2Heading = "### 2. Adversarial pass per task (the heart)";
    private const string Section2bHeading = "### 2b. EXECUTE the guardrails";
    private const string Section6Heading = "### 6. Report";
    private const string Section7Heading = "### 7. Record the review";
    private const string QualityBarHeading = "## Quality bar";

    // ---- the three clauses whose PLACEMENT is pinned as well as their presence -------------------------
    // Held as constants so the row list and the placement fact can never drift into two slightly different
    // copies of the same sentence.
    private const string ProbeBulletClause =
        """- **Model-appropriateness — the tag-quality net (#229 · `model-tiering-stage-3` charter §C)**:""";

    private const string ReportLineClause =
        """On an unconfigured plan this line is ABSENT — the graceful skip is silence, not a line saying it was silent.""";

    private const string QualityBarClause =
        """is named as an advisory MISSING-CLASSIFICATION finding""";

    /// <summary>
    /// The anchor set: (skill file, normative clause, the doctrine it carries). Held as a plain tuple list
    /// so the hygiene test can walk it without depending on how <c>TheoryData</c> enumerates. The fourteen
    /// clauses are VERBATIM and are shared, character-for-character, with the task that writes them into the
    /// skill — paraphrasing one here makes a clause that can never be satisfied.
    /// </summary>
    private static readonly (string SkillFile, string Clause, string Doctrine)[] AnchorRows =
    [
        // ---- the probe itself: it exists, and it says where it came from -------------------------------
        (
            ReviewSkill,
            ProbeBulletClause,
            "§C — the probe's EXISTENCE, in section 2's list, and its provenance. Lose this row and the whole net can vanish leaving no trace of which issue and charter section asked for it."
        ),

        // ---- the gate, the silence, and the reason the silence is a requirement ------------------------
        (
            ReviewSkill,
            """**Run this probe ONLY when tiering is CONFIGURED** — at least one `promptRunners.<name>.routing` block, or a top-level `tiering` block, in the config that governs this plan.""",
            "§C's note — the GATE. Without it the probe has no 'when', and the graceful skip has nothing to hang on."
        ),
        (
            ReviewSkill,
            """On a plan generated before tiering shipped this probe produces NOTHING AT ALL — no finding, no unchecked-gap line, and no note saying it was skipped.""",
            "§C's note — the SILENCE, stated as an absolute. The three 'no ...' clauses are the whole of it, and the third is the one a careful author breaks by being helpful."
        ),
        (
            ReviewSkill,
            """A check that fires on every legacy plan gets muted within a week, and a muted check is indistinguishable from the absence this stage exists to fix.""",
            "§C's note — the WHY, in the charter's own words. Delete it and the skip reads as politeness rather than a requirement, and the next editor softens it."
        ),

        // ---- finding 1: the deterministic half, and the trap that would empty it -----------------------
        (
            ReviewSkill,
            """**Missing classification** *(deterministic — a fact about the folder)*: a prompt-action task, or a surviving judge guardrail, with neither a difficulty tag nor an explicit `action.model` / `action.effort` pin.""",
            "§C — the deterministic finding's DEFINITION, in the charter's own words. Without it 'missing classification' is a name with no subject population and no discharge condition."
        ),
        (
            ReviewSkill,
            """A plan-wide `tiering.defaultTier` does NOT discharge it: the loader resolves that default into every untagged task, so a probe that read the RESOLVED tier would report every configured plan as fully classified.""",
            "DoR §12.4 — the TRAP, and the single most likely way this probe ships and then finds nothing forever."
        ),
        (
            ReviewSkill,
            """Read the task's own declaration — `TierOrigin.Task` is the value that survives the `?? defaultTier` collapse (DoR §12.4).""",
            "DoR §12.4 — WHERE to read instead of the resolved tier: the one field that survives the load-time collapse."
        ),
        (
            ReviewSkill,
            """A surviving prompt-judge guardrail carries its own `tier` in FRONTMATTER (SSOT §4.2), and an absent key means its rung FOLLOWS THE ACTOR it guards — so it is unclassified only when that task is unclassified too, or when it guards no task at all (a plan-root or wave-root gate).""",
            "SSOT §4.2 — the judge population, its tag SITE, and the inheritance that keeps this half from being noise; flagging every untagged judge would fire on almost every configured plan, which is how a check gets muted."
        ),

        // ---- finding 2: the judgment half, and the ruling that keeps it applying the design -------------
        (
            ReviewSkill,
            """**Mismatched tier** *(judgment — a model's opinion about difficulty)*: a high-risk task tagged for a weak tier, or a mechanical one tagged frontier-only.""",
            "§C — the judgment finding's DEFINITION. Without it the net ships half-built, which was an explicit option the charter considered and rejected."
        ),
        (
            ReviewSkill,
            """Difficulty maps to a candidate SET, not to a single model strength""",
            "The standing ruling that difficulty maps to a candidate SET. A probe that reasons about 'the right model' instead of 'the right rung' is arguing with the design rather than applying it."
        ),

        // ---- the two boundaries: no code, and no second opinion on validate's config ---------------------
        (
            ReviewSkill,
            """Neither finding gets a GR code: a GR code is a thing that can fail a build, and the harness does not block on a model-quality opinion (DoR §12.6).""",
            "DoR §12.6 — the no-code ruling WITH its reason attached. The reason is what stops the next person re-opening it."
        ),
        (
            ReviewSkill,
            """a plan with tags and no `routing` block anywhere is GR2049's business, not this probe's""",
            "The boundary against a diagnostic that already exists. Without it the probe emits a second opinion on a config the validator has already reported."
        ),

        // ---- the report line, and the checklist item -----------------------------------------------------
        (
            ReviewSkill,
            ReportLineClause,
            "§6 — the REPORT counterpart of the silence, and the sharpest clause in the set: section 6's convention is to state what the pass could not check, which is exactly the instinct that would break the skip."
        ),
        (
            ReviewSkill,
            QualityBarClause,
            "The QUALITY BAR item, so the probe is on the checklist and not merely in the prose."
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
             #229 DOCTRINE LOST — {skillFile} no longer carries a load-bearing clause.

               doctrine : {doctrine}
               clause   : {clause}

             This wave ships NO `validate` code and NO diagnostic code (charter §C, DoR §12.6), so this
             anchor set is the ONLY durable evidence the rule was ever applied. Do not delete this row to go
             green.

               - If the clause genuinely MOVED or was reworded deliberately, re-point the anchor in the SAME
                 change that moved the clause, and check the stage-3 charter still describes what the skill
                 now says.
               - If it was lost, restore it. A rule quietly retired from a skill every agent loads is the
                 failure this test exists to catch.

             Matching ignores line endings, indentation, markdown blockquote markers and whitespace runs, so
             a re-wrap cannot cause this — only a wording change can.
             """);
    }

    /// <summary>
    /// The placement fact, and the one the clause theory cannot carry: a probe pasted at the end of the file
    /// satisfies every clause row. The three insertions belong in three different sections, beside the
    /// siblings that give each of them its meaning — the probe among section 2's other probes, the report
    /// line in section 6's list, the checklist item under the Quality bar.
    ///
    /// <para>All three are accumulated and reported together, because a reader who has pasted the block once
    /// in the wrong place has usually pasted all of it in the wrong place, and fixing one at a time is three
    /// runs of the suite. The section headings are checked FIRST for the same reason: a restructured skill
    /// should fail with "the heading moved", not with three unexplained ordering failures.</para>
    /// </summary>
    [Fact]
    public void TheThreeInsertionsLandInTheirOwnSections()
    {
        string path = SkillPath(ReviewSkill);
        Assert.True(File.Exists(path), $"Skill file not found: {path}");

        string skill = Normalize(File.ReadAllText(path));

        (string Heading, string Role)[] navigation =
        [
            (Section2Heading, "opens the probe list clause 1 must sit inside"),
            (Section2bHeading, "closes it"),
            (Section6Heading, "opens the report section clause 13 must sit inside"),
            (Section7Heading, "closes it"),
            (QualityBarHeading, "opens the checklist clause 14 must sit after")
        ];

        string[] moved = navigation
            .Where(h => !skill.Contains(Normalize(h.Heading), StringComparison.Ordinal))
            .Select(h => $"'{h.Heading}' — {h.Role}")
            .ToArray();

        Assert.True(moved.Length == 0,
            $"""
             THE SKILL WAS RESTRUCTURED — {moved.Length} section heading(s) this fact navigates by are gone:

               {string.Join("; ", moved)}

             Nothing is claimed about the model-appropriateness probe's placement here: without these
             headings there are no section boundaries to place it between. Re-point the heading constants at
             whatever the sections are now called, in the same change that renamed them.
             """);

        var failures = new List<string>();

        void MustSitInside(string clause, string what, string startHeading, string? endHeading)
        {
            string bounds = endHeading is null
                ? $"after '{startHeading}'"
                : $"between '{startHeading}' and '{endHeading}'";

            int at = skill.IndexOf(Normalize(clause), StringComparison.Ordinal);
            if (at < 0)
            {
                failures.Add($"{what} is not in the skill AT ALL, so it cannot be {bounds}: {clause}");
                return;
            }

            int start = skill.IndexOf(Normalize(startHeading), StringComparison.Ordinal);
            int end = endHeading is null
                ? skill.Length
                : skill.IndexOf(Normalize(endHeading), StringComparison.Ordinal);

            if (at <= start || at >= end)
            {
                failures.Add($"{what} is present but NOT {bounds} — it sits outside its section: {clause}");
            }
        }

        MustSitInside(
            ProbeBulletClause,
            "clause 1, the probe bullet",
            Section2Heading,
            Section2bHeading);

        MustSitInside(
            ReportLineClause,
            "clause 13, the report line",
            Section6Heading,
            Section7Heading);

        MustSitInside(
            QualityBarClause,
            "clause 14, the quality-bar item",
            QualityBarHeading,
            null);

        Assert.True(failures.Count == 0,
            $"""
             #229 PROBE MIS-PLACED — {failures.Count} of 3 insertions are not in their own section.

               {string.Join($"{Environment.NewLine}  ", failures)}

             Presence is not placement, and the clause theory cannot tell the difference: a probe appended to
             the END of the skill carries all fourteen clauses and is read by nobody, because a reviewer
             working section 2's probe list never reaches it. The probe belongs among its siblings in section
             2, the report line in section 6's list, the checklist item under the Quality bar.

             Offsets are compared after the same normalization the clause theory uses, so re-wrapping or
             re-indenting cannot cause this — only moving the text can.
             """);
    }

    /// <summary>
    /// The anchor SET's own hygiene, asked of itself the way this repo asks it of a guardrail: what wrong
    /// edit would this still pass? Two ways the set could rot into ceremony — a clause too short to be
    /// evidence of anything, and two rows pinning the same sentence twice (which reads as broader coverage
    /// than it is). Both are cheap to check and would otherwise be invisible.
    ///
    /// <para>This fact reads no skill text, so it is GREEN the moment the row list is well-formed — which is
    /// the point. Without it, a row list that failed to load at all would be counted as a clean TDD red, and
    /// a census of "fourteen rows, all failing" would not mean fourteen DISTINCT rows.</para>
    /// </summary>
    [Fact]
    public void TheAnchorSetIsEvidence_NotCeremony()
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach ((string skillFile, string clause, string doctrine) in AnchorRows)
        {
            string normalized = Normalize(clause);

            // Short enough to hit by accident is short enough to survive a gutted skill. The terse rows
            // here are the report line's tail and the quality-bar fragment, and both are full clauses
            // rather than tokens, so this floor costs nothing and catches a clause softened to a word.
            Assert.True(normalized.Length >= 19,
                $"Anchor for {skillFile} ({doctrine}) is too short to be evidence: '{normalized}'");

            Assert.False(seen.TryGetValue(normalized, out string? already),
                $"Two anchor rows pin the same clause — '{doctrine}' and '{already}'. The set would " +
                "look broader than it is; give one of them its own distinct clause or drop it.");
            seen[normalized] = doctrine;
        }
    }

    // ---- normalization ------------------------------------------------------------------------------

    /// <summary>
    /// Line endings, indentation, markdown blockquote markers and whitespace runs collapsed away, so an
    /// anchor survives re-wrapping, re-indenting and a CRLF checkout (<c>.claude/skills/**</c> is not
    /// <c>eol=lf</c>-pinned) but not a change of words. Semantics are lifted verbatim from
    /// <see cref="SeamDoctrineAnchorTests"/>, deliberately: two anchor sets over the same skill file that
    /// disagreed about what counts as a match would be a trap for whoever edits it next.
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
