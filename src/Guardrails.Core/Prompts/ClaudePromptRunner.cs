using System.Text;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Prompts;

/// <summary>
/// The v1 prompt runner: Claude Code headless (<c>claude -p</c>). ALL Claude-specific flag
/// spelling and stream parsing is confined to this class (SSOT §9). Invocation:
/// <code>
/// claude -p --output-format stream-json --verbose --permission-mode &lt;m&gt; --max-turns &lt;n&gt;
///   [--model &lt;m&gt;] [--allowedTools &lt;joined&gt;] --add-dir &lt;planDir&gt; [extraArgs…]
/// </code>
/// The composed prompt is delivered on STDIN; cwd = workspace; every raw stream line is
/// teed to <c>claude-stream.jsonl</c>. Semantic disposition: a non-zero exit OR no terminal
/// <c>result</c> message ⇒ <see cref="PromptResult.Completed"/> = false.
/// </summary>
public sealed class ClaudePromptRunner : IPromptRunner
{
    /// <summary>
    /// Pin the two persisted log artifacts to UTF-8 (no BOM) explicitly (issue #55). The
    /// no-arg <see cref="StreamWriter"/> overloads already default to this, but the symptom of
    /// #55 — mojibake — lived in exactly these files, so stating the encoding keeps a future edit
    /// from silently regressing them to a BOM/code-page default. Matches <see cref="State.AtomicFile"/>
    /// and <see cref="ProcessRunner"/>'s decode.
    /// </summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly ProcessRunner _processRunner;
    private readonly string _command;

