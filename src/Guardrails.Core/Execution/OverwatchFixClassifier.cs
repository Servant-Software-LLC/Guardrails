using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// The MECHANICAL ASYMMETRY (SSOT §9.2, doc 11 §3) — a PURE classifier the harness (never the judge)
/// applies to every proposed overwatcher fix op. It is the load-bearing guarantee that self-healing can
/// never soften a deterministic guardrail's verdict: the verdict surface is <see cref="OverwatchAuthorityClass.Denylist"/>
/// (propose-only at every tier, including <c>auto</c>), the action/budget layer is
/// <see cref="OverwatchAuthorityClass.Allowlist"/>, and everything else is
/// <see cref="OverwatchAuthorityClass.Default"/> (propose-only — a closed allowlist).
///
/// <para>The verdict surface is decided by a path/field-membership test against the SAME notion of "what
/// defines a task's verdict" the shipped <see cref="Journal.TaskDefinitionFiles"/> / <see cref="Journal.PlanDefinitionHash"/>
/// fold over — the guardrail/preflight folders (task-level, plan-level, and per-wave) and the
/// verdict-driving <c>task.json</c> fields — so the asymmetry and the #260 review-marker key can never
/// drift. A guardrail-body change is denylist precisely because applying it changes
/// <c>PlanDefinitionHash</c>, which self-invalidates <c>state/guardrails-review.json</c> (§13); a
/// <c>writeScope</c> change is denylist because narrowing HIDES a §3.4 violation and widening CHANGES the
/// checked surface.</para>
/// </summary>
public static class OverwatchFixClassifier
{
    /// <summary>Folder name of the guardrail bodies at every scope (matches the loader / the definition hashes).</summary>
    private const string GuardrailsDirName = "guardrails";

    /// <summary>Folder name of the preflight bodies at every scope (matches the loader / the definition hashes).</summary>
    private const string PreflightsDirName = "preflights";

    /// <summary>
    /// The <c>task.json</c> fields that DRIVE a deterministic verdict (doc 11 §3.1). Case-insensitive so a
    /// judge's casing variance still lands on the denylist. Any change to one of these routes to human +
    /// <c>/guardrails-review</c>, never auto.
    /// </summary>
    private static readonly HashSet<string> VerdictFields =
        new(StringComparer.OrdinalIgnoreCase) { "writeScope", "scope", "dependsOn", "integrationGate" };

    /// <summary>
    /// The runtime budget fields that are safe to override WITHOUT touching an authored file (doc 11 §3.2)
    /// — the same levers #94 already escalates. Case-insensitive.
    /// </summary>
    private static readonly HashSet<string> BudgetFields =
        new(StringComparer.OrdinalIgnoreCase) { "maxTurns", "retries", "timeoutSeconds" };

    /// <summary>
    /// Classify one proposed fix op for <paramref name="task"/> in <paramref name="plan"/>. Deterministic
    /// and side-effect-free. A null/blank descriptor for the op's kind is treated conservatively as
    /// <see cref="OverwatchAuthorityClass.Default"/> (propose-only) — never allowlist.
    /// </summary>
    public static OverwatchAuthorityClass Classify(OverwatchFixOp op, TaskNode task, PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(op);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(plan);

        return op.Kind switch
        {
            // Ephemeral appended guidance touches no authored file — always the action layer.
            OverwatchFixKind.GuidanceInjection =>
                string.IsNullOrWhiteSpace(op.Guidance) ? OverwatchAuthorityClass.Default : OverwatchAuthorityClass.Allowlist,

            // A runtime budget override is allowlist ONLY for the three sanctioned fields; any other field
            // name is unclassified → propose-only (a judge that named "writeScope" as a "budget" field must
            // not be laundered onto the allowlist).
            OverwatchFixKind.BudgetOverride =>
                op.BudgetField is { } f && BudgetFields.Contains(f) ? OverwatchAuthorityClass.Allowlist : OverwatchAuthorityClass.Default,

            // A task.json field edit: denylist for a verdict-driving field, else propose-only.
            OverwatchFixKind.TaskFieldEdit =>
                op.TaskField is { } tf && VerdictFields.Contains(tf) ? OverwatchAuthorityClass.Denylist : OverwatchAuthorityClass.Default,

            // A file edit: denylist for a guardrail/preflight body (the four folders), else propose-only.
            OverwatchFixKind.FileEdit =>
                op.TargetPath is { } p && IsVerdictSurfaceFile(p, task, plan) ? OverwatchAuthorityClass.Denylist : OverwatchAuthorityClass.Default,

            _ => OverwatchAuthorityClass.Default
        };
    }

