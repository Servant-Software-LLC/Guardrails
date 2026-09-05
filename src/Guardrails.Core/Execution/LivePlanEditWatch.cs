using System.Security.Cryptography;
using System.Text;
using Guardrails.Core.Hashing;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>One definition file whose presence or bytes moved between two <see cref="LivePlanEditWatch.Poll"/>
/// calls. <c>Label</c> is the <see cref="TaskDefinitionFiles.Enumerate"/> label — a <c>/</c>-normalized
/// task-folder-relative path (<c>task.json</c>, <c>action:&lt;rel&gt;</c>, <c>guardrails/…</c>,
/// <c>preflights/…</c>) — so the advisory can name the file without any git involvement.</summary>
public sealed record PlanEditedFile(string TaskId, string Label, PlanEditKind Kind);

/// <summary>How one definition file moved: it appeared, it vanished, or its bytes changed.</summary>
public enum PlanEditKind { Added, Removed, Modified }

/// <summary>Every definition file of ONE task that moved since the last poll, with the task's watch-level
/// definition hash either side. <c>Files</c> is the per-file breakdown §5.2 buys over a whole-task hash.</summary>
public sealed record PlanEdit(string TaskId, string OldHash, string NewHash,
                              IReadOnlyList<PlanEditedFile> Files);

/// <summary>
/// Detects an OPERATOR edit to the plan folder made while the run is live (plan 31 §5.2, issue #545 part 3).
/// A passive object: it holds a per-FILE baseline of every task's definition surface and recomputes it when
/// asked. No <c>FileSystemWatcher</c>, no thread, no lock, no daemon (invariant 6) — the Scheduler calls
/// <see cref="Poll"/> on its own thread at the two boundaries that already exist (task dispatch, task settle),
/// which costs timeliness (the warning appears at the next boundary, not instantly) and buys the harness's
/// own writes not firing it.
///
/// <para>The baseline is, per task, the per-file hashes of <see cref="TaskDefinitionFiles.Enumerate"/> —
/// <c>task.json</c>, the resolved action file, <c>guardrails/**</c> and <c>preflights/**</c> — folded through
/// the same <see cref="HashText"/> primitive <see cref="TaskDefinitionHash"/> uses, so the two cannot disagree
/// about what defines a task. <c>logs/</c> and <c>state/</c> are outside that enumeration, which is the
/// structural reason the harness's own constant writes under the plan folder cannot trigger an advisory that
/// exists to report HUMAN edits (an advisory that fires on the harness's own writes stops being read, #229).</para>
///
/// <para><b>One deliberate divergence from the hash: the editor-artifact ignore list, applied HERE and NOT in
/// <see cref="HashText"/>.</b> <c>HashText.EnumerateFolderFiles</c> lists <c>"*"</c> recursively and filters
/// nothing, so a stray <c>.DS_Store</c> / <c>Thumbs.db</c> / <c>*.swp</c> / <c>*.orig</c> / <c>*.rej</c> in a
/// <c>guardrails/</c> folder IS part of a task's definition today — and must stay that way. That function feeds
/// <see cref="TaskDefinitionHash"/> and <see cref="PlanDefinitionHash"/>, so changing its file set would move
/// every recorded definition hash in every plan, and a moved definition hash is a definition-drift HALT on the
/// next resume. Dropping the patterns only here makes the watch strictly QUIETER than the hash and never
/// noisier; anything the hash sees and the watch ignores is a pre-existing drift condition the resume-time
/// check already owns.</para>
/// </summary>
public sealed class LivePlanEditWatch
{
    /// <summary>Whole file names that are editor/OS junk wherever they appear (see the class remarks).</summary>
    private static readonly string[] IgnoredFileNames = [".DS_Store", "Thumbs.db"];

    /// <summary>Suffixes that are editor/merge junk: a vim swap file and git's conflict leftovers.</summary>
    private static readonly string[] IgnoredSuffixes = [".swp", ".orig", ".rej"];

