using System.Text;
using System.Text.RegularExpressions;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Prompts;

/// <summary>
/// What differs between the agent CLIs that speak the Claude-shaped <c>stream-json</c> NDJSON dialect
/// (<see cref="ClaudePromptRunner"/>, <see cref="CursorPromptRunner"/>) once the process is launched. Every
/// other part of a session — the stdin write, the live tee to <c>*.stream.jsonl</c> and <c>transcript.md</c>,
/// the #504 stall watchdog, the launch-failure catch, the terminal-result parse and the failure
/// classification (<see cref="ClaudeSignalClassifier"/>, one classifier for both) — is the same code, in
/// <see cref="StreamJsonCliSession"/>.
/// </summary>
internal sealed record StreamJsonCliDialect
{
    /// <summary>
    /// The word every summary opens with (<c>"claude exited 1"</c>, <c>"cursor timed out"</c>). The CLI's
    /// PRODUCT name, not its <c>command</c>: the command appears separately in the launch-failure summary.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Feed <see cref="ClaudePermissionScanner"/> from the stream. True only for a CLI whose denials the
    /// scanner can actually recognise (Claude's <c>tool_result</c> denial phrasing). When false the scanner
    /// is never constructed, so <see cref="PromptResult.BlockedWritePaths"/> stays empty and
    /// <see cref="PromptInvocation.AbortAfterConsecutiveToolDenials"/> is INERT for this CLI — honestly
    /// absent rather than fed a dialect it would misread.
    /// </summary>
    public required bool ScansPermissionDenials { get; init; }
}

