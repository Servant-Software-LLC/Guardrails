using System.Text.Json;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Core.Execution;

/// <summary>
/// Runs a task's ACTION and normalizes it into the disposition the attempt loop needs.
/// Script actions go through the interpreter map; prompt actions go through the prompt
/// pipeline (compose → runner → parse). The returned <see cref="ActionRun"/> collapses both
/// shapes into: success, exit code (for the journal), timeout, cost, a needsHuman question (if
/// any), and failure feedback/summary.
/// </summary>
internal sealed class ActionRunner
{
    private readonly PlanDefinition _plan;
    private readonly ScriptUnitRunner _scriptRunner;
    private readonly PromptExecutionSupport _promptSupport;
    private readonly DependencyContextBuilder _dependencyContext;
    private readonly Func<TaskNode, int?, TimeSpan> _resolveTimeout;

    public ActionRunner(
        PlanDefinition plan,
        ScriptUnitRunner scriptRunner,
        PromptExecutionSupport promptSupport,
        DependencyContextBuilder dependencyContext,
        Func<TaskNode, int?, TimeSpan> resolveTimeout)
    {
        _plan = plan;
        _scriptRunner = scriptRunner;
        _promptSupport = promptSupport;
        _dependencyContext = dependencyContext;
        _resolveTimeout = resolveTimeout;
    }

    /// <summary>
    /// Run a task's action. Script actions go through the interpreter map; prompt actions go
    /// through the prompt pipeline (compose → runner → parse). The returned <see cref="ActionRun"/>
    /// normalizes both into the disposition the attempt loop needs: success, exit code (for the
    /// journal), timeout, cost, a needsHuman question (if any), and failure feedback/summary.
    /// </summary>
    public async Task<ActionRun> RunAsync(
        TaskNode task,
        int attemptNumber,
        string workspace,
        IReadOnlyDictionary<string, string> env,
        string snapshotPath,
        string fragmentOutPath,
        string? previousFeedbackPath,
        string logDir,
        double timeoutMultiplier,
        string? stagingDir,
        double maxTurnsMultiplier,
        CancellationToken cancellationToken,
        string? worktreeRoot = null)
    {
        if (task.Action.Kind != ActionKind.Prompt)
        {
            ProcessResult script = await _scriptRunner.RunAsync(
                task.Action.Path, task.Action.Args, workspace, env,
                Extend(_resolveTimeout(task, task.Action.TimeoutSeconds), timeoutMultiplier),
                cancellationToken).ConfigureAwait(false);
            return ActionRun.FromScript(script, ParseNeedsHuman(fragmentOutPath), HarnessWrite.RequestFrom(fragmentOutPath));
        }

        return await RunPromptActionAsync(
            task, attemptNumber, workspace, env, snapshotPath, fragmentOutPath, previousFeedbackPath,
            logDir, timeoutMultiplier, stagingDir, maxTurnsMultiplier, cancellationToken, worktreeRoot).ConfigureAwait(false);
    }

    /// <summary>Apply the timeout-extension factor (issue #119); 1× is the identity.</summary>
    private static TimeSpan Extend(TimeSpan timeout, double multiplier) =>
        multiplier <= 1.0 ? timeout : TimeSpan.FromSeconds(timeout.TotalSeconds * multiplier);

    /// <summary>
    /// Apply the turn-budget-extension factor (issue #129 / #94) to a <c>maxTurns</c> setting; 1× is
    /// the identity. Rounds UP so a fractional bump still raises the integer cap by at least one turn.
    /// </summary>
    private static int ExtendTurns(int maxTurns, double multiplier) =>
        multiplier <= 1.0 ? maxTurns : (int)Math.Ceiling(maxTurns * multiplier);

