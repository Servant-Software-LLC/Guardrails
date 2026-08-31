using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// TDD-red tests for GR2066 (plan 28 §3.7/§7, issue #223): an <c>openai-compat</c> <c>promptRunners</c>
/// block is an ERROR when it is reachable for an <b>Action</b>. <c>validate</c> stays STATIC and
/// OFFLINE — every assertion here is readable from <c>guardrails.json</c> plus a prompt file's YAML
/// frontmatter; nothing spawns a process or opens a socket.
///
/// <para><b>Authored RED, before the check exists.</b> <c>DiagnosticCodes.cs</c> reserves GR2066 BY NAME
/// (task 19 deliberately skipped it) but declares no constant, and nothing in <see cref="PlanValidator"/>
/// inspects reachability yet — so every assertion below currently finds ZERO matching diagnostics. The
/// route-4 (frontmatter) test has a SECOND reason to be red today: §3.7 records that an action prompt's
/// frontmatter is folded onto nothing at load time, and only task 21's loader-fold change makes that
/// route visible to a validator at all. This task authors the tests only; a later task makes them pass.</para>
///
/// <para><b>GR2066 is asserted as a string literal</b>, not via a <see cref="DiagnosticCodes"/> constant —
/// none exists yet (the marker comment above <c>OpenAiCompatWeakOrUnreachable</c> still reads "CURRENT
/// next-free code: GR2068" with GR2066 named but unallocated).</para>
///
/// <para><b>Each of the five §3.7 routes gets its own test</b> (route 2 gets two, since the "effective
/// default" route has a default-pointer half and a sole-declared-runner half that
/// <c>PromptRunnerRegistry.ResolveDefault</c> treats identically) — a single combined test would let one
/// route regress unnoticed by the other five. Every route fixture isolates its ONE route: the
/// <c>openai-compat</c> block under test never also has <c>routing</c>, is never also the effective
/// default, is never also named by <c>action.runner</c> or a prompt's frontmatter, and is never also a
/// reserved profile name, unless that is the specific route being tested.</para>
///
/// <para><b>The two negatives get their own tests too, each with a discriminator.</b> A block pinned by a
/// judge guardrail's frontmatter <c>runner:</c>, and a block named <c>overwatch</c>/<c>ai-triage</c> (the
/// Advisory reserved profiles), are LEGAL and must never trip GR2066 — "the entire point of v1", per the
/// plan, because both are how a human reaches a local judge. A bare <c>Assert.DoesNotContain</c> against
/// GR2066 would be vacuously green today (nothing fires yet) and would stay green under an implementation
/// that bans every <c>openai-compat</c> block outright — the "blunt ban" this task exists to prevent. So
/// each negative test also declares a SEPARATE reserved-Action-profile block that SHOULD fire, and asserts
/// <c>Assert.Single</c> against the whole GR2066 collection: an implementation that never fires fails
/// (zero matches), one that fires on the legal sibling too fails (two matches), and only the correct one
/// passes (exactly the discriminator).</para>
///
/// <para><b>The pin fixture is the on-disk shape production code already reads.</b>
/// <see cref="GuardrailDefinition"/> carries no <c>Runner</c> field — a judge's frontmatter <c>runner:</c>
/// pin is re-read at run time by re-parsing the guardrail's own <c>.prompt.md</c> file, and
/// <c>PlanValidator.PinnedRunnerNames</c> already does exactly that for GR2067. So the pinned fixture here
/// writes a REAL prompt guardrail with a REAL YAML frontmatter <c>runner:</c> block, matching that
/// existing re-parse path.</para>
/// </summary>
public sealed class ActionReachabilityGateTests : IDisposable
{
    private const string Gr2066 = "GR2066";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gr-action-reach-" + Guid.NewGuid().ToString("N"));

    public ActionReachabilityGateTests() => Directory.CreateDirectory(_root);

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

    // --- Route 1: the block declares `routing` -----------------------------------------------------

