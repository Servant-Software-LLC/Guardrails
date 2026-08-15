using System.Text.Json.Nodes;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// Stage 1.5 — the reconciliation that makes the SHIPPED schema match the design of record
/// (<c>docs/plans/17-model-tiering.md</c> §4.1/§4.2/§4.3/§5/§6.2/§6.5.1/§12.1/§12.3, issue #201).
/// Stage 1 landed green against a materially different shape; this file pins the corrected one.
///
/// <para>What it covers, in the order the DoR states it:</para>
/// <list type="number">
///   <item><b><c>routing.tiers</c></b> — REQUIRED and machine-consumed, GR2047 on missing/empty/wrong
///     type/out-of-enum (§4.2).</item>
///   <item><b><c>tiering.verifier.minTier</c></b> — parses, validates (GR2043) and round-trips. It is a
///     FLOOR, not a default (§6.5.1); no resolver exists yet, so this stage proves the CONTRACT and
///     nothing about resolution.</item>
///   <item><b>GR2043 at all FOUR tier-bearing sites</b> — <c>action.tier</c>, judge frontmatter
///     <c>tier</c>, <c>tiering.defaultTier</c>, <c>tiering.verifier.minTier</c> (§5, §13.3's recorded
///     gap).</item>
///   <item><b><c>effort</c></b> — on the block and on the action, GR2050 shape check (§4.2, §12.3).</item>
///   <item><b>GR2049 <c>TieringInert</c></b> — tags with no <c>routing</c> block anywhere (§4.2).</item>
///   <item><b>GR2048 <c>UnservableTier</c></b> — a used tier with no candidate at-or-above, with the
///     two causes told apart (§6.2, §14.1, settled OD-G).</item>
///   <item><b>Invariant 7</b> — a config touching none of this validates byte-identically.</item>
/// </list>
///
/// <para><b>Everything goes through the REAL pipeline</b> — <see cref="PlanLoader"/> then
/// <see cref="PlanValidator"/> — because "validates" is a claim about that pipeline, not about a record's
/// property initializers. Diagnostics are pooled from both phases: WHICH layer catches a malformed value
/// is an implementation choice, while THAT <c>guardrails validate</c> reports it is the contract.</para>
///
/// <para><b>These assert on <see cref="DiagnosticCodes"/> constants, unlike the Stage-1 files beside
/// them.</b> Stage 1's tests deliberately asserted on message text because the codes were the implement
/// task's to allocate and pinning a number would have pinned a claim that task had no authority over.
/// Stage 1.5 IS that allocation — GR2047–GR2050, re-verified against <c>DiagnosticCodes.cs</c> at landing
/// — so the codes are now contract, and asserting them is what makes a future renumber fail loudly.</para>
/// </summary>
public sealed class TierRoutingSchemaTests : IDisposable
{
    private readonly string _root;

    public TierRoutingSchemaTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-mt15-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // ── 1. routing.tiers — required, machine-consumed (GR2047) ─────────────────────────────────

    /// <summary>
    /// A <c>routing</c> block with a well-formed <c>tiers</c> list parses, validates, and round-trips —
    /// and the values arrive in the order and spelling the author wrote them. The order matters because
    /// nothing normalises it: <c>tiers</c> is the author's statement of which rungs the route may serve,
    /// and the resolver reads it as written.
    /// </summary>
    [Fact]
    public void RoutingTiers_ParseValidateAndRoundTrip()
    {
        Loaded first = Load(ConfigWith("""
            "sonnet": {
              "command": "claude", "model": "claude-sonnet-4-5", "strength": 3,
              "routing": { "tiers": ["medium", "hard"], "notes": "typical single-module coding" }
            }
            """, defaultRunner: "sonnet"));

        AssertNoErrors(first);
        PromptRunnerRouting routing = Assert.IsType<PromptRunnerRouting>(first.Runner("sonnet").Routing);
        Assert.Equal(["medium", "hard"], routing.Tiers);
        Assert.Equal("typical single-module coding", routing.Notes);

        Loaded second = Load(ConfigWith(RenderRoutingBlock("sonnet", routing), defaultRunner: "sonnet"));

        AssertNoErrors(second);
        Assert.Equal(routing.Tiers, second.Runner("sonnet").Routing!.Tiers);
        Assert.Equal(routing.Notes, second.Runner("sonnet").Routing!.Notes);
    }

    /// <summary>
    /// Every accepted tier token is accepted inside <c>routing.tiers</c>, including a single-element list.
    /// The one-element case is the shape a reserved-ish "this block only does hard work" block takes, and
    /// it is exactly the shape a naive "a list means more than one" implementation would get wrong.
    /// </summary>
    [Theory]
    [InlineData("[\"easy\"]")]
    [InlineData("[\"medium\"]")]
    [InlineData("[\"hard\"]")]
    [InlineData("[\"easy\", \"medium\", \"hard\"]")]
    public void RoutingTiers_AcceptsEveryRecognizedTierToken(string tiersJson)
    {
        Loaded loaded = Load(ConfigWith(
            $$"""
            "block": { "command": "claude", "routing": { "tiers": {{tiersJson}} } }
            """,
            defaultRunner: "block"));

        AssertNoErrors(loaded);
        Assert.NotEmpty(loaded.Runner("block").Routing!.Tiers);
    }

