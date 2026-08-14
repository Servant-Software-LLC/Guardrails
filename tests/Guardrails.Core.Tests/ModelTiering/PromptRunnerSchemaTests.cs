using System.Text.Json.Nodes;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// The Stage 1 provider-registry SCHEMA (model-tiering-stage-1 charter §A, issue #224): the
/// <c>kind</c> discriminator, the three per-model axes (<c>costly</c> / <c>strength</c> /
/// <c>specialization</c>), per-model <c>routing</c> guidance, and the retired-<c>rank</c> warning.
/// Everything here goes through the REAL pipeline — <see cref="PlanLoader"/> then
/// <see cref="PlanValidator"/> — because "validates" is a claim about that pipeline, not about a
/// record's property initializers.
///
/// <para><b>These tests are authored RED, before the behaviour exists.</b> The stubs in
/// <c>PromptRunnerConfig.cs</c> declare the shape; the loader/validator still ignore the new keys,
/// so every assertion about a PARSED value or an emitted diagnostic fails until the implement task
/// lands. That is the point — a test that passes against the stubs is asserting nothing.</para>
///
/// <para><b><c>[Trait("Category", "ModelTieringStage1")]</c> is load-bearing.</b> The plan's baseline
/// preflight runs <c>--filter "Category!=ModelTieringStage1"</c>, so this deliberately-red file never
/// masquerades as pre-existing breakage in the "never build on red" baseline. Keep the trait on the
/// class; a case that loses it breaks that filter.</para>
///
/// <para><b>Diagnostics are asserted by MESSAGE and SEVERITY, not by <c>DiagnosticCodes</c> constant.</b>
/// The codes (GR2043+) are the implement task's to allocate — the charter explicitly warns that the
/// block must be re-verified against <c>DiagnosticCodes.cs</c> at landing. Pinning a code here would
/// pin a number this task has no authority over; what the plan actually promises is a message that
/// NAMES the offending value, which is what these tests hold it to.</para>
/// </summary>
[Trait("Category", "ModelTieringStage1")]
public sealed class PromptRunnerSchemaTests : IDisposable
{
    private readonly string _root;

    public PromptRunnerSchemaTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-mt-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // --- 1/2. the kind discriminator ---------------------------------------------------

    /// <summary>
    /// ABSENT <c>kind</c> ⇒ <c>claude</c> (the additive guarantee — every config written before the
    /// discriminator existed is implicitly Claude), and each of the four accepted tokens parses to its
    /// kind. The block is named "primary", not "claude", so the default can only come from the schema
    /// default and never from the map key.
    /// </summary>
    [Theory]
    [InlineData(null, PromptRunnerKind.Claude)]
    [InlineData("claude", PromptRunnerKind.Claude)]
    [InlineData("codex", PromptRunnerKind.Codex)]
    [InlineData("openrouter", PromptRunnerKind.OpenRouter)]
    [InlineData("local", PromptRunnerKind.Local)]
    public void Kind_DefaultsToClaudeWhenAbsent_AndParsesEveryAcceptedValue(string? kind, PromptRunnerKind expected)
    {
        string kindKey = kind is null ? string.Empty : $"\"kind\": \"{kind}\", ";
        Loaded loaded = Load($$"""
            {
              "version": 1,
              "promptRunners": {
                "default": "primary",
                "primary": { {{kindKey}}"command": "claude" }
              }
            }
            """);

        AssertNoErrors(loaded);
        Assert.Equal(expected, loaded.Runner("primary").Kind);
    }

