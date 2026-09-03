using System.Text.RegularExpressions;
using Guardrails.Core.Execution;
using static Guardrails.Core.Tests.PlanFixtures;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #382, wave 4 — the CONTENT of the retry-salvage advice (<c>RetryPolicy.AppendSalvageSection</c>).
///
/// Wave 3 made the harness PROVISION what its feedback prescribes: <c>Bash(git show*)</c> is now injected
/// into <c>--allowedTools</c> on every invocation. That fixes the grant; it does not fix the advice. This
/// suite pins the advice itself, on four axes:
///
/// <list type="number">
///   <item>the PATCH route leads — reading <c>prior-attempt.patch</c> and making targeted edits is the
///     cheapest option and must be presented first, not as an all-or-nothing "Pull in EVERYTHING";</item>
///   <item>the advice ROUTES BY SIZE off the <c>DiffStat</c> already embedded in the feedback — a handful
///     of changed lines means read the hunk and edit, an essentially-new file means pull the whole blob;</item>
///   <item>the ACCEPTANCE INVARIANT (load-bearing): every command the advice names is <c>git show</c>, the
///     patch path, or a file-editing tool — NOTHING else. <c>git diff &lt;taskBase&gt; &lt;ref&gt;</c> is
///     outside the injected grant, so leaving it reproduces this exact defect in miniature;</item>
///   <item>no copy-pasteable <c>git &lt;write-verb&gt;</c> invocation (checkout / restore / reset / stash),
///     while the PROSE may still name those verbs to warn they are ungranted (that warning is the point of
///     the #374 fix — it stops the agent burning turns rediscovering the refusal).</item>
/// </list>
///
/// Deliberate latitude: the scanner below judges INVOCATIONS, not prose. A backticked span that names a
/// bare verb (<c>git apply</c>) is a warning an agent cannot copy-paste and act on; a span carrying
/// arguments (<c>git apply "&lt;patch&gt;"</c>) is exactly the shape it does act on. Phrase assertions
/// accept a SET of wordings rather than one sentence, so the implementation is pinned to the DIRECTION,
/// not to today's prose.
///
/// Companion to <c>RetryPolicyTests.SalvageSection_NeverRecommendsAStateMutatingGitCommandAsThePerFileRoute</c>
/// (the #374 regression), which this suite strengthens rather than replaces.
/// </summary>
[Collection(GitEnvironmentCollection.Name)]
public sealed class RetryPolicySalvageAdviceTests
{
    private const string SalvagedRef = "refs/guardrails/07-impl/attempt-1";
    private const string SalvagedPatch = "/plan/logs/run/07-impl/attempt-1/prior-attempt.patch";

    /// <summary>A backticked span — how the feedback markdown hands the agent a command to run.</summary>
    private const string BacktickSpanPattern = @"`([^`\r\n]+)`";

    /// <summary>The one git verb this protocol prescribes, and (since wave 3) the one it provisions.</summary>
    private const string ProvisionedVerb = "show";

    // ── phrase sets ───────────────────────────────────────────────────────────────────────────────
    // Each assertion accepts ANY of these, so the wording stays the implementer's call.

    private static readonly string[] CheapestFirstPhrases = ["cheapest", "targeted", "surgical", "smallest"];

    private static readonly string[] WholePatchIsOftenWrongPhrases =
        ["often wrong", "usually wrong", "rarely", "out-of-scope", "out of scope", "outside your writescope"];

    private static readonly string[] HunkPhrases = ["hunk"];

    private static readonly string[] EditingToolPhrases = ["file-editing tool", "editing tool", "`edit`", "edit tool"];

    private static readonly string[] WholeBlobPhrases =
        ["whole blob", "whole file", "entire file", "essentially new", "essentially a new", "all additions", "mostly additions"];

    private static readonly string[] UngrantedWarningPhrases = ["not granted", "ungranted", "no grant", "not in the grant"];

    private static readonly string[] CwdIsTheWorktreePhrases =
        ["already", "unnecessary", "not needed", "no need", "cwd", "working directory"];

    /// <summary>Git subcommands that MUTATE state — never handed over as a runnable invocation (#374).</summary>
    private static readonly string[] WriteVerbs = ["checkout", "restore", "reset", "stash", "clean", "commit", "push"];

