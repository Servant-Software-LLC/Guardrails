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
                streamWriter.WriteLine(line);
                transcript?.Feed(line);
            }

            ProcessResult process = await _processRunner.RunAsync(
                command,
                invocation.WorkingDirectory,
                invocation.Environment,
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

            return new PromptResult
            {
                Completed = completed,
                IsError = result.IsError,
                ResultText = result.ResultText,
                CostUsd = result.CostUsd,
                NumTurns = result.NumTurns,
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
