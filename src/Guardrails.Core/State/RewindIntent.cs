using System.Text.Json;

namespace Guardrails.Core.State;

/// <summary>
/// The durable rewind-intent marker that makes a Part C safe-drift resolution CRASH-ATOMIC (issue #274
/// Part C, SSOT §7.2). The destructive plan-branch rewind (one atomic <c>git reset --hard</c> removing the
/// WHOLE safe suffix) and the per-task journal-reset (one persisted <c>run.json</c> write per task) are two
/// separate durable effects. A crash BETWEEN them could leave a NON-drifted descendant — in the safe set
/// only via the transitive-dependents closure — still journal-<c>Succeeded</c> with its own unchanged hash
/// while its integration commit was already discarded: the drift pre-pass would NOT re-flag it (its hash
/// matches) and would SKIP it, permanently losing its green work and forking dependents off a base missing
/// its contribution — the exact "silently reuse a missing base" failure this feature prevents.
///
/// <para>The marker closes that window: it is written (atomically) BEFORE the rewind and cleared only AFTER
/// both the rewind and every journal-reset persist. On resume, the Scheduler's pre-pass
/// (<see cref="Replay"/>) idempotently re-resets the whole recorded set to <c>pending</c> then clears the
/// marker — so a crash at any point self-heals to "re-run the safe set". Written by BOTH consumers
/// (the Scheduler auto-resolve AND <see cref="RunReset.ScopedReset"/>), replayed by the Scheduler.
/// It is belt-and-suspenders with the general resume reconciliation invariant (a journal-<c>Succeeded</c>
/// task whose plan-branch trailer is absent MUST re-run) — either alone recovers the descendant.</para>
/// </summary>
public sealed record RewindIntent
{
    /// <summary>The full safe set S (drifted ∪ descendants, or the named set ∪ descendants) that was reset to pending.</summary>
    public required IReadOnlyList<string> SafeSet { get; init; }

    /// <summary>
    /// The wave dir(s) rewound at wave granularity (SSOT §14.6/§14.8, #254 M2b): a WAVE-scoped rewind resets
    /// both the wave's tasks (in <see cref="SafeSet"/>) AND the wave journal records — two more durable
    /// effects a crash could split. The crash-replay (<see cref="Replay"/>) resets these wave entries to
    /// <c>pending</c> too, so a kill between the wave rewind and the wave-journal-resets self-heals and never
    /// leaves a wave entry <c>Completed</c> with a now-dangling <c>MarkerSha</c> (the sideways-reset window).
    /// Empty for a task-scoped/flat rewind (backward-compatible — an older marker omits it).
    /// </summary>
    public IReadOnlyList<string> Waves { get; init; } = [];

    /// <summary>The plan-branch tip immediately before the rewind (the CAS anchor / audit).</summary>
    public string? PreRewindTip { get; init; }

    /// <summary>The <c>git reset --hard</c> target the branch was rewound to (null for a journal-only reset).</summary>
    public string? ResetTarget { get; init; }

    /// <summary>UTC time the intent was recorded.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>The marker's path: <c>&lt;plan&gt;/state/rewind-intent.json</c> (transient; gitignored; cleared by <c>--fresh</c>).</summary>
    public static string PathFor(string planDirectory) =>
        Path.Combine(planDirectory, "state", "rewind-intent.json");

    /// <summary>Persist the intent atomically BEFORE the destructive rewind (resume reads it).</summary>
    public static void Write(string planDirectory, RewindIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        string path = PathFor(planDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(intent, Options));
    }

    /// <summary>Read the marker, or null when absent / unreadable (a corrupt marker is treated as absent — the reconciliation invariant is the safety net).</summary>
    public static RewindIntent? TryRead(string planDirectory)
    {
        string path = PathFor(planDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RewindIntent>(File.ReadAllText(path), Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Delete the marker (idempotent) — called only AFTER both the rewind and the journal-resets persist, and on <c>--fresh</c>.</summary>
    public static void Clear(string planDirectory)
    {
        string path = PathFor(planDirectory);
        if (File.Exists(path))
        {
            try { File.Delete(path); }
            catch (IOException) { /* best-effort — a leftover marker only replays an idempotent reset */ }
        }
    }
}
