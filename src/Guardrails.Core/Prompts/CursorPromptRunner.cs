using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Prompts;

/// <summary>
/// The <c>kind: "cursor"</c> prompt runner (#764): Cursor's Agent CLI (<c>agent</c>) headless. ALL
/// Cursor-specific flag spelling is confined to this class (SSOT §9.9). Invocation:
/// <code>
/// agent -p &lt;positional pointer&gt; --output-format stream-json --force --trust --workspace &lt;cwd&gt;
///   [--model &lt;m&gt;] --add-dir &lt;planDir&gt; [extraArgs…]
/// </code>
/// with the composed prompt on STDIN (see <see cref="Deliver"/>) and cwd = workspace.
///
/// <para><b>What is deliberately NOT emitted.</b> Cursor rejects <c>--verbose</c>, <c>--permission-mode</c>,
/// <c>--max-turns</c> and <c>--allowedTools</c> as unknown options and exits at parse time, before any model
/// call — that is the failure #764 reported. <c>permissionMode</c>, <c>allowedTools</c>, <c>maxTurns</c> and
/// <c>maxOutputTokens</c> therefore have no Cursor spelling and are IGNORED for a cursor block (a validate
/// warning, GR2080, names every one a block declares). The bounds that DO apply are
/// <see cref="PromptInvocation.Timeout"/> and <see cref="PromptInvocation.StallBound"/>, both enforced by the
/// shared <see cref="StreamJsonCliSession"/>.</para>
///
/// <para><b>Permissions are full, and said so.</b> Print mode has no per-tool allowlist, so the runner passes
/// <c>--force</c> (allow commands unless explicitly denied) and <c>--trust</c> (trust the workspace without a
/// prompt): without them a headless session cannot act at all. Write scope is enforced only after the fact by
/// the harness's git-diff checks. The Claude worktree-containment hook (<c>--settings</c>, SSOT §9.4) is a
/// Claude Code PreToolUse hook Cursor cannot load, so <see cref="PromptRunnerKinds.NeedsContainmentHook"/> is
/// false for this kind and the flag is REFUSED here if it ever arrives.</para>
///
/// <para><b>The stream is parsed by <see cref="ClaudeStreamParser"/>, unforked.</b> Cursor's
/// <c>stream-json</c> matches Claude's envelope at the two points the parser reads — the opening
/// <c>system/init</c> (its <c>model</c> is the observed-model echo) and the terminal <c>result</c>
/// (<c>is_error</c>, <c>result</c>) — and the parser skips every other event type, including Cursor's
/// <c>tool_call</c>. Cursor's result carries no <c>total_cost_usd</c>, <c>num_turns</c> or <c>usage</c>, so
/// cost, turns and usage stay NULL (absent), never zero.</para>
///
/// <para><b>No permission scanner.</b> <see cref="ClaudePermissionScanner"/> reads Claude's
/// <c>tool_result</c> denial phrasing, which Cursor never emits (and with <c>--force</c> it has no tool
/// denials to report), so it is not fed: <see cref="PromptResult.BlockedWritePaths"/> stays empty and
/// <see cref="PromptInvocation.AbortAfterConsecutiveToolDenials"/> is inert for this runner.</para>
/// </summary>
public sealed class CursorPromptRunner : IPromptRunner
{
    /// <summary>
    /// The <c>command</c> a cursor block defaults to when it declares none — Cursor's Agent CLI binary.
    /// </summary>
    public const string DefaultCommand = "agent";

    /// <summary>
    /// The fixed positional prompt: a POINTER, not the task. Cursor documents the prompt as a positional
    /// argument; the composed prompt routinely exceeds the Windows command-line limit (8191 characters
    /// through a <c>.cmd</c> shim, 32767 for <c>CreateProcess</c>), so it travels on stdin and this tells the
    /// agent where to find it.
    /// </summary>
    internal const string StdinPointerPrompt =
        "Your complete task instructions are provided on standard input. Read all of standard input and " +
        "follow those instructions exactly; they are the whole of your task.";