    /// <summary>Non-git command shapes: the "NOTHING else" half of the acceptance invariant.</summary>
    private static readonly string[] ForeignCommandPrefixes =
        ["patch ", "cat ", "cp ", "mv ", "rm ", "sed ", "awk ", "diff ", "curl ", "bash ", "sh ", "pwsh ", "powershell "];

    private static readonly ProcessResult Boom = new()
    {
        ExitCode = 1,
        StandardOutput = string.Empty,
        StandardError = "boom",
        TimedOut = false,
        Duration = TimeSpan.FromSeconds(1)
    };

    // Deliberately NOT a protected-artifact (tests-untouched) name: that archetype suppresses salvage
    // entirely (WEAK-1), which would make these tests vacuous on the guardrail path.
    private static readonly List<GuardrailResult> GuardrailVerdicts =
    [
        new() { Name = "01-build", Passed = true },
        new() { Name = "02-covers-key-behaviors", Passed = false, Reason = "add a test referencing `ingress`" }
    ];

    private static readonly List<WriteScopeOffense> Offenses = [new() { Path = "outside.txt", Status = 'A' }];

    // ── 1. the patch route leads ──────────────────────────────────────────────────────────────────

    [Fact]
    public void PatchRoute_LeadsTheAdvice_AndIsNotFramedAsAllOrNothing()
    {
        // Measured, not assumed: across 356 real file-emitting tool calls the largest single emission was
        // 13% of the output cap — agents use targeted `Edit`, never whole-file rewrites — and one real
        // hand-recovery cost ~4,200 output tokens with no transcription drift. So the cheapest route is
        // READ the patch + edit the affected lines. `prior-attempt.patch` needs no git at all and is
        // already inside the granted read surface (the harness emits `--add-dir <planDirectory>`
        // unconditionally), which makes it strictly better than a whole blob for a surgical fix.
        //
        // Today the same bullet exists but is framed "Pull in EVERYTHING", which reads all-or-nothing and
        // hides the cheapest option behind the most expensive one.
        string section = SalvageSection(Feedback("action", SmallDiff));
        string[] bullets = Bullets(section);

        Assert.True(bullets.Length > 0, $"The salvage advice offers no options at all.\n{Emitted(section)}");
        Assert.Contains("prior-attempt.patch", bullets[0]);

        Assert.DoesNotContain("EVERYTHING", section);

        Assert.True(
            IndexOf(section, "prior-attempt.patch") < IndexOf(section, "git show"),
            "The patch route must be presented BEFORE the `git show` blob route — it is the cheaper of the " +
            $"two and needs no git at all.\n{Emitted(section)}");

        AssertSays(section, CheapestFirstPhrases, "that the patch route is the cheap/targeted one");
        AssertSays(section, EditingToolPhrases, "that the recovered lines are written back with the file-editing tool");
    }

    [Fact]
    public void WholePatchAdoption_IsWarnedAsOftenWrong_NotOfferedAsTheDefault()
    {
        // Observed in a real run: the agent correctly REFUSED whole-patch adoption because the patch
        // carried out-of-scope packages.lock.json churn that would have failed the write-scope check.
        // Adopting the lot is the exceptional case, not the headline — the advice must say so.
        string section = SalvageSection(Feedback("action", SmallDiff));

        AssertSays(section, WholePatchIsOftenWrongPhrases, "that adopting the WHOLE patch is often the wrong move");
    }

    // ── 2. size routing off DiffStat ──────────────────────────────────────────────────────────────

    [Fact]
    public void SizeRouting_FewChangedLines_PointsAtTheHunkAndATargetedEdit()
    {
        // `SalvageRef.DiffStat` already carries per-file changed-line counts and is already embedded in
        // the feedback. Nothing connects it to a CHOICE today, so the agent gets one flat recipe for a
        // 3-line fix and a 400-line new file alike. Three changed lines means: read that file's hunk in
        // the patch, edit those lines, done.
        string section = SalvageSection(Feedback("action", SmallDiff));

        AssertSays(section, HunkPhrases, "that a small diff is recovered by reading its HUNK in the patch");
        AssertSays(section, EditingToolPhrases, "that the hunk is applied with the file-editing tool");
    }