    private async Task<ActionRun> RunPromptActionAsync(
        TaskNode task,
        int attemptNumber,
        string workspace,
        IReadOnlyDictionary<string, string> env,
        string snapshotPath,
        string fragmentOutPath,
        string? previousFeedbackPath,
        string logDir,
        double timeoutMultiplier,
        string? stagingDir,
        double maxTurnsMultiplier,
        CancellationToken cancellationToken,
        string? worktreeRoot)
    {
        PromptRunnerRegistry registry = _promptSupport.RequireRegistry();
        PromptFile promptFile = PromptExecutionSupport.LoadPromptFile(task.Action.Path);
        PromptRunnerConfig runnerConfig = registry.ResolveConfig(task.Action.Runner ?? promptFile.Frontmatter.Runner);

        IReadOnlyList<DependencyContextRef> dependencies = _dependencyContext.BuildDependencyContext(task);
        IReadOnlyList<PriorAttemptRef> priorAttempts = _dependencyContext.BuildPriorAttempts(task.Id, attemptNumber);
        bool isWorktreeMode = !string.IsNullOrEmpty(worktreeRoot);

        // Prompt-output staging (SSOT §9.5, issue #266): the harness's own GUARDRAILS_STATE_OUT
        // target is never handed to the sub-agent directly — a `.claude/`-nested plan folder would
        // put it inside Claude Code's sensitive-path block. Stage it under a plain dot-folder inside
        // the effective workspace root instead (never `.claude/`-nested, always inside the worktree-
        // containment hook's allowed root) and promote it to fragmentOutPath the instant the runner
        // returns, unconditionally for every prompt action.
        string effectiveWorkspaceRoot = worktreeRoot ?? _plan.Workspace;
        string attemptFolder = Path.GetFileName(logDir);
        string stagingStateOutPath = PromptOutputStaging.PrepareStagingPath(
            effectiveWorkspaceRoot, task.Id, attemptFolder, fragmentOutPath);

        // #361 Phase 3 (doc 12 §7.4/§7.6): the autonomous reply channel. When a resume consumed a firstmate
        // answer for this unit's escalated needs-human gate, or a below-threshold judgment call recorded a
        // best-guess, the Scheduler stages the raw text in a per-task injected-human-answer file beside the
        // task's log dir. Read (and CONSUME once) it here so it flows into ComposeAction's injectedHumanAnswer
        // section as clearly-delimited UNTRUSTED DATA (never a harness instruction, §7.4 Finding 4). Absent in
        // the overwhelming common case (and whenever the dial is not wired) ⇒ null ⇒ the prompt is unchanged.
        string? injectedHumanAnswer = ReadAndConsumeInjectedAnswer(logDir);

        // §3.5: the staging dir + from→to map are embedded verbatim in the prompt (agents read
        // instructions, not env vars) — only when the task declares stagingOutputs and a staging dir
        // was provisioned (the executor passes null otherwise). The output-contract path embedded in
        // the prompt TEXT is the STAGING path (#266) so it matches what the agent is actually told to
        // write to via GUARDRAILS_STATE_OUT below.
        string composed = PromptComposer.ComposeAction(
            promptFile.Body, snapshotPath, stagingStateOutPath, previousFeedbackPath, dependencies, priorAttempts,
            stagingDir, stagingDir is not null ? task.StagingOutputs : null, isWorktreeMode, injectedHumanAnswer);
        AtomicFile.WriteAllText(Path.Combine(logDir, "composed-prompt.md"), composed);

        PromptRunnerSettings settings = PromptExecutionSupport.ApplyPromptOverrides(
            runnerConfig.EffectiveSettings(isGuardrail: false),
            task.Action.MaxTurns ?? promptFile.Frontmatter.MaxTurns);

        // task.json action.model override (issue #200): task override > the runner's own configured
        // model (already resolved into `settings.Model` above) > whatever the CLI's own default is —
        // ApplyModelOverride leaves `settings` untouched when there is no task-level override, so a
        // null Model still falls through to ClaudePromptRunner's "omit --model entirely" behavior.
        settings = PromptExecutionSupport.ApplyModelOverride(settings, task.Action.Model);

        // Auto-escalate the turn budget after a prior max-turns exhaustion (issue #129 / #94): raise
        // the effective maxTurns by the multiplier so the retry has headroom instead of re-hitting the
        // same cap. 1× (no prior max-turns) leaves it unchanged.
        settings = settings with { MaxTurns = ExtendTurns(settings.MaxTurns, maxTurnsMultiplier) };

        // Worktree containment hook (issue #199/#192): the OUTER runtime boundary — hard-enforced via
        // a Claude Code PreToolUse hook, on TOP of the write-scope CHECK's post-hoc diff (the INNER
        // boundary, unaffected). Injected ONLY for a real segment worktree (worktreeRoot non-null);
        // never for serial/shared-workspace mode, where there is no isolated tree to contain writes to.
        if (isWorktreeMode)
        {
            string settingsPath = WorktreeContainmentHook.WriteHookFiles(logDir, worktreeRoot!);
            settings = settings with { ExtraArgs = [.. settings.ExtraArgs, "--settings", settingsPath] };
        }

        // Copy-with-override (mirrors GuardrailRunner's guardrailEnv pattern): a PROMPT action's
        // GUARDRAILS_STATE_OUT is overridden to the staging path for THIS invocation only — the
        // shared `env` dict handed in by the caller is never mutated.
        var actionEnv = new Dictionary<string, string>(env, StringComparer.Ordinal)
        {
            ["GUARDRAILS_STATE_OUT"] = stagingStateOutPath
        };

        var invocation = new PromptInvocation
        {
            ComposedPrompt = composed,
            WorkingDirectory = workspace,
            PlanDirectory = _plan.PlanDirectory,
            Environment = actionEnv,
            Settings = settings,
            Timeout = Extend(
                _resolveTimeout(task, task.Action.TimeoutSeconds ?? promptFile.Frontmatter.TimeoutSeconds),
                timeoutMultiplier),
            StreamLogPath = Path.Combine(logDir, "claude-stream.jsonl"),
            TranscriptLogPath = Path.Combine(logDir, "transcript.md")
        };

        PromptResult result = await registry.Resolve(task.Action.Runner ?? promptFile.Frontmatter.Runner)
            .RunAsync(invocation, cancellationToken).ConfigureAwait(false);

        // Promote the staged fragment to its documented final location THE INSTANT the sub-agent
        // process exits — strictly before anything below reads fragmentOutPath (SSOT §9.5).
        PromptOutputStaging.PromoteAndCleanup(stagingStateOutPath, fragmentOutPath);

        // A prompt action's fragment may carry the needsHuman escape (SSOT §9) or a needsHarnessWrite
        // request (SSOT §9, issue #191) — both read from the same already-written fragment file.
        NeedsHumanSignal? needsHuman = ParseNeedsHuman(fragmentOutPath);
        HarnessWriteBatch? harnessWrite = HarnessWrite.RequestFrom(fragmentOutPath);

        return ActionRun.FromPrompt(result, needsHuman, harnessWrite);
    }

