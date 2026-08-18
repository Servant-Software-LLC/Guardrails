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
    /// Apply the RESOLVED ROUTE's model over the runner-config settings (issues #200/#201, DoR
    /// <c>docs/plans/17-model-tiering.md</c> §6.1 and §12.5 — "<c>--model</c>/effort flags are emitted
    /// from the RESOLVED route"). <paramref name="route"/> is the ONE resolution
    /// <see cref="TaskExecutor"/> ran immediately before this attempt launched, so the string that
    /// reaches the CLI and the string <c>run.json</c> records come from the same object rather than from
    /// two derivations that agree only by construction.
    ///
    /// <para><b>A pinned or tier-resolved route decides OUTRIGHT — null included.</b> §6.1 folds the
    /// task's own <c>action.model</c> pin into the resolution's own precedence, so a pinned task still
    /// gets its model here; and a resolved block that names NO model means "pass no <c>--model</c>, let
    /// the runner CLI pick", which must not silently fall back to the model of whichever block
    /// <paramref name="settings"/> came from. That fallback is exactly the drift this seam removes —
    /// provenance would record the sentinel while the invocation carried a real model.</para>
    ///
    /// <para><b>The LEGACY route applies no override at all</b> (Invariant 7). It IS today's two-level
    /// fallback — <c>promptRunners.&lt;name&gt;.model</c> else the CLI's own default — and
    /// <paramref name="settings"/> already carries that answer, including for the prompt-frontmatter
    /// <c>runner:</c> selection an <c>ActionDefinition</c>-only resolution cannot see. So an untagged
    /// task in a routing-enabled config runs byte-identically to before tiering existed.</para>
    ///
    /// <para>A null <paramref name="route"/> means nothing was resolved — a SCRIPT action, which never
    /// reaches the prompt path — and likewise leaves the settings untouched.</para>
    /// </summary>
    public static PromptRunnerSettings ApplyModelOverride(PromptRunnerSettings settings, TierResolution? route) =>
        route is null || route.Legacy ? settings : settings with { Model = route.Model };

    /// <summary>
    /// The DISPLAY-ONLY stand-in recorded when nothing configured a model at all, so the runner CLI
    /// picks its own (issue #200). NEVER passed as a real <c>--model</c> value — it exists so per-attempt
    /// provenance is not a silent gap for a prompt task.
    /// </summary>
    public const string CliDefaultModelDisplay = "(cli default)";

    /// <summary>
    /// The provenance form of an ALREADY-RESOLVED route's model (issues #198/#200/#201): the resolved
    /// string when the route names one, else <see cref="CliDefaultModelDisplay"/>.
    ///
    /// <para><b>This is all that is left of the shipped two-level fallback.</b> Its precedence —
    /// <c>action.model</c> &gt; the runner block's <c>model</c> &gt; the sentinel — now lives inside
    /// <see cref="TierResolver.Resolve"/>'s own §6.1 branches (D30 makes the legacy branch exactly that
    /// fallback), so re-spelling the first two levels here would be the second derivation this wave
    /// exists to delete. Only the sentinel is a DISPLAY concern, and only it stays here.</para>
    /// </summary>
    public static string ResolvedModelForDisplay(string? resolvedModel) =>
        resolvedModel ?? CliDefaultModelDisplay;
}
