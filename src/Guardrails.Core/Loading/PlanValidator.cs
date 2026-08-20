using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Guardrails.Core.Execution;
using Guardrails.Core.Graph;
using Guardrails.Core.Model;

namespace Guardrails.Core.Loading;

/// <summary>
/// Semantic validation of a loaded <see cref="PlanDefinition"/> (SSOT §3–§5): DAG
/// reference integrity, the at-least-one-guardrail rule, prompt-runner name
/// references, and interpreter resolvability for every script extension the plan
/// actually uses. Structural/parse problems are caught earlier in <see cref="PlanLoader"/>.
/// </summary>
public sealed class PlanValidator
{
    private readonly IExecutableProbe _probe;
    private readonly BannedPatternRegistry _bannedPatterns;
    private readonly IScriptSyntaxProbe _syntaxProbe;

    /// <summary>Validate with the given PATH probe and the embedded default banned-pattern registry.</summary>
    public PlanValidator(IExecutableProbe probe) : this(probe, BannedPatternRegistry.Load()) { }

    /// <summary>Validate with the real PATH probe and the embedded default banned-pattern registry.</summary>
    public PlanValidator() : this(new PathExecutableProbe()) { }

    /// <summary>
    /// Validate with an injected PATH probe AND an injected banned-pattern registry (SSOT §4.6,
    /// issue #346). Mirrors the <see cref="IExecutableProbe"/> injection so the GR2037 scan is
    /// unit-testable with a synthetic registry, without touching the shipped one.
    /// </summary>
    public PlanValidator(IExecutableProbe probe, BannedPatternRegistry bannedPatterns)
        : this(probe, bannedPatterns, new InterpreterScriptSyntaxProbe(probe)) { }

    /// <summary>
    /// Validate with an injected script-syntax probe as well (issue #473). Separate from the PATH
    /// probe because parse-checking spawns an interpreter: tests inject a fake so the GR2056 check is
    /// exercised without pwsh/bash present, and a caller that must not spawn anything can pass
    /// <see cref="NullScriptSyntaxProbe"/>.
    /// </summary>
    public PlanValidator(IExecutableProbe probe, BannedPatternRegistry bannedPatterns, IScriptSyntaxProbe syntaxProbe)
    {
        _probe = probe;
        _bannedPatterns = bannedPatterns;
        _syntaxProbe = syntaxProbe;
    }

    /// <summary>Run every semantic check and return all diagnostics (errors and warnings).</summary>
    public IReadOnlyList<Diagnostic> Validate(PlanDefinition plan)
    {
        var diagnostics = new List<Diagnostic>();

        ValidateWorkspaceIsGitRoot(plan, diagnostics);
        ValidateMaxPathRisk(plan, diagnostics);
        ValidateTaskIdsUnique(plan, diagnostics);
        ValidateStableIdsUnique(plan, diagnostics);
        ValidateStableIdFormat(plan, diagnostics);
        ValidateCostCap(plan, diagnostics);
        ValidateDependencies(plan, diagnostics);
        ValidateNoCycles(plan, diagnostics);
        ValidateCrossTaskStateReferences(plan, diagnostics);
        ValidateStaleCoverageTokens(plan, diagnostics);
        ValidateGuardrailsPresent(plan, diagnostics);
        ValidateNoLegacyIntegrationGate(plan, diagnostics);
        ValidatePlanGuardrailsIntegrationReRun(plan, diagnostics);
        ValidateGuardrailScopeValues(plan, diagnostics);
        ValidateInertWaveIntegrationScope(plan, diagnostics);
        ValidateGuardrailExpectedDurations(plan, diagnostics);
        ValidateDuplicateCheckNames(plan, diagnostics);
        ValidateBannedGuardrailPatterns(plan, diagnostics);
        ValidateUnsatisfiableGuardrailFloor(plan, diagnostics);
        ValidateGuardrailRequiresForbiddenToken(plan, diagnostics);
        ValidateGuardrailScriptsParse(plan, diagnostics);
        ValidateWriteScopes(plan, diagnostics);
        ValidateStructuralOverScope(plan, diagnostics);
        ValidateStagingOutputs(plan, diagnostics);
        ValidatePromptRunners(plan, diagnostics);
        ValidatePromptRunnerCommands(plan, diagnostics);
        ValidatePromptRunnerOutputCaps(plan, diagnostics);
        ValidatePromptRunnerAxes(plan, diagnostics);
        ValidatePromptRunnerKindsImplemented(plan, diagnostics);
        ValidateModelValues(plan, diagnostics);
        ValidateEffortValues(plan, diagnostics);
        ValidateTierValues(plan, diagnostics);
        ValidateTieringInert(plan, diagnostics);
        ValidateTierServability(plan, diagnostics);
        ValidateAutonomy(plan, diagnostics);
        ValidateInterpreters(plan, diagnostics);
        ValidateIntendedWaves(plan, diagnostics);
        ValidateWaveBreakdownIntent(plan, diagnostics);

        return diagnostics;
    }

    /// <summary>
    /// The review-marker nudge (GR2025, WARNING — SSOT §13, issue #79): missing/stale review marker.
    /// Deliberately NOT part of <see cref="Validate"/> (which is the pure semantic plan validator —
    /// keeping it out keeps every plan that lacks a marker from being noisy in the harness's own
    /// validation, and keeps the check a deliberate command-layer concern). The <c>validate</c> and
    /// <c>run</c> CLI commands call THIS to surface the same warning; both reuse the one deterministic
    /// <see cref="Review.ReviewMarker.EvaluateAll"/> computation — which is also what makes the nudge
    /// per-wave on a waved plan (issues #472/#488) on BOTH surfaces at once, rather than in one of them.
    ///
    /// <para><paramref name="surface"/> is REQUIRED (no default) on purpose: issue #410 was exactly a
    /// caller silently inheriting the other command's remediation, printing a <c>--skip-review-check</c>
    /// suggestion that <c>validate</c> rejects. Making every call site name its surface means a new
    /// caller cannot re-introduce that by omission.</para>
    /// </summary>
    /// <param name="plan">The plan whose review marker(s) are evaluated.</param>
    /// <param name="surface">The command emitting the nudge; selects the remediation clause.</param>
    /// <returns>
    /// One diagnostic per unattested ATTESTATION TARGET — a flat plan has at most one (today's behaviour
    /// exactly); a WAVED plan has one per authored, unattested wave and NO plan-level line
    /// (<see cref="Review.ReviewMarker.EvaluateAll"/>, issues #472/#488). Empty when everything in scope is
    /// freshly reviewed. Each diagnostic is located at the folder whose marker is missing/stale, so the
    /// operator is pointed at the exact wave.
    /// </returns>
    public static IReadOnlyList<Diagnostic> ReviewMarkerDiagnostics(
        PlanDefinition plan, Review.ReviewNudgeSurface surface)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (Review.ReviewEvaluation evaluation in Review.ReviewMarker.EvaluateAll(plan))
        {
            if (evaluation.ShouldWarn && evaluation.NudgeMessage(surface) is { } message)
            {
                diagnostics.Add(Warning(
                    DiagnosticCodes.ReviewMarkerMissingOrStale, LocationOf(plan, evaluation), message));
            }
        }