/// <summary>
/// ONE headless agent-CLI session over the Claude-shaped <c>stream-json</c> dialect, shared by
/// <see cref="ClaudePromptRunner"/> and <see cref="CursorPromptRunner"/> (#764). The runners own their
/// argv, environment and prompt delivery (the vendor spelling, SSOT §9); this owns everything after the
/// spawn. Extracted from <see cref="ClaudePromptRunner"/> verbatim — with <see cref="StreamJsonCliDialect.Label"/>
/// <c>"claude"</c> and <see cref="StreamJsonCliDialect.ScansPermissionDenials"/> true, the result is
/// byte-identical to the pre-#764 runner.
/// </summary>
internal static class StreamJsonCliSession
{
    /// <summary>
    /// Pin the two persisted log artifacts to UTF-8 (no BOM) explicitly (issue #55). The
    /// no-arg <see cref="StreamWriter"/> overloads already default to this, but the symptom of
    /// #55 — mojibake — lived in exactly these files, so stating the encoding keeps a future edit
    /// from silently regressing them to a BOM/code-page default. Matches <see cref="State.AtomicFile"/>
    /// and <see cref="ProcessRunner"/>'s decode.
    /// </summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Launch <paramref name="command"/> and drive the session to a <see cref="PromptResult"/>. Semantic
    /// disposition: a non-zero exit OR no terminal <c>result</c> message ⇒ <see cref="PromptResult.Completed"/>
    /// = false. <paramref name="lineObserver"/> sees every stdout line in order, on the reader thread, for a
    /// runner that must check something about the stream itself (Cursor's prompt-echo check, #764).
    /// </summary>
    public static async Task<PromptResult> RunAsync(
        ProcessRunner processRunner,
        ResolvedCommand command,
        IReadOnlyDictionary<string, string> environment,
        string? standardInput,
        PromptInvocation invocation,
        StreamJsonCliDialect dialect,
        CancellationToken cancellationToken,
        Action<string>? lineObserver = null)
    {
        var parser = new ClaudeStreamParser();

        // Mine permission-wall signals from the same stream lines (issues #86 / #104): a write/edit
        // refused because the path is not granted. The scanner is fed in the Tee alongside the parser
        // and transcript; its output flows out as the runner-agnostic BlockedWritePaths list. Null for a
        // dialect whose denials the scanner cannot read (#764) — see StreamJsonCliDialect.
        ClaudePermissionScanner.Scanner? permissionScanner =
            dialect.ScansPermissionDenials ? new ClaudePermissionScanner.Scanner() : null;

        // Open both log artifacts for incremental writes before launching the process so the
        // "view log" link can tail them in real time (issue #41) — both the raw debug stream and
        // transcript.md (the human/dependent-task view, issues #26/#27) grow live, instead of appearing
        // only when the task finishes. OutputDataReceived events are serialized by AsyncStreamReader, so
        // the shared writers/parser need no locking.
        //
        // DELIBERATE TRADEOFF: master wrote both artifacts via AtomicFile.WriteAllText (temp+move)
        // once the process exited; this streams them in place so a "view log" tail sees them grow
        // live (issue #41). Dropping atomicity is acceptable for these two append-only log artifacts
        // because nothing hashes or guardrail-gates them: the verdict never comes from these files —
        // it comes from the parsed `result` line + exit code (see `completed` below).
        //
        // An EMPTY / null StreamLogPath means "don't write a stream log" (issue #381), NOT "abort": the
        // advisory criticality assessment (CriticalityJudge.BuildInvocation) and any other caller that
        // wants no raw debug tee leaves it empty. SKIP the writer (and its Directory.CreateDirectory)
        // rather than crashing on Path.GetDirectoryName("") == null. The file is a debug/log artifact
        // no code hashes or gates, so its absence is benign — the Tee below guards the writer with `?.`.
        StreamWriter? streamWriter = null;
        if (!string.IsNullOrEmpty(invocation.StreamLogPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(invocation.StreamLogPath)!);
            streamWriter = new StreamWriter(invocation.StreamLogPath, append: false, Utf8NoBom) { AutoFlush = true };
        }

        // transcript.md is rendered incrementally from the same lines via StreamingWriter, which
        // parses each line independently and is byte-identical to a batch Render at Complete().
        // StreamingWriter flushes itself, so this writer needs no AutoFlush.
        StreamWriter? transcriptFile = invocation.TranscriptLogPath is { } transcriptPath
            ? new StreamWriter(transcriptPath, append: false, Utf8NoBom)
            : null;
        ClaudeTranscriptRenderer.StreamingWriter? transcript =
            transcriptFile is null ? null : new ClaudeTranscriptRenderer.StreamingWriter(transcriptFile);

        // The #452 fail-fast: a linked source so a run whose every tool call is refused can be killed
        // mid-stream WITHOUT disturbing the caller's token. ProcessRunner treats cancellation as
        // "kill the tree and return" (not a throw), so the abort lands as an ordinary ProcessResult.
        using var abortCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Inert without a scanner: nothing could ever count a denial, so no threshold can be reached.
        int? denialAbortThreshold = permissionScanner is null ? null : invocation.AbortAfterConsecutiveToolDenials;

        // Spawn guard only — it stops the sink from queueing a redundant Cancel on every subsequent
        // line. The AUTHORITATIVE "did we abort" is re-derived from the scanner AFTER the process
        // returns (its state is final by then), so no cross-thread read of this flag is load-bearing.
        bool denialAbortFired = false;

        // #504 stall watchdog / #517 suspend discrimination. Bounds SILENCE, not duration: the caller's
        // Timeout stays a backstop while this kills a session that has stopped producing. The clock, the
        // cadence and the verdict all live on the shared StallWatch — OpenAiCompatPromptRunner drives the
        // same object, because a bound spelled twice is a bound that is wrong in one of the two places.
        // Its `Stalled` flag is the same spawn-guard shape as denialAbortFired above, and is likewise
        // re-read AFTER the process returns rather than trusted cross-thread mid-flight.
        StallWatch? stall = invocation.StallBound is { } bound && bound > TimeSpan.Zero
            ? new StallWatch(bound)
            : null;

        try
        {
            void Tee(string line)
            {
                stall?.Beat();
                parser.Feed(line);
                permissionScanner?.Feed(line);
                lineObserver?.Invoke(line);
                streamWriter?.WriteLine(line);
                transcript?.Feed(line);

                if (denialAbortThreshold is not { } threshold
                    || denialAbortFired
                    || permissionScanner!.ConsecutiveDenials < threshold)
                {
                    return;
                }

                denialAbortFired = true;

                // OFF this thread on purpose. Tee runs on the stdout reader callback; cancelling inline
                // can resume WaitForExitAsync's continuation here, which then awaits the very reader
                // drain this thread owes — a self-deadlock. Task.Run hands the cancel to the pool.
                _ = Task.Run(() =>
                {
                    try
                    {
                        abortCts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // The run already finished and the source was disposed — nothing left to abort.
                    }
                });
            }

            // The watchdog itself: poll staleness on a cadence well under the bound, and abort through the
            // SAME linked source the #452 fail-fast uses, so a stall lands as an ordinary ProcessResult
            // (ProcessRunner treats cancellation as "kill the tree and return") rather than a throw.
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(abortCts.Token);
            Task? stallWatchdog = null;
            if (stall is not null)
            {
                // The loop, the verdict and the #517 window reset all live on the watch —
                // OpenAiCompatPromptRunner drives the same object. Killing the session is this runner's own
                // business, and it goes through the SAME linked source the #452 fail-fast uses so a stall
                // lands as an ordinary ProcessResult rather than a throw.
                stallWatchdog = Task.Run(() => stall.WatchAsync(
                    Task.Delay,
                    () =>
                    {
                        try
                        {
                            abortCts.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                            // The run already finished and the source was disposed.
                        }
                    },
                    stallCts.Token));
            }

            ProcessResult process;
            try
            {
                process = await processRunner.RunAsync(
                    command,
                    invocation.WorkingDirectory,
                    environment,
                    invocation.Timeout,
                    standardInput,
                    stdoutLineSink: Tee,
                    abortCts.Token).ConfigureAwait(false);
            }
            catch (System.ComponentModel.Win32Exception launchFailure)
            {
                // The runner binary itself would not start (DoR §6.3's missing-CLI shape): the command is
                // not on PATH, or the OS refused the spawn. ProcessRunner calls Process.Start with no try
                // — deliberately, since it is shared with script actions and guardrails — so the fault
                // arrives HERE, before any text was ever produced to classify. Without this catch it
                // escapes RunAsync entirely and the attempt dies as an unhandled executor fault instead
                // of a classified, pausable one.
                //
                // NARROW ON PURPOSE: Win32Exception is the spawn fault on this path and nothing else —
                // a failure once the child is running comes back as a ProcessResult, and every other
                // exception type keeps propagating untouched.
                //
                // `guardrails validate`'s GR2009 PATH probe already warns about a missing runner command
                // at validate time; this is the runtime residual of that same fact — the relationship
                // `no-route` has to GR2048.
                return LaunchFailureResult(launchFailure, command.Executable, dialect);
            }

            // The run is over, so retire the watchdog before anything below can be blamed on it.
            stallCts.Cancel();
            if (stallWatchdog is not null)
            {
                try { await stallWatchdog.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected on the normal path */ }
            }

            // Both files are fully written line-by-line above; Complete() finalizes the transcript's
            // trailing newline so it matches a batch render exactly.
            transcript?.Complete();

            ClaudeResult result = parser.Build();

            // #504: a stall abort, re-derived AFTER the process returned (the flag's write is final by
            // now), and reported as its own kind. `!result.HasResult` keeps it from ever DISCARDING a
            // verdict — if the child raced the kill and produced a terminal result anyway, that result
            // is the answer, exactly as the #452 fail-fast below treats its own abort.
            if (stall is { Stalled: true } && !result.HasResult)
            {
                TimeSpan silentFor = stall.SilentFor();
                return new PromptResult
                {
                    Completed = false,
                    IsError = true,
                    ResultText = result.ResultText,
                    CostUsd = result.CostUsd,
                    NumTurns = result.NumTurns,
                    Usage = ToPromptUsage(result.Usage),
                    FailureKind = PromptFailureKind.Stalled,
                    Summary =
                        $"STALLED — no stream output for {silentFor.TotalMinutes:F1}m " +
                        $"(bound {(invocation.StallBound ?? TimeSpan.Zero).TotalMinutes:F0}m); the session was killed. " +
                        "The process was alive and producing nothing, which is not the same as slow: a session " +
                        "that keeps emitting is never stopped by this bound."
                };
            }

            // #452 fail-fast outcome. Re-derived from the scanner (final now the reader has drained)
            // rather than from the sink's flag, and reported as a DISTINCT summary: "no verdict" is
            // useless to an operator without the reason, and the reason here — every granted-tool route
            // was refused — is a CONFIGURATION fault the caller must surface, not a model failure.
            // Deliberately NOT PromptFailureKind.Transient: re-running changes nothing.
            // `!result.HasResult` keeps this from ever DISCARDING a verdict: if the child raced the kill
            // and produced a terminal result anyway, that result is the answer. The bound exists to stop
            // waste, not to punish a run that got there despite the refusals.
            if (denialAbortThreshold is { } abortThreshold
                && !result.HasResult
                && permissionScanner!.ConsecutiveDenials >= abortThreshold)
            {
                string refused = permissionScanner.BlockedWritePaths.Count > 0
                    ? $" (refused: {string.Join(", ", permissionScanner.BlockedWritePaths.Take(3))})"
                    : string.Empty;
                return new PromptResult
                {
                    Completed = false,
                    IsError = true,
                    ResultText = result.ResultText,
                    CostUsd = result.CostUsd,
                    NumTurns = result.NumTurns,
                    Usage = ToPromptUsage(result.Usage),

                    // Carried on the abort path too (#349): the stream that got this far still echoed
                    // which model was refused every route, and that is a fact about the attempt.
                    ObservedModel = result.Model,
                    FailureKind = PromptFailureKind.Error,
                    BlockedWritePaths = permissionScanner.BlockedWritePaths,
                    RefusedCommands = permissionScanner.RefusedCommands,
                    Summary =
                        $"aborted after {abortThreshold} consecutive permission-denied tool calls — " +
                        $"the prompt has no granted tool for what it was asked to do{refused}"
                };
            }

            bool completed = process.Succeeded && result.HasResult;
            string summary = BuildSummary(process, result, dialect.Label);
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

                // A straight CARRY of what the parser mined (DoR §12.4 / #230-lite) — no recomputation
                // and no defaulting to { 0, 0 }: a runner that reported no usage stays null, so the
                // per-tier spend line can tell "not reported" from "consumed nothing". The Claude-shaped
                // ClaudeUsage is restated as the runner-agnostic PromptUsage here, where the quarantine
                // (SSOT §9) ends. Cursor's terminal result carries no usage at all, so it is null there.
                Usage = ToPromptUsage(result.Usage),

                // The same straight carry for the model the stream ECHOED (#349) — what actually ran,
                // as opposed to what the harness asked for (already recorded as AttemptProvenance.Model).
                // Nothing is substituted from the request when the stream named none: null stays null,
                // and the Claude-shaped ClaudeResult.Model becomes the runner-agnostic ObservedModel here,
                // where the quarantine (SSOT §9) ends.
                ObservedModel = result.Model,
                FailureKind = failureKind,
                ResetHint = resetHint,
                BlockedWritePaths = permissionScanner?.BlockedWritePaths ?? [],
                RefusedCommands = permissionScanner?.RefusedCommands ?? [],
                Summary = summary
            };
        }
        finally
        {
            // streamWriter is no longer an `await using var` (it is skipped for an empty StreamLogPath,
            // issue #381), so it — like transcriptFile — is disposed explicitly here.
            if (streamWriter is not null)
            {
                await streamWriter.DisposeAsync().ConfigureAwait(false);
            }

            if (transcriptFile is not null)
            {
                await transcriptFile.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static PromptUsage? ToPromptUsage(ClaudeUsage? usage) =>
        usage is { } u ? new PromptUsage { InputTokens = u.InputTokens, OutputTokens = u.OutputTokens } : null;

    /// <summary>
    /// The result for a run that never started: the launch fault is classified by the SAME
    /// classifier every other failure goes through (DoR §6.3 — "cannot reach this provider right now" is
    /// <see cref="PromptFailureKind.Transient"/>, so it rides the shipped #115 bounded pause and does not
    /// burn a retry on re-launching a binary that is still absent). <see cref="PromptResult.Completed"/> is
    /// false and the summary names the command — the actionable half, since the harness surfaces this
    /// summary and the operator needs to know WHICH command could not be launched.
    /// <para>
    /// The classified text is TYPE + native code + message rather than <c>ex.Message</c> alone. .NET's
    /// own launch message ("An error occurred trying to start process 'claude' with working directory
    /// '…'. The system cannot find the file specified.") is discriminating by itself and classifies
    /// without help; the <c>ex.ToString()</c>-shaped header
    /// ("<c>System.ComponentModel.Win32Exception (2): …</c>") is what also classifies the SHORTER form,
    /// where a Win32Exception carries only the bare OS string — "the system cannot find the file
    /// specified" is ordinary enough text that the classifier deliberately does not treat it as a signal
    /// on its own. Composed explicitly rather than taken from <c>ToString()</c> so a stack trace's
    /// contents can never reach the matcher.
    /// </para>
    /// </summary>
    private static PromptResult LaunchFailureResult(
        System.ComponentModel.Win32Exception launchFailure, string executable, StreamJsonCliDialect dialect)
    {
        string classificationText =
            $"{launchFailure.GetType().FullName} ({launchFailure.NativeErrorCode}): {launchFailure.Message}";

        return new PromptResult
        {
            Completed = false,

            // Nothing reported an error: the agent never ran. Completed = false already fails the
            // attempt — the same shape as today's "no terminal result" outcome.
            IsError = false,
            FailureKind = ClaudeSignalClassifier.Classify(classificationText),
            Summary = $"{dialect.Label} could not be launched: '{executable}' — {launchFailure.Message}"
        };
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

        // Prefer the STRUCTURED max-turns signal: Claude stamps the terminal result subtype
        // "error_max_turns" on a turn-budget exhaustion (issue #129). The result TEXT also carries
        // "Reached maximum number of turns (N)", which the text classifier matches too, but the
        // subtype is the stable structured signal and is checked first so a result-text wording
        // change cannot regress it. (Cursor has no turn budget and never emits this subtype.)
        if (string.Equals(result.Subtype, "error_max_turns", StringComparison.Ordinal))
        {
            return PromptFailureKind.MaxTurns;
        }

        PromptFailureKind classified = ClaudeSignalClassifier.Classify(ClassificationText(process, result));

        // A recognized transient/cap signal wins. Otherwise this is a genuine error — but if there was
        // no error text at all (e.g. a clean exit with no terminal result), still report Error so the
        // attempt fails rather than being mistaken for success.
        return classified == PromptFailureKind.None ? PromptFailureKind.Error : classified;
    }

    /// <summary>
    /// The text to classify: the terminal result's error message when the result IS an error (on an error the
    /// agent's final <c>result</c> field carries the error description), else the captured process streams.
    /// <para>
    /// A result that is NOT an error is never classified from its text (#763 review). On a non-zero exit with
    /// <c>is_error: false</c>, <c>result</c> is the agent's own closing prose ("Added the per-run spend limit
    /// check"), and reading it would classify a real failure as a provider limit and pause the task for hours.
    /// That case classifies from stderr plus the non-stream stdout exactly as a run with no result does, with the
    /// result envelope itself left out of the stdout for the same reason.
    /// </para>
    /// </summary>
    private static string ClassificationText(ProcessResult process, ClaudeResult result)
    {
        if (result.HasResult && result.IsError && !string.IsNullOrWhiteSpace(result.ResultText))
        {
            return result.ResultText!;
        }

        // No usable result text — fall back to the raw streams (stderr first: rejections print there).
        //
        // #516: stdout is the ENTIRE accumulated JSONL stream (ProcessRunner accumulates AND tees), so
        // handing it to the transient classifier whole means pattern-matching everything the agent read
        // and wrote. That is not hypothetical here: `PromptFailureKind.cs`'s own doc comment names
        // "429/503/529", "overloaded" and "usage/session/rate limit" — every one a pinned transient
        // phrase — and it was echoed into 10+ task streams of one Stage 3 run, because agents read that
        // file while doing observer work. The harness's own source was a false-positive trigger for its
        // own classifier.
        //
        // The fix is structural rather than a size cap: this fallback exists for output that is NOT A
        // STREAM AT ALL — a rejection printed before any envelope (#115's "instant rejection, no result
        // line"). So take only stdout lines that are not well-formed stream envelopes, plus the terminal
        // `result` line when it reports an error. Tool-result content is excluded by construction, and a long
        // rejection still classifies — which a tail-only or byte-capped heuristic would silently drop.
        bool resultMayBeRead = !result.HasResult || result.IsError;
        return string.Join(
            "\n",
            new[]
                {
                    resultMayBeRead ? result.ResultText : null,
                    result.Subtype,
                    process.StandardError,
                    NonStreamStdout(process.StandardOutput, includeResultEnvelope: resultMayBeRead)
                }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>
    /// The part of a runner's stdout that is NOT stream content (#516): lines that do not parse as a
    /// stream envelope, plus (unless <paramref name="includeResultEnvelope"/> is false) the terminal
    /// <c>result</c> envelope. Everything an agent read or wrote arrives as an <c>assistant</c>/<c>user</c>/
    /// <c>system</c> envelope (or, on Cursor, a <c>tool_call</c> envelope) and is dropped here, so a file whose
    /// text happens to contain "rate limit" can no longer be classified as a rate limit.
    /// </summary>
    internal static string? NonStreamStdout(string? stdout, bool includeResultEnvelope = true)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        var kept = new List<string>();
        foreach (string line in stdout.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // A stream envelope is a JSON object carrying a "type". Keep the terminal result (it names
            // the stop reason) and everything that is not an envelope at all; drop assistant/user/system
            // content, which is where an agent's READING of a file would otherwise leak into the verdict.
            bool isEnvelope = trimmed[0] == '{' && trimmed.Contains("\"type\":", StringComparison.Ordinal);
            bool isResult = isEnvelope && trimmed.Contains("\"type\":\"result\"", StringComparison.Ordinal);
            if (!isEnvelope || (isResult && includeResultEnvelope))
            {
                kept.Add(trimmed);
            }
        }

        return kept.Count == 0 ? null : string.Join("\n", kept);
    }

    /// <summary>The longest provider excerpt a no-result exit summary carries (#763), ellipsis included.</summary>
    internal const int NoResultExcerptMaxChars = 200;

    /// <summary>A Node.js runtime warning line (<c>(node:1234) [DEP0040] DeprecationWarning: …</c>), never the cause.</summary>
    private static readonly Regex NodeRuntimeWarning = new(@"^\(node:\d+\)", RegexOptions.CultureInvariant);

    /// <summary>
    /// The one line of a failed, no-terminal-result run that says why it failed (#763), chosen in this order:
    /// <list type="number">
    /// <item>the first line (stderr's, then the non-stream stdout's) that <see cref="ClaudeSignalClassifier"/>
    /// recognizes as a specific signal (transient, output cap, max turns), so the provider's refusal wins over
    /// whatever else the CLI printed around it;</item>
    /// <item>else the first stderr line that is not a Node.js runtime warning (a <c>DeprecationWarning</c> on
    /// stderr says nothing about why the run failed);</item>
    /// <item>else the first non-stream stdout line;</item>
    /// <item>else the first stderr line, warning or not, rather than nothing.</item>
    /// </list>
    /// Non-stream stdout is the same #516 filter the classifier reads, so a stream envelope carrying an agent's
    /// file content is never quoted. stderr is NOT filtered: whatever the process wrote there can be quoted.
    /// Truncated to <see cref="NoResultExcerptMaxChars"/>; null when neither stream has a non-empty line.
    /// </summary>
    internal static string? NoResultExcerpt(ProcessResult process)
    {
        List<string> stderr = NonEmptyLines(process.StandardError);
        List<string> stdout = NonEmptyLines(NonStreamStdout(process.StandardOutput));

        string? line = stderr.Concat(stdout).FirstOrDefault(IsRecognizedSignal)
            ?? stderr.FirstOrDefault(l => !NodeRuntimeWarning.IsMatch(l))
            ?? stdout.FirstOrDefault()
            ?? stderr.FirstOrDefault();
        if (line is null)
        {
            return null;
        }

        return line.Length <= NoResultExcerptMaxChars
            ? line
            : string.Concat(line.AsSpan(0, NoResultExcerptMaxChars - 1).TrimEnd(), "…");
    }

    /// <summary>
    /// True when the classifier names a SPECIFIC cause for <paramref name="line"/>. <see cref="PromptFailureKind.Error"/>
    /// is the classifier's answer for any non-empty text it does not recognize, so it is not a signal.
    /// </summary>
    private static bool IsRecognizedSignal(string line) =>
        ClaudeSignalClassifier.Classify(line) is not (PromptFailureKind.None or PromptFailureKind.Error);

    private static List<string> NonEmptyLines(string? text)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return lines;
        }

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                lines.Add(trimmed);
            }
        }

