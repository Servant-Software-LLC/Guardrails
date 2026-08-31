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
    /// discriminator existed is implicitly Claude), and each accepted token PARSES to its kind. The block
    /// is named "primary", not "claude", so the default can only come from the schema default and never
    /// from the map key.
    ///
    /// <para>This asserts PARSING only. Whether a parsed kind is one this build can SERVE is a separate
    /// question with a separate answer — see
    /// <see cref="RecognizedButUnimplementedKind_FailsValidate_NotJustRegistryConstruction"/>. Keeping the
    /// two apart is what lets a reserved name (<c>openai-compat</c>, #223) be spelled correctly today and
    /// implemented later without the parse test moving.</para>
    /// </summary>
    [Theory]
    [InlineData(null, PromptRunnerKind.Claude)]
    [InlineData("claude", PromptRunnerKind.Claude)]
    [InlineData("codex", PromptRunnerKind.Codex)]
    [InlineData("openrouter", PromptRunnerKind.OpenRouter)]
    [InlineData("local", PromptRunnerKind.Local)]
    [InlineData("openai-compat", PromptRunnerKind.OpenAiCompat)]
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

        Assert.Equal(expected, loaded.Runner("primary").Kind);
    }

    /// <summary>
    /// <c>openai-compat</c> — the #223 seam covering Ollama / llama.cpp / LM Studio / MLX / vLLM, which
    /// share a wire protocol — is a RECOGNIZED token, and now an IMPLEMENTED one. It is asserted
    /// separately from the parse theory above because of what it would otherwise cost: the
    /// design-of-record's own worked example (§14) writes <c>"kind": "openai-compat"</c>, so a config
    /// copied straight out of the design would have failed validation as an UNRECOGNIZED kind — a message
    /// saying the design is wrong rather than that the build is early.
    ///
    /// <para><b>What changed, and what deliberately did not.</b> Stage 1 also asserted here that the kind
    /// was refused for having NO IMPLEMENTATION, which was true when it was written and is exactly the
    /// premise plan 28 exists to falsify (§3.1: <i>"v1 implements <c>PromptRunnerKind.OpenAiCompat</c> and
    /// nothing else"</i>). That half is gone. The recognition half stays, because it is still worth
    /// pinning that this token never reads as unknown — and
    /// <see cref="RecognizedButUnimplementedKind_FailsValidate_NotJustRegistryConstruction"/> keeps its
    /// full force for <c>codex</c>, <c>openrouter</c> and <c>local</c>, which remain reserved names and
    /// remain GR2044 errors.</para>
    /// </summary>
    [Fact]
    public void OpenAiCompatKind_IsRecognized_NotAnUnknownToken()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "local-kimi",
                "local-kimi": { "kind": "openai-compat", "command": "http://inference.local:11434" }
              }
            }
            """);

        Assert.Equal(PromptRunnerKind.OpenAiCompat, loaded.Runner("local-kimi").Kind);

        // Neither half of GR2044 fires for this token any more: not the loader's "unrecognised kind", and
        // not the validator's "recognised but has NO implementation in this build". Both are asserted,
        // because they share one diagnostic code — a test that checked only the code would not be able to
        // tell which of the two it had ruled out.
        Assert.DoesNotContain(loaded.Diagnostics, d => d.Code == DiagnosticCodes.InvalidPromptRunnerKind);
        Assert.DoesNotContain(
            loaded.Diagnostics, d => d.Message.Contains("no implementation", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A RECOGNIZED-but-unimplemented <c>kind</c> fails <c>guardrails validate</c> (GR2044) — the change
    /// Stage 1.5 makes to what Stage 1 shipped. Stage 1 let such a config load and validate CLEAN and
    /// caught it only at <c>PromptRunnerRegistry</c> construction; the design's rule is that <b>registry
    /// construction is the BACKSTOP, not the gate</b>. The difference is real and not cosmetic: under the
    /// old behaviour a `validate`-clean config would begin a run and then die composing the registry,
    /// which is the "knowable at load time, discovered at run time" cascade this repo turns into
    /// load-time catches everywhere else.
    /// </summary>
    [Theory]
    [InlineData("codex")]
    [InlineData("openrouter")]
    [InlineData("local")]
    public void RecognizedButUnimplementedKind_FailsValidate_NotJustRegistryConstruction(string kind)
    {
        Loaded loaded = Load($$"""
            {
              "version": 1,
              "promptRunners": {
                "default": "primary",
                "primary": { "kind": "{{kind}}", "command": "some-cli" }
              }
            }
            """);

        Diagnostic error = Assert.Single(
            loaded.Diagnostics, d => d.Code == DiagnosticCodes.InvalidPromptRunnerKind);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains(kind, error.Message, StringComparison.Ordinal);

        // …and it names what this build CAN serve, so the fix does not require reading the source.
        Assert.Contains("'claude'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The implemented-kind GATE and the registry's dispatch BACKSTOP must agree, for every kind. They
    /// are two statements of one fact in two files, which is precisely the shape that drifts: a kind
    /// added to <see cref="PromptRunnerKinds.Implemented"/> without a dispatch arm would validate clean
    /// and then throw mid-run, and an arm added without the list entry would be permanently unreachable
    /// behind a validation error. Pinning them together costs one test and removes the whole class.
    /// </summary>
    [Fact]
    public void ImplementedKindList_AgreesWithRegistryDispatch_ForEveryKind()
    {
        foreach (PromptRunnerKind kind in Enum.GetValues<PromptRunnerKind>())
        {
            var config = new RunConfig
            {
                Version = 1,
                PromptRunnerNames = new HashSet<string>(StringComparer.Ordinal) { "primary" },
                DefaultPromptRunner = "primary",
                PromptRunners = new Dictionary<string, PromptRunnerConfig>(StringComparer.Ordinal)
                {
                    ["primary"] = new()
                    {
                        Name = "primary",
                        Command = "cli-under-test",
                        Settings = new PromptRunnerSettings(),
                        Kind = kind
                    }
                }
            };

            Exception? failure = Record.Exception(
                () => Guardrails.Core.Prompts.PromptRunnerRegistry.FromConfig(config, new Guardrails.Core.Execution.ProcessRunner()));

            Assert.True(
                PromptRunnerKinds.IsImplemented(kind) == (failure is null),
                $"kind '{PromptRunnerKinds.Token(kind)}': PromptRunnerKinds.IsImplemented says " +
                $"{PromptRunnerKinds.IsImplemented(kind)}, but registry construction " +
                $"{(failure is null ? "SUCCEEDED" : "threw " + failure.GetType().Name)}. The validate-time " +
                "gate and the runtime backstop have drifted.");
        }
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
        Assert.Null(runner.Effort);
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
                  "kind": "claude",
                  "costly": true,
                  "strength": 7,
                  "specialization": "planning-reasoning"
                }
              }
            }
            """);

        AssertNoErrors(first);
        PromptRunnerConfig parsed = first.Runner("primary");
        Assert.Equal(PromptRunnerKind.Claude, parsed.Kind);
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
    /// Per-model <c>routing</c> exists, validates, and round-trips — <c>tiers</c> (the machine-consumed
    /// half) alongside the human-facing prose. The prose keys are asserted here as well as
    /// <c>tiers</c> because they are ADDITIVE, not superseded: Stage 1.5 adds a required key to this
    /// block, it does not take the Stage-1 ones away, and a config carrying both must keep both.
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
                    "tiers": ["medium", "hard"],
                    "notes": "cross-module architecture; retry/journal contract work.",
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
        Assert.Equal(["medium", "hard"], parsed.Routing.Tiers);
        Assert.Equal("cross-module architecture; retry/journal contract work.", parsed.Routing.Notes);
        Assert.Equal("Prefer for wide-context refactors; avoid for one-line fixes.", parsed.Routing.Guidance);
        Assert.Equal(["refactoring", "long-context"], parsed.Routing.Tags);

        Loaded second = Load(Render(parsed));

        AssertNoErrors(second);
        PromptRunnerRouting? round = second.Runner("primary").Routing;
        Assert.NotNull(round);
        Assert.Equal(parsed.Routing.Tiers, round.Tiers);
        Assert.Equal(parsed.Routing.Notes, round.Notes);
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
                  "routing": { "tiers": ["medium"], "rank": 2, "guidance": "Legacy block mid-migration." }
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

        if (runner.Effort is { } effort)
        {
            block["effort"] = effort;
        }

        if (runner.Routing is { } routing)
        {
            var routingBlock = new JsonObject();

            // `tiers` is REQUIRED, so it is emitted unconditionally: a Render that dropped it would
            // produce a document the loader rejects, turning every round-trip assertion into a GR2047
            // report instead of the comparison it is meant to be.
            var tiers = new JsonArray();
            foreach (string tier in routing.Tiers)
            {
                tiers.Add(tier);
            }

            routingBlock["tiers"] = tiers;

            if (routing.Notes is not null)
            {
                routingBlock["notes"] = routing.Notes;
            }

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
