using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using Guardrails.Core.State;

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
        var streamLines = new List<string>();

        // Open claude-stream.jsonl for incremental writes before launching the process so
        // the "view log" link can tail it in real time (issue #41). AutoFlush ensures each
        // line is visible on disk as it arrives rather than after the process exits.
        // OutputDataReceived events are serialized by AsyncStreamReader, so no lock needed.
        Directory.CreateDirectory(Path.GetDirectoryName(invocation.StreamLogPath)!);
        await using var streamWriter = new StreamWriter(invocation.StreamLogPath, append: false);
        streamWriter.AutoFlush = true;

        void Tee(string line)
        {
            streamLines.Add(line);
            parser.Feed(line);
            streamWriter.WriteLine(line);
        }

        ProcessResult process = await _processRunner.RunAsync(
            command,
            invocation.WorkingDirectory,
            invocation.Environment,
            invocation.Timeout,
            standardInput: invocation.ComposedPrompt,
            stdoutLineSink: Tee,
            cancellationToken).ConfigureAwait(false);

        // claude-stream.jsonl is fully written line-by-line above; no batch write needed.

        // Derive the CLI-equivalent transcript.md deterministically from the same stream
        // (issue #27): the raw JSONL is the debug artifact; the transcript is what humans
        // skim and what dependent tasks read (issue #26).
        if (invocation.TranscriptLogPath is { } transcriptPath)
        {
            string rawStream = string.Join('\n', streamLines);
            AtomicFile.WriteAllText(transcriptPath, ClaudeTranscriptRenderer.Render(rawStream));
        }

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
