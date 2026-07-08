using Guardrails.Core.Hashing;
using Guardrails.Core.Model;

namespace Guardrails.Core.Journal;

/// <summary>
/// The SHARED per-task file-set enumeration (SSOT §7.3 step 2): the ordered, labeled set of files that
/// define ONE task's behavior — its <c>task.json</c>, its resolved action file, and every file under
/// its <c>guardrails/</c> and <c>preflights/</c> folders (recursive, including <c>.json</c> sidecars).
///
/// <para>This is deliberately a reusable primitive, NOT inlined into a hash: it is the single seam that
/// both <see cref="PlanDefinitionHash"/> (issue #260) and the future per-task <c>TaskDefinitionHash</c>
/// (SSOT §7.2, issue #274 Part A) fold over, so the two hashes cannot drift on "what defines a task."
/// The nesting the SSOT records — <c>PlanDefinitionHash</c>'s inputs ⊇ each <c>TaskDefinitionHash</c>'s
/// — holds precisely because both consume this exact enumeration.</para>
///
/// <para>Labels are <c>/</c>-normalized paths RELATIVE to the task folder (<c>task.json</c>,
/// <c>action:&lt;rel&gt;</c>, <c>guardrails/…</c>, <c>preflights/…</c>) so a per-task consumer can hash
/// them directly; <see cref="PlanDefinitionHash"/> additionally prefixes each with <c>task:&lt;id&gt;/</c>
/// to disambiguate across tasks.</para>
/// </summary>
internal static class TaskDefinitionFiles
{
    /// <summary>Folder name of a task's postcondition guardrails (matches the loader).</summary>
    private const string GuardrailsDirName = "guardrails";

    /// <summary>Folder name of a task's JIT dependency preflights (matches the loader).</summary>
    private const string PreflightsDirName = "preflights";

    /// <summary>
    /// Enumerate the labeled definition files for <paramref name="task"/> in fixed order:
    /// <c>task.json</c>, the resolved action file, then <c>guardrails/**</c> and <c>preflights/**</c>
    /// (each recursive, sorted by <c>/</c>-normalized relative path). Missing files/folders simply do
    /// not appear (an absent <c>task.json</c> or action is still surfaced, as an empty-body segment, by
    /// the labeled entries here — the caller frames absence).
    /// </summary>
    public static IEnumerable<(string Label, string AbsolutePath)> Enumerate(TaskNode task)
    {
        ArgumentNullException.ThrowIfNull(task);

        // 1. task.json.
        yield return ("task.json", Path.Combine(task.Directory, "task.json"));

        // 2. The resolved action file. Label carries its relative name so a rename (which can change
        //    the interpreter picked by extension) changes the hash.
        string actionRel = HashText.NormalizeRelative(task.Directory, task.Action.Path);
        yield return ($"action:{actionRel}", task.Action.Path);

        // 3. guardrails/** then 4. preflights/** — recursive, sorted, catching .json sidecars.
        foreach ((string Label, string AbsolutePath) file in
                 HashText.EnumerateFolderFiles(task.Directory, Path.Combine(task.Directory, GuardrailsDirName)))
        {
            yield return file;
        }

        foreach ((string Label, string AbsolutePath) file in
                 HashText.EnumerateFolderFiles(task.Directory, Path.Combine(task.Directory, PreflightsDirName)))
        {
            yield return file;
        }
    }
}
