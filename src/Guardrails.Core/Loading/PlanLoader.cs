using System.Text.Json;
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
    private const string PromptExtension = ".prompt.md";
    private const string ActionFilePrefix = "action.";

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

        IReadOnlyList<TaskNode> tasks = LoadTasks(planDir, diagnostics);

        string workspace = Path.GetFullPath(Path.Combine(planDir, config.Workspace));

        var plan = new PlanDefinition
        {
            PlanDirectory = planDir,
            Config = config,
            Tasks = tasks,
            Workspace = workspace
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
            GuardrailMode = mode,
            Workspace = string.IsNullOrWhiteSpace(raw.Workspace) ? ".." : raw.Workspace,
            WorktreeRoot = string.IsNullOrWhiteSpace(raw.WorktreeRoot) ? null : raw.WorktreeRoot.Trim(),
            RunOnCurrentBranch = raw.RunOnCurrentBranch ?? false,
            MergeOnSuccess = raw.MergeOnSuccess ?? false,
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
            ExtraArgs = raw.ExtraArgs is null ? [] : [.. raw.ExtraArgs]
        };

        PromptRunnerOverrides? overrides = raw.GuardrailOverrides is null
            ? null
            : new PromptRunnerOverrides
            {
                PermissionMode = raw.GuardrailOverrides.PermissionMode,
                AllowedTools = raw.GuardrailOverrides.AllowedTools is null ? null : [.. raw.GuardrailOverrides.AllowedTools],
                MaxTurns = raw.GuardrailOverrides.MaxTurns,
                Model = raw.GuardrailOverrides.Model,
                ExtraArgs = raw.GuardrailOverrides.ExtraArgs is null ? null : [.. raw.GuardrailOverrides.ExtraArgs]
            };

        return new PromptRunnerConfig
        {
            Name = name,
            Command = string.IsNullOrWhiteSpace(raw.Command) ? name : raw.Command,
            Settings = settings,
            GuardrailOverrides = overrides
        };
    }

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

    private TaskNode? LoadTask(string taskFolder, List<Diagnostic> diagnostics)
    {
        string taskId = Path.GetFileName(taskFolder);
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

        IReadOnlyList<GuardrailDefinition> guardrails = LoadGuardrails(taskFolder, diagnostics);

        return new TaskNode
        {
            Id = taskId,
            StableId = string.IsNullOrWhiteSpace(raw.StableId) ? null : raw.StableId.Trim(),
            Directory = taskFolder,
            Description = raw.Description.Trim(),
            DependsOn = raw.DependsOn ?? [],
            Retries = raw.Retries,
            TimeoutSeconds = raw.TimeoutSeconds,
            IntegrationGate = raw.IntegrationGate ?? false,
            WriteScope = raw.WriteScope is { Count: > 0 } ws ? [.. ws] : null,
            Action = action,
            Guardrails = guardrails
        };
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
    /// Discover guardrails under <c>guardrails/</c>, ordered by filename ordinal sort.
    /// A <c>&lt;basename&gt;.json</c> file that sits next to a same-basename script is a
    /// metadata sidecar (not a guardrail). A bare <c>.json</c> with no sibling script is
    /// an orphan sidecar — flagged as an error. Sidecar metadata is loaded onto the
    /// matching deterministic guardrail.
    /// </summary>
    private IReadOnlyList<GuardrailDefinition> LoadGuardrails(string taskFolder, List<Diagnostic> diagnostics)
    {
        string guardrailsDir = Path.Combine(taskFolder, GuardrailsDirName);
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

            guardrails.Add(guardrail);
        }

        return guardrails;
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

        // Front-matter must start with "---" on the very first line.
        if (!content.StartsWith("---", StringComparison.Ordinal))
            return guardrail;

        int firstNewline = content.IndexOfAny(['\r', '\n']);
        if (firstNewline < 0)
            return guardrail;

        int bodyStart = firstNewline + 1;
        if (bodyStart < content.Length && content[firstNewline] == '\r' && content[bodyStart] == '\n')
            bodyStart++;

        // Find the closing "---" line.
        int closePos = FindFrontmatterClose(content, bodyStart);
        if (closePos < 0)
            return guardrail;

        string frontmatter = content[bodyStart..closePos];
        string? scope = ParseFrontmatterScalar(frontmatter, "scope");
        if (scope is null)
            return guardrail;

        return guardrail with
        {
            Scope = string.IsNullOrWhiteSpace(scope) ? null : scope.Trim().ToLowerInvariant()
        };
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

    /// <summary>The parsed <c>promptRunners</c> map: names, the default pointer, and full configs.</summary>
    private readonly record struct PromptRunnersResult(
        IReadOnlySet<string> Names,
        string? Default,
        IReadOnlyDictionary<string, PromptRunnerConfig> Runners);
}
