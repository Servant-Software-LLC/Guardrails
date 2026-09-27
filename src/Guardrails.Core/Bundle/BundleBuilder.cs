using System.Text;
using System.Text.Json;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Bundle;

/// <summary>One MANIFEST.md row (SSOT §17.5). <see cref="BundlePath"/> is null for a file with no bundle entry.</summary>
public sealed record BundleManifestRow(string? BundlePath, string Source, long BytesRead, string Status, string Reason);

/// <summary>The bundle, built: its entries, its zip, and whether it fits the cap.</summary>
public sealed record BundleOutcome
{
    /// <summary>The run bundled.</summary>
    public required string RunId { get; init; }

    /// <summary>Every entry, sorted ordinally by path.</summary>
    public required IReadOnlyList<KeyValuePair<string, byte[]>> Entries { get; init; }

    /// <summary>The deterministic zip of <see cref="Entries"/>.</summary>
    public required byte[] Zip { get; init; }

    /// <summary>Still over <c>--max-size</c> after every trim tier (exit 1, the zip is still written).</summary>
    public required bool OverCap { get; init; }

    /// <summary>Whether any trim tier applied.</summary>
    public required bool Trimmed { get; init; }

    /// <summary>MANIFEST.md's rows.</summary>
    public required IReadOnlyList<BundleManifestRow> Manifest { get; init; }

    /// <summary>What <c>--lean</c> withheld, for the one-line stderr note.</summary>
    public required IReadOnlyList<string> LeanWithheld { get; init; }
}

/// <summary>A bundle that cannot be built: the plan's journal could not be read. The CLI exits 1.</summary>
public sealed class BundleRefusedException(string message) : Exception(message);

/// <summary>
/// Builds a run bundle (SSOT §17). Reads <c>run.json</c> FIRST (§17.2 item 2), re-runs validate, collects the allow-listed
/// artifacts (§17.3) with one bounded read each, redacts them (§17.6), adds the git evidence, computes SUMMARY.md,
/// MANIFEST.md and REDACTIONS.md, and trims to the cap in the fixed tier order (§17.7). Writes nothing: the caller
/// places the zip.
/// </summary>
public sealed partial class BundleBuilder
{
    private const string PlanDir = "plan/";

    private readonly BundleRequest _request;
    private readonly BundleProbes _probes;
    private readonly BundleOptions _options;
    private readonly PlanDefinition _plan;
    private readonly List<Entry> _entries = [];
    private readonly List<string> _trimNotes = [];
    private readonly Dictionary<(string Path, byte[] Content), long> _sizeCache = [];

    private JournalDocument? _journal;
    private string _runId = string.Empty;
    private bool _earlierRun;
    private string _runLogs = string.Empty;
    private BundleSecrets _secrets = BundleSecrets.None;
    private HashSet<string> _exempt = new(StringComparer.Ordinal);
    private BundlePathAnonymizer? _anonymizer;
    private IReadOnlyList<string> _selectedTasks = [];
    private long _streamCap = BundleCatalog.StreamCap;
    private long _logCap = BundleCatalog.LogCap;

    private BundleBuilder(BundleRequest request, BundleProbes probes)
    {
        _request = request;
        _probes = probes;
        _options = request.Options;
        _plan = request.Plan;
    }

    /// <summary>Build the bundle. Throws <see cref="BundleRefusedException"/> when there is no readable run to bundle.</summary>
    public static BundleOutcome Build(BundleRequest request, BundleProbes probes) => new BundleBuilder(request, probes).Run();

    private string PlanName => Path.GetFileName(Path.TrimEndingDirectorySeparator(_plan.PlanDirectory));

    private BundleOutcome Run()
    {
        ReadJournalFirst();
        string validateOutput = _probes.Validate();

        _selectedTasks = _options.Tasks.Count > 0
            ? [.. _options.Tasks.Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal)]
            : [.. _plan.Tasks.Select(t => t.Id).OrderBy(t => t, StringComparer.Ordinal)];

