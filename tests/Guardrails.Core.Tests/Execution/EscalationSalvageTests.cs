using Guardrails.Core.Execution;
using Guardrails.Core.Graph;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.Execution;

/// <summary>
/// Issue #554, plan 31 §3.5 clarifications 1–2 — the FORWARD CARRY half of escalation salvage: a prior
/// attempt that left a <c>prior-attempt.patch</c> must reach the NEXT attempt's COMPOSED PROMPT as a
/// recovery-routing block, not as one more log-path bullet.
///
/// <para><b>Why the composed prompt and not <c>feedback.md</c> (§3.5 clarification 1).</b> The escalation
/// path returns <c>FeedbackPath: null</c> (<c>AttemptJournaler.NeedsHuman</c>), so the inlined-feedback
/// route is not the carrier here — the carry runs through <c>PriorAttemptRef</c>. A test reading
/// <c>feedback.md</c> would pass with the composed prompt still silent, which is the whole defect.</para>
///
/// <para><b>Why the pins name no new API member (plan §7).</b> #554 needs no stub stage: every assertion
/// below is on an OBSERVABLE ARTIFACT — a file laid down on disk, and the composed string. The tests
/// therefore compile against today's assemblies and fail because the FEATURE is absent, never because a
/// member throws. Concretely: no prior-attempt pointer carrying a patch is ever constructed here. The
/// log directory is laid down by hand, and <c>DependencyContextBuilder.BuildPriorAttempts</c> — the
/// production filler — is driven over it, exactly as a real attempt would be.</para>
///
/// <para><b>Phrase SETS, not sentences.</b> Following the shipped
/// <c>RetryPolicySalvageAdviceTests</c> convention: each behavioural assertion accepts any of several
/// wordings, so the pin binds the DIRECTION the text must take and leaves the prose to the
/// implementer.</para>
/// </summary>
public sealed class EscalationSalvageTests : IDisposable
{
    private const string TaskId = "01-implement";

    /// <summary>
    /// The ref name §3.3 says is DERIVED — <c>refs/guardrails/&lt;taskId&gt;/attempt-&lt;N&gt;</c> — never
    /// journalled. Written out as a literal so this suite pins the derivation rather than re-deriving it
    /// with the same expression the implementation uses (which would agree with any bug).
    /// </summary>
    private const string DerivedRefForAttempt1 = "refs/guardrails/01-implement/attempt-1";

    /// <summary>The artifact whose PRESENCE on disk is the record that an attempt left recoverable work.</summary>
    private const string PatchFileName = "prior-attempt.patch";

    /// <summary>A real (small) unified diff, so the laid-down patch is non-empty exactly like a live one.</summary>
    private const string PatchBytes =
        "diff --git a/src/widget.txt b/src/widget.txt\n" +
        "index e69de29..d95f3ad 100644\n" +
        "--- a/src/widget.txt\n" +
        "+++ b/src/widget.txt\n" +
        "@@ -0,0 +1 @@\n" +
        "+attempt-1-output\n";

    // ── phrase sets ───────────────────────────────────────────────────────────────────────────────
    // Mirrors RetrySalvageAdvice's convention: the wording stays the implementer's call, the direction
    // does not. These are the two halves of the SIZE ROUTING (§3.5 clarification 2).

    /// <summary>The small-edit half: read that file's hunk in the patch and edit those lines back.</summary>
    private static readonly string[] SmallEditPhrases = ["hunk", "few changed lines", "changed lines"];

    /// <summary>The essentially-new-file half: no hunk is worth reading, so take the whole prior blob.</summary>
    private static readonly string[] WholeBlobPhrases =
        ["whole blob", "whole file", "entire file", "essentially new", "essentially a new", "all additions", "mostly additions"];

    /// <summary>The caveat: salvaged files are still governed by the task's declared write scope.</summary>
    private static readonly string[] WriteScopeTokens = ["writescope", "write-scope", "write scope"];

    /// <summary>…and that the caveat is a CONSTRAINT on adoption, not an incidental mention of the word.</summary>
    private static readonly string[] StillGovernedPhrases =
        ["remain subject to", "still subject to", "are subject to", "remains subject to", "subject to this task", "subject to the task"];

