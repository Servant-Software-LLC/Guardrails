using System.Text;
using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Prompts;

/// <summary>
/// The <c>kind: "cursor"</c> prompt runner (#764): Cursor's Agent CLI (<c>agent</c>) headless. ALL
/// Cursor-specific flag spelling is confined to this class (SSOT §9.9). Invocation:
/// <code>
/// agent -p --output-format stream-json [--force | --auto-review] --trust --workspace &lt;cwd&gt;
///   [--model &lt;m&gt;] --add-dir &lt;planDir&gt; [extraArgs…]
/// </code>
/// with the composed prompt on STDIN and NO positional prompt (see <see cref="Deliver"/>), and cwd =
/// workspace. The approval flag is the block's <c>approvalMode</c> (#767, <see cref="CursorApprovalMode"/>):
/// <c>force</c> ⇒ <c>--force</c> (the default), <c>auto-review</c> ⇒ <c>--auto-review</c>, <c>none</c> ⇒
/// neither. <c>--trust</c> is always passed.
///
/// <para><b>What is deliberately NOT emitted.</b> Cursor rejects <c>--verbose</c>, <c>--permission-mode</c>,
/// <c>--max-turns</c> and <c>--allowedTools</c> as unknown options and exits at parse time, before any model
/// call — that is the failure #764 reported. <c>permissionMode</c>, <c>allowedTools</c>, <c>maxTurns</c> and
/// <c>maxOutputTokens</c> therefore have no Cursor spelling and are IGNORED for a cursor block (a validate
/// warning, GR2080, names every one a block declares). The bounds that DO apply are
/// <see cref="PromptInvocation.Timeout"/> and <see cref="PromptInvocation.StallBound"/>, both enforced by the
/// shared <see cref="StreamJsonCliSession"/>.</para>
///
/// <para><b>Approval is chosen by the operator, and said so.</b> Print mode has no per-tool allowlist the harness
/// can set — measured, a project <c>.cursor/cli.json</c> allowlist has no effect there — so the only lever is
/// the approval mode. Measured on an enterprise account whose administrator disabled "Run Everything" (Cursor
/// <c>agent</c> 2026.09.23): <c>--force</c> is refused at launch (classified
/// <see cref="PromptFailureKind.RunnerConfiguration"/> by <see cref="CursorSignalClassifier"/>, so it halts
/// needs-human with the remedy instead of burning retries); <c>--auto-review</c>, <c>--sandbox enabled</c>, or
/// both, run file writes AND shell; with no approval flag and no sandbox, files are written but EVERY shell
/// call is rejected; and <c>--auto-review</c> with <c>--force</c> is a CLI error ("pick one"), which is why
/// GR2082 keeps the approval flags out of <c>extraArgs</c>. GR2080 states what the chosen mode grants. The
/// Claude worktree-containment hook (<c>--settings</c>, SSOT §9.4) is a Claude
/// Code PreToolUse hook Cursor cannot load, so <see cref="PromptRunnerKinds.NeedsContainmentHook"/> is false
/// for this kind and the flag is REFUSED here if it ever arrives. Cursor does not serve the
/// <see cref="PromptRole.Advisory"/> role (<see cref="PromptRunnerKinds.ServesRoles"/>): the overwatcher,
/// ai-triage and the criticality judge are read-only by construction and must never run on an agent with no
/// allowlist, so an Advisory invocation is refused before anything launches.</para>
///
/// <para><b>The stream is parsed by <see cref="ClaudeStreamParser"/>, unforked.</b> Cursor's
/// <c>stream-json</c> matches Claude's envelope at the points the parser reads — the opening
/// <c>system/init</c>, the terminal <c>result</c> and its <c>usage</c> (camelCase on Cursor, read by the same
/// parser) — and the parser skips every other event type, including Cursor's <c>tool_call</c> and
/// <c>thinking</c>. Cursor's result carries no cost and no turn count, so both stay NULL. The init
/// <c>model</c> is a DISPLAY name (<c>"Auto"</c> when no <c>--model</c> is given), not a model id, so it is
/// NOT reported as <see cref="PromptResult.ObservedModel"/> (which would read as a model mismatch on every
/// attempt); it is named in the summary instead.</para>
///
/// <para><b>Refused tool calls are read per call (#773).</b> Cursor's terminal result says <c>success</c> even
/// when every shell call was refused, so the parser's verdict is not enough. <see cref="CursorToolCallScanner"/>
/// reads each completed <c>tool_call</c>'s own result, and pairs <c>started</c>/<c>completed</c> by <c>call_id</c>
/// so a call still open at the terminal result counts as an ABANDONED refusal (#778 — auto-review holding a command
/// for an approval print mode cannot give); its refusals feed
/// <see cref="PromptResult.BlockedWritePaths"/> / <see cref="PromptResult.RefusedCommands"/> (so
/// <c>PermissionWallTracker</c> sees them exactly as it sees Claude's), the #452 consecutive-refusal counter
/// (it trips <see cref="PromptInvocation.AbortAfterConsecutiveToolDenials"/> when a caller sets that bound — no
/// task-action caller does today, so for cursor actions the fail-fast is available but not active), and
/// <see cref="PromptResult.RefusedToolCalls"/>, which names each refusal and its reason in the summary and the
/// retry feedback. The verdict rule is in <see cref="Finish"/>: an every-shell-refused ACTION completes marked
/// <see cref="PromptResult.AllShellRefused"/> and its guardrails decide (needs-human with the approval-mode
/// remedy if they fail); an every-shell-refused JUDGE fails closed; a session with SOME refusals completes,
/// carrying them — the same rule the Claude path applies to a denial the agent routed around (#534 / #708).
/// Known gap: refusals inside a <c>taskToolCall</c> subagent's nested conversation steps are not scanned.</para>
/// </summary>
public sealed class CursorPromptRunner : IPromptRunner
{
    /// <summary>
    /// The <c>command</c> a cursor block defaults to when it declares none — Cursor's Agent CLI binary.
    /// </summary>
    public const string DefaultCommand = "agent";

