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

    public PlanValidator(IExecutableProbe probe) => _probe = probe;

    /// <summary>Validate with the real PATH probe.</summary>
    public PlanValidator() : this(new PathExecutableProbe()) { }

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
        ValidateGuardrailExpectedDurations(plan, diagnostics);
        ValidateDuplicateCheckNames(plan, diagnostics);
        ValidateWriteScopes(plan, diagnostics);
        ValidateStagingOutputs(plan, diagnostics);
        ValidatePromptRunners(plan, diagnostics);
        ValidatePromptRunnerCommands(plan, diagnostics);
        ValidatePromptRunnerOutputCaps(plan, diagnostics);
        ValidateModelValues(plan, diagnostics);
        ValidateInterpreters(plan, diagnostics);

        return diagnostics;
    }

    /// <summary>
    /// The review-marker nudge (GR2025, WARNING — SSOT §13, issue #79): missing/stale review marker.
    /// Deliberately NOT part of <see cref="Validate"/> (which is the pure semantic plan validator —
    /// keeping it out keeps every plan that lacks a marker from being noisy in the harness's own
    /// validation, and keeps the check a deliberate command-layer concern). The <c>validate</c> and
    /// <c>run</c> CLI commands call THIS to surface the same warning; both reuse the one deterministic
    /// <see cref="Review.ReviewMarker.Evaluate"/> computation. Returns null when freshly reviewed.
    /// </summary>
    public static Diagnostic? ReviewMarkerDiagnostic(PlanDefinition plan)
    {
        Review.ReviewEvaluation evaluation = Review.ReviewMarker.Evaluate(plan);
        return evaluation.ShouldWarn && evaluation.NudgeMessage is { } message
            ? Warning(DiagnosticCodes.ReviewMarkerMissingOrStale, plan.PlanDirectory, message)
            : null;
    }

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
            if (runner.Settings.Model is { } baseModel && !IsValidModelValue(baseModel))
            {
                diagnostics.Add(Error(DiagnosticCodes.ModelInvalid, plan.PlanDirectory,
                    $"promptRunners.{runner.Name}.model is empty, whitespace-only, or contains " +
                    "leading/trailing/embedded whitespace or control characters — it must be a real " +
                    "model identifier, or omitted entirely to use the CLI default."));
            }

            if (runner.GuardrailOverrides?.Model is { } overrideModel && !IsValidModelValue(overrideModel))
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

            if (!IsValidModelValue(taskModel))
            {
                diagnostics.Add(Error(DiagnosticCodes.ModelInvalid, task.Directory,
                    $"Task '{task.Id}' action.model is empty, whitespace-only, or contains " +
                    "leading/trailing/embedded whitespace or control characters — it must be a real " +
                    "model identifier, or omitted entirely to inherit the runner's default model."));
            }
        }
    }

    /// <summary>
    /// True when <paramref name="model"/> is a plausible model identifier: non-null, non-empty,
    /// contains no leading/trailing whitespace (a <c>Trim()</c> round-trips it unchanged), and
    /// contains no whitespace or control character anywhere (no real Claude model name is ever
    /// space-separated or carries a stray tab/newline). This is a shape check, not an allow-list —
    /// there is no enumerable set of valid model names to compare against.
    /// </summary>
    private static bool IsValidModelValue(string? model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return false;
        }

        if (model.Trim().Length != model.Length)
        {
            return false; // leading/trailing whitespace
        }

        return !model.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
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
    /// </summary>
    private static bool InvokesIntegrationCommand(string strippedBody)
    {
        foreach (string line in strippedBody.Split('\n'))
        {
            string cleaned = StripQuotedLiterals(line);
            foreach (string segment in SplitIntoStatementSegments(cleaned))
            {
                string trimmed = segment.TrimStart();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                // Discard a segment led by an output builtin — echo/printf/Write-Output "…dotnet test…"
                // MENTIONS a command, it does not invoke one.
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
    /// Validate <c>writeScope</c> entries across all tasks (plan 08 §2/§3.4, SSOT §3.4).
    /// GR2019 ERROR: an entry is an absolute path or contains <c>..</c> (escapes the workspace).
    /// GR2020 WARNING: an entry is vacuous/over-broad (e.g. <c>**</c> or <c>*</c>).
    /// </summary>
    private static void ValidateWriteScopes(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        foreach (TaskNode task in plan.Tasks)
        {
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
