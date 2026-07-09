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
    /// <param name="fileWritesRolledBack">
    /// True in worktree mode for a non-final attempt (segment reset to taskBase before the next attempt):
    /// the header must not claim preserved work (issue #167 gap — this path previously always emitted the
    /// "keep what already works" header even when the writes were discarded).
    /// </param>
    /// <param name="salvageRef">
    /// Retry salvage (issue #306): when non-null, the rolled-back attempt was stashed — appends the
    /// salvage-adoption section so the agent can recover the good parts. Null when salvage is off, not
    /// worktree mode, or the attempt produced nothing.
    /// </param>
    public static string ForActionFailure(
        TaskNode task, int attempt, ProcessResult action, bool fileWritesRolledBack = false, SalvageRef? salvageRef = null)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt, ActionKind.Prompt, fileWritesRolledBack, salvageRef);
        text.AppendLine("## What failed");
        text.AppendLine(action.TimedOut
            ? "The action timed out and was killed. Guardrails were skipped."
            : $"The action exited with code {action.ExitCode}. Guardrails were skipped.");
        AppendTail(text, "Action stderr (tail)", action.StandardError);
        AppendTail(text, "Action stdout (tail)", action.StandardOutput);
        AppendSalvageSection(text, salvageRef);
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
    public static string ForOutputCapExceeded(
        TaskNode task, int attempt, SalvageRef? salvageRef = null, bool fileWritesRolledBack = false)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt, ActionKind.Prompt, fileWritesRolledBack, salvageRef);
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
        AppendHeader(text, task, attempt, ActionKind.Prompt, fileWritesRolledBack, salvageRef);
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
    /// NOT tell the agent to continue from it; it discloses the reset. False in serial mode (file
    /// writes persist) and on the final attempt (never reset), where the existing "continue from
    /// preserved partial work" guidance is kept.
    /// </param>
    /// <param name="salvageRef">
    /// Retry salvage (issue #306): when non-null, the rolled-back attempt was stashed to a git ref +
    /// applyable patch before the reset — the "your work is gone, re-author" instruction is then softened
    /// to point at the salvage section, and that section is appended. Null when salvage is off, not
    /// worktree mode, or the attempt produced nothing. (Issue #306 extends salvage to timeout, which
    /// #195 had deliberately left out.)
    /// </param>
    public static string ForTimeout(
        TaskNode task, int attempt, bool fileWritesRolledBack = false, SalvageRef? salvageRef = null)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt, ActionKind.Prompt, fileWritesRolledBack, salvageRef);
        text.AppendLine("## The previous attempt timed out");
        text.AppendLine();
        // #167: only the "PARTIAL WORK is preserved" claim is mode-dependent. In worktree mode the
        // attempt's writes are reverted (rollback note below), so that claim is false — drop it; the
        // "don't re-explore, the clock matters" advice is valid in BOTH modes and is kept. Serial mode
        // is byte-for-byte unchanged. #306: when a salvage ref exists, the reverted work is recoverable,
        // so the "re-author the files" instruction points at the salvage section instead of a flat redo.
        if (fileWritesRolledBack && salvageRef is not null)
        {
            text.AppendLine("The previous attempt ran out of time and was stopped. Its partial work was reverted from");
            text.AppendLine("your working tree, but it was NOT discarded — see '## Prior attempt work is salvageable'");
            text.AppendLine("below to recover it. Do NOT waste the extended clock re-reading the whole codebase to");
            text.AppendLine("re-orient: recover what's good and go straight to the deliverable.");
            text.AppendLine();
        }
        else if (fileWritesRolledBack)
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
        AppendSalvageSection(text, salvageRef);
        return text.ToString();
    }

    /// <summary>
    /// Compose feedback for an attempt where one or more GUARDRAILS failed.
    /// </summary>
    /// <param name="fileWritesRolledBack">
    /// True in worktree mode for a non-final attempt (the segment is reset to taskBase + cleaned before
    /// the next attempt): the attempt's file writes are reverted, so the header must NOT claim the work
    /// is preserved on disk (issue #167 gap — this path previously ALWAYS emitted "keep what already
    /// works" regardless of the reset). False in serial mode and on the final attempt.
    /// </param>
    /// <param name="salvageRef">
    /// Retry salvage (issue #306): when non-null, the attempt's rolled-back working tree was stashed to a
    /// git ref + applyable patch before the reset — appends the salvage-adoption section and makes the
    /// header's "keep what already works" intent TRUE (recover, don't re-author). Null when salvage is
    /// off, not worktree mode, the attempt was a no-op, or the preserve failed.
    /// </param>
    public static string ForGuardrailFailures(
        TaskNode task,
        int attempt,
        IReadOnlyList<GuardrailResult> results,
        bool fileWritesRolledBack = false,
        SalvageRef? salvageRef = null)
    {
        var text = new StringBuilder();

        // A tests-untouched (protected-artifact) failure means the agent gamed a check by editing a
        // protected upstream file. TaskExecutor already SUPPRESSES the salvage stash AT CREATION for this
        // case (issue #306 review WEAK-1) — so salvageRef arrives null and nothing is offered — but we
        // re-derive the signal here as belt-and-suspenders (a caller passing a stale ref must still not
        // have it advertised) and to gate the dedicated block below. NOTE: the actual guarantee that a
        // gamed edit can never reach green is the DETERMINISTIC per-attempt re-check (write-scope + this
        // task's guardrails, re-run on every attempt's FINAL state); this suppression is defense-in-depth
        // (don't hand back / advertise the gamed patch), not the load-bearing safety property.
        bool testsUntouchedFailed = results.Any(r => !r.Passed && GuardrailArchetypes.IsProtectedArtifactCheck(r.Name));
        SalvageRef? effectiveSalvage = testsUntouchedFailed ? null : salvageRef;

        // WEAK-3: the header is mode-aware even on the tests-untouched path — in worktree mode the F2
        // reset discarded the WHOLE tree (not just the restored test file), so with no salvage offered
        // this yields the honest "rolled back, re-author" header rather than a false "keep what works".
        AppendHeader(text, task, attempt, task.Action.Kind, fileWritesRolledBack, effectiveSalvage);
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
        // Returning here suppresses the WHOLE verdict ledger + salvage footer — not just a tests-pass
        // entry. That is deliberate: a tests-pass guardrail that went green by editing the tests is
        // exactly what must NOT be preserved (nor its work re-offered for salvage), and after restore the
        // passing set is recomputed next attempt anyway, so listing "do not break these" here would
        // mislead. (testsUntouchedFailed was computed at the top to gate the header + salvage disposition.)
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

        // Per-guardrail verdicts (issue #306): a compact ✅/❌ ledger of EVERY guardrail this attempt ran,
        // so the agent sees exactly how much already passed and can make a TARGETED fix rather than
        // re-deriving. Ordered as the guardrails ran (cheapest-first, ordinal by filename). The ✅ set is
        // the "do not break these" constraint; each ❌ carries its one-line reason (full output is in the
        // "## Failed guardrails" detail above). Skipped on the tests-untouched path (handled above).
        AppendVerdictLedger(text, results);
        AppendSalvageSection(text, effectiveSalvage);

        return text.ToString();
    }

    /// <summary>
    /// Append the "## Prior attempt: guardrail verdicts" ledger (issue #306) — every guardrail marked
    /// ✅ (passed, do not break) or ❌ (failed, with its one-line reason). This is the "how much is
    /// already good to start from" signal that turns a one-token miss into a one-token fix instead of a
    /// full re-author. No-op when there are no results.
    /// </summary>
    private static void AppendVerdictLedger(StringBuilder text, IReadOnlyList<GuardrailResult> results)
    {
        if (results.Count == 0)
        {
            return;
        }

        text.AppendLine("## Prior attempt: guardrail verdicts");
        text.AppendLine();
        foreach (GuardrailResult r in results)
        {
            if (r.Passed)
            {
                text.AppendLine($"- ✅ {r.Name}");
            }
            else
            {
                string reason = string.IsNullOrWhiteSpace(r.Reason) ? "guardrail failed (no reason printed)" : r.Reason!;
                text.AppendLine($"- ❌ {r.Name} — {reason}");
            }
        }

        text.AppendLine();
        text.AppendLine("The ✅ guardrails already pass — do not break them. Fix only the ❌ ones (full detail above).");
    }

    /// <summary>
    /// Compose feedback for an attempt rejected because its state fragment was invalid (SSOT §6.2).
    /// When <paramref name="fileWritesRolledBack"/> is true (worktree mode: the segment is reset to
    /// taskBase before the next attempt — issue #162), the feedback also discloses that the attempt's
    /// FILE writes were reverted, so the agent re-authors them rather than assuming they survived.
    /// </summary>
    public static string ForInvalidFragment(TaskNode task, int attempt, string reason, bool fileWritesRolledBack = false)
    {
        var text = new StringBuilder();
        // #167/#306: route the rollback flag into the header so it never says "keep what already works"
        // while the body (AppendRollbackDisclosure) instructs re-authoring. Fragment rejections keep the
        // #162 re-author disclosure and are NOT salvaged (documented scope boundary), so no salvage ref.
        AppendHeader(text, task, attempt, ActionKind.Prompt, fileWritesRolledBack);
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
        // #167/#306: route the rollback flag into the header (see ForInvalidFragment). Fragment
        // rejections are NOT salvaged (documented scope boundary) — no salvage ref here.
        AppendHeader(text, task, attempt, ActionKind.Prompt, fileWritesRolledBack);
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
    /// Append the retry-salvage adoption section (issues #195 / #306): the prior attempt's rolled-back
    /// working tree was stashed before the F2 reset discarded it, and this exposes it as a FIRST-CLASS,
    /// agent-controlled retry input — the agent decides whether to pull ALL of it (<c>git apply</c> the
    /// patch), SOME of it (<c>git checkout &lt;ref&gt; -- &lt;path&gt;</c> per file), or NONE (re-author). The
    /// framing is deliberately outcome-NEUTRAL: #306 offers this on EVERY non-final worktree failure
    /// (guardrail-fail, action-fail, timeout, max-turns, output-cap, write-scope), not only the #195
    /// non-logic budget-exhaustion outcomes, so it must not claim the attempt "was making real progress"
    /// — the per-guardrail verdicts (where shown, on the guardrail-fail path) tell the agent what
    /// already passed. No-op when <paramref name="salvageRef"/> is null (salvage off, serial mode, an
    /// empty/no-op diff, or the preserve itself failed).
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
        text.AppendLine($"Attempt {salvageRef.Attempt}'s FULL working tree — before the reset above — was stashed to the");
        text.AppendLine($"git ref `{salvageRef.RefName}` so you can BUILD ON IT instead of re-deriving everything. The");
        text.AppendLine("clean base is still the default starting point; recovering the prior work is YOUR call — pull");
        text.AppendLine("all of it, some of it, or none:");
        text.AppendLine();
        if (salvageRef.PatchPath is { Length: > 0 } patchPath)
        {
            // Forward slashes so the command works verbatim on every OS: git accepts `C:/…` on Windows,
            // and it avoids a bash backslash-escape hazard in the emitted command.
            string applyPath = patchPath.Replace('\\', '/');
            text.AppendLine($"- Pull in EVERYTHING, then edit: `git apply \"{applyPath}\"` re-applies the whole prior");
            text.AppendLine("  attempt on top of the clean base. (Or open that patch file to read exactly what changed.)");
        }

        text.AppendLine($"- Pull in ONE file that is correct as-is: `git checkout \"{salvageRef.RefName}\" -- <path>`.");
        text.AppendLine($"- Inspect before adopting: `git show --stat \"{salvageRef.RefName}\"` or `git diff <taskBase> \"{salvageRef.RefName}\"`.");
        text.AppendLine("- Re-author, from scratch, only what is INCOMPLETE or wrong — judge each file; do not blindly");
        text.AppendLine("  restore everything.");

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
    /// <remarks>
    /// Issue #253: each path is labelled with its raw git change-status (A/M/D) so a human debugging
    /// a later <c>needs-human</c> can immediately tell a brand-new untracked file with no history at
    /// this task's base commit — suspicious/unattributable, since <c>git add -A</c> sweeps up ANY
    /// untracked file present in the worktree, not just ones the agent's own tool calls wrote — apart
    /// from a modification/deletion of a file that genuinely existed before this attempt (far more
    /// likely a real agent mistake). A new file also carries a <see cref="WriteScopeOffensePreview"/>
    /// (size + content preview) captured before the revert deleted it, since by the time anyone reads
    /// this feedback the file itself is already gone from the worktree.
    /// </remarks>
    /// <param name="fileWritesRolledBack">
    /// True in worktree mode for a non-final attempt: the F2 reset reverts the WHOLE attempt (including
    /// the in-scope changes) before the next one, so the feedback must NOT claim "your in-scope changes
    /// are preserved" (a #167-class false claim this corrects). False in serial mode / the final attempt,
    /// where only the out-of-scope paths were reverted and the in-scope work genuinely persists.
    /// </param>
    /// <param name="salvageRef">
    /// Retry salvage (issue #306): when non-null, the attempt (after the out-of-scope scoped-revert) was
    /// stashed to a git ref + applyable patch before the reset — so the in-scope work IS recoverable.
    /// Appends the salvage-adoption section. Null in serial mode or when salvage is off.
    /// </param>
    public static string ForWriteScopeViolation(
        TaskNode task, int attempt, IReadOnlyList<WriteScopeOffense> offendingPaths,
        bool fileWritesRolledBack = false, SalvageRef? salvageRef = null)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt, task.Action.Kind, fileWritesRolledBack, salvageRef);
        text.AppendLine("## Write-scope violation");
        text.AppendLine();
        text.AppendLine("The following path(s) were modified but fall OUTSIDE this task's declared writeScope:");
        foreach (WriteScopeOffense offense in offendingPaths)
        {
            text.AppendLine($"- `{offense.Path}` ({DescribeStatus(offense.Status)})");
            if (offense.Preview is { } preview)
            {
                text.AppendLine($"  - {preview.SizeBytes} byte(s) before revert:");
                text.AppendLine("    ```");
                foreach (string previewLine in preview.TextPreview.Replace("\r\n", "\n").Split('\n'))
                {
                    text.AppendLine($"    {previewLine}");
                }
                text.AppendLine("    ```");
            }
        }

        text.AppendLine();
        if (fileWritesRolledBack)
        {
            // Worktree mode: the out-of-scope paths were scoped-reverted, then the WHOLE attempt is reset
            // to taskBase before the next one — so the in-scope work is NOT on disk either. It is stashed
            // (see the salvage section) when salvage is on. Do not claim it "is preserved".
            text.AppendLine("The out-of-scope path(s) above were reverted, and the whole attempt is then reset to a");
            text.AppendLine("clean base before your retry. On retry, ensure you only write to paths covered by this");
            text.AppendLine("task's writeScope (SSOT §3.4, plan 08 §2).");
        }
        else
        {
            text.AppendLine("The harness has already reverted those files to their pre-attempt state. Your");
            text.AppendLine("in-scope changes are preserved. On retry, ensure you only write to paths covered");
            text.AppendLine("by this task's writeScope (SSOT §3.4, plan 08 §2).");
        }

        AppendSalvageSection(text, salvageRef);
        return text.ToString();
    }

    /// <summary>Human-readable label for a <see cref="WriteScopeOffense.Status"/> letter.</summary>
    private static string DescribeStatus(char status) => status switch
    {
        'A' => "A: new/untracked — no history at this task's base commit",
        'M' => "M: modified a file that existed at this task's base commit",
        'D' => "D: deleted a file that existed at this task's base commit",
        _ => $"{status}: unrecognized git change status"
    };

    /// <summary>
    /// Compose feedback for a <c>needsHarnessWrite</c> request REJECTED by prospective validation
    /// (issue #191, SSOT §9) — the requested path escaped the task's effective workspace, or fell
    /// outside its declared <c>writeScope</c>. Names the offending path so the agent either requests a
    /// path actually within scope, or asks a human if the deliverable genuinely needs a broader scope.
    /// Mirrors <see cref="ForWriteScopeViolation"/>'s phrasing/actionability for the prospective case.
    /// </summary>
    public static string ForHarnessWriteOutOfScope(
        TaskNode task, int attempt, string requestedPath, string reason,
        bool fileWritesRolledBack = false, SalvageRef? salvageRef = null)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt, ActionKind.Prompt, fileWritesRolledBack, salvageRef);
        text.AppendLine("## needsHarnessWrite rejected");
        text.AppendLine();
        text.AppendLine($"Your `needsHarnessWrite` request for `{requestedPath}` was REJECTED before any write happened:");
        text.AppendLine();
        text.AppendLine($"> {reason}");
        text.AppendLine();
        text.AppendLine("Request a path that is genuinely within this task's declared `writeScope` (SSOT §3.4), or,");
        text.AppendLine("if the deliverable truly needs a broader scope, write `{\"needsHuman\": \"<why>\"}` to");
        text.AppendLine("GUARDRAILS_STATE_OUT instead so a human can widen the scope.");
        AppendSalvageSection(text, salvageRef);
        return text.ToString();
    }

    /// <summary>
    /// Compose feedback for a <c>needsHarnessWrite</c> request DENIED by the permission-file carve-out
    /// (issue #321, SSOT §9) — the requested path is a permission-granting <c>.claude/settings.json</c>
    /// / <c>.claude/settings.local.json</c>, which the harness will never write on an agent's behalf.
    /// Retrying the same path cannot clear it (it is a policy, not a transient failure), so the feedback
    /// routes the agent to the only real remedy: ask a human, or drop the settings write entirely.
    /// </summary>
    public static string ForHarnessWriteDenied(
        TaskNode task, int attempt, string requestedPath, string reason,
        bool fileWritesRolledBack = false, SalvageRef? salvageRef = null)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt, ActionKind.Prompt, fileWritesRolledBack, salvageRef);
        text.AppendLine("## needsHarnessWrite denied");
        text.AppendLine();
        text.AppendLine($"Your `needsHarnessWrite` request for `{requestedPath}` was DENIED before any write happened:");
        text.AppendLine();
        text.AppendLine($"> {reason}");
        text.AppendLine();
        text.AppendLine("`.claude/settings.json` and `.claude/settings.local.json` grant tool permissions, so the");
        text.AppendLine("harness will never write them for you — a task must not be able to widen its own permission");
        text.AppendLine("surface. Retrying the SAME path will keep being denied. Every OTHER `.claude/` deliverable");
        text.AppendLine("(commands, skills, hooks, agents) IS writable via needsHarnessWrite — only the");
        text.AppendLine("permission-granting settings files are denied. If this task genuinely needs new permissions,");
        text.AppendLine("write `{\"needsHuman\": \"<why>\"}` to GUARDRAILS_STATE_OUT so a human can author the settings file.");
        AppendSalvageSection(text, salvageRef);
        return text.ToString();
    }

    /// <summary>
    /// Compose feedback for a <c>needsHarnessWrite</c> request that PASSED validation but whose actual
    /// write failed (disk full, a genuinely unwritable location even for the harness process, etc.) —
    /// treated as an action failure: guardrails are skipped and the retry gets an actionable reason.
    /// </summary>
    public static string ForHarnessWriteFailed(
        TaskNode task, int attempt, string requestedPath, string reason,
        bool fileWritesRolledBack = false, SalvageRef? salvageRef = null)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt, ActionKind.Prompt, fileWritesRolledBack, salvageRef);
        text.AppendLine("## needsHarnessWrite failed");
        text.AppendLine();
        text.AppendLine($"The harness attempted to write `{requestedPath}` on your behalf but the write itself failed:");
        text.AppendLine();
        text.AppendLine($"> {reason}");
        text.AppendLine();
        text.AppendLine("This is not a scope problem — the path was in bounds. Retry, or write");
        text.AppendLine("`{\"needsHuman\": \"<why>\"}` to GUARDRAILS_STATE_OUT if the write is likely to keep failing.");
        AppendSalvageSection(text, salvageRef);
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
    public static string ForStagingFailure(
        TaskNode task, int attempt, string reason,
        bool fileWritesRolledBack = false, SalvageRef? salvageRef = null)
    {
        var text = new StringBuilder();
        AppendHeader(text, task, attempt, ActionKind.Prompt, fileWritesRolledBack, salvageRef);
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
        AppendSalvageSection(text, salvageRef);
        return text.ToString();
    }

    /// <summary>
    /// Compose the task-level <c>feedback.md</c> for a PERMISSION WALL early halt (issues #86 / #104):
    /// the runtime refused a write/edit because the path is not granted, and retrying cannot clear it.
    /// This is NOT a retry-input (the task is settling <c>needs-human</c>) — it is the human's
    /// remediation note, so it names the exact blocked path(s) and the concrete fixes. A
    /// <c>.claude/</c> wall (<paramref name="structuralPaths"/>) is called out as a known structural
    /// restriction whose PRIMARY remedy is the <c>needsHarnessWrite</c> escape hatch (#191); the old
    /// <c>.claude/settings.json</c> <c>Write(.claude/**)</c> grant is called out as RETIRED (#273 — it
    /// no longer works), with <c>stagingOutputs</c> and a session-wide <c>bypassPermissions</c> as
    /// alternatives. Any other repeated path (<paramref name="repeatedPaths"/>) is named as an
    /// un-retryable wall (a settings grant still works for non-<c>.claude/</c> paths).
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
            text.AppendLine("`permissionMode` is `acceptEdits`. No amount of retrying — and no per-path settings");
            text.AppendLine("grant — will let a prompt action write there directly. To make this task completable");
            text.AppendLine("autonomously, do ONE of:");
            text.AppendLine();
            text.AppendLine("1. (Primary) Hand the write to the harness via `needsHarnessWrite` (issue #191). The");
            text.AppendLine("   action prompt should NOT write the `.claude/` file directly — instead write");
            text.AppendLine("   `{\"needsHarnessWrite\": {\"path\": \"<workspace-relative path>\", \"content\": \"<full");
            text.AppendLine("   file content>\", \"reason\": \"<why>\"}}` to the state-out path. The .NET harness");
            text.AppendLine("   process — not subject to the tool-permission layer — performs the write, then your");
            text.AppendLine("   guardrails still run against the result. `/plan-breakdown` now injects this");
            text.AppendLine("   instruction into any task whose deliverable is under `.claude/`; re-author this");
            text.AppendLine("   task's action prompt to use it and re-run.");
            text.AppendLine("2. Re-target the task to write its deliverable to a staging path OUTSIDE `.claude/`,");
            text.AppendLine("   then move it into place with a follow-up script step the harness runs with full");
            text.AppendLine("   permissions (the `stagingOutputs` contract, SSOT §3.5).");
            text.AppendLine("3. As a last resort, re-run the whole session with `--permission-mode bypassPermissions`.");
            text.AppendLine("   This disables ALL permission enforcement for the run (a session-wide bypass, not a");
            text.AppendLine("   scoped grant) — use it only if you accept that.");
            text.AppendLine();
            text.AppendLine("The old remedy — committing a `.claude/settings.json` with a `Write(.claude/**)` /");
            text.AppendLine("`Edit(.claude/**)` grant — NO LONGER WORKS against current Claude Code: the `.claude/`");
            text.AppendLine("block is unconditional regardless of the allow-list (issue #273). Do not rely on it.");
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
    /// #329: feedback for the OUTCOME-AWARE structural <c>.claude/</c>-wall halt (needs-human). #326
    /// settles a NON-converged attempt that carries a structural <c>.claude/</c> wall to
    /// <c>needs-human</c> on ONE attempt (the #104 fast-halt). When the non-convergence has a
    /// genuinely-observed, more-specific cause — a guardrail that actually ran and FAILED — that cause
    /// LEADS the feedback (<paramref name="primaryHeading"/> / <paramref name="primaryBody"/>) and the
    /// <c>.claude/</c> wall the agent hit is disclosed as SECONDARY context. The wall is real (a
    /// <c>.claude/</c> write/reference WAS refused this attempt) but is NOT necessarily the cause: the
    /// Claude Code Bash classifier phrases even a <c>.claude/</c> READ source (a <c>cp</c>/<c>cat</c>) as a
    /// write, and the agent may have recovered from it — so surfacing the wall as the PRIMARY outcome (as
    /// #326 did, with an empty <c>failedGuardrails[]</c>) hid the real failure and misdirected triage into
    /// chasing a permission/config issue that did not exist (issue #329).
    /// </summary>
    public static string ForStructuralWallHalt(
        TaskNode task,
        string primaryHeading,
        string primaryBody,
        IReadOnlyList<string> structuralPaths)
    {
        var text = new StringBuilder();
        text.AppendLine($"# Task '{task.Id}' needs a human");
        text.AppendLine();
        text.AppendLine($"Task: {task.Description}");
        text.AppendLine();
        text.AppendLine($"## {primaryHeading}");
        text.AppendLine();
        text.AppendLine(primaryBody.TrimEnd());
        text.AppendLine();
        text.AppendLine("The harness settled this task `needs-human` on this attempt: a structural `.claude/`");
        text.AppendLine("wall was also present (below), which no retry can clear, so the remaining retry budget");
        text.AppendLine("was not burned. Fix the primary cause above, then re-run.");
        text.AppendLine();
        text.AppendLine("## Secondary context — a `.claude/` write was blocked this attempt");
        text.AppendLine();
        foreach (string path in structuralPaths)
        {
            text.AppendLine($"- `{path}`");
        }

        text.AppendLine();
        text.AppendLine("The Claude Code runtime refused an automated write/reference to the `.claude/` path(s)");
        text.AppendLine("above — it classifies ANY `.claude/` reference (even a Bash `cp`/`cat` READ source) as a");
        text.AppendLine("write. This is recorded as CONTEXT, not the primary cause: the agent may have recovered");
        text.AppendLine("from it (e.g. re-read the file with the Read tool). If the primary failure above is a");
        text.AppendLine("MISSING or under-populated `.claude/` deliverable, this wall is the likely reason — hand");
        text.AppendLine("the write to the harness via `needsHarnessWrite` (issue #191) or the `stagingOutputs`");
        text.AppendLine("contract (SSOT §3.5). Otherwise the wall was an incidental detour; fix the primary cause.");
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

    /// <summary>
    /// The shared feedback header. <paramref name="actionKind"/> selects the retry-guidance line
    /// (issue #264): a <c>script</c> action gets a deterministic-action message (there is no agent to
    /// read this; re-running unchanged bytes fails the same guardrail every time, so the script or its
    /// guardrail must be EDITED to converge). A PROMPT action gets one of THREE agent-oriented lines,
    /// chosen by what actually happened to its on-disk work (issues #167 / #306) — so the header can
    /// never claim preserved work the harness did not provide:
    /// <list type="bullet">
    ///   <item><b>Persisted</b> (serial mode / the final attempt — no reset): file writes are still on
    ///     disk, so the classic "keep what already works, address only what failed" wording is
    ///     ACCURATE and unchanged.</item>
    ///   <item><b>Rolled back but stashed</b> (worktree non-final WITH a salvage ref, #306): the segment
    ///     was reset to a clean base, but the prior work was SAVED — point the agent at the salvage
    ///     section so "keep what already works" becomes true via recovery, not a false claim.</item>
    ///   <item><b>Rolled back and lost</b> (worktree non-final, salvage off/failed): the writes are
    ///     genuinely gone — say so honestly and instruct re-authoring (the #167 gap this closes).</item>
    /// </list>
    /// Defaults keep the Persisted prompt wording, so callers that pass no disposition are unchanged.
    /// </summary>
    private static void AppendHeader(
        StringBuilder text,
        TaskNode task,
        int attempt,
        ActionKind actionKind = ActionKind.Prompt,
        bool fileWritesRolledBack = false,
        SalvageRef? salvageRef = null)
    {
        text.AppendLine($"# Attempt {attempt} of task '{task.Id}' failed");
        text.AppendLine();
        text.AppendLine($"Task: {task.Description}");
        text.AppendLine();
        if (actionKind == ActionKind.Script)
        {
            text.AppendLine("This is a deterministic `script` action — there is no agent to self-correct");
            text.AppendLine("between attempts. Re-running the unchanged script produces byte-identical output");
            text.AppendLine("and fails the same guardrail every time; the script or its guardrail must be");
            text.AppendLine("edited to converge.");
        }
        else if (fileWritesRolledBack && salvageRef is not null)
        {
            text.AppendLine("Your previous attempt was rolled back to a clean base (so a broken partial state");
            text.AppendLine("cannot compound), but that work was SAVED, not lost. Recover the parts that already");
            text.AppendLine("work from '## Prior attempt work is salvageable' below, then make ONLY the change");
            text.AppendLine("needed to fix what failed — do NOT re-author from scratch what already worked.");
        }
        else if (fileWritesRolledBack)
        {
            text.AppendLine("Your previous attempt's file writes were rolled back to a clean base and are NOT");
            text.AppendLine("recoverable. Re-author from scratch, but carry forward what you learned and go");
            text.AppendLine("straight at what failed — do not re-explore the whole codebase to re-orient.");
        }
        else
        {
            text.AppendLine("Fix the specific problems below. Do NOT start over from scratch — keep what");
            text.AppendLine("already works and address only what failed.");
        }

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