    /// <summary>The plan this watch covers. REPLACED wholesale by <see cref="Rebase"/> after a mid-run
    /// splice (#568) — not merged into, because <see cref="Poll"/> and <see cref="Rebaseline"/> both iterate
    /// <c>_plan.Tasks</c> and a JIT wave's tasks are not in the loaded plan at all.</summary>
    private PlanDefinition _plan;

    /// <summary>The tasks this watch covers, keyed by id — so <see cref="Rebaseline"/> can reject an unknown
    /// id without a scan, and without ever falling back to "no known id, therefore re-baseline everything".</summary>
    private Dictionary<string, TaskNode> _tasks;

    /// <summary>Last-known definition surface per task id. Replaced wholesale by <see cref="Poll"/> (which
    /// therefore also prunes tasks that left the plan) and per id by <see cref="Rebaseline"/>.</summary>
    private Dictionary<string, TaskSnapshot> _baseline;

    public LivePlanEditWatch(PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        _plan = plan;
        _tasks = Index(plan);

        // Baseline at construction, not at the first poll: an edit made between load and the first scheduler
        // boundary is still an edit made during the run, and the first Poll() should report it.
        _baseline = SnapshotAll(null);
    }

    /// <summary>
    /// Replace the plan this watch covers after a mid-run splice (issue #568, design 37 §6) — a JIT wave the
    /// run LOADED as an empty stub, whose tasks the harness authored at the barrier.
    ///
    /// <para><b>The plan is REPLACED, not re-baselined.</b> Re-baselining cannot work: <see cref="Poll"/>
    /// iterates <c>_plan.Tasks</c> and so does <see cref="Rebaseline"/>, and the spliced tasks are not in
    /// <c>_plan</c> at all — so before this the #545 advisory was structurally blind on the one plan shape
    /// where mid-run editing is NORMAL, because JIT breakdown writes the folder while the run is live.</para>
    ///
    /// <para><b>The BASELINE is deliberately left alone.</b> The next <see cref="Poll"/> then sees each
    /// newly-covered task with no baseline and adopts it silently, through the branch that already exists for
    /// exactly this case — the freshly-authored files are the HARNESS's own breakdown output, not an operator
    /// edit, and reporting them would blame the operator for the harness's writes (#229's "an advisory that
    /// fires on the harness's own writes stops being read"). That branch was unreachable in production until
    /// now; this gives it its producer rather than leaving it dead code beside a duplicate snapshot here.
    /// Tasks ALREADY covered keep their baselines, so an operator edit that landed before the splice is still
    /// the next poll's to report.</para>
    ///
    /// <para><b>Cost: a one-poll blind window.</b> Between this call and the next <see cref="Poll"/> — the
    /// very next task dispatch, milliseconds later, since the wave is about to drain — an operator edit to a
    /// newly-covered task is folded into the adoption. That window is CORRECT to be blind: the harness itself
    /// has been writing that folder for the last thirty minutes.</para>
    /// </summary>
    public void Rebase(PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        _plan = plan;
        _tasks = Index(plan);
    }

    private static Dictionary<string, TaskNode> Index(PlanDefinition plan)
    {
        var tasks = new Dictionary<string, TaskNode>(StringComparer.Ordinal);
        foreach (TaskNode task in plan.Tasks)
        {
            tasks[task.Id] = task;
        }

        return tasks;
    }

