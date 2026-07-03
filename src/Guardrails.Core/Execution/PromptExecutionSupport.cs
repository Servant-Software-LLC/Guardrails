using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Execution;

/// <summary>
/// Shared prompt-execution helpers used by both the prompt ACTION path
/// (<see cref="ActionRunner"/>) and the prompt GUARDRAIL path (<see cref="GuardrailRunner"/>):
/// resolving the runner registry, loading/parsing a <c>*.prompt.md</c> file, and applying a
/// task/frontmatter <c>maxTurns</c> override over the runner-config settings. Extracted so the
/// two prompt callers do not each carry their own copy.
/// </summary>
internal sealed class PromptExecutionSupport
{
    private readonly PromptRunnerRegistry? _promptRunners;

    public PromptExecutionSupport(PromptRunnerRegistry? promptRunners) => _promptRunners = promptRunners;

    public PromptRunnerRegistry RequireRegistry() =>
        _promptRunners ?? throw new InvalidOperationException(
            "This plan has prompt actions/guardrails but no prompt-runner registry was provided to the executor.");

    /// <summary>
    /// Load and parse a <c>*.prompt.md</c> file. Loading-time validation (GR10xx) should have
    /// caught malformed frontmatter, but if parsing fails here we fall back to the raw text as
    /// the body so the run surfaces a real prompt result rather than crashing.
    /// </summary>
    public static PromptFile LoadPromptFile(string path)
    {
        string content = File.ReadAllText(path);
        PromptParseResult parsed = PromptFileParser.Parse(content);
        return parsed.File ?? new PromptFile { Frontmatter = PromptFrontmatter.Empty, Body = content };
    }

    /// <summary>Apply a task/frontmatter <c>maxTurns</c> override over the runner-config settings.</summary>
    public static PromptRunnerSettings ApplyPromptOverrides(PromptRunnerSettings settings, int? maxTurns) =>
        maxTurns is { } turns ? settings with { MaxTurns = turns } : settings;

    /// <summary>
    /// Apply a task-level <c>action.model</c> override over the runner-config settings (issue #200).
    /// Precedence: a non-null <paramref name="modelOverride"/> wins outright; otherwise
    /// <paramref name="settings"/>'s own <c>Model</c> (already the runner/guardrailOverrides-resolved
    /// value) passes through unchanged — which is itself null when nothing configures a model, so the
    /// runner falls through to the CLI's own default. Mirrors <see cref="ApplyPromptOverrides"/>'s
    /// <c>maxTurns</c> shape exactly; kept as a separate method (rather than folded into it) because
    /// only <see cref="ActionRunner"/> applies a model override — <see cref="GuardrailRunner"/> has no
    /// task.json-level guardrail equivalent to apply.
    /// </summary>
    public static PromptRunnerSettings ApplyModelOverride(PromptRunnerSettings settings, string? modelOverride) =>
        modelOverride is { } model ? settings with { Model = model } : settings;

    /// <summary>
    /// The ONE precedence resolver for "what model does/did this prompt task run on" (issue #200,
    /// fixing the #198 provenance gap): task.json <c>action.model</c> (if set) &gt; the runner
    /// config's own <paramref name="runnerModel"/> (if set, itself already
    /// <c>guardrailOverrides</c>-resolved by the caller when relevant) &gt; the sentinel
    /// <c>"(cli default)"</c> — DISPLAY-ONLY, never passed as a real <c>--model</c> value — when
    /// neither is set, so provenance is never a silent gap for a prompt task. Shared by
    /// <see cref="ActionRunner"/> (via <see cref="ApplyModelOverride"/>, which needs the real
    /// resolved-or-null value to decide whether to pass <c>--model</c> at all) and
    /// <see cref="TaskExecutor.ResolveModel"/> (which needs the same precedence for provenance
    /// display) so the two can never drift apart.
    /// </summary>
    public static string ResolveModelForDisplay(string? taskModelOverride, string? runnerModel) =>
        taskModelOverride ?? runnerModel ?? "(cli default)";
}