    [Fact]
    public void SizeRouting_EssentiallyNewFile_PointsAtPullingTheWholeBlobWithGitShow()
    {
        // The other half of the rule: when a file is essentially NEW (the diff-stat is nearly all
        // additions) there is no hunk worth reading — pull the whole prior blob and write it back.
        string section = SalvageSection(Feedback("action", NewFile));

        AssertSays(section, WholeBlobPhrases, "that an essentially-new file is recovered as a WHOLE blob");
        Assert.Contains($"git show \"{SalvagedRef}:<path>\"", section);
    }

    // ── 3. the acceptance invariant ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("action", "small")]
    [InlineData("timeout", "new")]
    [InlineData("max-turns", "no-patch")]
    [InlineData("output-cap", "small")]
    [InlineData("guardrail", "new")]
    [InlineData("write-scope", "no-patch")]
    public void EveryCommandNamed_IsGitShow_ThePatchPath_OrAFileEditingTool(string entryPoint, string salvageKind)
    {
        // THE load-bearing assertion of this wave. Wave 3 provisions exactly one verb (`Bash(git show*)`),
        // so exactly one verb may be prescribed. Anything else the advice hands over as a runnable command
        // is refused on a clean box — which is issue #382 reproduced in miniature, inside the very text
        // that exists to stop it. Checked on EVERY entry point that appends the section, and for a salvage
        // ref with and without a patch file, because the defect is in the shared section, not one caller.
        string section = SalvageSection(Feedback(entryPoint, Salvage(salvageKind)));
        IReadOnlyList<string> offenders = UngrantedCommands(section);

        Assert.True(
            offenders.Count == 0,
            "The salvage advice names command(s) the harness does not provision. Every command it emits " +
            "must be `git show`, the patch path, or a file-editing tool — nothing else.\n" +
            string.Join("\n", offenders.Select(o => "  - " + o)) + "\n" + Emitted(section));
    }

    [Fact]
    public void TheUngrantedGitDiffInspectionAlternative_IsGone()
    {
        // `git diff <taskBase> "<ref>"` is the last command in the salvage text outside the injected
        // grant. `git show --stat "<ref>"` already covers inspection, so the prescribed set collapses to
        // the single provisioned verb. This is called out separately from the scanner above because it is
        // the specific survivor of PR #426 — and because a bare "git diff" left in prose is still an
        // invitation to try it.
        string section = StripFencedBlocks(SalvageSection(Feedback("action", SmallDiff)));

        Assert.DoesNotContain("git diff", section);
        Assert.DoesNotContain("<taskBase>", section);
    }

    [Fact]
    public void TheWorkingInvocationShape_IsNamed_SoTheAgentDoesNotPrefixGitDashC()
    {
        // The dominant refusal is an invocation SHAPE, not a verb: `git -C <abs-path> …` was refused 86
        // times in one run and it kills read-only verbs too (4 of 4 `git -C show` calls). The harness sets
        // cwd to the worktree, so the `-C` prefix is unnecessary as well as fatal. Saying so is cheaper
        // than any grant.
        string section = SalvageSection(Feedback("action", SmallDiff));

        Assert.Contains("git -C", section);
        AssertSays(section, CwdIsTheWorktreePhrases, "that the harness already runs you IN the worktree, so `-C` is unnecessary");
    }

    // ── 4. write verbs: named in prose, never handed over as a command ─────────────────────────────

    [Theory]
    [InlineData("action", "small")]
    [InlineData("guardrail", "new")]
    [InlineData("write-scope", "no-patch")]
    public void NoCopyPasteableWriteVerbInvocation_IsEmitted(string entryPoint, string salvageKind)
    {
        // #374's direction, re-pinned at INVOCATION granularity so the rewrite cannot lose it: the prose
        // may name `git checkout` / `git restore` to warn they are ungranted, but a span carrying
        // arguments is a command an agent copies and runs. Green today — it must STAY green.
        string section = SalvageSection(Feedback(entryPoint, Salvage(salvageKind)));

        List<string> offenders = [];
        foreach (string span in BacktickSpans(section))
        {
            string[] tokens = span.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3 || tokens[0] != "git")
            {
                continue;
            }

            string verb = Subcommand(tokens);
            if (WriteVerbs.Contains(verb, StringComparer.Ordinal))
            {
                offenders.Add($"`{span}` — `git {verb}` mutates state and is not granted");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "The salvage advice hands over a runnable state-mutating git command. Naming the verb in prose " +
            "to warn it is ungranted is fine; a copy-pasteable invocation is not.\n" +
            string.Join("\n", offenders.Select(o => "  - " + o)) + "\n" + Emitted(section));
    }

