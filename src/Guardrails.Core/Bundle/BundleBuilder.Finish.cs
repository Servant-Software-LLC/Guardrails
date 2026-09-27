using System.Text;
using System.Text.Json;
using Guardrails.Core.Journal;

namespace Guardrails.Core.Bundle;

public sealed partial class BundleBuilder
{
    // ------------------------------------------------------------------ git evidence (§17.2 item 5, §17.3 git/)

    private void CollectGit()
    {
        // The git files are generated AFTER the logs are read: the segment worktrees and their base commits come
        // from provenance, which may be on disk only (a newer-than-journal attempt, or an earlier run).
        _gitPending = true;
    }

    private bool _gitPending;

    private void AddGitEntries()
    {
        if (!_gitPending)
        {
            return;
        }

        _gitPending = false;
        var added = new List<Entry>();
        (string? integrationDir, bool worktreeMode) = IntegrationDirectory();
        if (integrationDir is not null && Directory.Exists(integrationDir))
        {
            added.AddRange(GitEntries("integration", integrationDir, baseCommit: null,
                worktreeMode ? "the integration base commit is not recorded" : "serial mode records no base commit"));
        }

        foreach (string taskId in _selectedTasks)
        {
            if (LatestProvenance(taskId) is { WorktreePath: { } worktree } provenance && Directory.Exists(worktree))
            {
                added.AddRange(GitEntries(taskId, worktree, provenance.BaseCommit, "the segment's base commit is not recorded"));
            }
        }

        foreach (Entry entry in added)
        {
            Add(entry);
            ApplyFilters(entry);
            if (entry.Included)
            {
                Materialize(entry);
            }
        }
    }

    private IEnumerable<Entry> GitEntries(string name, string directory, string? baseCommit, string noBaseReason)
    {
        var text = new StringBuilder();
        text.Append("# git evidence: ").Append(name).Append('\n');
        text.Append("# ").Append(directory.Replace('\\', '/')).Append("\n\n");
        AppendGit(text, directory, ["status", "--porcelain=v1", "-b"]);
        AppendGit(text, directory, _options.WithoutAgentText
            ? ["log", "-5", "--format=%h %ad"]
            : ["log", "-5", "--format=%h %ad %s"]);
        if (baseCommit is null)
        {
            text.Append("$ git diff --stat <base>..HEAD\n(omitted: ").Append(noBaseReason).Append(")\n\n");
        }
        else
        {
            AppendGit(text, directory, ["diff", "--stat", $"{baseCommit}..HEAD"]);
        }

        yield return new Entry
        {
            BundlePath = $"git/{name}.txt",
            Kind = new BundleKind("git", BundleClass.Lean, BundleCapClass.None, AgentText: false),
            Generated = text.ToString(),
            SourceLabel = $"generated: git status, log -5, diff --stat ({name})",
        };

        if (_options.IncludeWorktreeDiff)
        {
            string diff;
            if (baseCommit is null)
            {
                diff = $"(omitted: {noBaseReason})\n";
            }
            else
            {
                BundleProcessResult result = _probes.Git.Run(directory, ["diff", $"{baseCommit}..HEAD"]);
                diff = GitOutput(result);
            }

            yield return new Entry
            {
                BundlePath = $"git/{name}.diff",
                Kind = new BundleKind("worktree-diff", BundleClass.Full, BundleCapClass.None, AgentText: true),
                Generated = diff,
                SourceLabel = $"generated: git diff ({name})",
            };
        }
    }

    private void AppendGit(StringBuilder text, string directory, IReadOnlyList<string> arguments)
    {
        text.Append("$ git ").Append(string.Join(' ', arguments)).Append('\n');
        text.Append(GitOutput(_probes.Git.Run(directory, arguments))).Append('\n');
    }

