using System.Text;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

public sealed class PromptComposerTests : IDisposable
{
    private readonly string _dir;

    public PromptComposerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gr-composer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private string WriteState(string content)
    {
        string path = Path.Combine(_dir, "state-in.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Action_SmallState_IsInlined()
    {
        string stateIn = WriteState("{ \"recipientName\": \"World\" }");
        string stateOut = Path.Combine(_dir, "out.json");

        string composed = PromptComposer.ComposeAction("Do the thing.", stateIn, stateOut, feedbackPath: null);

        Assert.Contains("## Shared state", composed);
        Assert.Contains("\"recipientName\": \"World\"", composed);
        Assert.Contains("## Output contract", composed);
        Assert.Contains(stateOut, composed);
        Assert.Contains("needsHuman", composed);
        // No retry section on attempt 1.
        Assert.DoesNotContain("## Previous attempt failed", composed);
    }

    [Fact]
    public void Action_WithStagingOutputs_EmbedsStagingDirAndFromToMap()
    {
        // SSOT §3.5: a stagingOutputs-declaring action must carry the absolute staging dir AND the
        // from→to map verbatim in the prompt body (agents read instructions, not env vars), with the
        // "do not write .claude/ directly" instruction. The section appears after ## Output contract.
        string stateIn = WriteState("{}");
        string stagingDir = Path.Combine(_dir, ".guardrails-staging", "05-certify-knowledge");
        IReadOnlyList<StagingOutput> outputs =
        [
            new StagingOutput { From = "skill/**", To = ".claude/skills/certify-knowledge/" }
        ];

        string composed = PromptComposer.ComposeAction(
            "Author the skill.", stateIn, Path.Combine(_dir, "o.json"), feedbackPath: null,
            dependencies: null, priorAttempts: null, stagingDir: stagingDir, stagingOutputs: outputs);

        Assert.Contains("## Staging outputs", composed);
        Assert.Contains(stagingDir, composed);
        Assert.Contains("skill/**", composed);
        Assert.Contains(".claude/skills/certify-knowledge/", composed);
        Assert.Contains("Do NOT attempt to write under `.claude/` directly", composed);
        // The map arrow joins from→to.
        Assert.Contains("`skill/**`  →  `.claude/skills/certify-knowledge/`", composed);
    }

    [Fact]
    public void Action_WithoutStagingOutputs_OmitsStagingSection()
    {
        // No stagingOutputs ⇒ the section is absent (the unchanged default for the vast majority
        // of tasks). Passing a stagingDir without outputs (or outputs without a dir) also omits it.
        string stateIn = WriteState("{}");

        string composed = PromptComposer.ComposeAction("body", stateIn, Path.Combine(_dir, "o.json"), feedbackPath: null);

        Assert.DoesNotContain("## Staging outputs", composed);
    }

    [Fact]
    public void Action_StateAt16KbBoundary_IsInlined_JustOver_IsByPath()
    {
        // Exactly 16 KB → inlined (≤ limit). One byte over → by path.
        string atLimit = new string('a', PromptComposer.StateInlineLimitBytes);
        Assert.Equal(PromptComposer.StateInlineLimitBytes, Encoding.UTF8.GetByteCount(atLimit));

        string stateInAt = WriteState(atLimit);
        string composedAt = PromptComposer.ComposeAction("body", stateInAt, Path.Combine(_dir, "o1.json"), null);
        Assert.Contains(atLimit, composedAt);
        Assert.DoesNotContain("Read the JSON at the absolute path", composedAt);

        string overLimit = new string('b', PromptComposer.StateInlineLimitBytes + 1);
        string stateInOver = WriteState(overLimit);
        string composedOver = PromptComposer.ComposeAction("body", stateInOver, Path.Combine(_dir, "o2.json"), null);
        Assert.DoesNotContain(overLimit, composedOver);
        Assert.Contains("Read the JSON at the absolute path", composedOver);
        Assert.Contains(stateInOver, composedOver);
    }

    [Fact]
    public void Action_WithFeedback_IncludesPreviousAttemptSectionVerbatim()
    {
        string stateIn = WriteState("{}");
        string feedbackPath = Path.Combine(_dir, "feedback.md");
        File.WriteAllText(feedbackPath, "Guardrail 01-greeting-contains failed: missing 'Hello'.");

        string composed = PromptComposer.ComposeAction("body", stateIn, Path.Combine(_dir, "o.json"), feedbackPath);

        Assert.Contains("## Previous attempt failed", composed);
        Assert.Contains("Guardrail 01-greeting-contains failed: missing 'Hello'.", composed);
        Assert.Contains("do not start over", composed);
    }

    [Fact]
    public void Action_WithDependencyContext_ListsAncestorTranscripts_BeforeOutputContract()
    {
        string stateIn = WriteState("{}");
        var deps = new List<DependencyContextRef>
        {
            new()
            {
                TaskId = "01-research-tsw",
                Description = "research the .tsw write mechanism",
                LogDir = "/plan/state/logs/01-research-tsw/attempt-1",
                TranscriptPath = "/plan/state/logs/01-research-tsw/attempt-1/transcript.md",
                FragmentPath = "/plan/state/logs/01-research-tsw/attempt-1/fragment.json"
            }
        };

        string composed = PromptComposer.ComposeAction("body", stateIn, Path.Combine(_dir, "o.json"), null, deps);

        Assert.Contains("## Context from completed dependency tasks", composed);
        Assert.Contains("01-research-tsw", composed);
        Assert.Contains("research the .tsw write mechanism", composed);
        Assert.Contains("/plan/state/logs/01-research-tsw/attempt-1/transcript.md", composed);
        Assert.Contains("/plan/state/logs/01-research-tsw/attempt-1/fragment.json", composed);
        // Dependency context must precede the output contract (read what exists before writing).
        Assert.True(
            composed.IndexOf("## Context from completed dependency tasks", StringComparison.Ordinal)
            < composed.IndexOf("## Output contract", StringComparison.Ordinal));
    }

    [Fact]
    public void Action_DependencyWithoutTranscript_FallsBackToLogDir()
    {
        string stateIn = WriteState("{}");
        var deps = new List<DependencyContextRef>
        {
            new()
            {
                TaskId = "02-restructure",
                Description = "restructure the solution",
                LogDir = "/plan/state/logs/02-restructure/attempt-1",
                TranscriptPath = null, // script ancestor — no Claude stream
                FragmentPath = null
            }
        };

        string composed = PromptComposer.ComposeAction("body", stateIn, Path.Combine(_dir, "o.json"), null, deps);

        Assert.Contains("Logs: `/plan/state/logs/02-restructure/attempt-1`", composed);
        Assert.DoesNotContain("What it did:", composed);
    }

    [Fact]
    public void Action_NoDependencies_OmitsContextSection()
    {
        string stateIn = WriteState("{}");
        string composed = PromptComposer.ComposeAction("body", stateIn, Path.Combine(_dir, "o.json"), null);

        Assert.DoesNotContain("## Context from completed dependency tasks", composed);
    }

    [Fact]
    public void Action_WithPriorAttempts_ListsAllMostRecentFirst_WithTranscriptAndFeedback()
    {
        string stateIn = WriteState("{}");
        string feedbackPath = Path.Combine(_dir, "feedback.md");
        File.WriteAllText(feedbackPath, "BG1002: BAML not found.");

        var priors = new List<PriorAttemptRef>
        {
            new()
            {
                Attempt = 2, Outcome = "guardrail-failed",
                LogDir = "/plan/state/logs/08/attempt-2",
                TranscriptPath = "/plan/state/logs/08/attempt-2/transcript.md",
                FeedbackPath = "/plan/state/logs/08/attempt-2/feedback.md"
            },
            new()
            {
                Attempt = 1, Outcome = "guardrail-failed",
                LogDir = "/plan/state/logs/08/attempt-1",
                TranscriptPath = "/plan/state/logs/08/attempt-1/transcript.md",
                FeedbackPath = "/plan/state/logs/08/attempt-1/feedback.md"
            }
        };

        string composed = PromptComposer.ComposeAction(
            "body", stateIn, Path.Combine(_dir, "o.json"), feedbackPath, dependencies: null, priorAttempts: priors);

        // Inline latest feedback is still present.
        Assert.Contains("BG1002: BAML not found.", composed);
        // Pointers to all prior attempts.
        Assert.Contains("### Prior attempt logs", composed);
        Assert.Contains("/plan/state/logs/08/attempt-2/transcript.md", composed);
        Assert.Contains("/plan/state/logs/08/attempt-1/feedback.md", composed);
        // Most recent first: attempt-2 listed before attempt-1.
        Assert.True(
            composed.IndexOf("attempt-2", StringComparison.Ordinal)
            < composed.IndexOf("attempt-1", StringComparison.Ordinal));
    }

    [Fact]
    public void Guardrail_HasVerifierContract_AndVerdictPath()
    {
        string stateIn = WriteState("{}");
        string verdictOut = Path.Combine(_dir, "verdict.json");
        string actionStdout = Path.Combine(_dir, "action-stdout.log");

        string composed = PromptComposer.ComposeGuardrail("Judge the tone.", stateIn, verdictOut, actionStdout);

        Assert.Contains("## Verdict contract", composed);
        Assert.Contains("VERIFIER", composed);
        Assert.Contains("Do NOT fix", composed);
        Assert.Contains(verdictOut, composed);
        Assert.Contains(actionStdout, composed);
        Assert.Contains("\"pass\"", composed);
        // A guardrail prompt never gets an output/needsHuman contract.
        Assert.DoesNotContain("## Output contract", composed);
    }

    [Fact]
    public void Body_IsPreservedAtTop()
    {
        string stateIn = WriteState("{}");
        string composed = PromptComposer.ComposeAction("FIRST LINE OF BODY", stateIn, Path.Combine(_dir, "o.json"), null);

        Assert.StartsWith("FIRST LINE OF BODY", composed);
    }

    // --- worktree safety (issue #192) ---------------------------------------------------------

    [Fact]
    public void Action_SerialMode_OmitsWorktreeSafetySection()
    {
        string stateIn = WriteState("{}");
        string composed = PromptComposer.ComposeAction(
            "body", stateIn, Path.Combine(_dir, "o.json"), feedbackPath: null, isWorktreeMode: false);

        Assert.DoesNotContain("## Worktree safety", composed);
        Assert.DoesNotContain("git stash", composed);
    }

    [Fact]
    public void Action_WorktreeMode_IncludesStashWarning_AndSafeAlternative()
    {
        string stateIn = WriteState("{}");
        string composed = PromptComposer.ComposeAction(
            "body", stateIn, Path.Combine(_dir, "o.json"), feedbackPath: null, isWorktreeMode: true);

        Assert.Contains("## Worktree safety", composed);
        Assert.Contains("git stash", composed);
        Assert.Contains("NOT safe", composed);
        Assert.Contains("repo-wide", composed);

        // Issue #382: the stash-free alternative must be runnable under the harness's OWN defaults,
        // and must tell the SAME story as the retry-salvage advice a retry prompt inlines alongside
        // it — read with the one git verb the harness provisions (`Bash(git show*)`, injected into
        // every invocation), write with the agent's own file-editing tool, and keep the scratch copy
        // INSIDE the worktree under the stage-excluded `.guardrails-agent-io/`.
        string section = WorktreeSafetySection(composed);
        Assert.Contains("git show", section);
        Assert.Contains("file-editing tool", section);
        Assert.Contains(".guardrails-agent-io", section);

        // The superseded three-line recipe must not come back. Every line of it was unusable under
        // the harness's own defaults: the redirect target resolved OUTSIDE the worktree, so the very
        // containment hook this section speaks for blocked it, and the checkout/apply write verbs are
        // ungranted on a clean box where the plan's allowedTools IS the whole grant.
        //
        // The ban is INVOCATION-shaped, matching the carve-out RetryPolicySalvageAdviceTests pins for
        // the sibling salvage advice: prose may NAME an ungranted verb to warn that it is ungranted
        // (`git apply` is named exactly that way below, and `/tmp` as an anti-example), but nothing
        // the agent could copy and run may survive. A trailing space is what separates the two — a
        // backticked bare span ends in a backtick, an invocation carries arguments.
        Assert.DoesNotContain("/tmp/mine.patch", section);
        Assert.DoesNotContain("> /tmp", section);
        Assert.DoesNotContain("git checkout", section);
        Assert.DoesNotContain("git apply ", section);
        Assert.DoesNotContain("git diff", section);

        // ...and the verb that IS named stays framed as ungranted, never as a step.
        Assert.Contains("`git apply`", section);
        Assert.Contains("not granted by", section);
    }

    // The advisory is appended LAST by both ComposeAction and ComposeGuardrail, so it runs to the end
    // of the prompt. Slicing it out keeps the negative assertions scoped to the advisory itself: a
    // retry prompt legitimately inlines feedback that MENTIONS the ungranted verbs (RetryPolicy hedges
    // `git apply` on allowedTools), and that must never read as this section recommending them.
    private static string WorktreeSafetySection(string composed)
    {
        int start = composed.IndexOf("## Worktree safety", StringComparison.Ordinal);
        Assert.True(start >= 0, "the composed prompt has no '## Worktree safety' section:\n" + composed);
        return composed[start..];
    }

    [Fact]
    public void Guardrail_WorktreeMode_IncludesStashWarning()
    {
        string stateIn = WriteState("{}");
        string composed = PromptComposer.ComposeGuardrail(
            "Judge the tone.", stateIn, Path.Combine(_dir, "v.json"), Path.Combine(_dir, "out.log"), isWorktreeMode: true);

        Assert.Contains("## Verdict contract", composed);
        Assert.Contains("## Worktree safety", composed);
        Assert.Contains("git stash", composed);
    }

    [Fact]
    public void Guardrail_SerialMode_OmitsWorktreeSafetySection()
    {
        string stateIn = WriteState("{}");
        string composed = PromptComposer.ComposeGuardrail(
            "Judge the tone.", stateIn, Path.Combine(_dir, "v.json"), Path.Combine(_dir, "out.log"), isWorktreeMode: false);

        Assert.DoesNotContain("## Worktree safety", composed);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }
}