    [Fact]
    public void WriteVerbs_AreStillNamedInProse_AsUngranted()
    {
        // The other side of the same coin, and the reason the rule above is invocation-shaped rather than
        // a flat substring ban: the WARNING is load-bearing. #374 was observed live as an agent burning
        // ~4 turns rediscovering that `git checkout` is refused. Deleting the warning to satisfy a
        // substring ban would re-open that hole. Green today — it must STAY green.
        string section = SalvageSection(Feedback("action", SmallDiff));

        AssertSays(section, UngrantedWarningPhrases, "that the write-side git verbs are NOT granted");
    }

    [Fact]
    public void NoSalvageRef_StillEmitsNoSalvageSection()
    {
        // No-collateral guard: the rewrite must not start advertising recovery for an attempt that has
        // nothing stashed. Green today — it must STAY green.
        string feedback = RetryPolicy.ForActionFailure(
            PromptTask("07-impl"), attempt: 2, Boom, fileWritesRolledBack: true);

        Assert.Null(TryFindSalvageSection(feedback));
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A handful of changed lines in an existing file — the "read the hunk and edit" case.</summary>
    private static SalvageRef SmallDiff => new(
        SalvagedRef,
        " src/Widget.cs | 3 ++-\n 1 file changed, 2 insertions(+), 1 deletion(-)",
        Attempt: 1,
        PatchPath: SalvagedPatch);

    /// <summary>An essentially-new file (all additions) — the "pull the whole blob" case.</summary>
    private static SalvageRef NewFile => new(
        SalvagedRef,
        " src/Widget/NewThing.cs | 412 ++++++++++++++++++++++++\n 1 file changed, 412 insertions(+)",
        Attempt: 1,
        PatchPath: SalvagedPatch);

    /// <summary>Salvage preserved the ref but the patch could not be written (best-effort, #306).</summary>
    private static SalvageRef NoPatch => new(SalvagedRef, " src/Widget.cs | 3 ++-", Attempt: 1);

    private static SalvageRef Salvage(string kind) => kind switch
    {
        "small" => SmallDiff,
        "new" => NewFile,
        "no-patch" => NoPatch,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown salvage fixture")
    };

    /// <summary>Every <c>RetryPolicy</c> entry point that appends the shared salvage section.</summary>
    private static string Feedback(string entryPoint, SalvageRef salvage) => entryPoint switch
    {
        "action" => RetryPolicy.ForActionFailure(
            PromptTask("07-impl"), attempt: 2, Boom, fileWritesRolledBack: true, salvageRef: salvage),
        "timeout" => RetryPolicy.ForTimeout(
            PromptTask("07-impl"), attempt: 2, fileWritesRolledBack: true, salvageRef: salvage),
        "max-turns" => RetryPolicy.ForMaxTurnsExceeded(
            PromptTask("07-impl"), attempt: 2, fileWritesRolledBack: true, salvageRef: salvage),
        "output-cap" => RetryPolicy.ForOutputCapExceeded(
            PromptTask("07-impl"), attempt: 2, salvageRef: salvage, fileWritesRolledBack: true),
        "guardrail" => RetryPolicy.ForGuardrailFailures(
            PromptTask("07-impl"), attempt: 2, GuardrailVerdicts, fileWritesRolledBack: true, salvageRef: salvage),
        "write-scope" => RetryPolicy.ForWriteScopeViolation(
            PromptTask("07-impl"), attempt: 2, Offenses, fileWritesRolledBack: true, salvageRef: salvage),
        _ => throw new ArgumentOutOfRangeException(nameof(entryPoint), entryPoint, "unknown RetryPolicy entry point")
    };

    // ── section extraction ────────────────────────────────────────────────────────────────────────

    private static string SalvageSection(string feedback)
    {
        string? section = TryFindSalvageSection(feedback);
        Assert.True(
            section is not null,
            "No salvage section was emitted at all — expected a `## …salvage…` heading.\n" + Emitted(feedback));
        return section!;
    }

    /// <summary>
    /// Locate the salvage section by HEADING SHAPE (a level-2 heading mentioning salvage) rather than by
    /// its exact title, so renaming the heading does not silently blind this suite.
    /// </summary>
    private static string? TryFindSalvageSection(string feedback)
    {
        string[] lines = Normalize(feedback).Split('\n');
        int start = Array.FindIndex(
            lines,
            l => l.StartsWith("## ", StringComparison.Ordinal) && l.Contains("salvage", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            return null;
        }

        int end = Array.FindIndex(lines, start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        return string.Join("\n", lines[start..(end < 0 ? lines.Length : end)]);
    }

    private static string[] Bullets(string section) =>
        [.. StripFencedBlocks(section).Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal))];

    /// <summary>
    /// Drop fenced blocks before scanning: the embedded <c>DiffStat</c> is DATA the harness computed, not
    /// advice, and must never be read as a command the text prescribes.
    /// </summary>
    private static string StripFencedBlocks(string section)
    {
        List<string> kept = [];
        bool inFence = false;
        foreach (string line in Normalize(section).Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence)
            {
                kept.Add(line);
            }
        }

        return string.Join("\n", kept);
    }