    private static string GitOutput(BundleProcessResult result)
    {
        if (result.NotFound)
        {
            return "(git is not on PATH)\n";
        }

        if (result.TimedOut)
        {
            return "(timed out after 30 s)\n";
        }

        string output = Lf(result.StandardOutput);
        if (output.Length > 0 && !output.EndsWith('\n'))
        {
            output += "\n";
        }

        if (result.ExitCode != 0)
        {
            string firstError = Lf(result.StandardError).Split('\n').FirstOrDefault(l => l.Length > 0) ?? string.Empty;
            output += $"(git exited {result.ExitCode}: {firstError})\n";
        }

        return output;
    }

    private (string? Directory, bool WorktreeMode) IntegrationDirectory()
    {
        string? segment = AllProvenance().Select(p => p.WorktreePath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        if (segment is null)
        {
            // Serial mode: the repository holding the plan is the integration tree.
            return (_plan.Workspace, false);
        }

        string? runRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(segment)));
        string? candidate = runRoot is null ? null : Path.Combine(runRoot, "_integration");
        if (candidate is not null && Directory.Exists(candidate))
        {
            return (candidate, true);
        }

        return (_probes.WorktreeRoot is { } root ? Path.Combine(root, _runId, "_integration") : null, true);
    }

    // ------------------------------------------------------------------ provenance, journal and disk

    private IEnumerable<AttemptProvenance> AllProvenance()
    {
        if (RunJournalDoc is { } journal)
        {
            foreach (TaskJournalEntry entry in journal.Tasks.Values)
            {
                foreach (AttemptRecord attempt in entry.Attempts)
                {
                    if (attempt.Provenance is { } provenance)
                    {
                        yield return provenance;
                    }
                }
            }
        }

        foreach (Entry entry in _entries.Where(e => e.Kind?.Kind == "provenance" && e.Raw is not null))
        {
            if (ParseProvenance(entry.Raw!) is { } provenance)
            {
                yield return provenance;
            }
        }
    }

    private AttemptProvenance? LatestProvenance(string taskId)
    {
        int? latest = AttemptsOnDisk(taskId).DefaultIfEmpty().Max();
        return latest is > 0 ? ProvenanceOf(taskId, latest.Value) : null;
    }

    private AttemptProvenance? ProvenanceOf(string taskId, int attempt)
    {
        Entry? file = _entries.FirstOrDefault(e =>
            e.TaskId == taskId && e.Attempt == attempt && e.Kind?.Kind == "provenance" && e.Raw is not null);
        if (file is not null && ParseProvenance(file.Raw!) is { } fromDisk)
        {
            return fromDisk;
        }

        return RunJournalDoc?.Tasks.GetValueOrDefault(taskId)?.Attempts.FirstOrDefault(a => a.Attempt == attempt)?.Provenance;
    }

    private static AttemptProvenance? ParseProvenance(byte[] raw)
    {
        try
        {
            return JsonSerializer.Deserialize<AttemptProvenance>(raw, JournalJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private IEnumerable<int> AttemptsOnDisk(string taskId)
    {
        IEnumerable<int> journaled = RunJournalDoc?.Tasks.GetValueOrDefault(taskId)?.Attempts.Select(a => a.Attempt) ?? [];
        return _entries.Where(e => e.TaskId == taskId && e.Attempt is not null).Select(e => e.Attempt!.Value)
            .Concat(journaled).Concat(AttemptDirectories(taskId)).Distinct().OrderBy(n => n);
    }

    private IEnumerable<int> AttemptDirectories(string taskId)
    {
        string dir = Path.Combine(_runLogs, taskId);
        return Directory.Exists(dir)
            ? Directory.EnumerateDirectories(dir).Select(d => AttemptNumber(Path.GetFileName(d))).OfType<int>()
            : [];
    }

    // ------------------------------------------------------------------ the size budget (§17.7)

    private BundleOutcome TrimAndFinish()
    {
        AddGitEntries();
        ApplyStreamConsistency();

        long cap = _options.MaxSizeBytes;
        IReadOnlyList<(int Tier, Func<bool> Apply)> steps = TrimSteps();
        Dictionary<string, byte[]> documents = Documents(trimmed: false);
        long size = Estimate(documents);
        int step = 0;
        while (size > cap && step < steps.Count)
        {
            (int tier, Func<bool> apply) = steps[step++];
            if (apply())
            {
                string note = $"trim-tier-{tier}";
                if (!_trimNotes.Contains(note, StringComparer.Ordinal))
                {
                    _trimNotes.Add(note);
                }
            }

            documents = Documents(trimmed: _trimNotes.Count > 0);
            size = Estimate(documents);
        }

        documents = Documents(trimmed: _trimNotes.Count > 0);
        List<KeyValuePair<string, byte[]>> entries =
        [
            .. _entries.Where(e => e.HasContent).Select(e => new KeyValuePair<string, byte[]>(e.BundlePath!, e.Content!)),
            .. documents,
        ];
        entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        byte[] zip = BundleZip.Build(entries);

        return new BundleOutcome
        {
            RunId = _runId,
            Entries = entries,
            Zip = zip,
            OverCap = zip.LongLength > cap,
            Trimmed = _trimNotes.Count > 0,
            Manifest = ManifestRows(),
            LeanWithheld = LeanWithheldPaths(),
        };
    }

    private long Estimate(Dictionary<string, byte[]> documents)
    {
        long total = BundleZip.EmptyArchiveSize;
        foreach (Entry entry in _entries.Where(e => e.HasContent))
        {
            total += SizeOf(entry.BundlePath!, entry.Content!);
        }

        foreach ((string path, byte[] content) in documents)
        {
            total += SizeOf(path, content);
        }

        return total;
    }

    private long SizeOf(string path, byte[] content)
    {
        if (!_sizeCache.TryGetValue((path, content), out long size))
        {
            size = BundleZip.EntrySize(path, content);
            _sizeCache[(path, content)] = size;
        }

        return size;
    }

    private List<(int Tier, Func<bool> Apply)> TrimSteps()
    {
        var steps = new List<(int, Func<bool>)>
        {
            (1, () => Drop(e => e.Kind?.Kind == "worktree-diff", 1)),
            (2, () => Drop(e => e.Kind?.Kind is "stream" or "gateway-session" && !IsFirstOrLatest(e), 2)),
        };

        // Tier 3: the MIDDLE attempts' transcripts and composed prompts, oldest attempt first, one attempt per step.
        foreach ((string task, int attempt) in MiddleAttemptsOldestFirst())
        {
            steps.Add((3, () => Drop(
                e => e.TaskId == task && e.Attempt == attempt && e.Kind?.Kind is "transcript" or "composed-prompt", 3)));
        }

        steps.Add((4, () => HalveCaps(BundleCatalog.StreamCap / 2, BundleCatalog.LogCap / 2)));
        steps.Add((4, () => HalveCaps(BundleCatalog.StreamCap / 4, BundleCatalog.LogCap / 2)));
        steps.Add((5, () => Drop(e => e.Kind?.Kind is "stream" or "transcript" && IsFirstAttempt(e) && !IsProtectedLatest(e), 5)));
        return steps;
    }

    private bool Drop(Func<Entry, bool> predicate, int tier)
    {
        bool any = false;
        foreach (Entry entry in _entries.Where(e => e.HasContent && e.Kind is { Protected: false } && predicate(e)))
        {
            entry.Exclude("trimmed", $"trim-tier-{tier}", entry.Bytes);
            any = true;
        }

        return any;
    }

    private bool HalveCaps(long streamCap, long logCap)
    {
        if (_streamCap == streamCap && _logCap == logCap)
        {
            return false;
        }

        _streamCap = streamCap;
        _logCap = logCap;
        bool any = false;
        foreach (Entry entry in _entries.Where(e => e.HasContent && e.Raw is not null && e.Kind is { Protected: false }).ToList())
        {
            if (CapFor(entry.Kind!) is not { } cap || entry.Raw!.LongLength <= cap)
            {
                continue;
            }

            entry.CapOverride = cap;
            Materialize(entry);
            if (entry.Included)
            {
                entry.Status = "trimmed";
                entry.Reason = "trim-tier-4";
            }

            any = true;
        }

        ApplyStreamConsistency();
        return any;
    }

    private (int First, int Latest)? AttemptRange(string? taskId)
    {
        if (taskId is null)
        {
            return null;
        }

        List<int> attempts = [.. AttemptsOnDisk(taskId)];
        return attempts.Count == 0 ? null : (attempts[0], attempts[^1]);
    }

    private bool IsFirstOrLatest(Entry entry)
    {
        (string? task, int? attempt) = AttributedAttempt(entry);
        return attempt is { } n && AttemptRange(task) is { } range && (n == range.First || n == range.Latest);
    }

    private bool IsFirstAttempt(Entry entry)
    {
        (string? task, int? attempt) = AttributedAttempt(entry);
        return attempt is { } n && AttemptRange(task) is { } range && n == range.First;
    }

    // Never the latest attempt of a failing or in-flight task (every task not succeeded; for an earlier run, every task).
    private bool IsProtectedLatest(Entry entry)
    {
        (string? task, int? attempt) = AttributedAttempt(entry);
        if (task is null || attempt is not { } n || AttemptRange(task) is not { } range || n != range.Latest)
        {
            return false;
        }

        TaskJournalEntry? journal = RunJournalDoc?.Tasks.GetValueOrDefault(task);
        return journal is null || journal.Status != Journal.TaskStatus.Succeeded || journal.InFlightAttempt is not null;
    }

    // A gateway session is attributed to the attempt whose segment worktree it ran in: Claude names a project
    // directory after the cwd with every non-alphanumeric character replaced by '-'. Unattributed: no attempt.
    private (string? Task, int? Attempt) AttributedAttempt(Entry entry)
    {
        if (entry.Attempt is not null || entry.Kind?.Kind != "gateway-session" || entry.SessionProjectDir is null)
        {
            return (entry.TaskId, entry.Attempt);
        }

        foreach (string task in _selectedTasks)
        {
            foreach (int attempt in AttemptsOnDisk(task))
            {
                if (entry.SessionProjectDir.EndsWith(ClaudeProjectName($"{task}/attempt-{attempt}"), StringComparison.Ordinal))
                {
                    return (task, attempt);
                }
            }
        }

        return (null, null);
    }

    private static string ClaudeProjectName(string path) =>
        new([.. path.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);

    private IEnumerable<(string Task, int Attempt)> MiddleAttemptsOldestFirst()
    {
        var middles = new List<(string Task, int Attempt, DateTimeOffset? Started)>();
        foreach (string task in _selectedTasks)
        {
            List<int> attempts = [.. AttemptsOnDisk(task)];
            foreach (int attempt in attempts.Skip(1).Take(Math.Max(0, attempts.Count - 2)))
            {
                DateTimeOffset? started = RunJournalDoc?.Tasks.GetValueOrDefault(task)?.Attempts
                    .FirstOrDefault(a => a.Attempt == attempt)?.StartedAt;
                middles.Add((task, attempt, started));
            }
        }

        return middles
            .OrderBy(m => m.Started ?? DateTimeOffset.MaxValue)
            .ThenBy(m => m.Attempt)
            .ThenBy(m => m.Task, StringComparer.Ordinal)
            .Select(m => (m.Task, m.Attempt));
    }

    // ------------------------------------------------------------------ the generated documents

    private Dictionary<string, byte[]> Documents(bool trimmed)
    {
        (string summary, IReadOnlyList<string> summaryLabels) = RenderSummary(trimmed);
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["SUMMARY.md"] = Encoding.UTF8.GetBytes(summary),
            ["MANIFEST.md"] = Encoding.UTF8.GetBytes(RenderManifest()),
            ["REDACTIONS.md"] = Encoding.UTF8.GetBytes(RenderRedactions(summaryLabels)),
        };
    }

    private IReadOnlyList<BundleManifestRow> ManifestRows() =>
    [
        .. _entries
            .Select(e => new BundleManifestRow(e.BundlePath, DisplaySource(e), e.Bytes, e.Status, e.Reason))
            .OrderBy(r => r.BundlePath ?? r.Source, StringComparer.Ordinal)
            .ThenBy(r => r.Source, StringComparer.Ordinal)
    ];

    private IReadOnlyList<string> LeanWithheldPaths() =>
    [
        .. _entries.Where(e => e is { Status: "withheld", Reason: "lean" }).Select(e => e.BundlePath!)
            .OrderBy(p => p, StringComparer.Ordinal)
    ];

    private string RenderManifest()
    {
        var text = new StringBuilder();
        text.Append("# MANIFEST\n\n");
        text.Append("Every file this bundle considered: included, tailed, withheld, excluded, trimmed, or listed only.\n\n");
        text.Append("| Bundle path | Source (anonymized) | Bytes read | Status | Reason |\n");
        text.Append("|---|---|---|---|---|\n");
        foreach (BundleManifestRow row in ManifestRows())
        {
            text.Append("| ").Append(Cell(row.BundlePath ?? "-"))
                .Append(" | ").Append(Cell(row.Source))
                .Append(" | ").Append(row.BytesRead.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(" | ").Append(row.Status)
                .Append(" | ").Append(row.Reason.Length == 0 ? "-" : row.Reason)
                .Append(" |\n");
        }

        var notes = new List<string>(_probes.Notes);
        if (_earlierRun)
        {
            notes.Add(_journal is null
                ? $"--run {_runId}: the plan has no journal, so this run's facts come from its logs alone."
                : $"--run {_runId}: state/run.json describes run {_journal.RunId}, not this one, so it is not included (other-run-journal).");
        }

        notes.AddRange(_trimNotes.Select(t => $"Trim tier applied: {t}."));
        if (notes.Count > 0)
        {
            text.Append("\n## Notes\n\n");
            foreach (string note in notes)
            {
                text.Append("- ").Append(note).Append('\n');
            }
        }

        return text.ToString();
    }

    private string RenderRedactions(IReadOnlyList<string> summaryLabels)
    {
        if (_options.NoRedact)
        {
            return NotRedactedLine + "\n";
        }

        var rows = new List<(string Path, string Label, int Count)>();
        foreach (Entry entry in _entries.Where(e => e.HasContent))
        {
            AddCounts(rows, entry.BundlePath!, entry.Labels);
        }

        AddCounts(rows, "SUMMARY.md", summaryLabels);
        rows.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path) is var c && c != 0 ? c : string.CompareOrdinal(a.Label, b.Label));

        var text = new StringBuilder();
        text.Append("# REDACTIONS\n\n");
        text.Append("A count per label or kind for every bundled file. Never a value, never a hash of a value. ");
        text.Append("Over-redaction is the accepted cost, and it is counted here.\n\n");
        text.Append("| Bundle path | Label or kind | Count |\n");
        text.Append("|---|---|---|\n");
        foreach ((string path, string label, int count) in rows)
        {
            text.Append("| ").Append(Cell(path)).Append(" | ").Append(Cell(label)).Append(" | ")
                .Append(count.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" |\n");
        }

        text.Append('\n').Append(BundleRedactor.CannotCatchText);
        return text.ToString();
    }

    private static void AddCounts(List<(string Path, string Label, int Count)> rows, string path, IReadOnlyList<string> labels)
    {
        if (labels.Count == 0)
        {
            rows.Add((path, "(none)", 0));
            return;
        }

        foreach (IGrouping<string, string> group in labels.GroupBy(l => l, StringComparer.Ordinal))
        {
            rows.Add((path, group.Key, group.Count()));
        }
    }

    /// <summary>The single line of an unredacted bundle's REDACTIONS.md, and SUMMARY.md's first line (§17.6.8).</summary>
    public const string NotRedactedLine = "NOT REDACTED: do not post publicly";

    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
