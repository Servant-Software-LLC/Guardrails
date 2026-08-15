namespace Guardrails.Cli;

/// <summary>
/// Issue #461 — which <c>guardrails skills install</c> invocation actually writes to a given directory.
/// <para>
/// The drift warning names the root each stale skill was found under, and until #461 it followed that with
/// ONE fixed remedy — <c>skills install --force</c> — regardless. That command writes to
/// <c>~/.claude/skills</c> and nowhere else, so for drift found under any other root the printed remedy
/// could not clear the warning it accompanied: run it, re-run <c>--version</c>, same warning. The failure
/// mode is worse than useless, because the natural escalation from "the documented remedy did nothing" is
/// to reach for the flag that DOES target that root.
/// </para>
/// <para>
/// Pure and root-parameterized: the user-level and project-level destinations are passed in rather than
/// resolved here, so the mapping is unit-testable without touching (or depending on) the real
/// <c>~/.claude/skills</c> or the process's current directory. Callers pass
/// <see cref="SkillsInstaller.ResolveTargetDir"/>'s own answers, which is what keeps the printed command
/// and the command's actual destination from drifting apart.
/// </para>
/// </summary>
public static class SkillsInstallRemedy
{
    /// <summary>
    /// The install command whose destination is <paramref name="root"/>: the bare form for
    /// <paramref name="userRoot"/>, <c>--project</c> for <paramref name="projectRoot"/>, and an explicit
    /// <c>--target</c> for anything else (quoted when the path contains whitespace, so it is
    /// copy-pasteable). Always includes <c>--force</c> — refreshing an existing install is the whole point,
    /// and without it the folder is silently skipped.
    /// </summary>
    public static string CommandFor(string root, string userRoot, string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (SameDirectory(root, userRoot))
        {
            return "guardrails skills install --force";
        }

        if (SameDirectory(root, projectRoot))
        {
            return "guardrails skills install --project --force";
        }

        string quoted = root.Any(char.IsWhiteSpace) ? $"\"{root}\"" : root;
        return $"guardrails skills install --target {quoted} --force";
    }

    /// <summary>
    /// Do two path strings name the same directory? Full paths compared without a trailing separator, and
    /// case-insensitively for the same reason <see cref="SkillVersionReport"/> deduplicates roots that way:
    /// Windows and default macOS filesystems are case-insensitive, and these particular roots are never
    /// intentional case-only variants of each other. A false negative would only downgrade the remedy to
    /// the (still correct) explicit <c>--target</c> form.
    /// </summary>
    private static bool SameDirectory(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}
