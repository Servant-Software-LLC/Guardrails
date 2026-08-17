using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// Loader coverage for <see cref="ActionDefinition.TierOrigin"/> — the provenance DoR §12.4's journal field
/// <c>provenance.tierSource</c> is derived from. The loader resolves <c>action.tier ?? tiering.defaultTier</c>
/// into a single nullable <see cref="ActionDefinition.Tier"/>, and that collapse destroys which of the two
/// sites supplied the rung. These tests pin the restored answer.
///
/// <para><b>Only <c>task</c> vs <c>plan-default</c> is at stake here.</b> The enum's third journal value,
/// <c>"override"</c>, is produced by a full <c>action.runner</c>/<c>action.model</c> pin (DoR §6.1 item 1,
/// D31) — a pin is still visible to the resolver on <see cref="ActionDefinition.Runner"/> /
/// <see cref="ActionDefinition.Model"/>, so nothing about it needs restoring at load.</para>
///
/// <para>Every case drives the REAL <see cref="PlanLoader"/> over a plan folder on disk. Hand-building an
/// <see cref="ActionDefinition"/> would assert an object initializer and prove nothing about the collapse
/// this coverage exists to undo — the whole defect lives in the loader's precedence expression.</para>
///
/// <para>The load-bearing case is <see cref="ActionTier_SameTokenAsDefault_OriginIsStillTask"/>. Stage 1
/// guessed the origin after the fact by comparing the resolved tier against the default
/// (<c>PlanValidator.cs</c>: <c>tier != plan.Config.Tiering?.DefaultTier</c>); that guess satisfies every
/// other case on this list and gets exactly that one wrong — in the COMMON situation where a task's own tier
/// happens to equal the plan default. An implementation that reproduces it certifies the bug.</para>
/// </summary>
[Trait("Category", "TierResolution")]
public sealed class ActionTierProvenanceTests : IDisposable
{
    private readonly string _root;
    private int _plans;

    public ActionTierProvenanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-tierprov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // maxParallelism 1 keeps these fixtures SERIAL, exactly as ActionTierTests does: worktree mode (>1)
    // would drag in the unrelated GR2015 (workspace-not-a-git-repo — a temp dir never is) and GR2028
    // (terminal-gate obligation) errors that several assertions below would then trip over.

    /// <summary>A single-model config with NO tiering block — the plan-wide default site is absent.</summary>
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

    private static string ConfigWithDefaultTier(string defaultTier) =>
        $$"""
          {
            "version": 1,
            "maxParallelism": 1,
            "tiering": { "defaultTier": "{{defaultTier}}" },
            "promptRunners": {
              "default": "claude",
              "claude": { "command": "claude", "model": "claude-sonnet-5" }
            }
          }
          """;

    private const string NoActionBlockTaskJson =
        """{ "description": "t", "dependsOn": [], "writeScope": [] }""";

    private static string TaskJsonWithTier(string tier) =>
        $$"""{ "description": "t", "dependsOn": [], "writeScope": [], "action": { "tier": "{{tier}}" } }""";

