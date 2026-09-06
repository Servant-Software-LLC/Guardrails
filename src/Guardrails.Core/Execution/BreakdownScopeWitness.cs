using System.Security.Cryptography;

namespace Guardrails.Core.Execution;

/// <summary>
/// Issue #557 — a witness for what a JIT wave breakdown wrote OUTSIDE the wave it was asked to author.
///
/// <para>
/// <b>The mismatch this exists for.</b> The breakdown agent's authority is plan-wide:
/// <see cref="WaveBreakdownInvoker"/> runs it with <c>workingDirectory</c> and <c>planDirectory</c> both at
/// the plan folder, <c>PermissionMode = "acceptEdits"</c>, and Read/Write/Edit/Bash/Grep/Glob. It calls the
/// runner directly, so the worktree containment hook is never injected and there is no <c>writeScope</c> on
/// this path at all. The prompt <i>asks</i> it to break down one named wave; nothing enforced that.
/// </para>
///
/// <para>
/// The revert's authority is one wave. <see cref="BreakdownInventory"/> is built against a single wave
/// directory and covers <c>tasks</c> / <c>guardrails</c> / <c>preflights</c> of that wave only —
/// deliberately, because that scope is what makes its byte-identity property hold for
/// <c>PlanDefinitionHash</c>. So the set the agent could write was strictly larger than the set the harness
/// could inventory, snapshot or restore.
/// </para>
///
/// <para>
/// <b>The failure that made it worth closing.</b> A breakdown authoring wave 3 edits wave 1's
/// <c>tasks/04-…/task.json</c> — plausibly with good intent. The edit is outside the inventory, so no
/// snapshot exists; if the breakdown is then rejected, the revert restores only wave 3 and <b>the wave-1
/// edit survives a rejected breakdown</b>. If wave 1's task 04 already succeeded, #556 applies and no drift
/// fires. If it has not yet run, it runs a definition no human wrote and no review saw. This is the
/// harness's own agent, with <c>acceptEdits</c>, running unattended, per wave, with no human between
/// invocations.
/// </para>
///
/// <para>
/// <b>What this class does, and what it deliberately does not.</b> It fingerprints the plan folder around
/// the invocation and names what changed outside the wave. That is candidate (3) from the issue — "detect
/// and refuse" — chosen over the other two after weighing them:
/// </para>
/// <list type="bullet">
///   <item><b>(1) contain the agent to its wave</b> by moving <c>workingDirectory</c> down. Smallest edit,
///     but it changes what the <c>plan-breakdown</c> skill can SEE — the plan's <c>guardrails.json</c>, the
///     sibling waves it reasons about — which is a skill-contract change with blast radius well beyond this
///     defect, and it would surface as the agent failing confusingly rather than as a clear refusal.</item>
///   <item><b>(2) widen the inventory to the plan folder.</b> The issue calls it heavier, and the weight is
///     specific: the inventory's wave scope is exactly what makes its byte-identity property provable.
///     Widening it trades a proven property for a broader one that would need re-proving.</item>
/// </list>
/// <para>
/// (3) closes the part that actually harms — the SILENCE — without disturbing either. An out-of-wave write
/// stops being uninventoried-and-unnoticed and becomes a named, refused, reported halt.
/// </para>
/// </summary>
public static class BreakdownScopeWitness
{
    /// <summary>
    /// Plan-root entries a breakdown may change without it being an escape: the harness's own mutable run
    /// state and audit tree. Everything else under the plan folder is authored content, and a breakdown
    /// authoring one wave has no business writing any of it.
    /// </summary>
    private static readonly string[] HarnessOwnedRoots = ["logs", "state"];

    /// <summary>
    /// Fingerprint every authored file under <paramref name="planDirectory"/> EXCEPT the wave being
    /// authored and the harness-owned roots. Returns plan-relative paths (with <c>/</c> separators) mapped
    /// to a content hash.
    ///
    /// <para>
    /// Hashes rather than timestamps: a breakdown that rewrites a file with identical bytes has not
    /// escaped its wave in any sense that matters, and reporting it would train an operator to ignore this
    /// halt. Returns an EMPTY map — never null — when the plan folder cannot be read, so a capture failure
    /// degrades to "detected nothing" rather than to a false accusation.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> Capture(string planDirectory, string waveDirectory)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(planDirectory))
        {
            return map;
        }

        string waveFull = Path.GetFullPath(waveDirectory);

        try
        {
            foreach (string file in Directory.EnumerateFiles(planDirectory, "*", SearchOption.AllDirectories))
            {
                string full = Path.GetFullPath(file);
                if (IsUnder(full, waveFull))
                {
                    continue;   // the wave being authored — the inventory owns this, precisely
                }

                string relative = Path.GetRelativePath(planDirectory, full).Replace('\\', '/');
                if (HarnessOwnedRoots.Any(root =>
                        relative.StartsWith(root + "/", StringComparison.Ordinal)))
                {
                    continue;
                }

                if (TryHash(full) is { } hash)
                {
                    map[relative] = hash;
                }
            }
        }
        catch (IOException)
        {
            // A partial capture is worse than none: it would report every file it failed to see as a
            // deletion. Degrade to "detected nothing" and let the gate's other checks speak.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return map;
    }

    /// <summary>
    /// The plan-relative paths that were ADDED, REMOVED or MODIFIED between two captures, sorted, so a halt
    /// reads the same way twice. An empty <paramref name="before"/> is treated as "no witness was taken"
    /// and yields nothing — a capture that failed must never be reported as the agent deleting the plan.
    /// </summary>
    public static IReadOnlyList<string> Escapes(
        IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        if (before.Count == 0)
        {
            return [];
        }

        var changed = new List<string>();
        foreach ((string path, string hash) in after)
        {
            if (!before.TryGetValue(path, out string? was))
            {
                changed.Add(path + " (added)");
            }
            else if (!string.Equals(was, hash, StringComparison.Ordinal))
            {
                changed.Add(path + " (modified)");
            }
        }

        foreach (string path in before.Keys)
        {
            if (!after.ContainsKey(path))
            {
                changed.Add(path + " (deleted)");
            }
        }

        changed.Sort(StringComparer.Ordinal);
        return changed;
    }

    private static bool IsUnder(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string? TryHash(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
