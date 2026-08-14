using System.Text.Json;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// Loader round-trip and validation coverage for the task.json <c>action.tier</c> difficulty tag
/// (issue #225, charter section C items 1 and 4): mirrors <c>action.model</c>/<c>action.maxTurns</c>
/// exactly — same raw type, same resolved record, same "bound verbatim, judged by the validator"
/// split. Nothing ROUTES on the tier in Stage 1; this stage only proves a plan can SAY what it has
/// and that <c>guardrails validate</c> holds it to that.
///
/// <para>Four behaviours, one per charter bullet:</para>
/// <list type="number">
///   <item><c>action.tier</c> binds onto <c>RawAction</c> and reaches <see cref="ActionDefinition"/>,
///     accepting <c>easy|medium|hard</c>.</item>
///   <item><c>guardrails validate</c> REJECTS an unrecognized tier, with an error naming the bad
///     value (at both sites a tier can be declared: the task and the plan-wide default).</item>
///   <item><c>tier</c> is OPTIONAL — the additive guarantee. A plan with no tier anywhere parses,
///     validates clean, and resolves to a NULL tier: a single-model user's plan is unaffected, and
///     no default is fabricated where none was configured.</item>
///   <item>A plan-wide default tier covers anything left untagged, including a task hand-added to
///     the folder after breakdown. An explicit <c>action.tier</c> beats the default.</item>
/// </list>
///
/// <para>The plan-wide default is read from <c>guardrails.json</c> as
/// <c>"tiering": { "defaultTier": "medium" }</c> — the <c>tiering</c> block the charter's gate warning
/// names ("no <c>action.tier</c>, no <c>tiering</c> block, no report lines" when tiering is not
/// configured). The tier is resolved AT LOAD onto <see cref="ActionDefinition.Tier"/>, which is what
/// makes the default reach a hand-added task that no breakdown ever touched.</para>
///
/// <para>Diagnostics are collected from the loader AND the validator together, because
/// <c>guardrails validate</c> reports both and the charter's requirement is on that command's verdict,
/// not on which layer happens to catch it.</para>
/// </summary>
[Trait("Category", "ModelTieringStage1")]
public sealed class ActionTierTests : IDisposable
{
    private readonly string _root;

    public ActionTierTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-actiontier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // maxParallelism 1 keeps these fixtures SERIAL. Worktree mode (>1) would drag in the unrelated
    // GR2015 (workspace-not-a-git-repo — the temp dir never is) and GR2028 (terminal-gate obligation)
    // errors, and the assertions below turn on there being no error diagnostic AT ALL.

    /// <summary>A single-model config: prompt runners, NO tiering block — today's shape.</summary>
    private const string UntieredConfigJson =
        """
        {
          "version": 1,
          "maxParallelism": 1,
          "promptRunners": {
            "default": "claude",
            "claude": { "command": "claude", "model": "claude-sonnet-5" }
          }
        }
        """;

    /// <summary>A tiered config carrying the plan-wide default that covers untagged tasks.</summary>
    private const string DefaultTierConfigJson =
        """
        {
          "version": 1,
          "maxParallelism": 1,
          "tiering": { "defaultTier": "medium" },
          "promptRunners": {
            "default": "claude",
            "claude": { "command": "claude", "model": "claude-sonnet-5" }
          }
        }
        """;

    /// <summary>Writes a plan folder with one task per entry and returns its path.</summary>
    private string PlanWith(string configJson, params (string Id, string TaskJson)[] tasks)
    {
        File.WriteAllText(Path.Combine(_root, "guardrails.json"), configJson);

        foreach ((string id, string taskJson) in tasks)
        {
            string taskDir = Path.Combine(_root, "tasks", id);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
            File.WriteAllText(Path.Combine(taskDir, "task.json"), taskJson);
            File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");
            File.WriteAllText(
                Path.Combine(taskDir, "guardrails", "01-ok.sh"),
                "# catches: nothing — a fixture stand-in so the task is not guardrail-less.\nexit 0\n");
        }

        return _root;
    }

    /// <summary>A one-task plan, the shape every single-task case below shares.</summary>
    private string SingleTaskPlan(string configJson, string taskJson) =>
        PlanWith(configJson, ("01-task", taskJson));