    /// <summary>
    /// The back-compat pin: a config written BEFORE any of this existed still loads clean, and none of
    /// the new keys is fabricated onto it. "Additive, not breaking" is the charter's first acceptance
    /// criterion, and an absent axis must stay absent — a defaulted-to-something axis would feed the
    /// Stage 2 resolver an opinion the operator never expressed.
    /// </summary>
    [Fact]
    public void ConfigWithNoTieringKeys_ValidatesUnchanged_AndFabricatesNoAxes()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "claude",
                "claude": {
                  "command": "claude",
                  "permissionMode": "acceptEdits",
                  "maxTurns": 50,
                  "guardrailOverrides": { "permissionMode": "default", "maxTurns": 20 }
                }
              }
            }
            """);

        AssertNoErrors(loaded);

        PromptRunnerConfig runner = loaded.Runner("claude");
        Assert.Equal(PromptRunnerKind.Claude, runner.Kind);
        Assert.Null(runner.Costly);
        Assert.Null(runner.Strength);
        Assert.Equal(PromptRunnerSpecialization.Unspecified, runner.Specialization);
        Assert.Null(runner.Routing);

        // The pre-existing settings are untouched by the new schema surface.
        Assert.Equal(50, runner.Settings.MaxTurns);
        Assert.Equal(20, runner.EffectiveSettings(isGuardrail: true).MaxTurns);
    }

    /// <summary>
    /// An unrecognized <c>kind</c> is an ERROR that NAMES the bad value. Naming it is the whole point:
    /// the operator typed something, and a message that only says "invalid kind" makes them hunt for
    /// which of several blocks is wrong.
    /// </summary>
    [Fact]
    public void UnrecognizedKind_IsAnError_NamingTheBadValue()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "primary",
                "primary": { "kind": "gpt-9-ultra", "command": "gpt" }
              }
            }
            """);

        RequireDiagnostic(loaded, DiagnosticSeverity.Error, "gpt-9-ultra");
    }

    // --- 3/4. the three axes ------------------------------------------------------------

    /// <summary>
    /// The axes are TOP-LEVEL on the block (charter Decision 7 — not nested under <c>routing</c>, not
    /// under <c>settings</c>), and a block carrying all three survives a parse → serialise → parse
    /// cycle unchanged. The second leg is what makes this more than a parse test: it proves the values
    /// come back out in a form the schema itself accepts, which is what <c>providers init</c> will
    /// depend on when it writes blocks back to <c>guardrails.json</c>.
    /// </summary>
    [Fact]
    public void Axes_AreTopLevelOnTheBlock_AndSurviveAParseSerialiseCycle()
    {
        Loaded first = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "primary",
                "primary": {
                  "command": "claude",
                  "kind": "codex",
                  "costly": true,
                  "strength": 7,
                  "specialization": "planning-reasoning"
                }
              }
            }
            """);

        AssertNoErrors(first);
        PromptRunnerConfig parsed = first.Runner("primary");
        Assert.Equal(PromptRunnerKind.Codex, parsed.Kind);
        Assert.Equal(true, parsed.Costly);
        Assert.Equal(7, parsed.Strength);
        Assert.Equal(PromptRunnerSpecialization.PlanningReasoning, parsed.Specialization);

        Loaded second = Load(Render(parsed));

        AssertNoErrors(second);
        PromptRunnerConfig round = second.Runner("primary");
        Assert.Equal(parsed.Kind, round.Kind);
        Assert.Equal(parsed.Costly, round.Costly);
        Assert.Equal(parsed.Strength, round.Strength);
        Assert.Equal(parsed.Specialization, round.Specialization);
    }

    /// <summary>Every accepted <c>specialization</c> token parses — including <c>unspecified</c>, which is writable, not merely the absent-key fallback.</summary>
    [Theory]
    [InlineData("coding", PromptRunnerSpecialization.Coding)]
    [InlineData("planning-reasoning", PromptRunnerSpecialization.PlanningReasoning)]
    [InlineData("general", PromptRunnerSpecialization.General)]
    [InlineData("unspecified", PromptRunnerSpecialization.Unspecified)]
    public void Specialization_ParsesEveryAcceptedValue(string token, PromptRunnerSpecialization expected)
    {
        Loaded loaded = Load($$"""
            {
              "version": 1,
              "promptRunners": {
                "default": "primary",
                "primary": { "command": "claude", "specialization": "{{token}}" }
              }
            }
            """);

        AssertNoErrors(loaded);
        Assert.Equal(expected, loaded.Runner("primary").Specialization);
    }

    /// <summary>1 is the lowest legal <c>strength</c> — the boundary the <c>strength: 0</c> rejection sits against.</summary>
    [Fact]
    public void Strength_OfOne_IsAccepted()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "primary",
                "primary": { "command": "claude", "strength": 1 }
              }
            }
            """);

        AssertNoErrors(loaded);
        Assert.Equal(1, loaded.Runner("primary").Strength);
    }

    /// <summary>
    /// Each malformed axis form is a validation ERROR naming the axis: a non-bool <c>costly</c>, a
    /// <c>strength</c> below 1, and an out-of-enum <c>specialization</c>. Silently ignoring any of
    /// these would leave the operator believing they had expressed a routing preference they had not.
    /// </summary>
    [Theory]
    [InlineData("\"costly\": \"yes\"", "costly")]
    [InlineData("\"strength\": 0", "strength")]
    [InlineData("\"specialization\": \"quantum-poetry\"", "specialization")]
    public void MalformedAxis_FailsValidation(string axisJson, string axisName)
    {
        Loaded loaded = Load($$"""
            {
              "version": 1,
              "promptRunners": {
                "default": "primary",
                "primary": { "command": "claude", {{axisJson}} }
              }
            }
            """);

        RequireDiagnostic(loaded, DiagnosticSeverity.Error, axisName);
    }

    // --- 5/6. routing guidance and the retired rank -------------------------------------

    /// <summary>
    /// Per-model <c>routing</c> guidance exists, validates, and round-trips. Stage 2 (#226) is its
    /// first reader, so this stage asserts nothing about what it MEANS — only that prose and tags
    /// survive the parse/serialise cycle intact rather than being dropped on the floor.
    /// </summary>
    [Fact]
    public void RoutingGuidance_ValidatesAndRoundTrips()
    {
        Loaded first = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "primary",
                "primary": {
                  "command": "claude",
                  "strength": 4,
                  "routing": {
                    "guidance": "Prefer for wide-context refactors; avoid for one-line fixes.",
                    "tags": ["refactoring", "long-context"]
                  }
                }
              }
            }
            """);

        AssertNoErrors(first);
        PromptRunnerConfig parsed = first.Runner("primary");
        Assert.NotNull(parsed.Routing);
        Assert.Equal("Prefer for wide-context refactors; avoid for one-line fixes.", parsed.Routing.Guidance);
        Assert.Equal(["refactoring", "long-context"], parsed.Routing.Tags);

        Loaded second = Load(Render(parsed));

        AssertNoErrors(second);
        PromptRunnerRouting? round = second.Runner("primary").Routing;
        Assert.NotNull(round);
        Assert.Equal(parsed.Routing.Guidance, round.Guidance);
        Assert.Equal(parsed.Routing.Tags, round.Tags);
    }

    /// <summary>
    /// A config still carrying <c>routing.rank</c> gets a retired-field WARNING — not an error, and not
    /// silence. Not an error because a migrated config must keep loading; not silence because
    /// <c>rank</c> is NOT implemented (settled OD-F: ordering is ascending <c>strength</c>, weakest
    /// model that can serve the tier first). Accepting <c>rank</c> quietly is exactly how a migrated
    /// config's ordering changes without anyone being told.
    /// </summary>
    [Fact]
    public void RetiredRoutingRank_IsAWarning_NotAnError()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "primary",
                "primary": {
                  "command": "claude",
                  "strength": 4,
                  "routing": { "rank": 2, "guidance": "Legacy block mid-migration." }
                }
              }
            }
            """);

        RequireDiagnostic(loaded, DiagnosticSeverity.Warning, "rank");

        // The config still LOADS: a retired key is a warning about ordering, not a broken config.
        AssertNoErrors(loaded);
        Assert.NotNull(loaded.Plan);
        Assert.Equal("Legacy block mid-migration.", loaded.Runner("primary").Routing?.Guidance);
    }

    // --- harness ------------------------------------------------------------------------

    /// <summary>A loaded plan plus every diagnostic from BOTH phases — loading and validation.</summary>
    private sealed record Loaded(PlanDefinition? Plan, IReadOnlyList<Diagnostic> Diagnostics)
    {
        public PromptRunnerConfig Runner(string name) => Plan!.Config.PromptRunners[name];
    }

    /// <summary>
    /// Load and validate a plan whose <c>guardrails.json</c> is <paramref name="guardrailsJson"/>.
    /// Diagnostics from both phases are pooled because WHERE a malformed value is caught (the loader's
    /// deserialization or the validator's rules) is the implementation's choice; that it is caught,
    /// with the right severity and an actionable message, is the plan's requirement.
    /// </summary>
    private Loaded Load(string guardrailsJson)
    {
        PlanLoadResult result = new PlanLoader().Load(PlanWith(guardrailsJson));
        List<Diagnostic> diagnostics = [.. result.Diagnostics];

        if (result.Plan is not null)
        {
            diagnostics.AddRange(new PlanValidator(FakeExecutableProbe.All).Validate(result.Plan));
        }

        return new Loaded(result.Plan, diagnostics);
    }

    /// <summary>A minimal one-prompt-task plan folder carrying the given config.</summary>
    private string PlanWith(string guardrailsJson)
    {
        File.WriteAllText(Path.Combine(_root, "guardrails.json"), guardrailsJson);

        string taskDir = Path.Combine(_root, "tasks", "01-task");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t", "writeScope": [], "dependsOn": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "exit 0\n");

        return _root;
    }

    /// <summary>
    /// Serialise a parsed runner back to a full <c>guardrails.json</c> — the "serialise" leg of the
    /// round-trip. It goes through the shared wire-token mappings so the test cannot round-trip via a
    /// private spelling the loader would not accept.
    /// </summary>
    private static string Render(PromptRunnerConfig runner)
    {
        var block = new JsonObject
        {
            ["command"] = runner.Command,
            ["kind"] = PromptRunnerKinds.Token(runner.Kind),
            ["specialization"] = PromptRunnerSpecializations.Token(runner.Specialization)
        };

        if (runner.Costly is bool costly)
        {
            block["costly"] = costly;
        }

        if (runner.Strength is int strength)
        {
            block["strength"] = strength;
        }

        if (runner.Routing is { } routing)
        {
            var routingBlock = new JsonObject();
            if (routing.Guidance is not null)
            {
                routingBlock["guidance"] = routing.Guidance;
            }

            if (routing.Tags.Count > 0)
            {
                var tags = new JsonArray();
                foreach (string tag in routing.Tags)
                {
                    tags.Add(tag);
                }

                routingBlock["tags"] = tags;
            }

            block["routing"] = routingBlock;
        }

        var root = new JsonObject
        {
            ["version"] = 1,
            ["promptRunners"] = new JsonObject { ["default"] = runner.Name, [runner.Name] = block }
        };

        return root.ToJsonString();
    }

    /// <summary>
    /// Assert a diagnostic of the given severity mentions <paramref name="mustMention"/> — the bad
    /// value or the offending key. <see cref="DiagnosticCodes.WorkspaceNotGitRoot"/> is excluded
    /// because every one of these plans lives in a temp folder, not a git root.
    /// </summary>
    private static void RequireDiagnostic(Loaded loaded, DiagnosticSeverity severity, string mustMention)
    {
        Diagnostic[] matches =
        [
            .. loaded.Diagnostics.Where(d =>
                d.Code != DiagnosticCodes.WorkspaceNotGitRoot
                && d.Message.Contains(mustMention, StringComparison.OrdinalIgnoreCase))
        ];

        Assert.False(
            matches.Length == 0,
            $"expected a {severity} diagnostic mentioning '{mustMention}'; diagnostics were:\n{Dump(loaded)}");
        Assert.Equal(severity, matches[0].Severity);
    }

    private static void AssertNoErrors(Loaded loaded)
    {
        Diagnostic[] errors =
        [
            .. loaded.Diagnostics.Where(d =>
                d.Severity == DiagnosticSeverity.Error && d.Code != DiagnosticCodes.WorkspaceNotGitRoot)
        ];

        Assert.False(errors.Length > 0, $"unexpected validation errors:\n{string.Join("\n", errors)}");
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