    public ClaudePromptRunner(string name, string command, ProcessRunner processRunner)
    {
        Name = name;
        _command = command;
        _processRunner = processRunner;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
    {
        var command = new ResolvedCommand
        {
            Executable = _command,
            Arguments = BuildArguments(invocation)
        };

        var parser = new ClaudeStreamParser();

        // Mine permission-wall signals from the same stream lines (issues #86 / #104): a write/edit
        // refused because the path is not granted. The scanner is fed in the Tee alongside the parser
        // and transcript; its output flows out as the runner-agnostic BlockedWritePaths list.
        var permissionScanner = new ClaudePermissionScanner.Scanner();

        // Open both log artifacts for incremental writes before launching the process so the
        // "view log" link can tail them in real time (issue #41) — both claude-stream.jsonl (the
        // raw debug stream) and transcript.md (the human/dependent-task view, issues #26/#27) grow
        // live, instead of appearing only when the task finishes. OutputDataReceived events are
        // serialized by AsyncStreamReader, so the shared writers/parser need no locking.
        //
        // DELIBERATE TRADEOFF: master wrote both artifacts via AtomicFile.WriteAllText (temp+move)
        // once the process exited; this streams them in place so a "view log" tail sees them grow
        // live (issue #41). Dropping atomicity is acceptable for these two append-only log artifacts
        // because nothing hashes or guardrail-gates them: the verdict never comes from these files —
        // it comes from the parsed `result` line + exit code (see `completed` below).
        Directory.CreateDirectory(Path.GetDirectoryName(invocation.StreamLogPath)!);
        await using var streamWriter = new StreamWriter(invocation.StreamLogPath, append: false, Utf8NoBom) { AutoFlush = true };

        // transcript.md is rendered incrementally from the same lines via StreamingWriter, which
        // parses each line independently and is byte-identical to a batch Render at Complete().
        // StreamingWriter flushes itself, so this writer needs no AutoFlush.
        StreamWriter? transcriptFile = invocation.TranscriptLogPath is { } transcriptPath
            ? new StreamWriter(transcriptPath, append: false, Utf8NoBom)
            : null;
        ClaudeTranscriptRenderer.StreamingWriter? transcript =
            transcriptFile is null ? null : new ClaudeTranscriptRenderer.StreamingWriter(transcriptFile);

        try
        {
            void Tee(string line)
            {
                parser.Feed(line);
                permissionScanner.Feed(line);
                streamWriter.WriteLine(line);
                transcript?.Feed(line);
            }

            ProcessResult process = await _processRunner.RunAsync(
                command,
                invocation.WorkingDirectory,
                BuildEnvironment(invocation),
                invocation.Timeout,
                standardInput: invocation.ComposedPrompt,
                stdoutLineSink: Tee,
                cancellationToken).ConfigureAwait(false);

            // Both files are fully written line-by-line above; Complete() finalizes the transcript's
            // trailing newline so it matches a batch render exactly.
            transcript?.Complete();

            ClaudeResult result = parser.Build();

            bool completed = process.Succeeded && result.HasResult;
            string summary = BuildSummary(process, result);
            PromptFailureKind failureKind = ClassifyFailure(process, result);
            string? resetHint = failureKind == PromptFailureKind.Transient
                ? ClaudeSignalClassifier.ExtractResetHint(ClassificationText(process, result))
                : null;

            return new PromptResult
            {
                Completed = completed,
                IsError = result.IsError,
                ResultText = result.ResultText,
                CostUsd = result.CostUsd,
                NumTurns = result.NumTurns,
                FailureKind = failureKind,
                ResetHint = resetHint,
                BlockedWritePaths = permissionScanner.BlockedWritePaths,
                Summary = summary
            };
        }
        finally
        {
            if (transcriptFile is not null)
            {
                await transcriptFile.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The Claude-specific env name for the output-token cap (issue #114). QUARANTINED here — this
    /// is the ONLY place in the codebase that knows the CLI's env-var spelling; the harness model
    /// carries only the abstract <c>maxOutputTokens</c> int (SSOT §9, never §5.1's GUARDRAILS_* set).
    /// </summary>
    internal const string MaxOutputTokensEnvVar = "CLAUDE_CODE_MAX_OUTPUT_TOKENS";

    /// <summary>
    /// The effective child environment: the harness <c>GUARDRAILS_*</c> set (<see cref="PromptInvocation.Environment"/>),
    /// overlaid with the Claude output-token cap (<see cref="MaxOutputTokensEnvVar"/>, issue #114), then
    /// the user's <c>env</c> passthrough (which wins last, so an explicit user value is authoritative).
    /// </summary>
    internal static IReadOnlyDictionary<string, string> BuildEnvironment(PromptInvocation invocation)
    {
        var env = new Dictionary<string, string>(invocation.Environment, StringComparer.Ordinal)
        {
            [MaxOutputTokensEnvVar] = invocation.Settings.MaxOutputTokens.ToString()
        };

        foreach (KeyValuePair<string, string> entry in invocation.Settings.Env)
        {
            env[entry.Key] = entry.Value;
        }

        return env;
    }

    /// <summary>Build the <c>claude</c> argument list (SSOT §9). All flag spelling lives here.</summary>
    internal static IReadOnlyList<string> BuildArguments(PromptInvocation invocation)
    {
        PromptRunnerSettings settings = invocation.Settings;
        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--permission-mode", settings.PermissionMode,
            "--max-turns", settings.MaxTurns.ToString()
        };

        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            args.Add("--model");
            args.Add(settings.Model);
        }

        if (settings.AllowedTools.Count > 0)
        {
            args.Add("--allowedTools");
            args.Add(string.Join(",", settings.AllowedTools));
        }

        args.Add("--add-dir");
        args.Add(invocation.PlanDirectory);

        args.AddRange(settings.ExtraArgs);

        return args;
    }

    /// <summary>
    /// Classify a non-success run into a runner-agnostic <see cref="PromptFailureKind"/> (SSOT §9).
    /// Precedence: a process timeout is <see cref="PromptFailureKind.Timeout"/>; otherwise the error
    /// TEXT — the terminal result's error message, or, when no terminal result was produced (the
    /// "instant rejection, no result line" case in #115), the captured stdout/stderr — is classified
    /// by <see cref="ClaudeSignalClassifier"/>. A clean success is <see cref="PromptFailureKind.None"/>.
    /// </summary>
    private static PromptFailureKind ClassifyFailure(ProcessResult process, ClaudeResult result)
    {
        if (process.TimedOut)
        {
            return PromptFailureKind.Timeout;
        }

        // Success = clean exit AND a terminal result that is not an error.
        if (process.Succeeded && result.HasResult && !result.IsError)
        {
            return PromptFailureKind.None;
        }

        PromptFailureKind classified = ClaudeSignalClassifier.Classify(ClassificationText(process, result));

        // A recognized transient/cap signal wins. Otherwise this is a genuine error — but if there was
        // no error text at all (e.g. a clean exit with no terminal result), still report Error so the
        // attempt fails rather than being mistaken for success.
        return classified == PromptFailureKind.None ? PromptFailureKind.Error : classified;
    }

    /// <summary>
    /// The text to classify: the terminal result's error message when present (on an error the agent's
    /// final <c>result</c> field carries the error description), else the captured process streams
    /// (the no-terminal-result rejection case). Both are inside the Claude quarantine.
    /// </summary>
    private static string ClassificationText(ProcessResult process, ClaudeResult result)
    {
        if (result.HasResult && result.IsError && !string.IsNullOrWhiteSpace(result.ResultText))
        {
            return result.ResultText!;
        }

        // No usable result text — fall back to the raw streams (stderr first: rejections print there).
        return string.Join(
            "\n",
            new[] { result.ResultText, result.Subtype, process.StandardError, process.StandardOutput }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private static string BuildSummary(ProcessResult process, ClaudeResult result)
    {
        if (process.TimedOut)
        {
            return "claude timed out";
        }

        if (!process.Succeeded)
        {
            return $"claude exited {process.ExitCode}";
        }

        if (!result.HasResult)
        {
            return "claude produced no terminal result message";
        }

        string cost = result.CostUsd is { } c ? $", cost ${c:0.0000}" : string.Empty;
        string turns = result.NumTurns is { } t ? $", {t} turn(s)" : string.Empty;
        return result.IsError
            ? $"claude reported is_error{cost}{turns}"
            : $"claude completed{cost}{turns}";
    }
}