    /// <summary>
    /// Everything <c>guardrails validate</c> would report: the loader's diagnostics plus the
    /// validator's. Either layer may legitimately own the tier-value check — the contract is the
    /// command's verdict, so the tests assert against the union rather than pinning a layer.
    /// </summary>
    private static IReadOnlyList<Diagnostic> ValidateDiagnostics(PlanLoadResult result)
    {
        List<Diagnostic> all = [.. result.Diagnostics];
        if (result.Plan is not null)
        {
            all.AddRange(new PlanValidator(FakeExecutableProbe.All).Validate(result.Plan));
        }

        return all;
    }

    private static string TaskJsonWithTier(string tier) =>
        $$"""
          { "description": "t", "dependsOn": [], "writeScope": [], "action": { "tier": "{{tier}}" } }
          """;

    // --- 1. action.tier binds onto RawAction ------------------------------------------------

    [Theory]
    [InlineData("easy")]
    [InlineData("medium")]
    [InlineData("hard")]
    public void ActionTier_BindsOntoRawAction(string tier)
    {
        string json = $$"""{ "tier": "{{tier}}" }""";

        RawAction? raw = JsonSerializer.Deserialize<RawAction>(json, PlanJson.Options);

        Assert.Equal(tier, raw!.Tier);
    }

    // --- 1. …and reaches the resolved model -------------------------------------------------

    [Theory]
    [InlineData("easy")]
    [InlineData("medium")]
    [InlineData("hard")]
    public void TaskJsonActionTier_RoundTripsIntoActionDefinition(string tier)
    {
        PlanLoadResult result = new PlanLoader().Load(SingleTaskPlan(UntieredConfigJson, TaskJsonWithTier(tier)));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        TaskNode task = Assert.Single(result.Plan!.Tasks);
        Assert.Equal(tier, task.Action.Tier);
    }