        return diagnostics;
    }

    /// <summary>The folder a review nudge points at: the wave folder for a wave-scoped one, else the plan root.</summary>
    private static string LocationOf(PlanDefinition plan, Review.ReviewEvaluation evaluation) =>
        evaluation.WaveDir is { } waveDir
            ? plan.Waves.FirstOrDefault(w => string.Equals(w.Dir, waveDir, StringComparison.Ordinal))?.Directory
              ?? plan.PlanDirectory
            : plan.PlanDirectory;

    private static bool HasAnyPrompt(PlanDefinition plan) =>
        plan.Tasks.Any(t =>
            t.Action.Kind == ActionKind.Prompt ||
            t.Guardrails.Any(g => g.Kind == ActionKind.Prompt) ||
            t.Preflights.Any(g => g.Kind == ActionKind.Prompt)) ||
        plan.PlanPreflights.Any(g => g.Kind == ActionKind.Prompt) ||
        plan.PlanGuardrails.Any(g => g.Kind == ActionKind.Prompt);

    private static void ValidateNoCycles(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        if (new Graph.DependencyGraph(plan.Tasks).FindCycle() is { } cycle)
        {
            diagnostics.Add(Error(DiagnosticCodes.DependencyCycle, plan.PlanDirectory,
                $"Dependency cycle: {string.Join(" -> ", cycle)}."));
        }
    }

    private static void ValidateTaskIdsUnique(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (TaskNode task in plan.Tasks)
        {
            if (!seen.Add(task.Id))
            {
                diagnostics.Add(Error(DiagnosticCodes.DuplicateTaskId, task.Directory,
                    $"Duplicate task id '{task.Id}'."));
            }
        }
    }

    /// <summary>
    /// A declared <c>stableId</c> must be unique across tasks (SSOT §3/§11). The regeneration
    /// merge keys task identity on <c>stableId</c>, so two tasks sharing one would be
    /// indistinguishable to it — a duplicate is almost always a copy-paste slip. Tasks without a
    /// stableId are skipped (it is optional; absent ⇒ identity falls back to the folder name).
    /// </summary>
    private static void ValidateStableIdsUnique(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (TaskNode task in plan.Tasks)
        {
            if (task.StableId is { } stableId && !seen.Add(stableId))
            {
                diagnostics.Add(Error(DiagnosticCodes.DuplicateStableId, task.Directory,
                    $"Task '{task.Id}' declares stableId '{stableId}', which is already used by another task."));
            }
        }
    }

    /// <summary>
    /// A declared <c>stableId</c> must match <c>^[a-z0-9][a-z0-9._-]*$</c> (SSOT §3/§11): lowercase
    /// alphanumerics, optionally with <c>. _ -</c>, starting alphanumeric. This reserves the format
    /// so a real stableId can never collide with the merge's synthetic <c>folder:&lt;name&gt;</c>
    /// identity (a colon is disallowed), and keeps ids stable across path/JSON handling. Tasks
    /// without a stableId are skipped (it is optional).
    /// </summary>
    private static void ValidateStableIdFormat(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (TaskNode task in plan.Tasks)
        {
            if (task.StableId is { } stableId && !StableIdPattern.IsMatch(stableId))
            {
                diagnostics.Add(Error(DiagnosticCodes.InvalidStableId, task.Directory,
                    $"Task '{task.Id}' declares stableId '{stableId}', which is not in the allowed format " +
                    "'^[a-z0-9][a-z0-9._-]*$' (lowercase alphanumerics, optionally with '.', '_' or '-')."));
            }
        }
    }

    private static readonly Regex StableIdPattern =
        new("^[a-z0-9][a-z0-9._-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The per-run cost cap (<c>maxCostUsd</c>, SSOT §2) must be positive when present. A zero or
    /// negative cap would trip before any work could run — almost always a configuration mistake —
    /// so it is an ERROR (GR2012). An absent cap is the no-cap default and is fine.
    /// </summary>
    private static void ValidateCostCap(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        if (plan.Config.MaxCostUsd is { } cap && cap <= 0m)
        {
            diagnostics.Add(Error(DiagnosticCodes.CostCapNonPositive, plan.PlanDirectory,
                $"maxCostUsd is {cap}, but a cost cap must be positive; a zero or negative cap would " +
                "halt the run before any work could run."));
        }
    }

    /// <summary>
    /// A prompt runner's <c>maxOutputTokens</c> (and its <c>guardrailOverrides.maxOutputTokens</c>)
    /// must be positive (SSOT §2/§9, issue #114). The value caps the runner's per-response output
    /// budget; a non-positive cap would make every prompt response fail, so it is an ERROR (GR2023).
    /// An absent value is the harness default and is fine.
    /// </summary>
    private static void ValidatePromptRunnerOutputCaps(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (PromptRunnerConfig runner in plan.Config.PromptRunners.Values)
        {
            if (runner.Settings.MaxOutputTokens <= 0)
            {
                diagnostics.Add(Error(DiagnosticCodes.MaxOutputTokensNonPositive, plan.PlanDirectory,
                    $"promptRunners.{runner.Name}.maxOutputTokens is {runner.Settings.MaxOutputTokens}, " +
                    "but it must be a positive integer."));
            }

            // The guardrail profile (base + guardrailOverrides) is checked too: an override could drive
            // the effective cap non-positive even when the base is fine.
            if (runner.GuardrailOverrides is not null &&
                runner.EffectiveSettings(isGuardrail: true).MaxOutputTokens <= 0)
            {
                diagnostics.Add(Error(DiagnosticCodes.MaxOutputTokensNonPositive, plan.PlanDirectory,
                    $"promptRunners.{runner.Name}.guardrailOverrides.maxOutputTokens resolves to a " +
                    "non-positive value, but it must be a positive integer."));
            }
        }
    }

    /// <summary>
    /// A present <c>strength</c> axis must be at least 1 (SSOT §9, issue #224 / charter Decision 7).
    /// <c>strength</c> is relative capability, higher = stronger, and it is the ORDERING key for tier
    /// candidates (ascending — the weakest model that can serve the tier goes first), so a zero or negative
    /// value has no meaning to order by and is always an authoring mistake — an ERROR (GR2045), mirroring
    /// the other optional-positive checks (cf. GR2012 <c>maxCostUsd</c>, GR2023 <c>maxOutputTokens</c>,
    /// GR2036 <c>expectedDurationSeconds</c>). An absent axis is "not stated" and is never flagged. The
    /// axes' TYPE checks are the loader's (<c>PlanLoader.ReadCostly</c>/<c>ReadStrength</c>/
    /// <c>ReadSpecialization</c>), which is the only place holding the raw JSON; both halves report GR2045.
    /// </summary>
    private static void ValidatePromptRunnerAxes(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (PromptRunnerConfig runner in plan.Config.PromptRunners.Values)
        {
            if (runner.Strength is { } strength && strength < 1)
            {
                diagnostics.Add(Error(DiagnosticCodes.InvalidRunnerAxis, plan.PlanDirectory,
                    $"promptRunners.{runner.Name}.strength is {strength}, but it must be an integer of at " +
                    "least 1 (higher = stronger); candidates for a tier are ordered by ascending strength, " +
                    "so there is no meaningful zeroth or negative capability."));
            }
        }
    }

    /// <summary>
    /// A declared <c>kind</c> must be one this BUILD can actually serve (SSOT §9, issue #224 / DoR §4.2)
    /// — GR2044, the same code the loader uses for an UNRECOGNISED token, because they are one rule:
    /// <i>a kind that will not run must fail <c>guardrails validate</c>, never a run.</i>
    ///
    /// <para><b>This is the GATE; <c>PromptRunnerRegistry.FromConfig</c> is the BACKSTOP.</b> The registry
    /// still refuses an unimplemented kind at construction (it must — it cannot substitute Claude for a
    /// provider the config named), but reaching that throw means the run is already in flight, and
    /// everything knowable from the config alone belongs at validate time. The two are not alternatives:
    /// this one catches the config, and the backstop still covers a kind cast in past the loader.</para>
    ///
    /// <para><b>Why this half lives in the validator and the unrecognised half in the loader.</b> The
    /// loader holds the raw STRING, so only it can name a token that parses to nothing; "which kinds this
    /// build implements" is a semantic fact about the assembly, read off the parsed enum. Same split, same
    /// reason, as GR2045's type checks (loader) versus its range check (validator).</para>
    /// </summary>
    private static void ValidatePromptRunnerKindsImplemented(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (PromptRunnerConfig runner in plan.Config.PromptRunners.Values)
        {
            if (PromptRunnerKinds.IsImplemented(runner.Kind))
            {
                continue;
            }

            diagnostics.Add(Error(DiagnosticCodes.InvalidPromptRunnerKind, plan.PlanDirectory,
                $"promptRunners.{runner.Name}.kind is '{PromptRunnerKinds.Token(runner.Kind)}', which is a " +
                "recognised runner kind but has NO implementation in this build — this build can serve " +
                $"{PromptRunnerKinds.ImplementedTokenList} (concrete non-Claude runners are issue #223). " +
                "Point that promptRunners block at an implemented kind, or remove it and route the tasks " +
                "that used it to a runner this build can serve. The harness will NOT substitute a " +
                "different model for the one the config asked for, so this is an honest halt at validate " +
                "time rather than a surprise when the run starts (SSOT §9)."));
        }
    }

    /// <summary>
    /// A present <c>effort</c> must be a real-looking value (SSOT §2/§3/§9, issue #201): non-empty,
    /// non-whitespace, with no leading/trailing whitespace and no embedded whitespace/control characters.
    /// Checked at both sites <c>effort</c> can be declared — <c>promptRunners.&lt;name&gt;.effort</c> and a
    /// task's <c>task.json action.effort</c> (GR2050 ERROR). A <c>null</c>/absent value at either site is
    /// fine and is not flagged.
    ///
    /// <para><c>effort</c> is OPAQUE to the harness — the runner CLASS translates it into whatever its
    /// CLI/API exposes — so there is no enumerable set of legal tokens and this is deliberately the SAME
    /// cheap, zero-false-positive shape check <c>model</c> gets (GR2030), sharing its
    /// <see cref="IsValidOpaqueToken"/> predicate rather than growing a second, subtly different copy.</para>
    /// </summary>
    private static void ValidateEffortValues(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (PromptRunnerConfig runner in plan.Config.PromptRunners.Values)
        {
            if (runner.Effort is { } effort && !IsValidOpaqueToken(effort))
            {
                diagnostics.Add(Error(DiagnosticCodes.EffortInvalid, plan.PlanDirectory,
                    $"promptRunners.{runner.Name}.effort is empty, whitespace-only, or contains " +
                    "leading/trailing/embedded whitespace or control characters — it must be a real " +
                    "effort token the runner can translate (e.g. \"low\", \"xhigh\"), or be omitted " +
                    "entirely to leave the block's effort unstated."));
            }
        }

        foreach (TaskNode task in plan.Tasks)
        {
            if (task.Action.Kind != ActionKind.Prompt || task.Action.Effort is not { } taskEffort)
            {
                continue; // absent ⇒ no override, no check (script tasks have no effort at all).
            }

            if (!IsValidOpaqueToken(taskEffort))
            {
                diagnostics.Add(Error(DiagnosticCodes.EffortInvalid, task.Directory,
                    $"Task '{task.Id}' action.effort is empty, whitespace-only, or contains " +
                    "leading/trailing/embedded whitespace or control characters — it must be a real " +
                    "effort token the runner can translate (e.g. \"low\", \"xhigh\"), or be omitted " +
                    "entirely to inherit the resolved route's effort."));
            }
        }
    }

    /// <summary>
    /// A present <c>model</c> must be a real-looking value (SSOT §2/§3, issue #200): non-empty,
    /// non-whitespace, with no leading/trailing whitespace and no embedded whitespace/control
    /// characters — none of which any real Claude model identifier ever contains. There is no
    /// enumerable list of valid model names to check against, so this deliberately stays a cheap,
    /// zero-false-positive shape check rather than an allow-list. Checked at all three sites a
    /// <c>model</c> can be declared: <c>promptRunners.&lt;name&gt;.model</c>, its
    /// <c>guardrailOverrides.model</c>, and a task's <c>task.json action.model</c> (GR2030 ERROR). A
    /// <c>null</c>/absent value at any site is fine (no override) and is not flagged.
    /// </summary>
    private static void ValidateModelValues(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (PromptRunnerConfig runner in plan.Config.PromptRunners.Values)
        {
            if (runner.Settings.Model is { } baseModel && !IsValidOpaqueToken(baseModel))
            {
                diagnostics.Add(Error(DiagnosticCodes.ModelInvalid, plan.PlanDirectory,
                    $"promptRunners.{runner.Name}.model is empty, whitespace-only, or contains " +
                    "leading/trailing/embedded whitespace or control characters — it must be a real " +
                    "model identifier, or omitted entirely to use the CLI default."));
            }

            if (runner.GuardrailOverrides?.Model is { } overrideModel && !IsValidOpaqueToken(overrideModel))
            {
                diagnostics.Add(Error(DiagnosticCodes.ModelInvalid, plan.PlanDirectory,
                    $"promptRunners.{runner.Name}.guardrailOverrides.model is empty, whitespace-only, " +
                    "or contains leading/trailing/embedded whitespace or control characters — it must " +
                    "be a real model identifier, or omitted entirely to inherit the base model."));
            }
        }

        foreach (TaskNode task in plan.Tasks)
        {
            if (task.Action.Kind != ActionKind.Prompt || task.Action.Model is not { } taskModel)
            {
                continue; // absent ⇒ no override, no check (script tasks have no model at all).
            }

            if (!IsValidOpaqueToken(taskModel))
            {
                diagnostics.Add(Error(DiagnosticCodes.ModelInvalid, task.Directory,
                    $"Task '{task.Id}' action.model is empty, whitespace-only, or contains " +
                    "leading/trailing/embedded whitespace or control characters — it must be a real " +
                    "model identifier, or omitted entirely to inherit the runner's default model."));
            }
        }
    }

    /// <summary>
    /// True when <paramref name="token"/> is a plausible OPAQUE vendor token: non-null, non-empty,
    /// contains no leading/trailing whitespace (a <c>Trim()</c> round-trips it unchanged), and
    /// contains no whitespace or control character anywhere (no real model name or effort token is ever
    /// space-separated or carries a stray tab/newline). This is a shape check, not an allow-list —
    /// neither <c>model</c> (GR2030) nor <c>effort</c> (GR2050) has an enumerable set of valid values to
    /// compare against, and they share ONE predicate deliberately: two copies of "what a vendor token
    /// looks like" would drift, and a token accepted at one site and rejected at the other is the exact
    /// confusion this check exists to prevent.
    /// </summary>
    private static bool IsValidOpaqueToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        if (token.Trim().Length != token.Length)
        {
            return false; // leading/trailing whitespace
        }

        return !token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
    }

    /// <summary>
    /// A declared difficulty tier must be one of the three recognised tokens (SSOT §2/§3/§4.2, issue
    /// #225 / DoR §5), checked at ALL FOUR sites a tier can be declared (GR2043 ERROR):
    /// <list type="number">
    ///   <item>a task's <c>task.json action.tier</c>;</item>
    ///   <item>a PROMPT guardrail's frontmatter <c>tier</c> — the judge-guardrail site, across every
    ///     guardrail-shaped folder (task guardrails/preflights, wave guardrails/preflights, plan
    ///     guardrails/preflights), exactly like the <c>scope</c> check;</item>
    ///   <item>the plan-wide <c>tiering.defaultTier</c>;</item>
    ///   <item>the plan-wide <c>tiering.verifier.minTier</c> floor.</item>
    /// </list>
    /// Unlike <c>model</c>/<c>effort</c> (GR2030/GR2050, shape checks — there is no enumerable set of
    /// valid vendor tokens) the tier vocabulary is CLOSED, so this is a real membership test against
    /// <see cref="ActionTiers.All"/>, matched verbatim. An absent tier at any site is fine (untagged) and
    /// is not flagged.
    ///
    /// <para>Covering all four is the point of this check existing in one place: a tier token that reaches
    /// the resolver unrecognised is unroutable wherever it was written, and a site validated at one
    /// declaration point but not another is exactly how a typo survives into a run.</para>
    ///
    /// <para>The two plan-wide sites are checked FIRST and independently because they are the more
    /// dangerous ones: a typo in <c>defaultTier</c> would tier every untagged task in the plan. Each is
    /// reported exactly ONCE, at its own declaration site — the loader's <c>PropagatableDefaultTier</c>
    /// deliberately does not propagate an unrecognised default onto tasks, so a single typo can never fan
    /// out into one error per untagged task.</para>
    /// </summary>
    private static void ValidateTierValues(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        if (plan.Config.Tiering?.DefaultTier is { } defaultTier && !ActionTiers.IsRecognized(defaultTier))
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidTierValue, plan.PlanDirectory,
                $"tiering.defaultTier is '{defaultTier}', which is not a recognised difficulty tier. " +
                $"Expected exactly one of {ActionTiers.TokenList} (matched verbatim — no surrounding " +
                "whitespace), or omit the tiering block entirely to leave untagged tasks untagged. This " +
                "default applies to every task that declares no action.tier of its own, so a typo here " +
                "would mistier the whole plan."));
        }

        if (plan.Config.Tiering?.Verifier?.MinTier is { } minTier && !ActionTiers.IsRecognized(minTier))
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidTierValue, plan.PlanDirectory,
                $"tiering.verifier.minTier is '{minTier}', which is not a recognised difficulty tier. " +
                $"Expected exactly one of {ActionTiers.TokenList} (matched verbatim — no surrounding " +
                "whitespace), or omit the key to leave the judge's rung entirely to the resolution rule. " +
                "minTier is a FLOOR, not a default: it never selects a rung, it only refuses one that " +
                "came out below it."));
        }

        foreach (TaskNode task in plan.Tasks)
        {
            if (task.Action.Tier is { } tier && !ActionTiers.IsRecognized(tier))
            {
                diagnostics.Add(Error(DiagnosticCodes.InvalidTierValue, task.Directory,
                    $"Task '{task.Id}' declares action.tier '{tier}', which is not a recognised difficulty " +
                    $"tier. Expected exactly one of {ActionTiers.TokenList} (matched verbatim — no " +
                    "surrounding whitespace), or omit the field to inherit the plan-wide default."));
            }
        }

        foreach ((GuardrailDefinition guardrail, string context) in EveryCheck(plan))
        {
            if (guardrail.Tier is { } judgeTier && !ActionTiers.IsRecognized(judgeTier))
            {
                diagnostics.Add(Error(DiagnosticCodes.InvalidTierValue, guardrail.Path,
                    $"Guardrail '{guardrail.Name}' ({context}) declares frontmatter tier '{judgeTier}', " +
                    $"which is not a recognised difficulty tier. Expected exactly one of " +
                    $"{ActionTiers.TokenList}, or omit the key to let the judge's rung follow the actor's."));
            }
        }
    }

    /// <summary>
    /// Every guardrail-shaped check in the plan paired with a human-readable context, across ALL SIX
    /// folder families (SSOT §4/§14.3): each task's <c>guardrails/</c> + <c>preflights/</c>, each wave's,
    /// and the plan root's. Written once here because tier frontmatter — like <c>scope</c> and
    /// <c>expectedDurationSeconds</c> before it — is a guardrail-shaped-file concept, and a folder covered
    /// by one check but missed by another is how a rule silently stops applying to a whole layout.
    /// </summary>
    private static IEnumerable<(GuardrailDefinition Guardrail, string Context)> EveryCheck(PlanDefinition plan)
    {
        foreach (TaskNode task in plan.Tasks)
        {
            foreach (GuardrailDefinition g in task.Guardrails) yield return (g, $"task '{task.Id}'");
            foreach (GuardrailDefinition g in task.Preflights) yield return (g, $"task '{task.Id}' preflights");
        }

        foreach (WaveNode wave in plan.Waves)
        {
            foreach (GuardrailDefinition g in wave.Preflights) yield return (g, $"wave '{wave.Dir}' preflights/");
            foreach (GuardrailDefinition g in wave.Guardrails) yield return (g, $"wave '{wave.Dir}' guardrails/");
        }

        foreach (GuardrailDefinition g in plan.PlanPreflights) yield return (g, "<plan>/preflights/");
        foreach (GuardrailDefinition g in plan.PlanGuardrails) yield return (g, "<plan>/guardrails/");
    }

    /// <summary>
    /// Tiering is CONFIGURED for a plan iff at least ONE <c>promptRunners</c> block declares
    /// <c>routing</c> (SSOT §9.6, DoR §4.2). This predicate gates the two checks below in OPPOSITE
    /// directions — GR2049 fires only when it is false, GR2048 only when it is true — which is what keeps
    /// an unconfigured plan from producing one "unservable" error per tag.
    /// </summary>
    private static bool TieringIsConfigured(PlanDefinition plan) =>
        plan.Config.PromptRunners.Values.Any(r => r.Routing is not null);

    /// <summary>
    /// Every difficulty tier the plan actually USES (DoR §6.2), de-duplicated and each paired with the
    /// sites that use it: a task's <c>action.tier</c>, a judge guardrail's frontmatter <c>tier</c>, and
    /// the plan-wide <c>tiering.defaultTier</c>. Only RECOGNISED tokens are collected — an unrecognised
    /// one is already GR2043's to report, and reporting it a second time as "unservable" would be two
    /// errors for one typo with only one of them actionable.
    ///
    /// <para><c>tiering.verifier.minTier</c> is deliberately NOT a "used" tier. It is a floor on the
    /// judge's rung, and an unsatisfiable verifier floor DEGRADES to an advisory rather than halting
    /// (DoR §6.5.1) — the opposite disposition from an actor tier. Counting it here would turn an advisory
    /// condition into a build-failing error, which §12.6 forbids by name.</para>
    /// </summary>
    private static IReadOnlyDictionary<string, List<string>> UsedTiers(PlanDefinition plan)
    {
        var used = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void Record(string? tier, string site)
        {
            if (!ActionTiers.IsRecognized(tier))
            {
                return;
            }

            if (!used.TryGetValue(tier!, out List<string>? sites))
            {
                used[tier!] = sites = [];
            }

            sites.Add(site);
        }

        Record(plan.Config.Tiering?.DefaultTier, "tiering.defaultTier");

        foreach (TaskNode task in plan.Tasks)
        {
            // A task's Tier is already the RESOLVED value (action.tier ?? defaultTier), so a plan whose
            // tasks all inherit the default still reports the default's own site above, not N copies.
            if (task.Action.Tier is { } tier && tier != plan.Config.Tiering?.DefaultTier)
            {
                Record(tier, $"task '{task.Id}'");
            }
        }

        foreach ((GuardrailDefinition guardrail, string context) in EveryCheck(plan))
        {
            Record(guardrail.Tier, $"guardrail '{guardrail.Name}' ({context})");
        }

        return used;
    }

    /// <summary>
    /// The plan carries tier tags but NO block declares <c>routing</c>, so tiering is not CONFIGURED and
    /// every tag is inert — GR2049 WARNING (SSOT §2/§9.6, DoR §4.2). The plan runs exactly as it does
    /// today, by legacy resolution (the runner's own model, else the CLI default).
    ///
    /// <para>A warning rather than an error because the plan is entirely runnable and its behaviour is
    /// unchanged — failing it would break the legitimate order of tagging before registering providers.
    /// Not silence either: the author who wrote <c>"tier": "easy"</c> believes they are routing, and the
    /// gap between that belief and "it ran on the frontier model anyway" is precisely the quiet no-op this
    /// repo refuses to ship. Emitted ONCE per plan, at the config — one message about one config-level
    /// fact, not one per tagged task.</para>
    /// </summary>
    private static void ValidateTieringInert(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        if (TieringIsConfigured(plan))
        {
            return;
        }

        IReadOnlyDictionary<string, List<string>> used = UsedTiers(plan);
        if (used.Count == 0)
        {
            return; // No tags at all — the single-model path, and the whole feature stays invisible.
        }

        string tiers = string.Join(", ", ActionTiers.All.Where(used.ContainsKey).Select(t => $"'{t}'"));
        diagnostics.Add(Warning(DiagnosticCodes.TieringInert, plan.PlanDirectory,
            $"This plan declares difficulty tiers ({tiers}) but NO promptRunners block declares a " +
            "'routing' key, so tiering is not configured and the tags have NO effect: every prompt " +
            "action resolves by legacy resolution (the runner's own model, else the CLI default), " +
            "exactly as it would with no tiers at all. Add \"routing\": { \"tiers\": [...] } to at least " +
            "one runner block to make the tags route, or remove the tags to say plainly that this plan " +
            "does not tier (SSOT §9.6)."));
    }

    /// <summary>
    /// In a TIERING-CONFIGURED plan, every USED tier must have a CANDIDATE block at or above it —
    /// GR2048 ERROR (SSOT §9.6, DoR §6.2/§14.1, settled OD-G). Candidacy is
    /// <see cref="PromptRunnerConfig.ServesTier"/>: <c>routing</c> present ∧ rung ∈ <c>routing.tiers</c> ∧
    /// <c>costly</c> is not <c>true</c>. "At or above" is the never-route-DOWN floor made checkable —
    /// resolution may climb to a stronger rung when a rung's own candidate set is empty, so only a tier
    /// with nothing at or above it is genuinely unservable.
    ///
    /// <para><b>The message distinguishes the two causes, because they have different fixes.</b> Either
    /// (a) no block declares the rung at all — register one or widen a block's <c>routing.tiers</c>; or
    /// (b) blocks DO declare it and every one is <c>costly: true</c>, which the harness may never
    /// auto-select — pin the task, clear the flag, or add the rung to a non-costly block. Collapsing them
    /// into one "no block serves tier X" would send a user hunting for a block that is sitting right there
    /// in their config.</para>
    ///
    /// <para>An ERROR, and it halts rather than degrading: an actor route is LOAD-BEARING, so shipping
    /// work from a model nobody vouched for at that difficulty is not an option, and neither is quietly
    /// reaching for the costly block (the floor has no override) nor dropping to a weaker rung (that
    /// routes weaker than asked). Reported ONCE per unservable TIER, naming the sites that use it, rather
    /// than once per task — one config gap is one problem.</para>
    /// </summary>
    private static void ValidateTierServability(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        if (!TieringIsConfigured(plan))
        {
            return; // Unconfigured ⇒ GR2049's territory; every tag is inert, not unservable.
        }

        PromptRunnerConfig[] runners = [.. plan.Config.PromptRunners.Values];
        IReadOnlyDictionary<string, List<string>> used = UsedTiers(plan);

        // ActionTiers.All is ordered ASCENDING by difficulty, so "at or above rung i" is the tail from i.
        for (int i = 0; i < ActionTiers.All.Count; i++)
        {
            string tier = ActionTiers.All[i];
            if (!used.TryGetValue(tier, out List<string>? sites))
            {
                continue;
            }

            string[] atOrAbove = [.. ActionTiers.All.Skip(i)];

            if (atOrAbove.Any(rung => runners.Any(r => r.ServesTier(rung))))
            {
                continue;
            }

            string[] costlyBlockers =
            [
                .. runners
                    .Where(r => r.Costly is true && atOrAbove.Any(r.DeclaresTier))
                    .Select(r => r.Name)
                    .Order(StringComparer.Ordinal)
            ];

            string cause = costlyBlockers.Length == 0
                ? $"NO promptRunners block declares tier '{tier}' (or any stronger tier) in its " +
                  "routing.tiers. Fix ONE of: add the tier to an existing block's routing.tiers, or " +
                  "register a block that serves it."
                : $"the only block(s) declaring tier '{tier}' or stronger — " +
                  $"{string.Join(", ", costlyBlockers.Select(n => $"promptRunners.{n}"))} — are marked " +
                  "\"costly\": true, and the harness NEVER auto-selects a costly model: not for its own " +
                  "rung, not for a stronger-rung climb, not for a judge bump. Fix ONE of: pin the work " +
                  "explicitly (\"action\": { \"runner\": \"" + costlyBlockers[0] + "\" }) — a costly " +
                  "model is reachable by YOUR assignment, just never by the harness's choice; clear " +
                  $"\"costly\": true on {costlyBlockers[0]}; or add tier '{tier}' to a non-costly " +
                  "block's routing.tiers.";

            diagnostics.Add(Error(DiagnosticCodes.UnservableTier, plan.PlanDirectory,
                $"Tier '{tier}' is used by {DescribeSites(sites)}, but no block can serve it: {cause} " +
                "The harness will not route weaker than asked, so this halts at validate time — before a " +
                "token is spent — rather than at runtime (SSOT §9.6)."));
        }
    }

    /// <summary>The sites using a tier, capped so one config gap cannot print a hundred task ids.</summary>
    private static string DescribeSites(IReadOnlyList<string> sites)
    {
        const int Shown = 3;
        return sites.Count <= Shown
            ? string.Join(", ", sites)
            : $"{string.Join(", ", sites.Take(Shown))} (+{sites.Count - Shown} more)";
    }

    /// <summary>
    /// Semantic validation of the OPTIONAL <c>autonomy</c> criticality-dial block (issue #361, doc 12
    /// §3.4/§3.5/§5.2; decided §10 M). Two ERROR codes:
    /// <list type="bullet">
    /// <item><b>GR2039</b> (<see cref="ValidateAutonomyDialValues"/>) — an unrecognised value: an
    ///   <c>escalationThreshold</c> that is not a criticality level, a <c>gateThresholds.needs-human</c> /
    ///   <c>wave-checkpoint</c> that is not a criticality level, or a <c>gateThresholds.review-gate</c> that
    ///   is neither <c>escalate</c> nor <c>proceed-unreviewed</c>.</item>
    /// <item><b>GR2040</b> (<see cref="ViolatesCompoundConfig"/>) — the forbidden compound config, keyed on
    ///   the reachable end-state (Finding 3).</item>
    /// </list>
    /// The whole block absent ⇒ <see cref="RunConfig.Autonomy"/> is <c>null</c> ⇒ nothing to validate (the
    /// dial is inert, doc 12 §3.2 back-compat). Mirrors GR2031 (<c>autonomyPolicy</c>) for the orthogonal
    /// unified-autonomy knob.
    /// </summary>
    private static void ValidateAutonomy(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        if (plan.Config.Autonomy is not { } autonomy)
        {
            return; // block absent ⇒ inert dial; nothing to validate.
        }

        ValidateAutonomyDialValues(plan, diagnostics);

        // GR2040 keyed on the reachable end-state via the reusable predicate (B1). The predicate is the
        // single source for the message so a later stage re-checking the EFFECTIVE config (task 07's
        // --dial/--autonomous mutation runs AFTER load-time validation) reports identically.
        if (ViolatesCompoundConfig(autonomy, out string compoundDiagnostic))
        {
            diagnostics.Add(Error(DiagnosticCodes.IncompatibleAutonomyCompoundConfig, plan.PlanDirectory,
                compoundDiagnostic));
        }
    }

    /// <summary>
    /// GR2039 (ERROR): flag any <c>autonomy</c> wire value that is not one of its recognised tokens. This
    /// must inspect the RAW values, because the parse (<see cref="PlanLoader.MapAutonomy"/>) silently falls an
    /// unrecognised value back to the dial/default — the parsed <see cref="AutonomyConfig"/> no longer carries
    /// the typo, so a value read from the model could never reveal it. The raw block is therefore re-read from
    /// <c>guardrails.json</c> through the loader's EXACT deserialization path (<see cref="RawRunConfig"/> +
    /// <see cref="PlanJson.Options"/>: case-insensitive, comment- and trailing-comma-tolerant), so the value
    /// checked here is byte-for-byte the value the loader would have bound. A missing/unreadable/malformed file
    /// is skipped — the loader would already have failed it (GR1001/GR1002) and the validator would not run.
    /// The recognised-token tests reuse the SSOT parsers (<see cref="EscalationThresholds.TryParse"/> /
    /// <see cref="ReviewGateDecisions.TryParse"/>) so the spelling never forks.
    /// </summary>
    private static void ValidateAutonomyDialValues(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        string configPath = Path.Combine(plan.PlanDirectory, "guardrails.json");
        string? text = TryReadAllText(configPath);
        if (text is null)
        {
            return;
        }

        RawAutonomyConfig? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawRunConfig>(text, PlanJson.Options)?.Autonomy;
        }
        catch (JsonException)
        {
            return; // malformed JSON is GR1002's concern; it would not have produced a model to validate here.
        }

        if (raw is null)
        {
            return; // the block re-read as absent — nothing to check.
        }

        // escalationThreshold — the run-wide dial: a criticality level (doc 12 §3.4).
        if (raw.EscalationThreshold is { } threshold && !EscalationThresholds.TryParse(threshold, out _))
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidAutonomyDialValue, configPath,
                $"autonomy.escalationThreshold '{threshold}' is not a recognised criticality level; expected " +
                "'low', 'moderate', 'high', or 'critical' (doc 12 §3.4)."));
        }

        if (raw.GateThresholds is not { } gates)
        {
            return;
        }

        // needs-human / wave-checkpoint are criticality levels; review-gate is the escalate/proceed-unreviewed
        // acknowledgment (a floor, NOT a criticality level — doc 12 §3.5). Gate keys are matched
        // case-insensitively, mirroring the loader (PlanLoader.TryGetGate).
        CheckCriticalityGateValue(gates, "needs-human", configPath, diagnostics);
        CheckCriticalityGateValue(gates, "wave-checkpoint", configPath, diagnostics);

        if (TryGetGateValue(gates, "review-gate", out string? reviewGate) && reviewGate is not null &&
            !ReviewGateDecisions.TryParse(reviewGate, out _))
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidAutonomyDialValue, configPath,
                $"autonomy.gateThresholds.review-gate '{reviewGate}' is not a recognised value; it is a floor, " +
                "not a criticality level — expected 'escalate' (default) or the named opt-in " +
                "'proceed-unreviewed' (doc 12 §3.5/§5.2)."));
        }
    }

    /// <summary>GR2039 helper: a <c>gateThresholds</c> criticality-level gate (needs-human / wave-checkpoint).</summary>
    private static void CheckCriticalityGateValue(
        Dictionary<string, string> gates, string key, string configPath, List<Diagnostic> diagnostics)
    {
        if (TryGetGateValue(gates, key, out string? value) && value is not null &&
            !EscalationThresholds.TryParse(value, out _))
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidAutonomyDialValue, configPath,
                $"autonomy.gateThresholds.{key} '{value}' is not a recognised criticality level; expected " +
                "'low', 'moderate', 'high', or 'critical' (doc 12 §3.5)."));
        }
    }

    /// <summary>
    /// Case-insensitive lookup into the raw gate map (the wire keys are kebab-case, e.g. <c>needs-human</c>),
    /// mirroring the loader's <c>PlanLoader.TryGetGate</c> so the value checked matches the value bound.
    /// </summary>
    private static bool TryGetGateValue(Dictionary<string, string> gates, string key, out string? value)
    {
        foreach (KeyValuePair<string, string> gate in gates)
        {
            if (string.Equals(gate.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = gate.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// The GR2040 core (B1 — load-bearing, reusable). Returns whether an ARBITRARY EFFECTIVE
    /// <see cref="AutonomyConfig"/> hits the forbidden compound configuration (issue #361, doc 12 §5.2/§3.4;
    /// decided §10 M/A) — <c>gateThresholds.review-gate == proceed-unreviewed</c> AND a reachable
    /// <c>critical</c> end-state (<c>escalationThreshold == critical</c> OR any in-wave <c>gateThresholds</c>
    /// criticality value — <c>needs-human</c> / <c>wave-checkpoint</c> — <c>== critical</c>) — and, when it
    /// does, the GR2040 <paramref name="diagnostic"/> string (else the empty string). Keyed on the REACHABLE
    /// END-STATE (Finding 3), so a per-gate override like
    /// <c>{ "needs-human": "critical", "review-gate": "proceed-unreviewed" }</c> under
    /// <c>escalationThreshold: high</c> cannot route around it. <c>proceed-unreviewed</c> stays a valid named
    /// opt-in at the cautious / <c>high</c> dials (no reachable <c>critical</c>) and returns <c>false</c> there.
    /// <para>
    /// Deliberately <c>public static</c> and taking the effective config (not a <see cref="PlanDefinition"/>):
    /// load-time validation (<see cref="ValidateAutonomy"/>) calls it — its load-time behaviour is unchanged —
    /// but so must a later stage AFTER <c>--dial</c>/<c>--autonomous</c> mutate the config post-load (task 07,
    /// which <c>dependsOn</c> this task and CALLS this predicate). GR2040 must be re-checkable on the effective
    /// config, not inline-only, so the core cannot live buried in the load-time walk.
    /// </para>
    /// </summary>
    public static bool ViolatesCompoundConfig(AutonomyConfig effective, out string diagnostic)
    {
        diagnostic = string.Empty;

        GateThresholds? gates = effective.GateThresholds;
        if (gates?.ReviewGate != ReviewGateDecision.ProceedUnreviewed)
        {
            return false; // review is not bypassed — the compound gate needs BOTH conjuncts.
        }

        bool reachableCritical =
            effective.EscalationThreshold == EscalationThreshold.Critical ||
            gates.NeedsHuman == EscalationThreshold.Critical ||
            gates.WaveCheckpoint == EscalationThreshold.Critical;
        if (!reachableCritical)
        {
            return false; // proceed-unreviewed is permitted at the cautious / high dials (doc 12 §5.1).
        }

        diagnostic =
            "autonomy declares the forbidden compound configuration (doc 12 §5.2, GR2040): " +
            "gateThresholds.review-gate is 'proceed-unreviewed' (review skipped) AND the reachable end-state " +
            "best-guesses a 'critical' hard call (escalationThreshold 'critical', or a per-gate " +
            "needs-human/wave-checkpoint 'critical'). Auto-best-guessing a critical hard call while ALSO " +
            "skipping review is 'Guardrails with no guardrails' and is refused at load time. Either lower the " +
            "reachable criticality below 'critical', or set review-gate to 'escalate'.";
        return true;
    }

    /// <summary>
    /// The plan workspace must reside within a git repository — but ONLY in worktree mode
    /// (<c>maxParallelism &gt; 1</c>), per the PO decision. Parallel tasks need per-segment worktree
    /// isolation, which requires a git repository (plan branch, segment worktrees) → GR2015 ERROR
    /// when the workspace is outside any git repo. A SERIAL run (<c>maxParallelism == 1</c>) uses the
    /// shared-workspace model: no worktrees, no concurrency, no isolation/corruption risk, so git is
    /// NOT required and GR2015 is not emitted. Skipped when the workspace directory does not yet exist
    /// (other structural errors are caught by the loader).
    /// </summary>
    private static void ValidateWorkspaceIsGitRoot(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        // git required only in worktree mode (maxParallelism>1), PO decision; serial runs use the
        // shared workspace.
        if (plan.Config.MaxParallelism <= 1)
        {
            return;
        }

        string workspace = plan.Workspace;
        if (!Directory.Exists(workspace))
        {
            return;
        }

        try
        {
            if (!IsInsideGitRepo(workspace))
            {
                diagnostics.Add(Error(DiagnosticCodes.WorkspaceNotGitRoot, workspace,
                    $"Workspace '{workspace}' is not a git repository and is not inside one. " +
                    "Worktree mode (maxParallelism > 1) requires a git repository to create per-run " +
                    "worktrees (plan branch, segment worktrees). Run 'git init' in the workspace, point " +
                    "it at a path inside an existing git repository, or set maxParallelism to 1 to run " +
                    "serially in the shared workspace (SSOT §1, plan 08 §1)."));
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Cannot probe the directory ancestry — skip GR2015 rather than a false positive.
        }
    }

    private static bool IsInsideGitRepo(string directory)
    {
        DirectoryInfo? dir = new DirectoryInfo(directory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return true;
            }

            dir = dir.Parent;
        }

        return false;
    }

    /// <summary>
    /// A configured <c>worktreeRoot</c> whose path length is large risks exceeding the Windows
    /// MAX_PATH limit of 260 characters when combined with harness-managed suffixes (segment
    /// worktrees, task subdirectories, guardrail files). Windows-only; POSIX has no 260-char limit.
    /// Emits GR2016 WARNING (not error — the plan may work if <c>core.longpaths</c> is enabled).
    /// </summary>
    private static void ValidateMaxPathRisk(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string? worktreeRoot = plan.Config.WorktreeRoot;
        if (worktreeRoot is null)
        {
            return;
        }

        // Harness-managed suffix: /<planName>/<segment>/tasks/<taskId>/guardrails/<file> — ~60+ chars.
        // A worktreeRoot longer than 200 chars puts typical paths at risk of exceeding MAX_PATH (260).
        if (worktreeRoot.Length > 200)
        {
            diagnostics.Add(Warning(DiagnosticCodes.MaxPathRisk, plan.PlanDirectory,
                $"worktreeRoot '{worktreeRoot}' is {worktreeRoot.Length} characters long; combined " +
                "with harness-managed suffixes (segment worktrees, task subdirs, guardrail files) " +
                "this risks exceeding the Windows MAX_PATH limit (260 chars). " +
                "Mitigate with: git config --system core.longpaths true (SSOT §2, plan 08 §1)."));
        }
    }

    /// <summary>
    /// The <c>integrationGate: true</c> task kind is RETIRED (SSOT §3.3, design-of-record
    /// 09-preflight-first-class) with NO coexistence window: the terminal whole-repo checks now live in
    /// the first-class plan-level <c>&lt;plan&gt;/guardrails/</c> folder
    /// (<see cref="ValidatePlanGuardrailsIntegrationReRun"/>), not on a no-op sink task. A plan that
    /// STILL declares the legacy key gets a HARD validation ERROR (GR2029) — honest-over-silent, so the
    /// stale declaration is caught at validate time rather than silently ignored. The
    /// <see cref="TaskNode.IntegrationGate"/> model property is kept solely to DETECT the legacy
    /// declaration here (and is still read by the scheduler's terminal-gate run until that path is
    /// replaced by the terminal phase in a later deliverable).
    /// </summary>
    private static void ValidateNoLegacyIntegrationGate(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (TaskNode task in plan.Tasks)
        {
            if (task.IntegrationGate)
            {
                diagnostics.Add(Error(DiagnosticCodes.RetiredIntegrationGateKey, task.Directory,
                    $"Task '{task.Id}' declares the retired 'integrationGate: true' task kind. The terminal " +
                    "integration gate is no longer a task kind: its whole-repo checks now live in the " +
                    "plan-level '<plan>/guardrails/' folder (SSOT §3.3). Remove 'integrationGate' from " +
                    "task.json and place the terminal checks — each re-running the integration set — in " +
                    "'<plan>/guardrails/'."));
            }
        }
    }

    /// <summary>
    /// The re-homed GR2018 content-teeth rule (SSOT §3.3, design-of-record 09-preflight-first-class,
    /// B3). A plan with a parallel topology — ≥2 leaf tasks (no dependents) or any fan-in task (≥2
    /// upstreams) — MUST carry, in its terminal <c>&lt;plan&gt;/guardrails/</c> folder, at least one
    /// deterministic check that ACTUALLY re-runs the integration set (a whole-repo build / full suite /
    /// a union invariant). This preserves GR2018's teeth: it is NOT weakened to "the folder is
    /// non-empty" — an empty folder fails, and so does a folder holding only a tautological
    /// <c>exit 0</c> file that certifies nothing (the precise failure GR2018 exists to prevent). The
    /// "counts toward the terminal gate" marker is folder membership (a folder-scoped equivalent of the
    /// §4.3 <c>scope:"integration"</c> tag, which is unchanged and still drives the per-union re-verify);
    /// the surviving obligation — ≥1 real integration-set re-run — is checked by content inspection
    /// (<see cref="ReRunsIntegrationSet"/>). A single linear chain (one leaf, no fan-in) forms no union
    /// and is exempt, and — matching the retired GR2017/GR2018's exact firing conditions — the rule
    /// applies ONLY in worktree mode (<c>maxParallelism &gt; 1</c>): a serial run uses the shared
    /// workspace and merges no parallel branches, so there is no merged-HEAD union for a terminal gate
    /// to certify. GR2028 ERROR.
    /// </summary>
    private static void ValidatePlanGuardrailsIntegrationReRun(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        // Terminal-gate obligation required only in worktree mode (maxParallelism>1) — the exact
        // condition the retired GR2017/GR2018 fired under; a serial run merges no parallel branches.
        if (plan.Config.MaxParallelism <= 1)
        {
            return;
        }

        // Multi-wave plans (SSOT §3.3/§14.3): GR2028 applies PER WAVE — each multi-leaf/fan-in wave's own
        // '<wave>/guardrails/' folder must carry ≥1 real integration re-run (the last wave's exit gate is
        // the whole-plan boundary; the plan-root '<plan>/guardrails/' is optional-additive). A flat plan
        // keeps the whole-plan check unchanged.
        if (plan.IsWaved)
        {
            foreach (WaveNode wave in plan.Waves)
            {
                if (RequiresIntegrationGate(wave.Tasks) && !wave.Guardrails.Any(ReRunsIntegrationSet))
                {
                    diagnostics.Add(Error(DiagnosticCodes.PlanGuardrailsMissingIntegrationReRun, wave.Directory,
                        $"Wave '{wave.Dir}' has a parallel topology (≥2 leaf tasks or a fan-in task) but its " +
                        $"'{wave.Dir}/guardrails/' exit gate carries no deterministic check that re-runs the " +
                        "integration set. Each such wave's exit gate is a union soundness boundary; an empty " +
                        "folder — or one holding only a tautological 'exit 0' file — verifies nothing. " +
                        Gr2028AcceptedFormsClause +
                        $" Add a '{wave.Dir}/guardrails/' check of one of the two accepted forms " +
                        "(SSOT §3.3/§14.3)."));
                }
            }

            return;
        }

        if (RequiresIntegrationGate(plan.Tasks) && !plan.PlanGuardrails.Any(ReRunsIntegrationSet))
        {
            diagnostics.Add(Error(DiagnosticCodes.PlanGuardrailsMissingIntegrationReRun, plan.PlanDirectory,
                "Plan has a parallel topology (≥2 leaf tasks or a fan-in task) but its terminal " +
                "'<plan>/guardrails/' folder carries no deterministic check that re-runs the integration " +
                "set. The terminal gate is the whole-repo soundness boundary; an empty folder — or one " +
                "holding only a tautological 'exit 0' file — verifies nothing. " +
                Gr2028AcceptedFormsClause +
                " Add a '<plan>/guardrails/' check of one of the two accepted forms (SSOT §3.3)."));
        }
    }

    /// <summary>
    /// The teaching clause shared by both GR2028 messages (plan-level and per-wave). Names the two
    /// ungameable forms that satisfy the terminal gate and states — the #343 doctrine-tightening — that a
    /// content/"contribution-present" grep alone is NOT sufficient: it is additive, because the union-safe
    /// conditional shape (SSOT §4.3/#165) can never FAIL when a merge dropped a contribution entirely, so
    /// it certifies nothing about union soundness on its own. This is the single message that would have
    /// saved the #343 reporter the trial-and-error.
    /// </summary>
    private const string Gr2028AcceptedFormsClause =
        "A GR2028-satisfying check must prove union soundness one of two ways: a " +
        "git-conflict-marker-freedom check (a line-anchored '<<<<<<<' / '>>>>>>>' scan) OR a recognized " +
        "whole-repo build/test/suite invocation. A content/'contribution-present' grep alone does NOT " +
        "satisfy GR2028 — it is additive, not sufficient, because it cannot fail when a merge DROPPED a " +
        "contribution entirely.";

    /// <summary>
    /// True when a task set forms a UNION that needs a terminal integration re-run (GR2028): ≥2 leaf tasks
    /// (no dependents within the set) or any fan-in task (≥2 upstreams). A single linear chain forms no
    /// union and is exempt. Shared by the whole-plan (flat) and per-wave (SSOT §14.3) checks.
    /// </summary>
    private static bool RequiresIntegrationGate(IReadOnlyList<TaskNode> tasks)
    {
        var dependedOn = new HashSet<string>(tasks.SelectMany(t => t.DependsOn), StringComparer.Ordinal);
        int leafCount = tasks.Count(t => !dependedOn.Contains(t.Id));
        return leafCount >= 2 || tasks.Any(t => t.DependsOn.Count >= 2);
    }

    /// <summary>
    /// Content-teeth test for the re-homed terminal gate (GR2028). A <c>&lt;plan&gt;/guardrails/</c>
    /// file "re-runs the integration set" when it is a deterministic (script) check whose effective
    /// body — comment and blank lines stripped, so a comment that merely NAMES a build command cannot
    /// count — matches EITHER of the two recognised forms SSOT §3.3 documents as equally valid:
    /// <list type="bullet">
    /// <item>a recognised whole-repo build/test/suite command actually INVOKED (<see cref="InvokesIntegrationCommand"/>),
    /// or</item>
    /// <item>a genuine UNION INVARIANT — a check for git conflict markers
    /// (<see cref="UnionInvariantConflictMarker"/>), the deterministic verdict a merged/union tree
    /// actually integrated cleanly. This form exists for plans with no build/test tool to invoke at all
    /// (e.g. a portable, zero-toolchain demo plan) whose only honest integration content is "the merged
    /// bytes are non-empty and conflict-marker-free" — the canonical shape used throughout this repo's
    /// own union-safe guardrails (catalogue → "A scope:'integration' guardrail MUST be UNION-SAFE").</item>
    /// </list>
    /// A tautological <c>exit 0</c> file, a bare <c>echo</c>, or a prompt guardrail does NOT qualify
    /// under either form: the rule certifies a real re-run, not a present file. Unreadable files do not
    /// qualify (other checks surface the IO problem).
    /// <para>
    /// <b>Invocation-shape teeth (issue #207).</b> The build/test form is NOT a bare keyword match anywhere
    /// on a non-comment line — that was gameable by a line that merely MENTIONS a build command inside a
    /// string, e.g. <c>echo "reminder: dotnet test should pass"</c> (a non-comment line, yet nothing is
    /// invoked). It now requires a real INVOCATION shape: the command must appear at a <b>statement
    /// position</b> — the leading command word of a pipeline/statement segment — and NOT be the argument of
    /// an output builtin (<c>echo</c>/<c>printf</c>/<c>Write-Output</c>/…). Quoted-string literals are
    /// stripped first so a keyword inside a quote never counts. The conflict-marker form deliberately keeps
    /// operating on the comment-stripped (NOT quote-stripped) body: a genuine marker check often carries the
    /// 7-char token in a quoted string (<c>grep -q '&lt;&lt;&lt;&lt;&lt;&lt;&lt;'</c>), and there is no
    /// legitimate reason to write that exact sequence other than detecting it, so it stays ungameable.
    /// </para>
    /// </summary>
    private static bool ReRunsIntegrationSet(GuardrailDefinition guardrail)
    {
        if (guardrail.Kind != ActionKind.Script)
        {
            return false;
        }

        string? body = TryReadAllText(guardrail.Path);
        if (body is null)
        {
            return false;
        }

        string stripped = StripCommentLines(body);
        return InvokesIntegrationCommand(stripped) || UnionInvariantConflictMarker.IsMatch(stripped);
    }

    /// <summary>
    /// The GR2028 build/test content teeth (form 1 of 2) with issue-#207 invocation-shape rigor. Returns
    /// true only when a recognised whole-repo build/test/suite command is actually INVOKED — the leading
    /// command word of some pipeline/statement segment of a non-comment line — not merely mentioned. Each
    /// line has its quoted-string literals stripped (so a keyword inside a quote never counts), is split
    /// into statement/pipeline segments on shell/PowerShell boundaries (<c>|</c>, <c>;</c>, <c>&amp;&amp;</c>,
    /// <c>||</c>, <c>(</c>, <c>{</c>, <c>$(</c>, backtick, <c>then</c>/<c>do</c>/<c>else</c>), and each
    /// segment whose leading command word is an OUTPUT builtin (<see cref="OutputBuiltin"/>) is discarded —
    /// its arguments are just text, not a build invocation. Only then is the segment tested against
    /// <see cref="IntegrationReRunCommand"/> anchored at the segment's start.
    /// <para>
    /// <b>Captured invocations count (issue #429).</b> A leading assignment prefix is stripped from each
    /// segment first (<see cref="StripCaptureAssignment"/>), so <c>$log = dotnet test &lt;sln&gt; 2>&amp;1</c>
    /// — the capture-then-re-emit form the .NET stack reference MANDATES for every tests-pass guardrail
    /// (#179), so the failure detail lands in the harness's ~60-line retry-feedback tail — is recognized as
    /// the invocation it is. It previously was not: the <c>$</c> statement boundary left the segment reading
    /// <c>log = dotnet test …</c>, whose leading word is <c>log</c>. A terminal gate that correctly followed
    /// #179 therefore FAILED GR2028 for having "no integration re-run" while running the whole solution's
    /// build and suite — two shipped rules that could not both be satisfied in one file. The recognizer must
    /// not reject the exact form another rule requires.
    /// </para>
    /// </summary>
    private static bool InvokesIntegrationCommand(string strippedBody)
    {
        foreach (string line in strippedBody.Split('\n'))
        {
            string cleaned = StripQuotedLiterals(line);
            foreach (string segment in SplitIntoStatementSegments(cleaned))
            {
                // Issue #429 — a CAPTURED invocation is still an invocation. `$log = dotnet test …` puts the
                // command one token to the right of the statement start, so drop a leading assignment
                // prefix before asking whether the segment's leading word is a build/test command.
                string trimmed = StripCaptureAssignment(segment.TrimStart());
                if (trimmed.Length == 0)
                {
                    continue;
                }

                // Discard a segment led by an output builtin — echo/printf/Write-Output "…dotnet test…"
                // MENTIONS a command, it does not invoke one. Applied AFTER the assignment strip, so
                // `$x = echo "… dotnet test …"` is discarded on the same grounds as a bare `echo`.
                if (OutputBuiltin.IsMatch(trimmed))
                {
                    continue;
                }

                // The command must be at the statement's START (its leading command word), not buried as
                // an argument mid-segment — a real invocation shape, not a keyword anywhere on the line.
                if (IntegrationReRunCommand.IsMatch(trimmed))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Remove single- and double-quoted string literals from a single line so a build/test keyword INSIDE
    /// a quoted string (the issue-#207 <c>echo "… dotnet test …"</c> bypass) is not mistaken for an
    /// invocation. Best-effort textual strip (not a full shell tokenizer): each run between matching quotes
    /// is dropped. An unbalanced trailing quote drops the remainder of the line, which is the conservative
    /// direction (a mentioned keyword must not survive).
    /// </summary>
    private static string StripQuotedLiterals(string line) =>
        QuotedLiteral.Replace(line, " ");

    private static readonly Regex QuotedLiteral = new(
        "\"[^\"]*\"?|'[^']*'?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Split a (quote-stripped) line into pipeline/statement segments on the shell + PowerShell boundaries
    /// that begin a fresh command word: <c>|</c>, <c>;</c>, <c>&amp;</c>, <c>&amp;&amp;</c>, <c>||</c>,
    /// <c>(</c>, <c>)</c>, <c>{</c>, <c>}</c>, <c>$(</c>, and backtick. The boundary keywords
    /// <c>then</c>/<c>do</c>/<c>else</c> are not split here (they are handled by the leading-word test
    /// after their own line/segment); this keeps the split purely on punctuation.
    /// </summary>
    private static IEnumerable<string> SplitIntoStatementSegments(string line) =>
        line.Split(StatementBoundaries, StringSplitOptions.RemoveEmptyEntries);

    private static readonly char[] StatementBoundaries = ['|', ';', '&', '(', ')', '{', '}', '`', '$'];

    /// <summary>
    /// Issue #429 — remove ONE leading <c>&lt;name&gt; =</c> capture-assignment prefix from a statement
    /// segment, so the command being assigned is tested as the leading command word it actually is.
    /// <para>
    /// The PowerShell capture form <c>$log = dotnet test &lt;sln&gt; -c Release 2>&amp;1 | Out-String</c> is
    /// what #179 mandates for every tests-pass guardrail. <c>$</c> is already a statement boundary (so
    /// <c>$(…)</c> command substitution splits correctly), which leaves the segment as
    /// <c>log = dotnet test …</c> — a real invocation hidden behind an identifier. The POSIX twin
    /// <c>log=$(dotnet test …)</c> needed no help: <c>$</c> and <c>(</c> already split it.
    /// </para>
    /// <para>
    /// <b>Why this cannot re-open the #207 mention bypass.</b> Stripping happens on a body that has already
    /// had whole-line comments removed (<see cref="StripCommentLines"/>) and the line's quoted literals
    /// removed (<see cref="StripQuotedLiterals"/>). So the only thing an assignment's right-hand side can be
    /// here is a bare, unquoted command word — <c>$msg = "run dotnet test"</c> strips to <c>msg =</c> and
    /// credits nothing, and <c>$x = echo "… dotnet test …"</c> strips to <c>echo</c> and is discarded by
    /// <see cref="OutputBuiltin"/> exactly as a bare <c>echo</c> is. A comparison is not stripped either:
    /// the <c>(?![=~])</c> lookahead keeps <c>==</c> and the bash <c>=~</c> out, and an unstripped
    /// comparison could not match the anchored command regex anyway.
    /// </para>
    /// </summary>
    private static string StripCaptureAssignment(string segment) =>
        CaptureAssignmentPrefix.Replace(segment, string.Empty, 1).TrimStart();

    /// <summary>
    /// A single <c>&lt;identifier&gt; =</c> assignment prefix at a segment's start. The identifier allows
    /// <c>:</c> and <c>.</c> so PowerShell scope/drive qualifiers (<c>$script:out</c>, <c>$env:CI</c>) are
    /// covered; the leading <c>$</c> is already gone, consumed as a statement boundary. Comparison operators
    /// (<c>==</c>, <c>=~</c>) are excluded by lookahead.
    /// </summary>
    private static readonly Regex CaptureAssignmentPrefix = new(
        @"^[A-Za-z_][A-Za-z0-9_:.]*\s*=(?![=~])\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// An output builtin that PRINTS its arguments (they are text, never an invocation): <c>echo</c>,
    /// <c>printf</c>, <c>print</c>, and the PowerShell <c>Write-*</c> family. Anchored at the segment start.
    /// </summary>
    private static readonly Regex OutputBuiltin = new(
        @"^(?:echo|printf|print|write-output|write-host|write-error|write-warning|write-information|write-verbose|write-debug)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Recognised whole-repo build / test / suite invocations that constitute a real integration-set
    /// re-run (the GR2028 content teeth, form 1 of 2). Deliberately broad across ecosystems (.NET, node,
    /// python, rust, go, java/kotlin, C/C++, ruby, php) so a genuine full-suite/build check is credited
    /// while a tautological <c>exit 0</c> or bare no-op is not. Case-insensitive. ANCHORED at the start of
    /// a statement segment (issue #207) so it matches a command actually being run, not a keyword buried
    /// mid-line; an optional <c>&amp;</c> (PowerShell call operator) or <c>sudo</c>/<c>exec</c> prefix is
    /// allowed before the command word.
    /// </summary>
    private static readonly Regex IntegrationReRunCommand = new(
        @"^(?:&\s*|sudo\s+|exec\s+)?(?:dotnet\s+(?:test|build|msbuild|vstest|run)|msbuild|nuke|cake|npm\s+(?:test|run|ci)|yarn|pnpm|pytest|python\d?\s+-m\s+(?:pytest|unittest)|tox|cargo\s+(?:test|build|check)|go\s+(?:test|build|vet)|mvn|gradle|ctest|cmake\s+--build|bazel\s+(?:test|build)|swift\s+(?:test|build)|make|rspec|jest|vitest|mocha|phpunit|git\s+diff\s+--check)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Recognised git-conflict-marker check that constitutes a real integration-set re-run via a genuine
    /// UNION INVARIANT (the GR2028 content teeth, form 2 of 2 — SSOT §3.3, added for plans with no
    /// build/test tool to invoke, e.g. a portable zero-toolchain demo). Matches a literal occurrence of
    /// one of the two labelled ours/theirs 7-character git conflict-marker tokens
    /// (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c>, <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>) in the STRIPPED body —
    /// comments already removed by <see cref="StripCommentLines"/>, so a comment that merely explains
    /// what conflict markers are cannot satisfy this. A script that genuinely tests for these markers is,
    /// by construction, verifying the merged/union bytes integrated cleanly — no legitimate reason exists
    /// to search for this exact 7-character sequence other than conflict-marker detection, so this
    /// signal is effectively ungameable without actually performing the check.
    /// <para>
    /// <b>The bare <c>=======</c> middle marker is NOT credited (issue #343, aligning with #187).</b>
    /// #187 retired the bare <c>=======</c> check from the doctrine because it collides with legitimate
    /// content — a <c>======</c> banner, a Markdown setext header underline, an ASCII-art table rule —
    /// and false-fires on a correct run. A guardrail whose ONLY conflict evidence was a bare
    /// <c>=======</c> used to be credited here (a latent validator/doctrine drift); it no longer is. The
    /// labelled ours/theirs tokens are the union-soundness signal, and the good anchored form
    /// (<c>(?m)^&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> / <c>(?m)^&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>) still
    /// contains them.
    /// </para>
    /// </summary>
    private static readonly Regex UnionInvariantConflictMarker = new(
        @"<{7}|>{7}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Drop whole-line comments (leading <c>#</c>, <c>//</c>, <c>REM</c>, <c>::</c>) so a comment that
    /// merely NAMES a build/test command (e.g. a <c>catches:</c> line) cannot be mistaken for the check
    /// actually invoking one.
    /// </summary>
    private static string StripCommentLines(string body) =>
        string.Join('\n', body.Split('\n').Where(line => !IsCommentLine(line)));

    /// <summary>
    /// <see cref="StripCommentLines"/>'s line-preserving twin: a comment line is BLANKED rather than
    /// removed, so an offset into the result still maps to the line number the reader will find in the
    /// file. Same #97 exclusion (the shared <see cref="IsCommentLine"/>), so a header comment that merely
    /// DESCRIBES a construction still cannot be what trips a check. Used by GR2057, which cites two clause
    /// LINE NUMBERS — a citation off by however many comment lines sit above it is worse than none.
    /// </summary>
    private static string BlankCommentLines(string body) =>
        string.Join('\n', body.Split('\n').Select(line => IsCommentLine(line) ? string.Empty : line));

    private static bool IsCommentLine(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith('#')
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("::", StringComparison.Ordinal)
            || (trimmed.StartsWith("REM", StringComparison.OrdinalIgnoreCase) &&
                (trimmed.Length == 3 || char.IsWhiteSpace(trimmed[3])));
    }

    /// <summary>
    /// Validate <c>writeScope</c> across all tasks — including every waved task, since
    /// <see cref="PlanDefinition.Tasks"/> is the flattened union of the waves (plan 08 §2/§3.4, SSOT §3.4).
    /// GR2041 ERROR: <c>writeScope</c> is absent/null — REQUIRED on every task (issue #389); a present-empty
    /// <c>[]</c> ("writes nothing to the repo") is VALID and falls through untouched.
    /// GR2019 ERROR: an entry is an absolute path or contains <c>..</c> (escapes the workspace).
    /// GR2020 WARNING: an entry is vacuous/over-broad (e.g. <c>**</c> or <c>*</c>).
    /// Plan-level and wave-level gate FOLDERS have no <c>task.json</c>, so they are unaffected.
    /// </summary>
    private static void ValidateWriteScopes(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (TaskNode task in plan.Tasks)
        {
            // GR2041 (#389): writeScope is REQUIRED on every task. An ABSENT/null field is the "lazy
            // planning" this forbids — it would skip the write-scope check and let the task write
            // anywhere. This PRESENCE check runs BEFORE the Count guard so that a DELIBERATE present-empty
            // [] (Count == 0 — "writes nothing to the repo") FALLS THROUGH as valid via the guard below
            // and is never flagged.
            if (task.WriteScope is null)
            {
                diagnostics.Add(Error(DiagnosticCodes.MissingWriteScope, task.Directory,
                    $"Task '{task.Id}' does not declare a writeScope. Every task must declare its write " +
                    "surface — list the paths it writes, or an empty [] if it writes nothing to the repo. " +
                    "Omitting the field is not allowed (SSOT §3.4)."));
                continue;
            }

            if (task.WriteScope is not { Count: > 0 } scope) continue;

            foreach (string entry in scope)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;

                // GR2019: absolute path or contains ".." segments (could escape workspace).
                if (Path.IsPathRooted(entry) ||
                    entry.Split('/', '\\').Any(seg => seg == ".."))
                {
                    diagnostics.Add(Error(DiagnosticCodes.WriteScopeEscapesWorkspace, task.Directory,
                        $"Task '{task.Id}' writeScope entry '{entry}' is an absolute path or contains " +
                        "'..' segments, which could reference files outside the workspace root. " +
                        "Write-scope entries must be relative to the repository root (SSOT §3.4)."));
                }

                // GR2020: vacuous entry that matches everything (e.g. "**" or "*").
                string trimmed = entry.Trim('/');
                if (trimmed is "**" or "*")
                {
                    diagnostics.Add(Warning(DiagnosticCodes.WriteScopeVacuous, task.Directory,
                        $"Task '{task.Id}' writeScope entry '{entry}' is over-broad and matches every " +
                        "path in the repository, providing no meaningful write isolation. Narrow the " +
                        "scope to a specific directory or file pattern (SSOT §3.4)."));
                }
            }
        }
    }

    /// <summary>
    /// The named turn-budget threshold for the GR2042 structural over-scope lint (issue #378). A task
    /// whose <c>action.maxTurns</c> is at or above this is turn-heavy by the author's OWN #94 budget bump;
    /// combined with a multi-file <c>writeScope</c> it is the thrash-and-timeout profile. Keyed here (≈60)
    /// rather than on the current literal max (75) so the lint does not silently break when the max budget
    /// bump moves — a task bumped to any near-max budget still trips clause (i).
    /// </summary>
    public const int OverScopeTurnThreshold = 60;

    /// <summary>
    /// GR2042 (WARNING, issue #378 / SSOT §3.4): the deterministic structural over-scope lint. Reads the
    /// mechanically-checkable over-scope signals sitting in the emitted <c>task.json</c> — <c>writeScope</c>
    /// cardinality, <c>action.maxTurns</c>, and <c>dependsOn</c> fan-in — and warns on the co-occurring
    /// fingerprint of a fan-in / composition-root-wiring SINK (the motivating task-15 shape: a turn-heavy
    /// budget bump PLUS a multi-file surface PLUS a wide fan-in). A WARNING, not an error: it surfaces the
    /// over-scope for <c>/guardrails-review</c> to acknowledge or resolve with a split, moving the whole
    /// thrash-and-timeout class LEFT of the run deterministically, without hard-failing a plan whose author
    /// had a defensible reason. Post-#389 every task carries a <c>writeScope</c>; a non-writing task's
    /// <c>[]</c> (Count 0) never trips any clause, so this never fires on a read-only/verification task.
    /// </summary>
    private static void ValidateStructuralOverScope(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (TaskNode task in plan.Tasks)
        {
            int writeScopeCount = task.WriteScope?.Count ?? 0;
            int dependsOnCount = task.DependsOn.Count;
            int? maxTurns = task.Action.MaxTurns;

            // Each clause is a distinct thrash-and-timeout profile (SSOT §3.4). Build the fired-signal
            // list so the message names exactly WHICH co-occurrence tripped, not a generic "too big".
            var signals = new List<string>();

            // (i) turn-heavy budget bump AND a multi-file surface — the smoking-gun co-occurrence.
            if (maxTurns is int turns && turns >= OverScopeTurnThreshold && writeScopeCount >= 4)
            {
                signals.Add(
                    $"action.maxTurns {turns} (>= {OverScopeTurnThreshold}, turn-heavy by the author's own " +
                    $"budget bump) co-occurs with a {writeScopeCount}-path writeScope");
            }

            // (ii) a wide blast radius regardless of budget.
            if (writeScopeCount >= 6)
            {
                signals.Add($"writeScope spans {writeScopeCount} paths (wide blast radius; a one-line " +
                    "guardrail miss re-does the whole multi-file change)");
            }

            // (iii) a fan-in sink: many upstream producers composed into a multi-file composition root.
            if (dependsOnCount >= 5 && writeScopeCount >= 3)
            {
                signals.Add($"dependsOn fans in {dependsOnCount} upstream producers into a " +
                    $"{writeScopeCount}-path writeScope (a fan-in / composition-root sink)");
            }

            if (signals.Count == 0) continue;

            diagnostics.Add(Warning(DiagnosticCodes.StructuralOverScope, task.Directory,
                $"Task '{task.Id}' carries the structural over-scope fingerprint of a fan-in / " +
                $"composition-root-wiring sink: {string.Join("; ", signals)}. Such a task thrashes at run " +
                "time — every guardrail miss re-runs the whole oversized action, the most likely " +
                "needs-human in a run. Split it into one task per collaborator wiring (factory " +
                "registration, scheduler call-site, CLI plumbing — each a separately-verifiable " +
                "integration point), isolating the turn-expensive composition-root proof to a thin sink " +
                "(SSOT §3.4, #378). 'It's just wiring' is a rationalization that dodges the split; this is " +
                "a WARN for /guardrails-review to resolve, not a hard failure."));
        }
    }

    /// <summary>
    /// Validate <c>stagingOutputs</c> entries across all tasks (SSOT §3.5, issue #130). All causes
    /// share one code, <c>GR2024</c> (error), each with a precise reason string (mirrors how GR2019/
    /// GR2020 carry one code with a specific reason). <c>stagingOutputs</c> is OPTIONAL — absent (null)
    /// ⇒ no check. A PRESENT list is rejected when:
    /// <list type="bullet">
    ///   <item>the array is empty (declares staging but stages nothing);</item>
    ///   <item>an entry has a missing/empty <c>from</c> or <c>to</c>;</item>
    ///   <item>a <c>to</c> does not normalize to a path under <c>.claude/</c> (the load-bearing check:
    ///     <c>stagingOutputs</c> exists only to land <c>.claude/</c> deliverables);</item>
    ///   <item>a <c>to</c> escapes the workspace (absolute, or <c>..</c> climbing out — same family as
    ///     GR2019 for <c>writeScope</c>);</item>
    ///   <item>a <c>from</c> escapes the staging root (absolute, or <c>..</c> climbing above the
    ///     per-task staging dir).</item>
    /// </list>
    /// </summary>
    private static void ValidateStagingOutputs(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (TaskNode task in plan.Tasks)
        {
            if (task.StagingOutputs is not { } staging)
            {
                continue; // absent ⇒ no staging, no check (the unchanged default).
            }

            if (staging.Count == 0)
            {
                diagnostics.Add(Error(DiagnosticCodes.StagingOutputsInvalid, task.Directory,
                    $"Task '{task.Id}' declares an empty 'stagingOutputs' array — it declares staging " +
                    "but stages nothing. Remove the field, or add at least one { from, to } entry (SSOT §3.5)."));
                continue;
            }

            foreach (StagingOutput entry in staging)
            {
                ValidateStagingEntry(task, entry, diagnostics);
            }
        }
    }

    private static void ValidateStagingEntry(TaskNode task, StagingOutput entry, List<Diagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(entry.From))
        {
            diagnostics.Add(Error(DiagnosticCodes.StagingOutputsInvalid, task.Directory,
                $"Task '{task.Id}' has a stagingOutputs entry with a missing or empty 'from'. " +
                "'from' is the path/glob (relative to GUARDRAILS_STAGING_DIR) the action writes (SSOT §3.5)."));
            return;
        }

        if (string.IsNullOrWhiteSpace(entry.To))
        {
            diagnostics.Add(Error(DiagnosticCodes.StagingOutputsInvalid, task.Directory,
                $"Task '{task.Id}' stagingOutputs 'from' '{entry.From}' has a missing or empty 'to'. " +
                "'to' is the workspace-relative destination under '.claude/' (SSOT §3.5)."));
            return;
        }

        // 'from' must stay WITHIN the staging root: not absolute, no '..' climbing above it.
        if (Path.IsPathRooted(entry.From) ||
            entry.From.Split('/', '\\').Any(seg => seg == ".."))
        {
            diagnostics.Add(Error(DiagnosticCodes.StagingOutputsInvalid, task.Directory,
                $"Task '{task.Id}' stagingOutputs 'from' '{entry.From}' is an absolute path or contains " +
                "'..' segments, which would escape the per-task staging root. 'from' must be relative to " +
                "GUARDRAILS_STAGING_DIR and stay within it (SSOT §3.5)."));
        }

        // 'to' must stay WITHIN the workspace: not absolute, no '..' climbing out (same family as GR2019).
        if (Path.IsPathRooted(entry.To) ||
            entry.To.Split('/', '\\').Any(seg => seg == ".."))
        {
            diagnostics.Add(Error(DiagnosticCodes.StagingOutputsInvalid, task.Directory,
                $"Task '{task.Id}' stagingOutputs 'to' '{entry.To}' is an absolute path or contains " +
                "'..' segments, which could reference files outside the workspace root. 'to' must be " +
                "workspace-relative (SSOT §3.5, cf. GR2019)."));
            return; // an escape already disqualifies it; the under-.claude check below is moot.
        }

        // 'to' must normalize to a path UNDER '.claude/' — the load-bearing check.
        if (!NormalizesUnderClaude(entry.To))
        {
            diagnostics.Add(Error(DiagnosticCodes.StagingOutputsInvalid, task.Directory,
                $"Task '{task.Id}' stagingOutputs 'to' '{entry.To}' does not resolve under '.claude/'. " +
                "stagingOutputs exists only to land '.claude/' deliverables; a non-'.claude/' destination " +
                "is either a misunderstanding (use a normal action write) or an escape attempt (SSOT §3.5)."));
        }
    }

    /// <summary>
    /// True when <paramref name="to"/> (already known to be workspace-relative and free of <c>..</c>)
    /// has <c>.claude</c> as its first normalized path segment — so it lands under <c>.claude/</c>.
    /// Tolerates a leading <c>./</c> and either slash style; an empty/whitespace segment (a stray
    /// leading slash already excluded as "rooted") is skipped.
    /// </summary>
    private static bool NormalizesUnderClaude(string to)
    {
        foreach (string segment in to.Split('/', '\\'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue; // skip leading "./" and empty segments.
            }

            return segment == ".claude";
        }

        return false;
    }

    /// <summary>
    /// Every non-null guardrail <c>scope</c> must be one of <c>integration</c> or <c>local</c>
    /// (plan 08 M2, SSOT §4.3). An unrecognised value (e.g. a typo like <c>intergation</c>)
    /// silently degrades to <c>local</c> at runtime, dropping the guardrail from the integration
    /// union re-verify set — a deterministic gate quietly stops re-running. GR2021 ensures the
    /// typo is caught at validate time, never at silent runtime. Fires for both the deterministic
    /// sidecar <c>scope</c> key and the prompt-frontmatter <c>scope</c> (both are normalised to
    /// lowercase by the loader; the validator can do case-sensitive comparison).
    /// </summary>
    private static void ValidateGuardrailScopeValues(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        // Scope is a guardrail-shaped-file concept, so it is validated across ALL FOUR folders (SSOT §4)
        // — a typo in a preflight or a plan-level folder degrades the same way as one in a task guardrail.
        foreach (TaskNode task in plan.Tasks)
        {
            CheckGuardrailScopes(task.Guardrails, $"task '{task.Id}'", diagnostics);
            CheckGuardrailScopes(task.Preflights, $"task '{task.Id}' preflights", diagnostics);
        }

        CheckGuardrailScopes(plan.PlanPreflights, "<plan>/preflights/", diagnostics);
        CheckGuardrailScopes(plan.PlanGuardrails, "<plan>/guardrails/", diagnostics);
    }

    private static void CheckGuardrailScopes(
        IReadOnlyList<GuardrailDefinition> guardrails, string context, List<Diagnostic> diagnostics)
    {
        foreach (GuardrailDefinition guardrail in guardrails)
        {
            if (guardrail.Scope is null)
                continue;

            if (guardrail.Scope != "integration" && guardrail.Scope != "local")
            {
                diagnostics.Add(Error(DiagnosticCodes.InvalidGuardrailScopeValue, guardrail.Path,
                    $"Guardrail '{guardrail.Name}' ({context}) has unrecognised scope value " +
                    $"'{guardrail.Scope}'. The only recognised values are 'integration' and 'local'. " +
                    "An unrecognised value silently degrades to 'local' at runtime, dropping the " +
                    "guardrail from the integration union re-verify set (SSOT §4.3, plan 08 §3)."));
            }
        }
    }

    /// <summary>
    /// GR2059 (WARNING, SSOT §4.3/§14.3, issue #459): a WAVE-ROOT guardrail declaring
    /// <c>scope:"integration"</c> is announcing an intention the harness does not act on.
    /// <para>
    /// The per-union re-verify set is <c>Scheduler.UnionIntegrationSet</c> — task
    /// <c>&lt;task&gt;/guardrails/</c> plus plan-root <c>&lt;plan&gt;/guardrails/</c> (#451). A
    /// <c>&lt;plan&gt;/&lt;wave&gt;/guardrails/</c> file is never in it: it is the wave's EXIT gate, run
    /// once on the merged HEAD at wave end (§14.3). So the tag is not honoured and not rejected — it is
    /// inert, and the author gets no signal, which is the whole complaint. #457 is what that silence
    /// costs: the natural home for a union-safe invariant on a waved plan IS the wave that owns the
    /// colliding siblings, and placing it there is exactly what makes it never fire.
    /// </para>
    /// <para>
    /// A WARNING and NOT a behaviour change, on purpose. Adding these files to the union set would change
    /// the §14.3 exit-gate contract: a check with one evaluation point does not have to be UNION-SAFE, and
    /// running it at every intra-wave union demands that it pass on a partial merge where downstream tasks
    /// have not run (#125/#165) — a terminal postcondition tagged <c>integration</c> would start
    /// red-halting healthy partial merges. #459 names that an architect call. This is the interim answer
    /// that is right under every destination.
    /// </para>
    /// <para>
    /// CONSERVATIVE by construction, per the "a validator that cries wolf gets ignored" bar: it needs a
    /// waved plan, the wave-root <c>guardrails/</c> folder, and the exact recognised value
    /// <c>integration</c> (GR2021 owns unrecognised spellings; the loader lowercases). Every position
    /// where the tag DOES work — task guardrails, the plan root, any flat plan — is untouched, so the
    /// warning is emitted only where the author's stated intent and the harness's behaviour genuinely
    /// disagree.
    /// </para>
    /// </summary>
    private static void ValidateInertWaveIntegrationScope(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        if (!plan.IsWaved)
        {
            return;
        }

        foreach (WaveNode wave in plan.Waves)
        {
            foreach (GuardrailDefinition guardrail in wave.Guardrails)
            {
                if (guardrail.Scope != "integration")
                {
                    continue;
                }

                diagnostics.Add(Warning(DiagnosticCodes.WaveIntegrationScopeInert, guardrail.Path,
                    $"Guardrail '{guardrail.Name}' ({wave.Dir}/guardrails/) declares scope:\"integration\", " +
                    "which is INERT in this position. A wave-root guardrail is the wave's EXIT gate " +
                    $"(SSOT §14.3): it runs exactly once, on the merged HEAD at the end of '{wave.Dir}'. The " +
                    "per-union re-verify set is built from the task '<task>/guardrails/' folders plus the " +
                    "plan-root '<plan>/guardrails/' folder (SSOT §4.3), so this check does NOT re-run at " +
                    "unions inside the wave — including the fan-in it was most likely written for. " +
                    "To get per-union re-verification, move the check to '<plan>/guardrails/' and keep " +
                    "scope:\"integration\" there; it must then be UNION-SAFE — able to PASS on a partial " +
                    "merge where downstream tasks have not run yet (SSOT §4.3, #125). To keep it as the " +
                    "wave-exit gate it already is, drop the 'scope' key: the file behaves identically " +
                    "without it. Whether wave-root integration scope should become meaningful is an open " +
                    "contract question (issue #459); this warning exists so the tag is never silently inert."));
            }
        }
    }

    /// <summary>
    /// A guardrail's optional <c>expectedDurationSeconds</c> hint (SSOT §4.1.1, issue #331) must be a
    /// positive integer when present (GR2036) — a non-positive value can never be a real duration and
    /// would render nonsensically in the running-guardrail heartbeat. Validated across ALL FOUR
    /// guardrail-shaped folders (like <see cref="ValidateGuardrailScopeValues"/>), since the sidecar
    /// (and its hint) can sit next to any guardrail-shaped file. Absent (null) ⇒ no check.
    /// </summary>
    private static void ValidateGuardrailExpectedDurations(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (TaskNode task in plan.Tasks)
        {
            CheckGuardrailExpectedDurations(task.Guardrails, $"task '{task.Id}'", diagnostics);
            CheckGuardrailExpectedDurations(task.Preflights, $"task '{task.Id}' preflights", diagnostics);
        }

        CheckGuardrailExpectedDurations(plan.PlanPreflights, "<plan>/preflights/", diagnostics);
        CheckGuardrailExpectedDurations(plan.PlanGuardrails, "<plan>/guardrails/", diagnostics);
    }

    private static void CheckGuardrailExpectedDurations(
        IReadOnlyList<GuardrailDefinition> guardrails, string context, List<Diagnostic> diagnostics)
    {
        foreach (GuardrailDefinition guardrail in guardrails)
        {
            if (guardrail.ExpectedDurationSeconds is { } seconds && seconds <= 0)
            {
                diagnostics.Add(Error(DiagnosticCodes.ExpectedDurationNonPositive, guardrail.Path,
                    $"Guardrail '{guardrail.Name}' ({context}) has expectedDurationSeconds {seconds}, " +
                    "but it must be a positive integer. The field is a read-only progress hint " +
                    "surfaced in the running-guardrail heartbeat (SSOT §4.1.1); a zero/negative value " +
                    "is never a real duration. Remove it or set a positive number of seconds."));
            }
        }
    }

    /// <summary>
    /// Every check within a SINGLE folder must have a unique <see cref="GuardrailDefinition.Name"/>
    /// (GR2035, SSOT §4.5, issue #332). A guardrail's Name is its filename with the final extension
    /// dropped (<c>PlanLoader.GuardrailName</c>), so a portable pair like <c>01-build.ps1</c> +
    /// <c>01-build.sh</c> in one folder both collapse to Name <c>"01-build"</c>. Every surface that keys a
    /// check by <c>(taskId, Name)</c> or bare Name — the #219 status badges, the journal's
    /// <c>FailedGuardrail.Name</c>, the resume seed — then silently collapses the two distinct checks into
    /// one, so a result is misattributed to the wrong node. An ERROR: the ambiguity is knowable at load
    /// time. Checked per folder for every folder in the four-folder model — each task's <c>guardrails/</c>
    /// and <c>preflights/</c>, each wave's <c>preflights/</c> and <c>guardrails/</c> (SSOT §14.3), and the
    /// plan-level <c>preflights/</c> and <c>guardrails/</c>. Comparison is <see cref="StringComparer.Ordinal"/>,
    /// matching the case-sensitive keying the collapsing maps actually use (a case-only difference in Name
    /// stays two distinct keys, so it is not a collision).
    /// </summary>
    private static void ValidateDuplicateCheckNames(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        // Each list below is exactly ONE folder's worth of checks, so a within-list duplicate Name is a
        // within-folder collision. plan.Tasks is flattened across waves, so waved TASK folders are covered
        // by this loop; only the wave-LEVEL folders need the separate wave loop.
        foreach (TaskNode task in plan.Tasks)
        {
            CheckDuplicateCheckNames(task.Guardrails, $"task '{task.Id}' guardrails/", diagnostics);
            CheckDuplicateCheckNames(task.Preflights, $"task '{task.Id}' preflights/", diagnostics);
        }

        foreach (WaveNode wave in plan.Waves)
        {
            CheckDuplicateCheckNames(wave.Preflights, $"wave '{wave.Dir}' preflights/", diagnostics);
            CheckDuplicateCheckNames(wave.Guardrails, $"wave '{wave.Dir}' guardrails/", diagnostics);
        }

        CheckDuplicateCheckNames(plan.PlanPreflights, "<plan>/preflights/", diagnostics);
        CheckDuplicateCheckNames(plan.PlanGuardrails, "<plan>/guardrails/", diagnostics);
    }

    private static void CheckDuplicateCheckNames(
        IReadOnlyList<GuardrailDefinition> checks, string folderContext, List<Diagnostic> diagnostics)
    {
        foreach (IGrouping<string, GuardrailDefinition> group in
                 checks.GroupBy(c => c.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            // The colliding files share a directory; name it (and the files) so the fix is obvious.
            List<GuardrailDefinition> colliding = group.ToList();
            string folderPath = Path.GetDirectoryName(colliding[0].Path) ?? colliding[0].Path;
            string files = string.Join(", ", colliding
                .Select(c => Path.GetFileName(c.Path))
                .OrderBy(f => f, StringComparer.Ordinal));

            diagnostics.Add(Error(DiagnosticCodes.DuplicateCheckName, folderPath,
                $"Folder {folderContext} has {colliding.Count} checks that share the name '{group.Key}' " +
                $"(colliding files: {files}). A check's name is its filename without the final extension, so " +
                "a portable pair like '01-build.ps1' + '01-build.sh' collapses to one name — the harness keys " +
                "status badges, journal failures, and the resume seed by (task, name), so the second silently " +
                "overwrites the first and a result is misattributed to the wrong check. Rename one of the " +
                "colliding files so the names differ (SSOT §4.5)."));
        }
    }

    /// <summary>
    /// The banned-guardrail-pattern scan (GR2037, SSOT §4.6, issue #346). For every four-folder
    /// SCRIPT guardrail — task <c>guardrails/</c>+<c>preflights/</c>, wave <c>guardrails/</c>+
    /// <c>preflights/</c>, plan <c>guardrails/</c>+<c>preflights/</c> — read its body, <b>strip
    /// whole-line comments first</b> (reusing <see cref="StripCommentLines"/> — itself the #97
    /// lesson, so a <c>catches:</c>/header comment that merely DESCRIBES a banned construction cannot
    /// false-fire), then test each registry entry's <c>badPattern</c> against the stripped body. Emit
    /// ONE GR2037 per (guardrail, entry) match, citing the entry <c>id</c> + <c>reason</c> +
    /// <c>goodPatternHint</c>. Prompt guardrails are prose (not a regex construction) and script
    /// ACTIONS are out of scope for v1 — the scan is script-guardrail-only. A body that cannot be read
    /// is skipped (other checks surface the structural problem). Data-driven: a new lesson is a JSON
    /// entry, never new C# here.
    /// </summary>
    private void ValidateBannedGuardrailPatterns(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        if (_bannedPatterns.Patterns.Count == 0)
        {
            return;
        }

        foreach (GuardrailDefinition guardrail in FourFolderScriptGuardrails(plan))
        {
            string? body = TryReadAllText(guardrail.Path);
            if (body is null)
            {
                continue;
            }

            string stripped = StripCommentLines(body);

            foreach (BannedPattern pattern in _bannedPatterns.Patterns)
            {
                bool hit;
                try
                {
                    hit = pattern.Matcher.IsMatch(stripped);
                }
                catch (RegexMatchTimeoutException)
                {
                    // Issue #487: the matcher's bounded timeout must DEGRADE, not crash. Left unhandled it
                    // propagates out of Validate and takes down every unrelated check with it, surfacing as
                    // a stack trace rather than a diagnostic. Skip the pair and say so.
                    diagnostics.Add(Warning(DiagnosticCodes.BannedPatternScanTimedOut, guardrail.Path,
                        $"The banned-guardrail-pattern scan timed out matching entry {pattern.Id} against " +
                        $"guardrail '{guardrail.Name}'; that ONE (guardrail, entry) pair was skipped and the " +
                        "rest of validation is unaffected. This is not a finding about the plan — it means the " +
                        "scan could not reach a verdict, so the guardrail is neither cleared nor condemned for " +
                        "this entry. It should never happen: the registry's costliest entry is strictly linear " +
                        "and needs thousands of candidate sites in one script to reach the ceiling. Treat it as " +
                        "evidence of a pathological registry entry or an extraordinary script — not as a reason " +
                        "to weaken the entry (GR2058, SSOT §4.6, issue #487)."));
                    continue;
                }

                if (hit)
                {
                    diagnostics.Add(Error(DiagnosticCodes.BannedGuardrailPattern, guardrail.Path,
                        $"Guardrail '{guardrail.Name}' contains a banned regex construction ({pattern.Id}): " +
                        $"{pattern.Reason} Fix: {pattern.GoodPatternHint} A correct SKILL.md does not " +
                        "guarantee an LLM applies it every generation, so this fixed-spelling lesson is " +
                        "enforced deterministically here (banned-guardrail-patterns registry, GR2037 — " +
                        "SSOT §4.6)."));
                }
            }
        }
    }

    /// <summary>
    /// GR2056 (issue #473) — a guardrail SCRIPT that does not PARSE. Such a guardrail fails
    /// unconditionally: every attempt runs the action, then trips over a syntax error the agent cannot
    /// fix (the script is not in its write scope), so the task burns its whole retry budget and settles
    /// <c>needs-human</c>. Measured cost of one instance: two attempts plus a halt, on a script whose
    /// only defect was a stray backtick inside a double-quoted string.
    /// <para>Parsing is NOT executing — the probe asks the interpreter whether the text is
    /// well-formed and never runs it, so <c>validate</c> stays a read-only check safe for CI. The
    /// sibling failures #478 also wanted (already-green, throws-at-runtime, filter-matches-nothing)
    /// genuinely require execution and live in the skill phases instead (#479).</para>
    /// <para>Silence from the probe is NOT a clean bill of health: an absent interpreter, an
    /// unsupported language, or a probe timeout all report nothing. That asymmetry is deliberate —
    /// see <see cref="IScriptSyntaxProbe"/>.</para>
    /// </summary>
    private void ValidateGuardrailScriptsParse(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        List<GuardrailDefinition> scripts = [.. FourFolderScriptGuardrails(plan)];
        if (scripts.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<string, string> failures =
            _syntaxProbe.FindSyntaxErrors([.. scripts.Select(g => g.Path)]);
        if (failures.Count == 0)
        {
            return;
        }

        foreach (GuardrailDefinition guardrail in scripts)
        {
            if (!failures.TryGetValue(guardrail.Path, out string? message))
            {
                continue;
            }

            diagnostics.Add(Error(DiagnosticCodes.GuardrailScriptDoesNotParse, guardrail.Path,
                $"Guardrail '{guardrail.Name}' does not PARSE: {message} A guardrail that cannot be " +
                "parsed fails on every attempt, and the agent cannot fix it — the script is not in its " +
                "write scope — so the task burns its whole retry budget and dead-ends at needs-human. " +
                "Fix the syntax before running the plan (issue #473)."));
        }
    }

    /// <summary>
    /// A literal collection assigned to a variable: <c>$tests = @( 'a', 'b', 'c' )</c>. Captures the
    /// variable name and the element list so the element COUNT can be compared against a floor.
    /// Single- or double-quoted elements, any whitespace/newlines between them.
    /// </summary>
    private static readonly Regex LiteralNameArray = new(
        @"\$(?<var>\w+)\s*=\s*@\(\s*(?<items>(?:'[^']*'|""[^""]*"")(?:\s*,\s*(?:'[^']*'|""[^""]*""))*)\s*,?\s*\)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>A zero-match floor: <c>-lt 14</c>. The guard shape every generated test guardrail uses.</summary>
    private static readonly Regex NumericFloor = new(@"-lt\s+(?<floor>\d+)\b", RegexOptions.Compiled);

    /// <summary>A quoted element inside a captured <c>@( … )</c> list.</summary>
    private static readonly Regex QuotedItem = new(@"'[^']*'|""[^""]*""", RegexOptions.Compiled);

    /// <summary>
    /// GR2055 (issue #484) — a guardrail whose zero-match floor exceeds the cardinality of the literal
    /// collection its own <c>--filter</c> is built from, so it can never pass for any input.
    /// <para>The check is deliberately narrow, because a validator that produces false positives gets
    /// ignored and then the true positives are lost with it. All FOUR must hold before it fires:
    /// (1) a variable is assigned a literal array of N quoted names; (2) that SAME variable is
    /// referenced on a line that also mentions <c>filter</c> — the linkage that proves the array is
    /// what selects the tests, so an unrelated array and an unrelated threshold cannot collide;
    /// (3) the body contains a numeric floor <c>-lt M</c>; (4) M &gt; N.</para>
    /// <para>Comment lines are stripped first (the #97 lesson, as GR2037 does): a header comment
    /// explaining a floor must never be what trips it.</para>
    /// </summary>
    private static void ValidateUnsatisfiableGuardrailFloor(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (GuardrailDefinition guardrail in FourFolderScriptGuardrails(plan))
        {
            string? body = TryReadAllText(guardrail.Path);
            if (body is null)
            {
                continue;
            }

            string stripped = StripCommentLines(body);

            MatchCollection floors = NumericFloor.Matches(stripped);
            if (floors.Count == 0)
            {
                continue;
            }

            foreach (Match array in LiteralNameArray.Matches(stripped))
            {
                string varName = array.Groups["var"].Value;
                int count = QuotedItem.Matches(array.Groups["items"].Value).Count;
                if (count == 0 || !FeedsAFilter(stripped, varName))
                {
                    continue;
                }

                foreach (Match floor in floors)
                {
                    if (!int.TryParse(floor.Groups["floor"].Value, out int required) || required <= count)
                    {
                        continue;
                    }

                    diagnostics.Add(Error(DiagnosticCodes.UnsatisfiableGuardrailFloor, guardrail.Path,
                        $"Guardrail '{guardrail.Name}' can never pass: its --filter is built from ${varName}, " +
                        $"a literal list of {count} name(s), but it then requires at least {required} " +
                        $"executed test(s) (-lt {required}). The filter can select at most {count}, so the " +
                        $"floor is unreachable and EVERY attempt fails — the task dead-ends at needs-human " +
                        $"with its work possibly complete. Either lower the floor to what ${varName} can " +
                        $"produce, or widen the filter to the set the floor describes. A floor left behind " +
                        $"by a later narrowing of the filter is the usual cause (issue #484): the two " +
                        $"numbers are ONE invariant and must move together."));
                }
            }
        }
    }

    /// <summary>
    /// Does <paramref name="varName"/> appear on a line that also mentions a filter? That co-occurrence
    /// is what links the counted array to the test selection — without it the count means nothing and
    /// the check must stay silent.
    /// </summary>
    private static bool FeedsAFilter(string body, string varName)
    {
        foreach (string line in body.Split('\n'))
        {
            if (line.Contains("$" + varName, StringComparison.Ordinal)
                && line.Contains("filter", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A single-clause PowerShell presence test whose ENTIRE condition is ONE <c>-match</c>/<c>-notmatch</c>
    /// of a variable against a SINGLE-QUOTED literal, opening a block:
    /// <c>if ($content -notmatch '…') {</c>. Everything else is deliberately unmatched, because everything
    /// else makes the clause's polarity undecidable from the text:
    /// <list type="bullet">
    /// <item>a COMPOUND condition (<c>-and</c>/<c>-or</c>/<c>-not</c>/nested parens) — the block is then a
    /// verdict on the conjunction, not on this pattern, so taking the branch does not prove the pattern is
    /// required (the <c>\s*\)</c> immediately after the closing quote enforces this);</item>
    /// <item>a DOUBLE-QUOTED or COMPOSED operand (<c>("(?m)\b" + [regex]::Escape($m) + "\s*\(")</c>) — the
    /// pattern is not statically known, since PowerShell interpolates <c>$</c> inside <c>"…"</c>;</item>
    /// <item>a pattern spanning a newline — no guardrail in the field writes one, and admitting it lets a
    /// stray quote swallow half a script.</item>
    /// </list>
    /// <c>-cmatch</c>/<c>-imatch</c> and their <c>not</c> forms are the same operator with an explicit
    /// case rule and are admitted.
    /// </summary>
    private static readonly Regex PresenceClause = new(
        @"\bif\s*\(\s*\$(?<subject>\w+)\s+-[ci]?(?<neg>not)?match\s+'(?<pat>(?:[^'\r\n]|'')*)'\s*\)\s*\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Evidence that a clause's branch FAILS the guardrail rather than recording something: an append to a
    /// <c>$failures</c>-shaped accumulator, a non-zero <c>exit</c>, a <c>throw</c>, or a <c>Write-Error</c>.
    /// Both clauses of the measured #470 instance append to <c>$failures</c>; the catalogue's prescribed
    /// form writes a line and <c>exit 1</c>.
    /// </summary>
    private static readonly Regex ClauseFailsTheGuardrail = new(
        @"\$\w*fail\w*\s*\+=|\bexit\s+[1-9]|\bthrow\b|\bWrite-Error\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Regex metacharacters that make a pattern non-literal, so no exact witness can be derived.</summary>
    private const string RegexMetacharacters = "()[]{}|*+?.^$";

    /// <summary>
    /// Shortest witness worth reconciling. Below this a "collision" is noise — a two-character required
    /// literal tripping some forbidden pattern says nothing about the guardrail being unsatisfiable.
    /// </summary>
    private const int MinimumWitnessLength = 3;

    /// <summary>Bounded match timeout for the ad-hoc regexes GR2057 compiles out of a plan's own text.</summary>
    private static readonly TimeSpan ClauseMatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// GR2057 (issue #470 ask 1) — a guardrail that REQUIRES a token it also FORBIDS. One script carries a
    /// required-present clause and a forbidden-present clause over the SAME subject text, and the literal
    /// the first demands trips the pattern the second bans: removing the text fails clause 1, keeping it
    /// fails clause 2, so NO file satisfies both. Every attempt fails identically, the retry feedback is
    /// coherent, actionable and WRONG, and the task dead-ends at <c>needs-human</c> having never been
    /// achievable. The measured instance was a required <c>[Trait("Category", "TierResolution")]</c> whose
    /// own STRING LITERAL carried the token a clause 40 lines later forbade — each clause individually
    /// correct, which is why it was found by EXECUTING the guardrail and not by reading it.
    ///
    /// <para><b>Polarity — decided by what the branch DOES, not by the operator alone.</b> A clause counts
    /// only when its block carries a failure signal (<see cref="ClauseFailsTheGuardrail"/>). Given a
    /// failure branch, <c>-notmatch</c> means required-present and <c>-match</c> means forbidden-present.
    /// Without that requirement <c>if ($c -match 'x') { $ok = $true }</c> reads as a prohibition when it is
    /// in fact a REQUIREMENT wearing <c>-match</c>, and the polarity inverts — the single richest source of
    /// false positives available here. What this deliberately CANNOT decide, and therefore stays silent on:
    /// a clause whose block neither fails nor is brace-balanced in plain text (a <c>{</c> inside a string
    /// literal defeats the counter — silence, not a guess), an <c>else</c> branch, a <c>switch</c>, a
    /// negated wrapper, and any accumulator whose eventual effect is decided elsewhere.</para>
    ///
    /// <para><b>The two clauses must test the SAME subject variable.</b> This is the load-bearing
    /// conservatism, not a shortcut. The catalogue's prescribed fix for this very defect is the TWO-VARIABLE
    /// rule: the required clause reads <c>$code</c> (comments stripped) while the forbidden clause reads
    /// <c>$scan</c> (comments AND string literals stripped), so the trait's own literal survives for clause
    /// 1 and is gone for clause 2. Those are different TEXTS, and a collision between them is not proven by
    /// anything in the script. Requiring one subject makes GR2057 silent BY CONSTRUCTION on the fix it is
    /// asking for — a lint that fires on the remedy it recommends is worse than no lint.</para>
    ///
    /// <para><b>Literal extraction (de-regexing) handles a bounded subset and bails otherwise.</b>
    /// Admitted: escaped punctuation (<c>\[</c> → <c>[</c>), <c>\s*</c>/<c>\s?</c> → nothing,
    /// <c>\s</c>/<c>\s+</c> → one space, <c>\b</c> → nothing (zero-width), leading inline options
    /// (<c>(?i)</c>, <c>(?m)</c>), and a leading <c>^</c> / trailing <c>$</c> (zero-width anchors — the
    /// required text must still literally appear somewhere in a satisfying file, which is all the collision
    /// test needs). ANY other metacharacter — alternation, a group, a class, a quantifier, <c>.</c>,
    /// <c>\w</c>, <c>\d</c>, a backreference, a lookaround — means the required pattern does not pin one
    /// exact string, so no witness is produced and the clause is dropped. The witness is then re-tested
    /// against its OWN pattern: if the de-regexer produced something the original regex would not accept,
    /// the extraction was wrong and the clause is dropped. So the measured
    /// <c>\[Trait\s*\(\s*"Category"\s*,\s*"TierResolution"\s*\)\s*\]</c> yields the testable
    /// <c>[Trait("Category","TierResolution")]</c>, while its sibling <c>\[(Fact|Theory)\]</c> yields
    /// nothing at all.</para>
    ///
    /// <para><b>Forbidden patterns carrying an INPUT anchor are skipped</b> (<c>^</c>, <c>$</c>,
    /// <c>\A</c>, <c>\Z</c>, <c>\z</c>, <c>\G</c>). The witness is matched standalone, but in a real file
    /// the required text is EMBEDDED, so an anchor that looks satisfied against the bare witness need not
    /// be in the file the author would write. Lookarounds are deliberately NOT skipped: they are exactly
    /// what the prescribed anchor-on-a-USE fix is built from, and they see the witness's real neighbouring
    /// characters — which is why <c>TierResolver\s*\.|(?&lt;![\w.])TierResolution(?![\w"])</c> stays silent
    /// against the same witness that <c>TierResolver|TierResolution</c> trips.</para>
    ///
    /// <para><b>Out of scope by design.</b> Same-file pairs only — the cross-file variant (one guardrail
    /// requires what a sibling forbids) is strictly harder and #470 says it must not block this. And
    /// <c>.sh</c> guardrails: the equivalent shape is <c>grep -q</c> / <c>! grep -q</c> in POSIX ERE/BRE,
    /// a different pattern language whose collision test would not be sound under .NET regex semantics.
    /// Portable guardrails ship as <c>.ps1</c>+<c>.sh</c> pairs, so the defect is still caught for the
    /// pair. Comment lines are blanked first (the #97 lesson), so a header comment describing the
    /// collision cannot be what reports it.</para>
    /// </summary>
    private static void ValidateGuardrailRequiresForbiddenToken(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (GuardrailDefinition guardrail in FourFolderScriptGuardrails(plan))
        {
            string? body = TryReadAllText(guardrail.Path);
            if (body is null)
            {
                continue;
            }

            string scanned = BlankCommentLines(body);

            List<(string Subject, string Witness, int Line)> required = [];
            List<(string Subject, string Pattern, int Line)> forbidden = [];

            foreach (Match clause in PresenceClause.Matches(scanned))
            {
                // The regex ends ON the block's opening brace; the branch must FAIL for polarity to mean anything.
                if (!BranchFailsTheGuardrail(scanned, clause.Index + clause.Length - 1))
                {
                    continue;
                }

                string subject = clause.Groups["subject"].Value;
                string pattern = clause.Groups["pat"].Value.Replace("''", "'", StringComparison.Ordinal);
                int line = LineNumberAt(scanned, clause.Index);

                if (!clause.Groups["neg"].Success)
                {
                    forbidden.Add((subject, pattern, line));
                    continue;
                }

                string? witness = TryLiteralWitness(pattern);
                if (witness is null || witness.Trim().Length < MinimumWitnessLength || !MatchesWitness(pattern, witness))
                {
                    continue;
                }

                required.Add((subject, witness, line));
            }

            foreach ((string subject, string witness, int requiredLine) in required)
            {
                foreach ((string bannedSubject, string bannedPattern, int forbiddenLine) in forbidden)
                {
                    if (!string.Equals(subject, bannedSubject, StringComparison.OrdinalIgnoreCase)
                        || HasInputAnchor(bannedPattern)
                        || !MatchesWitness(bannedPattern, witness))
                    {
                        continue;
                    }

                    diagnostics.Add(Error(DiagnosticCodes.GuardrailRequiresForbiddenToken, guardrail.Path,
                        $"Guardrail '{guardrail.Name}' can never pass: line {requiredLine} REQUIRES " +
                        $"'{ClauseExcerpt(witness)}' (-notmatch, so its absence fails) and line {forbiddenLine} " +
                        $"FORBIDS '{ClauseExcerpt(bannedPattern)}' (-match, so its presence fails), both over " +
                        $"${subject}. The required text MATCHES the forbidden pattern, so no file can satisfy " +
                        $"both — removing it fails the first clause, keeping it fails the second. Every attempt " +
                        $"fails identically with coherent, actionable and WRONG feedback, and the task dead-ends " +
                        $"at needs-human having never been achievable. Fix per the catalogue's two-variable rule " +
                        $"(#470): run the FORBIDDEN scan over STRIPPED source — comments AND string literals, " +
                        $"since #97/#98 strips only comments — and anchor the ban on a USE (a dotted call, a type " +
                        $"position) rather than a bare mention (#76), while the REQUIRED clause keeps reading the " +
                        $"comment-stripped text. Stripping literals for BOTH clauses makes the required one " +
                        $"unsatisfiable, which is the same dead-end wearing the other polarity."));
                }
            }
        }
    }

    /// <summary>
    /// Does the block opened at <paramref name="openBrace"/> FAIL the guardrail? Brace-matched in plain
    /// text, so a <c>{</c> inside a string literal can leave the block unbalanced — in which case the
    /// answer is NO. Silence beats a guess: a mis-read block that ran to end-of-file would pick up some
    /// other clause's failure signal and invert this clause's polarity.
    /// </summary>
    private static bool BranchFailsTheGuardrail(string text, int openBrace)
    {
        int depth = 0;
        for (int i = openBrace; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
                continue;
            }

            if (text[i] != '}')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return ClauseFailsTheGuardrail.IsMatch(text[openBrace..(i + 1)]);
            }
        }

        return false;
    }

    /// <summary>
    /// The exact text every file satisfying <paramref name="pattern"/> must contain, or <c>null</c> when the
    /// pattern does not pin one — see the bounded subset documented on
    /// <see cref="ValidateGuardrailRequiresForbiddenToken"/>.
    /// </summary>
    private static string? TryLiteralWitness(string pattern)
    {
        int i = 0;

        // Leading inline option groups — (?i), (?m), (?is) — change matching, never the text matched.
        while (i + 2 < pattern.Length && pattern[i] == '(' && pattern[i + 1] == '?')
        {
            int close = i + 2;
            while (close < pattern.Length && "imsxn-".Contains(pattern[close], StringComparison.Ordinal))
            {
                close++;
            }

            if (close == i + 2 || close >= pattern.Length || pattern[close] != ')')
            {
                break;
            }

            i = close + 1;
        }

        if (i < pattern.Length && pattern[i] == '^')
        {
            i++;                                                    // zero-width start anchor
        }

        int end = pattern.Length;
        if (end > i && pattern[end - 1] == '$' && (end - 2 < i || pattern[end - 2] != '\\'))
        {
            end--;                                                  // zero-width end anchor
        }

        StringBuilder witness = new();
        while (i < end)
        {
            char c = pattern[i];
            if (c != '\\')
            {
                if (RegexMetacharacters.Contains(c, StringComparison.Ordinal))
                {
                    return null;
                }

                witness.Append(c);
                i++;
                continue;
            }

            if (i + 1 >= end)
            {
                return null;
            }

            char escaped = pattern[i + 1];
            i += 2;

            if (escaped == 'b')
            {
                continue;                                           // zero-width word boundary
            }

            if (escaped == 's')
            {
                char quantifier = i < end ? pattern[i] : '\0';
                if (quantifier is '*' or '?')
                {
                    i++;                                            // zero whitespace is a valid witness
                    continue;
                }

                if (quantifier == '+')
                {
                    i++;
                }

                witness.Append(' ');
                continue;
            }

            if (char.IsAsciiLetterOrDigit(escaped))
            {
                return null;                                        // \w \d \S \n \t \1 …
            }

            witness.Append(escaped);                                // escaped punctuation is itself
        }

        return witness.ToString();
    }

    /// <summary>
    /// Does <paramref name="pattern"/>, compiled from the PLAN's own text, match <paramref name="witness"/>?
    /// A pattern that is not a valid regex, or that times out, answers NO — <c>validate</c> is read-only and
    /// must degrade rather than throw over a plan author's typo (GR2056's precedent; issue #487).
    /// </summary>
    private static bool MatchesWitness(string pattern, string witness)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, ClauseMatchTimeout).IsMatch(witness);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Does the pattern anchor to the input/line boundary? Such a pattern cannot be soundly tested against a
    /// standalone witness, because in a real file the required text is embedded in surrounding context.
    /// A negated character class <c>[^…]</c> is caught by the same sweep and skipped too — over-caution in
    /// the safe direction.
    /// </summary>
    private static bool HasInputAnchor(string pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '\\')
            {
                if (pattern[i] is '^' or '$')
                {
                    return true;
                }

                continue;
            }

            if (i + 1 < pattern.Length && "AZzG".Contains(pattern[i + 1], StringComparison.Ordinal))
            {
                return true;
            }

            i++;
        }

        return false;
    }

    /// <summary>1-based line number of <paramref name="index"/> within <paramref name="text"/>.</summary>
    private static int LineNumberAt(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <summary>Keep a quoted clause excerpt short enough to read inside a diagnostic sentence.</summary>
    private static string ClauseExcerpt(string text) =>
        text.Length <= 120 ? text : string.Concat(text.AsSpan(0, 117), "...");

    /// <summary>
    /// Every SCRIPT guardrail across the four folders at all three scopes — task <c>guardrails/</c>+
    /// <c>preflights/</c>, wave <c>guardrails/</c>+<c>preflights/</c>, plan <c>guardrails/</c>+
    /// <c>preflights/</c> — the uniform enumeration the four-folder checks use. <c>plan.Tasks</c> is
    /// flattened across waves (so waved TASK folders are covered by the task loop); only the
    /// wave-LEVEL folders need the separate <c>plan.Waves</c> loop. Prompt guardrails are excluded
    /// (they are prose, not a regex construction).
    /// </summary>
    private static IEnumerable<GuardrailDefinition> FourFolderScriptGuardrails(PlanDefinition plan)
    {
        foreach (TaskNode task in plan.Tasks)
        {
            foreach (GuardrailDefinition guardrail in ScriptGuardrails(task.Guardrails))
            {
                yield return guardrail;
            }

            foreach (GuardrailDefinition guardrail in ScriptGuardrails(task.Preflights))
            {
                yield return guardrail;
            }
        }

        foreach (WaveNode wave in plan.Waves)
        {
            foreach (GuardrailDefinition guardrail in ScriptGuardrails(wave.Guardrails))
            {
                yield return guardrail;
            }

            foreach (GuardrailDefinition guardrail in ScriptGuardrails(wave.Preflights))
            {
                yield return guardrail;
            }
        }

        foreach (GuardrailDefinition guardrail in ScriptGuardrails(plan.PlanPreflights))
        {
            yield return guardrail;
        }

        foreach (GuardrailDefinition guardrail in ScriptGuardrails(plan.PlanGuardrails))
        {
            yield return guardrail;
        }
    }

    private static IEnumerable<GuardrailDefinition> ScriptGuardrails(IReadOnlyList<GuardrailDefinition> guardrails) =>
        guardrails.Where(g => g.Kind == ActionKind.Script);

    private static void ValidateDependencies(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        var ids = plan.Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (TaskNode task in plan.Tasks)
        {
            foreach (string dependency in task.DependsOn)
            {
                if (!ids.Contains(dependency))
                {
                    diagnostics.Add(Error(DiagnosticCodes.UnknownDependency, task.Directory,
                        $"Task '{task.Id}' dependsOn '{dependency}', which is not a known task id."));
                }
            }
        }
    }

    /// <summary>
    /// A guardrail or script-action body that reads another task's state namespace in the canonical
    /// state-access form declares a *runtime read dependency* on that producer. Because the scheduler
    /// orders only on <c>dependsOn</c>, a consumer that reads <c>$state.'&lt;id&gt;'</c> without a
    /// dependency path to <c>&lt;id&gt;</c> can run before the producer — the read returns null and
    /// the guardrail fails at runtime as <c>needs-human</c> (the real <c>46</c>→<c>35</c> cascade,
    /// issue #121). GR2022 turns this into a load-time ERROR: every referenced task id that is a real
    /// task and is not the referencing task's own id MUST be a transitive <c>dependsOn</c> ancestor —
    /// OR be satisfied by the pre-existing baseline, i.e. <c>state/seed.json</c> carries a top-level
    /// key exactly equal to that id (§6.2/§6.3). The check is scoped to the canonical state-key SHAPE
    /// — the form single-writer-per-key namespacing makes deterministic (the producer of key
    /// <c>'&lt;id&gt;'</c> is exactly task <c>&lt;id&gt;</c>) — so an id matching no task, or a quoted
    /// string outside a <c>state</c> access, is ignored: zero false positives. Produced-file
    /// references are NOT linted in v1 (no deterministic producer→artifact map exists). Skipped when a
    /// cycle was found (the ancestor closure is unreliable on a graph that already failed GR2007).
    /// </summary>
    private static void ValidateCrossTaskStateReferences(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        // A cycle (GR2007) makes the dependency closure meaningless — skip to avoid noise on a plan
        // that already fails validation for a more fundamental reason.
        var graph = new DependencyGraph(plan.Tasks);
        if (graph.FindCycle() is not null)
        {
            return;
        }

        var taskIds = new HashSet<string>(plan.Tasks.Select(t => t.Id), StringComparer.Ordinal);
        var seedKeys = ReadSeedTopLevelKeys(plan.PlanDirectory);

        // Wave-aware branch (SSOT §14.2, GR2022): in a waved plan a cross-task state read whose producer is
        // in an EARLIER wave is satisfied by the wave barrier (the earlier wave provably ran first); a
        // SAME-wave read still needs the dependsOn ancestor (the existing rule); a LATER-wave read is an
        // error (not yet produced). Flat plans have no waves → all these maps are empty and the branch is inert.
        var tasksById = plan.Tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var waveOrdinal = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < plan.Waves.Count; i++)
        {
            waveOrdinal[plan.Waves[i].Dir] = i;
        }

        foreach (TaskNode task in plan.Tasks)
        {
            IReadOnlySet<string> ancestors = graph.TransitiveDependenciesOf(task.Id);

            // The action body (script actions only — a prompt action is not a deterministic script
            // and its "references" are prose, not a state deref) plus every guardrail body.
            if (task.Action.Kind == ActionKind.Script)
            {
                CheckBody(task, task.Action.Path, ancestors);
            }

            foreach (GuardrailDefinition guardrail in task.Guardrails)
            {
                CheckBody(task, guardrail.Path, ancestors);
            }
        }

        void CheckBody(TaskNode task, string bodyPath, IReadOnlySet<string> ancestors)
        {
            string body;
            try
            {
                body = File.ReadAllText(bodyPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return; // unreadable body — a structural problem the loader/other checks surface
            }

            // De-dup so a body referencing the same producer twice yields one diagnostic.
            var reported = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match match in StateKeyReference.Matches(body))
            {
                string referencedId = match.Groups["id"].Value;

                // Only flag a reference to a REAL OTHER task: an id matching no task is not a
                // cross-task reference (could be any quoted string), and a self-reference is always
                // satisfiable (the task writes its own namespace).
                if (!taskIds.Contains(referencedId) || referencedId == task.Id)
                {
                    continue;
                }

                // Wave-aware branch (SSOT §14.2): a cross-WAVE read is governed by the barrier, not a
                // dependsOn edge (which cannot cross waves, GR2034). An earlier-wave producer is satisfied;
                // a later-wave producer is a hard error (not yet run when this task reads it).
                if (task.WaveDir is { } myWave && tasksById.TryGetValue(referencedId, out TaskNode? producer) &&
                    producer.WaveDir is { } refWave && !string.Equals(refWave, myWave, StringComparison.Ordinal))
                {
                    if (waveOrdinal.TryGetValue(refWave, out int refOrd) &&
                        waveOrdinal.TryGetValue(myWave, out int myOrd) && refOrd < myOrd)
                    {
                        continue; // earlier wave — satisfied by the barrier (SSOT §14.2).
                    }

                    if (!reported.Add(referencedId))
                    {
                        continue;
                    }

                    diagnostics.Add(Error(DiagnosticCodes.CrossTaskStateReferenceWithoutDependency, bodyPath,
                        $"Task '{task.Id}' reads state key '{referencedId}', produced by a task in a LATER wave " +
                        $"('{refWave}') that has not run yet when this task runs. A wave never reads a later " +
                        "wave's output — reorder the producing task into this wave or an earlier one (SSOT §14.2)."));
                    continue;
                }

                // Satisfied by a dependency edge or by a pre-existing seed top-level key.
                if (ancestors.Contains(referencedId) || seedKeys.Contains(referencedId))
                {
                    continue;
                }

                if (!reported.Add(referencedId))
                {
                    continue;
                }

                diagnostics.Add(Error(DiagnosticCodes.CrossTaskStateReferenceWithoutDependency, bodyPath,
                    $"Task '{task.Id}' reads state key '{referencedId}' (produced by task '{referencedId}') " +
                    "but declares no dependsOn path to it, and no seed.json top-level key provides it. " +
                    "The scheduler may run this task before its producer, so the state read returns null " +
                    $"and the guardrail fails at runtime as needs-human. Add '{referencedId}' to this task's " +
                    "dependsOn (directly or transitively) so the producer always runs first (SSOT §6.2)."));
            }
        }
    }

    /// <summary>
    /// Matches the canonical state-key access shapes (case-sensitive on the <c>state</c> token),
    /// capturing the quoted task id: <c>$state.'&lt;id&gt;'</c>, <c>$state."&lt;id&gt;"</c> (PowerShell
    /// property access), and <c>state['&lt;id&gt;']</c> / <c>state["&lt;id&gt;"]</c> (bracket index,
    /// JS/Python/jq idioms). The id is any non-quote run, validated against real task ids by the caller.
    /// </summary>
    private static readonly Regex StateKeyReference = new(
        """(?<![\w$])\$?state\s*(?:\.\s*'(?<id>[^']+)'|\.\s*"(?<id>[^"]+)"|\[\s*'(?<id>[^']+)'\s*\]|\[\s*"(?<id>[^"]+)"\s*\])""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Read the top-level object keys of <c>&lt;planDirectory&gt;/state/seed.json</c> (comment- and
    /// trailing-comma-tolerant, matching the committed-manifest convention). Returns an empty set when
    /// the file is absent, unreadable, or not a JSON object — the reference then simply isn't seed-satisfied.
    /// </summary>
    private static HashSet<string> ReadSeedTopLevelKeys(string planDirectory)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string seedPath = Path.Combine(planDirectory, "state", "seed.json");
        if (!File.Exists(seedPath))
        {
            return keys;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(seedPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in doc.RootElement.EnumerateObject())
                {
                    keys.Add(property.Name);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A malformed/unreadable seed simply provides no exemption keys.
        }

        return keys;
    }

    /// <summary>
    /// Stale-coverage WARNING (GR2026, issue #157 §1). For each task, locate its
    /// <c>covers-key-behaviors</c>-style script guardrail (the archetype that greps a test file for
    /// distinctive literal terms — recognised by a <c>$hits -lt N</c> threshold or the canonical
    /// guardrail name, see <see cref="CoverageGuardrailHeuristic"/>), extract its required tokens, and
    /// cross-reference each against the SAME task's action body text (case-insensitive keyword
    /// presence). A token the action prompt never mentions is almost certainly STALE — the prompt was
    /// edited (a scenario removed, scope narrowed) without updating the guardrail — so a correct
    /// implementation following the prompt can never satisfy it and the task dead-ends at needs-human
    /// on every attempt.
    ///
    /// <para>This is a HEURISTIC, never an error: it fires ONLY when the archetype and a clear literal
    /// keyword are both confidently identified (the extraction is conservative — quoted, metachar-free,
    /// ≥3-char literals on a <c>-match</c>/<c>-notmatch</c> against the scanned content variable). Its
    /// limits: surface keyword presence in the prose is a strong signal but not a proof — a token named
    /// only in a synonym, or mentioned in an unrelated sentence, can produce a false negative or
    /// positive; when in doubt the heuristic stays silent. A guardrail body that cannot be read is
    /// skipped (other checks surface the structural problem).</para>
    /// </summary>
    private static void ValidateStaleCoverageTokens(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (TaskNode task in plan.Tasks)
        {
            // Cross-reference is against this task's action body text (the action prompt, typically
            // action.prompt.md). If the action is unreadable, skip — nothing to compare against.
            string? actionText = TryReadAllText(task.Action.Path);
            if (actionText is null)
            {
                continue;
            }

            foreach (GuardrailDefinition guardrail in task.Guardrails)
            {
                // Only script guardrails carry the covers-key-behaviors archetype (a prompt guardrail
                // is prose, not a `$content -match` grep).
                if (guardrail.Kind != ActionKind.Script)
                {
                    continue;
                }

                string? guardrailBody = TryReadAllText(guardrail.Path);
                if (guardrailBody is null)
                {
                    continue;
                }

                IReadOnlyList<string> tokens =
                    CoverageGuardrailHeuristic.ExtractCoverageTokens(guardrailBody, guardrail.Name);

                foreach (string token in tokens)
                {
                    if (!ActionMentions(actionText, token))
                    {
                        diagnostics.Add(Warning(DiagnosticCodes.StaleCoverageToken, guardrail.Path,
                            $"Task '{task.Id}' guardrail '{guardrail.Name}' requires the coverage token " +
                            $"'{token}', but the task's action prompt never mentions it. If the prompt was " +
                            "edited (a scenario removed or scope narrowed) without updating this guardrail, " +
                            "the token is stale: a correct implementation following the prompt can never " +
                            "satisfy the guardrail, so the task will fail every attempt and dead-end at " +
                            "needs-human. Remove or update the token in the guardrail, or add the behavior " +
                            "back to the action prompt (heuristic WARNING — SSOT §4, issue #157)."));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Case-insensitive keyword-presence test: does <paramref name="actionText"/> mention
    /// <paramref name="token"/> bounded by non-alphanumerics? An alphanumeric boundary (rather than a
    /// raw substring) avoids a spurious match inside an unrelated longer identifier (token
    /// <c>ProcessId</c> must not match <c>ProcessIdentifier</c>) while still finding the token in
    /// prose, in punctuation (<c>XtcFileOnly.</c>, <c>(TcApiLocal)</c>), or in dotted/qualified code.
    /// The token is metachar-free (the heuristic only extracts clear keywords) but is regex-escaped
    /// defensively before matching.
    /// </summary>
    private static bool ActionMentions(string actionText, string token)
    {
        string pattern = $@"(?<![A-Za-z0-9]){Regex.Escape(token)}(?![A-Za-z0-9])";
        return Regex.IsMatch(actionText, pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>Read a file's text, or null when it is missing/unreadable (a structural problem other checks surface).</summary>
    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void ValidateGuardrailsPresent(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (TaskNode task in plan.Tasks)
        {
            if (task.Guardrails.Count == 0)
            {
                diagnostics.Add(Error(DiagnosticCodes.NoGuardrails, task.Directory,
                    $"Task '{task.Id}' has zero guardrails; a task that cannot be verified is not allowed."));
            }
        }
    }

    /// <summary>
    /// Prompt-runner integrity (SSOT §2/§9). A plan with ANY prompt action or prompt
    /// guardrail must declare at least one runner under <c>promptRunners</c> (GR2008,
    /// because nothing could run those prompts). A prompt action that names a runner
    /// (<c>action.runner</c>) must name a declared one (GR2004). A prompt action/guardrail
    /// that relies on the default must have a usable default — either <c>promptRunners.default</c>
    /// resolves to a config, or there is exactly one declared runner to fall back to (GR2004).
    /// </summary>
    private static void ValidatePromptRunners(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        bool hasPrompts = HasAnyPrompt(plan);
        if (!hasPrompts)
        {
            return;
        }

        if (plan.Config.PromptRunners.Count == 0)
        {
            diagnostics.Add(Error(DiagnosticCodes.NoPromptRunners, plan.PlanDirectory,
                "Plan has prompt action(s)/guardrail(s) but no 'promptRunners' configuration to run them. " +
                "Add a promptRunners block to guardrails.json (SSOT §2)."));
            return;
        }

        // Explicit runner references on prompt actions must resolve.
        foreach (TaskNode task in plan.Tasks)
        {
            if (task.Action.Kind == ActionKind.Prompt && task.Action.Runner is not null &&
                !plan.Config.PromptRunnerNames.Contains(task.Action.Runner))
            {
                diagnostics.Add(Error(DiagnosticCodes.UnknownPromptRunner, task.Action.Path,
                    $"Task '{task.Id}' references prompt runner '{task.Action.Runner}', which is not declared in promptRunners."));
            }
        }

        // A prompt that relies on the default needs a resolvable default. The default is
        // promptRunners.default, falling back to the sole declared runner if exactly one.
        bool anyReliesOnDefault = plan.Tasks.Any(t =>
            (t.Action.Kind == ActionKind.Prompt && t.Action.Runner is null) ||
            t.Guardrails.Any(g => g.Kind == ActionKind.Prompt) ||
            t.Preflights.Any(g => g.Kind == ActionKind.Prompt)) ||
            plan.PlanPreflights.Any(g => g.Kind == ActionKind.Prompt) ||
            plan.PlanGuardrails.Any(g => g.Kind == ActionKind.Prompt);

        if (anyReliesOnDefault && ResolveDefaultRunner(plan.Config) is null)
        {
            diagnostics.Add(Error(DiagnosticCodes.UnknownPromptRunner, plan.PlanDirectory,
                "A prompt action/guardrail relies on the default prompt runner, but no default is resolvable. " +
                "Set promptRunners.default to a declared runner (or declare exactly one runner)."));
        }
    }

    /// <summary>The default runner name: <c>promptRunners.default</c> if it resolves, else the sole declared runner.</summary>
    private static string? ResolveDefaultRunner(RunConfig config)
    {
        if (config.DefaultPromptRunner is { } named && config.PromptRunnerNames.Contains(named))
        {
            return named;
        }

        return config.PromptRunnerNames.Count == 1 ? config.PromptRunnerNames.Single() : null;
    }

    /// <summary>
    /// Probe each DECLARED prompt runner's <c>command</c> on PATH (reusing the same
    /// <see cref="IExecutableProbe"/> as interpreter resolution). An unresolvable command is a
    /// WARNING (GR2009), not an error: the plan may have been authored to run on another
    /// machine where the runner is installed. Every declared runner is probed even if no task
    /// currently references it — a stale runner config is worth surfacing. Runs only after the
    /// GR2008 error path (no runners at all) has been handled by <see cref="ValidatePromptRunners"/>.
    /// </summary>
    private void ValidatePromptRunnerCommands(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (PromptRunnerConfig runner in plan.Config.PromptRunners.Values
                     .OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            if (!_probe.Exists(runner.Command))
            {
                diagnostics.Add(Warning(DiagnosticCodes.PromptRunnerNotOnPath, plan.PlanDirectory,
                    $"Prompt runner '{runner.Name}' command '{runner.Command}' is not resolvable on PATH. " +
                    "Prompt tasks using this runner will fail unless it is installed on the machine that runs the plan."));
            }
        }
    }

    /// <summary>
    /// For every distinct extension used by a *script* action or guardrail, probe the
    /// interpreter. A used deterministic extension with no resolvable interpreter is an
    /// ERROR in M2 (we cannot run it). Prompt actions/guardrails validate fine here — they
    /// are run by a prompt runner, not the interpreter map (M5).
    /// </summary>
    private void ValidateInterpreters(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        var interpreterMap = new InterpreterMap(_probe, plan.Config.Interpreters);

        // Distinct (extension, first-seen file) so each extension is reported once with a
        // concrete example path.
        var seenExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string scriptPath in ScriptFiles(plan))
        {
            string extension = Path.GetExtension(scriptPath).ToLowerInvariant();
            if (!seenExtensions.Add(extension))
            {
                continue;
            }

            InterpreterMap.Resolution resolution = interpreterMap.Resolve(scriptPath, []);
            switch (resolution.Status)
            {
                case InterpreterMap.Status.WrongPlatform:
                    diagnostics.Add(Error(DiagnosticCodes.InterpreterWrongPlatform, scriptPath,
                        $"Extension '{extension}' is only supported on Windows."));
                    break;
                case InterpreterMap.Status.NotOnPath:
                    string probed = string.Join("' / '", resolution.ProbedExecutables);
                    diagnostics.Add(Error(DiagnosticCodes.UnresolvableInterpreter, scriptPath,
                        $"No interpreter for extension '{extension}' is resolvable on PATH (tried '{probed}')."));
                    break;
            }
        }
    }

    private static IEnumerable<string> ScriptFiles(PlanDefinition plan)
    {
        foreach (TaskNode task in plan.Tasks)
        {
            if (task.Action.Kind == ActionKind.Script)
            {
                yield return task.Action.Path;
            }

            foreach (string path in ScriptGuardrailPaths(task.Guardrails))
            {
                yield return path;
            }

            foreach (string path in ScriptGuardrailPaths(task.Preflights))
            {
                yield return path;
            }
        }

        // The plan-level folders' scripts need a resolvable interpreter too (SSOT §4/§5.2).
        foreach (string path in ScriptGuardrailPaths(plan.PlanPreflights))
        {
            yield return path;
        }

        foreach (string path in ScriptGuardrailPaths(plan.PlanGuardrails))
        {
            yield return path;
        }
    }

    private static IEnumerable<string> ScriptGuardrailPaths(IReadOnlyList<GuardrailDefinition> guardrails) =>
        guardrails.Where(g => g.Kind == ActionKind.Script).Select(g => g.Path);

    /// <summary>
    /// <b>`planIsClosed`</b> (doc 19 §3.3) — the plan has no declared wave folder with zero tasks, so its
    /// declaration set is COMPLETE and nothing further is expected to be authored. Trivially true for a flat
    /// plan: there is no JIT breakdown, so nothing is pending by construction.
    /// <para>It is the shared suppressor for the producer-coverage family. GR2062 uses it here; GR2060 (doc
    /// 19 §3.1, reserved, not built) uses the same predicate for the same reason — while an un-authored wave
    /// stub exists a shortfall is EXPECTED (that IS the #365 one-ahead invariant working) and a warning that
    /// fired then would be ignored within a week.</para>
    /// </summary>
    private static bool PlanIsClosed(PlanDefinition plan) => plan.Waves.All(w => w.Tasks.Count > 0);

    /// <summary>
    /// GR2062 (issue #477, doc 19 §3.2, SSOT §2/§14.1) — the plan INTENDS more waves than it DECLARES while
    /// every declared wave is authored, so the #365 one-ahead invariant is not pending but GONE. The other
    /// polarity (fewer intended than declared — the plan grew past its stated intent) warns with the same
    /// code.
    /// <para><b>Two conjuncts, both load-bearing.</b> <c>intendedWaves</c> ABSENT ⇒ skipped entirely (the
    /// field is optional; no plan is forced to migrate). <see cref="PlanIsClosed"/> FALSE ⇒ silent, which is
    /// what keeps it quiet through every healthy JIT mid-plan state.</para>
    /// <para><b>A WARNING, never an error.</b> A genuinely final wave has no successor and an author may
    /// legitimately collapse waves. The value is not enforcement — it is that a missing wave becomes
    /// NAMEABLE, which it was not: the count lived in a charter that is a sibling of the plan folder with no
    /// reference from inside it, and <c>diagram.md</c> is regenerated FROM the folders so it can never
    /// disagree with them.</para>
    /// </summary>
    private static void ValidateIntendedWaves(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        if (plan.Config.IntendedWaves is not { } intended || !PlanIsClosed(plan))
        {
            return;
        }

        int declared = plan.Waves.Count;
        if (intended == declared)
        {
            return;
        }

        // A FLAT plan reaches here only when the author explicitly wrote a waved-plans-only key into a plan
        // that has no waves; saying "declares 0" would be arithmetically true and useless, so name the shape.
        string shortfall = plan.IsWaved
            ? $"declares {declared} wave folder(s)"
            : "declares NO wave folders at all — it is a FLAT plan (SSOT §14.1), where 'intendedWaves' has "
              + "no waves to describe";

        string diagnosis = intended > declared
            ? "Every declared wave is authored, so this is not the one-ahead invariant pending (#365) — the "
              + "wave that should be next is GONE. A lost wave stub leaves no forward reference to trip over "
              + "the way a lost task does: validate, 'graph --check' and review all stay clean, and the run "
              + "drains the whole DAG before the terminal gate fails on a wave that was never authored."
            : "The plan grew past its stated intent. If the extra wave(s) are deliberate, raise "
              + "'intendedWaves'; if not, they were added without the decision that should precede a wave.";

        diagnostics.Add(Warning(DiagnosticCodes.IntendedWaveNotDeclared, plan.PlanDirectory,
            $"'guardrails.json' sets \"intendedWaves\": {intended}, but the plan {shortfall}. "
            + diagnosis
            + " Correct the wave folders, or update 'intendedWaves' to the count that actually holds."));
    }

    /// <summary>
    /// GR2063 (issue #402, SSOT §14.11) — a wave whose breakdown DECLARED more tasks than it AUTHORED. A
    /// truncated breakdown leaves a valid PREFIX whose debt is not computable from the prefix, so the
    /// breakdown declares its decomposition first, in <c>&lt;wave&gt;/state/breakdown-intent.json</c>, and
    /// this is the set-compare against it.
    /// <para>Absent / unparseable / satisfied manifest ⇒ SILENT (the GR2062 rule). A WARNING, because the
    /// enforcement that matters is the harness routing on the CODE — a human hand-finishing a wave with
    /// fewer tasks than declared is nudged, not blocked.</para>
    /// <para>GR2064 is the fourth case, and it is NOT silent: a manifest that exists and PARSES but yields
    /// no usable folder disables the very salvage it was written to enable, and read through
    /// <c>TryRead</c> alone it is indistinguishable from an absent one — so one typo cost the whole
    /// mechanism with no diagnostic at all. Hence the single <see cref="BreakdownIntent.Read"/> here: one
    /// file read, four distinguishable outcomes.</para>
    /// </summary>
    private static void ValidateWaveBreakdownIntent(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (WaveNode wave in plan.Waves)
        {
            BreakdownIntentRead read = BreakdownIntent.Read(wave.Directory);
            if (read.Presence == BreakdownIntentPresence.NoUsableEntries)
            {
                diagnostics.Add(Warning(DiagnosticCodes.BreakdownIntentDeclaresNothing, read.Path,
                    $"Wave '{wave.Dir}' carries a '{BreakdownIntent.FileName}' manifest that "
                    + $"{read.Explanation}, so the truncation salvage the manifest exists to enable is "
                    + "DISABLED for this wave: a cut-off breakdown here is quarantined rather than resumed, "
                    + "and GR2063 can never report the shortfall."
                    + (read.RejectedEntries.Count == 0
                        ? ""
                        : " Rejected: " + string.Join("; ", read.RejectedEntries) + ".")
                    + $" Correct the 'folder' values so each names a folder directly under '{wave.Dir}/tasks/', "
                    + $"or delete '{wave.Dir}/state/{BreakdownIntent.FileName}' if this wave declares no intent."));
                continue;
            }

            if (read.Usable is not { } intent)
            {
                continue;
            }

            IReadOnlyList<string> missing = intent.MissingFolders(wave.Directory);
            if (missing.Count == 0)
            {
                continue;
            }

            int declared = intent.DeclaredFolders().Count;
            diagnostics.Add(Warning(DiagnosticCodes.WaveBreakdownIncomplete, wave.Directory,
                $"Wave '{wave.Dir}' declared {declared} task(s) in '{BreakdownIntent.FileName}' but "
                + $"{missing.Count} have no complete task folder: {string.Join(", ", missing)}. The breakdown "
                + "was cut off before finishing; the valid prefix is preserved and the JIT checkpoint resumes "
                + $"it on the next 'guardrails run'. If the wave is finished as-is, correct or delete "
                + $"'{wave.Dir}/state/{BreakdownIntent.FileName}' to record the intent that actually holds."));
        }
    }

    private static Diagnostic Error(string code, string path, string message) => new()
    {
        Code = code,
        Severity = DiagnosticSeverity.Error,
        Path = path,
        Message = message
    };

    private static Diagnostic Warning(string code, string path, string message) => new()
    {
        Code = code,
        Severity = DiagnosticSeverity.Warning,
        Path = path,
        Message = message
    };
}