    /// <summary>Recompute the definition surface, return what changed since the last call, and
    /// re-baseline. Empty when nothing changed. Never throws: an unreadable file is skipped.</summary>
    public IReadOnlyList<PlanEdit> Poll()
    {
        var edits = new List<PlanEdit>();
        var next = new Dictionary<string, TaskSnapshot>(StringComparer.Ordinal);

        foreach (TaskNode task in _plan.Tasks)
        {
            _baseline.TryGetValue(task.Id, out TaskSnapshot? previous);
            TaskSnapshot current = Snapshot(task, previous);
            next[task.Id] = current;

            // A task with no baseline is a JIT wave's freshly-authored task, not an operator edit: adopt it
            // silently. Reporting it would blame the operator for the harness's own breakdown output.
            if (previous is null)
            {
                continue;
            }

            IReadOnlyList<PlanEditedFile> files = Diff(task.Id, previous, current);
            if (files.Count > 0)
            {
                edits.Add(new PlanEdit(task.Id, previous.Hash, current.Hash, files));
            }
        }

        // Report ONCE, then stay quiet: without re-baselining here, an edit made early in a long run would be
        // re-reported at every subsequent scheduler boundary.
        _baseline = next;
        return edits;
    }

    /// <summary>Silently re-baseline these tasks - a HARNESS-authored edit is not an operator edit.
    /// An unknown task id is a no-op. Pass no ids to re-baseline the whole plan.</summary>
    public void Rebaseline(params string[] taskIds)
    {
        // No ids is the PLAN-WIDE form §5.3 needs after each of the five harness writers, three of which have
        // authority over files outside the unit they nominally act on.
        if (taskIds is null || taskIds.Length == 0)
        {
            _baseline = SnapshotAll(_baseline);
            return;
        }

        foreach (string taskId in taskIds)
        {
            // An unknown id is a no-op in BOTH directions: it must not throw, and it must not be read as "no
            // known id, therefore re-baseline everything" — a pending edit to a real task is still the next
            // poll's to report.
            if (taskId is null || !_tasks.TryGetValue(taskId, out TaskNode? task))
            {
                continue;
            }

            _baseline.TryGetValue(taskId, out TaskSnapshot? previous);
            _baseline[taskId] = Snapshot(task, previous);
        }
    }

    /// <summary>Snapshot every task in the plan. <paramref name="previous"/> is the surface each task's
    /// carry-forward of an unreadable file reads from — null only from the constructor, which has no
    /// previous surface to carry anything forward from.</summary>
    private Dictionary<string, TaskSnapshot> SnapshotAll(IReadOnlyDictionary<string, TaskSnapshot>? previous)
    {
        var snapshots = new Dictionary<string, TaskSnapshot>(StringComparer.Ordinal);
        foreach (TaskNode task in _plan.Tasks)
        {
            TaskSnapshot? prior =
                previous is not null && previous.TryGetValue(task.Id, out TaskSnapshot? found) ? found : null;
            snapshots[task.Id] = Snapshot(task, prior);
        }

        return snapshots;
    }

