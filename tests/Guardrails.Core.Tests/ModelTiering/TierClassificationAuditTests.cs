using System.Text.Json.Nodes;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// <b>The deterministic half of the model-tiering review net</b> — the regression signal for
/// <see cref="TierClassificationAudit"/>, the folder-observable answer to <i>"which prompt task and which
/// surviving prompt judge did this tiering-configured plan leave unclassified?"</i>
///
/// <para><b>Why this file is the only gate.</b> The settled ruling for this wave allocates no
/// <c>validate</c> check and no GR code: an author who leaves a task on the plan-wide default has done
/// nothing invalid, and the harness does not block on a model-quality opinion. So the review pass owns the
/// finding, and — exactly as #382's T\* rule discovered — a review-pass rule with no executable form is a
/// claim. <see cref="BothFixturesLoadAndValidateClean_BecauseValidateCannotSeeThisDefect"/> is that ruling
/// made executable: both fixtures are plans <c>validate</c> has nothing whatsoever to say about.</para>
///
/// <para><b>Two-sided by construction.</b> <c>TestData/tier-tags/configured/</c> and
/// <c>TestData/tier-tags/untagged/</c> are byte-identical except for ONE key —
/// <c>tasks/01-author-widget-tests/task.json</c>'s <c>action.tier</c> —
/// which <see cref="TheTwoFixturesDifferOnlyInTheMissingTag"/> pins. The audit therefore demonstrably keys
/// on the tag and on nothing else. The cases a committed fixture pair cannot carry (a judge with its
/// frontmatter tier removed, a plan-root judge, a pre-tiering legacy plan) run off temp copies, so the
/// mutants are generated and can never drift out of sync with the shape they mutate.</para>
///
/// <para><b>Group A carries the red; Group B deliberately does not.</b> A silence assertion cannot be red
/// before the feature exists — a legacy folder produces no finding both before and after, so authoring
/// <see cref="LegacyPlan_WithNoTierVocabularyAnywhere_ProducesNothingAtAll"/> as a TDD red would be
/// impossible, and "fixing" it by converting it to a positive would delete this plan's only Invariant-7
/// protection. It is asserted here alongside the positive cases, and the positive cases carry the red.
/// Group C never calls the audit at all: it is the fixtures' own integrity, and it passes the moment the
/// fixtures are authored correctly.</para>
///
/// <para><b>Every "no findings" assertion is preceded by a non-vacuity assertion.</b> An audit reporting
/// nothing because it recognised nothing is green for the wrong reason — the passing-but-blind shape this
/// whole net exists to remove.</para>
/// </summary>
[Trait("Category", "ModelTieringStage3")]
public sealed class TierClassificationAuditTests
{
    /// <summary>The clean side: every subject carries a classification of its own.</summary>
    private const string Configured = "tier-tags/configured";

    /// <summary>The defect: the same plan with <c>01-author-widget-tests</c>'s <c>action.tier</c> removed.</summary>
    private const string Untagged = "tier-tags/untagged";

    private const string AuthorTask = "01-author-widget-tests";
    private const string ImplementTask = "02-implement-widget";
    private const string TuneTask = "03-tune-widget-effort";
    private const string SeedTask = "04-seed-widget-dir";

    /// <summary>The surviving prompt judge on <see cref="AuthorTask"/>, and its subject id.</summary>
    private const string Judge = "02-widget-review";
    private const string JudgeSubject = $"{AuthorTask}/{Judge}";

    /// <summary>The plan-root judge the mutated case adds — it guards no task at all.</summary>
    private const string PlanRootJudge = "01-final-review";
    private const string PlanRootJudgeSubject = $"<plan>/guardrails/{PlanRootJudge}";

    /// <summary>The one file that differs between the two committed fixtures.</summary>
    private const string TaggedTaskJson = $"tasks/{AuthorTask}/task.json";

    // ---- Group A: the audit's behaviour (these MUST FAIL against the throwing stub) ---------------

    /// <summary>
    /// The clean side. Non-vacuity comes FIRST and deliberately: an audit that reports nothing because it
    /// recognised no subject at all would be green for the wrong reason, and this fixture's whole job is
    /// to be the plan where there is genuinely nothing to say.
    /// </summary>
    [Fact]
    public void ConfiguredPlan_FullyTagged_ProducesNoFinding()
    {
        PlanDefinition plan = Load(Configured);

        Assert.True(TierClassificationAudit.IsTieringConfigured(plan),
            "The 'configured' fixture declares a promptRunners routing block and a tiering block; if the " +
            "gate reads false here, every other assertion in this file is silence for the wrong reason.");

        Assert.NotEmpty(TierClassificationAudit.ClassifiableSubjects(plan));

        Assert.Empty(TierClassificationAudit.Audit(plan));
    }