    /// <summary>
    /// An <c>openai-compat</c> block that declares <c>routing</c> would become a tier candidate for
    /// actors — reachable for an Action by the tier resolver, not merely by a human's explicit pin.
    /// </summary>
    [Fact]
    public void DeclaresRouting_IsGr2066Error()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "claude",
                "claude": { "command": "claude" },
                "local-qwen": {
                  "kind": "openai-compat",
                  "endpoint": "http://127.0.0.1:11434/v1",
                  "model": "qwen3-coder:30b",
                  "contextTokens": 32768,
                  "strength": 2,
                  "routing": { "tiers": ["medium"] }
                }
              }
            }
            """);

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == Gr2066);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("local-qwen", error.Message, StringComparison.Ordinal);
    }

    // --- Route 2: the effective default (default pointer, or the sole declared runner) -------------

    /// <summary>
    /// Route 2, half A: the <c>default</c> pointer names an <c>openai-compat</c> block. It is the
    /// effective default even though a second runner (<c>claude</c>) is also declared.
    /// </summary>
    [Fact]
    public void EffectiveDefault_DefaultPointerNamesIt_IsGr2066Error()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "local-qwen",
                "claude": { "command": "claude" },
                "local-qwen": {
                  "kind": "openai-compat",
                  "endpoint": "http://127.0.0.1:11434/v1",
                  "model": "qwen3-coder:30b",
                  "contextTokens": 32768,
                  "strength": 2
                }
              }
            }
            """);

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == Gr2066);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("local-qwen", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Route 2, half B: NO <c>default</c> pointer names anything — the <c>openai-compat</c> block is the
    /// SOLE declared runner, which <c>PromptRunnerRegistry.ResolveDefault</c> treats exactly like an
    /// explicit pointer. This is the most natural misconfiguration there is: a plan with a single local
    /// runner and nothing else.
    /// </summary>
    [Fact]
    public void EffectiveDefault_SoleDeclaredRunner_IsGr2066Error()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "local-qwen": {
                  "kind": "openai-compat",
                  "endpoint": "http://127.0.0.1:11434/v1",
                  "model": "qwen3-coder:30b",
                  "contextTokens": 32768,
                  "strength": 2
                }
              }
            }
            """);

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == Gr2066);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("local-qwen", error.Message, StringComparison.Ordinal);
    }

    // --- Route 3: a task's action.runner names it --------------------------------------------------

    /// <summary>A task's <c>task.json action.runner</c> naming an <c>openai-compat</c> block pins it for that task's Action.</summary>
    [Fact]
    public void TaskActionRunner_NamesIt_IsGr2066Error()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "claude",
                "claude": { "command": "claude" },
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
            actionRunner: "local-qwen");

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == Gr2066);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("local-qwen", error.Message, StringComparison.Ordinal);
    }

    // --- Route 4: an action prompt's YAML frontmatter `runner:` names it -----------------------------

    /// <summary>
    /// An action PROMPT's own YAML frontmatter <c>runner:</c> names an <c>openai-compat</c> block, with
    /// no <c>task.json action.runner</c> declared at all — <c>ActionRunner.cs</c>'s resolution chain
    /// (<c>route?.Runner?.Name ?? task.Action.Runner ?? promptFile.Frontmatter.Runner</c>) already
    /// reaches this at RUN time, but §3.7 records that <c>PlanLoader</c> folds nothing from an action
    /// prompt's frontmatter onto the task definition, so <see cref="PlanValidator"/> has nothing to read
    /// until task 21's loader fold exists. This is the route the validator cannot see today.
    /// </summary>
    [Fact]
    public void ActionPromptFrontmatterRunner_NamesIt_IsGr2066Error()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "claude",
                "claude": { "command": "claude" },
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
            actionFrontmatterRunner: "local-qwen");

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == Gr2066);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("local-qwen", error.Message, StringComparison.Ordinal);
    }

    // --- Route 5: a reserved Action-role profile name (ai-merge or breakdown) -----------------------

    /// <summary>
    /// An <c>openai-compat</c> block declared under a reserved ACTION-role profile name —
    /// <c>SchedulerFactory</c> resolves <c>ai-merge</c> and <c>breakdown</c> by name and hands their
    /// runner straight to an Action-writing resolver (<c>AiMergeResolver</c> /
    /// <c>WaveBreakdownInvoker</c>), unlike <c>overwatch</c>/<c>ai-triage</c>, which are Advisory.
    /// </summary>
    [Theory]
    [InlineData("ai-merge")]
    [InlineData("breakdown")]
    public void ReservedActionRoleProfileName_IsGr2066Error(string profile)
    {
        Loaded loaded = Load($$"""
            {
              "version": 1,
              "promptRunners": {
                "default": "claude",
                "claude": { "command": "claude" },
                "{{profile}}": {
                  "kind": "openai-compat",
                  "endpoint": "http://127.0.0.1:11434/v1",
                  "model": "qwen3-coder:30b",
                  "contextTokens": 32768,
                  "strength": 2
                }
              }
            }
            """);

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == Gr2066);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains(profile, error.Message, StringComparison.Ordinal);
    }

    // --- Negative: a judge guardrail's frontmatter pin is LEGAL and must NOT fire -------------------

    /// <summary>
    /// An <c>openai-compat</c> block reachable ONLY by a judge guardrail's frontmatter <c>runner:</c>
    /// pin is legal (SSOT §9.6 rule 1: an explicit pin "bypasses selection entirely") and must never trip
    /// GR2066 — pinning a local judge is the flagship deliverable this whole plan exists to reach.
    /// Paired with a sibling <c>ai-merge</c> block (reachable by route 5) as the discriminator: without
    /// it, "no GR2066 ever fires" would pass this assertion today just as vacuously as "GR2066 fires on
    /// every openai-compat block" would after a blunt-ban implementation. <c>Assert.Single</c> against
    /// the whole GR2066 collection fails on either wrong shape and passes only when exactly the
    /// unpinned <c>ai-merge</c> block is flagged.
    /// </summary>
    [Fact]
    public void PinnedByJudgeGuardrailFrontmatter_DoesNotFireGr2066()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "claude",
                "claude": { "command": "claude" },
                "local-pinned": {
                  "kind": "openai-compat",
                  "endpoint": "http://127.0.0.1:11434/v1",
                  "model": "qwen3-coder:30b",
                  "contextTokens": 32768,
                  "strength": 2
                },
                "ai-merge": {
                  "kind": "openai-compat",
                  "endpoint": "http://127.0.0.1:11434/v1",
                  "model": "qwen3-coder:30b",
                  "contextTokens": 32768,
                  "strength": 2
                }
              }
            }
            """,
            pinnedRunner: "local-pinned");

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == Gr2066);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("ai-merge", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("local-pinned", error.Message, StringComparison.Ordinal);
    }

    // --- Negative: the Advisory reserved profiles are LEGAL and must NOT fire -----------------------

    /// <summary>
    /// Blocks named <c>overwatch</c> or <c>ai-triage</c> — the Advisory reserved profiles §3.3 names as
    /// v1's entire payload — are never reachable for an Action and must never trip GR2066. Paired with a
    /// sibling <c>breakdown</c> block (route 5) as the discriminator, on the same anti-vacuity reasoning
    /// as the pin negative above: an implementation that fires on nothing, or that fires on the two
    /// legal Advisory names as well, both fail <c>Assert.Single</c>.
    /// </summary>
    [Fact]
    public void ReservedAdvisoryProfileNames_DoNotFireGr2066()
    {
        Loaded loaded = Load("""
            {
              "version": 1,
              "promptRunners": {
                "default": "claude",
                "claude": { "command": "claude" },
                "overwatch": {
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
                "breakdown": {
                  "kind": "openai-compat",
                  "endpoint": "http://127.0.0.1:11434/v1",
                  "model": "qwen3-coder:30b",
                  "contextTokens": 32768,
                  "strength": 2
                }
              }
            }
            """);

        Diagnostic error = Assert.Single(loaded.Diagnostics, d => d.Code == Gr2066);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("breakdown", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("overwatch", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ai-triage", error.Message, StringComparison.Ordinal);
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
    private Loaded Load(
        string guardrailsJson,
        string? pinnedRunner = null,
        string? actionRunner = null,
        string? actionFrontmatterRunner = null,
        IExecutableProbe? probe = null)
    {
        PlanLoadResult result = new PlanLoader().Load(
            PlanWith(guardrailsJson, pinnedRunner, actionRunner, actionFrontmatterRunner));
        List<Diagnostic> diagnostics = [.. result.Diagnostics];

        if (result.Plan is not null)
        {
            diagnostics.AddRange(new PlanValidator(probe ?? FakeExecutableProbe.All).Validate(result.Plan));
        }

        return new Loaded(result.Plan, diagnostics);
    }

    /// <summary>
    /// A minimal one-task plan folder carrying the given config. <paramref name="pinnedRunner"/> adds a
    /// second, judge PROMPT guardrail whose frontmatter pins <c>runner:</c> to it (the on-disk shape SSOT
    /// §9.6 rule 1 already reads at run time). <paramref name="actionRunner"/> sets the task's own
    /// <c>action.runner</c> (route 3). <paramref name="actionFrontmatterRunner"/> gives the ACTION prompt
    /// itself a YAML frontmatter <c>runner:</c> pin (route 4), with no <c>task.json action.runner</c> set,
    /// so the two routes are never exercised by the same fixture.
    /// </summary>
    private string PlanWith(
        string guardrailsJson, string? pinnedRunner, string? actionRunner, string? actionFrontmatterRunner)
    {
        File.WriteAllText(Path.Combine(_root, "guardrails.json"), guardrailsJson);

        string taskDir = Path.Combine(_root, "tasks", "01-task");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        string taskJson = actionRunner is null
            ? """{ "description": "t", "dependsOn": [] }"""
            : $$"""{ "description": "t", "dependsOn": [], "action": { "runner": "{{actionRunner}}" } }""";
        File.WriteAllText(Path.Combine(taskDir, "task.json"), taskJson);

        string actionPrompt = actionFrontmatterRunner is null
            ? "Do the thing.\n"
            : "---\n" + $"runner: {actionFrontmatterRunner}\n" + "---\n" + "\n" + "Do the thing.\n";
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), actionPrompt);

        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "exit 0\n");

        if (pinnedRunner is not null)
        {
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "02-verdict.prompt.md"),
                "---\n" + $"runner: {pinnedRunner}\n" + "---\n" + "\n" + "Render a verdict.\n");
        }

        return _root;
    }
}
