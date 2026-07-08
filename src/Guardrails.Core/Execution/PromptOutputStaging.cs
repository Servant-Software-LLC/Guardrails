namespace Guardrails.Core.Execution;

/// <summary>
/// Prompt-output staging for <c>GUARDRAILS_STATE_OUT</c> / <c>GUARDRAILS_VERDICT_OUT</c> (SSOT
/// §9.5, issue #266): a PROMPT action/guardrail's own harness-internal output target is never
/// handed to the sub-agent directly — when the plan folder (and thus the documented final path
/// under <c>logs/&lt;runId&gt;/&lt;task&gt;/attempt-N/…</c>) is nested under <c>.claude/</c>, Claude
/// Code's own sensitive-path block refuses the sub-agent's Write, and no retry clears it (a SCRIPT
/// action/guardrail is unaffected — no sub-agent, no tool-permission layer, so its target is the
/// documented path directly, unchanged). Instead the sub-agent is handed a per-attempt STAGING path
/// under a plain dot-folder INSIDE the effective workspace root — never <c>.claude/</c>-nested and
/// always inside the worktree-containment hook's allowed root (mirroring
/// <see cref="StagingMover"/>'s own <c>.guardrails-staging/</c> placement) — and the harness promotes
/// the staged file to its documented final location immediately after the sub-agent process exits.
/// </summary>
/// <remarks>
/// Pure and static, mirroring <see cref="StagingMover"/>'s idiom: filesystem moves only, no git, no
/// journal. <see cref="ActionRunner"/>/<see cref="GuardrailRunner"/> own the timing (compute the
/// staging path before composing the prompt / invoking the runner; promote the instant the runner
/// returns, strictly before anything reads the final path).
/// </remarks>
public static class PromptOutputStaging
{
    /// <summary>The per-attempt staging root segment, sibling of <c>.guardrails-staging/</c> (§3.5).</summary>
    private const string StagingFolderName = ".guardrails-agent-io";

    /// <summary>
    /// Compute the per-attempt staging path for <paramref name="finalPath"/> and pre-create its
    /// staging DIRECTORY only — never the file itself, so "the agent contributed nothing" stays
    /// distinguishable from "the agent wrote an empty fragment/verdict". Returns:
    /// <c>&lt;effectiveWorkspaceRoot&gt;/.guardrails-agent-io/&lt;taskId&gt;/&lt;attemptFolder&gt;/&lt;filename of finalPath&gt;</c>.
    /// </summary>
    public static string PrepareStagingPath(
        string effectiveWorkspaceRoot, string taskId, string attemptFolder, string finalPath)
    {
        string stagingDir = Path.Combine(effectiveWorkspaceRoot, StagingFolderName, taskId, attemptFolder);
        Directory.CreateDirectory(stagingDir);
        return Path.Combine(stagingDir, Path.GetFileName(finalPath));
    }

    /// <summary>
    /// If the staged file exists, move it to <paramref name="finalPath"/> (creating its parent
    /// directory as needed; overwriting an existing final file — the same last-write-wins convention
    /// <see cref="StagingMover"/> uses for its own moves). Then, unconditionally and best-effort,
    /// delete the per-attempt staging subtree so no scaffolding lingers or is ever committed.
    /// </summary>
    public static void PromoteAndCleanup(string stagingPath, string finalPath)
    {
        try
        {
            if (File.Exists(stagingPath))
            {
                string? parent = Path.GetDirectoryName(finalPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }

                File.Move(stagingPath, finalPath);
            }
        }
        finally
        {
            // Best-effort, mirroring StagingMover's own delete-the-whole-tree idiom: a delete failure
            // must not mask a successful promote — the segment reset/clean (worktree mode) or the next
            // attempt's fresh PrepareStagingPath call (serial mode) sweeps any residue.
            TryDeleteAttemptStagingDir(stagingPath);
        }
    }

    private static void TryDeleteAttemptStagingDir(string stagingPath)
    {
        string? attemptDir = Path.GetDirectoryName(stagingPath);
        if (string.IsNullOrEmpty(attemptDir))
        {
            return;
        }

        try
        {
            if (Directory.Exists(attemptDir))
            {
                Directory.Delete(attemptDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Swallowed: the staging tree never reaching a commit is the primary git-hygiene
            // guarantee, but the move's durable effect is the promoted file, not the staging dir.
        }
    }
}