    /// <summary>
    /// The defect side, and the discriminator's payload: one key removed, one finding, naming the task and
    /// the site the fix is made at.
    /// </summary>
    [Fact]
    public void ConfiguredPlan_UntaggedPromptTask_IsAFindingThatNamesTheTask()
    {
        TierClassificationFinding finding = Assert.Single(TierClassificationAudit.Audit(Load(Untagged)));

        Assert.Equal(TierClassificationSubject.PromptTask, finding.Kind);
        Assert.Equal(AuthorTask, finding.SubjectId);

        // "Name the remedy" — a finding that says "this is wrong" without saying where the fix goes sends
        // the author hunting, which is how a review rule stops being applied.
        Assert.Contains("action.tier", finding.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The sharpest assertion in this file.</b> <see cref="PlanLoader"/> RESOLVES the tier — precedence
    /// <c>action.tier</c> &gt; <c>tiering.defaultTier</c> &gt; null — so this untagged task arrives from
    /// the loader carrying <c>"medium"</c>. An audit that read the RESOLVED
    /// <see cref="ActionDefinition.Tier"/> would see a fully classified plan and find nothing here,
    /// forever, on every plan that sets a default; and it would look entirely correct while doing it.
    ///
    /// <para>So this asserts BOTH halves at once: the resolved tier is present AND the task is still
    /// flagged. <see cref="ActionDefinition.TierOrigin"/> exists precisely because the <c>?? defaultTier</c>
    /// collapse would otherwise destroy the answer, and this is the test that spends it.</para>
    /// </summary>
    [Fact]
    public void PlanWideDefaultTier_DoesNotDischargeTheFinding_BecauseItIsResolvedAtLoad()
    {
        PlanDefinition plan = Load(Untagged);

        // First: the loader really did fill the rung in. Without this the assertions below could pass on a
        // plan where nothing resolved at all, which is a different (and uninteresting) fixture.
        ActionDefinition action = Assert.Single(plan.Tasks, t => t.Id == AuthorTask).Action;
        Assert.Equal("medium", action.Tier);
        Assert.Equal(TierOrigin.PlanDefault, action.TierOrigin);

        TierClassificationFinding finding = Assert.Single(TierClassificationAudit.Audit(plan));

        Assert.Equal(AuthorTask, finding.SubjectId);
        Assert.Equal("medium", finding.ResolvedTier);
        Assert.Equal(TierOrigin.PlanDefault, finding.Origin);
    }

    /// <summary>
    /// A script action runs no model, so it can carry no rung. It is therefore not a subject AT ALL —
    /// which is a stronger statement than "a subject that passed", and the reason the census is asserted
    /// here too rather than only the findings.
    /// </summary>
    [Fact]
    public void ScriptActionTask_IsNeverFlagged_ItRunsNoModel()
    {
        PlanDefinition plan = Load(Untagged);

        // Non-vacuity: this plan HAS a finding, so an absence below is a real exclusion rather than an
        // audit that returned nothing.
        Assert.NotEmpty(TierClassificationAudit.Audit(plan));
        Assert.DoesNotContain(TierClassificationAudit.Audit(plan), f => f.SubjectId == SeedTask);

        Assert.NotEmpty(TierClassificationAudit.ClassifiableSubjects(plan));
        Assert.DoesNotContain(SeedTask, TierClassificationAudit.ClassifiableSubjects(plan));
    }

    /// <summary>
    /// Three pins, one method. A task whose author stated the route directly is owed no rung, whichever
    /// spelling they used: <c>action.model</c> and <c>action.effort</c> are already in the committed
    /// fixture, and <c>action.runner</c> is added on a temp copy of the DEFECT side — so the third pin is
    /// proved by the finding DISAPPEARING, not merely by an absence in a plan that had no finding anyway.
    /// </summary>
    [Fact]
    public void PinnedTask_IsNotFlagged_WhetherThePinIsModelRunnerOrEffort()
    {
        IReadOnlyList<TierClassificationFinding> findings = TierClassificationAudit.Audit(Load(Untagged));

        Assert.NotEmpty(findings);
        Assert.DoesNotContain(findings, f => f.SubjectId == ImplementTask);
        Assert.DoesNotContain(findings, f => f.SubjectId == TuneTask);

        RunOnMutatedCopy(Untagged, root =>
        {
            PinRunner(root, AuthorTask, "claude");

            PlanDefinition pinned = LoadFrom(root);

            Assert.NotEmpty(TierClassificationAudit.ClassifiableSubjects(pinned));
            Assert.Empty(TierClassificationAudit.Audit(pinned));
        });
    }

    /// <summary>
    /// <b>The subtlest rule in the wave (SSOT §4.2 / §9.6 rule 2).</b> An absent frontmatter <c>tier</c> on
    /// a judge does not mean undefined — it means <i>the judge's rung follows the actor it guards</i>. So
    /// the same untagged judge is a finding in one plan and correct in another, and the variable is the
    /// ACTOR:
    /// <list type="number">
    ///   <item>on the untagged side the actor is itself unclassified, so there is nothing to follow — two
    ///     findings, the task and its judge;</item>
    ///   <item>on the configured side the actor carries <c>action.tier</c>, so the judge follows it — no
    ///     finding. Flagging this would fire on almost every configured plan, which is how a check gets
    ///     muted;</item>
    ///   <item>a plan-root judge guards no task at all, so no actor exists to follow — a finding, even on
    ///     the otherwise-clean configured plan.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void UntaggedJudge_IsAFindingOnlyWhenItHasNoClassifiedActorToFollow()
    {
        // (1) Unclassified actor ⇒ the judge has nothing to follow.
        RunOnMutatedCopy(Untagged, root =>
        {
            RemoveJudgeFrontmatterTier(TaskJudgePath(root));

            IReadOnlyList<TierClassificationFinding> findings =
                TierClassificationAudit.Audit(LoadFrom(root));

            Assert.Equal(2, findings.Count);
            Assert.Contains(findings, f => f.SubjectId == AuthorTask);
            AssertJudgeFinding(findings, JudgeSubject);
        });

        // (2) Classified actor ⇒ the judge follows it. The audit must still SEE the judge, so the silence
        // is a decision rather than a blind spot.
        RunOnMutatedCopy(Configured, root =>
        {
            RemoveJudgeFrontmatterTier(TaskJudgePath(root));

            PlanDefinition plan = LoadFrom(root);

            Assert.Contains(JudgeSubject, TierClassificationAudit.ClassifiableSubjects(plan));
            Assert.Empty(TierClassificationAudit.Audit(plan));
        });

        // (3) A plan-root judge guards no task, so there is no actor to follow at any classification.
        RunOnMutatedCopy(Configured, root =>
        {
            AddPlanRootJudge(root);

            AssertJudgeFinding(TierClassificationAudit.Audit(LoadFrom(root)), PlanRootJudgeSubject);
        });
    }

    /// <summary>
    /// The census itself. Without it, "the audit reported nothing" and "the audit recognised nothing" are
    /// the same observation — and every silence assertion in this file rests on telling them apart.
    /// </summary>
    [Fact]
    public void TheAuditNamesWhatItSaw_SoAnEmptyResultIsNotAVacuousOne()
    {
        IReadOnlyList<string> subjects = TierClassificationAudit.ClassifiableSubjects(Load(Configured));

        // Compared as a SET, which is what this test actually means and what its own prompt specified:
        // "ClassifiableSubjects names the three prompt tasks and the one prompt judge, and nothing else."
        // Ordering was never part of the claim, and asserting it made the test UNSATISFIABLE by any
        // implementation: JudgeSubject is $"{AuthorTask}/{Judge}", so under StringComparer.Ordinal it sorts
        // SECOND (a prefix sorts before the longer string), never fourth — while the expected array was
        // written in population order (prompt tasks, then judges). Both sides are fixed, so no
        // implementation could reconcile them; the audit was complete and correct when this was found.
        Assert.Equal(
            new[] { AuthorTask, ImplementTask, TuneTask, JudgeSubject }.OrderBy(s => s, StringComparer.Ordinal),
            subjects.OrderBy(s => s, StringComparer.Ordinal));
    }

    // ---- Group B: the graceful skip (asserted, NOT authored as a TDD red) -------------------------

    /// <summary>
    /// <b>DoR Invariant 7, executable.</b> A plan as it would have been generated before tiering shipped —
    /// no <c>routing</c>, no <c>tiering</c>, no tags and no pins anywhere — must produce NOTHING. Not a
    /// softer finding, not an advisory: a single-model user who never asked for any of this must never be
    /// told their plan is under-classified.
    ///
    /// <para><b>This is not a TDD red and must not be converted into one.</b> A silence assertion cannot
    /// fail before the feature exists — this folder yields no finding both before and after. Its value is
    /// entirely as a REGRESSION guard on the day the audit ships, which is exactly when the gate is easiest
    /// to drop.</para>
    /// </summary>
    [Fact]
    public void LegacyPlan_WithNoTierVocabularyAnywhere_ProducesNothingAtAll()
    {
        RunOnMutatedCopy(Untagged, root =>
        {
            StripTieringConfig(root);
            StripTaskTierVocabulary(root);
            RemoveJudgeFrontmatterTier(TaskJudgePath(root));

            PlanDefinition plan = LoadFrom(root);

            // Non-vacuity, in the only form available here: the POPULATION the audit would classify is
            // still fully present — three prompt actions and a prompt judge — so the silence below is the
            // gate's doing and not an empty plan's.
            Assert.Equal(3, plan.Tasks.Count(t => t.Action.Kind == ActionKind.Prompt));
            Assert.Contains(
                Assert.Single(plan.Tasks, t => t.Id == AuthorTask).Guardrails,
                g => g.Kind == ActionKind.Prompt);

            // And the folder really is pre-tiering: nothing anywhere carries a rung.
            Assert.All(plan.Tasks, t => Assert.Null(t.Action.Tier));
            Assert.All(plan.Tasks.SelectMany(t => t.Guardrails), g => Assert.Null(g.Tier));

            Assert.False(TierClassificationAudit.IsTieringConfigured(plan));
            Assert.Empty(TierClassificationAudit.Audit(plan));
        });
    }

    /// <summary>
    /// The gate is the CONFIGURATION, never the tags. Only the <c>routing</c> and <c>tiering</c> blocks are
    /// removed here and every tag is left exactly as it is — so the one variable between this plan and the
    /// one <see cref="ConfiguredPlan_UntaggedPromptTask_IsAFindingThatNamesTheTask"/> flags is whether the
    /// plan opted in.
    ///
    /// <para>This is also the plan the validator already reports on its own — tags with no routing anywhere
    /// is <c>GR2049 TieringInert</c> — which is why the review probe defers to it rather than duplicating
    /// it with a second, quieter finding.</para>
    /// </summary>
    [Fact]
    public void RemovingOnlyTheTieringMetadata_SilencesTheFinding_TheTagsAreUntouched()
    {
        RunOnMutatedCopy(Untagged, root =>
        {
            StripTieringConfig(root);

            PlanDefinition plan = LoadFrom(root);

            // Non-vacuity: the tags really are still there. If a future edit to StripTieringConfig also
            // stripped them, this test would silently become a duplicate of the legacy-plan case above.
            Assert.Equal("hard", Assert.Single(
                Assert.Single(plan.Tasks, t => t.Id == AuthorTask).Guardrails,
                g => g.Kind == ActionKind.Prompt).Tier);
            Assert.Equal("claude-opus-5", Assert.Single(plan.Tasks, t => t.Id == ImplementTask).Action.Model);
            Assert.Equal("xhigh", Assert.Single(plan.Tasks, t => t.Id == TuneTask).Action.Effort);

            Assert.False(TierClassificationAudit.IsTieringConfigured(plan));
            Assert.Empty(TierClassificationAudit.Audit(plan));
        });
    }

    // ---- Group C: the fixtures' own integrity (never calls the audit) ----------------------------

    /// <summary>
    /// The two-sidedness proof. If the fixtures differed in anything else — a description, a writeScope
    /// entry, a second guardrail — the audit could be keying on that instead of on the tag, and the pair
    /// would be a snapshot rather than a discriminator.
    /// </summary>
    [Fact]
    public void TheTwoFixturesDifferOnlyInTheMissingTag()
    {
        Dictionary<string, string> configured = FixtureContents(Configured);
        Dictionary<string, string> untagged = FixtureContents(Untagged);

        Assert.Equal(
            configured.Keys.OrderBy(k => k, StringComparer.Ordinal),
            untagged.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (string shared in configured.Keys.Where(k => k != TaggedTaskJson))
        {
            Assert.True(configured[shared] == untagged[shared],
                $"The two tier-tag fixtures have drifted at '{shared}'. They must differ ONLY in whether " +
                $"'{TaggedTaskJson}' carries an action.tier — any other difference lets the classification " +
                "audit pass for a reason other than the missing tag, which turns this pair back into a " +
                "snapshot of one plan instead of a discriminator between two.");
        }

        Assert.NotEqual(configured[TaggedTaskJson], untagged[TaggedTaskJson]);

        // ...and the difference is exactly the one key, not merely somewhere inside that one file.
        JsonObject configuredTask = ParseObject(configured[TaggedTaskJson]);
        JsonObject untaggedTask = ParseObject(untagged[TaggedTaskJson]);

        JsonObject configuredAction = Assert.IsType<JsonObject>(configuredTask["action"]);
        JsonObject untaggedAction = Assert.IsType<JsonObject>(untaggedTask["action"]);

        Assert.Equal(new[] { "tier" }, configuredAction.Select(p => p.Key));
        Assert.Equal("medium", (string?)configuredAction["tier"]);
        Assert.Empty(untaggedAction);

        configuredTask.Remove("action");
        untaggedTask.Remove("action");
        Assert.Equal(configuredTask.ToJsonString(), untaggedTask.ToJsonString());
    }

    /// <summary>
    /// <b>The no-GR-code ruling, made executable.</b> Both fixtures are plans <c>guardrails validate</c>
    /// has NOTHING to say about — no error, no warning. That is exactly why this defect could not ship as a
    /// lint and why the review pass is the only gate: the shape is entirely legal, and an author who leaves
    /// a task on the plan-wide default has done nothing wrong by the schema.
    ///
    /// <para><b>If this goes red, fix the FIXTURE, never this assertion.</b> A diagnostic firing here means
    /// the fixture drew an unrelated lint and is no longer the plan this file is about. If it cannot be
    /// fixed without changing what the fixture IS, that is a needsHuman — not a relaxed assertion.</para>
    /// </summary>
    [Theory]
    [InlineData(Configured)]
    [InlineData(Untagged)]
    public void BothFixturesLoadAndValidateClean_BecauseValidateCannotSeeThisDefect(string fixture)
    {
        IReadOnlyList<Diagnostic> diagnostics =
            new PlanValidator(FakeExecutableProbe.All).Validate(Load(fixture));

        Assert.True(diagnostics.Count == 0,
            $"'{fixture}' is meant to be a plan `validate` has NOTHING to say about — that is the " +
            "no-GR-code ruling made executable. Diagnostics:\n" +
            string.Join("\n", diagnostics.Select(d => $"  {d.Severity} {d.Code}: {d.Message}")));
    }

    // ---- assertions shared by the judge cases -----------------------------------------------------

    /// <summary>
    /// The judge half of a finding, asserted the same way wherever it appears.
    /// <see cref="GuardrailDefinition.Tier"/> is bound from frontmatter and from NOTHING else — there is no
    /// plan-wide default standing behind a judge — so an untagged judge's resolved tier is null and its
    /// origin is <see cref="TierOrigin.None"/>, and the remedy is a frontmatter <c>tier</c> rather than the
    /// <c>action.tier</c> a task would be told to add.
    /// </summary>
    private static void AssertJudgeFinding(
        IReadOnlyList<TierClassificationFinding> findings, string subjectId)
    {
        TierClassificationFinding finding = Assert.Single(findings, f => f.SubjectId == subjectId);

        Assert.Equal(TierClassificationSubject.PromptJudge, finding.Kind);
        Assert.Null(finding.ResolvedTier);
        Assert.Equal(TierOrigin.None, finding.Origin);

        Assert.Contains("frontmatter", finding.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("action.tier", finding.Detail, StringComparison.Ordinal);
    }

    // ---- loading ----------------------------------------------------------------------------------

    private static PlanDefinition Load(string fixture) => LoadFrom(TestPaths.Fixture(fixture));

    private static PlanDefinition LoadFrom(string planDir)
    {
        PlanLoadResult result = new PlanLoader().Load(planDir);
        Assert.False(result.HasErrors,
            string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        Assert.NotNull(result.Plan);
        return result.Plan!;
    }

    /// <summary>Every fixture file, keyed by forward-slashed relative path, line-endings normalized.</summary>
    private static Dictionary<string, string> FixtureContents(string fixture)
    {
        string root = TestPaths.Fixture(fixture);
        return Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path).Replace('\\', '/'),
                path => File.ReadAllText(path).Replace("\r\n", "\n").Replace("\r", "\n"),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Copies a committed fixture to a temp folder, hands the copy to <paramref name="body"/>, and always
    /// cleans up. The committed fixtures stay pristine; the mutants are GENERATED, so they can never drift
    /// out of sync with the shape they mutate.
    /// </summary>
    private static void RunOnMutatedCopy(string fixture, Action<string> body)
    {
        string root = Path.Combine(Path.GetTempPath(), "gr-tier-tags-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyTree(TestPaths.Fixture(fixture), root);
            body(root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    // ---- mutations ---------------------------------------------------------------------------------

    private static string TaskJudgePath(string root) =>
        Path.Combine(root, "tasks", AuthorTask, "guardrails", $"{Judge}.prompt.md");

    /// <summary>
    /// Drops the frontmatter <c>tier:</c> line and leaves the rest of the file — including the
    /// <c>catches:</c> declaration — exactly as it was, so the mutant differs from the committed judge in
    /// the tag and in nothing else.
    /// </summary>
    private static void RemoveJudgeFrontmatterTier(string judgePath)
    {
        string[] kept =
        [
            .. File.ReadAllLines(judgePath)
                   .Where(line => !line.TrimStart().StartsWith("tier:", StringComparison.OrdinalIgnoreCase))
        ];

        Assert.DoesNotContain(kept, line => line.Contains("tier:", StringComparison.OrdinalIgnoreCase));
        File.WriteAllLines(judgePath, kept);
    }

    /// <summary>Adds an <c>action.runner</c> pin — the third pin spelling, the one no fixture carries.</summary>
    private static void PinRunner(string root, string taskId, string runner)
    {
        string path = TaskJsonPath(root, taskId);
        JsonObject task = ParseObject(File.ReadAllText(path));

        JsonObject action = task["action"] as JsonObject ?? new JsonObject();
        action["runner"] = runner;
        task["action"] = action;

        WriteJson(path, task);
    }

    /// <summary>
    /// Adds a plan-root <c>&lt;plan&gt;/guardrails/</c> judge carrying no frontmatter <c>tier</c>. It DOES
    /// carry a <c>catches:</c> declaration — the plan-root folder enforces one (GR2027) and a load error
    /// would make this a test about the loader instead of about the audit.
    /// </summary>
    private static void AddPlanRootJudge(string root)
    {
        string dir = Path.Combine(root, "guardrails");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, $"{PlanRootJudge}.prompt.md"),
            """
            ---
            catches: a plan that assembled but whose parts never agreed with one another.
            ---

            FIXTURE JUDGE - loaded by the tier-classification audit meta-test, never executed.

            Read the merged result and answer PASS or FAIL.

            """);
    }

    /// <summary>
    /// Removes the CONFIGURATION half only — every block's <c>routing</c> and the top-level
    /// <c>tiering</c> — and touches no tag anywhere. This is what makes the plan unconfigured for tiering
    /// (the same two-part condition <c>NoRoutingGolden.IsUnconfiguredForTiering</c> reads).
    /// </summary>
    private static void StripTieringConfig(string root)
    {
        string path = Path.Combine(root, "guardrails.json");
        JsonObject config = ParseObject(File.ReadAllText(path));

        config.Remove("tiering");
        if (config["promptRunners"] is JsonObject runners)
        {
            foreach (KeyValuePair<string, JsonNode?> entry in runners)
            {
                (entry.Value as JsonObject)?.Remove("routing");
            }
        }

        WriteJson(path, config);
    }

    /// <summary>
    /// Removes every action-level tier/route key from every task — the rest of what a pre-tiering plan
    /// simply never had.
    /// </summary>
    private static void StripTaskTierVocabulary(string root)
    {
        foreach (string path in Directory.EnumerateFiles(
                     Path.Combine(root, "tasks"), "task.json", SearchOption.AllDirectories))
        {
            JsonObject task = ParseObject(File.ReadAllText(path));
            if (task["action"] is not JsonObject action)
            {
                continue;
            }

            foreach (string key in new[] { "tier", "model", "runner", "effort" })
            {
                action.Remove(key);
            }

            WriteJson(path, task);
        }
    }

    private static string TaskJsonPath(string root, string taskId) =>
        Path.Combine(root, "tasks", taskId, "task.json");

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json) as JsonObject ??
        throw new InvalidOperationException($"expected a JSON object, got: {json}");

    private static void WriteJson(string path, JsonObject node) =>
        File.WriteAllText(path, node.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        }));
}
