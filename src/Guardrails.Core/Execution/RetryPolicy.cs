using System.Text;
using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// Composes the <c>feedback.md</c> written after a failed attempt (SSOT §8). The text is
/// the retry's input: deterministic actions just re-run, but prompt actions receive it
/// verbatim via <c>GUARDRAILS_FEEDBACK</c> / the composed prompt, so it must be specific
/// and actionable — guardrail names, their reasons, and output tails, never just "failed".
/// </summary>
public static class RetryPolicy
{
    private const int TailLines = 60;
    private const int TailChars = 4000;

    /// <summary>Compose feedback for an attempt whose ACTION failed (guardrails were skipped).</summary>
    public static string ForActionFailure(TaskNode task, int attempt, ProcessResult action)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt);
        text.AppendLine("## What failed");
        text.AppendLine(action.TimedOut
            ? "The action timed out and was killed. Guardrails were skipped."
            : $"The action exited with code {action.ExitCode}. Guardrails were skipped.");
        AppendTail(text, "Action stderr (tail)", action.StandardError);
        AppendTail(text, "Action stdout (tail)", action.StandardOutput);
        return text.ToString();
    }

    /// <summary>
    /// Compose feedback for a prompt action that exceeded the runner's OUTPUT-TOKEN cap (issue #114).
    /// The retry must CHANGE BEHAVIOR — a re-run with the identical config just re-hits the same wall —
    /// so the feedback is actionable: split the work, write the file with small incremental edits, and
    /// keep reasoning terse. Distinct from a generic action failure so a human (and §9 triage) sees a
    /// tool/budget issue, not "the agent failed".
    /// </summary>
    /// <param name="salvageRef">
    /// Retry salvage (issue #195): when non-null, the attempt's rolled-back working tree was preserved
    /// to this git ref before the F2 reset — appends the salvage-adoption section naming it. Null when
    /// salvage is off, not in worktree mode, or the preserve itself failed (best-effort).
    /// </param>
    public static string ForOutputCapExceeded(TaskNode task, int attempt, SalvageRef? salvageRef = null)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt);
        text.AppendLine("## Response truncated at the output-token cap");
        text.AppendLine();
        text.AppendLine("Your previous response exceeded the runner's output-token cap, so it was cut off and");
        text.AppendLine("NOTHING was written. Re-running the same way will hit the same wall. CHANGE your");
        text.AppendLine("approach on this attempt:");
        text.AppendLine();
        text.AppendLine("- Write each file with SMALL, INCREMENTAL edits (one tool call per file/section), not");
        text.AppendLine("  one giant response containing the whole file.");
        text.AppendLine("- Keep prose/reasoning terse — spend the output budget on the deliverable, not narration.");
        text.AppendLine("- If the task genuinely needs more than one response's worth of output, split it: produce");
        text.AppendLine("  the most important part first, then continue in subsequent turns.");
        text.AppendLine("- If the deliverable is inherently too large to produce within the cap, STOP and write");
        text.AppendLine("  {\"needsHuman\": \"<why this task is too large for the output cap>\"} to GUARDRAILS_STATE_OUT.");
        AppendSalvageSection(text, salvageRef);
        return text.ToString();
    }

    /// <summary>
    /// Compose feedback for a prompt action that exhausted its TURN budget (issue #129 / #94). A
    /// max-turns termination is NOT a logic failure — the agent was making real progress and simply
    /// ran out of turns mid-task (often reverse-engineering an unfamiliar SDK). The harness has
    /// already AUTO-ESCALATED the next attempt's turn budget (see <c>TaskExecutor</c>), so the retry
    /// must spend its turns on the deliverable, not re-exploration. Distinct from a generic action
    /// failure so a human (and §9 triage) sees a budget issue, not "the agent failed".
    /// </summary>
    /// <param name="fileWritesRolledBack">
    /// True in worktree mode for a non-final attempt (the segment is reset to taskBase + cleaned
    /// before the next attempt — issue #167): the partial work on disk is GONE, so the feedback must
    /// NOT tell the agent to continue from it; it discloses the reset and instructs re-authoring ALL
    /// files. False in serial mode (file writes persist) and on the final attempt (never reset), where
    /// the existing "continue from preserved partial work" guidance is kept.
    /// </param>
    /// <param name="salvageRef">
    /// Retry salvage (issue #195): when non-null, the attempt's rolled-back working tree was preserved
    /// to this git ref before the F2 reset — appends a section naming the ref and the diff-stat so the
    /// agent can selectively adopt the good parts instead of re-deriving everything. Null when salvage
    /// is off, not in worktree mode, or the preserve itself failed (best-effort).
    /// </param>
    public static string ForMaxTurnsExceeded(
        TaskNode task, int attempt, bool fileWritesRolledBack = false, SalvageRef? salvageRef = null)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt);
        text.AppendLine("## The previous attempt ran out of turns");
        text.AppendLine();
        text.AppendLine("The previous attempt hit the max-turns cap and was stopped mid-progress — this is a TURN");
        // #167: only the "PARTIAL WORK is preserved" claim is mode-dependent. In worktree mode the
        // attempt's writes are reverted (rollback note below), so that claim is false — drop it; the
        // raised-turn-budget advice is valid in BOTH modes and is kept. Serial mode is byte-for-byte
        // unchanged. #195: when a salvage ref exists, the "do NOT continue from on-disk files" guidance
        // is softened — the work is not lost, just relocated to a ref the agent can pull good parts from.
        if (fileWritesRolledBack)
        {
            text.AppendLine("BUDGET exhaustion, NOT a logic error. The harness has RAISED the turn budget for this");
            text.AppendLine("attempt, but do not waste the headroom:");
            text.AppendLine();
            if (salvageRef is null)
            {
                text.AppendLine("- Do NOT continue from on-disk files: the partial work was reverted (see the rollback");
                text.AppendLine("  note below). You need NOT re-explore from scratch either — carry forward what you");
                text.AppendLine("  LEARNED and go straight to producing the deliverable.");
            }
            else
            {
                text.AppendLine("- The partial work was reverted from your WORKING TREE, but it was NOT discarded — see");
                text.AppendLine("  '## Prior attempt work is salvageable' below for how to selectively recover it.");
            }
        }
        else
        {
            text.AppendLine("BUDGET exhaustion, NOT a logic error. Its PARTIAL WORK is preserved in your workspace. The");
            text.AppendLine("harness has RAISED the turn budget for this attempt, but do not waste the headroom:");
            text.AppendLine();
            text.AppendLine("- CONTINUE from the partial work already on disk; do NOT start over or re-read the whole");
            text.AppendLine("  codebase to re-orient.");
        }

        text.AppendLine("- Work DIRECTLY toward the deliverable. Batch related edits, avoid redundant exploration,");
        text.AppendLine("  and don't re-discover what a prior attempt already established.");
        text.AppendLine("- Prioritise getting the change to COMPILE and the guardrails to GO GREEN first; refine after.");
        text.AppendLine("- If this task genuinely cannot finish within a reasonable turn budget (it bundles several");
        text.AppendLine("  distinct sub-features, or needs an expensive one-time setup better done by an upstream task),");
        text.AppendLine("  STOP and write {\"needsHuman\": \"<this task is under-budgeted for turns; suggest a split or a");
        text.AppendLine("  higher maxTurns>\"} to GUARDRAILS_STATE_OUT rather than burning more attempts.");
        AppendRollbackDisclosure(text, fileWritesRolledBack);
        AppendSalvageSection(text, salvageRef);
        return text.ToString();
    }

    /// <summary>
    /// Compose feedback for a prompt/script action that TIMED OUT (issue #119). A timeout means the
    /// task needed more wall-clock, so the retry must go straight at the deliverable rather than
    /// re-explore from scratch (the wasteful "15 reads, 0 edits" retry the issue documents). The
    /// harness also extends the retry's clock (see <c>TaskExecutor</c>).
    /// </summary>
    /// <param name="fileWritesRolledBack">
    /// True in worktree mode for a non-final attempt (the segment is reset to taskBase + cleaned
    /// before the next attempt — issue #167): the partial work on disk is GONE, so the feedback must
    /// NOT tell the agent to continue from it; it discloses the reset and instructs re-authoring ALL
    /// files. False in serial mode (file writes persist) and on the final attempt (never reset), where
    /// the existing "continue from preserved partial work" guidance is kept.
    /// </param>
    public static string ForTimeout(TaskNode task, int attempt, bool fileWritesRolledBack = false)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt);
        text.AppendLine("## The previous attempt timed out");
        text.AppendLine();
        // #167: only the "PARTIAL WORK is preserved" claim is mode-dependent. In worktree mode the
        // attempt's writes are reverted (rollback note below), so that claim is false — drop it; the
        // "don't re-explore, the clock matters" advice is valid in BOTH modes and is kept. Serial mode
        // is byte-for-byte unchanged.
        if (fileWritesRolledBack)
        {
            text.AppendLine("The previous attempt ran out of time and was stopped. Its partial work was reverted (see");
            text.AppendLine("the rollback note below), so re-author the files — but do NOT waste the extended clock");
            text.AppendLine("re-reading the whole codebase to re-orient: carry forward what you LEARNED and go straight");
            text.AppendLine("to the deliverable.");
            text.AppendLine();
        }
        else
        {
            text.AppendLine("The previous attempt ran out of time and was stopped. Its PARTIAL WORK is preserved in");
            text.AppendLine("your workspace — do NOT start over and do NOT re-read the whole codebase to re-orient.");
            text.AppendLine();
            text.AppendLine("- CONTINUE from the partial work already on disk; build on it.");
        }

        text.AppendLine("- Prioritise getting the change to COMPILE and the guardrails to GO GREEN first; refine after.");
        text.AppendLine("- Make focused edits — minimise exploration, maximise progress, because the clock matters.");
        text.AppendLine("- If this task bundles several distinct sub-features and cannot finish in the time given,");
        text.AppendLine("  STOP and write {\"needsHuman\": \"<this task is under-sized for the timeout; suggest a split>\"}");
        text.AppendLine("  to GUARDRAILS_STATE_OUT rather than burning more attempts.");
        AppendRollbackDisclosure(text, fileWritesRolledBack);
        return text.ToString();
    }

    /// <summary>Compose feedback for an attempt where one or more GUARDRAILS failed.</summary>
    public static string ForGuardrailFailures(
        TaskNode task,
        int attempt,
        IReadOnlyList<GuardrailResult> results)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt);
        text.AppendLine("## Failed guardrails");

        foreach (GuardrailResult failed in results.Where(r => !r.Passed))
        {
            GuardrailDefinition? definition = task.Guardrails.FirstOrDefault(g => g.Name == failed.Name);
            text.AppendLine($"### {failed.Name}");
            if (!string.IsNullOrWhiteSpace(definition?.Description))
            {
                text.AppendLine($"Checks: {definition.Description}");
            }

            text.AppendLine($"Reason: {failed.Reason ?? "guardrail failed (no reason printed)"}");

            // The one-line reason is the FIRST line only; include the full (tail-bounded) output
            // so a multi-error failure shows every error, not just the first (issue #26 Gap 1).
            // Skipped when the output is just the reason line again (no extra signal).
            if (HasMoreThanReason(failed.Output, failed.Reason))
            {
                AppendTail(text, "Full output (tail)", failed.Output!);
            }

            text.AppendLine();
        }

        // When a tests-untouched guardrail failed, the agent edited the authored test file (almost
        // always to force a tests-pass guardrail green). The harness has restored that file to its
        // authored baseline for the next attempt (issue #51), so steer the agent to fix the
        // IMPLEMENTATION and emit the "Do NOT edit the test file(s)" block, then RETURN.
        //
        // Returning here suppresses the WHOLE "Guardrails that PASSED (do not break these)" footer —
        // not just a tests-pass entry. That is deliberate: a tests-pass guardrail that went green by
        // editing the tests is exactly what must NOT be preserved, and after restore the passing set
        // is recomputed next attempt anyway, so listing "do not break these" here would be misleading.
        bool testsUntouchedFailed = results.Any(r => !r.Passed && IsTestsUntouched(r.Name));
        if (testsUntouchedFailed)
        {
            text.AppendLine("## Do NOT edit the test file(s)");
            text.AppendLine("A `tests-untouched` guardrail failed: the authored test file was modified. The harness");
            text.AppendLine("has restored each affected test file to its authored baseline for this attempt — it is");
            text.AppendLine("pristine again. Make the ORIGINAL tests pass by fixing the implementation; do not change");
            text.AppendLine("the tests. If the authored tests are genuinely wrong or incompatible with a reasonable");
            text.AppendLine("implementation, STOP and write {\"needsHuman\": \"<why>\"} to GUARDRAILS_STATE_OUT instead");
            text.AppendLine("of editing them.");
            return text.ToString();
        }

        IReadOnlyList<string> passed = results.Where(r => r.Passed).Select(r => r.Name).ToList();
        if (passed.Count > 0)
        {
            text.AppendLine($"Guardrails that PASSED (do not break these): {string.Join(", ", passed)}");
        }

        return text.ToString();
    }

    /// <summary>A guardrail whose name marks it as a tests-untouched check (doctrine: <c>NN-tests-untouched</c>).</summary>
    private static bool IsTestsUntouched(string name) =>
        name.Contains("untouched", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Compose feedback for an attempt rejected because its state fragment was invalid (SSOT §6.2).
    /// When <paramref name="fileWritesRolledBack"/> is true (worktree mode: the segment is reset to
    /// taskBase before the next attempt — issue #162), the feedback also discloses that the attempt's
    /// FILE writes were reverted, so the agent re-authors them rather than assuming they survived.
    /// </summary>
    public static string ForInvalidFragment(TaskNode task, int attempt, string reason, bool fileWritesRolledBack = false)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt);
        text.AppendLine("## Invalid state fragment");
        text.AppendLine(reason);
        text.AppendLine();
        text.AppendLine("The file written to GUARDRAILS_STATE_OUT must be a single JSON object, e.g.");
        text.AppendLine($"`{{ \"{task.Id}\": {{ \"someKey\": \"someValue\" }} }}`.");
        AppendRollbackDisclosure(text, fileWritesRolledBack);
        return text.ToString();
    }

    /// <summary>
    /// Compose feedback for an attempt rejected because its state fragment carried a top-level key
    /// the task does not own — a foreign task id or an arbitrary shared key (SSOT §6.2,
    /// single-writer-per-key, issue #48). Names the exact offending key(s) so a confused (non-malicious)
    /// agent drops the stray key on retry and writes ONLY under its own id.
    /// </summary>
    /// <param name="fileWritesRolledBack">
    /// True in worktree mode (the segment is reset to taskBase before the next attempt — issue #162):
    /// the feedback then ALSO discloses that the attempt's FILE writes were reverted, so the agent
    /// re-authors them instead of fixing only the key and then failing a <c>file-exists</c> guardrail
    /// against files it believes still exist.
    /// </param>
    public static string ForForeignKey(TaskNode task, int attempt, IReadOnlyList<string> foreignKeys, bool fileWritesRolledBack = false)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt);
        text.AppendLine("## State fragment wrote a key this task does not own");
        text.AppendLine();
        foreach (string key in foreignKeys)
        {
            text.AppendLine($"- top-level key '{key}' is not owned by this task");
        }

        text.AppendLine();
        text.AppendLine($"A task may only write state under its OWN id, '{task.Id}'. The harness is the");
        text.AppendLine("single writer of every namespace, so writing under another task's id (or any");
        text.AppendLine("shared key) is rejected and NOTHING is merged. Remove the stray top-level key(s)");
        text.AppendLine("above and nest everything you publish under your own id, e.g.");
        text.AppendLine($"`{{ \"{task.Id}\": {{ \"someKey\": \"someValue\" }} }}`.");
        AppendRollbackDisclosure(text, fileWritesRolledBack);
        return text.ToString();
    }

    /// <summary>
    /// Append the file-write rollback disclosure when a non-final attempt's worktree is reset to
    /// <c>taskBase</c> + cleaned before the next attempt — so the agent re-authors ALL files instead
    /// of assuming its prior file writes survived. The single source of the disclosure WORDING, shared
    /// by the state-rejection path (issue #162) and the timeout / max-turns paths (issue #167) so every
    /// "your prior writes are gone" message stays consistent. No-op when no rollback occurred (serial
    /// mode, where file writes persist across attempts, or the final attempt, which is never reset).
    /// </summary>
    private static void AppendRollbackDisclosure(StringBuilder text, bool fileWritesRolledBack)
    {
        if (!fileWritesRolledBack)
        {
            return;
        }

        text.AppendLine();
        text.AppendLine("## File writes were also rolled back");
        text.AppendLine();
        text.AppendLine("Because the state fragment was rejected, all file writes from this attempt were");
        text.AppendLine("reverted. On your next attempt, re-author ALL files from scratch — do not assume");
        text.AppendLine("any file you wrote in a previous attempt is still present on disk.");
    }

    /// <summary>
    /// Append the retry-salvage adoption section (issue #195) when a prior attempt's rolled-back
    /// working tree was preserved to a git ref before the F2 reset discarded it: names the ref, its
    /// <c>git diff --stat</c> summary, and instructs the agent to selectively adopt the good parts
    /// (<c>git checkout &lt;ref&gt; -- &lt;path&gt;</c>) rather than re-deriving everything from scratch — the
    /// core "salvage, don't restart clean" behavior the issue is built on. No-op when
    /// <paramref name="salvageRef"/> is null (salvage off, serial mode, or the preserve itself failed).
    /// </summary>
    private static void AppendSalvageSection(StringBuilder text, SalvageRef? salvageRef)
    {
        if (salvageRef is null)
        {
            return;
        }

        text.AppendLine();
        text.AppendLine("## Prior attempt work is salvageable");
        text.AppendLine();
        text.AppendLine($"Attempt {salvageRef.Attempt}'s FULL working tree — before the reset above — was preserved");
        text.AppendLine($"to the git ref `{salvageRef.RefName}`. That attempt was likely making REAL progress (it ran");
        text.AppendLine("out of budget, not out of correctness), so REVIEW it and selectively adopt what's good instead");
        text.AppendLine("of re-deriving everything from scratch:");
        text.AppendLine();
        text.AppendLine($"- Inspect what changed: `git show --stat {salvageRef.RefName}` or `git diff <taskBase> {salvageRef.RefName}`.");
        text.AppendLine($"- Pull in a file that is CORRECT as-is: `git checkout {salvageRef.RefName} -- <path>`.");
        text.AppendLine("- Redo, from scratch, only what is INCOMPLETE or wrong — do not blindly restore every file;");
        text.AppendLine("  judge each one.");

        if (!string.IsNullOrWhiteSpace(salvageRef.DiffStat))
        {
            text.AppendLine();
            text.AppendLine("What that attempt changed (`git diff --stat` vs. this task's base commit):");
            text.AppendLine("```");
            text.AppendLine(salvageRef.DiffStat.TrimEnd('\r', '\n'));
            text.AppendLine("```");
        }

        text.AppendLine();
        text.AppendLine("Salvaged files remain subject to this task's declared writeScope, exactly like any other");
        text.AppendLine("write this attempt makes — the write-scope check runs on your FINAL state regardless of how");
        text.AppendLine("it got there.");
    }

    /// <summary>
    /// Compose feedback for an attempt rejected by the write-scope check (plan 08 §2/§3.4).
    /// Names each offending path so the agent removes the out-of-scope change on retry. The
    /// harness has already performed a scoped revert of the offending paths before calling this.
    /// </summary>
    public static string ForWriteScopeViolation(TaskNode task, int attempt, IReadOnlyList<string> offendingPaths)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt);
        text.AppendLine("## Write-scope violation");
        text.AppendLine();
        text.AppendLine("The following path(s) were modified but fall OUTSIDE this task's declared writeScope:");
        foreach (string path in offendingPaths)
        {
            text.AppendLine($"- `{path}`");
        }

        text.AppendLine();
        text.AppendLine("The harness has already reverted those files to their pre-attempt state. Your");
        text.AppendLine("in-scope changes are preserved. On retry, ensure you only write to paths covered");
        text.AppendLine("by this task's writeScope (SSOT §3.4, plan 08 §2).");
        return text.ToString();
    }

    /// <summary>
    /// Compose feedback for a <c>needsHarnessWrite</c> request REJECTED by prospective validation
    /// (issue #191, SSOT §9) — the requested path escaped the task's effective workspace, or fell
    /// outside its declared <c>writeScope</c>. Names the offending path so the agent either requests a
    /// path actually within scope, or asks a human if the deliverable genuinely needs a broader scope.
    /// Mirrors <see cref="ForWriteScopeViolation"/>'s phrasing/actionability for the prospective case.
    /// </summary>
    public static string ForHarnessWriteOutOfScope(TaskNode task, int attempt, string requestedPath, string reason)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt);
        text.AppendLine("## needsHarnessWrite rejected");
        text.AppendLine();
        text.AppendLine($"Your `needsHarnessWrite` request for `{requestedPath}` was REJECTED before any write happened:");
        text.AppendLine();
        text.AppendLine($"> {reason}");
        text.AppendLine();
        text.AppendLine("Request a path that is genuinely within this task's declared `writeScope` (SSOT §3.4), or,");
        text.AppendLine("if the deliverable truly needs a broader scope, write `{\"needsHuman\": \"<why>\"}` to");
        text.AppendLine("GUARDRAILS_STATE_OUT instead so a human can widen the scope.");
        return text.ToString();
    }

    /// <summary>
    /// Compose feedback for a <c>needsHarnessWrite</c> request that PASSED validation but whose actual
    /// write failed (disk full, a genuinely unwritable location even for the harness process, etc.) —
    /// treated as an action failure: guardrails are skipped and the retry gets an actionable reason.
    /// </summary>
    public static string ForHarnessWriteFailed(TaskNode task, int attempt, string requestedPath, string reason)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt);
        text.AppendLine("## needsHarnessWrite failed");
        text.AppendLine();
        text.AppendLine($"The harness attempted to write `{requestedPath}` on your behalf but the write itself failed:");
        text.AppendLine();
        text.AppendLine($"> {reason}");
        text.AppendLine();
        text.AppendLine("This is not a scope problem — the path was in bounds. Retry, or write");
        text.AppendLine("`{\"needsHuman\": \"<why>\"}` to GUARDRAILS_STATE_OUT if the write is likely to keep failing.");
        return text.ToString();
    }

    /// <summary>
    /// Compose feedback for an attempt whose STAGING MOVE failed (SSOT §3.5, issue #130): the action
    /// succeeded but the declared <c>stagingOutputs</c> deliverable was not produced under the staging
    /// dir (an empty source), or the move hit an IO error. The retry must CHANGE BEHAVIOR — write the
    /// deliverable to <c>GUARDRAILS_STAGING_DIR</c> under the declared <c>from</c> path(s) — so the
    /// feedback names the staging dir and the exact <c>from→to</c> map. An empty-source move is a
    /// deliverable-not-produced condition (guardrail-class), not a crash.
    /// </summary>
    public static string ForStagingFailure(TaskNode task, int attempt, string reason)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt);
        text.AppendLine("## Staging move failed");
        text.AppendLine();
        text.AppendLine(reason);
        text.AppendLine();
        text.AppendLine("Your action completed, but the harness could not move your `.claude/` deliverable into");
        text.AppendLine("place because it was not staged. Write your deliverable to the absolute staging directory");
        text.AppendLine("(`GUARDRAILS_STAGING_DIR`, embedded in the `## Staging outputs` section of your prompt)");
        text.AppendLine("under the declared `from` path(s) BEFORE you finish:");
        text.AppendLine();
        if (task.StagingOutputs is { } staging)
        {
            foreach (StagingOutput entry in staging)
            {
                text.AppendLine($"- `{entry.From}`  →  `{entry.To}`");
            }

            text.AppendLine();
        }

        text.AppendLine("Do NOT write under `.claude/` directly — the runtime refuses it. Stage your files under");
        text.AppendLine("the staging dir and the harness will move them into `.claude/` for you (SSOT §3.5).");
        return text.ToString();
    }

    /// <summary>
    /// Compose the task-level <c>feedback.md</c> for a PERMISSION WALL early halt (issues #86 / #104):
    /// the runtime refused a write/edit because the path is not granted, and retrying cannot clear it.
    /// This is NOT a retry-input (the task is settling <c>needs-human</c>) — it is the human's
    /// remediation note, so it names the exact blocked path(s) and the concrete fixes. A
    /// <c>.claude/</c> wall (<paramref name="structuralPaths"/>) is called out as a known structural
    /// restriction with its specific remediations; any other repeated path
    /// (<paramref name="repeatedPaths"/>) is named as an un-retryable wall.
    /// </summary>
    public static string ForPermissionWall(
        TaskNode task,
        IReadOnlyList<string> structuralPaths,
        IReadOnlyList<string> repeatedPaths)
    {
        var text = new StringBuilder();
        text.AppendLine($"# Task '{task.Id}' hit a permission wall");
        text.AppendLine();
        text.AppendLine($"Task: {task.Description}");
        text.AppendLine();
        text.AppendLine("The runtime REFUSED to write one or more paths because they are not on the granted");
        text.AppendLine("permission allow-list. Retrying cannot clear a permission wall — switching tools or");
        text.AppendLine("re-issuing the same write hits the same refusal — so the harness escalated to you");
        text.AppendLine("immediately instead of burning the remaining attempts on it.");
        text.AppendLine();

        if (structuralPaths.Count > 0)
        {
            text.AppendLine("## Blocked `.claude/` path(s) — a STRUCTURAL restriction");
            text.AppendLine();
            foreach (string path in structuralPaths)
            {
                text.AppendLine($"- `{path}`");
            }

            text.AppendLine();
            text.AppendLine("The Claude Code sub-agent runtime blocks automated writes under `.claude/` even when");
            text.AppendLine("`permissionMode` is `acceptEdits`. No amount of retrying will let the agent write there.");
            text.AppendLine("To make this task completable autonomously, do ONE of:");
            text.AppendLine();
            text.AppendLine("1. Grant the write explicitly — add a rule like `Write(.claude/**)` (and `Edit(.claude/**)`)");
            text.AppendLine("   to the project's `.claude/settings.json` allow-list before re-running.");
            text.AppendLine("2. Re-target the task to write its deliverable to a path OUTSIDE `.claude/` (e.g. a");
            text.AppendLine("   staging directory under the plan folder), then move it into `.claude/` by hand or with");
            text.AppendLine("   a follow-up script step the harness runs with full permissions.");
            text.AppendLine();
        }

        if (repeatedPaths.Count > 0)
        {
            text.AppendLine("## Repeatedly-refused path(s)");
            text.AppendLine();
            foreach (string path in repeatedPaths)
            {
                text.AppendLine($"- `{path}`");
            }

            text.AppendLine();
            text.AppendLine("The same path was refused on multiple attempts. Confirm the runner's `permissionMode`");
            text.AppendLine("and `allowedTools` (and any `.claude/settings.json` allow-list) cover this path, then");
            text.AppendLine("re-run — the harness will resume from here.");
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>
    /// True when <paramref name="output"/> carries more than the one-line <paramref name="reason"/>
    /// already shown — i.e. it is non-empty and not just the reason line repeated.
    /// </summary>
    private static bool HasMoreThanReason(string? output, string? reason)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        string trimmed = output.Trim();
        return !string.Equals(trimmed, reason?.Trim(), StringComparison.Ordinal);
    }

    private static void AppendHeader(StringBuilder text, TaskNode task, int attempt)
    {
        text.AppendLine($"# Attempt {attempt} of task '{task.Id}' failed");
        text.AppendLine();
        text.AppendLine($"Task: {task.Description}");
        text.AppendLine();
        text.AppendLine("Fix the specific problems below. Do NOT start over from scratch — keep what");
        text.AppendLine("already works and address only what failed.");
        text.AppendLine();
    }

    private static void AppendTail(StringBuilder text, string title, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        string[] lines = content.TrimEnd().Split('\n');
        IEnumerable<string> tail = lines.Length > TailLines ? lines[^TailLines..] : lines;
        string joined = string.Join('\n', tail);
        if (joined.Length > TailChars)
        {
            joined = joined[^TailChars..];
        }

        text.AppendLine($"## {title}");
        text.AppendLine("```");
        text.AppendLine(joined.TrimEnd('\r', '\n'));
        text.AppendLine("```");
    }
}