    /// <summary>
    /// The per-task reply-channel injection file (doc 12 §7.4/§7.6): the raw human-answer / best-guess text the
    /// Scheduler stages for the NEXT attempt. Lives at the TASK log level (<c>logs/&lt;runId&gt;/&lt;taskId&gt;/</c>),
    /// one directory above an attempt dir, so it is found regardless of the next attempt number. The literal is
    /// the coupling with <see cref="Scheduler"/>'s writer — kept identical there.
    /// </summary>
    private const string InjectedAnswerFileName = "injected-human-answer.txt";

    /// <summary>
    /// Read (and CONSUME once) the per-task <see cref="InjectedAnswerFileName"/> the Scheduler drops beside the
    /// task's log dir when a resume consumed a firstmate answer (§7.6) or a below-threshold best-guess was
    /// recorded (§4.1) for this unit. The value is threaded into this attempt's composed prompt via
    /// <see cref="PromptComposer.ComposeAction"/>'s <c>injectedHumanAnswer</c> section (which wraps it in the
    /// delimited UNTRUSTED envelope). It is DELETED after reading so it is injected into exactly ONE attempt
    /// (consume-once) and never leaks into a later, unrelated one. Absent ⇒ null (the common path).
    /// </summary>
    private static string? ReadAndConsumeInjectedAnswer(string logDir)
    {
        if (Path.GetDirectoryName(logDir) is not { } taskLogDir)
        {
            return null;
        }

        string injectionPath = Path.Combine(taskLogDir, InjectedAnswerFileName);
        if (!File.Exists(injectionPath))
        {
            return null;
        }

        try
        {
            string text = File.ReadAllText(injectionPath);
            File.Delete(injectionPath);
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (IOException)
        {
            // A best-effort reply-channel read must never abort the attempt — treat an unreadable file as absent.
            return null;
        }
    }

    /// <summary>
    /// Read the (already-written) action fragment and parse a <c>needsHuman</c> escape (SSOT §9), in EITHER
    /// shape (issue #387):
    /// <list type="bullet">
    ///   <item><b>Free-text (back-compat):</b> <c>{"needsHuman": "&lt;question&gt;"}</c> — the string is the
    ///     question, with no options.</item>
    ///   <item><b>Structured (enumerated decision):</b>
    ///     <c>{"needsHuman": {"question": "…", "options": ["A","B"]}}</c> — the <c>question</c> plus a bounded
    ///     <c>options[]</c> the operator can PICK from (interactive SelectionPrompt / web button), instead of
    ///     hand-authoring a reply. Only string <c>options</c> entries are kept; a missing/empty array yields no
    ///     options (behaviourally the free-text form).</item>
    /// </list>
    /// Returns null (no short-circuit) for any other shape — no <c>needsHuman</c> key, a structured object with
    /// no string <c>question</c>, or unparseable JSON (the merge step rejects a malformed fragment later).
    /// </summary>
    private static NeedsHumanSignal? ParseNeedsHuman(string fragmentOutPath)
    {
        if (!File.Exists(fragmentOutPath))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(fragmentOutPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("needsHuman", out JsonElement needsHuman))
            {
                return null;
            }

            // Free-text form: needsHuman is a bare string (the question); no options.
            if (needsHuman.ValueKind == JsonValueKind.String)
            {
                return needsHuman.GetString() is { Length: > 0 } q ? new NeedsHumanSignal(q, []) : null;
            }

            // Structured form: needsHuman is an object carrying the question + an optional bounded options[].
            if (needsHuman.ValueKind == JsonValueKind.Object &&
                needsHuman.TryGetProperty("question", out JsonElement question) &&
                question.ValueKind == JsonValueKind.String &&
                question.GetString() is { Length: > 0 } structuredQuestion)
            {
                return new NeedsHumanSignal(structuredQuestion, ReadOptions(needsHuman));
            }
        }
        catch (JsonException)
        {
            // Not parseable JSON → not a needsHuman signal; the merge step will reject it later.
        }

        return null;
    }