        _secrets = CollectSecrets();
        _exempt = CollectExemptTokens();
        _anonymizer = _options.KeepPaths
            ? null
            : new BundlePathAnonymizer(
                _probes.Home, _plan.Workspace, _probes.WorktreeRoot,
                new BundleIdentity(_probes.UserName, GitConfig("user.name"), GitConfig("user.email")),
                [_journal?.Environment?.Host, _journal?.Owner?.Host], _probes.CaseInsensitivePaths);

        CollectPlanFiles(validateOutput);
        CollectJournalEntry();
        CollectStateFragments();
        CollectLogs();
        CollectGit();
        SanitizeEntryNames();

        foreach (Entry entry in _entries)
        {
            ApplyFilters(entry);
        }

        foreach (Entry entry in _entries.Where(e => e.Included))
        {
            Materialize(entry);
        }

        ApplyStreamConsistency();
        return TrimAndFinish();
    }

    // ------------------------------------------------------------------ journal (read FIRST, §17.2 item 2)

    private void ReadJournalFirst()
    {
        if (_options.RunId is { } requested && !IsSafeRunId(requested))
        {
            throw new BundleRefusedException($"--run must be a single run id, not a path ('{requested}').");
        }

        string journalPath = RunJournal.PathFor(_plan.PlanDirectory);
        if (File.Exists(journalPath) && IsLink(new FileInfo(journalPath)))
        {
            throw new BundleRefusedException("state/run.json is a symbolic link; the bundle never reads through a link.");
        }

        if (File.Exists(journalPath))
        {
            BundleRead read = _probes.Reader.Read(journalPath);
            if (read.Status == BundleReadStatus.Excluded)
            {
                throw new BundleRefusedException($"state/run.json could not be read ({read.Reason}).");
            }

            try
            {
                _journal = JsonSerializer.Deserialize<JournalDocument>(Encoding.UTF8.GetString(read.Bytes), JournalJson.Options);
            }
            catch (JsonException ex)
            {
                throw new BundleRefusedException($"state/run.json could not be parsed: {ex.Message}");
            }

            _journalRead = read;
        }

        if (_journal is null && _options.RunId is null)
        {
            throw new BundleRefusedException(
                "this plan has no run journal (state/run.json), so there is no run to bundle. Pass --run <id> to bundle "
                + "an earlier run's logs.");
        }

        _runId = _options.RunId ?? _journal!.RunId;
        _earlierRun = _journal is null || !string.Equals(_runId, _journal.RunId, StringComparison.Ordinal);
        _runLogs = Path.Combine(_plan.PlanDirectory, "logs", _runId);
    }

    private BundleRead? _journalRead;

    /// <summary>
    /// #805 S4: a <c>--run</c> value is one path segment — no separator, no <c>.</c> or <c>..</c>, not rooted, no invalid
    /// file-name character — so it can only name a directory directly under <c>logs/</c>.
    /// </summary>
    public static bool IsSafeRunId(string runId) =>
        runId.Length > 0
        && runId is not ("." or "..")
        && !runId.Contains("..", StringComparison.Ordinal)
        && runId.IndexOfAny(['/', '\\', ':']) < 0
        && runId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !Path.IsPathRooted(runId);

    // W4 (#805): git's identity, read at bundle time through the lock-free process-wide git settings (§17.2 item 5).
    private string? GitConfig(string key)
    {
        string directory = Directory.Exists(_plan.Workspace) ? _plan.Workspace : _plan.PlanDirectory;
        BundleProcessResult result = _probes.Git.Run(directory, ["config", "--get", key]);
        string value = result.Succeeded ? result.StandardOutput.Trim() : string.Empty;
        return value.Length > 0 ? value : null;
    }

    /// <summary>The journal that describes the bundled run, or null for an earlier run (built from its logs alone).</summary>
    private JournalDocument? RunJournalDoc => _earlierRun ? null : _journal;

    // ------------------------------------------------------------------ known values and exemptions

    private BundleSecrets CollectSecrets()
    {
        var envEntries = new List<KeyValuePair<string, string>>();
        foreach (PromptRunnerConfig block in _plan.Config.PromptRunners.Values.OrderBy(b => b.Name, StringComparer.Ordinal))
        {
            envEntries.AddRange(block.Settings.Env.OrderBy(p => p.Key, StringComparer.Ordinal));
            if (block.GuardrailOverrides?.Env is { } overrides)
            {
                envEntries.AddRange(overrides.OrderBy(p => p.Key, StringComparer.Ordinal));
            }
        }

        foreach (TaskNode task in _plan.Tasks.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            envEntries.AddRange(task.Action.Env.OrderBy(p => p.Key, StringComparer.Ordinal));
        }

        return BundleSecrets.Collect(_probes.Environment, BundleD1.BlockVariables(_plan), envEntries);
    }

    // §17.6.2: whole tokens enumerated from the plan and the journal — never from a disk walk.
    private HashSet<string> CollectExemptTokens()
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal) { PlanName, _runId, $"guardrails/{PlanName}" };
        tokens.UnionWith(BundleCatalog.KnownFileNames);
        foreach (TaskNode task in _plan.Tasks)
        {
            tokens.Add(task.Id);
            tokens.UnionWith(task.Guardrails.Select(g => g.Name));
            tokens.UnionWith(task.Preflights.Select(g => g.Name));
        }

        foreach (WaveNode wave in _plan.Waves)
        {
            tokens.Add(wave.Dir);
            tokens.Add(wave.Slug);
            tokens.UnionWith(wave.Guardrails.Select(g => g.Name));
            tokens.UnionWith(wave.Preflights.Select(g => g.Name));
        }

        tokens.UnionWith(_plan.PlanPreflights.Select(g => g.Name));
        tokens.UnionWith(_plan.PlanGuardrails.Select(g => g.Name));

        if (_journal is not null)
        {
            foreach ((string taskId, TaskJournalEntry entry) in _journal.Tasks)
            {
                tokens.Add(taskId);
                foreach (AttemptRecord attempt in entry.Attempts)
                {
                    tokens.Add($"attempt-{attempt.Attempt}");
                    AddBranch(tokens, attempt.Provenance?.SegmentBranch);
                }
            }

            AddBranch(tokens, _journal.Delivery?.PlanBranch);
            AddBranch(tokens, _journal.Delivery?.DeliveredToBranch);
            AddBranch(tokens, _journal.DeliveryTarget);
        }

        for (int n = 1; n <= 50; n++)
        {
            tokens.Add($"attempt-{n}");
        }

        // The segments of the roots the plan and journal name, so an absolute path to them is not an entropy hit.
        // A known value inside one (a session id in a temp path) is still scrubbed: exemptions never touch pass 1.
        IEnumerable<string?> roots =
        [
            _plan.PlanDirectory, _plan.Workspace, _probes.Home, _probes.WorktreeRoot,
            .. (_journal?.Tasks.Values.SelectMany(t => t.Attempts).Select(a => a.Provenance?.WorktreePath) ?? []),
        ];
        foreach (string root in roots.OfType<string>())
        {
            tokens.UnionWith(root.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries));
        }

        tokens.RemoveWhere(string.IsNullOrWhiteSpace);
        return tokens;
    }

    private static void AddBranch(HashSet<string> tokens, string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return;
        }

        tokens.Add(branch);
        tokens.UnionWith(branch.Split('/', StringSplitOptions.RemoveEmptyEntries));
    }

    private BundleRedactionContext ContextFor(string bundlePath, bool anonymize = true) => new(bundlePath, _probes.Environment)
    {
        Secrets = _secrets,
        ExemptTokens = _exempt,
        Paths = anonymize ? _anonymizer : null,
    };

    // An entry name or a MANIFEST path cell: known values and patterns, no entropy rule, no anonymizer (already applied).
    private BundleRedactionContext NameContext(string bundlePath) => new(bundlePath, _probes.Environment)
    {
        Secrets = _secrets,
        ExemptTokens = _exempt,
        Entropy = false,
    };

    // ------------------------------------------------------------------ collection (the allow-list, §17.3)

    private void CollectPlanFiles(string validateOutput)
    {
        Add(new Entry
        {
            BundlePath = PlanDir + "guardrails.json",
            SourcePath = Path.Combine(_plan.PlanDirectory, "guardrails.json"),
            Kind = BundleCatalog.PlanConfig,
        });

        foreach (TaskNode task in _plan.Tasks.Where(t => _selectedTasks.Contains(t.Id, StringComparer.Ordinal))
                     .OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            string file = Path.Combine(task.Directory, "task.json");
            Add(new Entry
            {
                BundlePath = PlanDir + Relative(_plan.PlanDirectory, file),
                SourcePath = file,
                Kind = BundleCatalog.TaskDefinition,
                TaskId = task.Id,
            });
        }

        Add(new Entry
        {
            BundlePath = PlanDir + "validate.txt",
            Kind = new BundleKind("validate", BundleClass.Lean, BundleCapClass.None, AgentText: false),
            Generated = ValidateLabel + "\n\n" + Lf(validateOutput),
            SourceLabel = "generated: guardrails validate (re-run)",
        });
    }

    /// <summary>§17.2 item 6: validate.txt's first line.</summary>
    public const string ValidateLabel = "re-run at bundle time; wrote only an OS temp directory; spawned bash/pwsh/git read-only";

    private void CollectJournalEntry()
    {
        string journalPath = RunJournal.PathFor(_plan.PlanDirectory);
        if (_journalRead is null)
        {
            return;
        }

        var entry = new Entry
        {
            BundlePath = "state/run.json",
            SourcePath = journalPath,
            Kind = BundleCatalog.Journal,
            PreRead = _journalRead,
        };
        if (_earlierRun)
        {
            // state/run.json describes the CURRENT run, not the one bundled.
            entry.Exclude("excluded", "other-run-journal", _journalRead.BytesRead);
        }

        Add(entry);
    }

    private void CollectStateFragments()
    {
        string stateDir = Path.Combine(_plan.PlanDirectory, "state");
        if (!Directory.Exists(stateDir))
        {
            return;
        }

        foreach (string file in SortedFiles(stateDir))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".json", StringComparison.Ordinal) && name != "run.json")
            {
                Add(Listed(file, "excluded", "state-fragments-phase-2"));
            }
        }
    }

    private void CollectLogs()
    {
        if (!Directory.Exists(_runLogs) || SkipLink(new DirectoryInfo(_runLogs)))
        {
            return;
        }

        var waveDirs = _plan.Waves.Select(w => w.Dir).ToHashSet(StringComparer.Ordinal);
        var taskIds = _plan.Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

        foreach (string file in SortedFiles(_runLogs))
        {
            string name = Path.GetFileName(file);
            Add(BundleCatalog.RunFile(name) is { } kind
                ? new Entry { BundlePath = "run/" + name, SourcePath = file, Kind = kind }
                : Listed(file, "listed-only", "unknown-kind"));
        }

        foreach (string dir in SortedDirectories(_runLogs))
        {
            string name = Path.GetFileName(dir);
            switch (name)
            {
                case "escalations":
                    foreach (string file in SortedFiles(dir))
                    {
                        Add(file.EndsWith(".json", StringComparison.Ordinal)
                            ? new Entry { BundlePath = "run/escalations/" + Path.GetFileName(file), SourcePath = file, Kind = BundleCatalog.Escalation }
                            : Listed(file, "listed-only", "unknown-kind"));
                    }

                    ListUnknownDirectories(dir);
                    break;
                case "preflights" or "guardrails":
                    CollectGate(dir, "gates/" + name);
                    break;
                case "claude-config":
                    CollectClaudeConfig(dir);
                    break;
                default:
                    if (waveDirs.Contains(name))
                    {
                        CollectWave(dir, name);
                    }
                    else if (taskIds.Contains(name))
                    {
                        if (_selectedTasks.Contains(name, StringComparer.Ordinal))
                        {
                            CollectTask(dir, name);
                        }
                    }
                    else
                    {
                        ListRecursively(dir);
                    }

                    break;
            }
        }
    }

    private void CollectWave(string waveDir, string waveName)
    {
        foreach (string file in SortedFiles(waveDir))
        {
            Add(Listed(file, "listed-only", "unknown-kind"));
        }

        foreach (string dir in SortedDirectories(waveDir))
        {
            string name = Path.GetFileName(dir);
            if (name is "preflights" or "guardrails")
            {
                CollectGate(dir, $"gates/{waveName}/{name}");
            }
            else
            {
                ListRecursively(dir);
            }
        }
    }

    private void CollectGate(string gateDir, string bundlePrefix)
    {
        foreach (string file in SortedFiles(gateDir))
        {
            Add(Listed(file, "listed-only", "unknown-kind"));
        }

        foreach (string checkDir in SortedDirectories(gateDir))
        {
            string check = Path.GetFileName(checkDir);
            foreach (string file in SortedFiles(checkDir))
            {
                string name = Path.GetFileName(file);
                Add(BundleCatalog.GateFile(name) is { } kind
                    ? new Entry { BundlePath = $"{bundlePrefix}/{check}/{name}", SourcePath = file, Kind = kind }
                    : Listed(file, "listed-only", "unknown-kind"));
            }

            ListUnknownDirectories(checkDir);
        }
    }

    // claude-config/ beyond projects/**/*.jsonl is excluded by construction, and named — under --no-redact too.
    private void CollectClaudeConfig(string configDir, string? task = null, int? attempt = null)
    {
        Add(new Entry
        {
            SourcePath = configDir + Path.DirectorySeparatorChar,
            Status = "excluded",
            Reason = "claude-config-excluded",
            Included = false,
        });

        string projects = Path.Combine(configDir, "projects");
        if (!Directory.Exists(projects))
        {
            return;
        }

        // Claude Code names each project directory after the cwd with every separator turned into '-'
        // (C--Users-<user>-AppData-…), so the ORIGINAL name encodes a local path and must never ship: not in an entry
        // name, not in MANIFEST. Each directory becomes project-<n>, numbered by the ordinal sort of its original name
        // so the bytes stay deterministic. The original name is kept in memory only, for trim-tier attribution.
        string prefix = task is null ? "gateway/sessions/" : $"gateway/sessions/{task}/attempt-{attempt}/";
        string configLabel = Relative(_plan.PlanDirectory, configDir) + "/projects/";
        var projectNumbers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string dir in SortedDirectories(projects))
        {
            projectNumbers[Path.GetFileName(dir)] = projectNumbers.Count + 1;
        }

        foreach (string file in WalkFiles(projects, name => name.EndsWith(".jsonl", StringComparison.Ordinal)))
        {
            string relative = Relative(projects, file);
            int slash = relative.IndexOf('/', StringComparison.Ordinal);
            string? original = slash < 0 ? null : relative[..slash];
            string shipped = original is null ? relative : $"project-{projectNumbers[original]}{relative[slash..]}";
            var session = new Entry
            {
                BundlePath = prefix + shipped,
                SourcePath = file,
                SourceLabel = configLabel + shipped + (original is null ? string.Empty : " (original name withheld: encodes a local path)"),
                Kind = BundleCatalog.GatewaySession,
                TaskId = task,
                Attempt = attempt,
                SessionProjectDir = original,
            };

            // #805 S2: --task narrows per-task evidence, and a run-level session is per-task evidence once attributed. A
            // session of an unselected task, or one no attempt claims, is listed only.
            string? owner = task is null && _options.Tasks.Count > 0 ? AttributedAttempt(session).Task : task;
            if (task is null && _options.Tasks.Count > 0 && (owner is null || !_selectedTasks.Contains(owner, StringComparer.Ordinal)))
            {
                session.Exclude("listed-only", "task-filter", BundleFileStat.Of(file)?.Length ?? 0);
            }

            Add(session);
        }
    }

    // ------------------------------------------------------------------ entry names (§17.5, belt and braces)

    /// <summary>
    /// Every entry name passes the path anonymizer and the known-value and pattern passes before it is written. A name
    /// the anonymizer changes ships anonymized; a name a secret pass changes is excluded, fail closed (<c>unsafe-name</c>),
    /// and its MANIFEST row shows the redacted name. Runs before anything is read.
    /// </summary>
    private void SanitizeEntryNames()
    {
        foreach (Entry entry in _entries.Where(e => e.BundlePath is not null && !e.NameChecked))
        {
            entry.NameChecked = true;
            string name = entry.BundlePath!;
            if (_anonymizer is not null)
            {
                name = _anonymizer.Apply(name);
            }

            if (!_options.NoRedact)
            {
                BundleRedactionResult result = BundleRedactor.Redact(name, NameContext(name));
                if (result.Labels.Count > 0)
                {
                    entry.BundlePath = result.Text;
                    entry.Exclude("excluded", "unsafe-name", 0);
                    continue;
                }
            }

            entry.BundlePath = name;
        }
    }

    // A MANIFEST source cell: anonymized, then through the secret passes (a known value in a directory name never ships).
    private string SafeCell(string text)
    {
        string anonymized = _anonymizer is null ? text : _anonymizer.Apply(text);
        return _options.NoRedact ? anonymized : BundleRedactor.Redact(anonymized, NameContext("MANIFEST.md")).Text;
    }

    private void CollectTask(string taskDir, string taskId)
    {
        TaskJournalEntry? journalEntry = null;
        RunJournalDoc?.Tasks.TryGetValue(taskId, out journalEntry);
        var journaled = new HashSet<int>(journalEntry?.Attempts.Select(a => a.Attempt) ?? []);
        if (journalEntry?.InFlightAttempt is { } marker)
        {
            journaled.Add(marker.Attempt);
        }

        foreach (string file in SortedFiles(taskDir))
        {
            string name = Path.GetFileName(file);
            Add(BundleCatalog.TaskFile(name) is { } kind
                ? new Entry { BundlePath = $"tasks/{taskId}/{name}", SourcePath = file, Kind = kind, TaskId = taskId }
                : Listed(file, "listed-only", "unknown-kind"));
        }

        foreach (string dir in SortedDirectories(taskDir))
        {
            string name = Path.GetFileName(dir);
            if (AttemptNumber(name) is not { } attempt)
            {
                ListRecursively(dir);
                continue;
            }

            bool newer = RunJournalDoc is not null && !journaled.Contains(attempt);
            foreach (string file in SortedFiles(dir))
            {
                string fileName = Path.GetFileName(file);
                if (BundleCatalog.AttemptFile(fileName) is { } kind)
                {
                    Add(new Entry
                    {
                        BundlePath = $"tasks/{taskId}/{name}/{fileName}",
                        SourcePath = file,
                        Kind = kind,
                        TaskId = taskId,
                        Attempt = attempt,
                        NewerThanJournal = newer,
                    });
                }
                else
                {
                    Add(Listed(file, "listed-only", newer ? "newer-than-journal" : "unknown-kind"));
                }
            }

            foreach (string inner in SortedDirectories(dir))
            {
                if (Path.GetFileName(inner) == "claude-config")
                {
                    CollectClaudeConfig(inner, taskId, attempt);
                }
                else
                {
                    ListRecursively(inner);
                }
            }
        }
    }

    private static int? AttemptNumber(string directoryName) =>
        directoryName.StartsWith("attempt-", StringComparison.Ordinal)
        && int.TryParse(directoryName.AsSpan("attempt-".Length), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int n)
            ? n
            : null;

    private void ListRecursively(string dir)
    {
        foreach (string file in WalkFiles(dir, _ => true))
        {
            Add(Listed(file, "listed-only", "unknown-kind"));
        }
    }

    private void ListUnknownDirectories(string dir)
    {
        foreach (string inner in SortedDirectories(dir))
        {
            ListRecursively(inner);
        }
    }

    private Entry Listed(string file, string status, string reason)
    {
        long size = 0;
        try
        {
            size = new FileInfo(file).Length;
        }
        catch (IOException)
        {
            // Listed by name alone.
        }

        var entry = new Entry { SourcePath = file };
        entry.Exclude(status, reason, size);
        return entry;
    }

    private void Add(Entry entry) => _entries.Add(entry);

    // ------------------------------------------------------------------ filters (--lean, --without-agent-text)

    private void ApplyFilters(Entry entry)
    {
        if (!entry.Included || entry.Kind is not { } kind)
        {
            return;
        }

        if (_options.WithoutAgentText && kind.AgentText)
        {
            entry.Exclude("withheld", "agent-text", 0);
        }
        else if (_options.Lean && kind.Class == BundleClass.Full)
        {
            entry.Exclude("withheld", "lean", 0);
        }
    }

    // ------------------------------------------------------------------ materialize: read, decode, project, redact

    private long? CapFor(BundleKind kind) => kind.Protected ? null : kind.Cap switch
    {
        BundleCapClass.Stream => _streamCap,
        BundleCapClass.Log => _logCap,
        _ => null
    };

    private void Materialize(Entry entry)
    {
        BundleKind kind = entry.Kind!;
        string text;
        if (entry.Generated is { } generated)
        {
            text = generated;
            entry.Bytes = Encoding.UTF8.GetByteCount(generated);
        }
        else
        {
            if (entry.Raw is null && entry.PreRead is null && IsLink(new FileInfo(entry.SourcePath!)))
            {
                entry.Exclude("excluded", "symlink", 0);
                return;
            }

            if (entry.Raw is null)
            {
                BundleRead read = entry.PreRead ?? _probes.Reader.Read(
                    entry.SourcePath!,
                    tailCap: kind.Cap == BundleCapClass.Patch ? null : CapFor(kind),
                    wholeOrNothingCap: kind.Cap == BundleCapClass.Patch ? BundleCatalog.StreamCap : null);
                entry.Bytes = read.BytesRead;
                if (read.Status == BundleReadStatus.Excluded)
                {
                    entry.Exclude("excluded", read.Reason!, read.Length);
                    return;
                }

                entry.Raw = read.Bytes;
                entry.ReadReason = read.Reason;
                entry.Status = read.Status == BundleReadStatus.Tail ? "tail" : "included";
                entry.Reason = read.Reason ?? string.Empty;
                if (entry.NewerThanJournal)
                {
                    entry.Reason = "newer-than-journal";
                }
            }

            byte[] bytes = entry.Raw;
            if (entry.CapOverride is { } smaller && BundleFileReader.Retail(bytes, smaller) is { } cut)
            {
                bytes = cut.Bytes;
            }
            else if (entry.CapOverride is not null)
            {
                entry.Exclude("trimmed", "trim-tier-4", entry.Bytes);
                return;
            }

            if (!TryDecodeUtf8(bytes, out text))
            {
                entry.Exclude("excluded", "not-utf8", entry.Bytes);
                return;
            }
        }

        if (_options.WithoutAgentText && kind.Projected)
        {
            if (AgentTextProjection.Project(text) is not { } projected)
            {
                entry.Exclude("excluded", "scan-failed", entry.Bytes);
                return;
            }

            text = projected;
        }

        if (_options.NoRedact)
        {
            entry.SetContent(_anonymizer is null ? text : _anonymizer.Apply(text), []);
            return;
        }

        // #805 S7: no wall-clock budget. Each Regex carries its own timeout; a RegexMatchTimeoutException is the only
        // timing exclusion, and it is never shipped raw.
        BundleRedactionResult result;
        try
        {
            result = _probes.Redact(text, ContextFor(entry.BundlePath!));
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) when (entry.Raw is { } raw && IsProtectedLatest(entry))
        {
            // The latest attempt of a failing or in-flight task is the evidence that matters most: never lose it to timing
            // alone. Retry on a halved tail, down to 64 KiB, before giving up (fail closed).
            long next = Math.Min(entry.CapOverride ?? raw.LongLength, raw.LongLength) / 2;
            if (next >= MinimumRetryTail)
            {
                entry.CapOverride = next;
                Materialize(entry);
                if (entry.Included)
                {
                    entry.Status = "tail";
                    entry.Reason = "scan-timeout";
                }

                return;
            }

            entry.Exclude("excluded", "scan-timeout", entry.Bytes);
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            entry.Exclude("excluded", ex is System.Text.RegularExpressions.RegexMatchTimeoutException ? "scan-timeout" : "scan-failed", entry.Bytes);
            return;
        }

        entry.SetContent(result.Text, result.Labels);
    }

    private const long MinimumRetryTail = 64L * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static bool TryDecodeUtf8(byte[] bytes, out string text)
    {
        try
        {
            text = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    // ------------------------------------------------------------------ pass 4: stream consistency

    private void ApplyStreamConsistency()
    {
        if (_options.NoRedact)
        {
            return;
        }

        foreach (Entry transcript in _entries.Where(e => e.HasContent && e.Kind?.Kind == "transcript"))
        {
            string siblingPath = StreamSiblingOf(transcript.BundlePath!);
            Entry? stream = _entries.FirstOrDefault(e => e.HasContent && e.BundlePath == siblingPath);
            if (stream is null)
            {
                continue;
            }

            var inStream = stream.Labels.ToHashSet(StringComparer.Ordinal);
            if (transcript.Labels.Any(label => !inStream.Contains(label)))
            {
                stream.Exclude("excluded", "stream-scrubbed-less", stream.Bytes);
            }
        }
    }

    private static string StreamSiblingOf(string transcriptPath)
    {
        int slash = transcriptPath.LastIndexOf('/');
        string dir = transcriptPath[..(slash + 1)];
        string name = transcriptPath[(slash + 1)..];
        return name == "transcript.md"
            ? dir + "claude-stream.jsonl"
            : dir + name[..^".transcript.md".Length] + ".stream.jsonl";
    }

    // ------------------------------------------------------------------ helpers

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    // #805 S6: a symbolic link or any other reparse point is never read and never recursed through. It is listed once,
    // by its own path, as excluded (`symlink`): a link could point anywhere, outside the plan included.
    private IEnumerable<string> SortedFiles(string dir) =>
        Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.Ordinal).Where(f => !SkipLink(new FileInfo(f))).ToList();

    private IEnumerable<string> SortedDirectories(string dir) =>
        Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.Ordinal).Where(d => !SkipLink(new DirectoryInfo(d))).ToList();

    /// <summary>Every non-link file under <paramref name="dir"/> matching <paramref name="pattern"/>, never through a link.</summary>
    private List<string> WalkFiles(string dir, Func<string, bool> pattern)
    {
        var files = new List<string>(SortedFiles(dir).Where(f => pattern(Path.GetFileName(f))));
        foreach (string inner in SortedDirectories(dir))
        {
            files.AddRange(WalkFiles(inner, pattern));
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private readonly HashSet<string> _linksListed = new(StringComparer.Ordinal);

    private bool SkipLink(FileSystemInfo info)
    {
        if (!IsLink(info))
        {
            return false;
        }

        if (_linksListed.Add(info.FullName))
        {
            var row = new Entry { SourcePath = info.FullName };
            row.Exclude("excluded", "symlink", 0);
            Add(row);
        }

        return true;
    }

    /// <summary>A symbolic link, junction or any other reparse point.</summary>
    public static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));

    private string DisplaySource(Entry entry)
    {
        if (entry.SourceLabel is { } label)
        {
            return SafeCell(label);
        }

        if (entry.SourcePath is not { } path)
        {
            return "generated";
        }

        return SafeCell(path.Replace('\\', '/'));
    }

    /// <summary>One considered file: a bundle entry, or a MANIFEST.md row only.</summary>
    private sealed class Entry
    {
        public string? BundlePath { get; set; }

        public bool NameChecked { get; set; }

        public string? SourcePath { get; init; }

        public string? SourceLabel { get; init; }

        public BundleKind? Kind { get; init; }

        public string? TaskId { get; init; }

        public int? Attempt { get; init; }

        public bool NewerThanJournal { get; init; }

        public string? SessionProjectDir { get; init; }

        public string? Generated { get; set; }

        public BundleRead? PreRead { get; init; }

        public bool Included { get; set; } = true;

        public string Status { get; set; } = "included";

        public string Reason { get; set; } = string.Empty;

        public long Bytes { get; set; }

        public byte[]? Raw { get; set; }

        public string? ReadReason { get; set; }

        public long? CapOverride { get; set; }

        public byte[]? Content { get; private set; }

        public IReadOnlyList<string> Labels { get; private set; } = [];

        public bool HasContent => Included && Content is not null;

        public void SetContent(string text, IReadOnlyList<string> labels)
        {
            Content = Encoding.UTF8.GetBytes(text);
            Labels = labels;
        }

        public void Exclude(string status, string reason, long bytes)
        {
            Included = false;
            Content = null;
            Status = status;
            Reason = reason;
            Bytes = bytes;
        }
    }
}