    /// <summary>
    /// A <c>routing</c> block with NO <c>tiers</c> is GR2047. This is the case that made Stage 1's shape
    /// materially different from the design's: the shipped block carried only <c>guidance</c>/<c>tags</c>,
    /// so the candidacy predicate had no tier field to read at all — every such block would have opted
    /// into routing and then never been selected, silently.
    /// </summary>
    [Fact]
    public void RoutingWithoutTiers_IsGr2047()
    {
        Loaded loaded = Load(ConfigWith("""
            "block": { "command": "claude", "routing": { "guidance": "prefer me for refactors" } }
            """, defaultRunner: "block"));

        Diagnostic error = RequireCode(loaded, DiagnosticCodes.MalformedRoutingGuidance);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("tiers", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The malformed shapes of <c>tiers</c>, each GR2047 and each naming what is wrong. An EMPTY list is
    /// included deliberately: it is not a type error and would deserialize perfectly, yet it serves no
    /// rung at all — indistinguishable in effect from having no routing block, which is a thing the
    /// author could have said honestly instead.
    /// </summary>
    [Theory]
    [InlineData("[]", "EMPTY")]                       // present, well-typed, and serves nothing
    [InlineData("\"medium\"", "ARRAY")]               // a bare string where a list belongs
    [InlineData("[\"medium\", \"extreme\"]", "extreme")] // an out-of-enum token beside a good one
    [InlineData("[\"hard \"]", "hard ")]              // verbatim matching: a stray trailing space
    [InlineData("[\"Medium\"]", "Medium")]            // verbatim matching: no case-folding
    [InlineData("[1, 2]", "1")]                       // numbers where tier tokens belong
    public void MalformedRoutingTiers_IsGr2047_NamingTheProblem(string tiersJson, string mustMention)
    {
        Loaded loaded = Load(ConfigWith(
            $$"""
            "block": { "command": "claude", "routing": { "tiers": {{tiersJson}} } }
            """,
            defaultRunner: "block"));

        Diagnostic error = RequireCode(loaded, DiagnosticCodes.MalformedRoutingGuidance);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains(mustMention, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two bad tokens in one list produce TWO diagnostics, and the good token beside them still parses.
    /// Reporting only the first would send the author round the loop once per typo; dropping the whole
    /// list on the first error would then cascade into a spurious GR2048 for a rung they DID declare.
    /// </summary>
    [Fact]
    public void MultipleBadTierTokens_EachGetTheirOwnDiagnostic_AndTheGoodOneSurvives()
    {
        Loaded loaded = Load(ConfigWith("""
            "block": { "command": "claude", "routing": { "tiers": ["easy", "extreme", "trivial"] } }
            """, defaultRunner: "block"));

        Assert.Equal(2, loaded.Diagnostics.Count(d => d.Code == DiagnosticCodes.MalformedRoutingGuidance));
        Assert.Equal(["easy"], loaded.Runner("block").Routing!.Tiers);
    }

    // ── 2. tiering.verifier.minTier — the FLOOR (§6.5.1) ───────────────────────────────────────

    /// <summary>
    /// <c>tiering.verifier.minTier</c> parses onto the model and round-trips. There is no resolver yet, so
    /// what this stage owes the design is exactly this: the key exists, survives, and is judged.
    /// </summary>
    [Theory]
    [InlineData("easy")]
    [InlineData("medium")]
    [InlineData("hard")]
    public void VerifierMinTier_ParsesAndRoundTrips(string tier)
    {
        Loaded loaded = Load(ConfigWith(
            "\"claude\": { \"command\": \"claude\" }",
            defaultRunner: "claude",
            tiering: $$"""{ "verifier": { "minTier": "{{tier}}" } }"""));

        AssertNoErrors(loaded);
        Assert.Equal(tier, loaded.Plan!.Config.Tiering?.Verifier?.MinTier);
    }

    /// <summary>
    /// The <c>verifier</c> block is OPTIONAL, and so is <c>minTier</c> within it. Absent ⇒ null at each
    /// level, never a fabricated floor: a defaulted <c>minTier</c> would apply a plan-wide judge policy
    /// nobody configured, which is the same "never invent an unstated axis" rule the registry follows.
    /// </summary>
    [Fact]
    public void VerifierBlock_IsOptional_AndNeverFabricated()
    {
        Loaded withTieringOnly = Load(ConfigWith(
            "\"claude\": { \"command\": \"claude\" }",
            defaultRunner: "claude",
            tiering: """{ "defaultTier": "medium" }"""));

        AssertNoErrors(withTieringOnly);
        Assert.Equal("medium", withTieringOnly.Plan!.Config.Tiering?.DefaultTier);
        Assert.Null(withTieringOnly.Plan!.Config.Tiering?.Verifier);

        Loaded emptyVerifier = Load(ConfigWith(
            "\"claude\": { \"command\": \"claude\" }",
            defaultRunner: "claude",
            tiering: """{ "verifier": { } }"""));

        AssertNoErrors(emptyVerifier);
        Assert.NotNull(emptyVerifier.Plan!.Config.Tiering?.Verifier);
        Assert.Null(emptyVerifier.Plan!.Config.Tiering!.Verifier!.MinTier);
    }

    // ── 3. GR2043 at all FOUR tier-bearing sites (§13.3) ───────────────────────────────────────

    /// <summary>
    /// GR2043 fires at every one of the four sites a tier can be declared. Stage 1 shipped it at exactly
    /// TWO (<c>action.tier</c>, <c>tiering.defaultTier</c>); the other two — a judge guardrail's
    /// frontmatter <c>tier</c> and <c>tiering.verifier.minTier</c> — were not merely unvalidated, they
    /// were not parsed at all. A site validated at one declaration point but not another is exactly how a
    /// typo survives into a run.
    /// </summary>
    [Theory]
    [InlineData(TierSite.ActionTier)]
    [InlineData(TierSite.JudgeFrontmatter)]
    [InlineData(TierSite.DefaultTier)]
    [InlineData(TierSite.VerifierMinTier)]
    public void UnrecognizedTier_IsGr2043_AtEveryDeclarationSite(TierSite site)
    {
        Loaded loaded = LoadWithBadTierAt(site, "wizard");

        Diagnostic error = RequireCode(loaded, DiagnosticCodes.InvalidTierValue);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("wizard", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The mirror of the above: a RECOGNIZED tier at each site validates clean and reaches the model.</summary>
    [Theory]
    [InlineData(TierSite.ActionTier)]
    [InlineData(TierSite.JudgeFrontmatter)]
    [InlineData(TierSite.DefaultTier)]
    [InlineData(TierSite.VerifierMinTier)]
    public void RecognizedTier_IsAcceptedAtEveryDeclarationSite(TierSite site)
    {
        Loaded loaded = LoadWithBadTierAt(site, "hard");

        AssertNoErrors(loaded);
    }

    /// <summary>
    /// A judge guardrail's frontmatter <c>tier</c> reaches <see cref="GuardrailDefinition.Tier"/> —
    /// alongside the <c>scope</c> key that already lived there. Asserting <c>scope</c> too is the point:
    /// the frontmatter reader used to bail the moment <c>scope</c> was absent, so a naive addition of
    /// <c>tier</c> would have made the two keys mutually exclusive.
    /// </summary>
    [Fact]
    public void JudgeFrontmatterTier_ReachesTheGuardrailDefinition_AlongsideScope()
    {
        string planDir = PlanFolder(ConfigWith("\"claude\": { \"command\": \"claude\" }", "claude"));
        WritePromptGuardrail(planDir, "02-judge", """
            ---
            catches: a plausible-but-wrong implementation the deterministic checks cannot see
            scope: local
            tier: hard
            ---
            You are a verifier.
            """);

        Loaded loaded = LoadFolder(planDir);

        AssertNoErrors(loaded);
        GuardrailDefinition judge = Assert.Single(
            loaded.Plan!.Tasks[0].Guardrails, g => g.Name == "02-judge");
        Assert.Equal("hard", judge.Tier);
        Assert.Equal("local", judge.Scope);
    }

    /// <summary>
    /// A prompt guardrail with NO frontmatter <c>tier</c> keeps a null one — the judge simply follows the
    /// actor's rung. Nothing is back-filled, at this site or any other.
    /// </summary>
    [Fact]
    public void JudgeWithoutFrontmatterTier_StaysUntagged()
    {
        string planDir = PlanFolder(ConfigWith("\"claude\": { \"command\": \"claude\" }", "claude"));
        WritePromptGuardrail(planDir, "02-judge", """
            ---
            catches: a plausible-but-wrong implementation
            scope: local
            ---
            You are a verifier.
            """);

        Loaded loaded = LoadFolder(planDir);

        AssertNoErrors(loaded);
        Assert.Null(Assert.Single(loaded.Plan!.Tasks[0].Guardrails, g => g.Name == "02-judge").Tier);
    }

    // ── 4. effort — block and action (GR2050) ──────────────────────────────────────────────────

    /// <summary>
    /// <c>effort</c> parses at BOTH sites and round-trips as the opaque token it is. Nothing consumes it
    /// yet (Stage 2's resolver is its first reader), so what is owed here is that the value survives the
    /// pipeline unaltered — an <c>effort</c> the harness "helpfully" normalised would reach a runner CLI
    /// as a spelling the vendor never defined.
    /// </summary>
    [Fact]
    public void Effort_ParsesAtBothSites_AndSurvivesVerbatim()
    {
        string planDir = PlanFolder(
            ConfigWith("""
                "opus-xhigh": { "command": "claude", "model": "claude-opus-4-6", "effort": "xhigh" }
                """, defaultRunner: "opus-xhigh"),
            taskJson: """{ "description": "t", "dependsOn": [], "writeScope": [], "action": { "effort": "low" } }""");

        Loaded loaded = LoadFolder(planDir);

        AssertNoErrors(loaded);
        Assert.Equal("xhigh", loaded.Runner("opus-xhigh").Effort);
        Assert.Equal("low", loaded.Plan!.Tasks[0].Action.Effort);
    }

    /// <summary>
    /// A malformed <c>effort</c> is GR2050 at either site — the GR2030 shape check applied to the other
    /// opaque vendor token. There is no enumerable set of legal efforts to check membership against, so
    /// this stays a cheap zero-false-positive shape test: empty, blank, or carrying whitespace/control
    /// characters no real token ever contains.
    /// </summary>
    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\" xhigh\"")]
    [InlineData("\"very high\"")]
    public void MalformedBlockEffort_IsGr2050(string effortJson)
    {
        Loaded loaded = Load(ConfigWith(
            $$"""
            "block": { "command": "claude", "effort": {{effortJson}} }
            """,
            defaultRunner: "block"));

        Assert.Equal(DiagnosticSeverity.Error, RequireCode(loaded, DiagnosticCodes.EffortInvalid).Severity);
    }

    /// <summary>The action-side half of the same rule — <c>task.json action.effort</c>.</summary>
    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"  \"")]
    [InlineData("\"x high\"")]
    public void MalformedActionEffort_IsGr2050(string effortJson)
    {
        string planDir = PlanFolder(
            ConfigWith("\"claude\": { \"command\": \"claude\" }", "claude"),
            taskJson:
                $$"""{ "description": "t", "dependsOn": [], "writeScope": [], "action": { "effort": {{effortJson}} } }""");

        Loaded loaded = LoadFolder(planDir);

        Assert.Equal(DiagnosticSeverity.Error, RequireCode(loaded, DiagnosticCodes.EffortInvalid).Severity);
    }

    // ── 5. GR2049 TieringInert ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tier tags with NO <c>routing</c> block anywhere: tiering is not CONFIGURED, so the tags are inert
    /// and the plan runs by legacy resolution. A WARNING — the plan is entirely runnable and its behaviour
    /// is today's behaviour, so failing it would break the legitimate order of tagging before registering
    /// providers. Not silence either: the author believes they are routing.
    /// </summary>
    [Fact]
    public void TierTagsWithNoRoutingBlockAnywhere_IsGr2049Warning_AndTheRunStillLoads()
    {
        string planDir = PlanFolder(
            ConfigWith("\"claude\": { \"command\": \"claude\" }", "claude"),
            taskJson: """{ "description": "t", "dependsOn": [], "writeScope": [], "action": { "tier": "hard" } }""");

        Loaded loaded = LoadFolder(planDir);

        Diagnostic warning = RequireCode(loaded, DiagnosticCodes.TieringInert);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("routing", warning.Message, StringComparison.Ordinal);

        // A warning, so the plan still LOADS and still validates — the tag just does nothing.
        AssertNoErrors(loaded);
        Assert.Equal("hard", loaded.Plan!.Tasks[0].Action.Tier);
    }

    /// <summary>
    /// GR2049 is reported ONCE per plan, however many tasks carry tags. It is one fact about the config,
    /// and N copies of it would train an operator to scroll past the diagnostic that matters.
    /// </summary>
    [Fact]
    public void TieringInert_IsReportedOncePerPlan_NotOncePerTaggedTask()
    {
        string planDir = PlanFolder(ConfigWith("\"claude\": { \"command\": \"claude\" }", "claude"));
        AddTask(planDir, "02-task", """{ "description": "t2", "dependsOn": [], "writeScope": [], "action": { "tier": "easy" } }""");
        AddTask(planDir, "03-task", """{ "description": "t3", "dependsOn": [], "writeScope": [], "action": { "tier": "hard" } }""");
        WriteTaskJson(planDir, "01-task", """{ "description": "t1", "dependsOn": [], "writeScope": [], "action": { "tier": "medium" } }""");

        Loaded loaded = LoadFolder(planDir);

        Assert.Single(loaded.Diagnostics, d => d.Code == DiagnosticCodes.TieringInert);
    }

    /// <summary>
    /// The gate that keeps GR2049 out of a single-model user's way: NO tags anywhere ⇒ no warning, even
    /// though there is equally no routing block. The warning is about a MISMATCH (tags without routing),
    /// never about the absence of tiering, which is the overwhelmingly common and entirely correct state.
    /// </summary>
    [Fact]
    public void NoTagsAndNoRouting_IsSilent()
    {
        Loaded loaded = Load(ConfigWith("\"claude\": { \"command\": \"claude\" }", "claude"));

        Assert.DoesNotContain(loaded.Diagnostics, d => d.Code == DiagnosticCodes.TieringInert);
    }

    /// <summary>
    /// With a <c>routing</c> block present, tiering IS configured and GR2049 cannot fire — even for a tier
    /// that block does not serve. That case is GR2048's, and the two must never both fire for one
    /// condition: "your tags do nothing" and "this specific tier cannot be served" are different claims
    /// and only one of them is true at a time.
    /// </summary>
    [Fact]
    public void TieringConfigured_NeverEmitsGr2049_EvenForAnUnservedTier()
    {
        string planDir = PlanFolder(
            ConfigWith("""
                "kimi": { "command": "kimi", "routing": { "tiers": ["easy"] } }
                """, defaultRunner: "kimi"),
            taskJson: """{ "description": "t", "dependsOn": [], "writeScope": [], "action": { "tier": "hard" } }""");

        Loaded loaded = LoadFolder(planDir);

        Assert.DoesNotContain(loaded.Diagnostics, d => d.Code == DiagnosticCodes.TieringInert);
        RequireCode(loaded, DiagnosticCodes.UnservableTier);
    }

    // ── 6. GR2048 UnservableTier ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Cause (a): NOTHING declares the rung. The fix is to widen a block's <c>routing.tiers</c> or
    /// register a block, and the message says so without mentioning <c>costly</c> — which would send the
    /// operator hunting for a flag nobody set.
    /// </summary>
    [Fact]
    public void UsedTierNoBlockDeclares_IsGr2048_NamingTheNothingServesItCause()
    {
        string planDir = PlanFolder(
            ConfigWith("""
                "kimi": { "command": "kimi", "strength": 2, "routing": { "tiers": ["easy"] } }
                """, defaultRunner: "kimi"),
            taskJson: """{ "description": "t", "dependsOn": [], "writeScope": [], "action": { "tier": "hard" } }""");

        Loaded loaded = LoadFolder(planDir);

        Diagnostic error = RequireCode(loaded, DiagnosticCodes.UnservableTier);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("hard", error.Message, StringComparison.Ordinal);
        Assert.Contains("01-task", error.Message, StringComparison.Ordinal);
        Assert.Contains("NO promptRunners block declares", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("costly", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Cause (b): blocks DO declare the rung, and every one of them is <c>costly: true</c>. This is the
    /// §14.1 cliff — the change a cost-conscious user reaches for on day one — and the DoR is explicit
    /// that it is INTENDED behaviour, not a rough edge: the config is now saying, checkably, "hard tasks
    /// must be pinned by a human", and it says so before a token is spent.
    ///
    /// <para>The message must NAME the costly block and offer the pin, because the fix is completely
    /// different from cause (a)'s: nothing needs registering, and the block the operator is looking for is
    /// sitting right there in their config.</para>
    /// </summary>
    [Fact]
    public void UsedTierServedOnlyByCostlyBlocks_IsGr2048_NamingTheCostlyCause()
    {
        string planDir = PlanFolder(
            ConfigWith("""
                "sonnet": { "command": "claude", "strength": 3, "routing": { "tiers": ["medium"] } },
                "opus":   { "command": "claude", "strength": 4, "costly": true,
                            "routing": { "tiers": ["hard"] } }
                """, defaultRunner: "sonnet"),
            taskJson: """{ "description": "t", "dependsOn": [], "writeScope": [], "action": { "tier": "hard" } }""");

        Loaded loaded = LoadFolder(planDir);

        Diagnostic error = RequireCode(loaded, DiagnosticCodes.UnservableTier);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("promptRunners.opus", error.Message, StringComparison.Ordinal);
        Assert.Contains("costly", error.Message, StringComparison.Ordinal);
        Assert.Contains("\"runner\": \"opus\"", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two causes must be TOLD APART, not merely both reported — they have different fixes, and this
    /// is the DoR's explicit requirement on GR2048's message. Asserting the messages DIFFER (rather than
    /// each matching its own substring in isolation) is what a single generic "no block serves tier X"
    /// string would fail.
    /// </summary>
    [Fact]
    public void TheTwoUnservableCauses_ProduceDifferentMessages()
    {
        string nothingServes = SingleUnservableMessage("""
            "kimi": { "command": "kimi", "routing": { "tiers": ["easy"] } }
            """, "kimi");

        string onlyCostly = SingleUnservableMessage("""
            "kimi": { "command": "kimi", "routing": { "tiers": ["easy"] } },
            "opus": { "command": "claude", "costly": true, "routing": { "tiers": ["hard"] } }
            """, "kimi");

        Assert.NotEqual(nothingServes, onlyCostly);
        Assert.DoesNotContain("costly", nothingServes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("costly", onlyCostly, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>The carve-out, pinned.</b> <c>costly</c> is TRI-STATE in the schema — <c>null</c> ("absent, not
    /// stated") is distinct from an explicit <c>false</c> — but at the CANDIDACY PREDICATE it is
    /// two-valued: only an explicit <c>true</c> excludes a block, so an UN-ANNOTATED registry stays fully
    /// routable. Without this rule every config written before the axis existed would become unservable
    /// the moment it grew a <c>routing</c> block, which would make the whole feature un-adoptable.
    /// </summary>
    [Theory]
    [InlineData("")]                      // costly ABSENT — null, "not stated"
    [InlineData("\"costly\": false, ")]   // costly EXPLICITLY false — "stated to be cheap"
    public void CostlyAbsentOrExplicitlyFalse_BothServe(string costlyJson)
    {
        string planDir = PlanFolder(
            ConfigWith(
                $$"""
                "block": { "command": "claude", {{costlyJson}}"routing": { "tiers": ["hard"] } }
                """,
                defaultRunner: "block"),
            taskJson: """{ "description": "t", "dependsOn": [], "writeScope": [], "action": { "tier": "hard" } }""");

        Loaded loaded = LoadFolder(planDir);

        AssertNoErrors(loaded);
        Assert.True(loaded.Runner("block").ServesTier("hard"));
    }

    /// <summary>
    /// The never-route-DOWN floor made checkable: a rung with no candidate of its own is NOT unservable
    /// when a STRONGER rung has one, because resolution may climb. Here <c>easy</c> is declared by nobody
    /// and served by nobody, yet <c>hard</c>'s block is at-or-above it, so the climb succeeds and there is
    /// nothing to report. A GR2048 that checked only the exact rung would fail this and reject a perfectly
    /// routable config.
    /// </summary>
    [Fact]
    public void TierWithNoOwnCandidateButAStrongerOne_IsServable()
    {
        string planDir = PlanFolder(
            ConfigWith("""
                "opus": { "command": "claude", "strength": 4, "routing": { "tiers": ["hard"] } }
                """, defaultRunner: "opus"),
            taskJson: """{ "description": "t", "dependsOn": [], "writeScope": [], "action": { "tier": "easy" } }""");

        Loaded loaded = LoadFolder(planDir);

        AssertNoErrors(loaded);
    }

    /// <summary>
    /// The opposite direction, which is the whole point of "at or ABOVE": a WEAKER rung's candidate never
    /// rescues a stronger tier. Resolution never routes weaker than asked, so a config whose only block
    /// serves <c>easy</c> cannot serve a <c>hard</c> task, and the halt is honest.
    /// </summary>
    [Fact]
    public void WeakerRungCandidate_DoesNotRescueAStrongerTier()
    {
        string planDir = PlanFolder(
            ConfigWith("""
                "kimi": { "command": "kimi", "strength": 2, "routing": { "tiers": ["easy"] } }
                """, defaultRunner: "kimi"),
            taskJson: """{ "description": "t", "dependsOn": [], "writeScope": [], "action": { "tier": "hard" } }""");

        RequireCode(LoadFolder(planDir), DiagnosticCodes.UnservableTier);
    }

    /// <summary>
    /// <c>tiering.defaultTier</c> is a USED tier in its own right (DoR §6.2) — it is what every untagged
    /// task resolves to, so a default nothing can serve is exactly as broken as a task tag nothing can
    /// serve, and it is reported at the config rather than fanned out over the tasks that inherited it.
    /// </summary>
    [Fact]
    public void UnservableDefaultTier_IsGr2048_ReportedOnce()
    {
        string planDir = PlanFolder(ConfigWith("""
            "kimi": { "command": "kimi", "routing": { "tiers": ["easy"] } }
            """, defaultRunner: "kimi", tiering: """{ "defaultTier": "hard" }"""));
        AddTask(planDir, "02-task", """{ "description": "t2", "dependsOn": [], "writeScope": [] }""");
        AddTask(planDir, "03-task", """{ "description": "t3", "dependsOn": [], "writeScope": [] }""");

        Loaded loaded = LoadFolder(planDir);

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == DiagnosticCodes.UnservableTier);
        Assert.Contains("tiering.defaultTier", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A judge guardrail's frontmatter tier is a USED tier too — the verifier half of the epic resolves
    /// through the same candidate set, so a rung only a judge asks for still has to be servable.
    /// </summary>
    [Fact]
    public void UnservableJudgeFrontmatterTier_IsGr2048()
    {
        string planDir = PlanFolder(ConfigWith("""
            "kimi": { "command": "kimi", "routing": { "tiers": ["easy"] } }
            """, defaultRunner: "kimi"));
        WritePromptGuardrail(planDir, "02-judge", """
            ---
            catches: a plausible-but-wrong implementation
            tier: hard
            ---
            You are a verifier.
            """);

        Diagnostic error = RequireCode(LoadFolder(planDir), DiagnosticCodes.UnservableTier);
        Assert.Contains("02-judge", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>tiering.verifier.minTier</c> is deliberately NOT a used tier, and this is the one negative
    /// assertion in the file that encodes a design ruling rather than a mechanism. An unsatisfiable
    /// verifier floor DEGRADES to an advisory (§6.5.1) while an unsatisfiable ACTOR tier HALTS — degrade
    /// what is advisory, halt what is load-bearing. Counting the floor as "used" would convert an
    /// advisory condition into a build-failing error, which §12.6 forbids by name: a GR code is a thing
    /// that can fail a build, and no verifier condition may ever fail one.
    /// </summary>
    [Fact]
    public void UnsatisfiableVerifierFloor_IsNotGr2048()
    {
        Loaded loaded = Load(ConfigWith("""
            "kimi": { "command": "kimi", "routing": { "tiers": ["easy"] } }
            """, defaultRunner: "kimi", tiering: """{ "verifier": { "minTier": "hard" } }"""));

        Assert.DoesNotContain(loaded.Diagnostics, d => d.Code == DiagnosticCodes.UnservableTier);
        AssertNoErrors(loaded);
    }

    /// <summary>
    /// The DoR's own §14 worked example, minus the <c>openai-compat</c> block (#223 has not landed, so
    /// that block is a validate error by design and the example says to delete it). It must validate
    /// CLEAN: a design whose worked example fails its own validator is a design that has not been checked
    /// against reality, which is what Stage 1.5 exists to correct.
    /// </summary>
    [Fact]
    public void TheDesignsWorkedExample_ValidatesClean()
    {
        string planDir = PlanFolder(ConfigWith("""
            "fable":  { "command": "claude", "model": "claude-fable-5", "effort": "xhigh",
                        "costly": true, "strength": 5, "specialization": "planning-reasoning" },
            "opus":   { "command": "claude", "model": "claude-opus-4-6", "effort": "high",
                        "strength": 4, "specialization": "planning-reasoning",
                        "routing": { "tiers": ["hard"], "notes": "cross-module architecture" } },
            "sonnet": { "command": "claude", "model": "claude-sonnet-4-5",
                        "strength": 3, "specialization": "coding",
                        "routing": { "tiers": ["medium", "hard"], "notes": "typical single-module coding" } }
            """, defaultRunner: "sonnet", tiering: """{ "defaultTier": "medium" }"""));
        AddTask(planDir, "02-implement-stats", """{ "description": "t", "dependsOn": [], "writeScope": [], "action": { "tier": "hard" } }""");
        AddTask(planDir, "04-hand-added-hotfix", """{ "description": "t", "dependsOn": [], "writeScope": [], "action": { "model": "claude-opus-4-6" } }""");

        Loaded loaded = LoadFolder(planDir);

        AssertNoErrors(loaded);
        Assert.Equal(["hard"], loaded.Runner("opus").Routing!.Tiers);
        Assert.True(loaded.Runner("fable").Costly);
        Assert.Null(loaded.Runner("fable").Routing); // reserved by BOTH forms — costly AND no routing
    }

    // ── 7. Invariant 7 — additive, byte-identically ────────────────────────────────────────────

    /// <summary>
    /// <b>DoR Invariant 7, the load-bearing one for this epic.</b> A config with none of these fields —
    /// no <c>routing</c>, no <c>tiering</c>, no <c>kind</c>, no <c>effort</c>, no tags — validates exactly
    /// as it did before Stage 1.5, with ZERO diagnostics of any severity from any of the new checks and
    /// nothing fabricated onto the model. Every one of the seven codes this slice touches is asserted
    /// absent by NAME rather than by a blanket "no errors": a blanket assertion would pass while a new
    /// WARNING quietly started appearing on every single-model user's plan, which is precisely the
    /// regression Invariant 7 exists to forbid.
    /// </summary>
    [Fact]
    public void ConfigWithNoneOfTheseFields_ValidatesUnchanged_AndEmitsNothingNew()
    {
        string planDir = PlanFolder("""
            {
              "version": 1,
              "maxParallelism": 1,
              "promptRunners": {
                "default": "claude",
                "claude": {
                  "command": "claude",
                  "permissionMode": "acceptEdits",
                  "allowedTools": ["Read", "Edit"],
                  "maxTurns": 50,
                  "model": "claude-sonnet-4-5",
                  "guardrailOverrides": { "permissionMode": "default", "maxTurns": 20 }
                }
              }
            }
            """);
        WritePromptGuardrail(planDir, "02-judge", """
            ---
            catches: a plausible-but-wrong implementation
            ---
            You are a verifier.
            """);

        Loaded loaded = LoadFolder(planDir);

        string[] newCodes =
        [
            DiagnosticCodes.MalformedRoutingGuidance,
            DiagnosticCodes.UnservableTier,
            DiagnosticCodes.TieringInert,
            DiagnosticCodes.EffortInvalid,
            DiagnosticCodes.InvalidTierValue,
            DiagnosticCodes.InvalidPromptRunnerKind,
            DiagnosticCodes.RetiredRoutingRank
        ];

        Diagnostic[] leaked = [.. loaded.Diagnostics.Where(d => newCodes.Contains(d.Code))];
        Assert.True(leaked.Length == 0,
            "A config that opted into NOTHING must see no tiering diagnostic at all. Leaked:\n" +
            string.Join("\n", leaked.Select(d => $"  {d.Severity} {d.Code}: {d.Message}")));

        AssertNoErrors(loaded);

        // …and nothing was invented on the model either.
        PromptRunnerConfig runner = loaded.Runner("claude");
        Assert.Null(runner.Routing);
        Assert.Null(runner.Effort);
        Assert.Null(runner.Costly);
        Assert.Equal(PromptRunnerKind.Claude, runner.Kind);
        Assert.Null(loaded.Plan!.Config.Tiering);
        Assert.Null(loaded.Plan!.Tasks[0].Action.Tier);
        Assert.Null(loaded.Plan!.Tasks[0].Action.Effort);
        Assert.Null(Assert.Single(loaded.Plan!.Tasks[0].Guardrails, g => g.Name == "02-judge").Tier);
    }

    // ── harness ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The four sites a difficulty tier can be declared (DoR §5) — the theory axis for GR2043.</summary>
    public enum TierSite
    {
        /// <summary><c>task.json action.tier</c>.</summary>
        ActionTier,

        /// <summary>A prompt guardrail's frontmatter <c>tier</c> — the judge site.</summary>
        JudgeFrontmatter,

        /// <summary><c>guardrails.json tiering.defaultTier</c>.</summary>
        DefaultTier,

        /// <summary><c>guardrails.json tiering.verifier.minTier</c> — the verifier FLOOR.</summary>
        VerifierMinTier
    }

    /// <summary>
    /// A plan declaring <paramref name="tier"/> at exactly ONE of the four sites. Every fixture routes the
    /// tier through a config that serves <c>hard</c>, so a RECOGNIZED tier validates clean and the only
    /// thing the theory can be measuring is the tier token itself.
    /// </summary>
    private Loaded LoadWithBadTierAt(TierSite site, string tier)
    {
        string runners = """
            "claude": { "command": "claude", "routing": { "tiers": ["easy", "medium", "hard"] } }
            """;

        string tiering = site switch
        {
            TierSite.DefaultTier => $$"""{ "defaultTier": "{{tier}}" }""",
            TierSite.VerifierMinTier => $$"""{ "verifier": { "minTier": "{{tier}}" } }""",
            _ => null!
        };

        string taskJson = site == TierSite.ActionTier
            ? $$"""{ "description": "t", "dependsOn": [], "writeScope": [], "action": { "tier": "{{tier}}" } }"""
            : DefaultTaskJson;

        string planDir = PlanFolder(ConfigWith(runners, "claude", tiering), taskJson);

        if (site == TierSite.JudgeFrontmatter)
        {
            WritePromptGuardrail(planDir, "02-judge", $"""
                ---
                catches: a plausible-but-wrong implementation
                tier: {tier}
                ---
                You are a verifier.
                """);
        }

        return LoadFolder(planDir);
    }

    /// <summary>The single GR2048 message a one-task <c>hard</c> plan produces against <paramref name="runners"/>.</summary>
    private string SingleUnservableMessage(string runners, string defaultRunner)
    {
        string planDir = Path.Combine(_root, "case-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(planDir);
        WriteConfig(planDir, ConfigWith(runners, defaultRunner));
        WriteTask(planDir, "01-task",
            """{ "description": "t", "dependsOn": [], "writeScope": [], "action": { "tier": "hard" } }""");

        return RequireCode(LoadFolder(planDir), DiagnosticCodes.UnservableTier).Message;
    }

    private const string DefaultTaskJson =
        """{ "description": "t", "dependsOn": [], "writeScope": [] }""";

    /// <summary>
    /// A whole <c>guardrails.json</c> around <paramref name="runners"/> (raw block text, comma-separated).
    /// <c>maxParallelism: 1</c> keeps every fixture SERIAL — worktree mode drags in the unrelated GR2015
    /// (temp dirs are not git roots) and GR2028 (terminal-gate obligation) errors, and these assertions
    /// turn on there being no error at all.
    /// </summary>
    private static string ConfigWith(string runners, string defaultRunner, string? tiering = null)
    {
        string tieringLine = tiering is null ? string.Empty : $"  \"tiering\": {tiering},\n";
        return $$"""
            {
              "version": 1,
              "maxParallelism": 1,
            {{tieringLine}}  "promptRunners": {
                "default": "{{defaultRunner}}",
                {{runners}}
              }
            }
            """;
    }

    /// <summary>Serialise a parsed routing block back into a runner block — the "serialise" round-trip leg.</summary>
    private static string RenderRoutingBlock(string name, PromptRunnerRouting routing)
    {
        var tiers = new JsonArray();
        foreach (string tier in routing.Tiers)
        {
            tiers.Add(tier);
        }

        var routingBlock = new JsonObject { ["tiers"] = tiers };
        if (routing.Notes is not null)
        {
            routingBlock["notes"] = routing.Notes;
        }

        var block = new JsonObject { ["command"] = "claude", ["routing"] = routingBlock };
        return $"\"{name}\": {block.ToJsonString()}";
    }

    /// <summary>A minimal one-prompt-task plan folder carrying the given config (and optional task.json).</summary>
    private string PlanFolder(string configJson, string? taskJson = null)
    {
        WriteConfig(_root, configJson);
        WriteTask(_root, "01-task", taskJson ?? DefaultTaskJson);
        return _root;
    }

    private static void WriteConfig(string planDir, string configJson)
    {
        Directory.CreateDirectory(planDir);
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), configJson);
    }

    private static void WriteTask(string planDir, string id, string taskJson)
    {
        string taskDir = Path.Combine(planDir, "tasks", id);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), taskJson);
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");
        File.WriteAllText(
            Path.Combine(taskDir, "guardrails", "01-ok.sh"),
            "# catches: nothing — a fixture stand-in so the task is not guardrail-less.\nexit 0\n");
    }

    private void AddTask(string planDir, string id, string taskJson) => WriteTask(planDir, id, taskJson);

    private static void WriteTaskJson(string planDir, string id, string taskJson) =>
        File.WriteAllText(Path.Combine(planDir, "tasks", id, "task.json"), taskJson);

    /// <summary>Adds a PROMPT guardrail to the plan's first task — the judge-guardrail site.</summary>
    private static void WritePromptGuardrail(string planDir, string name, string content) =>
        File.WriteAllText(
            Path.Combine(planDir, "tasks", "01-task", "guardrails", $"{name}.prompt.md"),
            content);

    /// <summary>A loaded plan plus every diagnostic from BOTH phases — loading and validation.</summary>
    private sealed record Loaded(PlanDefinition? Plan, IReadOnlyList<Diagnostic> Diagnostics)
    {
        public PromptRunnerConfig Runner(string name) => Plan!.Config.PromptRunners[name];
    }

    private Loaded Load(string configJson) => LoadFolder(PlanFolder(configJson));

    private static Loaded LoadFolder(string planDir)
    {
        PlanLoadResult result = new PlanLoader().Load(planDir);
        List<Diagnostic> diagnostics = [.. result.Diagnostics];

        if (result.Plan is not null)
        {
            diagnostics.AddRange(new PlanValidator(FakeExecutableProbe.All).Validate(result.Plan));
        }

        return new Loaded(result.Plan, diagnostics);
    }

    /// <summary>The single diagnostic carrying <paramref name="code"/>, with a readable dump when it is missing.</summary>
    private static Diagnostic RequireCode(Loaded loaded, string code)
    {
        Diagnostic[] matches = [.. loaded.Diagnostics.Where(d => d.Code == code)];
        Assert.True(matches.Length > 0, $"expected a {code} diagnostic; diagnostics were:\n{Dump(loaded)}");
        return matches[0];
    }

    /// <summary>
    /// No ERROR diagnostics. <see cref="DiagnosticCodes.WorkspaceNotGitRoot"/> is excluded because every
    /// fixture lives in a temp folder, which is never a git root.
    /// </summary>
    private static void AssertNoErrors(Loaded loaded)
    {
        Diagnostic[] errors =
        [
            .. loaded.Diagnostics.Where(d =>
                d.Severity == DiagnosticSeverity.Error && d.Code != DiagnosticCodes.WorkspaceNotGitRoot)
        ];

        Assert.True(errors.Length == 0, $"unexpected validation errors:\n{string.Join("\n", errors)}");
    }

    private static string Dump(Loaded loaded) =>
        loaded.Diagnostics.Count == 0 ? "(none)" : string.Join("\n", loaded.Diagnostics);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }
}