    [Theory]
    [InlineData("easy")]
    [InlineData("medium")]
    [InlineData("hard")]
    public void RecognizedTier_IsNotRejectedByValidate(string tier)
    {
        PlanLoadResult result = new PlanLoader().Load(SingleTaskPlan(UntieredConfigJson, TaskJsonWithTier(tier)));

        IReadOnlyList<Diagnostic> diagnostics = ValidateDiagnostics(result);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    // --- 2. validate REJECTS an unrecognized tier, naming the bad value ---------------------

    [Theory]
    [InlineData("extreme")]
    [InlineData("trivial")]
    [InlineData("Hard ")]
    public void UnrecognizedTaskTier_IsRejectedWithAnErrorNamingTheBadValue(string badTier)
    {
        PlanLoadResult result = new PlanLoader().Load(SingleTaskPlan(UntieredConfigJson, TaskJsonWithTier(badTier)));

        IReadOnlyList<Diagnostic> diagnostics = ValidateDiagnostics(result);

        Diagnostic error = Assert.Single(
            diagnostics,
            d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains(badTier, StringComparison.Ordinal));
        Assert.Contains("tier", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The plan-wide default is the OTHER site a tier can be declared, and the more dangerous one: a
    /// typo there silently applies a garbage tier to every untagged task in the plan.
    /// </summary>
    [Fact]
    public void UnrecognizedPlanWideDefaultTier_IsRejectedWithAnErrorNamingTheBadValue()
    {
        const string badConfigJson =
            """
            {
              "version": 1,
              "maxParallelism": 1,
              "tiering": { "defaultTier": "wizard" },
              "promptRunners": {
                "default": "claude",
                "claude": { "command": "claude", "model": "claude-sonnet-5" }
              }
            }
            """;

        PlanLoadResult result = new PlanLoader().Load(SingleTaskPlan(
            badConfigJson,
            """{ "description": "t", "dependsOn": [], "writeScope": [] }"""));

        IReadOnlyList<Diagnostic> diagnostics = ValidateDiagnostics(result);

        Diagnostic error = Assert.Single(
            diagnostics,
            d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("wizard", StringComparison.Ordinal));
        Assert.Contains("tier", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- 3. tier is OPTIONAL — the additive guarantee ---------------------------------------

    /// <summary>
    /// The load-bearing additive guarantee: a plan that never mentions a tier — no
    /// <c>action.tier</c>, no <c>tiering</c> block — parses and validates exactly as it does today,
    /// and resolves to a NULL tier. A single-model user's plan must be unaffected by this feature.
    /// </summary>
    [Fact]
    public void PlanWithNoTierAnywhere_ParsesAndValidatesExactlyAsToday()
    {
        PlanLoadResult result = new PlanLoader().Load(SingleTaskPlan(
            UntieredConfigJson,
            """{ "description": "t", "dependsOn": [], "writeScope": [] }"""));

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        IReadOnlyList<Diagnostic> diagnostics = ValidateDiagnostics(result);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("tier", StringComparison.OrdinalIgnoreCase));

        TaskNode task = Assert.Single(result.Plan!.Tasks);
        Assert.Null(task.Action.Tier);
    }

    /// <summary>
    /// An untagged task under an UNTIERED config gets no tier at all — the absent <c>tiering</c>
    /// block must never be substituted by a hard-coded fallback. This is the negative half of the
    /// plan-wide-default behaviour: a default applies only where one was actually configured.
    /// </summary>
    [Fact]
    public void UntaggedTask_WithNoTieringBlock_DoesNotFabricateADefaultTier()
    {
        PlanLoadResult result = new PlanLoader().Load(SingleTaskPlan(
            UntieredConfigJson,
            """{ "description": "t", "dependsOn": [], "writeScope": [], "action": { "model": "claude-haiku-4-5" } }"""));

        TaskNode task = Assert.Single(result.Plan!.Tasks);
        Assert.Equal("claude-haiku-4-5", task.Action.Model);
        Assert.Null(task.Action.Tier);
    }

    [Fact]
    public void RawActionWithoutTier_LeavesTierNull()
    {
        RawAction? raw = JsonSerializer.Deserialize<RawAction>(
            """{ "maxTurns": 30, "model": "claude-haiku-4-5" }""", PlanJson.Options);

        Assert.Null(raw!.Tier);
    }

    // --- 4. a plan-wide default covers anything left untagged -------------------------------

    /// <summary>
    /// The default has to reach a task NO breakdown ever tagged — the hand-added case the charter
    /// calls out. <c>02-hand-added-after-breakdown</c> carries no <c>action</c> block at all (exactly
    /// what a human writes by hand), and must still come out of the loader tiered.
    /// </summary>
    [Fact]
    public void PlanWideDefaultTier_CoversAnUntaggedHandAddedTask()
    {
        PlanLoadResult result = new PlanLoader().Load(PlanWith(
            DefaultTierConfigJson,
            ("01-tagged", TaskJsonWithTier("hard")),
            ("02-hand-added-after-breakdown",
                """{ "description": "added by hand", "dependsOn": ["01-tagged"], "writeScope": [] }""")));

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

        TaskNode handAdded = Assert.Single(
            result.Plan!.Tasks, t => t.Id == "02-hand-added-after-breakdown");
        Assert.Equal("medium", handAdded.Action.Tier);
    }

    /// <summary>An explicit <c>action.tier</c> beats the plan-wide default — the default only fills gaps.</summary>
    [Fact]
    public void ExplicitTaskTier_BeatsThePlanWideDefault()
    {
        PlanLoadResult result = new PlanLoader().Load(PlanWith(
            DefaultTierConfigJson,
            ("01-tagged", TaskJsonWithTier("hard")),
            ("02-hand-added-after-breakdown",
                """{ "description": "added by hand", "dependsOn": ["01-tagged"], "writeScope": [] }""")));

        TaskNode tagged = Assert.Single(result.Plan!.Tasks, t => t.Id == "01-tagged");
        Assert.Equal("hard", tagged.Action.Tier);
    }

    [Fact]
    public void PlanWideDefaultTier_ValidatesClean()
    {
        PlanLoadResult result = new PlanLoader().Load(SingleTaskPlan(
            DefaultTierConfigJson,
            """{ "description": "t", "dependsOn": [], "writeScope": [] }"""));

        IReadOnlyList<Diagnostic> diagnostics = ValidateDiagnostics(result);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

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