    /// <summary>
    /// The four Claude flags Cursor rejects as unknown options (exit at parse time). Pinned by a test so no
    /// future edit can reintroduce one into the argv.
    /// </summary>
    internal static readonly IReadOnlyList<string> RejectedClaudeFlags =
        ["--verbose", "--permission-mode", "--max-turns", "--allowedTools"];

    /// <summary>
    /// How many leading characters of the composed prompt the echo check compares (see
    /// <see cref="PromptEchoCheck"/>).
    /// </summary>
    internal const int EchoPrefixChars = 4096;

    /// <summary>Cursor's "Run Everything" flag — emitted for <see cref="CursorApprovalMode.Force"/> only.</summary>
    internal const string ForceFlag = "--force";

    /// <summary>Cursor's alias of <see cref="ForceFlag"/>; never emitted, but refused in <c>extraArgs</c> (GR2082).</summary>
    internal const string YoloFlag = "--yolo";

    /// <summary>Cursor's classifier-reviewed approval flag — emitted for <see cref="CursorApprovalMode.AutoReview"/> only.</summary>
    internal const string AutoReviewFlag = "--auto-review";

    /// <summary>Cursor's documented short alias of <see cref="ForceFlag"/> (<c>agent --help</c>: "-f, --force"); never emitted, refused in <c>extraArgs</c> (GR2082).</summary>
    internal const string ForceShortFlag = "-f";

    /// <summary>The approval flags <c>approvalMode</c> owns, which GR2082 keeps out of <c>extraArgs</c>. Every one but <see cref="AutoReviewFlag"/> means <c>force</c>.</summary>
    internal static readonly IReadOnlyList<string> ApprovalFlags = [ForceFlag, ForceShortFlag, YoloFlag, AutoReviewFlag];

