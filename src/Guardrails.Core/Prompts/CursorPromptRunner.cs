using System.Text;
using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Prompts;

/// <summary>
/// The <c>kind: "cursor"</c> prompt runner (#764): Cursor's Agent CLI (<c>agent</c>) headless. ALL
/// Cursor-specific flag spelling is confined to this class (SSOT §9.9). Invocation:
/// <code>
/// agent -p --output-format stream-json --force --trust --workspace &lt;cwd&gt;
///   [--model &lt;m&gt;] --add-dir &lt;planDir&gt; [extraArgs…]
/// </code>
/// with the composed prompt on STDIN and NO positional prompt (see <see cref="Deliver"/>), and cwd =
/// workspace.
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
/// <c>--force</c> and <c>--trust</c>: live, a session without <c>--force</c> still writes files but has every
/// shell command rejected. The Claude worktree-containment hook (<c>--settings</c>, SSOT §9.4) is a Claude
/// Code PreToolUse hook Cursor cannot load, so <see cref="PromptRunnerKinds.NeedsContainmentHook"/> is false
/// for this kind and the flag is REFUSED here if it ever arrives. Cursor does not serve the
/// <see cref="PromptRole.Advisory"/> role (<see cref="PromptRunnerKinds.ServesRoles"/>): the overwatcher,
/// ai-triage and the criticality judge are read-only by construction and must never run under
/// <c>--force</c>, so an Advisory invocation is refused before anything launches.</para>
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
/// <para><b>No permission scanner.</b> <see cref="ClaudePermissionScanner"/> reads Claude's
/// <c>tool_result</c> denial phrasing, which Cursor never emits, so it is not fed:
/// <see cref="PromptResult.BlockedWritePaths"/> stays empty and
/// <see cref="PromptInvocation.AbortAfterConsecutiveToolDenials"/> is inert for this runner.</para>
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

    private readonly ProcessRunner _processRunner;
    private readonly string _command;
    private readonly Func<string, string?> _resolveCommand;

    /// <param name="name">The <c>promptRunners</c> block name.</param>
    /// <param name="command">The block's <c>command</c> (default <see cref="DefaultCommand"/>).</param>
    /// <param name="processRunner">The shared process spawner.</param>
    /// <param name="resolveCommand">
    /// Resolves <paramref name="command"/> to the file to launch; null (production) uses
    /// <see cref="ResolveLaunchPath"/> against the process <c>PATH</c>. A seam so a test can point it at a
    /// fixture directory without mutating process-wide environment state.
    /// </param>
    public CursorPromptRunner(
        string name, string command, ProcessRunner processRunner, Func<string, string?>? resolveCommand = null)
    {
        Name = name;
        _command = command;
        _processRunner = processRunner;
        _resolveCommand = resolveCommand
            ?? (c => ResolveLaunchPath(c, Environment.GetEnvironmentVariable("PATH")));
    }

    /// <inheritdoc />
    public string Name { get; }

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
                    "An advisory prompt is read-only by construction and Cursor runs with --force (full write " +
                    "and shell access), so it is never run on a cursor block (SSOT §9.9)."
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
            Arguments = BuildArguments(invocation)
        };

        var echo = new PromptEchoCheck(delivery.StandardInput);
        PromptResult result = await StreamJsonCliSession.RunAsync(
            _processRunner,
            command,
            BuildEnvironment(invocation),
            delivery.StandardInput,
            invocation,
            Dialect,
            cancellationToken,
            echo.Feed).ConfigureAwait(false);

        return Finish(result, echo);
    }

    /// <summary>
    /// Fold the two Cursor-specific facts onto the shared session's result: the prompt-echo verdict (#764 —
    /// the guard against the false green) and the display-name model, which is moved from
    /// <see cref="PromptResult.ObservedModel"/> into the summary.
    /// </summary>
    private static PromptResult Finish(PromptResult result, PromptEchoCheck echo)
    {
        string? displayModel = result.ObservedModel;
        string modelNote = displayModel is { Length: > 0 } ? $" (Cursor reported model: {displayModel})" : string.Empty;
        result = result with { ObservedModel = null };

        // Only a run that would otherwise COMPLETE is re-judged. A run that already failed (bad --model: exit 1
        // and no stream at all; a stall; a timeout) keeps its own, more specific failure.
        if (result.Completed && echo.Verdict is { } undelivered)
        {
            return result with
            {
                Completed = false,
                IsError = true,
                FailureKind = PromptFailureKind.Error,
                Summary = $"cursor did not receive the composed prompt ({undelivered}){modelNote}"
            };
        }

        return result with { Summary = result.Summary + modelNote };
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

    /// <summary>Build the <c>agent</c> argument list (SSOT §9.9). All Cursor flag spelling lives here.</summary>
    internal static IReadOnlyList<string> BuildArguments(PromptInvocation invocation)
    {
        PromptRunnerSettings settings = invocation.Settings;

        var args = new List<string>
        {
            "-p",
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

    private static readonly StreamJsonCliDialect Dialect = new()
    {
        Label = "cursor",
        ScansPermissionDenials = false
    };

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