        return lines;
    }

    private static string BuildSummary(ProcessResult process, ClaudeResult result, string label)
    {
        if (process.TimedOut)
        {
            return $"{label} timed out";
        }

        // A max-turns exhaustion (issue #129) gets a distinct, human-readable summary so the journal /
        // feedback shows a TURN-budget signal rather than a generic "is_error". The structured subtype
        // is the authority; the turn count (if any) is appended.
        if (string.Equals(result.Subtype, "error_max_turns", StringComparison.Ordinal))
        {
            string n = result.NumTurns is { } turnCount ? $" ({turnCount} turn(s))" : string.Empty;
            return $"{label} reached the turn limit{n}";
        }

        if (!process.Succeeded)
        {
            // #763: with no terminal result, the process's own words are the only account of WHY it
            // exited — "You've hit your individual spend limit · run /usage-credits …" — and a bare
            // "claude exited 1" dropped them. This summary becomes the pause reason, the needs-human
            // line and the live/status detail, so the operator would otherwise have to open the stream
            // log to learn something the harness already held. A run that DID produce a terminal result
            // keeps the plain form: its result text already travels separately (ResultText, feedback.md).
            string? excerpt = result.HasResult ? null : NoResultExcerpt(process);
            return excerpt is null
                ? $"{label} exited {process.ExitCode}"
                : $"{label} exited {process.ExitCode}: {excerpt}";
        }

        if (!result.HasResult)
        {
            return $"{label} produced no terminal result message";
        }

        string cost = result.CostUsd is { } c ? $", cost ${c:0.0000}" : string.Empty;
        string turns = result.NumTurns is { } t ? $", {t} turn(s)" : string.Empty;
        return result.IsError
            ? $"{label} reported is_error{cost}{turns}"
            : $"{label} completed{cost}{turns}";
    }
}
