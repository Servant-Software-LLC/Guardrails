using System.Text.Json;
using System.Text.RegularExpressions;
using Guardrails.Core.Model;

namespace Guardrails.Core.Loading;

/// <summary>
/// Loads a plan folder from disk into a <see cref="PlanDefinition"/> (SSOT §1–§4).
/// Responsible for the *structural* concerns: parsing JSON (comments + trailing commas
/// allowed), discovering action files by convention, and resolving guardrails while
/// distinguishing metadata sidecars from real guardrails. Semantic checks (the DAG,
/// interpreter probing) live in <see cref="PlanValidator"/>.
/// </summary>
public sealed class PlanLoader
{
    private const string ConfigFileName = "guardrails.json";
    private const string TasksDirName = "tasks";
    private const string TaskManifestName = "task.json";
    private const string GuardrailsDirName = "guardrails";
    private const string PreflightsDirName = "preflights";
    private const string PromptExtension = ".prompt.md";
    private const string ActionFilePrefix = "action.";

    /// <summary>
    /// The wave-directory convention (SSOT §14.1, Open Decision F): <c>wave-</c>, a numeric prefix (group 1,
    /// load-bearing — drives the strict total order), a hyphen, then a kebab slug (group 2). Anchored.
    /// </summary>
    private static readonly Regex WaveDirPattern =
        new("^wave-([0-9]+)-([a-z0-9-]+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Plan-root subdirectories that are NOT waves and must not be mistaken for a non-conforming wave dir
    /// (GR2033). These are the harness/runtime folders that legitimately sit alongside the wave dirs.
    /// </summary>
    private static readonly IReadOnlySet<string> KnownPlanRootFolders =
        new HashSet<string>(StringComparer.Ordinal) { "state", "logs", "guardrails", "preflights", "captured", "tasks" };

    /// <summary>Load the plan rooted at <paramref name="planDirectory"/>.</summary>
    public PlanLoadResult Load(string planDirectory)
    {
        var diagnostics = new List<Diagnostic>();
        string planDir = Path.GetFullPath(planDirectory);

        if (!Directory.Exists(planDir))
        {
            diagnostics.Add(Error(DiagnosticCodes.MissingFile, planDir, "Plan folder does not exist."));
            return new PlanLoadResult { Diagnostics = diagnostics };
        }

        string configPath = Path.Combine(planDir, ConfigFileName);
        RunConfig? config = LoadConfig(configPath, diagnostics);
        if (config is null)
        {
            return new PlanLoadResult { Diagnostics = diagnostics };
        }

        LoadTasksOrWaves(planDir, diagnostics, out IReadOnlyList<TaskNode> tasks, out IReadOnlyList<WaveNode> waves);

        // Plan-level preflights/guardrails folders (SSOT §1/§4) sit at the plan ROOT, siblings of
        // tasks/. They reuse the SAME guardrail-file parser as a task's guardrails/ — they differ only
        // in WHERE they live and WHEN they run — but the `catches:` declaration is enforced here
        // (GR2027), the canonical malformed-declaration diagnostic for the four-folder model.
        IReadOnlyList<GuardrailDefinition> planPreflights =
            LoadGuardrailsFromFolder(Path.Combine(planDir, PreflightsDirName), diagnostics, enforceCatches: true);
        IReadOnlyList<GuardrailDefinition> planGuardrails =
            LoadGuardrailsFromFolder(Path.Combine(planDir, GuardrailsDirName), diagnostics, enforceCatches: true);

        string workspace = Path.GetFullPath(Path.Combine(planDir, config.Workspace));

        var plan = new PlanDefinition
        {
            PlanDirectory = planDir,
            Config = config,
            Tasks = tasks,
            Waves = waves,
            Workspace = workspace,
            PlanPreflights = planPreflights,
            PlanGuardrails = planGuardrails
        };

        return new PlanLoadResult { Plan = plan, Diagnostics = diagnostics };
    }

    // --- guardrails.json --------------------------------------------------------------

    private static RunConfig? LoadConfig(string configPath, List<Diagnostic> diagnostics)
    {
        if (!File.Exists(configPath))
        {
            diagnostics.Add(Error(DiagnosticCodes.MissingFile, configPath, $"{ConfigFileName} is required but was not found."));
            return null;
        }

        RawRunConfig? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawRunConfig>(File.ReadAllText(configPath), PlanJson.Options);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidJson, configPath, $"Could not parse {ConfigFileName}: {ex.Message}"));
            return null;
        }

        if (raw is null)
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidJson, configPath, $"{ConfigFileName} is empty or null."));
            return null;
        }

        if (raw.Version is null)
        {
            diagnostics.Add(Error(DiagnosticCodes.MissingRequiredField, configPath, "Required field 'version' is missing."));
            return null;
        }

        GuardrailMode mode = GuardrailMode.FailFast;
        if (raw.GuardrailMode is not null && !TryParseGuardrailMode(raw.GuardrailMode, out mode))
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidFieldValue, configPath,
                $"Unknown guardrailMode '{raw.GuardrailMode}'. Expected 'failFast' or 'runAll'."));
            return null;
        }

        AutonomyPolicy autonomyPolicy = AutonomyPolicy.Prompt;
        if (raw.AutonomyPolicy is not null && !AutonomyPolicies.TryParse(raw.AutonomyPolicy, out autonomyPolicy))
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidAutonomyPolicy, configPath,
                $"Unknown autonomyPolicy '{raw.AutonomyPolicy}'. Expected 'prompt' (default), 'halt', or 'auto' (SSOT §2.1/§7.2)."));
            return null;
        }

        PromptRunnersResult runners;
        try
        {
            runners = ReadPromptRunners(raw.PromptRunners);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidJson, configPath,
                $"Could not parse promptRunners in {ConfigFileName}: {ex.Message}"));
            return null;
        }

        var interpreters = (raw.Interpreters ?? [])
            .ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<string>)kvp.Value,
                StringComparer.OrdinalIgnoreCase);

        return new RunConfig
        {
            Version = raw.Version.Value,
            MaxParallelism = raw.MaxParallelism ?? 3,
            DefaultRetries = raw.DefaultRetries ?? 2,
            MaxCostUsd = raw.MaxCostUsd,
            DefaultTimeoutSeconds = raw.DefaultTimeoutSeconds ?? 1800,
            TransientPauseBudgetSeconds = raw.TransientPauseBudgetSeconds ?? 14400,
            GuardrailMode = mode,
            Workspace = string.IsNullOrWhiteSpace(raw.Workspace) ? ".." : raw.Workspace,
            WorktreeRoot = string.IsNullOrWhiteSpace(raw.WorktreeRoot) ? null : raw.WorktreeRoot.Trim(),
            RunOnCurrentBranch = raw.RunOnCurrentBranch ?? false,
            MergeOnSuccess = raw.MergeOnSuccess ?? false,
            TriageAutoFile = raw.TriageAutoFile ?? false,
            AutonomyPolicy = autonomyPolicy,
            PreserveAttemptsForSalvage = raw.PreserveAttemptsForSalvage ?? true,
            Interpreters = interpreters,
            PromptRunnerNames = runners.Names,
            DefaultPromptRunner = runners.Default,
            PromptRunners = runners.Runners
        };
    }

    private static bool TryParseGuardrailMode(string value, out GuardrailMode mode)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "failfast":
                mode = GuardrailMode.FailFast;
                return true;
            case "runall":
                mode = GuardrailMode.RunAll;
                return true;
            default:
                mode = GuardrailMode.FailFast;
                return false;
        }
    }

    /// <summary>
    /// Parse the <c>promptRunners</c> map (SSOT §2/§9): a <c>"default"</c> string pointer plus
    /// one config object per named runner. Each runner's settings get documented defaults; a
    /// <c>guardrailOverrides</c> sub-block is a partial override (only present keys override).
    /// </summary>
    private static PromptRunnersResult ReadPromptRunners(JsonElement? promptRunners)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var runners = new Dictionary<string, PromptRunnerConfig>(StringComparer.Ordinal);
        string? defaultRunner = null;

        if (promptRunners is not { ValueKind: JsonValueKind.Object } element)
        {
            return new PromptRunnersResult(names, defaultRunner, runners);
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name == "default")
            {
                defaultRunner = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                continue;
            }

            names.Add(property.Name);

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                RawPromptRunner raw = property.Value.Deserialize<RawPromptRunner>(PlanJson.Options)!;
                runners[property.Name] = BuildRunnerConfig(property.Name, raw);
            }
        }

        return new PromptRunnersResult(names, defaultRunner, runners);
    }

    private static PromptRunnerConfig BuildRunnerConfig(string name, RawPromptRunner raw)
    {
        var settings = new PromptRunnerSettings
        {
            PermissionMode = string.IsNullOrWhiteSpace(raw.PermissionMode) ? "acceptEdits" : raw.PermissionMode,
            AllowedTools = raw.AllowedTools is null ? [] : [.. raw.AllowedTools],
            MaxTurns = raw.MaxTurns ?? 50,
            Model = raw.Model,
            ExtraArgs = raw.ExtraArgs is null ? [] : [.. raw.ExtraArgs],
            MaxOutputTokens = raw.MaxOutputTokens ?? PromptRunnerSettings.DefaultMaxOutputTokens,
            Env = raw.Env is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(raw.Env, StringComparer.Ordinal)
        };

        PromptRunnerOverrides? overrides = raw.GuardrailOverrides is null
            ? null
            : new PromptRunnerOverrides
            {
                PermissionMode = raw.GuardrailOverrides.PermissionMode,
                AllowedTools = raw.GuardrailOverrides.AllowedTools is null ? null : [.. raw.GuardrailOverrides.AllowedTools],
                MaxTurns = raw.GuardrailOverrides.MaxTurns,
                Model = raw.GuardrailOverrides.Model,
                ExtraArgs = raw.GuardrailOverrides.ExtraArgs is null ? null : [.. raw.GuardrailOverrides.ExtraArgs],
                MaxOutputTokens = raw.GuardrailOverrides.MaxOutputTokens,
                Env = raw.GuardrailOverrides.Env is null
                    ? null
                    : new Dictionary<string, string>(raw.GuardrailOverrides.Env, StringComparer.Ordinal)
            };

        return new PromptRunnerConfig
        {
            Name = name,
            Command = string.IsNullOrWhiteSpace(raw.Command) ? name : raw.Command,
            Settings = settings,
            GuardrailOverrides = overrides
        };
    }

    // --- layout detection: flat vs waved (SSOT §14.1) ---------------------------------

    /// <summary>
    /// Detect the plan layout and load its tasks (and waves). A plan is WAVED iff it has NO root
    /// <c>tasks/</c> AND ≥1 immediate subdirectory matching <see cref="WaveDirPattern"/>; otherwise FLAT
    /// (SSOT §14.1). MIXED (both a root <c>tasks/</c> and wave dirs) is <see cref="DiagnosticCodes.MixedWaveLayout"/>
    /// (GR2032) — reported, then loaded as flat so the remaining diagnostics still run (the error blocks the
    /// run regardless). For a flat plan <paramref name="waves"/> is empty and behaviour is unchanged.
    /// </summary>
    private void LoadTasksOrWaves(
        string planDir, List<Diagnostic> diagnostics,
        out IReadOnlyList<TaskNode> tasks, out IReadOnlyList<WaveNode> waves)
    {
        bool hasRootTasks = Directory.Exists(Path.Combine(planDir, TasksDirName));

        List<(string Path, string Name)> subdirs = Directory
            .EnumerateDirectories(planDir)
            .Select(d => (Path: d, Name: Path.GetFileName(d)))
            .ToList();

        bool hasWaveDirs = subdirs.Any(s => WaveDirPattern.IsMatch(s.Name));

        if (hasRootTasks && hasWaveDirs)
        {
            diagnostics.Add(Error(DiagnosticCodes.MixedWaveLayout, planDir,
                "Plan has a MIXED layout: both a root 'tasks/' directory and 'wave-*/' subdirectories. A " +
                "plan is either FLAT (a root 'tasks/') or WAVED (no root 'tasks/', with ordered 'wave-NN-slug/' " +
                "subdirs) — never both (SSOT §14.1). Remove one layout."));
            tasks = LoadTasks(planDir, diagnostics); // best-effort so other checks still run; GR2032 blocks the run.
            waves = [];
            return;
        }

        if (!hasWaveDirs)
        {
            tasks = LoadTasks(planDir, diagnostics); // FLAT (or neither — LoadTasks reports the missing tasks/).
            waves = [];
            return;
        }

        LoadWaves(planDir, subdirs, diagnostics, out tasks, out waves);
    }

    /// <summary>
    /// Load a WAVED plan (SSOT §14). Validates wave numbering (<see cref="DiagnosticCodes.WaveNumbering"/> —
    /// duplicate <c>NN</c> or a non-conforming sibling dir = error; a numbering gap = warning), loads each
    /// wave's tasks with WAVE-QUALIFIED ids, then qualifies each task's intra-wave <c>dependsOn</c> and flags
    /// cross-wave edges (<see cref="DiagnosticCodes.CrossWaveDependency"/>, GR2034). <paramref name="tasks"/>
    /// is the flattened union of every wave's tasks in strict wave order.
    /// </summary>
    private void LoadWaves(
        string planDir, List<(string Path, string Name)> subdirs, List<Diagnostic> diagnostics,
        out IReadOnlyList<TaskNode> tasks, out IReadOnlyList<WaveNode> waves)
    {
        // GR2033: a subdirectory alongside the wave dirs that is neither wave-conforming nor a recognised
        // plan-root folder (a typo'd wave dir like "wave-scaffold" with no number, or a stray "tasks-old/").
        foreach ((string path, string name) in subdirs)
        {
            if (WaveDirPattern.IsMatch(name) || KnownPlanRootFolders.Contains(name) || name.StartsWith('.'))
            {
                continue;
            }

            diagnostics.Add(Error(DiagnosticCodes.WaveNumbering, path,
                $"Subdirectory '{name}' sits alongside wave directories but does not match the wave-dir " +
                "pattern '^wave-([0-9]+)-[a-z0-9-]+$' and is not a recognised plan-root folder. Rename it to " +
                "a conforming 'wave-NN-slug/' or remove it (SSOT §14.1)."));
        }

        // Parse the conforming wave dirs.
        var parsed = new List<(string Dir, int Number, string Slug, string Path)>();
        foreach ((string path, string name) in subdirs)
        {
            Match m = WaveDirPattern.Match(name);
            if (!m.Success)
            {
                continue;
            }

            if (!int.TryParse(m.Groups[1].Value, out int number))
            {
                diagnostics.Add(Error(DiagnosticCodes.WaveNumbering, path,
                    $"Wave directory '{name}' has a numeric prefix that is out of range. Use a small, " +
                    "unique wave number (SSOT §14.1)."));
                continue;
            }

            parsed.Add((name, number, m.Groups[2].Value, path));
        }

        // GR2033 error: a duplicate wave number makes the strict total order ambiguous.
        foreach (IGrouping<int, (string Dir, int Number, string Slug, string Path)> grp in
                 parsed.GroupBy(p => p.Number).Where(g => g.Count() > 1))
        {
            string dirs = string.Join(", ", grp.Select(p => p.Dir).OrderBy(d => d, StringComparer.Ordinal));
            diagnostics.Add(Error(DiagnosticCodes.WaveNumbering, planDir,
                $"Wave number {grp.Key} is used by more than one wave directory ({dirs}); the numeric prefix " +
                "drives the strict wave order and must be unique (SSOT §14.1)."));
        }

        // Strict order: by number, then dir name (ordinal) as a stable tiebreak even on a duplicate number.
        List<(string Dir, int Number, string Slug, string Path)> ordered =
            parsed.OrderBy(p => p.Number).ThenBy(p => p.Dir, StringComparer.Ordinal).ToList();

        // GR2033 warning: an internal numbering gap (order stays unambiguous; usually a missing/renamed wave).
        List<int> distinctNumbers = ordered.Select(p => p.Number).Distinct().OrderBy(n => n).ToList();
        for (int i = 1; i < distinctNumbers.Count; i++)
        {
            if (distinctNumbers[i] != distinctNumbers[i - 1] + 1)
            {
                diagnostics.Add(Warning(DiagnosticCodes.WaveNumbering, planDir,
                    $"Wave numbering has a gap ({distinctNumbers[i - 1]:D2} → {distinctNumbers[i]:D2}); this is " +
                    "allowed (the order stays unambiguous) but usually indicates a missing or renamed wave (SSOT §14.1)."));
            }
        }

        // Load each wave's tasks (wave-qualified ids, authored dependsOn) + its entry/exit gate folders.
        var waveNodes = new List<WaveNode>();
        foreach ((string dir, int number, string slug, string path) in ordered)
        {
            IReadOnlyList<TaskNode> waveTasks = LoadWaveTasks(path, dir, diagnostics);

            waveNodes.Add(new WaveNode
            {
                Dir = dir,
                Number = number,
                Slug = slug,
                Directory = path,
                Tasks = waveTasks,
                Preflights = LoadGuardrailsFromFolder(Path.Combine(path, PreflightsDirName), diagnostics, enforceCatches: true),
                Guardrails = LoadGuardrailsFromFolder(Path.Combine(path, GuardrailsDirName), diagnostics, enforceCatches: true)
            });
        }

        // Qualify intra-wave dependsOn + flag cross-wave edges (GR2034). Rebuilds the WaveNodes with the
        // qualified task edges, then flattens into the whole-plan task list.
        waves = QualifyWaveDependencies(waveNodes, diagnostics);
        tasks = waves.SelectMany(w => w.Tasks).ToList();

        if (tasks.Count == 0)
        {
            diagnostics.Add(Error(DiagnosticCodes.NoTasks, planDir,
                "Waved plan has no tasks in any wave; a plan needs at least one task (SSOT §14.1)."));
        }
    }

    /// <summary>
    /// Load one wave's task folders from <c>&lt;waveDir&gt;/tasks/</c> with WAVE-QUALIFIED ids. A wave with
    /// no <c>tasks/</c> (or an empty one) is a not-yet-authored (JIT) wave — it loads as zero tasks with no
    /// error (the between-wave runtime checkpoint honest-halts on an unauthored next wave; SSOT §14.4); the
    /// whole-plan empty check in <see cref="LoadWaves"/> catches a plan with NO tasks anywhere.
    /// </summary>
    private IReadOnlyList<TaskNode> LoadWaveTasks(string wavePath, string waveDir, List<Diagnostic> diagnostics)
    {
        string tasksDir = Path.Combine(wavePath, TasksDirName);
        if (!Directory.Exists(tasksDir))
        {
            return [];
        }

        var tasks = new List<TaskNode>();
        foreach (string taskFolder in Directory
                     .EnumerateDirectories(tasksDir)
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            TaskNode? task = LoadTask(taskFolder, diagnostics, waveDir);
            if (task is not null)
            {
                tasks.Add(task);
            }
        }

        return tasks;
    }

    /// <summary>
    /// Qualify each waved task's authored <c>dependsOn</c> (plain sibling names) to the wave-qualified id
    /// <c>&lt;waveDir&gt;/&lt;name&gt;</c>, and flag cross-wave edges as
    /// <see cref="DiagnosticCodes.CrossWaveDependency"/> (GR2034, SSOT §14.2). A cross-wave edge — a
    /// wave-qualified reference to another wave, or a plain name that resolves to a task in a DIFFERENT wave —
    /// is DROPPED (not added to the qualified list) so it produces no phantom graph edge and no double GR2001.
    /// An unknown plain name (matching no task anywhere) is qualified to this wave so the validator's GR2001
    /// unknown-dependency check fires normally.
    /// </summary>
    private static IReadOnlyList<WaveNode> QualifyWaveDependencies(
        IReadOnlyList<WaveNode> waveNodes, List<Diagnostic> diagnostics)
    {
        // folderName -> the set of wave dirs that contain a task with that folder name (for cross-wave detection).
        var folderToWaves = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (WaveNode wave in waveNodes)
        {
            foreach (TaskNode task in wave.Tasks)
            {
                string folder = FolderNameOf(task);
                if (!folderToWaves.TryGetValue(folder, out HashSet<string>? set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    folderToWaves[folder] = set;
                }

                set.Add(wave.Dir);
            }
        }

        var rebuilt = new List<WaveNode>(waveNodes.Count);
        foreach (WaveNode wave in waveNodes)
        {
            var siblings = new HashSet<string>(wave.Tasks.Select(FolderNameOf), StringComparer.Ordinal);
            var qualifiedTasks = new List<TaskNode>(wave.Tasks.Count);

            foreach (TaskNode task in wave.Tasks)
            {
                var qualified = new List<string>();
                foreach (string authored in task.DependsOn)
                {
                    string entry = authored.Trim();
                    if (entry.StartsWith("./", StringComparison.Ordinal))
                    {
                        entry = entry[2..];
                    }

                    if (entry.Contains('/'))
                    {
                        // A wave-qualified reference. Only a SELF-qualified '<thisWave>/<name>' is legal.
                        string prefix = wave.Dir + "/";
                        if (entry.StartsWith(prefix, StringComparison.Ordinal) && !entry[prefix.Length..].Contains('/'))
                        {
                            qualified.Add(entry); // self-qualified sibling — keep (validator GR2001 if unknown).
                        }
                        else
                        {
                            diagnostics.Add(Error(DiagnosticCodes.CrossWaveDependency, task.Directory,
                                $"Task '{task.Id}' dependsOn '{authored}', a cross-wave reference. Cross-wave " +
                                "ordering is the wave barrier's job, not a task edge — each wave's DAG must be " +
                                "self-contained (SSOT §14.1/§14.2). dependsOn may only name a sibling in the " +
                                "SAME wave by its plain folder name."));
                        }

                        continue;
                    }

                    if (siblings.Contains(entry))
                    {
                        qualified.Add($"{wave.Dir}/{entry}"); // intra-wave sibling — qualify.
                    }
                    else if (folderToWaves.TryGetValue(entry, out HashSet<string>? owners) &&
                             owners.Any(w => !string.Equals(w, wave.Dir, StringComparison.Ordinal)))
                    {
                        // A plain name that resolves to a task in another wave — cross-wave (GR2034), drop it.
                        diagnostics.Add(Error(DiagnosticCodes.CrossWaveDependency, task.Directory,
                            $"Task '{task.Id}' dependsOn '{authored}', which is not a sibling in this wave but " +
                            $"names a task in another wave ({string.Join(", ", owners.OrderBy(w => w, StringComparer.Ordinal))}). " +
                            "Cross-wave ordering is the wave barrier's job, not a task edge (SSOT §14.1/§14.2)."));
                    }
                    else
                    {
                        // Unknown in this wave and nowhere else — qualify so the validator's GR2001 fires.
                        qualified.Add($"{wave.Dir}/{entry}");
                    }
                }

                qualifiedTasks.Add(task with { DependsOn = qualified });
            }

            rebuilt.Add(wave with { Tasks = qualifiedTasks });
        }

        return rebuilt;
    }

    /// <summary>The task's plain folder name — the segment of a wave-qualified id after the wave dir.</summary>
    private static string FolderNameOf(TaskNode task) =>
        task.WaveDir is { } wave && task.Id.StartsWith(wave + "/", StringComparison.Ordinal)
            ? task.Id[(wave.Length + 1)..]
            : task.Id;

    // --- tasks/* ----------------------------------------------------------------------

    private IReadOnlyList<TaskNode> LoadTasks(string planDir, List<Diagnostic> diagnostics)
    {
        string tasksDir = Path.Combine(planDir, TasksDirName);
        if (!Directory.Exists(tasksDir))
        {
            diagnostics.Add(Error(DiagnosticCodes.MissingFile, tasksDir, "Plan has no 'tasks' directory."));
            return [];
        }

        var tasks = new List<TaskNode>();
        List<string> taskFolders = Directory
            .EnumerateDirectories(tasksDir)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        // An empty tasks/ directory is a malformed plan: it would otherwise validate clean and
        // "run" 0/0 green. (A dir with task folders that all fail to load is already reported by
        // the per-task diagnostics below, so only the truly-empty case needs flagging here.)
        if (taskFolders.Count == 0)
        {
            diagnostics.Add(Error(DiagnosticCodes.NoTasks, tasksDir,
                "Plan's 'tasks' directory is empty; a plan needs at least one task folder."));
            return [];
        }

        foreach (string taskFolder in taskFolders)
        {
            TaskNode? task = LoadTask(taskFolder, diagnostics);
            if (task is not null)
            {
                tasks.Add(task);
            }
        }

        return tasks;
    }

    /// <summary>
    /// Load one task folder. In a FLAT plan <paramref name="waveDir"/> is null and the task id is the
    /// folder name. In a WAVED plan (SSOT §14.2) <paramref name="waveDir"/> is the owning wave dir and the
    /// task id is the WAVE-QUALIFIED <c>&lt;waveDir&gt;/&lt;folder&gt;</c>. <c>dependsOn</c> is stored AS
    /// AUTHORED here (plain sibling names); the caller's <see cref="QualifyWaveDependencies"/> post-pass
    /// qualifies it intra-wave and flags cross-wave edges (GR2034).
    /// </summary>
    private TaskNode? LoadTask(string taskFolder, List<Diagnostic> diagnostics, string? waveDir = null)
    {
        string folderName = Path.GetFileName(taskFolder);
        string taskId = waveDir is null ? folderName : $"{waveDir}/{folderName}";
        string manifestPath = Path.Combine(taskFolder, TaskManifestName);

        if (!File.Exists(manifestPath))
        {
            diagnostics.Add(Error(DiagnosticCodes.MissingFile, manifestPath, $"Task '{taskId}' has no {TaskManifestName}."));
            return null;
        }

        RawTask? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawTask>(File.ReadAllText(manifestPath), PlanJson.Options);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidJson, manifestPath, $"Could not parse {TaskManifestName}: {ex.Message}"));
            return null;
        }

        if (raw is null)
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidJson, manifestPath, $"{TaskManifestName} is empty or null."));
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw.Description))
        {
            diagnostics.Add(Error(DiagnosticCodes.MissingRequiredField, manifestPath, "Required field 'description' is missing or empty."));
            return null;
        }

        ActionDefinition? action = ResolveAction(taskFolder, taskId, raw.Action, diagnostics);
        if (action is null)
        {
            return null;
        }

        // Postcondition guardrails/ (catches enforcement is NOT retrofitted onto this pre-existing
        // folder — its behavior is preserved) and the sibling JIT preflights/ folder (a NEW folder, so
        // its files DO carry the enforced `catches:` declaration, GR2027).
        IReadOnlyList<GuardrailDefinition> guardrails =
            LoadGuardrailsFromFolder(Path.Combine(taskFolder, GuardrailsDirName), diagnostics, enforceCatches: false);
        IReadOnlyList<GuardrailDefinition> preflights =
            LoadGuardrailsFromFolder(Path.Combine(taskFolder, PreflightsDirName), diagnostics, enforceCatches: true);

        return new TaskNode
        {
            Id = taskId,
            WaveDir = waveDir,
            StableId = string.IsNullOrWhiteSpace(raw.StableId) ? null : raw.StableId.Trim(),
            Directory = taskFolder,
            Description = raw.Description.Trim(),
            DependsOn = raw.DependsOn ?? [],
            Retries = raw.Retries,
            TimeoutSeconds = raw.TimeoutSeconds,
            IntegrationGate = raw.IntegrationGate ?? false,
            WriteScope = raw.WriteScope is { Count: > 0 } ws ? [.. ws] : null,
            StagingOutputs = BindStagingOutputs(raw.StagingOutputs),
            Action = action,
            Guardrails = guardrails,
            Preflights = preflights
        };
    }

    /// <summary>
    /// Bind the raw <c>stagingOutputs</c> list (SSOT §3.5). Null (absent) stays null — the
    /// no-staging default. A PRESENT list is bound faithfully, INCLUDING an empty array and entries
    /// with a missing/empty <c>from</c> or <c>to</c> (mapped to <c>""</c>): the validator turns those
    /// into GR2024 errors, so the loader must preserve the "present but malformed" signal rather than
    /// silently dropping it (which would let a malformed contract validate clean).
    /// </summary>
    private static IReadOnlyList<StagingOutput>? BindStagingOutputs(List<RawStagingOutput>? raw)
    {
        if (raw is null)
        {
            return null;
        }

        return raw
            .Select(entry => new StagingOutput
            {
                From = entry.From?.Trim() ?? string.Empty,
                To = entry.To?.Trim() ?? string.Empty
            })
            .ToList();
    }

    // --- action discovery (SSOT §3) ---------------------------------------------------

    private ActionDefinition? ResolveAction(string taskFolder, string taskId, RawAction? rawAction, List<Diagnostic> diagnostics)
    {
        string? actionPath;
        if (!string.IsNullOrWhiteSpace(rawAction?.Path))
        {
            actionPath = Path.GetFullPath(Path.Combine(taskFolder, rawAction.Path));
            if (!File.Exists(actionPath))
            {
                diagnostics.Add(Error(DiagnosticCodes.ActionPathNotFound, taskFolder,
                    $"Task '{taskId}' action.path '{rawAction.Path}' does not exist."));
                return null;
            }
        }
        else
        {
            actionPath = DiscoverActionByConvention(taskFolder, taskId, diagnostics);
            if (actionPath is null)
            {
                return null;
            }
        }

        return new ActionDefinition
        {
            Path = actionPath,
            Kind = KindFor(actionPath),
            Args = rawAction?.Args ?? [],
            Runner = rawAction?.Runner,
            MaxTurns = rawAction?.MaxTurns,
            // Bound VERBATIM (no trim/nullify): a present-but-blank value (e.g. "   ") must reach the
            // validator's GR2030 check faithfully, the same "preserve the malformed signal" doctrine
            // BindStagingOutputs documents for stagingOutputs — silently normalizing it to null here
            // would let a malformed override validate clean.
            Model = rawAction?.Model,
            TimeoutSeconds = rawAction?.TimeoutSeconds,
            WorkingDirectory = rawAction?.WorkingDirectory,
            Env = (IReadOnlyDictionary<string, string>?)rawAction?.Env ?? new Dictionary<string, string>()
        };
    }

    /// <summary>
    /// Convention discovery: exactly ONE <c>action.*</c> file in the task folder.
    /// Zero or multiple is a validation error (SSOT §3). A multi-part extension like
    /// <c>action.prompt.md</c> still counts as one action file.
    /// </summary>
    private string? DiscoverActionByConvention(string taskFolder, string taskId, List<Diagnostic> diagnostics)
    {
        List<string> candidates = Directory
            .EnumerateFiles(taskFolder)
            .Where(f => Path.GetFileName(f).StartsWith(ActionFilePrefix, StringComparison.Ordinal))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        switch (candidates.Count)
        {
            case 0:
                diagnostics.Add(Error(DiagnosticCodes.NoActionFile, taskFolder,
                    $"Task '{taskId}' has no action file (expected one 'action.*' file or an explicit action.path)."));
                return null;
            case 1:
                return candidates[0];
            default:
                string names = string.Join(", ", candidates.Select(Path.GetFileName));
                diagnostics.Add(Error(DiagnosticCodes.AmbiguousActionFile, taskFolder,
                    $"Task '{taskId}' has {candidates.Count} action files ({names}); expected exactly one or an explicit action.path."));
                return null;
        }
    }

    // --- guardrail discovery (SSOT §4 / §4.1) -----------------------------------------

    /// <summary>
    /// Discover the guardrail-shaped files in one of the four preflights/guardrails folders
    /// (<c>&lt;plan&gt;/preflights/</c>, <c>&lt;plan&gt;/guardrails/</c>, <c>tasks/&lt;id&gt;/preflights/</c>,
    /// <c>tasks/&lt;id&gt;/guardrails/</c>), ordered by filename ordinal sort. The folders share this ONE
    /// parser — they differ only in WHERE they live and WHEN they run (SSOT §4). A
    /// <c>&lt;basename&gt;.json</c> next to a same-basename script is a metadata sidecar (not a guardrail);
    /// a bare <c>.json</c> with no sibling script is an orphan sidecar (error). Sidecar metadata is loaded
    /// onto the matching deterministic guardrail. When <paramref name="enforceCatches"/> is set, a file
    /// that does not open with a <c>catches:</c> declaration is a malformed declaration (GR2027) — the
    /// canonical per-folder diagnostic for the four-folder model. (The pre-existing
    /// <c>tasks/&lt;id&gt;/guardrails/</c> folder is loaded WITHOUT catches enforcement to preserve its
    /// behavior; the three new folders enforce it.)
    /// </summary>
    private IReadOnlyList<GuardrailDefinition> LoadGuardrailsFromFolder(
        string guardrailsDir, List<Diagnostic> diagnostics, bool enforceCatches)
    {
        if (!Directory.Exists(guardrailsDir))
        {
            return [];
        }

        List<string> files = Directory
            .EnumerateFiles(guardrailsDir)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        // Basenames (without final extension) that have a non-.json file — these own any
        // sibling .json as metadata. Prompt guardrails (.prompt.md) have basename incl.
        // ".prompt"; their sidecars are not part of the M2 contract, but the lookup is by
        // the exact file basename so it remains correct.
        var scriptBasenames = new HashSet<string>(
            files.Where(f => !IsJson(f)).Select(GuardrailBasename),
            StringComparer.Ordinal);

        var guardrails = new List<GuardrailDefinition>();

        foreach (string file in files)
        {
            string fileName = Path.GetFileName(file);
            string basename = GuardrailBasename(file);

            if (IsJson(file))
            {
                if (scriptBasenames.Contains(basename))
                {
                    continue; // metadata sidecar for a sibling script — not a guardrail.
                }

                diagnostics.Add(Error(DiagnosticCodes.OrphanGuardrailMetadata, file,
                    $"Guardrail metadata '{fileName}' has no sibling guardrail script with basename '{basename}'."));
                continue;
            }

            ActionKind kind = KindFor(file);
            GuardrailDefinition guardrail = new()
            {
                Name = GuardrailName(file, kind),
                Path = file,
                Kind = kind
            };

            if (kind == ActionKind.Script)
            {
                guardrail = ApplySidecar(guardrail, guardrailsDir, basename, diagnostics);
            }
            else if (kind == ActionKind.Prompt)
            {
                guardrail = ApplyPromptFrontmatter(guardrail);
            }

            if (enforceCatches && !HasCatchesDeclaration(guardrail))
            {
                diagnostics.Add(Error(DiagnosticCodes.GuardrailMissingCatches, file,
                    $"Guardrail '{guardrail.Name}' does not open with a 'catches:' " +
                    (kind == ActionKind.Prompt ? "front-matter field" : "comment") +
                    " stating what wrong implementation it catches (SSOT §4). A guardrail whose author " +
                    "cannot state what it catches is decorative — declare it, or remove the file."));
            }

            guardrails.Add(guardrail);
        }

        return guardrails;
    }

    /// <summary>
    /// True when a guardrail file OPENS with a <c>catches:</c> declaration (SSOT §4): a leading comment
    /// for a script guardrail, or a <c>catches</c> YAML front-matter field for a prompt guardrail. An
    /// unreadable file returns true — the GR2027 malformed-declaration diagnostic must not double-report
    /// a file whose IO error other checks already surface.
    /// </summary>
    private static bool HasCatchesDeclaration(GuardrailDefinition guardrail)
    {
        string content;
        try
        {
            content = File.ReadAllText(guardrail.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }

        return guardrail.Kind == ActionKind.Prompt
            ? FrontmatterDeclaresCatches(content)
            : LeadingCommentDeclaresCatches(content);
    }

    /// <summary>
    /// Scan the leading comment block — the contiguous comment/blank lines at the top — of a script
    /// guardrail for a <c>catches:</c> declaration. Returns false as soon as the first non-comment,
    /// non-blank line (real code) is reached without one: the declaration must OPEN the file. Recognises
    /// <c>#</c> (ps1/sh/py), <c>//</c>, and <c>REM</c>/<c>::</c> (cmd/bat) comment leaders; a shebang or
    /// other leading comment line before the <c>catches:</c> line is tolerated.
    /// </summary>
    private static bool LeadingCommentDeclaresCatches(string content)
    {
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string? comment = CommentBody(line);
            if (comment is null)
            {
                return false; // reached real code before any `catches:` comment
            }

            if (comment.StartsWith("catches:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The text of a comment line after its leader, or null when the line is not a comment.</summary>
    private static string? CommentBody(string trimmedLine)
    {
        if (trimmedLine.StartsWith('#'))
        {
            return trimmedLine[1..].TrimStart();
        }
        if (trimmedLine.StartsWith("//", StringComparison.Ordinal))
        {
            return trimmedLine[2..].TrimStart();
        }
        if (trimmedLine.StartsWith("::", StringComparison.Ordinal))
        {
            return trimmedLine[2..].TrimStart();
        }
        if (trimmedLine.StartsWith("REM", StringComparison.OrdinalIgnoreCase) &&
            (trimmedLine.Length == 3 || char.IsWhiteSpace(trimmedLine[3])))
        {
            return trimmedLine.Length > 3 ? trimmedLine[3..].TrimStart() : string.Empty;
        }
        return null;
    }

    /// <summary>
    /// True when a prompt guardrail's YAML front-matter declares a non-empty <c>catches</c> field
    /// (SSOT §4/§4.2). Reuses the same front-matter extraction as <see cref="ApplyPromptFrontmatter"/>.
    /// </summary>
    private static bool FrontmatterDeclaresCatches(string content)
    {
        string? frontmatter = ExtractFrontmatter(content);
        if (frontmatter is null)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(ParseFrontmatterScalar(frontmatter, "catches"));
    }

    private static GuardrailDefinition ApplySidecar(
        GuardrailDefinition guardrail,
        string guardrailsDir,
        string basename,
        List<Diagnostic> diagnostics)
    {
        string sidecarPath = Path.Combine(guardrailsDir, basename + ".json");
        if (!File.Exists(sidecarPath))
        {
            return guardrail;
        }

        RawGuardrailSidecar? sidecar;
        try
        {
            sidecar = JsonSerializer.Deserialize<RawGuardrailSidecar>(File.ReadAllText(sidecarPath), PlanJson.Options);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(Error(DiagnosticCodes.InvalidJson, sidecarPath, $"Could not parse guardrail sidecar: {ex.Message}"));
            return guardrail;
        }

        if (sidecar is null)
        {
            return guardrail;
        }

        return guardrail with
        {
            Description = sidecar.Description,
            Args = sidecar.Args ?? [],
            TimeoutSeconds = sidecar.TimeoutSeconds,
            ExpectedDurationSeconds = sidecar.ExpectedDurationSeconds,
            Scope = string.IsNullOrWhiteSpace(sidecar.Scope) ? null : sidecar.Scope.Trim().ToLowerInvariant()
        };
    }

    // --- prompt frontmatter (SSOT §4.2) -----------------------------------------------

    /// <summary>
    /// Reads the YAML front-matter block (between the opening and closing <c>---</c> delimiters)
    /// from a <c>.prompt.md</c> guardrail file and applies any recognised keys onto the guardrail.
    /// Currently only <c>scope</c> is harvested; unknown keys are silently ignored.
    /// </summary>
    private static GuardrailDefinition ApplyPromptFrontmatter(GuardrailDefinition guardrail)
    {
        string content;
        try { content = File.ReadAllText(guardrail.Path); }
        catch (IOException) { return guardrail; }

        string? frontmatter = ExtractFrontmatter(content);
        if (frontmatter is null)
            return guardrail;

        string? scope = ParseFrontmatterScalar(frontmatter, "scope");
        if (scope is null)
            return guardrail;

        return guardrail with
        {
            Scope = string.IsNullOrWhiteSpace(scope) ? null : scope.Trim().ToLowerInvariant()
        };
    }

    /// <summary>
    /// The YAML front-matter block of a <c>.prompt.md</c> file — the text between the opening
    /// <c>---</c> on the very first line and the next <c>---</c> line — or null when the file has no
    /// well-formed front-matter block.
    /// </summary>
    private static string? ExtractFrontmatter(string content)
    {
        // Front-matter must start with "---" on the very first line.
        if (!content.StartsWith("---", StringComparison.Ordinal))
            return null;

        int firstNewline = content.IndexOfAny(['\r', '\n']);
        if (firstNewline < 0)
            return null;

        int bodyStart = firstNewline + 1;
        if (bodyStart < content.Length && content[firstNewline] == '\r' && content[bodyStart] == '\n')
            bodyStart++;

        // Find the closing "---" line.
        int closePos = FindFrontmatterClose(content, bodyStart);
        if (closePos < 0)
            return null;

        return content[bodyStart..closePos];
    }

    private static int FindFrontmatterClose(string content, int startPos)
    {
        int pos = startPos;
        while (pos < content.Length)
        {
            int lineStart = pos;
            int lineEnd = content.IndexOfAny(['\r', '\n'], pos);
            if (lineEnd < 0) break;

            string line = content[lineStart..lineEnd];
            if (line == "---")
                return lineStart;

            pos = lineEnd + 1;
            if (pos < content.Length && content[lineEnd] == '\r' && content[pos] == '\n')
                pos++;
        }
        return -1;
    }

    private static string? ParseFrontmatterScalar(string frontmatter, string key)
    {
        foreach (string line in frontmatter.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            if (string.Equals(line[..colon].Trim(), key, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }
        return null;
    }

    // --- helpers ----------------------------------------------------------------------

    private static ActionKind KindFor(string path) =>
        path.EndsWith(PromptExtension, StringComparison.OrdinalIgnoreCase)
            ? ActionKind.Prompt
            : ActionKind.Script;

    private static bool IsJson(string path) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The guardrail "basename" used to pair a sidecar with its script: the file name with
    /// its final extension removed (e.g. "01-build-passes.ps1" → "01-build-passes";
    /// "02-review.prompt.md" → "02-review.prompt"). A <c>.json</c> sidecar must share this
    /// exact basename with its script for the pairing to hold.
    /// </summary>
    private static string GuardrailBasename(string path) =>
        Path.GetFileNameWithoutExtension(path);

    /// <summary>
    /// The human/journal name for a guardrail: a prompt guardrail drops the whole
    /// <c>.prompt.md</c> suffix (so "02-review.prompt.md" → "02-review"); a deterministic
    /// guardrail drops only its final extension (so "01-build-passes.ps1" → "01-build-passes").
    /// </summary>
    private static string GuardrailName(string path, ActionKind kind)
    {
        string fileName = Path.GetFileName(path);
        return kind == ActionKind.Prompt
            ? fileName[..^PromptExtension.Length]
            : Path.GetFileNameWithoutExtension(fileName);
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

    /// <summary>The parsed <c>promptRunners</c> map: names, the default pointer, and full configs.</summary>
    private readonly record struct PromptRunnersResult(
        IReadOnlySet<string> Names,
        string? Default,
        IReadOnlyDictionary<string, PromptRunnerConfig> Runners);
}