    /// <summary>
    /// True when <paramref name="targetPath"/> resolves inside ANY of the plan's guardrail/preflight
    /// folders — task-level <c>tasks/&lt;id&gt;/guardrails|preflights</c>, plan-level
    /// <c>&lt;plan&gt;/guardrails|preflights</c>, or (waved) <c>&lt;plan&gt;/&lt;wave&gt;/guardrails|preflights</c>.
    /// These are exactly the folders <see cref="Journal.TaskDefinitionFiles"/> / <see cref="Journal.PlanDefinitionHash"/>
    /// enumerate; the containment test also catches a NOT-YET-EXISTING file a judge would create under one
    /// (still the verdict surface). The path may be absolute or relative to the workspace/plan dir.
    /// </summary>
    private static bool IsVerdictSurfaceFile(string targetPath, TaskNode task, PlanDefinition plan)
    {
        string? resolved = ResolveCandidate(targetPath, plan);
        if (resolved is null)
        {
            // Cannot resolve the path against the plan/workspace: fail SAFE by not auto-applying it, but it
            // is not a verdict-surface MATCH — the caller treats an unresolved FileEdit as Default (propose-only).
            return false;
        }

        foreach (string folder in VerdictSurfaceFolders(task, plan))
        {
            if (IsWithin(resolved, folder))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every guardrail/preflight folder in the plan whose contents are the verdict surface.</summary>
    private static IEnumerable<string> VerdictSurfaceFolders(TaskNode task, PlanDefinition plan)
    {
        // Plan-level terminal gate + full-flight checks.
        yield return Path.Combine(plan.PlanDirectory, GuardrailsDirName);
        yield return Path.Combine(plan.PlanDirectory, PreflightsDirName);

        // Per-wave plan-level folders (waved plans).
        foreach (WaveNode wave in plan.Waves)
        {
            yield return Path.Combine(wave.Directory, GuardrailsDirName);
            yield return Path.Combine(wave.Directory, PreflightsDirName);
        }

        // Every task's own guardrails/preflights folders (the whole plan, not just the current task — a
        // proposal for THIS task must never be able to edit ANOTHER task's guardrail body either).
        foreach (TaskNode t in plan.Tasks)
        {
            yield return Path.Combine(t.Directory, GuardrailsDirName);
            yield return Path.Combine(t.Directory, PreflightsDirName);
        }
    }

    /// <summary>
    /// Resolve a judge-proposed path to an absolute path for containment testing. An absolute path is
    /// used as-is; a relative path is resolved against the plan dir first, then the workspace — either
    /// anchor is acceptable because the containment set spans folders under both. Returns null when the
    /// path is empty/unusable.
    /// </summary>
    private static string? ResolveCandidate(string targetPath, PlanDefinition plan)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return null;
        }

        try
        {
            if (Path.IsPathRooted(targetPath))
            {
                return Path.GetFullPath(targetPath);
            }

            // Prefer the plan-dir anchor (guardrail folders live under the plan tree); the workspace anchor
            // is only meaningfully different for a path that escapes the plan dir, which is never a
            // verdict-surface folder anyway.
            return Path.GetFullPath(Path.Combine(plan.PlanDirectory, targetPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is the folder itself or a descendant of it (path-segment
    /// aware). The comparison is **always case-insensitive** (NIT-1): on a case-insensitive filesystem
    /// (Windows, macOS's default HFS+/APFS) <c>tasks/x/GUARDRAILS/g.ps1</c> resolves to a real guardrail
    /// body, so it MUST classify as the verdict surface; and even on a case-sensitive filesystem
    /// over-classifying a case-variant path as denylist is the SAFE direction — the asymmetry must never
    /// UNDER-classify the verdict surface (the segment-boundary prefix keeps `guardrailsHelpers/…` from
    /// matching `guardrails/`). Known v2 limitation: <see cref="Path.GetFullPath"/> does NOT resolve
    /// symlinks, so a symlink pointing INTO a guardrails/preflights folder is not caught here (v1-inert —
    /// there is no apply path for a denylist op; harden with link resolution when the v2 apply seam lands).
    /// </summary>
    private static bool IsWithin(string candidate, string folder)
    {
        string normalizedFolder = Path.GetFullPath(folder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;

        if (string.Equals(normalizedCandidate, normalizedFolder, comparison))
        {
            return true;
        }

        string prefix = normalizedFolder + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, comparison);
    }
}
