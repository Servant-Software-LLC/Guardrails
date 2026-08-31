using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// TDD-red tests for the GR2065 / GR2067 <c>openai-compat</c> block-schema diagnostics and GR2009's
/// kind-aware fix (plan 28 §4/§7, issue #223). <c>validate</c> stays STATIC and OFFLINE — every
/// assertion here is readable from <c>guardrails.json</c> alone (plus a guardrail prompt's YAML
/// frontmatter, for the reachability-pin fixtures); nothing here spawns a process or opens a socket.
///
/// <para><b>Authored RED, before the checks exist.</b> Task 09 added the block config surface
/// (<c>PromptRunnerConfig.Endpoint</c>/<c>ContextTokens</c>/<c>ApiKeyEnv</c>/<c>Wire</c>/<c>Engine</c>)
/// and the loader binds all five verbatim, but nothing in <see cref="PlanValidator"/> inspects them
/// yet, and <c>ValidatePromptRunnerCommands</c> (GR2009) still probes every declared runner with no
/// kind filter. So every GR2065/GR2067 assertion below currently finds ZERO matching diagnostics, and
/// the GR2009 kind-aware assertion currently finds the warning on BOTH runners instead of one. That is
/// the point — the implement task makes these pass without moving this file.</para>
///
/// <para><b>Codes are asserted LITERALLY, not via a <see cref="DiagnosticCodes"/> constant.</b> Plan 28
/// §7 pins GR2065 and GR2067 by number (unlike the model-tiering Stage 1 schema, whose codes were still
/// unallocated when its tests were authored) — GR2065 is <c>DiagnosticCodes.cs</c>'s own current
/// next-free marker as of this task, so there is no constant yet to reference. GR2009 already has one
/// (<see cref="DiagnosticCodes.PromptRunnerNotOnPath"/>) and is used directly.</para>
///
/// <para><b>Reachability tests use SIBLING blocks in the SAME plan as the discriminator</b>, rather than
/// a separate "no warning" test, because a separate always-green test can never go RED today (nothing
/// warns about anything yet) and would sit outside this file's TDD-red posture. Instead, each
/// reachability test declares one block that SHOULD warn alongside one or two that must NOT, and asserts
/// <c>Assert.Single</c> against the whole diagnostics collection — so an implementation that warns
/// unconditionally (every sibling matches) fails exactly as loudly as one that never implements the rule
/// at all (nothing matches).</para>
///
/// <para><b>The "pinned" fixture is the on-disk shape production code already reads.</b>
/// <c>GuardrailRunner.cs</c>/<c>Scheduler.cs</c> resolve a judge's frontmatter <c>runner:</c> pin by
/// re-parsing the guardrail's own <c>.prompt.md</c> file at RUN time (<c>PromptFileParser.Parse</c>) —
/// <see cref="GuardrailDefinition"/> itself carries no <c>Runner</c> field. So the pinned-reachability
/// fixture below writes a REAL prompt guardrail with a REAL YAML frontmatter <c>runner:</c> block, the
/// exact shape a human already uses today, rather than a synthetic field this task has no authority to
/// add.</para>
/// </summary>
public sealed class OpenAiCompatDiagnosticsTests : IDisposable
{
    private const string Gr2065 = "GR2065";
    private const string Gr2067 = "GR2067";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gr-openai-diag-" + Guid.NewGuid().ToString("N"));

    public OpenAiCompatDiagnosticsTests() => Directory.CreateDirectory(_root);

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

    // --- GR2065: endpoint -----------------------------------------------------------------------

