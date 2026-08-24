using System.Text;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Prompts;

/// <summary>
/// The v1 prompt runner: Claude Code headless (<c>claude -p</c>). ALL Claude-specific flag
/// spelling and stream parsing is confined to this class (SSOT §9). Invocation:
/// <code>
/// claude -p --output-format stream-json --verbose --permission-mode &lt;m&gt; --max-turns &lt;n&gt;
///   [--model &lt;m&gt;] --allowedTools &lt;joined&gt; --add-dir &lt;planDir&gt; [extraArgs…]
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
        int? denialAbortThreshold = invocation.AbortAfterConsecutiveToolDenials;

        // Spawn guard only — it stops the sink from queueing a redundant Cancel on every subsequent
        // line. The AUTHORITATIVE "did we abort" is re-derived from the scanner AFTER the process
        // returns (its state is final by then), so no cross-thread read of this flag is load-bearing.
        bool denialAbortFired = false;

        // #504 stall watchdog. Bounds SILENCE, not duration: the caller's Timeout stays a backstop while
        // this kills a session that has stopped producing. Ticks via Volatile so the reader thread's write
        // is visible to the watchdog without a lock; `stallFired` is the same spawn-guard shape as
        // denialAbortFired above, and is likewise re-read AFTER the process returns rather than trusted
        // cross-thread mid-flight.
        long lastLineTicks = DateTime.UtcNow.Ticks;
        bool stallFired = false;

        try
        {
            void Tee(string line)
            {
                Volatile.Write(ref lastLineTicks, DateTime.UtcNow.Ticks);
                parser.Feed(line);
                permissionScanner.Feed(line);
                streamWriter?.WriteLine(line);
                transcript?.Feed(line);

                if (denialAbortThreshold is not { } threshold
                    || denialAbortFired
                    || permissionScanner.ConsecutiveDenials < threshold)
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
            if (invocation.StallBound is { } stallBound && stallBound > TimeSpan.Zero)
            {
                TimeSpan poll = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, stallBound.Ticks / 20));
                DateTime previousPollAt = DateTime.UtcNow;
                stallWatchdog = Task.Run(async () =>
                {
                    while (!stallCts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(poll, stallCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;   // the run finished; nothing to police
                        }
                        catch (ObjectDisposedException)
                        {
                            // The LAUNCH-FAILURE path returns before the shutdown below, so the source can
                            // be disposed out from under a watchdog still sitting in Delay. Swallow it:
                            // an unobserved exception on a pool thread is a poor way to report that a
                            // process never started, and the real fault is already on its way to the caller.
                            return;
                        }

                        DateTime pollAt = DateTime.UtcNow;
                        TimeSpan sincePreviousPoll = pollAt - previousPollAt;
                        previousPollAt = pollAt;
                        var silent = TimeSpan.FromTicks(pollAt.Ticks - Volatile.Read(ref lastLineTicks));

                        switch (ClassifySilence(silent, sincePreviousPoll, poll, stallBound))
                        {
                            case StallVerdict.Suspended:
                                // #517: the MACHINE was asleep, not the session. Give the session a fresh
                                // full window rather than counting time it had no opportunity to emit in.
                                Volatile.Write(ref lastLineTicks, pollAt.Ticks);
                                continue;

                            case StallVerdict.KeepWaiting:
                                continue;
                        }

                        stallFired = true;
                        try
                        {
                            abortCts.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                            // The run already finished and the source was disposed.
                        }
                        return;
                    }
                });
            }

            ProcessResult process;
            try
            {
                process = await _processRunner.RunAsync(
                    command,
                    invocation.WorkingDirectory,
                    BuildEnvironment(invocation),
                    invocation.Timeout,
                    standardInput: invocation.ComposedPrompt,
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
                return LaunchFailureResult(launchFailure);
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
            if (stallFired && !result.HasResult)
            {
                var silentFor = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Volatile.Read(ref lastLineTicks));
                return new PromptResult
                {
                    Completed = false,
                    IsError = true,
                    ResultText = result.ResultText,
                    CostUsd = result.CostUsd,
                    NumTurns = result.NumTurns,
                    Usage = result.Usage is { } stalledUsage
                        ? new PromptUsage { InputTokens = stalledUsage.InputTokens, OutputTokens = stalledUsage.OutputTokens }
                        : null,
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
                && permissionScanner.ConsecutiveDenials >= abortThreshold)
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
                    Usage = result.Usage is { } abortedUsage
                        ? new PromptUsage { InputTokens = abortedUsage.InputTokens, OutputTokens = abortedUsage.OutputTokens }
                        : null,
                    FailureKind = PromptFailureKind.Error,
                    BlockedWritePaths = permissionScanner.BlockedWritePaths,
                    Summary =
                        $"aborted after {abortThreshold} consecutive permission-denied tool calls — " +
                        $"the prompt has no granted tool for what it was asked to do{refused}"
                };
            }

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

                // A straight CARRY of what the parser mined (DoR §12.4 / #230-lite) — no recomputation
                // and no defaulting to { 0, 0 }: a runner that reported no usage stays null, so the
                // per-tier spend line can tell "not reported" from "consumed nothing". The Claude-shaped
                // ClaudeUsage is restated as the runner-agnostic PromptUsage here, where the quarantine
                // (SSOT §9) ends.
                Usage = result.Usage is { } usage
                    ? new PromptUsage { InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens }
                    : null,
                FailureKind = failureKind,
                ResetHint = resetHint,
                BlockedWritePaths = permissionScanner.BlockedWritePaths,
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

        // UNCONDITIONAL, exactly like the --add-dir <planDirectory> grant immediately below: the harness
        // provisions the permission its own retry protocol prescribes rather than hoping the plan author
        // (or the operator's ~/.claude/settings.json) already did. Emitted even when the plan declares
        // nothing, because ResolveToolGrants never returns an empty effective set.
        args.Add("--allowedTools");
        args.Add(string.Join(",", ResolveToolGrants(settings.AllowedTools).Effective));

        args.Add("--add-dir");
        args.Add(invocation.PlanDirectory);

        args.AddRange(settings.ExtraArgs);

        return args;
    }

    /// <summary>
    /// The ONE grant the harness provisions for itself (issue #382), spelled exactly as the #252
    /// read-only default and every <c>guardrails.json</c> spell it — a near-miss (<c>Bash(git show:*)</c>,
    /// <c>Bash(git show *)</c>) is a grant the CLI would not match. QUARANTINED here with the rest of the
    /// Claude flag spelling (SSOT §9).
    /// <para>
    /// READ-ONLY, and only this. The salvage feedback also offers a whole-patch route, but the verb that
    /// would license it mutates the tree and is unnarrowable under a prefix glob — so the harness never
    /// injects it; granting that route stays the plan author's explicit call.
    /// </para>
    /// </summary>
    internal const string SalvageInspectionGrant = "Bash(git show*)";

    /// <summary>
    /// Resolve the plan's DECLARED tool grants into the set the runner actually passes, reporting
    /// separately what the HARNESS added — the read-only git inspection grant the retry-salvage
    /// protocol (<see cref="RetryPolicy"/>'s salvage section) prescribes but has never provisioned.
    /// The result is RETURNED rather than the settings list being mutated in place, so the attempt
    /// provenance and the attempt log header can record the effective set beside the declared one
    /// instead of the two silently diverging.
    /// <para>
    /// Pure and idempotent: the declared entries keep their order, the harness grant is APPENDED only
    /// when absent, and the caller's list is never mutated (the same settings instance is reused across
    /// every attempt of every task on this runner, so an in-place append would accumulate).
    /// </para>
    /// </summary>
    internal static ToolGrantResolution ResolveToolGrants(IReadOnlyList<string> declaredTools)
    {
        var effective = new List<string>(declaredTools);
        var injected = new List<string>();

        if (!effective.Contains(SalvageInspectionGrant, StringComparer.Ordinal))
        {
            effective.Add(SalvageInspectionGrant);
            injected.Add(SalvageInspectionGrant);
        }

        return new ToolGrantResolution { Effective = effective, Injected = injected };
    }

    /// <summary>
    /// The result for a run that never started: the launch fault is classified by the SAME
    /// <see cref="ClaudeSignalClassifier"/> quarantine every other failure goes through (DoR §6.3 —
    /// "cannot reach this provider right now" is <see cref="PromptFailureKind.Transient"/>, so it rides
    /// the shipped #115 bounded pause and does not burn a retry on re-launching a binary that is still
    /// absent). <see cref="PromptResult.Completed"/> is false and the summary names the command — the
    /// actionable half, since the harness surfaces this summary and the operator needs to know WHICH
    /// command could not be launched.
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
    private PromptResult LaunchFailureResult(System.ComponentModel.Win32Exception launchFailure)
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
            Summary = $"claude could not be launched: '{_command}' — {launchFailure.Message}"
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
        // change cannot regress it.
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
        // `result` line. Tool-result content is excluded by construction, and a long rejection still
        // classifies — which a tail-only or byte-capped heuristic would silently drop.
        return string.Join(
            "\n",
            new[] { result.ResultText, result.Subtype, process.StandardError, NonStreamStdout(process.StandardOutput) }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>What a single stall-watchdog poll concluded (issue #517).</summary>
    internal enum StallVerdict
    {
        /// <summary>Silence is within the bound; keep polling.</summary>
        KeepWaiting,

        /// <summary>The MACHINE was not running between polls (sleep / hibernate / a hard freeze).</summary>
        Suspended,

        /// <summary>The session has genuinely produced nothing for longer than the bound.</summary>
        Stalled
    }

    /// <summary>
    /// A poll that took vastly longer than its own interval means the machine was SUSPENDED, not that the
    /// session went silent — so the gap must not be counted as silence (issue #517).
    ///
    /// <para><b>Why this is not a heuristic about load.</b> The watchdog polls at <c>stallBound / 20</c> —
    /// about 60 seconds at the shipped bound — so the two cases are separated by orders of magnitude, not
    /// by a margin: a two-hour gap in a one-minute loop is unambiguous. The factor below only has to be
    /// larger than the worst scheduling delay a running machine can impose.</para>
    ///
    /// <para><b>And the failure direction is the safe one.</b> Misreading a genuine stall as a suspend
    /// costs one more bound-length window before the kill; misreading a suspend as a stall KILLS HEALTHY
    /// WORK, which is the whole defect #504 set out to remove. When the two are hard to tell apart, wait.</para>
    ///
    /// <para>Pure and <c>internal</c> so the decision is testable: the loop that calls it reads
    /// <c>DateTime.UtcNow</c> directly, which is exactly why the wall-clock bug shipped untested.</para>
    /// </summary>
    internal static StallVerdict ClassifySilence(
        TimeSpan silent, TimeSpan sincePreviousPoll, TimeSpan poll, TimeSpan stallBound)
    {
        const int SuspendFactor = 4;

        if (poll > TimeSpan.Zero && sincePreviousPoll > poll * SuspendFactor)
        {
            return StallVerdict.Suspended;
        }

        return silent >= stallBound ? StallVerdict.Stalled : StallVerdict.KeepWaiting;
    }

    /// <summary>
    /// The part of a runner's stdout that is NOT stream content (#516): lines that do not parse as a
    /// stream envelope, plus the terminal <c>result</c> envelope. Everything an agent read or wrote
    /// arrives as an <c>assistant</c>/<c>user</c>/<c>system</c> envelope and is dropped here, so a file
    /// whose text happens to contain "rate limit" can no longer be classified as a rate limit.
    /// </summary>
    internal static string? NonStreamStdout(string? stdout)
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
            if (!isEnvelope || trimmed.Contains("\"type\":\"result\"", StringComparison.Ordinal))
            {
                kept.Add(trimmed);
            }
        }

        return kept.Count == 0 ? null : string.Join("\n", kept);
    }

    private static string BuildSummary(ProcessResult process, ClaudeResult result)
    {
        if (process.TimedOut)
        {
            return "claude timed out";
        }

        // A max-turns exhaustion (issue #129) gets a distinct, human-readable summary so the journal /
        // feedback shows a TURN-budget signal rather than a generic "is_error". The structured subtype
        // is the authority; the turn count (if any) is appended.
        if (string.Equals(result.Subtype, "error_max_turns", StringComparison.Ordinal))
        {
            string n = result.NumTurns is { } turnCount ? $" ({turnCount} turn(s))" : string.Empty;
            return $"claude reached the turn limit{n}";
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

/// <summary>
/// The outcome of <see cref="ClaudePromptRunner.ResolveToolGrants"/>: the grants actually handed to
/// the CLI, and — held separately, never folded away — the subset the HARNESS contributed. Keeping
/// the two apart is what makes the effective permission set auditable: a run can show what the plan
/// declared and what the harness added on top, instead of one merged list nobody can attribute.
/// </summary>
internal sealed record ToolGrantResolution
{
    /// <summary>
    /// The effective grants passed via <c>--allowedTools</c>: the declared entries (relative order
    /// preserved) plus <see cref="Injected"/>. Never empty — the harness always provisions its own grant.
    /// </summary>
    public required IReadOnlyList<string> Effective { get; init; }

    /// <summary>
    /// ONLY what the harness added on top of the declared list. Empty when the plan already declared
    /// everything the harness needs — the grant is provisioned, never duplicated.
    /// </summary>
    public required IReadOnlyList<string> Injected { get; init; }
}