    /// <summary>Read the structured <c>needsHuman.options</c> array — the string entries only, in order; empty when absent / not an array.</summary>
    private static IReadOnlyList<string> ReadOptions(JsonElement needsHuman)
    {
        if (!needsHuman.TryGetProperty("options", out JsonElement options) ||
            options.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<string>();
        foreach (JsonElement option in options.EnumerateArray())
        {
            if (option.ValueKind == JsonValueKind.String && option.GetString() is { Length: > 0 } value)
            {
                list.Add(value);
            }
        }

        return list;
    }
}

/// <summary>
/// A parsed <c>needsHuman</c> escape (SSOT §9, issue #387): the human-answerable <see cref="Question"/> plus an
/// optional bounded <see cref="Options"/> list an operator can PICK from. <see cref="Options"/> is empty for the
/// free-text form (<c>{"needsHuman": "…"}</c>) and carries the enumerated choices for the structured form.
/// </summary>
internal sealed record NeedsHumanSignal(string Question, IReadOnlyList<string> Options);

/// <summary>
/// A normalized view of an action run — script OR prompt — carrying exactly what the
/// attempt loop needs. Scripts map their exit code and timeout directly; prompts map
/// <c>Completed &amp;&amp; !is_error</c> to success (SSOT §9), with cost and the needsHuman escape.
/// </summary>
internal sealed record ActionRun
{
    public required bool Succeeded { get; init; }
    public required int? ExitCode { get; init; }
    public required bool TimedOut { get; init; }
    public decimal? CostUsd { get; init; }
    public string? NeedsHumanQuestion { get; init; }

    /// <summary>
    /// The bounded, enumerated options a structured <c>needsHuman</c> carried (issue #387,
    /// <c>{"needsHuman": {"question": …, "options": […]}}</c>), in order. Empty for the free-text form and for
    /// any non-needsHuman action. Rides into the escalation record so a resume + both pick surfaces (interactive
    /// SelectionPrompt / web button) can present the choices.
    /// </summary>
    public IReadOnlyList<string> NeedsHumanOptions { get; init; } = [];

    /// <summary>
    /// The <c>needsHarnessWrite</c> batch parsed from the action's fragment (issues #191, #445, SSOT §9),
    /// or null when none was present. One or more per-file entries, applied atomically. Non-null on
    /// EITHER a script or a prompt action — the fragment file is read the same way regardless of action
    /// kind, mirroring <see cref="NeedsHumanQuestion"/>.
    /// </summary>
    public HarnessWriteBatch? HarnessWriteBatch { get; init; }

    public string? FailureFeedback { get; init; }
    public string FailureSummary { get; init; } = "action failed";

    /// <summary>
    /// The runner-agnostic classification of a prompt action's failure (SSOT §9, issues #114/#115/#119).
    /// <see cref="PromptFailureKind.None"/> for a script action or a succeeded prompt. The
    /// <see cref="TaskExecutor"/> routes on this: <see cref="PromptFailureKind.Transient"/> pauses
    /// without consuming the retry budget; the others compose signal-specific feedback.
    /// </summary>
    public PromptFailureKind FailureKind { get; init; } = PromptFailureKind.None;