    /// <summary>A fresh plan root per call, so a FLAT fixture and a WAVED one never share a directory.</summary>
    private string NewPlanDir(string configJson)
    {
        string planDir = Path.Combine(_root, "plan-" + (++_plans).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(planDir);
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), configJson);
        return planDir;
    }

    /// <summary>A FLAT one-task plan: <c>tasks/01-task/</c> with the given manifest.</summary>
    private string FlatPlan(string configJson, string taskJson)
    {
        string planDir = NewPlanDir(configJson);
        WriteTaskFolder(Path.Combine(planDir, "tasks", "01-task"), taskJson);
        return planDir;
    }

    /// <summary>
    /// A WAVED one-task plan: <c>wave-01-alpha/tasks/01-task/</c> and NO root <c>tasks/</c>. The default
    /// reaches a wave task through <c>LoadWaves</c>/<c>LoadWaveTasks</c>, a different call path from flat
    /// <c>LoadTasks</c> — an implementation that patches only one is half-done in a way no flat case sees.
    /// </summary>
    private string WavedPlan(string configJson, string taskJson)
    {
        string planDir = NewPlanDir(configJson);
        WriteTaskFolder(Path.Combine(planDir, "wave-01-alpha", "tasks", "01-task"), taskJson);
        return planDir;
    }

    private static void WriteTaskFolder(string taskDir, string taskJson)
    {
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), taskJson);
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");
        File.WriteAllText(
            Path.Combine(taskDir, "guardrails", "01-ok.sh"),
            "# catches: nothing — a fixture stand-in so the task is not guardrail-less.\nexit 0\n");
    }

    /// <summary>Load the plan and return its single task, failing loudly on any load error.</summary>
    private static TaskNode SingleTaskOf(string planDir)
    {
        PlanLoadResult result = new PlanLoader().Load(planDir);
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        return Assert.Single(result.Plan!.Tasks);
    }

    // --- 1. action.tier set ⇒ the TASK supplied the rung -------------------------------------

    [Fact]
    public void ActionTier_Set_OriginIsTask()
    {
        TaskNode task = SingleTaskOf(FlatPlan(UntieredConfigJson, TaskJsonWithTier("hard")));

        Assert.Equal("hard", task.Action.Tier);
        Assert.Equal(TierOrigin.Task, task.Action.TierOrigin);
    }

    /// <summary>Every recognized token, so the origin cannot be keyed off one particular rung.</summary>
    [Theory]
    [InlineData("easy")]
    [InlineData("medium")]
    [InlineData("hard")]
    public void ActionTier_EveryRecognizedToken_OriginIsTask(string tier)
    {
        TaskNode task = SingleTaskOf(FlatPlan(UntieredConfigJson, TaskJsonWithTier(tier)));

        Assert.Equal(tier, task.Action.Tier);
        Assert.Equal(TierOrigin.Task, task.Action.TierOrigin);
    }

    // --- 2. no action.tier, a default configured ⇒ the PLAN DEFAULT supplied it ---------------

    [Fact]
    public void ActionTier_Absent_DefaultSupplied_OriginIsPlanDefault()
    {
        TaskNode task = SingleTaskOf(FlatPlan(ConfigWithDefaultTier("medium"), NoActionBlockTaskJson));

        Assert.Equal("medium", task.Action.Tier);
        Assert.Equal(TierOrigin.PlanDefault, task.Action.TierOrigin);
    }

    /// <summary>
    /// A present <c>action</c> block that simply says nothing about the tier is still the default's case —
    /// the trigger is an absent <c>tier</c> KEY, not an absent action block.
    /// </summary>
    [Fact]
    public void ActionBlockWithoutTier_DefaultSupplied_OriginIsPlanDefault()
    {
        TaskNode task = SingleTaskOf(FlatPlan(
            ConfigWithDefaultTier("easy"),
            """{ "description": "t", "dependsOn": [], "writeScope": [], "action": { "maxTurns": 30 } }"""));

        Assert.Equal("easy", task.Action.Tier);
        Assert.Equal(TierOrigin.PlanDefault, task.Action.TierOrigin);
    }

    // --- 3. the case a comparison-after-the-fact gets WRONG -----------------------------------

    /// <summary>
    /// THE point of this coverage. <c>action.tier</c> spelled with the SAME token as
    /// <c>tiering.defaultTier</c> was still supplied by the TASK — the task said it, and the fact that the
    /// plan default happens to agree does not transfer authorship. Comparing the resolved value against the
    /// default (Stage 1's guess) reports <c>plan-default</c> here and is wrong, and it is wrong in the
    /// ordinary case rather than an exotic one: a plan whose default is <c>medium</c> and whose tasks are
    /// mostly <c>medium</c> is the shape tiering is FOR.
    /// </summary>
    [Fact]
    public void ActionTier_SameTokenAsDefault_OriginIsStillTask()
    {
        TaskNode task = SingleTaskOf(FlatPlan(ConfigWithDefaultTier("medium"), TaskJsonWithTier("medium")));

        Assert.Equal("medium", task.Action.Tier);
        Assert.Equal(TierOrigin.Task, task.Action.TierOrigin);
    }

    /// <summary>The same trap on the waved path, where a second implementation site could reintroduce it.</summary>
    [Fact]
    public void WavedPlan_TaskTierSameTokenAsDefault_OriginIsStillTask()
    {
        TaskNode task = SingleTaskOf(WavedPlan(ConfigWithDefaultTier("hard"), TaskJsonWithTier("hard")));

        Assert.Equal("wave-01-alpha/01-task", task.Id);
        Assert.Equal("hard", task.Action.Tier);
        Assert.Equal(TierOrigin.Task, task.Action.TierOrigin);
    }

    // --- 4. neither site declared a tier ⇒ nothing resolved -----------------------------------

    [Fact]
    public void NoTierAnywhere_OriginIsNone_AndTierIsNull()
    {
        TaskNode task = SingleTaskOf(FlatPlan(UntieredConfigJson, NoActionBlockTaskJson));

        Assert.Null(task.Action.Tier);
        Assert.Equal(TierOrigin.None, task.Action.TierOrigin);
    }

    // --- 5. an unrecognized default does not propagate — so nothing landed to have an origin ---

    /// <summary>
    /// <c>PropagatableDefaultTier</c> refuses to propagate an unrecognized token (GR2043 reports it ONCE, at
    /// its declaration site, rather than multiplying one typo into an error per task). The task therefore
    /// comes out with <c>Tier == null</c>, and the origin must say <see cref="TierOrigin.None"/> — NOT
    /// <c>PlanDefault</c> over a value that never landed. The origin must always agree with what is actually
    /// in <c>Tier</c>; a <c>tierSource: "plan-default"</c> next to an absent <c>tier</c> is exactly the
    /// green-but-wrong journal record this stage exists to prevent.
    ///
    /// <para>The load itself is clean (GR2043 is the VALIDATOR's, so <c>HasErrors</c> stays false here);
    /// the assertion is on the loaded shape, not on the diagnostic.</para>
    /// </summary>
    [Fact]
    public void UnrecognizedDefaultTier_DoesNotPropagate_OriginIsNone()
    {
        TaskNode task = SingleTaskOf(FlatPlan(ConfigWithDefaultTier("wizard"), NoActionBlockTaskJson));

        Assert.Null(task.Action.Tier);
        Assert.Equal(TierOrigin.None, task.Action.TierOrigin);
    }

    /// <summary>
    /// The other half of "the origin agrees with <c>Tier</c>": an unrecognized token on the TASK is bound
    /// VERBATIM by the loader (the GR2030 preserve-the-malformed-signal doctrine — the validator judges it),
    /// so a tier IS present and the task is still its author. Reading <see cref="TierOrigin.None"/> as
    /// "unrecognized" rather than "nothing in <c>Tier</c>" would get this backwards.
    /// </summary>
    [Fact]
    public void UnrecognizedTaskTier_IsStillBound_OriginIsTask()
    {
        PlanLoadResult result = new PlanLoader().Load(FlatPlan(UntieredConfigJson, TaskJsonWithTier("extreme")));

        TaskNode task = Assert.Single(result.Plan!.Tasks);
        Assert.Equal("extreme", task.Action.Tier);
        Assert.Equal(TierOrigin.Task, task.Action.TierOrigin);
    }

    // --- 6. the waved propagation path ---------------------------------------------------------

    /// <summary>
    /// The plan-wide default reaches a WAVE task through <c>LoadWaves</c>/<c>LoadWaveTasks</c> — a separate
    /// call path from flat <c>LoadTasks</c>, and the one a hand-added task in a JIT-authored wave arrives
    /// through. Its provenance has to survive that path too.
    /// </summary>
    [Fact]
    public void WavedPlan_DefaultReachesWaveTask_OriginIsPlanDefault()
    {
        TaskNode task = SingleTaskOf(WavedPlan(ConfigWithDefaultTier("hard"), NoActionBlockTaskJson));

        Assert.Equal("wave-01-alpha/01-task", task.Id);
        Assert.Equal("hard", task.Action.Tier);
        Assert.Equal(TierOrigin.PlanDefault, task.Action.TierOrigin);
    }

    /// <summary>A waved plan with no tier at either site stays untiered, same as the flat case.</summary>
    [Fact]
    public void WavedPlan_NoTierAnywhere_OriginIsNone()
    {
        TaskNode task = SingleTaskOf(WavedPlan(UntieredConfigJson, NoActionBlockTaskJson));

        Assert.Null(task.Action.Tier);
        Assert.Equal(TierOrigin.None, task.Action.TierOrigin);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }
}