    // ── the command scanner ───────────────────────────────────────────────────────────────────────

    private static IEnumerable<string> BacktickSpans(string section) =>
        Regex.Matches(StripFencedBlocks(section), BacktickSpanPattern).Select(m => m.Groups[1].Value.Trim());

    /// <summary>Every backticked span in the section that names something the harness does not provision.</summary>
    private static IReadOnlyList<string> UngrantedCommands(string section)
    {
        List<string> offenders = [];
        foreach (string span in BacktickSpans(section))
        {
            if (Offense(span) is { } reason)
            {
                offenders.Add($"`{span}` — {reason}");
            }
        }

        return offenders;
    }

    private static string? Offense(string span)
    {
        string[] tokens = span.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        if (tokens[0] == "git")
        {
            // Two tokens is a VERB NAMED IN PROSE (`git apply`), not something an agent can copy and run.
            // Requirement 4 protects that deliberately: the warning is what stops the wasted turns.
            if (tokens.Length <= 2)
            {
                return null;
            }

            string verb = Subcommand(tokens);

            // No concrete subcommand (`git -C <abs-path>`): an invocation-SHAPE anti-example, not a
            // prescription. Wave 4 requires naming that shape, so it must not be scored as a command.
            if (verb.Length == 0 || !IsPlainWord(verb))
            {
                return null;
            }

            return verb == ProvisionedVerb
                ? null
                : $"names `git {verb}`, but the only git verb wave 3 provisions (and this protocol prescribes) is `git {ProvisionedVerb}`";
        }

        foreach (string prefix in ForeignCommandPrefixes)
        {
            if (span.StartsWith(prefix, StringComparison.Ordinal))
            {
                return $"invokes `{prefix.Trim()}`, which is neither `git {ProvisionedVerb}`, the patch path, nor a file-editing tool";
            }
        }

        return span.Contains(" > ", StringComparison.Ordinal)
            ? "redirects to a file — the harness's own containment hook blocks that, and it is not a file-editing tool"
            : null;
    }

    /// <summary>The subcommand of a git invocation, skipping global options (<c>-C &lt;path&gt;</c> et al).</summary>
    private static string Subcommand(string[] tokens)
    {
        int i = 1;
        while (i < tokens.Length && tokens[i].StartsWith('-'))
        {
            if (tokens[i] is "-C" or "--git-dir" or "--work-tree")
            {
                i++;
            }

            i++;
        }

        return i < tokens.Length ? tokens[i] : string.Empty;
    }

    /// <summary>True for a real subcommand token — not a placeholder (<c>&lt;cmd&gt;</c>) or an ellipsis.</summary>
    private static bool IsPlainWord(string token) => token.All(c => char.IsAsciiLetterLower(c) || c == '-');

    // ── assertion helpers ─────────────────────────────────────────────────────────────────────────

    private static void AssertSays(string section, string[] anyOf, string what)
    {
        Assert.True(
            anyOf.Any(phrase => section.Contains(phrase, StringComparison.OrdinalIgnoreCase)),
            $"The salvage advice never states {what}. The wording is yours, but the section must contain at " +
            $"least one of: {string.Join(", ", anyOf.Select(p => $"\"{p}\""))}.\n{Emitted(section)}");
    }

    private static int IndexOf(string section, string value) => section.IndexOf(value, StringComparison.Ordinal);

    private static string Normalize(string value) => value.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string Emitted(string section) =>
        "----- emitted salvage advice -----\n" + section + "\n----------------------------------";
}
