namespace Guardrails.Core.Execution;

/// <summary>
/// Windows-only path-shape conversion for bash-invoked script processes (issue #263). The harness
/// builds its <c>GUARDRAILS_*</c> env vars (SSOT §5.1) from .NET absolute paths, which on Windows are
/// backslash-separated (<c>C:\Users\...</c>). Bash's OWN path handling tolerates that form fine
/// (<c>cd</c>, <c>test -f</c>, <c>[ -f ... ]</c> all work on it), but a guardrail that interpolates the
/// SAME value into an escape-sensitive context another language/tool parses — a <c>node -e</c> JS
/// string literal, a regex, <c>sed</c>, <c>awk</c>, <c>perl -e</c> — has each backslash silently
/// consumed as an escape character, corrupting the path (e.g. a segment like <c>\244afa8d</c> becomes
/// <c>¤afa8d</c>, <c>\2</c> read as an escape). The corrupted path then fails with a misleading
/// downstream error (<c>MODULE_NOT_FOUND</c>, ...) that reads as a domain bug in the guardrail rather
/// than a path-corruption bug in the harness.
/// <para>
/// The fix: forward slashes survive ALL of those escape-sensitive contexts intact, and bash plus
/// native Windows tooling (node, sed, awk, python, ...) both accept forward slashes in paths fine —
/// so a bash-invoked script sees its <c>GUARDRAILS_*</c> path values in forward-slash form
/// (<c>C:/Users/...</c>, a straight backslash swap — not the MSYS <c>/c/Users/...</c> mount form,
/// which needs no drive-letter translation to be understood by bash, node, or Windows tooling alike).
/// </para>
/// <para>
/// Scoped to keys carrying the reserved <c>GUARDRAILS_</c> prefix only — a task/guardrail's own
/// declared <c>action.env</c> entries (SSOT §5.4) are never touched, so an author's literal value is
/// never second-guessed. This type performs the conversion UNCONDITIONALLY when called; the Windows +
/// bash-interpreter gating lives in the caller (<see cref="ScriptUnitRunner"/>) so the conversion logic
/// itself stays a pure function, testable on any OS without depending on <c>OperatingSystem.IsWindows</c>.
/// </para>
/// </summary>
internal static class WindowsBashPaths
{
    private const string ReservedPrefix = "GUARDRAILS_";

    /// <summary>
    /// Returns <paramref name="env"/> unchanged unless at least one <c>GUARDRAILS_*</c>-prefixed value
    /// contains a backslash, in which case a copy with those values' backslashes replaced by forward
    /// slashes is returned. Non-<c>GUARDRAILS_</c> keys (a task/guardrail's own declared env) are never
    /// inspected or modified. Never allocates on the common no-op path.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ToForwardSlashForm(IReadOnlyDictionary<string, string> env)
    {
        Dictionary<string, string>? converted = null;
        foreach (KeyValuePair<string, string> entry in env)
        {
            if (!entry.Key.StartsWith(ReservedPrefix, StringComparison.Ordinal) || !entry.Value.Contains('\\'))
            {
                continue;
            }

            converted ??= new Dictionary<string, string>(env, StringComparer.Ordinal);
            converted[entry.Key] = entry.Value.Replace('\\', '/');
        }

        return converted ?? env;
    }
}