    /// <summary>
    /// The four Claude flags Cursor rejects as unknown options (exit at parse time). Pinned by a test so no
    /// future edit can reintroduce one into the argv.
    /// </summary>
    internal static readonly IReadOnlyList<string> RejectedClaudeFlags =
        ["--verbose", "--permission-mode", "--max-turns", "--allowedTools"];

    private readonly ProcessRunner _processRunner;
    private readonly string _command;

    public CursorPromptRunner(string name, string command, ProcessRunner processRunner)
    {
        Name = name;
        _command = command;
        _processRunner = processRunner;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
    {
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
            Executable = _command,
            Arguments = BuildArguments(invocation, delivery)
        };

        return StreamJsonCliSession.RunAsync(
            _processRunner,
            command,
            BuildEnvironment(invocation),
            delivery.StandardInput,
            invocation,
            Dialect,
            cancellationToken);
    }

    /// <summary>
    /// How the composed prompt reaches Cursor — the ONE place that choice is made, so it can be swapped
    /// without touching anything else. Today: the composed prompt on STDIN (as Claude receives it) plus the
    /// short fixed <see cref="StdinPointerPrompt"/> as the positional prompt.
    ///
    /// <para><b>Unproven against a live binary.</b> Cursor documents the positional prompt; third-party guides
    /// show piped stdin being read as context alongside one (<c>git diff | agent -p "Summarize"</c>), but no
    /// Cursor document promises it. If a live dogfood shows stdin is ignored, the fallback is to return the
    /// composed prompt as <see cref="PromptDelivery.Positional"/> with no stdin — safe only while the prompt
    /// stays under the platform command-line limit, which is why it is not the default.</para>
    /// </summary>
    internal static PromptDelivery Deliver(PromptInvocation invocation) =>
        new(Positional: StdinPointerPrompt, StandardInput: invocation.ComposedPrompt);

    /// <summary>The prompt's two channels: the positional argument, and the stdin text (null = no stdin).</summary>
    internal readonly record struct PromptDelivery(string Positional, string? StandardInput);

    /// <summary>Build the <c>agent</c> argument list (SSOT §9.9). All Cursor flag spelling lives here.</summary>
    internal static IReadOnlyList<string> BuildArguments(PromptInvocation invocation) =>
        BuildArguments(invocation, Deliver(invocation));

    private static IReadOnlyList<string> BuildArguments(PromptInvocation invocation, PromptDelivery delivery)
    {
        PromptRunnerSettings settings = invocation.Settings;

        // The positional sits directly after -p, Cursor's documented `agent -p "<prompt>" …` form.
        var args = new List<string>
        {
            "-p",
            delivery.Positional,
            "--output-format", "stream-json",
            "--force",
            "--trust"
        };

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

        args.AddRange(settings.ExtraArgs);

        return args;
    }

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
    /// Cursor's quota wording that the shared classifier does not know. <c>You've hit your individual spend
    /// limit</c> is the text #764 observed; it is a provider refusal the operator cannot fix by re-running,
    /// so it is <see cref="PromptFailureKind.Transient"/> — the bounded provider-wait pause
    /// (<c>transientPauseBudgetSeconds</c>) that then halts, instead of three retries burned in seconds.
    /// Scoped to THIS runner so Claude's classification stays exactly as it was.
    /// </summary>
    private static readonly string[] QuotaPhrases = ["spend limit"];

    /// <summary>
    /// <see cref="ClaudeSignalClassifier.Classify"/>, widened by <see cref="QuotaPhrases"/> only where the
    /// shared classifier would otherwise call the text a plain <see cref="PromptFailureKind.Error"/>.
    /// </summary>
    internal static PromptFailureKind Classify(string? text)
    {
        PromptFailureKind shared = ClaudeSignalClassifier.Classify(text);
        if (shared != PromptFailureKind.Error || string.IsNullOrWhiteSpace(text))
        {
            return shared;
        }

        return QuotaPhrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase))
            ? PromptFailureKind.Transient
            : shared;
    }

    private static readonly StreamJsonCliDialect Dialect = new()
    {
        Label = "cursor",
        ScansPermissionDenials = false,
        Classify = Classify
    };
}