    /// <summary><c>endpoint</c> absent from an <c>openai-compat</c> block is a GR2065 error.</summary>
    [Fact]
    public void Endpoint_Missing_IsGr2065Error()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "local-qwen",
                "local-qwen": {
                  "kind": "openai-compat",
                  "model": "qwen3-coder:30b",
                  "contextTokens": 32768,
                  "strength": 2
                }
              }
            }
            """);

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == Gr2065);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("endpoint", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A present <c>endpoint</c> that is not an ABSOLUTE http/https URL is a GR2065 error: no scheme,
    /// the wrong scheme, and a relative path all fail the same way.
    /// </summary>
    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://inference.local:11434")]
    [InlineData("/v1/chat/completions")]
    public void Endpoint_NotAbsoluteHttpUrl_IsGr2065Error(string endpoint)
    {
        Loaded loaded = Load($$"""
            {
              "version": 1,
              "promptRunners": {
                "default": "local-qwen",
                "local-qwen": {
                  "kind": "openai-compat",
                  "endpoint": "{{endpoint}}",
                  "model": "qwen3-coder:30b",
                  "contextTokens": 32768,
                  "strength": 2
                }
              }
            }
            """);

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == Gr2065);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("endpoint", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- GR2065: model ---------------------------------------------------------------------------

    /// <summary><c>model</c> absent from an <c>openai-compat</c> block is a GR2065 error.</summary>
    [Fact]
    public void Model_Missing_IsGr2065Error()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "local-qwen",
                "local-qwen": {
                  "kind": "openai-compat",
                  "endpoint": "http://127.0.0.1:11434/v1",
                  "contextTokens": 32768,
                  "strength": 2
                }
              }
            }
            """);

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == Gr2065);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("model", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- GR2065: contextTokens --------------------------------------------------------------------

    /// <summary><c>contextTokens</c> absent from an <c>openai-compat</c> block is a GR2065 error.</summary>
    [Fact]
    public void ContextTokens_Missing_IsGr2065Error()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "local-qwen",
                "local-qwen": {
                  "kind": "openai-compat",
                  "endpoint": "http://127.0.0.1:11434/v1",
                  "model": "qwen3-coder:30b",
                  "strength": 2
                }
              }
            }
            """);

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == Gr2065);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("contextTokens", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A present <c>contextTokens</c> below 1 (the boundary GR2065 rejects) is an error.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ContextTokens_BelowOne_IsGr2065Error(int contextTokens)
    {
        Loaded loaded = Load($$"""
            {
              "version": 1,
              "promptRunners": {
                "default": "local-qwen",
                "local-qwen": {
                  "kind": "openai-compat",
                  "endpoint": "http://127.0.0.1:11434/v1",
                  "model": "qwen3-coder:30b",
                  "contextTokens": {{contextTokens}},
                  "strength": 2
                }
              }
            }
            """);

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == Gr2065);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("contextTokens", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- GR2065: wire overriding a harness-owned field --------------------------------------------

    /// <summary>
    /// A <c>wire</c> map that overrides a harness-owned request field is a GR2065 error —
    /// <c>wire: {"stream": false}</c> is the exact typo the plan names as the one that would silently
    /// disable streaming. Covers all six harness-owned fields the plan lists.
    /// </summary>
    [Theory]
    [InlineData("model", "\"some-other-model\"")]
    [InlineData("messages", "[]")]
    [InlineData("stream", "false")]
    [InlineData("stream_options", "{}")]
    [InlineData("tools", "[]")]
    [InlineData("max_tokens", "100")]
    public void WireOverridingHarnessOwnedField_IsGr2065Error(string field, string valueJson)
    {
        Loaded loaded = Load($$"""
            {
              "version": 1,
              "promptRunners": {
                "default": "local-qwen",
                "local-qwen": {
                  "kind": "openai-compat",
                  "endpoint": "http://127.0.0.1:11434/v1",
                  "model": "qwen3-coder:30b",
                  "contextTokens": 32768,
                  "strength": 2,
                  "wire": { "{{field}}": {{valueJson}} }
                }
              }
            }
            """);

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == Gr2065);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("wire", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(field, error.Message, StringComparison.Ordinal);
    }

    // --- GR2065: the four new keys on a non-openai-compat block -----------------------------------

    /// <summary>
    /// Any of the four new keys (<c>endpoint</c>/<c>contextTokens</c>/<c>apiKeyEnv</c>/<c>wire</c>) on
    /// a block whose <c>kind</c> is NOT <c>openai-compat</c> is a GR2065 error — a key that does nothing
    /// where it was written is indistinguishable from one that works.
    /// </summary>
    [Theory]
    [InlineData("\"endpoint\": \"http://127.0.0.1:11434/v1\"", "endpoint")]
    [InlineData("\"contextTokens\": 8192", "contextTokens")]
    [InlineData("\"apiKeyEnv\": \"LOCAL_INFERENCE_KEY\"", "apiKeyEnv")]
    [InlineData("\"wire\": { \"keep_alive\": \"30m\" }", "wire")]
    public void NewKeyOnNonOpenAiCompatBlock_IsGr2065Error(string keyJson, string keyName)
    {
        Loaded loaded = Load($$"""
            {
              "version": 1,
              "promptRunners": {
                "default": "claude",
                "claude": { "command": "claude", {{keyJson}} }
              }
            }
            """);

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == Gr2065);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains(keyName, error.Message, StringComparison.Ordinal);
    }

    // --- GR2067: no strength declared --------------------------------------------------------------

    /// <summary>
    /// An <c>openai-compat</c> block declaring no <c>strength</c> is a GR2067 warning —
    /// <c>TierResolver.IsWeakVerifier</c> treats a null-strength non-Claude block as permanently weak.
    /// "ai-triage" (strength declared, also reachable by reserved profile name) must NOT also warn,
    /// which is what proves the check is bound to the absence of <c>strength</c> and not merely to
    /// declaring an <c>openai-compat</c> block at all.
    /// </summary>
    [Fact]
    public void NoStrengthDeclared_WarnsGr2067_ButNotItsRankedSibling()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "ai-triage",
                "overwatch": {
                  "kind": "openai-compat",
                  "endpoint": "http://127.0.0.1:11434/v1",
                  "model": "qwen3-coder:30b",
                  "contextTokens": 32768
                },
                "ai-triage": {
                  "kind": "openai-compat",
                  "endpoint": "http://127.0.0.1:11434/v1",
                  "model": "qwen3-coder:30b",
                  "contextTokens": 32768,
                  "strength": 3
                }
              }
            }
            """);

        Diagnostic warning = Assert.Single(loaded.Diagnostics, d => d.Code == Gr2067);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("strength", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("overwatch", warning.Message, StringComparison.Ordinal);
    }

    // --- GR2067: unreachable block -------------------------------------------------------------------

    /// <summary>
    /// An <c>openai-compat</c> block that is neither pinned nor a reserved profile name is a GR2067
    /// warning — the exact case the plan names: <c>"triage"</c> written where <c>"ai-triage"</c> was
    /// meant. Two siblings in the SAME plan prove the check discriminates rather than always firing:
    /// <c>"ai-triage"</c> IS a reserved profile name, and <c>"local-pinned"</c> is pinned by a real
    /// guardrail's frontmatter <c>runner:</c> — neither may warn.
    /// </summary>
    [Fact]
    public void UnreachableBlock_WarnsGr2067_ButNotAPinnedOrReservedProfileSibling()
    {
        string guardrailsJson = """
            {
              "version": 1,
              "promptRunners": {
                "default": "ai-triage",
                "triage": {
                  "kind": "openai-compat",
                  "endpoint": "http://127.0.0.1:11434/v1",
                  "model": "qwen3-coder:30b",
                  "contextTokens": 32768,
                  "strength": 2
                },
                "ai-triage": {
                  "kind": "openai-compat",
                  "endpoint": "http://127.0.0.1:11434/v1",
                  "model": "qwen3-coder:30b",
                  "contextTokens": 32768,
                  "strength": 2
                },
                "local-pinned": {
                  "kind": "openai-compat",
                  "endpoint": "http://127.0.0.1:11434/v1",
                  "model": "qwen3-coder:30b",
                  "contextTokens": 32768,
                  "strength": 2
                }
              }
            }
            """;

        Loaded loaded = Load(guardrailsJson, pinnedRunner: "local-pinned");

        Diagnostic warning = Assert.Single(loaded.Diagnostics, d => d.Code == Gr2067);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("triage", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ai-triage", warning.Message, StringComparison.Ordinal);
    }

    // --- GR2009: kind-aware --------------------------------------------------------------------------

    /// <summary>
    /// GR2009 (the PATH probe) still fires for a <c>claude</c> runner whose command is not on PATH, but
    /// must NOT fire for an <c>openai-compat</c> sibling — probing a URL/block-name against PATH is a
    /// confident, wrong warning (plan §4). Both commands are equally unresolvable under the fake probe,
    /// so the only thing that can explain a different outcome is the kind filter.
    /// </summary>
    [Fact]
    public void Gr2009_StillProbesClaudeOnPath_ButSkipsOpenAiCompat()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "claude",
                "claude": { "command": "claude-cli-not-on-path" },
                "local-qwen": {
                  "kind": "openai-compat",
                  "endpoint": "http://127.0.0.1:11434/v1",
                  "model": "qwen3-coder:30b",
                  "contextTokens": 32768,
                  "strength": 2
                }
              }
            }
            """,
            probe: FakeExecutableProbe.None);

        Diagnostic warning = Assert.Single(loaded.Diagnostics, d => d.Code == DiagnosticCodes.PromptRunnerNotOnPath);
        Assert.Contains("claude", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("local-qwen", warning.Message, StringComparison.Ordinal);
    }

    // --- the negative: no openai-compat block, no noise ------------------------------------------

    /// <summary>
    /// A plan with NO <c>openai-compat</c> block emits none of GR2065/GR2067/GR2009. Unlike every test
    /// above, this one is expected to be GREEN both today and after the implement task — it is the
    /// additive-safety pin, not a red discriminator.
    /// </summary>
    [Fact]
    public void PlanWithNoOpenAiCompatBlock_EmitsNoneOfTheseCodes()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "claude",
                "claude": { "command": "claude" }
              }
            }
            """,
            probe: FakeExecutableProbe.With("claude"));

        Assert.DoesNotContain(loaded.Diagnostics, d => d.Code == Gr2065);
        Assert.DoesNotContain(loaded.Diagnostics, d => d.Code == Gr2067);
        Assert.DoesNotContain(loaded.Diagnostics, d => d.Code == DiagnosticCodes.PromptRunnerNotOnPath);
    }

    // --- harness --------------------------------------------------------------------------------

    /// <summary>A loaded plan plus every diagnostic from BOTH phases — loading and validation.</summary>
    private sealed record Loaded(PlanDefinition? Plan, IReadOnlyList<Diagnostic> Diagnostics);

    /// <summary>
    /// Load and validate a plan whose <c>guardrails.json</c> is <paramref name="guardrailsJson"/>.
    /// Diagnostics from both phases are pooled because WHERE a malformed value is caught (the loader's
    /// deserialization or the validator's rules) is the implementation's choice; that it is caught, with
    /// the right severity and an actionable message, is the plan's requirement.
    /// </summary>
    private Loaded Load(string guardrailsJson, string? pinnedRunner = null, IExecutableProbe? probe = null)
    {
        PlanLoadResult result = new PlanLoader().Load(PlanWith(guardrailsJson, pinnedRunner));
        List<Diagnostic> diagnostics = [.. result.Diagnostics];

        if (result.Plan is not null)
        {
            diagnostics.AddRange(new PlanValidator(probe ?? FakeExecutableProbe.All).Validate(result.Plan));
        }

        return new Loaded(result.Plan, diagnostics);
    }

    /// <summary>
    /// A minimal one-task plan folder carrying the given config. When <paramref name="pinnedRunner"/> is
    /// given, an additional PROMPT guardrail is written whose YAML frontmatter pins <c>runner:</c> to
    /// it — the exact on-disk shape a human uses to pin a judge (SSOT §9.6 rule 1), and the only way to
    /// express that pin since <see cref="GuardrailDefinition"/> carries no <c>Runner</c> field of its
    /// own.
    /// </summary>
    private string PlanWith(string guardrailsJson, string? pinnedRunner)
    {
        File.WriteAllText(Path.Combine(_root, "guardrails.json"), guardrailsJson);

        string taskDir = Path.Combine(_root, "tasks", "01-task");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t", "writeScope": [], "dependsOn": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "exit 0\n");

        if (pinnedRunner is not null)
        {
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "02-verdict.prompt.md"),
                "---\n" + $"runner: {pinnedRunner}\n" + "---\n" + "\n" + "Render a verdict.\n");
        }

        return _root;
    }
}