    /// <summary>An advisory rate-limit reset hint to surface in the pause notice (issue #115), or null.</summary>
    public string? ResetHint { get; init; }

    /// <summary>
    /// The distinct write/edit paths the runtime refused this attempt because they are not granted
    /// (issues #86 / #104), in first-seen order. Empty for a script action or a prompt that hit no
    /// permission wall. The <see cref="TaskExecutor"/> feeds these to <see cref="PermissionWallTracker"/>
    /// to decide an early <c>needs-human</c> halt instead of burning the remaining retries.
    /// </summary>
    public IReadOnlyList<string> BlockedWritePaths { get; init; } = [];

    // The action's captured streams. A SCRIPT action carries its real stdout/stderr so the harness
    // can write them to action-stdout.log / action-stderr.log (GUARDRAILS_ACTION_STDOUT/_STDERR,
    // issue #62) and surface stderr in action-failure feedback. A PROMPT action leaves these empty —
    // its "stdout" is the stream-json teed to claude-stream.jsonl, not a plain stream.
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;

    // For log artifacts (action-result.json + action-stdout/stderr.log) we reuse the ProcessResult
    // shape; prompt actions synthesize an exit code (0/1) reflecting success and carry no plain streams.
    public ProcessResult AsProcessResult() => new()
    {
        ExitCode = ExitCode ?? (Succeeded ? 0 : 1),
        StandardOutput = StandardOutput,
        StandardError = StandardError,
        TimedOut = TimedOut,
        Duration = TimeSpan.Zero
    };

    public static ActionRun FromScript(ProcessResult result, NeedsHumanSignal? needsHuman, HarnessWriteBatch? harnessWrite = null) => new()
    {
        Succeeded = result.Succeeded,
        ExitCode = result.ExitCode,
        TimedOut = result.TimedOut,
        StandardOutput = result.StandardOutput,
        StandardError = result.StandardError,
        NeedsHumanQuestion = needsHuman?.Question,
        NeedsHumanOptions = needsHuman?.Options ?? [],
        HarnessWriteBatch = harnessWrite,
        // A script timeout is classified Timeout so it shares the timeout-specific retry handling
        // (issue #119); any other non-zero exit is a generic action failure (no Claude signals apply).
        FailureKind = result.TimedOut ? PromptFailureKind.Timeout
            : result.Succeeded ? PromptFailureKind.None
            : PromptFailureKind.Error,
        FailureSummary = result.TimedOut ? "action timed out" : $"action exited {result.ExitCode}"
    };

    public static ActionRun FromPrompt(PromptResult result, NeedsHumanSignal? needsHuman, HarnessWriteBatch? harnessWrite = null)
    {
        bool succeeded = result.Completed && !result.IsError;
        string? feedback = succeeded ? null : BuildPromptFeedback(result);
        return new ActionRun
        {
            Succeeded = succeeded,
            // Synthesize an exit code for the journal: 0 on success, 1 otherwise.
            ExitCode = succeeded ? 0 : 1,
            TimedOut = result.FailureKind == PromptFailureKind.Timeout,
            CostUsd = result.CostUsd,
            NeedsHumanQuestion = needsHuman?.Question,
            NeedsHumanOptions = needsHuman?.Options ?? [],
            HarnessWriteBatch = harnessWrite,
            FailureFeedback = feedback,
            FailureKind = succeeded ? PromptFailureKind.None : result.FailureKind,
            ResetHint = result.ResetHint,
            BlockedWritePaths = result.BlockedWritePaths,
            FailureSummary = result.Summary
        };
    }

    private static string BuildPromptFeedback(PromptResult result)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine("# Prompt action did not succeed");
        text.AppendLine();
        text.AppendLine(result.Completed
            ? "The runner completed but reported an error (is_error = true)."
            : $"The runner did not complete cleanly: {result.Summary}.");
        text.AppendLine();
        if (!string.IsNullOrWhiteSpace(result.ResultText))
        {
            text.AppendLine("## Runner result (tail)");
            text.AppendLine("```");
            string tail = result.ResultText!.Length > 2000 ? result.ResultText[^2000..] : result.ResultText;
            text.AppendLine(tail.TrimEnd());
            text.AppendLine("```");
        }

        text.AppendLine();
        text.AppendLine("Fix the specific problem above on retry; do not start over.");
        return text.ToString();
    }
}
