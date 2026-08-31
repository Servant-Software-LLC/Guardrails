using Guardrails.Core.Io;

namespace Guardrails.Core.Prompts;

/// <summary>
/// Containment for the prompt-runner read tools (plan 28 §5) — <c>Read</c>/<c>Glob</c>/<c>Grep</c>
/// exposed to a prompt runner such as <see cref="ClaudePromptRunner"/>. <see
/// cref="Execution.WorkspaceContainment.Escapes"/> cannot serve this job: it rejects every ROOTED
/// path outright, and every path the harness hands a prompt is absolute — a read tool guarded by it
/// would refuse every read the harness instructs the model to make.
///
/// <para>The contract: normalise the candidate with <see cref="Path.GetFullPath(string)"/>, normalise
/// each root the same way, and accept on a directory-boundary match against any root (never a bare
/// string prefix — a sibling such as <c>srcevil</c> must not count as inside <c>src</c>). Roots are
/// typically <c>{ WorkingDirectory, PlanDirectory }</c> (<see cref="PromptInvocation"/>).</para>
///
/// <para><b>Empty root entries are dropped before matching</b> — <see
/// cref="Path.GetFullPath(string)"/> throws on an empty string, and the criticality assessment
/// caller supplies exactly that (both fields empty, plan §5). <b>An empty root set — after
/// dropping — denies every path</b>, deliberately: the only caller with no roots is the criticality
/// assessment, which needs no tools at all, and deny-all fails in the direction where being wrong is
/// a loud refused tool call rather than a silent read of the whole filesystem.</para>
/// </summary>
public static class PromptToolContainment
{
    /// <summary>
    /// True when <paramref name="absolutePath"/>, normalised, falls within a directory boundary of at
    /// least one entry in <paramref name="roots"/> (also normalised; empty entries dropped first).
    /// False for an empty root set, and false for every candidate when no root admits it.
    /// </summary>
    public static bool IsReadable(IReadOnlyList<string> roots, string absolutePath)
    {
        string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(absolutePath));

        foreach (string root in roots)
        {
            if (root.Length == 0)
            {
                continue;
            }

            string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

            if (string.Equals(candidate, rootFull, RealPath.Comparison) ||
                candidate.StartsWith(rootFull + Path.DirectorySeparatorChar, RealPath.Comparison))
            {
                return true;
            }
        }

        return false;
    }
}
