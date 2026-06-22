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
        ValidateGuardrailsPresent(plan, diagnostics);
        ValidateIntegrationGatePresent(plan, diagnostics);
        ValidateIntegrationGateNonEmpty(plan, diagnostics);
        ValidateGuardrailScopeValues(plan, diagnostics);
        ValidateWriteScopes(plan, diagnostics);
        ValidatePromptRunners(plan, diagnostics);
        ValidatePromptRunnerCommands(plan, diagnostics);
        ValidatePromptRunnerOutputCaps(plan, diagnostics);
        ValidateInterpreters(plan, diagnostics);

        return diagnostics;
    }

    private static bool HasAnyPrompt(PlanDefinition plan) =>
        plan.Tasks.Any(t =>
            t.Action.Kind == ActionKind.Prompt ||
            t.Guardrails.Any(g => g.Kind == ActionKind.Prompt));

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
    /// A plan with a parallel topology — ≥2 leaf tasks (tasks with no dependents) or any fan-in
    /// task (a task with ≥2 upstreams) — must declare exactly one <c>integrationGate:true</c> sink
    /// (plan 08 M2, SSOT §3.3) — but ONLY in worktree mode (<c>maxParallelism &gt; 1</c>), per the
    /// PO decision. The terminal gate verifies the merged union of *parallel* branches; a SERIAL
    /// run (<c>maxParallelism == 1</c>) uses the shared workspace and has no parallel branches to
    /// merge, so the hard requirement does not apply and GR2017 is not emitted. In worktree mode,
    /// omitting the gate leaves parallel branches unverified at the integration level → GR2017 ERROR.
    /// </summary>
    private static void ValidateIntegrationGatePresent(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        // Gate required only in worktree mode (maxParallelism>1), PO decision; a serial run uses the
        // shared workspace and merges no parallel branches, so there is nothing for the gate to verify.
        if (plan.Config.MaxParallelism <= 1)
        {
            return;
        }

        if (plan.Tasks.Any(t => t.IntegrationGate))
        {
            return;
        }

        var dependedOn = new HashSet<string>(plan.Tasks.SelectMany(t => t.DependsOn), StringComparer.Ordinal);
        int leafCount = plan.Tasks.Count(t => !dependedOn.Contains(t.Id));
        bool hasMultipleLeaves = leafCount >= 2;
        bool hasFanIn = plan.Tasks.Any(t => t.DependsOn.Count >= 2);

        if (hasMultipleLeaves || hasFanIn)
        {
            diagnostics.Add(Error(DiagnosticCodes.MissingIntegrationGate, plan.PlanDirectory,
                "Plan has a parallel topology (≥2 leaf tasks or a fan-in task) but no " +
                "integrationGate:true sink. The terminal gate is the whole-repo soundness boundary " +
                "for parallel execution; add a final task with integrationGate:true and at least one " +
                "guardrail declaring scope:\"integration\" (SSOT §3.3, plan 08 §3)."));
        }
    }

    /// <summary>
    /// Every <c>integrationGate:true</c> sink must carry at least one guardrail with
    /// <c>scope:"integration"</c> (plan 08 M2, SSOT §3.3/§4.3) — but ONLY in worktree mode
    /// (<c>maxParallelism &gt; 1</c>), per the PO decision. The integration gate verifies the
    /// merged union of *parallel* branches; a SERIAL run (<c>maxParallelism == 1</c>) uses the
    /// shared workspace and merges no parallel branches, so the requirement does not apply and
    /// GR2018 is not emitted. In worktree mode, an empty integration-guardrail set provides no
    /// whole-repo soundness check — a gate that verifies nothing → GR2018 ERROR.
    /// </summary>
    private static void ValidateIntegrationGateNonEmpty(PlanDefinition plan, List<Diagnostic> diagnostics)
    {
        // Gate verification required only in worktree mode (maxParallelism>1), PO decision; a serial
        // run merges no parallel branches, so there is nothing for the gate to verify.
        if (plan.Config.MaxParallelism <= 1)
        {
            return;
        }

        foreach (TaskNode task in plan.Tasks)
        {
            if (!task.IntegrationGate)
            {
                continue;
            }

            bool hasIntegrationGuardrail = task.Guardrails.Any(g =>
                string.Equals(g.Scope, "integration", StringComparison.OrdinalIgnoreCase));

            if (!hasIntegrationGuardrail)
            {
                diagnostics.Add(Error(DiagnosticCodes.IntegrationGateEmpty, task.Directory,
                    $"Task '{task.Id}' is an integrationGate:true sink but has no guardrail with " +
                    "scope:\"integration\". The terminal gate must carry at least one integration-scoped " +
                    "guardrail to be a sound soundness boundary. Add a guardrail sidecar declaring " +
                    "scope:\"integration\" (SSOT §3.3/§4.3, plan 08 §3)."));
            }
        }
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
        foreach (TaskNode task in plan.Tasks)
        {
            foreach (GuardrailDefinition guardrail in task.Guardrails)
            {
                if (guardrail.Scope is null)
                    continue;

                if (guardrail.Scope != "integration" && guardrail.Scope != "local")
                {
                    diagnostics.Add(Error(DiagnosticCodes.InvalidGuardrailScopeValue, guardrail.Path,
                        $"Guardrail '{guardrail.Name}' in task '{task.Id}' has unrecognised scope value " +
                        $"'{guardrail.Scope}'. The only recognised values are 'integration' and 'local'. " +
                        "An unrecognised value silently degrades to 'local' at runtime, dropping the " +
                        "guardrail from the integration union re-verify set (SSOT §4.3, plan 08 §3)."));
                }
            }
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
            t.Guardrails.Any(g => g.Kind == ActionKind.Prompt));

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

            foreach (GuardrailDefinition guardrail in task.Guardrails)
            {
                if (guardrail.Kind == ActionKind.Script)
                {
                    yield return guardrail.Path;
                }
            }
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