    /// <summary>
    /// The current definition surface of one task: the ignore-list-filtered
    /// <see cref="TaskDefinitionFiles.Enumerate"/> labels, each with its own content hash, in enumeration
    /// order. Never throws — a file the process cannot read right now (an editor's share-lock, an indexer,
    /// antivirus) is UNKNOWN rather than changed, so its last-known hash is carried forward and a transient
    /// lock is never reported as an operator edit.
    /// </summary>
    private static TaskSnapshot Snapshot(TaskNode task, TaskSnapshot? previous)
    {
        var files = new List<KeyValuePair<string, string>>();
        try
        {
            foreach ((string Label, string AbsolutePath) file in TaskDefinitionFiles.Enumerate(task))
            {
                if (IsEditorArtifact(file.AbsolutePath))
                {
                    continue;
                }

                // Absent is Removed; unreadable is not. Distinguish them BEFORE reading, so a deleted
                // guardrail is reported and a locked one is not.
                if (!File.Exists(file.AbsolutePath))
                {
                    continue;
                }

                string? hash = TryHashFile(file.Label, file.AbsolutePath);
                hash ??= previous?.HashOf(file.Label);
                if (hash is not null)
                {
                    files.Add(new KeyValuePair<string, string>(file.Label, hash));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The ENUMERATION failed (a locked or vanished guardrails/ folder), so everything past that point
            // is unknown rather than deleted. Fall back to the last complete surface rather than reporting the
            // rest of the task as removed.
            return previous ?? TaskSnapshot.Of(files);
        }

        return TaskSnapshot.Of(files);
    }

    /// <summary>The content hash of one definition file, framed by the same <see cref="HashText.AppendFile"/>
    /// primitive <see cref="TaskDefinitionHash"/> folds — so the watch and the hash cannot disagree about a
    /// file's bytes. Null when the file cannot be read right now.</summary>
    private static string? TryHashFile(string label, string absolutePath)
    {
        try
        {
            var builder = new StringBuilder();
            HashText.AppendFile(builder, label, absolutePath);
            return Sha256(builder.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True for the editor/OS artifacts the watch drops before comparing — and ONLY here, never in
    /// <see cref="HashText"/> (see the class remarks for why that distinction is load-bearing).
    /// <para><c>internal</c> rather than <c>private</c> (plan 32-executed-definition-hash §6.2/§15.2) so the
    /// settle-time divergence gate filters through this same predicate: one home for the list, so a future
    /// pattern cannot reach one reporting surface and miss the other.</para></summary>
    internal static bool IsEditorArtifact(string absolutePath)
    {
        string name = Path.GetFileName(absolutePath);
        return IgnoredFileNames.Any(n => string.Equals(name, n, StringComparison.OrdinalIgnoreCase))
            || IgnoredSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What moved between two surfaces of the same task, in enumeration order (added/modified first,
    /// then the labels that vanished, in their old order).</summary>
    private static IReadOnlyList<PlanEditedFile> Diff(string taskId, TaskSnapshot before, TaskSnapshot after)
    {
        var moved = new List<PlanEditedFile>();
        foreach (KeyValuePair<string, string> file in after.Files)
        {
            string? old = before.HashOf(file.Key);
            if (old is null)
            {
                moved.Add(new PlanEditedFile(taskId, file.Key, PlanEditKind.Added));
            }
            else if (!string.Equals(old, file.Value, StringComparison.Ordinal))
            {
                moved.Add(new PlanEditedFile(taskId, file.Key, PlanEditKind.Modified));
            }
        }

        foreach (KeyValuePair<string, string> file in before.Files)
        {
            if (after.HashOf(file.Key) is null)
            {
                moved.Add(new PlanEditedFile(taskId, file.Key, PlanEditKind.Removed));
            }
        }

        return moved;
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>
    /// One task's definition surface: its labeled per-file hashes in enumeration order, plus the aggregate
    /// <see cref="Hash"/> the advisory quotes either side of an edit. The aggregate folds the (label, per-file
    /// hash) pairs rather than the file bytes, so it stays derivable from the snapshot alone — including when
    /// an unreadable file's hash was carried forward from the previous surface.
    /// </summary>
    private sealed class TaskSnapshot
    {
        private readonly Dictionary<string, string> _byLabel;

        private TaskSnapshot(IReadOnlyList<KeyValuePair<string, string>> files, string hash)
        {
            Files = files;
            Hash = hash;
            _byLabel = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> file in files)
            {
                _byLabel[file.Key] = file.Value;
            }
        }

        /// <summary>(Label, content hash) in <see cref="TaskDefinitionFiles.Enumerate"/> order.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Files { get; }

        /// <summary>The <c>sha256:</c>-prefixed aggregate over <see cref="Files"/>.</summary>
        public string Hash { get; }

        public string? HashOf(string label) => _byLabel.TryGetValue(label, out string? hash) ? hash : null;

        public static TaskSnapshot Of(IReadOnlyList<KeyValuePair<string, string>> files)
        {
            var builder = new StringBuilder();
            foreach (KeyValuePair<string, string> file in files)
            {
                builder.Append(file.Key).Append(HashText.UnitSeparator)
                       .Append(file.Value).Append(HashText.RecordSeparator);
            }

            return new TaskSnapshot(files, "sha256:" + Sha256(builder.ToString()));
        }
    }
}