    /// <summary>
    /// The markers that, taken together, ARE the recovery-routing block. C4 asserts every one of them is
    /// absent; defining "no block" as a positive marker list (rather than as a heading lookup) keeps the
    /// negative pin from silently passing because a heading got renamed.
    /// </summary>
    private static readonly string[] RecoveryRoutingMarkers =
        [PatchFileName, "refs/guardrails/", "git show", "salvageable"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gr-esc-salvage-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => SafeDeleteTree(_root);

    // ── C1 — the size-routed choice, both halves ──────────────────────────────────────────────────

    [Fact]
    public void PriorAttemptWithPatch_ComposedPromptCarriesSizeRoutedRecoveryChoice()
    {
        // §3.5 clarification 2 — "name it" is NOT enough. `PromptComposer.AppendPreviousAttempt` already
        // renders every prior attempt as a log-path bullet whose only instruction is "read the transcript
        // … and the feedback"; adding the patch as one more bullet satisfies "names it" and changes
        // nothing an agent does. What the next attempt needs is the ROUTING: read the patch's hunk for a
        // handful of changed lines, take the whole prior blob for a file that is essentially new.
        string composed = ComposeNextAttemptPrompt(priorLeftAPatch: true);

        Assert.Contains(PatchFileName, composed);

        // The verbatim command shape already pinned by RetryPolicySalvageAdviceTests, so the two suites
        // cannot drift: `git show` is the ONE git verb the harness provisions (#382), and the ref/path
        // placeholder form is what an agent copies.
        Assert.Contains($"git show \"{DerivedRefForAttempt1}:<path>\"", composed);

        AssertSays(composed, SmallEditPhrases,
            "how to recover a file with a handful of changed lines (read its HUNK in the patch)");
        AssertSays(composed, WholeBlobPhrases,
            "how to recover an essentially-NEW file (take the whole prior blob, no hunk worth reading)");
    }

    // ── C2 — the writeScope caveat ────────────────────────────────────────────────────────────────

    [Fact]
    public void PriorAttemptWithPatch_ComposedPromptCarriesTheWriteScopeCaveat()
    {
        // The other half of §3.5 clarification 2. Adoption is not a licence: whatever the agent pulls
        // back is still measured by the retrospective write-scope check over its FINAL state, so a prompt
        // that offers recovery without saying so invites an attempt that fails the check it was never
        // warned about (plan §11 Risk 6).
        //
        // The composer is driven with worktree mode OFF deliberately. Its `## Worktree safety` section
        // independently names both `git show` and `writeScope`, so leaving it on would let this pin pass
        // on boilerplate that has nothing to do with the salvage carry.
        string composed = ComposeNextAttemptPrompt(priorLeftAPatch: true);

        AssertSays(composed, WriteScopeTokens, "that the task's declared write scope is involved at all");
        AssertSays(composed, StillGovernedPhrases,
            "that salvaged files REMAIN SUBJECT to that scope (naming the word alone is not the caveat)");
    }

    // ── C3 — the ref is DERIVED, not journalled ───────────────────────────────────────────────────

    [Fact]
    public void PriorAttemptWithPatch_ComposedPromptNamesTheDerivedSalvageRef()
    {
        // §3.3, "Why PriorAttemptRef and not a new journal field": the ref name is fully derivable from
        // the task id and the attempt number, and the patch's presence on disk is the record. Journalling
        // either would create a second source of truth for a fact the filesystem already holds. This pin
        // is what proves the derivation actually happens — the journal entry this fixture records carries
        // no ref name anywhere.
        string composed = ComposeNextAttemptPrompt(priorLeftAPatch: true);

        Assert.Contains(DerivedRefForAttempt1, composed);
    }

    // ── C4 — DECLARED EXEMPTION: no patch ⇒ no recovery block at all ──────────────────────────────

    [Fact]
    public void PriorAttemptWithoutPatch_ComposedPromptCarriesNoRecoveryBlock()
    {
        // DECLARED EXEMPTION (guardrail 02's manifest records it as Expect='Executed'). Today there is no
        // recovery block on ANY path, so a CORRECT test is green here before the feature lands as well as
        // after; demanding red would demand that a correct implementation fail. It is written and run
        // anyway, because a row that is silently dropped and a row that was overlooked look identical
        // from the outside.
        //
        // The rule it holds (§3.4, §11): the empty-diff guard stays exactly as-is. An attempt that
        // escalated having written nothing has nothing to salvage, and offering "recover your work" for
        // an absent patch is worse than silence — a signal that fires when nothing is wrong gets muted,
        // and then the real one is invisible too.
        string composed = ComposeNextAttemptPrompt(priorLeftAPatch: false);

        // Not vacuous: the prior attempt IS rendered into the prompt. Without this the pin would pass for
        // the boring reason that no prior attempt reached the composer at all.
        Assert.Contains("Attempt 1 (", composed);

        List<string> present = RecoveryRoutingMarkers
            .Where(m => composed.Contains(m, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            present.Count == 0,
            "The composed prompt offers recovery routing for a prior attempt that left NO " +
            $"{PatchFileName}. Marker(s) found: {string.Join(", ", present)}.\n" + Emitted(composed));
    }

    // ── fixture ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lay down ONE prior attempt of <see cref="TaskId"/> exactly as the escalation path leaves it — a
    /// journalled <c>needs-human</c> attempt whose log dir does (or does not) contain a non-empty
    /// <see cref="PatchFileName"/> — then compose the NEXT attempt's action prompt over it through the
    /// two production seams: <c>DependencyContextBuilder.BuildPriorAttempts</c> (which already walks the
    /// journal and already knows each prior attempt's log dir) and <c>PromptComposer.ComposeAction</c>.
    ///
    /// <para><c>feedbackPath</c> is null on purpose: that is what the escalation path returns (§3.5
    /// clarification 1), so the carry under test is the prior-attempt one, never the inlined-feedback
    /// one.</para>
    /// </summary>
    private string ComposeNextAttemptPrompt(bool priorLeftAPatch)
    {
        string planDir = Path.Combine(_root, "plan");
        string taskDir = Path.Combine(planDir, "tasks", TaskId);
        Directory.CreateDirectory(taskDir);

        var task = new TaskNode
        {
            Id = TaskId,
            Directory = taskDir,
            Description = "escalation-salvage forward-carry fixture",
            Action = new ActionDefinition
            {
                Path = Path.Combine(taskDir, "action.prompt.md"),
                Kind = ActionKind.Prompt
            },
            Guardrails =
            [
                new GuardrailDefinition
                {
                    Name = "01-ok",
                    Path = Path.Combine(taskDir, "guardrails", "01-ok.sh"),
                    Kind = ActionKind.Script
                }
            ],
            WriteScope = ["src/"]
        };

        var plan = new PlanDefinition
        {
            PlanDirectory = planDir,
            Workspace = _root,
            Config = new RunConfig { Version = 1 },
            Tasks = [task]
        };

        RunJournal journal = RunJournal.LoadOrCreate(plan);

        // The log dir is journalled PLAN-RELATIVE with forward slashes (TaskExecutor.RelativeLogDir);
        // BuildPriorAttempts resolves it back against the plan directory.
        string relativeLogDir = $"logs/{journal.Document.RunId}/{TaskId}/attempt-1";
        string logDir = Path.Combine(planDir, "logs", journal.Document.RunId, TaskId, "attempt-1");
        Directory.CreateDirectory(logDir);
        WriteFixtureFile(Path.Combine(logDir, "transcript.md"), "# attempt 1\n\nwrote src/widget.txt, then asked a human.\n");
        if (priorLeftAPatch)
        {
            WriteFixtureFile(Path.Combine(logDir, PatchFileName), PatchBytes);
        }

        DateTimeOffset at = DateTimeOffset.UtcNow;
        journal.RecordAttempt(
            TaskId,
            new AttemptRecord
            {
                Attempt = 1,
                StartedAt = at,
                EndedAt = at,
                ActionExitCode = 0,
                Outcome = AttemptOutcome.NeedsHuman,
                LogDir = relativeLogDir
            },
            Guardrails.Core.Journal.TaskStatus.NeedsHuman);

        var builder = new DependencyContextBuilder(
            plan,
            journal,
            new DependencyGraph(plan.Tasks),
            new Dictionary<string, TaskNode>(StringComparer.Ordinal) { [TaskId] = task });

        IReadOnlyList<PriorAttemptRef> priorAttempts = builder.BuildPriorAttempts(TaskId, currentAttemptNumber: 2);

        // Sanity, not a behaviour: the production walker really did see the attempt this fixture wrote.
        // Without it a builder change that returned nothing would make every pin above read as a silent
        // "the feature is missing" instead of "the fixture is broken".
        Assert.Single(priorAttempts);

        string stateInPath = Path.Combine(_root, "state.json");
        WriteFixtureFile(stateInPath, "{}");

        return PromptComposer.ComposeAction(
            body: "Implement the widget.",
            stateInPath: stateInPath,
            stateOutPath: Path.Combine(_root, "fragment.json"),
            feedbackPath: null,
            priorAttempts: priorAttempts);
    }

    // ── assertion + IO helpers ────────────────────────────────────────────────────────────────────

    private static void AssertSays(string composed, string[] anyOf, string what)
    {
        Assert.True(
            anyOf.Any(phrase => composed.Contains(phrase, StringComparison.OrdinalIgnoreCase)),
            $"The composed prompt never states {what}. The wording is yours, but it must contain at " +
            $"least one of: {string.Join(", ", anyOf.Select(p => $"\"{p}\""))}.\n{Emitted(composed)}");
    }

    private static string Emitted(string composed) =>
        "----- composed prompt -----\n" + composed + "\n---------------------------";

    /// <summary>Write a fixture file, creating its parent directory first.</summary>
    private static void WriteFixtureFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    /// Windows-safe recursive delete: strip the read-only attribute first, and catch
    /// <see cref="UnauthorizedAccessException"/> as well as <see cref="IOException"/>, because a
    /// read-only file makes <see cref="Directory.Delete(string, bool)"/> throw the FORMER (#116). This
    /// fixture writes no git objects, but teardown failing a green class is the same defect either way.
    /// </summary>
    private static void SafeDeleteTree(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
        catch (IOException) { /* best-effort teardown */ }
        catch (UnauthorizedAccessException) { /* best-effort teardown */ }
    }
}