    private readonly ProcessRunner _processRunner;
    private readonly string _command;
    private readonly CursorApprovalMode _approvalMode;
    private readonly Func<string, string?> _resolveCommand;
    private readonly StreamJsonCliDialect _dialect;

    /// <param name="name">The <c>promptRunners</c> block name.</param>
    /// <param name="command">The block's <c>command</c> (default <see cref="DefaultCommand"/>).</param>
    /// <param name="processRunner">The shared process spawner.</param>
    /// <param name="approvalMode">The block's <c>approvalMode</c> (#767); absent in config ⇒ <see cref="CursorApprovalModes.Default"/>.</param>
    /// <param name="resolveCommand">
    /// Resolves <paramref name="command"/> to the file to launch; null (production) uses
    /// <see cref="ResolveLaunchPath"/> against the process <c>PATH</c>. A seam so a test can point it at a
    /// fixture directory without mutating process-wide environment state.
    /// </param>
    public CursorPromptRunner(
        string name,
        string command,
        ProcessRunner processRunner,
        CursorApprovalMode approvalMode = CursorApprovalModes.Default,
        Func<string, string?>? resolveCommand = null)
    {
        Name = name;
        _command = command;
        _processRunner = processRunner;
        _approvalMode = approvalMode;
        _resolveCommand = resolveCommand
            ?? (c => ResolveLaunchPath(c, Environment.GetEnvironmentVariable("PATH")));
        _dialect = new StreamJsonCliDialect
        {
            Label = "cursor",
            ConfigurationRefusal = text => CursorSignalClassifier.IsRunEverythingDisabled(text)
                ? RunEverythingDisabledRemedy(name)
                : null
        };
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>The approval mode this runner launches Cursor with (#767) — what the registry read off the block.</summary>
    internal CursorApprovalMode ApprovalMode => _approvalMode;

    /// <inheritdoc />
    public async Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
    {
        // THE ROLE GATE, first — before anything launches. The set consulted is the BUILD FACT
        // PromptRunnerKinds.ServesRoles, so the declared capability and the refusal cannot drift (the same
        // shape OpenAiCompatPromptRunner uses). SchedulerFactory already keeps a cursor block out of every
        // Advisory profile; reaching this means that resolution was bypassed.
        if (!PromptRunnerKinds.ServesRoles(PromptRunnerKind.Cursor).Contains(invocation.Role))
        {
            return new PromptResult
            {
                Completed = false,
                IsError = true,
                FailureKind = PromptFailureKind.Error,
                Summary =
                    $"cursor refused an invocation in the {invocation.Role} role: runner '{Name}' serves " +
                    $"{string.Join(" and ", PromptRunnerKinds.ServesRoles(PromptRunnerKind.Cursor).Order())} only. " +
                    "An advisory prompt is read-only by construction and Cursor runs with no per-tool allowlist " +
                    "(write and shell access per its approvalMode), so it is never run on a cursor block (SSOT §9.9)."
            };
        }

        // The Claude containment hook is a Claude Code settings file. PromptRunnerKinds.NeedsContainmentHook
        // is false for cursor, so the splice never adds it; arriving here means the splice and that build
        // fact disagree — a harness bug. Throw rather than pass Cursor a flag it would reject at parse time
        // (or, worse, drop it and leave a boundary that silently does not apply) — the same backstop
        // OpenAiCompatPromptRunner keeps.
        if (invocation.Settings.ExtraArgs.Contains("--settings", StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Prompt runner '{Name}' (cursor) was handed the Claude worktree-containment '--settings' " +
                "flag. Cursor's Agent CLI cannot load a Claude Code PreToolUse hook, so honouring the flag is " +
                "impossible and dropping it silently would leave a boundary that does not apply (SSOT §9.9). " +
                "The containment splice and PromptRunnerKinds.NeedsContainmentHook disagree — that is a " +
                "harness bug, not a configuration one.");
        }

        PromptDelivery delivery = Deliver(invocation);
        var command = new ResolvedCommand
        {
            // Unresolvable ⇒ launch the command as written, so the shared launch-failure path classifies the
            // Win32Exception and names the command, exactly as before resolution existed.
            Executable = _resolveCommand(_command) ?? _command,
            Arguments = BuildArguments(invocation, _approvalMode)
        };

        var echo = new PromptEchoCheck(delivery.StandardInput);
        var refusals = new CursorToolCallScanner();
        PromptResult result = await StreamJsonCliSession.RunAsync(
            _processRunner,
            command,
            BuildEnvironment(invocation),
            delivery.StandardInput,
            invocation,
            _dialect,
            refusals,
            cancellationToken,
            echo.Feed).ConfigureAwait(false);

        return Finish(result, echo, refusals, _approvalMode, invocation.Settings.ExtraArgs, invocation.Role);
    }

    /// <summary>The most refusals a summary names one by one; the rest are counted (feedback.md lists them all).</summary>
    internal const int SummaryRefusalLimit = 5;

    /// <summary>
    /// Fold the Cursor-specific facts onto the shared session's result, in this order:
    /// <list type="number">
    /// <item>the display-name model moves from <see cref="PromptResult.ObservedModel"/> into the summary;</item>
    /// <item>every refused tool call (#773) is carried as <see cref="PromptResult.RefusedToolCalls"/> and named in
    /// the summary, whatever the verdict — a refusal is never silent;</item>
    /// <item>a run that already FAILED keeps its own, more specific failure (bad <c>--model</c>, the
    /// Run-Everything refusal, a stall, a timeout, the #452 abort) — calls it was still running are carried as
    /// <see cref="PromptResult.InFlightToolCalls"/> and named as such (never as refusals), and never change its
    /// failure kind (#778);</item>
    /// <item>the prompt-echo verdict (#764 — the guard against the false green);</item>
    /// <item><b>the #773 rule, outcome-aware:</b> a session that attempted shell and had EVERY shell call refused
    /// could run no build, no test and no git, however its terminal result reads, so it is marked
    /// (for an ACTION only explicit <c>rejected</c> shell calls count — an ABANDONED call, #778, is named as a
    /// refusal but neither reaches the action's wall lists nor makes the session every-shell-refused, since a lone abandoned <c>git commit</c> under
    /// auto-review must not turn an ordinary guardrail failure into a no-retry halt; for a JUDGE abandoned calls
    /// count too, so it still fails closed)
    /// <see cref="PromptResult.AllShellRefused"/> with a <see cref="PromptResult.RunnerConfigurationRemedy"/>
    /// (each refused command, its reason, the per-mode <c>approvalMode</c> advice). For an ACTION the run still
    /// COMPLETES: its edits may be right, so the task's guardrails decide (TaskExecutor's WEAK-4 rule — never halt
    /// what the gates could finish). Guardrails pass ⇒ green, with the refusals named; guardrails fail ⇒ the
    /// harness settles needs-human at once with the remedy, because the next attempt runs under the same approval
    /// policy. For a JUDGE (<see cref="PromptRole.Guardrail"/>) it FAILS CLOSED —
    /// <see cref="PromptFailureKind.RunnerConfiguration"/>, not completed — since a verifier that could run none
    /// of its checks certifies nothing, whatever verdict it wrote. A session with SOME refusals and at least one
    /// shell call that ran completes, carrying its refusals — exactly as a Claude attempt that routed around a
    /// denial is judged on its outcome (#534 / #708), and the refused targets still reach the #86
    /// repeated-refusal tracker.</item>
    /// </list>
    /// </summary>
    internal static PromptResult Finish(
        PromptResult result,
        PromptEchoCheck echo,
        CursorToolCallScanner refusals,
        CursorApprovalMode approvalMode,
        IReadOnlyList<string> extraArgs,
        PromptRole role = PromptRole.Action)
    {
        // The stream is over (#778). With a terminal result, calls still open were abandoned — refusals; without
        // one the run already failed on its own, and its open calls were merely cut off (InFlightCalls).
        refusals.EndOfStream();

        string? displayModel = result.ObservedModel;
        string modelNote = displayModel is { Length: > 0 } ? $" (Cursor reported model: {displayModel})" : string.Empty;
        string refusalNote = refusals.Refusals.Count == 0 ? string.Empty : $"; {DescribeRefusals(refusals.Refusals)}";
        // Wall targets are role-aware (#778): an ACTION's lists carry explicit rejections only (the shared session
        // already took them from the scanner), so an abandoned `git commit` repeated on every attempt never reads as a
        // repeated permission wall; a JUDGE's also carry its abandoned targets.
        bool isJudge = role == PromptRole.Guardrail;
        result = result with
        {
            ObservedModel = null,
            RefusedToolCalls = refusals.Refusals,
            InFlightToolCalls = refusals.InFlightCalls,
            BlockedWritePaths = isJudge
                ? Union(refusals.BlockedWritePaths, refusals.AbandonedBlockedWritePaths)
                : refusals.BlockedWritePaths,
            RefusedCommands = isJudge
                ? Union(refusals.RefusedCommands, refusals.AbandonedCommands)
                : refusals.RefusedCommands
        };

        if (!result.Completed)
        {
            string inFlightNote = refusals.InFlightCalls.Count == 0
                ? string.Empty
                : $"; {refusals.InFlightCalls.Count} tool call(s) still running when the session was stopped (not refused): " +
                  string.Join("; ", refusals.InFlightCalls.Take(SummaryRefusalLimit));
            return result with { Summary = result.Summary + refusalNote + inFlightNote + modelNote };
        }

        if (echo.Verdict is { } undelivered)
        {
            return result with
            {
                Completed = false,
                IsError = true,
                FailureKind = PromptFailureKind.Error,
                Summary = $"cursor did not receive the composed prompt ({undelivered}){refusalNote}{modelNote}"
            };
        }

        if (isJudge ? refusals.EveryShellCallRefusedOrAbandoned : refusals.EveryShellCallRefused)
        {
            int refusedShell = refusals.ShellCallsRefused + (isJudge ? refusals.ShellCallsAbandoned : 0);
            string remedy =
                $"EVERY shell command it attempted was refused ({refusedShell} shell call(s), none ran); " +
                $"{DescribeRefusals(refusals.Refusals)} — {EveryShellRefusedRemedy(approvalMode, extraArgs)}";

            // A JUDGE fails closed: a verifier that could run none of its checks certifies nothing, whatever
            // verdict file it wrote (the PR #775 B1 false green).
            if (isJudge)
            {
                return result with
                {
                    Completed = false,
                    IsError = true,
                    FailureKind = PromptFailureKind.RunnerConfiguration,
                    AllShellRefused = true,
                    RunnerConfigurationRemedy = remedy,
                    Summary = $"cursor reported success, but {remedy}{modelNote}"
                };
            }

            // An ACTION completes, carrying the fact: its work may still be right, and the task's guardrails —
            // not the refusals — decide (outcome-aware, TaskExecutor's WEAK-4 rule). If they fail, the harness
            // settles needs-human at once with this remedy, since a retry runs under the same policy.
            return result with
            {
                AllShellRefused = true,
                RunnerConfigurationRemedy = remedy,
                Summary = $"cursor completed, but {remedy}{modelNote}"
            };
        }

        return result with { Summary = result.Summary + refusalNote + modelNote };
    }

    private static IReadOnlyList<string> Union(IReadOnlyList<string> first, IReadOnlyList<string> second) =>
        second.Count == 0 ? first : first.Concat(second).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// The refusals, named: <c>2 tool call(s) refused by Cursor: shell `git status` — refused by …; …</c>, the
    /// first <see cref="SummaryRefusalLimit"/> in full and the rest counted.
    /// </summary>
    internal static string DescribeRefusals(IReadOnlyList<ToolRefusal> refusals)
    {
        string named = string.Join("; ", refusals.Take(SummaryRefusalLimit));
        string more = refusals.Count > SummaryRefusalLimit ? $"; and {refusals.Count - SummaryRefusalLimit} more" : string.Empty;
        return $"{refusals.Count} tool call(s) refused by Cursor: {named}{more}";
    }

    /// <summary>
    /// The remedy when every shell call was refused, per approval mode — the operator's next move, since the
    /// agent cannot change the approval policy it runs under.
    /// </summary>
    private static string EveryShellRefusedRemedy(CursorApprovalMode mode, IReadOnlyList<string> extraArgs) => mode switch
    {
        CursorApprovalMode.None when !EnablesSandbox(extraArgs) =>
            "approvalMode \"none\" without Cursor's sandbox refuses every shell command. Add \"extraArgs\": " +
            "[\"--sandbox\", \"enabled\"] or set \"approvalMode\": \"auto-review\" on this cursor block, then resume " +
            "(SSOT §9.9).",
        CursorApprovalMode.None =>
            "Cursor's sandbox did not run them. Check the reasons above and your Cursor sandbox settings, or set " +
            "\"approvalMode\": \"auto-review\" on this cursor block, then resume (SSOT §9.9).",
        CursorApprovalMode.AutoReview =>
            "Cursor's auto-review classifier (or a hook Cursor loaded — see the reasons above) refused every one. " +
            "Change the task so it needs no refused command, add Cursor's sandbox (\"extraArgs\": [\"--sandbox\", " +
            "\"enabled\"]), or run it on a runner whose policy permits them, then resume (SSOT §9.9).",
        _ =>
            "approvalMode \"force\" should run every command, so something Cursor loaded refused them — a reason " +
            "naming a hook points at one (Cursor's \"Include Third-Party Configs\" imports ~/.claude hooks). Fix or " +
            "disable it, then resume (SSOT §9.9)."
    };

    /// <summary>The remedy appended to a launch Cursor refused because the team disabled Run Everything (#767).</summary>
    private static string RunEverythingDisabledRemedy(string blockName) =>
        "your Cursor team administrator has disabled 'Run Everything', which approvalMode \"force\" (--force) " +
        $"needs, so no retry can succeed. Set \"approvalMode\": \"auto-review\" on promptRunners.{blockName} (or " +
        "\"none\" with \"extraArgs\": [\"--sandbox\", \"enabled\"]) and resume (SSOT §9.9).";

    /// <summary>
    /// The approval flags in <paramref name="args"/>, in order — each spelled as the canonical flag, whether written
    /// bare or as <c>--flag=value</c>. Read by GR2082.
    /// </summary>
    internal static IEnumerable<string> ApprovalFlagsIn(IEnumerable<string> args)
    {
        foreach (string arg in args)
        {
            string trimmed = arg.Trim();
            foreach (string flag in ApprovalFlags)
            {
                if (string.Equals(trimmed, flag, StringComparison.Ordinal) ||
                    trimmed.StartsWith(flag + "=", StringComparison.Ordinal))
                {
                    yield return flag;
                }
            }
        }
    }

    /// <summary>
    /// True when <paramref name="args"/> turn Cursor's sandbox on: <c>--sandbox enabled</c> or
    /// <c>--sandbox=enabled</c>. Read by GR2080's per-mode sentence and the every-shell-refused remedy.
    /// </summary>
    internal static bool EnablesSandbox(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i].Trim();
            if (string.Equals(arg, "--sandbox=enabled", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(arg, "--sandbox", StringComparison.Ordinal) &&
                 i + 1 < args.Count &&
                 string.Equals(args[i + 1].Trim(), "enabled", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How the composed prompt reaches Cursor — the ONE place that choice is made: on STDIN, with NO
    /// positional prompt.
    ///
    /// <para><b>Measured, not assumed (#764 live probe, <c>agent</c> 2026.09.18).</b> Cursor reads stdin as
    /// the prompt ONLY when there is no positional argument at all (its <c>build-prompt.ts</c>); with a
    /// positional present stdin is silently ignored, and the session still ends <c>result/success</c> with exit
    /// 0 — a false green. Stdin with no positional delivered a 57,226-character prompt intact, quotes,
    /// <c>&amp;</c>, <c>%</c> and <c>!</c> included. A positional carrying the whole prompt is not an
    /// alternative: it routinely exceeds the Windows command-line limit (8191 characters through a
    /// <c>.cmd</c> shim, 32767 for <c>CreateProcess</c>).</para>
    ///
    /// <para><b>Why this is verified at run time anyway.</b> A bare token in the block's <c>extraArgs</c>
    /// (<c>"extraArgs": ["summarize"]</c>) IS a positional prompt, and would silently re-create the false
    /// green. It is not refused statically — <c>"--sandbox", "enabled"</c> is legitimately a flag followed by a
    /// value, and telling the two apart would mean re-implementing Cursor's option parser. Instead
    /// <see cref="PromptEchoCheck"/> checks that the prompt the agent reports receiving IS this one.</para>
    /// </summary>
    internal static PromptDelivery Deliver(PromptInvocation invocation) => new(invocation.ComposedPrompt);

    /// <summary>The prompt's delivery: the stdin text. There is deliberately no positional channel.</summary>
    internal readonly record struct PromptDelivery(string StandardInput);

    /// <summary>
    /// Build the <c>agent</c> argument list (SSOT §9.9). All Cursor flag spelling lives here. The approval flag
    /// follows <paramref name="approvalMode"/> (#767): <c>--force</c>, <c>--auto-review</c>, or none at all — never
    /// both, which the CLI refuses. <c>--trust</c> (trust the workspace without prompting) is always passed.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(
        PromptInvocation invocation, CursorApprovalMode approvalMode = CursorApprovalModes.Default)
    {
        PromptRunnerSettings settings = invocation.Settings;

        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json"
        };

        switch (approvalMode)
        {
            case CursorApprovalMode.Force:
                args.Add(ForceFlag);
                break;
            case CursorApprovalMode.AutoReview:
                args.Add(AutoReviewFlag);
                break;
            case CursorApprovalMode.None:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(approvalMode), approvalMode, "Unhandled cursor approval mode.");
        }

        args.Add("--trust");

        // Skipped only for an EMPTY working directory — the advisory criticality assessment's shape
        // (CriticalityJudge.BuildInvocation, issue #381), where there is no workspace to name and an empty
        // flag value is a parse error waiting to happen. Every task/guardrail invocation names one.
        if (!string.IsNullOrWhiteSpace(invocation.WorkingDirectory))
        {
            args.Add("--workspace");
            args.Add(invocation.WorkingDirectory);
        }

        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            args.Add("--model");
            args.Add(settings.Model);
        }

        // The plan directory, for the same reason Claude gets it: the prompt references files there
        // (prompt bodies, dependency transcripts). Skipped when empty, as --workspace above.
        if (!string.IsNullOrWhiteSpace(invocation.PlanDirectory))
        {
            args.Add("--add-dir");
            args.Add(invocation.PlanDirectory);
        }

        // Appended verbatim. On Windows the launch goes through Cursor's agent.cmd shim, whose
        // `setlocal enabledelayedexpansion` EATS a literal `!` in an argument — keep `!` out of extraArgs.
        // (The composed prompt is unaffected: it travels on stdin, never through the shim's argument line.)
        args.AddRange(settings.ExtraArgs);

        return args;
    }

    /// <summary>
    /// The file to launch for <paramref name="command"/>. On Windows the command is resolved through
    /// <c>PATH</c> + <c>PATHEXT</c> to a FULL path by <see cref="PathExecutableProbe.ResolveFullPath"/> — the
    /// same rule GR2009's PATH probe applies — because Cursor installs as
    /// <c>%LOCALAPPDATA%\cursor-agent\agent.cmd</c> (a shim running <c>agent.ps1</c>, which runs
    /// <c>node.exe</c>): there is no <c>agent.exe</c>, and <c>Process.Start</c> with
    /// <c>UseShellExecute = false</c> does not apply PATHEXT to a bare name, so <c>agent</c> would throw
    /// <see cref="System.ComponentModel.Win32Exception"/>. Elsewhere the command is launched as written
    /// (the OS already searches PATH). Null when nothing resolves; the caller then launches the command as
    /// written and the existing launch-failure path reports it.
    /// </summary>
    internal static string? ResolveLaunchPath(string command, string? pathVariable) =>
        OperatingSystem.IsWindows() ? PathExecutableProbe.ResolveFullPath(command, pathVariable) : command;

    /// <summary>
    /// The child environment: the harness <c>GUARDRAILS_*</c> set overlaid with the user's <c>env</c>
    /// passthrough (which wins). Unlike Claude there is no output-token cap variable — Cursor exposes none,
    /// so <c>maxOutputTokens</c> is not translated into anything.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> BuildEnvironment(PromptInvocation invocation)
    {
        var env = new Dictionary<string, string>(invocation.Environment, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in invocation.Settings.Env)
        {
            env[entry.Key] = entry.Value;
        }

        return env;
    }

    /// <summary>
    /// The delivery check (#764): Cursor echoes the prompt it received as the stream's first
    /// <c>{"type":"user","message":{"content":[{"type":"text","text":…}]}}</c> event. A run whose echo is
    /// missing, or is not the composed prompt, never saw its task — whatever its terminal result says.
    ///
    /// <para><b>A strong PREFIX match, not equality.</b> Both sides are line-ending-normalized and trimmed, and
    /// the first <see cref="EchoPrefixChars"/> characters of the composed prompt must open the echo. Live, the
    /// echo of a 57,226-character prompt came back one character shorter (trailing whitespace), so byte
    /// equality would false-fail on a normalization the harness does not control; and the opening of the
    /// prompt is exactly what distinguishes "this task" from what a false green actually echoes — a short
    /// pointer sentence or a stray positional token, neither of which can carry the prompt's first 4 KB.</para>
    ///
    /// <para>Called on the stdout reader thread only (the session serializes lines), and read after the
    /// process has returned, so it needs no locking.</para>
    /// </summary>
    internal sealed class PromptEchoCheck
    {
        private readonly string _expectedPrefix;
        private bool _sawUser;
        private string? _echo;

        public PromptEchoCheck(string composedPrompt)
        {
            string expected = Normalize(composedPrompt);
            _expectedPrefix = expected.Length > EchoPrefixChars ? expected[..EchoPrefixChars] : expected;
        }

        public void Feed(string line)
        {
            if (_sawUser || string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                return;
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("type", out JsonElement type) ||
                    type.ValueKind != JsonValueKind.String ||
                    type.GetString() != "user")
                {
                    return;
                }

                _sawUser = true;
                _echo = UserText(root);
            }
        }

        /// <summary>Null when the composed prompt was delivered; otherwise the reason it was not.</summary>
        public string? Verdict
        {
            get
            {
                if (_expectedPrefix.Length == 0)
                {
                    return null; // nothing to compare: an empty prompt proves nothing either way
                }

                if (!_sawUser)
                {
                    return "the stream carried no user-prompt echo, so nothing shows the agent was given its task";
                }

                return _echo is { } echo && Normalize(echo).StartsWith(_expectedPrefix, StringComparison.Ordinal)
                    ? null
                    : "its prompt echo is not the composed prompt — stdin was not read; is there a positional " +
                      "argument in extraArgs? Cursor ignores stdin whenever one is present";
            }
        }

        private static string? UserText(JsonElement root)
        {
            if (!root.TryGetProperty("message", out JsonElement message) ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("content", out JsonElement content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var text = new StringBuilder();
            foreach (JsonElement block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object &&
                    block.TryGetProperty("text", out JsonElement t) &&
                    t.ValueKind == JsonValueKind.String)
                {
                    text.Append(t.GetString());
                }
            }

            return text.ToString();
        }

        private static string Normalize(string text) =>
            text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }
}
