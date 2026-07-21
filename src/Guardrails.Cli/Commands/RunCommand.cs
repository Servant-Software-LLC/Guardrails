using System.CommandLine;
using System.Text.Json;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.State;
using Spectre.Console;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails run [folder] [--fresh] [--no-ui]</c> — validate then execute the plan
/// DAG (parallel, retry-aware, resume-aware). <c>--fresh</c> wipes runtime state first
/// (SSOT §6.1). Live Spectre progress when interactive; plain lines otherwise. Exit codes
/// per SSOT §7: 0 green, 1 error, 2 needs-human/failed, 3 cancelled. Defaults to the
/// current directory when the folder is omitted.
/// </summary>
public static class RunCommand
{
    public static Command Create(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        var freshOption = new Option<bool>("--fresh")
        {
            Description = "Delete runtime state (run.json, state.json, logs), tear down the plan branch and all worktrees, then re-seed before running."
        };

        var noUiOption = new Option<bool>("--no-ui")
        {
            Description = "Plain line-by-line output instead of the live progress table."
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Validate and preview tiers + per-task resolution + resume skips, then exit 0 without running or touching state."
        };

        var noLogServerOption = new Option<bool>("--no-log-server")
        {
            Description = "Do not start the local log server / clickable per-task log links (for headless or CI use)."
        };

        var logPortOption = new Option<int>("--log-port")
        {
            Description = "Port for the local log server (default 0 = an automatically chosen free port). Bound to localhost only."
        };

        var mergeOnSuccessOption = new Option<bool>("--merge-on-success")
        {
            Description = "On a wholly-green run, merge the plan branch into your original branch at run end (SSOT §5.3). Forces mergeOnSuccess ON regardless of guardrails.json (delivery is now the DEFAULT — this only matters to override a config 'mergeOnSuccess: false')."
        };

        var noMergeOnSuccessOption = new Option<bool>("--no-merge-on-success")
        {
            Description = "Suppress the end-of-run delivery: leave the wholly-green work on the plan branch guardrails/<plan-name> for manual review/merge. Forces mergeOnSuccess OFF regardless of guardrails.json (#340). Contradictory with --merge-on-success."
        };

        var autonomyOption = new Option<string?>("--autonomy")
        {
            Description = "Set the unified autonomy policy for this run (SSOT §2.1): 'prompt' (default; interactive confirm, else halt), 'halt' (always halt), or 'auto' (apply a SAFE decision with no prompt). Overrides guardrails.json. An UNSAFE action still halts regardless."
        };

        var reprocessDriftOption = new Option<bool>("--reprocess-drift")
        {
            Description = "Legacy alias for --autonomy auto: on a resume with a PROVABLY-SAFE definition drift, auto-resolve it with no prompt — rewind the plan branch past the safe drifted suffix and re-run it (SSOT §7.2). An UNSAFE drift still halts."
        };

        var revalidateTaskOption = new Option<string?>("--revalidate-task")
        {
            Description = "Re-validate-only (issue #102): run ONLY this task's guardrails against the current workspace, spawning NO agent attempt — for confirming a hand-fix to a needs-human task. On pass the task is marked succeeded; serial mode only."
        };

        var skipReviewCheckOption = new Option<bool>("--skip-review-check")
        {
            Description = "Suppress the warning when the plan hasn't been through /guardrails-review (or has changed since) (SSOT §13, issue #79)."
        };

        var command = new Command("run", "Run a plan folder's task DAG to green (parallel; resume-aware).");
        command.Add(folderArgument);
        command.Add(freshOption);
        command.Add(noUiOption);
        command.Add(dryRunOption);
        command.Add(noLogServerOption);
        command.Add(logPortOption);
        command.Add(mergeOnSuccessOption);
        command.Add(noMergeOnSuccessOption);
        command.Add(autonomyOption);
        command.Add(reprocessDriftOption);
        command.Add(revalidateTaskOption);
        command.Add(skipReviewCheckOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out);
            bool fresh = parseResult.GetValue(freshOption);
            bool noUi = parseResult.GetValue(noUiOption);
            bool dryRun = parseResult.GetValue(dryRunOption);
            bool noLogServer = parseResult.GetValue(noLogServerOption);
            int logPort = parseResult.GetValue(logPortOption);
            bool mergeOnSuccess = parseResult.GetValue(mergeOnSuccessOption);
            bool noMergeOnSuccess = parseResult.GetValue(noMergeOnSuccessOption);
            string? autonomy = parseResult.GetValue(autonomyOption);
            bool reprocessDrift = parseResult.GetValue(reprocessDriftOption);
            string? revalidateTask = parseResult.GetValue(revalidateTaskOption);
            bool skipReviewCheck = parseResult.GetValue(skipReviewCheckOption);

            // #340 delivery tri-state: --merge-on-success forces ON, --no-merge-on-success forces OFF,
            // neither leaves it to guardrails.json (which itself now defaults ON). Passing BOTH is a
            // contradictory usage error. The resolved override is null (no flag → use config/default),
            // true, or false; precedence is CLI flag → guardrails.json → the true default.
            if (mergeOnSuccess && noMergeOnSuccess)
            {
                io.Out.WriteLine("--merge-on-success and --no-merge-on-success are contradictory; pass at most one.");
                return ExitCodes.HarnessError;
            }

            bool? mergeOnSuccessOverride = mergeOnSuccess ? true : noMergeOnSuccess ? false : null;

            // Re-validate-only (issue #102) is a single-task verification, not a run: it spawns no
            // agent attempt and ignores the run-shaped flags. Reject the combinations that would
            // otherwise silently no-op (or, for --fresh, destroy the very state being verified).
            if (!string.IsNullOrWhiteSpace(revalidateTask))
            {
                if (fresh || dryRun)
                {
                    io.Out.WriteLine("--revalidate-task cannot be combined with --fresh or --dry-run.");
                    return ExitCodes.HarnessError;
                }

                return await Revalidate.ExecuteAsync(folder, revalidateTask, io, cancellationToken).ConfigureAwait(false);
            }

            if (dryRun)
            {
                return DryRun.Execute(folder, io, skipReviewCheck);
            }

            return await RunAsync(folder, fresh, noUi, noLogServer, logPort, mergeOnSuccessOverride, autonomy, reprocessDrift, skipReviewCheck, io, cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    private static async Task<int> RunAsync(
        string folder, bool fresh, bool noUi, bool noLogServer, int logPort, bool? mergeOnSuccessOverride, string? autonomy, bool reprocessDrift, bool skipReviewCheck, IConsoleIo io, CancellationToken cancellationToken)
    {
        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, io.Out);
            io.Out.WriteLine("\nValidation failed; nothing was run.");
            return ExitCodes.HarnessError;
        }

        // Multi-wave EXECUTION (M2b, SSOT §14): a WAVED plan runs wave-by-wave behind hard barriers against
        // ONE continuous integration worktree + journal + plan branch (the Scheduler's RunWavedAsync). The
        // plan-level Full Flight Checks (<plan>/preflights/) and Terminal Gate (<plan>/guardrails/) below
        // wrap the whole waved run unchanged; the per-wave entry/exit gates + the barrier live in the
        // Scheduler. Print a one-line wave banner so an operator sees the shape.
        if (probe.Plan.IsWaved)
        {
            io.Out.WriteLine(
                $"'{Path.GetFileName(probe.Plan.PlanDirectory)}' is a WAVED plan — {probe.Plan.Waves.Count} wave(s) in strict order: " +
                $"{string.Join(", ", probe.Plan.Waves.Select(w => w.Dir))} (SSOT §14).");
            io.Out.WriteLine();
        }

        // Review-marker nudge (warn, never block — SSOT §13, issue #79): if the plan hasn't been
        // through /guardrails-review (or has changed since), print a one-line warning before running,
        // unless --skip-review-check. Reuses the same deterministic evaluation as `validate`.
        WarnIfUnreviewed(probe.Plan, skipReviewCheck, io);

        // #340 delivery tri-state (SSOT §2/§5.3, precedence: CLI flag → guardrails.json → the true
        // default). --merge-on-success forces ON, --no-merge-on-success forces OFF; neither leaves the
        // config-resolved value (which itself now defaults ON). Whether the effective value came PURELY
        // from the default (no flag AND no config key) is captured HERE, before the override is applied,
        // so the end-of-run notice can distinguish "delivered because of the new default" from an explicit
        // opt-in — the config's raw key-presence lives on MergeOnSuccessExplicit (null = omitted).
        bool deliveryFromDefaultOnly =
            mergeOnSuccessOverride is null && probe.Plan.Config.MergeOnSuccessExplicit is null;

        if (mergeOnSuccessOverride is { } mergeForced && probe.Plan.Config.MergeOnSuccess != mergeForced)
        {
            probe = probe with { Plan = probe.Plan with { Config = probe.Plan.Config with { MergeOnSuccess = mergeForced } } };
        }

        // --autonomy <value> sets the unified autonomy policy for this run (SSOT §2.1), overriding
        // guardrails.json; --reprocess-drift is its legacy alias for `auto`. Parse --autonomy first, then let
        // --reprocess-drift force `auto` (so the two agree; if a conflicting --autonomy and --reprocess-drift
        // are BOTH given, --reprocess-drift's explicit auto-intent wins). An UNSAFE action still halts.
        Core.Model.AutonomyPolicy? autonomyOverride = null;
        if (!string.IsNullOrWhiteSpace(autonomy))
        {
            if (!Core.Model.AutonomyPolicies.TryParse(autonomy, out Core.Model.AutonomyPolicy parsed))
            {
                io.Out.WriteLine($"Unknown --autonomy value '{autonomy}'. Expected 'prompt', 'halt', or 'auto' (SSOT §2.1).");
                return ExitCodes.HarnessError;
            }

            autonomyOverride = parsed;
        }

        if (reprocessDrift)
        {
            autonomyOverride = Core.Model.AutonomyPolicy.Auto;
        }

        if (autonomyOverride is { } policy && probe.Plan.Config.AutonomyPolicy != policy)
        {
            probe = probe with { Plan = probe.Plan with { Config = probe.Plan.Config with { AutonomyPolicy = policy } } };
        }

        if (fresh)
        {
            RunReset.Fresh(probe.Plan.PlanDirectory);
            io.Out.WriteLine(
                "Fresh run: runtime state cleared, the plan branch and all worktrees were torn down, "
                + "and state was re-seeded from your current HEAD.\n");
        }

        bool live = !noUi && AnsiConsole.Profile.Capabilities.Interactive && !Console.IsOutputRedirected;

        // Resolve the run's id up-front so the live log server and the post-mortem links target the
        // correct logs/<runId>/ tree (SSOT §8/§12). LoadOrCreate is idempotent: it creates run.json
        // here (or reads it on resume), and the Scheduler's own LoadOrCreate then reads the SAME
        // run.json — so this runId matches the one the executor writes attempt logs under.
        RunJournal journal = RunJournal.LoadOrCreate(probe.Plan);
        string runId = journal.Document.RunId;

        // #383 run-start path-length preflight (SSOT §2, GR2038). In WORKTREE mode on WINDOWS, refuse to
        // start when a task's segment worktree + its build output would exceed MAX_PATH (260): each task
        // builds under <root>/<runId>/<taskId>/attempt-N, and a built test-exe measured 264 chars broke
        // CreateProcessW with Win32 206 (LongPathsEnabled does NOT help). This is the AUTHORITATIVE check —
        // it depends on the machine's ACTUAL worktree root (the GUARDRAILS_WORKTREE_ROOT-aware
        // WorktreeRootFor), so it lives here at run start rather than in `guardrails validate`. Windows +
        // worktree-mode only: the IsWindows() short-circuit keeps non-Windows from ever spawning the git
        // probe WouldUseWorktreeMode runs, and serial / non-worktree mode never hits per-segment paths.
        if (OperatingSystem.IsWindows() && SchedulerFactory.WouldUseWorktreeMode(probe.Plan))
        {
            Diagnostic? pathHalt = WorktreePathPreflight.Check(
                SchedulerFactory.WorktreeRootFor(probe.Plan), runId, probe.Plan.Tasks.Select(t => t.Id));
            if (pathHalt is not null)
            {
                PlanProbe.PrintDiagnostics([pathHalt], io.Out);
                io.Out.WriteLine("\nWindows MAX_PATH preflight FAILED; nothing was run.");
                return ExitCodes.HarnessError;
            }
        }

        // Pre-DAG plan-preflight phase (SSOT §7, deliverable 3): evaluate <plan>/preflights/ ONCE,
        // BEFORE the Scheduler builds any wave, against the run's starting bytes. A red preflight halts
        // HERE — no task runs, zero tokens spent — journaled as planPreflights.status =
        // plan-preflight-failed (a top-level section OUTSIDE tasks{}). A passed marker whose planHash
        // still matches the current plan is SKIPPED on resume rather than re-evaluated (the B1 fix).
        // Issue #240: this phase (and the terminal one below) previously ran entirely silently on
        // success — no row in the live table (its lifetime doesn't even span these phases, #240's own
        // investigation), no console line at all. Bracket with plain WriteLines, guarded on the plan
        // actually declaring this folder (EvaluateAsync itself no-ops for free when it doesn't, so
        // printing "running..." then would be misleading noise for a plan that never opted in).
        bool hasPlanPreflights = probe.Plan.PlanPreflights.Count > 0;
        if (hasPlanPreflights)
        {
            io.Out.WriteLine("Full Flight Checks: running...");
        }

        bool preflightsPassed = await PlanPreflightPhase
            .EvaluateAsync(probe.Plan, journal, new ProcessRunner(), io.Out, cancellationToken)
            .ConfigureAwait(false);

        if (hasPlanPreflights && preflightsPassed)
        {
            io.Out.WriteLine("Full Flight Checks: passed.");
        }

        if (!preflightsPassed)
        {
            io.Out.WriteLine();
            io.Out.WriteLine("Plan preflight FAILED — halting before scheduling any task (SSOT §7 planPreflights).");
            io.Out.WriteLine($"  See {RunJournal.PathFor(probe.Plan.PlanDirectory)} (\"planPreflights\") for the failed check(s).");
            return ExitCodes.TaskFailed;
        }

        // Part C interactive drift confirm (SSOT §2.1/§7.2, issue #274). The default autonomyPolicy is
        // "prompt": a PROVABLY-SAFE drift must ask the operator BEFORE the run. The Spectre live table cannot
        // host a Console.ReadLine, so the prompt happens HERE — before any UI — via the same
        // Console.IsInputRedirected idiom as ResetCommand.Confirm. A `y` becomes driftPreConfirmed (the
        // Scheduler then rewinds + re-runs); every other case (no drift, unsafe, non-interactive) falls
        // through to the Scheduler, which halts or auto-resolves exactly as the policy dictates and renders
        // the authoritative report. --autonomy auto (or --reprocess-drift) / autonomyPolicy:halt skip the
        // prompt entirely.
        DriftAuthorization? driftAuthorization = null;
        if (probe.Plan.Config.AutonomyPolicy == Core.Model.AutonomyPolicy.Prompt)
        {
            (DriftPromptDecision decision, DriftAuthorization? authorized) =
                ConfirmSafeDriftIfInteractive(probe.Plan, journal, io);
            if (decision == DriftPromptDecision.Declined)
            {
                return ExitCodes.TaskFailed; // operator answered N — halt without running (they saw the preview).
            }

            driftAuthorization = authorized; // non-null only on a `y`; carries the CAPTURED plan (S + target + tip)
        }

        // Wave-drift interactive confirm (SSOT §14.6, #254 M2b): a COMPLETED wave whose WaveDefinitionHash
        // changed since it last completed. Under the default "prompt" policy the Scheduler cannot prompt (it
        // never touches the console), so — mirroring the task-drift confirm above — the CLI detects it BEFORE
        // any UI and, in an interactive TTY, asks; a `y` pre-authorizes rewinding that wave (+ downstream),
        // passed to the Scheduler as the authorized wave-dir set. Non-interactive / declined halts (the
        // Scheduler renders the authoritative WaveHalt). --autonomy auto resolves without a prompt; halt halts.
        IReadOnlySet<string>? waveDriftAuthorized = null;
        if (probe.Plan.IsWaved && probe.Plan.Config.AutonomyPolicy == Core.Model.AutonomyPolicy.Prompt)
        {
            (bool declined, IReadOnlySet<string>? authorizedWaves) =
                ConfirmWaveDriftIfInteractive(probe.Plan, journal, io);
            if (declined)
            {
                return ExitCodes.TaskFailed; // operator answered N — halt without running.
            }

            waveDriftAuthorized = authorizedWaves;
        }

        // #360 between-wave breakdown confirm. With the DEFAULT autoBreakdown (SSOT §14.4/§14.10) the JIT
        // checkpoint auto-invokes plan-breakdown with NO prompt regardless of autonomyPolicy, so no
        // confirmation is captured here. Only the LEGACY autoBreakdown:false + "prompt"-policy path prompts:
        // the Scheduler cannot prompt (it never touches the console, and the checkpoint fires INSIDE the
        // Spectre live region — #145 Bug 1), so — mirroring the wave-drift confirm — the CLI detects the
        // upcoming unauthored-wave checkpoint BEFORE any UI and asks y/N; the answers are passed to the
        // Scheduler. Non-interactive → no confirmation → honest-halt. "auto" needs no confirmation (it invokes
        // unconditionally); "halt" never invokes.
        IReadOnlyDictionary<string, bool>? breakdownConfirmations = null;
        if (probe.Plan.IsWaved && !probe.Plan.Config.AutoBreakdown
            && probe.Plan.Config.AutonomyPolicy == Core.Model.AutonomyPolicy.Prompt)
        {
            breakdownConfirmations = ConfirmWaveBreakdownIfInteractive(probe.Plan, journal, io);
        }

        // The log server is a companion to the live table: start it only in the interactive path
        // (nobody clicks links in CI / redirected output), and never let a binding failure abort
        // the run — TryStart returns null and prints one warning.
        LogServer? logServer = (live && !noLogServer)
            ? LogServer.TryStart(probe.Plan.PlanDirectory, runId, probe.Plan.Tasks, logPort, io.Out)
            : null;

        try
        {
            if (logServer is not null)
            {
                // The canonical "all tasks" page is the static index file (printed by
                // PrintStaticIndexLink below); this http server is just the tailing backend that the
                // static index links a RUNNING task to (issue #143). De-emphasised accordingly.
                io.Out.WriteLine($"Live tailing server (active tasks): {logServer.BaseUrl}\n");
            }

            Func<string, string?>? logUrlForTask = logServer is null ? null : logServer.UrlForTask;

            // The on-the-fly static site (issue #141 item 2) is written for BOTH the live and the
            // --no-ui paths — a file:// "all tasks" page that updates as tasks settle, useful headless
            // or interactive. It lives under logs/<runId>/, the same tree the executor writes attempts
            // into and the live server serves. The inner observer (live table or plain console) is
            // wrapped so the site is rewritten after each forwarded event.
            string logsRoot = Path.Combine(probe.Plan.PlanDirectory, "logs", runId);

            // Seed the live status diagram (issue #219, SSOT §10.1) from the freshly-persisted journal so
            // a resume — and the already-settled Full Flight Checks phase above (which runs before this
            // observer exists) — shows correct badges from the first frame; a fresh run seeds nothing
            // (every node pending until an event fires).
            JournalDocument? diagramSeed = TryReadJournalForSeed(probe.Plan.PlanDirectory);

            RunReport report;
            OnTheFlyDiagramObserver diagramObserver;
            if (live)
            {
                // Write the initial all-pending index + the seeded live diagram AND print their links
                // BEFORE constructing LiveRunObserver — its ctor starts the Spectre AnsiConsole.Live
                // region, and any console write into an active Live region corrupts the table (#145 Bug 1).
                // So both static writes + their links must precede the live region.
                OnTheFlyLogSiteObserver.WriteInitialIndex(logsRoot, runId, probe.Plan.Tasks, logUrlForTask);
                PrintStaticIndexLink(logsRoot, io);    // "all tasks" page link at run START
                OnTheFlyDiagramObserver.WriteInitialDiagram(logsRoot, probe.Plan, diagramSeed);
                PrintDiagramLink(logsRoot, io);        // live status diagram link at run START

                await using var liveObserver = new LiveRunObserver(probe.Plan.Tasks, logUrlForTask, probe.Plan.PlanDirectory, runId);
                var siteObserver = new OnTheFlyLogSiteObserver(liveObserver, logsRoot, runId, probe.Plan.Tasks, logUrlForTask);
                // Stack the diagram observer AROUND the log-site observer: it forwards every event down
                // the chain and re-renders logs/<runId>/diagram.html after each.
                diagramObserver = new OnTheFlyDiagramObserver(siteObserver, logsRoot, probe.Plan, diagramSeed);
                report = await ExecuteAsync(probe.Plan, diagramObserver, driftAuthorization, waveDriftAuthorized, breakdownConfirmations, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var siteObserver = new OnTheFlyLogSiteObserver(
                    new ConsoleRunObserver(io.Out), logsRoot, runId, probe.Plan.Tasks, logUrlForTask);
                diagramObserver = new OnTheFlyDiagramObserver(siteObserver, logsRoot, probe.Plan, diagramSeed);
                siteObserver.WriteInitialIndex();
                PrintStaticIndexLink(logsRoot, io);
                diagramObserver.WriteInitialDiagram();
                PrintDiagramLink(logsRoot, io);
                report = await ExecuteAsync(probe.Plan, diagramObserver, driftAuthorization, waveDriftAuthorized, breakdownConfirmations, cancellationToken).ConfigureAwait(false);
            }

            // Terminal plan-guardrail phase (SSOT §7/§7.1, deliverable 4): evaluate <plan>/guardrails/
            // ONCE, AFTER the DAG drains wholly green, against the merged plan-branch HEAD — replacing
            // the retired integrationGate task-kind's terminal role (Scheduler.cs skips that legacy path
            // whenever the plan declares this folder, SSOT §3.3). No-op (true) when the DAG did not
            // fully succeed this run/resume, or the plan has no <plan>/guardrails/ folder at all.
            // B2(b) terminal-only resume falls out for free: a resume where every task is already
            // succeeded drains the DAG with nothing left to do (report.AllSucceeded stays true, no
            // attempt burned), so this phase unconditionally re-fires against the current HEAD.
            // Issue #240: same silent-on-success gap as Full Flight Checks above. This phase is only
            // ever actually invoked when the DAG settled green AND the plan declares this folder (the
            // `!report.AllSucceeded ||` short-circuit means EvaluateAsync is never called at all
            // otherwise) — gate the bracketing lines on exactly that, or "Terminal Gate: running..."
            // would misleadingly print for a run that failed before ever reaching this phase.
            // Issue #333: the terminal-gate phase and the two end-of-run final-static writes are wrapped so
            // that an UNEXPECTED throw from PlanGuardrailPhase.EvaluateAsync (anything that is NOT a
            // #150-converted abort — it runs OUTSIDE the Scheduler, so an infra fault here propagates raw)
            // still settles BOTH final pages. Without this, a throw skips WriteFinalStatic + the durable
            // final log-site write, leaving logs/<runId>/diagram.html <meta refresh>-ing with the Terminal
            // Gate badge frozen on a spinner and the log index stuck in its during-run (refreshing) state.
            bool finalSitesSettled = false;
            try
            {
                bool hasPlanGuardrails = probe.Plan.PlanGuardrails.Count > 0;
                bool willEvaluateTerminalGate = report.AllSucceeded && hasPlanGuardrails;
                if (willEvaluateTerminalGate)
                {
                    io.Out.WriteLine("Terminal Gate: running...");
                    diagramObserver.PlanGuardrailsStarting(); // bracket-container spinner (issue #219)
                }

                bool planGuardrailsPassed = !report.AllSucceeded
                    || await PlanGuardrailPhase.EvaluateAsync(probe.Plan, new ProcessRunner(), io.Out, cancellationToken)
                        .ConfigureAwait(false);

                if (willEvaluateTerminalGate)
                {
                    diagramObserver.PlanGuardrailsFinished(planGuardrailsPassed); // settle the bracket badge
                    if (planGuardrailsPassed)
                    {
                        io.Out.WriteLine("Terminal Gate: passed.");
                    }
                }

                // The FINAL, settled live diagram (no meta refresh, no spinner) — the durable post-mortem of
                // the run, sourced from the observer's own in-memory map, mirroring the durable final log site
                // Finish writes. Best-effort; never changes the exit code (issue #219, SSOT §10.1).
                diagramObserver.WriteFinalStatic();

                int exitCode = Finish(report, probe.Plan, runId, io); // also writes the durable final log site
                finalSitesSettled = true; // both final pages are now settled on the normal path

                if (report.AllSucceeded && !planGuardrailsPassed)
                {
                    PrintTerminalGateFailure(probe.Plan.PlanDirectory, io);
                    return ExitCodes.TaskFailed;
                }

                // Issue #340: a WHOLLY-GREEN run (the DAG green AND the terminal gate passed) whose
                // verified work was NOT delivered — mergeOnSuccess resolved off — must be impossible to
                // miss. The plan branch alone carries the work, one --fresh/reset -y away from destruction.
                RenderUndeliveredWorkWarning(report, planGuardrailsPassed, probe.Plan.PlanDirectory, io.Out);

                // Issue #340 complement: when delivery fired PURELY because of the new default (neither the
                // config key nor a CLI flag was set), print a one-time notice naming the branch + the opt-out,
                // so the breaking default is observable/self-documenting rather than a silent surprise. Never
                // fires together with the undelivered warning (that requires delivery OFF; this requires it ran).
                RenderDeliveredByDefaultNotice(report, deliveryFromDefaultOnly, io.Out);

                return exitCode;
            }
            finally
            {
                // Issue #333: if the terminal-gate phase (or anything else after the run body) threw before
                // the normal-path settle above completed, still settle BOTH final static pages so the diagram
                // stops meta-refreshing with a frozen Terminal Gate spinner and the log index leaves its
                // during-run state. A no-op when the normal path already settled them. In a finally (not a
                // catch) so the original exception still propagates unchanged — the run verdict, exit code,
                // and state are untouched (SSOT §10.1: these are best-effort chrome).
                if (!finalSitesSettled)
                {
                    TrySettleFinalSitesAfterFault(diagramObserver, logsRoot, probe.Plan);
                }
            }
        }
        finally
        {
            if (logServer is not null)
            {
                await logServer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Settle BOTH end-of-run static pages best-effort after a fault interrupted the normal end-of-run path
    /// (issue #333): the live status diagram (<see cref="OnTheFlyDiagramObserver.WriteFinalStatic"/> — which
    /// drops the meta-refresh + spinner animation and settles any still-running node to an interrupted badge)
    /// and the durable, no-refresh log site (<see cref="WriteDurableFinalSite"/>). Invoked from a
    /// <c>finally</c>, so it MUST NOT throw: a settle-write hiccup (e.g. a corrupt journal) must never
    /// replace the original, more important exception, so every fault here is swallowed and the pages are
    /// left as they were. Public because the Cli assembly ships no InternalsVisibleTo (same rationale as
    /// <see cref="Hyperlink"/>).
    /// </summary>
    public static void TrySettleFinalSitesAfterFault(
        OnTheFlyDiagramObserver diagramObserver, string logsRoot, Core.Model.PlanDefinition plan)
    {
        try
        {
            diagramObserver.WriteFinalStatic();
            WriteDurableFinalSite(logsRoot, plan, plan.PlanDirectory);
        }
        catch (Exception)
        {
            // Best-effort settle inside a finally: swallow ALL so the original exception still propagates
            // (the individual writes are themselves best-effort; this is the belt-and-braces guarantee that
            // the settle can never mask the fault that brought us here).
        }
    }

    /// <summary>
    /// Print the review-marker nudge (GR2025, WARNING — SSOT §13, issue #79) when the plan is
    /// missing/stale a <c>/guardrails-review</c> marker, unless <paramref name="skipReviewCheck"/>.
    /// Warn, never block — the run proceeds regardless. Shared by <c>run</c> and <c>--dry-run</c>.
    /// </summary>
    public static void WarnIfUnreviewed(Core.Model.PlanDefinition plan, bool skipReviewCheck, IConsoleIo io)
    {
        if (skipReviewCheck)
        {
            return;
        }

        if (Core.Loading.PlanValidator.ReviewMarkerDiagnostic(plan) is { } nudge)
        {
            io.Out.WriteLine(nudge.ToString());
            io.Out.WriteLine();
        }
    }

    /// <summary>Print the summary and map the report to the process exit code (SSOT §7).</summary>
    private static int Finish(RunReport report, Core.Model.PlanDefinition plan, string runId, IConsoleIo io)
    {
        string planDirectory = plan.PlanDirectory;
        string logsRoot = Path.Combine(planDirectory, "logs", runId);

        // Issue #274 Part A — definition-drift halt (§7.2). A pre-DAG halt: nothing ran and no logs were
        // written, so render ONLY the itemized drift block and exit 2 (actionable/needs-human, matching
        // planPreflights/planGuardrails — NOT 1), skipping the normal per-task summary + logs pointer that
        // would otherwise list every task as a misleading "not started".
        if (report.DefinitionDrift is { } drift)
        {
            PrintDefinitionDrift(drift, planDirectory, io);
            return ExitCodes.TaskFailed;
        }

        // Write the DURABLE final site (issue #141 item 2): all-static links, NO meta-refresh, every
        // task page — so the artifact left on disk is complete and self-contained (identical to
        // `logs --export`). The during-run writer left a refreshing index with live links; this
        // replaces it. Best-effort: a render hiccup must never change the run's exit code.
        WriteDurableFinalSite(logsRoot, plan, planDirectory);

        PrintSummary(report, planDirectory, runId, io);

        // The "all tasks" static page link at run END (alongside the post-mortem logs pointer).
        PrintStaticIndexLink(logsRoot, io);

        // Issue #150 — honest halt for an infrastructure fault. The scheduler returned an ABORTED
        // report instead of throwing; render a one-line diagnostic + remedy as the headline, write
        // the FULL exception to the run logs (a dev tool keeps the detail, just not as the headline),
        // and exit non-zero — never a raw unhandled stack trace as the headline.
        if (report.Abort is { } abort)
        {
            WriteAbortDetailToLogs(logsRoot, abort);
            io.Out.WriteLine();
            io.Out.WriteLine($"RUN ABORTED: {abort.Headline}");
            io.Out.WriteLine($"  {abort.Remedy}");
            io.Out.WriteLine($"  Full fault detail written to {Path.GetFullPath(Path.Combine(logsRoot, "abort.log"))}");
            return ExitCodes.HarnessError;
        }

        // Multi-wave halt (SSOT §14, #254 M2b): a WAVED run stopped at a wave boundary — an unauthored next
        // wave (JIT checkpoint), a wave entry/exit gate failure, or a wave-drift under a halt/unconfirmed
        // policy. Rendered after the per-task summary (prior waves' tasks show green) and exits 2 (actionable).
        if (report.WaveHalt is { } waveHalt)
        {
            PrintWaveHalt(waveHalt, io);
            return ExitCodes.TaskFailed;
        }

        if (report.Cancelled)
        {
            return ExitCodes.Cancelled;
        }

        // Issue #150 — a wholly-green run whose end-of-run delivery to the user's branch was HALTED
        // (a git hook rejected the user-facing merge, a conflict, or a dirty user tree) is NOT a
        // clean success: the work is durable on the plan branch, but the user must act. Render the
        // actionable message and exit non-zero. A FastForwarded/Merged delivery, or no mergeOnSuccess
        // at all (null), leaves the success verdict untouched.
        if (report.AllSucceeded
            && report.MergeOnSuccessOutcome is { } mergeOutcome
            && mergeOutcome is not (MergeOnSuccessResult.FastForwarded or MergeOnSuccessResult.Merged))
        {
            PrintMergeOnSuccessHalt(report, plan, mergeOutcome, io);
            return ExitCodes.TaskFailed;
        }

        return report.AllSucceeded ? ExitCodes.Success : ExitCodes.TaskFailed;
    }

    /// <summary>
    /// Print the terminal plan-guardrail gate failure (D4). Read the failed checks (name + reason) that
    /// <see cref="PlanGuardrailPhase"/> journaled into <c>planGuardrails.failedChecks</c> and surface each
    /// one INLINE — so a terminal halt is as legible as the legacy per-task gate (which listed its failed
    /// guardrails in the summary), instead of a bare "see planGuardrails in run.json" pointer that forces
    /// the user to open the journal. Mirrors the shape of the NEEDS HUMAN block. Best-effort: a journal
    /// read hiccup falls back to the generic pointer rather than throwing (the exit code is unaffected).
    /// <para>
    /// It ALSO surfaces the #175 merge-collision hint (SSOT §3.3, issue #205) that
    /// <see cref="PlanGuardrailPhase"/> journals into <c>planGuardrails.collisionHint</c> when ≥2 tasks
    /// have overlapping <c>writeScope</c> on a shared file — the same attribution the legacy per-task gate
    /// carried in its summary, ported onto the terminal phase.
    /// </para>
    /// </summary>
    private static void PrintTerminalGateFailure(string planDirectory, IConsoleIo io)
    {
        TextWriter output = io.Out;
        string journalPath = RunJournal.PathFor(planDirectory);

        output.WriteLine();
        output.WriteLine("Plan guardrail gate FAILED on the merged HEAD — terminal halt (SSOT §7 planGuardrails).");

        PlanGuardrailsSection? section = TryReadPlanGuardrailSection(journalPath);
        IReadOnlyList<FailedGuardrail> failedChecks = section?.FailedChecks ?? [];
        if (failedChecks.Count > 0)
        {
            foreach (FailedGuardrail check in failedChecks)
            {
                // #272 Part 1: the reason now carries the TAIL of the guardrail's stdout (the re-emitted
                // failure detail), which may span multiple lines. Print the first line on the `FAILED:`
                // line and INDENT the continuation lines so the block stays legible instead of losing the
                // alignment at column 0.
                string[] reasonLines = (check.Reason ?? string.Empty)
                    .Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                output.WriteLine($"  FAILED: {check.Name} — {reasonLines[0]}");
                for (int i = 1; i < reasonLines.Length; i++)
                {
                    output.WriteLine($"          {reasonLines[i]}");
                }
            }
            output.WriteLine($"  (full detail in {journalPath} under \"planGuardrails\")");
        }
        else
        {
            // No structured checks readable (older/absent section, or a read hiccup): the prior pointer.
            output.WriteLine($"  See {journalPath} (\"planGuardrails\") for the failed check(s).");
        }

        // #175/#205 merge-collision attribution — advisory, only present when writeScopes overlap.
        if (section?.CollisionHint is { Length: > 0 } collisionHint)
        {
            output.WriteLine($"  {collisionHint}");
        }
    }

    /// <summary>
    /// Read the terminal gate's <c>planGuardrails</c> section (failed checks + the #175 collision hint)
    /// from the persisted journal. Returns null when the section is absent/passed or the journal cannot be
    /// read — the caller then falls back to the generic pointer.
    /// </summary>
    private static PlanGuardrailsSection? TryReadPlanGuardrailSection(string journalPath)
    {
        try
        {
            if (!File.Exists(journalPath))
            {
                return null;
            }

            JournalDocument document = JournalReader.Read(journalPath);
            return document.PlanGuardrails;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The outcome of the pre-live-region Part C drift confirm (issue #274, SSOT §7.2).</summary>
    private enum DriftPromptDecision
    {
        /// <summary>No prompt was shown (no drift, an unsafe drift, or a non-interactive stdin) — proceed and let the Scheduler decide.</summary>
        NotPrompted,

        /// <summary>The operator answered <c>y</c> — pre-authorize the safe rewind for this run.</summary>
        Confirmed,

        /// <summary>The operator answered <c>N</c> — halt without running (exit 2).</summary>
        Declined
    }

    /// <summary>
    /// Part C interactive confirm (issue #274, SSOT §7.2): probe for a provably-safe definition drift and,
    /// ONLY in an interactive TTY, disclose exactly what a <c>y</c> will rebuild and ask. Non-interactive
    /// stdin (CI / redirected / an overwatcher) is never prompted — it falls through so the Scheduler halts
    /// under the default policy (never spends unbidden). An unsafe drift is likewise not prompted (no flag
    /// authorizes an unsound rewind); the Scheduler renders the authoritative refusal report. Interactivity
    /// uses the same <see cref="Console.IsInputRedirected"/> idiom as <c>ResetCommand.Confirm</c>.
    /// </summary>
    private static (DriftPromptDecision Decision, DriftAuthorization? Authorization) ConfirmSafeDriftIfInteractive(
        Core.Model.PlanDefinition plan, RunJournal journal, IConsoleIo io)
    {
        DefinitionDriftProbe.Result drift = DefinitionDriftProbe.Evaluate(plan, journal);
        if (!drift.HasDrift || drift.Decision.Outcome == SafeSuffixOutcome.Refused || Console.IsInputRedirected)
        {
            return (DriftPromptDecision.NotPrompted, null);
        }

        PrintDriftPromptPreview(drift, io);

        string ask = drift.Decision.Outcome == SafeSuffixOutcome.Safe
            ? $"Rewind the plan branch ({drift.Decision.RemovedCommitCount} commit(s)) and re-run {drift.SafeSet.Count} task(s)"
            : $"Reset and re-run {drift.SafeSet.Count} task(s)";
        io.Out.Write($"{ask}? [y/N] ");

        string? answer = Console.ReadLine();
        bool yes = answer is not null && answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase);
        if (!yes)
        {
            io.Out.WriteLine("Declined — nothing was changed (definition-drift halt, SSOT §7.2).");
            return (DriftPromptDecision.Declined, null);
        }

        // Capture EXACTLY what the operator approved (S + reset target + the tip they saw) so the Scheduler
        // rewinds that, not a plan re-derived from files edited during this blocking prompt, and so a
        // concurrent same-plan session that moved the branch is caught by the tip compare-and-swap.
        var authorization = new DriftAuthorization
        {
            SafeSet = drift.SafeSet,
            ResetTarget = drift.Decision.ResetTarget,
            ExpectedTip = drift.Decision.ExpectedTip ?? ""
        };
        return (DriftPromptDecision.Confirmed, authorization);
    }

    /// <summary>
    /// Wave-drift interactive confirm (SSOT §14.6, #254 M2b), the wave-level analogue of
    /// <see cref="ConfirmSafeDriftIfInteractive"/>. Detects — from the journal — every COMPLETED wave whose
    /// current <c>WaveDefinitionHash</c> no longer matches the recorded one, and (in an interactive TTY)
    /// asks whether to rewind + re-run them. Returns (<c>Declined</c>=true) when the operator answered N
    /// (halt); otherwise the authorized wave-dir set (null = no drift / non-interactive, let the Scheduler
    /// halt or auto-resolve per policy). A wave-scoped rewind is ALWAYS a safe trailing suffix (§14.8), so
    /// no per-wave safety preview is needed.
    /// </summary>
    private static (bool Declined, IReadOnlySet<string>? Authorized) ConfirmWaveDriftIfInteractive(
        Core.Model.PlanDefinition plan, RunJournal journal, IConsoleIo io)
    {
        var drifted = new List<(string Dir, string Old, string New)>();
        foreach (Core.Model.WaveNode wave in plan.Waves)
        {
            if (journal.WaveEntryOf(wave.Dir) is not { Status: WaveStatus.Completed, DefinitionHash: { } recorded })
            {
                continue;
            }

            string current;
            try { current = WaveDefinitionHash.Compute(wave); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            if (!string.Equals(recorded, current, StringComparison.Ordinal))
            {
                drifted.Add((wave.Dir, recorded, current));
            }
        }

        if (drifted.Count == 0 || Console.IsInputRedirected)
        {
            return (false, null); // no drift, or non-interactive — the Scheduler halts/decides per policy.
        }

        io.Out.WriteLine();
        io.Out.WriteLine("WAVE DRIFT — one or more COMPLETED waves changed since they last completed (SSOT §14.6).");
        foreach ((string dir, string oldH, string newH) in drifted)
        {
            io.Out.WriteLine($"  {dir}: {ShortHash(oldH)} -> {ShortHash(newH)}");
        }

        io.Out.WriteLine("  A 'y' rewinds the harness-owned plan branch past each drifted wave + its downstream waves and re-runs them;");
        io.Out.WriteLine("  your own checkout is untouched. Discarded commits stay recoverable via git reflog until a later '--fresh'.");
        io.Out.Write($"Rewind + re-run {drifted.Count} drifted wave(s) (and downstream)? [y/N] ");

        string? answer = Console.ReadLine();
        bool yes = answer is not null && answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase);
        if (!yes)
        {
            io.Out.WriteLine("Declined — nothing was changed (wave-drift halt, SSOT §14.6).");
            return (true, null);
        }

        return (false, drifted.Select(d => d.Dir).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// #360 Phase 1 between-wave breakdown confirm (doc 11 §9.6). Under the default <c>prompt</c> policy, in an
    /// interactive TTY, ask whether to auto-invoke <c>plan-breakdown</c> for the upcoming unauthored-wave JIT
    /// checkpoint (a brief.md-bearing empty wave). Returns the captured answer keyed by the wave dir
    /// (<c>true</c> = approve → the Scheduler invokes; <c>false</c> = decline → the Scheduler honest-halts
    /// <c>prompted-declined</c>). Returns <c>null</c> — no confirmation, the Scheduler honest-halts — when
    /// there is no upcoming checkpoint, no <c>brief.md</c>, the run is non-interactive
    /// (<see cref="Console.IsInputRedirected"/>), or the run would not use worktree mode (no integration
    /// worktree = no materialized upstream to break down against).
    /// </summary>
    private static IReadOnlyDictionary<string, bool>? ConfirmWaveBreakdownIfInteractive(
        Core.Model.PlanDefinition plan, RunJournal journal, IConsoleIo io)
    {
        // The upcoming checkpoint = the FIRST not-completed wave with an empty tasks/ folder (skip completed
        // waves and authored-but-not-completed waves, which run before any later stub). This is the wave the
        // run halts at (the checkpoint is a terminal hard barrier).
        Core.Model.WaveNode? checkpoint = null;
        foreach (Core.Model.WaveNode wave in plan.Waves)
        {
            if (journal.WaveEntryOf(wave.Dir) is { Status: WaveStatus.Completed })
            {
                continue;
            }

            if (wave.Tasks.Count == 0)
            {
                checkpoint = wave;
                break;
            }
            // authored, not completed → runs before any later stub; keep scanning.
        }

        if (checkpoint is null)
        {
            return null;
        }

        bool briefPresent = File.Exists(Path.Combine(checkpoint.Directory, Core.Model.WaveNode.BriefFileName));
        if (!briefPresent || !SchedulerFactory.WouldUseWorktreeMode(plan) || Console.IsInputRedirected)
        {
            return null; // non-eligible or non-interactive → the Scheduler honest-halts per policy.
        }

        io.Out.WriteLine();
        io.Out.WriteLine($"WAVE CHECKPOINT — '{checkpoint.Dir}' is unauthored and carries a brief.md (SSOT §14.4, #360).");
        io.Out.WriteLine($"  Invoking plan-breakdown authors '{checkpoint.Dir}/tasks/' against the materialized upstream,");
        io.Out.WriteLine("  then HALTS for you to run /guardrails-review (the review gate is never auto-satisfied).");
        io.Out.Write($"Invoke plan-breakdown for '{checkpoint.Dir}' now? [y/N] ");

        string? answer = Console.ReadLine();
        bool yes = answer is not null && answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase);
        if (!yes)
        {
            io.Out.WriteLine($"Declined — '{checkpoint.Dir}' left unauthored; author it manually, then re-run.");
        }

        return new Dictionary<string, bool>(StringComparer.Ordinal) { [checkpoint.Dir] = yes };
    }

    /// <summary>
    /// Render a WAVED run's wave-boundary halt (SSOT §14, #254 M2b): the JIT-checkpoint (unauthored next
    /// wave), a wave entry/exit gate failure, or a wave-drift halt under a halt/unconfirmed-prompt policy.
    /// Exit 2 (actionable), like the definition-drift halt.
    /// </summary>
    private static void PrintWaveHalt(WaveHalt halt, IConsoleIo io)
    {
        TextWriter o = io.Out;
        o.WriteLine();
        string label = halt.Kind switch
        {
            WaveHaltKind.NextWaveUnauthored => "WAVE CHECKPOINT",
            WaveHaltKind.WaveDrift => "WAVE DRIFT",
            WaveHaltKind.EntryGateFailed => "WAVE ENTRY GATE FAILED",
            WaveHaltKind.ExitGateFailed => "WAVE EXIT GATE FAILED",
            WaveHaltKind.BreakdownComplete => "WAVE BREAKDOWN COMPLETE",
            WaveHaltKind.BreakdownFailed => "WAVE BREAKDOWN FAILED",
            _ => "WAVE HALT"
        };
        o.WriteLine($"{label}: {halt.Headline}");

        if (!string.IsNullOrWhiteSpace(halt.Detail))
        {
            foreach (string line in halt.Detail.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                o.WriteLine($"  {line}");
            }
        }

        foreach (GuardrailResult g in halt.FailedGates)
        {
            o.WriteLine($"  FAILED: {g.Name} — {g.Reason ?? "failed"}");
        }

        // JIT checkpoint (issue #359): render a focused wave diagram into the wave folder and
        // surface a "Wave diagram (focused):" link so the operator can see the wave's shape while
        // breaking it down. Best-effort: a render failure is swallowed; it never changes the exit
        // code or obscures the checkpoint message. The same render runs at wave-start on re-run
        // (see ConsoleRunObserver / LiveRunObserver.WaveStarting) so the diagram is always fresh.
        // #360 Phase 1: on BreakdownComplete the wave is now AUTHORED, so the focused diagram shows the
        // freshly-broken-down DAG the human is about to review.
        if (halt.Kind is WaveHaltKind.NextWaveUnauthored or WaveHaltKind.BreakdownComplete
            && halt.WaveDirectory is { } waveAbsDir)
        {
            if (GraphCommand.RenderWaveScoped(waveAbsDir, TextWriter.Null))
            {
                string diagramHtml = Path.Combine(waveAbsDir, "diagram.html");
                bool linkable = !Console.IsOutputRedirected && Spectre.Console.AnsiConsole.Profile.Capabilities.Links;
                string link = Hyperlink(diagramHtml, linkable);
                o.WriteLine($"  Wave diagram (focused): {link}");
            }
        }
    }

    /// <summary>
    /// Disclose what a <c>y</c> to the Part C drift confirm will rebuild (issue #274, SSOT §7.2): each
    /// drifted task's old→new short hash and the full re-run set (drifted + descendants), so the operator
    /// decides with the whole picture. The plan branch is harness-owned and the rewind is reflog-recoverable.
    /// </summary>
    private static void PrintDriftPromptPreview(DefinitionDriftProbe.Result drift, IConsoleIo io)
    {
        TextWriter output = io.Out;
        output.WriteLine();
        output.WriteLine("DEFINITION DRIFT — one or more already-succeeded tasks changed since they last succeeded (SSOT §7.2).");
        foreach (DefinitionDriftProbe.DriftedEntry d in drift.Drifted)
        {
            output.WriteLine($"  {d.TaskId}: {ShortHash(d.OldHash)} -> {ShortHash(d.NewHash)}");
        }

        output.WriteLine($"  Re-run set (drifted + descendants): {string.Join(", ", drift.SafeSet)}");
        output.WriteLine(
            "  A 'y' rewinds the harness-owned plan branch and re-runs that set; your own checkout is untouched.");
        output.WriteLine(
            "  Discarded commits stay recoverable via git reflog until a later '--fresh' / 'reset -y' tears the branch down.");
    }

    /// <summary>
    /// Render the definition-drift halt (issue #274 Part A, SSOT §7.2): for each drifted task, its
    /// old → new short definition hash, the best-effort per-file breakdown (added/removed/modified + an
    /// approximate ± line count, or the Tier-2 "not recoverable" note), the reference <c>git diff</c>
    /// command for full content, and its transitive-descendant set — followed by the two remediation
    /// paths named in §7.2. The changed task(s) are reported for the human's decision, never silently
    /// re-executed.
    /// </summary>
    private static void PrintDefinitionDrift(
        Core.Execution.DefinitionDriftReport drift, string planDirectory, IConsoleIo io)
    {
        TextWriter output = io.Out;
        string folder = Path.GetFileName(
            planDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        output.WriteLine();
        output.WriteLine("DEFINITION DRIFT — halting; nothing was scheduled (SSOT §7.2, issue #274).");
        output.WriteLine("One or more already-succeeded tasks have a definition (task.json / action / guardrails /");
        output.WriteLine("preflights) that changed since they last succeeded. The harness will NOT silently reuse the");
        output.WriteLine("stale cached result, nor silently re-run the changed task — you decide.");

        foreach (Core.Execution.DriftedTask t in drift.Tasks)
        {
            output.WriteLine();
            output.WriteLine($"  {t.TaskId}");
            output.WriteLine($"    definition hash: {ShortHash(t.OldHash)} -> {ShortHash(t.NewHash)}");

            if (t.ChangedFiles.Count > 0)
            {
                output.WriteLine("    changed files:");
                foreach (Core.Execution.ChangedDefinitionFile f in t.ChangedFiles)
                {
                    output.WriteLine($"      - {f.Path}  {f.Change}{FormatDelta(f)}");
                }
            }

            if (t.Note is { Length: > 0 } note)
            {
                output.WriteLine($"    note: {note}");
            }

            output.WriteLine($"    full diff: {t.DiffCommand}");

            if (t.Dependents.Count > 0)
            {
                output.WriteLine($"    dependents also affected: {string.Join(", ", t.Dependents)}");
            }
        }

        output.WriteLine();
        if (drift.SafeToAutoResolve)
        {
            // The drifted set IS a provably-safe suffix — the halt is a policy/consent choice, so the
            // auto-resolve flag actually works. Lead with it.
            output.WriteLine("Remediation (this drift is a PROVABLY-SAFE suffix — auto-resolve is available):");
            output.WriteLine($"  guardrails run {folder} --reprocess-drift — rewind the plan branch past the safe suffix + re-run");
            output.WriteLine($"                                              (or re-run interactively to confirm with 'y')");
            output.WriteLine($"  guardrails reset {folder} <taskId>...     — scoped reset of only the drifted task(s) + descendants");
            output.WriteLine($"  guardrails reset {folder} -y              — full correct rebuild (always sound)");
        }
        else
        {
            // The rewind was REFUSED as unsound — --reprocess-drift would just re-halt on the same floor.
            // Surface WHY and steer straight to the always-sound full rebuild.
            output.WriteLine("Cannot safely auto-resolve — the drifted set is NOT a safe trailing suffix of the plan branch:");
            if (drift.RewindRefusal is { Length: > 0 } refusal)
            {
                output.WriteLine($"  {refusal}");
            }

            if (drift.RewindBlockingTask is { Length: > 0 } blocker)
            {
                output.WriteLine($"  blocking task: {blocker}");
            }

            output.WriteLine();
            output.WriteLine("Remediation (--reprocess-drift would REFUSE the same way — do not use it here):");
            output.WriteLine($"  guardrails reset {folder} -y — full correct rebuild (always sound; tears the plan branch down)");
        }
    }

    /// <summary>Shorten a <c>sha256:</c>-prefixed hash for display (e.g. <c>sha256:a6bee1…</c>).</summary>
    private static string ShortHash(string hash)
    {
        const string prefix = "sha256:";
        if (hash.StartsWith(prefix, StringComparison.Ordinal))
        {
            string hex = hash[prefix.Length..];
            return prefix + (hex.Length <= 6 ? hex : hex[..6] + "…");
        }

        return hash.Length <= 12 ? hash : hash[..12] + "…";
    }

    /// <summary>Render a changed file's approximate ± line delta (e.g. <c> (+6 -2)</c>); empty when none.</summary>
    private static string FormatDelta(Core.Execution.ChangedDefinitionFile f)
    {
        var parts = new List<string>();
        if (f.Added is > 0)
        {
            parts.Add($"+{f.Added}");
        }

        if (f.Removed is > 0)
        {
            parts.Add($"-{f.Removed}");
        }

        return parts.Count == 0 ? "" : $" ({string.Join(" ", parts)})";
    }

    /// <summary>
    /// Render the issue #340 loud "work not delivered" warning: a run drained WHOLLY GREEN — the DAG AND
    /// the terminal gate (<paramref name="terminalGatePassed"/>) — but delivery did NOT happen because
    /// <c>mergeOnSuccess</c> resolved off (<see cref="RunReport.WhollyGreenButUndelivered"/>). The verified
    /// work is sitting on the plan branch <c>guardrails/&lt;plan-name&gt;</c>, undelivered — one
    /// <c>--fresh</c>/<c>reset -y</c> away from destruction. It is rendered as a bannered block so a run
    /// that did NOT deliver can never read as an ordinary success. No warning fires for a DELIVERED run
    /// (delivery requires <c>mergeOnSuccess</c> on, which forces the flag false), a non-green run, a
    /// serial/<c>runOnCurrentBranch</c> run (no separate plan branch ⇒ the flag is false — the work is
    /// already in the checkout), or a run whose terminal gate FAILED (<paramref name="terminalGatePassed"/>
    /// false — that path already halts exit 2). Pure (writes only to <paramref name="output"/>) and public
    /// + unit-tested with a <see cref="StringWriter"/> — the Cli assembly ships no InternalsVisibleTo (same
    /// rationale as <see cref="Hyperlink"/>).
    /// </summary>
    public static void RenderUndeliveredWorkWarning(
        RunReport report, bool terminalGatePassed, string planDirectory, TextWriter output)
    {
        if (!report.WhollyGreenButUndelivered || !terminalGatePassed)
        {
            return;
        }

        string planName = Path.GetFileName(
            planDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string planBranch = "guardrails/" + planName;
        const string rule = "==============================================================================";

        output.WriteLine();
        output.WriteLine(rule);
        output.WriteLine("*** WORK NOT DELIVERED ***");
        output.WriteLine(
            "mergeOnSuccess is off — this fully-green run's verified work is sitting on branch");
        output.WriteLine($"'{planBranch}', NOT on your checkout.");
        output.WriteLine(
            $"Deliver it before it is lost:  guardrails run {planName} --merge-on-success");
        output.WriteLine($"                               (or merge '{planBranch}' into your branch yourself).");
        output.WriteLine("A later --fresh or 'reset -y' will DESTROY this undelivered work.");
        output.WriteLine(rule);
    }

    /// <summary>
    /// Render the issue #340 one-time "delivered by default" notice: a single line, printed at run end
    /// ONLY when the end-of-run delivery actually RAN and succeeded (<see cref="RunReport.DeliveredToBranch"/>
    /// is non-null — an FF or clean merge) AND it fired PURELY because of the new default
    /// (<paramref name="deliveryFromDefaultOnly"/> — neither the <c>mergeOnSuccess</c> config key nor a CLI
    /// flag was set). This makes the breaking default change observable and self-documenting: it names the
    /// branch the work landed on and the two opt-out surfaces. It is the delivered-case complement of
    /// <see cref="RenderUndeliveredWorkWarning"/> and the two NEVER fire together (that warning requires
    /// delivery OFF; this requires delivery to have run). Silent for an explicit opt-in (config <c>true</c>
    /// or <c>--merge-on-success</c>), for any run that did not deliver (opt-out, serial, non-green), and for a
    /// halted delivery. Pure (writes only to <paramref name="output"/>) and public + unit-tested with a
    /// <see cref="StringWriter"/> — the Cli assembly ships no InternalsVisibleTo (same rationale as
    /// <see cref="Hyperlink"/>).
    /// </summary>
    public static void RenderDeliveredByDefaultNotice(
        RunReport report, bool deliveryFromDefaultOnly, TextWriter output)
    {
        if (!deliveryFromDefaultOnly || report.DeliveredToBranch is not { Length: > 0 } branch)
        {
            return;
        }

        output.WriteLine(
            $"delivered to {branch} (mergeOnSuccess now defaults on; pass --no-merge-on-success or set "
            + "\"mergeOnSuccess\": false to opt out)");
    }

    /// <summary>
    /// Render the actionable end-of-run delivery halt (issue #150). The plan branch carries all the
    /// (verified) work; only the optional merge back into the user's branch was refused. For a hook
    /// rejection the user's own hook stderr (<see cref="RunReport.MergeOnSuccessDetail"/>) is shown
    /// verbatim so they see exactly why and can resolve it or disable the hook for the merge.
    /// </summary>
    private static void PrintMergeOnSuccessHalt(
        RunReport report, Core.Model.PlanDefinition plan, MergeOnSuccessResult outcome, IConsoleIo io)
    {
        TextWriter output = io.Out;
        string planBranch = $"guardrails/{Path.GetFileName(plan.PlanDirectory)}";

        output.WriteLine();
        switch (outcome)
        {
            case MergeOnSuccessResult.HookRejected:
                output.WriteLine(
                    $"All tasks passed and are on branch `{planBranch}`. The final merge into your " +
                    "branch was rejected by your git hook:");
                if (!string.IsNullOrWhiteSpace(report.MergeOnSuccessDetail))
                {
                    output.WriteLine($"  {report.MergeOnSuccessDetail}");
                }
                output.WriteLine(
                    "  Resolve and merge manually, or disable the hook for the merge. Your branch is " +
                    "unchanged (the merge was aborted).");
                break;

            case MergeOnSuccessResult.Conflict:
                output.WriteLine(
                    $"All tasks passed and are on branch `{planBranch}`. The final merge into your " +
                    "branch CONFLICTED with a change made on your branch during the run; AI-merge is " +
                    "withheld here (SSOT §5.3). Your branch is unchanged — merge `" + planBranch +
                    "` manually.");
                break;

            case MergeOnSuccessResult.DirtyWorkingTree:
                output.WriteLine(
                    $"All tasks passed and are on branch `{planBranch}`. The final merge into your " +
                    "branch was refused because your working tree has uncommitted changes. Commit or " +
                    "stash them, then merge `" + planBranch + "` manually.");
                break;
        }
    }

    /// <summary>
    /// Write an aborted run's FULL fault detail (issue #150) to <c>logs/&lt;runId&gt;/abort.log</c> so
    /// the console headline stays a one-liner while the dev keeps the whole exception. Best-effort —
    /// a logs-tree write hiccup must never change the run's exit code or mask the abort.
    /// </summary>
    private static void WriteAbortDetailToLogs(string logsRoot, Core.Execution.RunAbort abort)
    {
        try
        {
            Directory.CreateDirectory(logsRoot);
            File.WriteAllText(
                Path.Combine(logsRoot, "abort.log"),
                $"{abort.Headline}\n\n{abort.Remedy}\n\n--- full fault detail ---\n{abort.Detail}\n");
        }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    /// <summary>
    /// Render the durable, self-contained static site (all-static links, no refresh, every task page)
    /// at run end via <see cref="LogSiteRenderer.ExportSite"/>, reading the freshly-persisted journal
    /// for per-task status. No-op (and never throws) when the journal is absent — a fully-resumed /
    /// all-skipped run writes no logs, so there is nothing to render.
    /// </summary>
    private static void WriteDurableFinalSite(string logsRoot, Core.Model.PlanDefinition plan, string planDirectory)
    {
        string journalPath = RunJournal.PathFor(planDirectory);
        if (!File.Exists(journalPath) || !Directory.Exists(logsRoot))
        {
            return;
        }

        try
        {
            JournalDocument document = JournalReader.Read(journalPath);
            LogSiteRenderer.ExportSite(logsRoot, plan.Tasks, document);
        }
        catch (IOException)
        {
            // Best-effort durable site — a transient lock must never flip the run's exit code.
        }
        catch (UnauthorizedAccessException)
        {
            // ditto — a logs-tree permission hiccup is not a run failure.
        }
    }

    /// <summary>
    /// Print a clickable <c>file://</c> link to the run's static "all tasks" index
    /// (<c>logs/&lt;runId&gt;/index.html</c>, issue #141 item 2) — the during-run refreshing page at run
    /// start, the durable page at run end. Emits an OSC 8 hyperlink only when the terminal can render
    /// one (matching the post-mortem pointer's gate); otherwise the plain absolute path, which is
    /// copy-pasteable. No-op when the index does not exist (nothing was rendered).
    /// </summary>
    private static void PrintStaticIndexLink(string logsRoot, IConsoleIo io)
    {
        string indexPath = Path.GetFullPath(Path.Combine(logsRoot, "index.html"));
        if (!File.Exists(indexPath))
        {
            return;
        }

        bool linkable = !Console.IsOutputRedirected && AnsiConsole.Profile.Capabilities.Links;
        io.Out.WriteLine($"All tasks (static log site): {Hyperlink(indexPath, linkable)}");
    }

    /// <summary>
    /// Print a clickable <c>file://</c> link to the run's live status diagram
    /// (<c>logs/&lt;runId&gt;/diagram.html</c>, issue #219) — the during-run refreshing page at run start,
    /// the durable settled page at run end. Same OSC 8 / plain-path gate as
    /// <see cref="PrintStaticIndexLink"/>. No-op when the diagram does not exist (nothing was rendered).
    /// It is the SAME DAG as the plan-root <c>diagram.html</c> (same <c>source-sha256</c>, same
    /// click-throughs) — only with the live status overlay (SSOT §10.1).
    /// </summary>
    private static void PrintDiagramLink(string logsRoot, IConsoleIo io)
    {
        string diagramPath = Path.GetFullPath(Path.Combine(logsRoot, "diagram.html"));
        if (!File.Exists(diagramPath))
        {
            return;
        }

        bool linkable = !Console.IsOutputRedirected && AnsiConsole.Profile.Capabilities.Links;
        io.Out.WriteLine($"Live status diagram: {Hyperlink(diagramPath, linkable)}");
    }

    /// <summary>
    /// Read the freshly-persisted journal for SEEDING the live status diagram (issue #219) — so a resume
    /// (and the already-settled pre-DAG Full Flight Checks phase) shows correct badges from the first
    /// frame. Best-effort: a missing/locked/corrupt journal returns null (the diagram then seeds every
    /// node pending), never throwing — seeding is a UX nicety and must not affect the run.
    /// </summary>
    private static JournalDocument? TryReadJournalForSeed(string planDirectory)
    {
        string path = RunJournal.PathFor(planDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JournalReader.Read(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task<RunReport> ExecuteAsync(
        Core.Model.PlanDefinition plan,
        IRunObserver observer,
        DriftAuthorization? driftAuthorization,
        IReadOnlySet<string>? waveDriftAuthorized,
        IReadOnlyDictionary<string, bool>? breakdownConfirmations,
        CancellationToken cancellationToken)
    {
        Scheduler scheduler = SchedulerFactory.Create(
            plan, new ProcessRunner(), new PathExecutableProbe(), observer, driftAuthorization, waveDriftAuthorized,
            breakdownConfirmations: breakdownConfirmations);
        return scheduler.RunAsync(plan, cancellationToken);
    }

    private static void PrintSummary(RunReport report, string planDirectory, string runId, IConsoleIo io)
    {
        TextWriter output = io.Out;

        output.WriteLine("Summary");
        output.WriteLine("-------");
        foreach (TaskResult result in report.Tasks)
        {
            output.WriteLine($"  {StatusLabel(result.Outcome),-16} {result.TaskId,-32} {result.Summary}");
        }

        int green = report.Tasks.Count(t => t.IsGreen);
        output.WriteLine();
        output.WriteLine(report.Cancelled
            ? $"Run CANCELLED — {green}/{report.Tasks.Count} task(s) green; in-flight tasks journaled pending. Re-run to resume."
            : $"{green}/{report.Tasks.Count} task(s) green (succeeded or skipped).");

        PrintTotalCost(planDirectory, output);

        // Post-mortem pointer for EVERY task, not just failures: a green task whose guardrails
        // turned out too weak is reviewed from the same on-disk logs (action output, guardrail
        // stdout, feedback per attempt). The link target is the ABSOLUTE logs/<runId>/ root so it is
        // clickable (issue #59); the <task-id>/attempt-N/ layout follows as guidance text. The
        // per-attempt artifacts live under logs/<runId>/ (SSOT §8), NOT the pre-plan-08 state/logs/.
        string logsRoot = Path.GetFullPath(Path.Combine(planDirectory, "logs", runId));
        string sep = Path.DirectorySeparatorChar.ToString();
        // Emit a clickable OSC 8 link only when the terminal can actually render one — matching the
        // live table's gate. Redirection alone is too weak: a non-redirected but hyperlink-incapable
        // TTY would get raw escape bytes as visible garbage, which Spectre's link capability check
        // avoids. Also require the target to exist — a full-resume/all-skipped run writes no logs, so
        // don't advertise a link that 404s. When not linkable the plain absolute path still serves as
        // copy-pasteable guidance and fixes the #59 regression (it was relative with literal placeholders).
        bool linkable = !Console.IsOutputRedirected
                        && AnsiConsole.Profile.Capabilities.Links
                        && Directory.Exists(logsRoot);
        output.WriteLine();
        output.WriteLine($"Logs (post-mortem any task — pass or fail): {Hyperlink(logsRoot, linkable)}");
        output.WriteLine($"  each task's attempts are under <task-id>{sep}attempt-N{sep}");

        PrintNeedsHumanSections(report, logsRoot, output);
    }

    /// <summary>
    /// Print the post-run NEEDS HUMAN sections, resolving each task's triage diagnosis from the
    /// on-disk <c>triage.json</c> sidecar in its task-level log dir. Thin production wrapper over the
    /// pure <see cref="RenderNeedsHumanSections"/> (which is unit-tested with an injected resolver).
    /// </summary>
    private static void PrintNeedsHumanSections(RunReport report, string logsRoot, TextWriter output) =>
        RenderNeedsHumanSections(
            report.Tasks, logsRoot, output,
            taskLogDir => TriageSummaryReader.TryRead(taskLogDir));

    /// <summary>
    /// Render the post-run NEEDS HUMAN sections (issue #163): per failed/needs-human task, surface the
    /// AI triage root-cause CATEGORY + one-line diagnosis (and the drafted GH-issue title when present)
    /// directly in the console — so the user does not open each <c>feedback.md</c>. When several tasks
    /// share a diagnosis category the repeat is annotated ("same root cause as …") so one fix resolving
    /// several failures is obvious at a glance. A task with no structured triage (unstructured or failed
    /// — <paramref name="triageFor"/> returns null) renders the prior shape, unchanged. The leading line
    /// stays parseable: <c>NEEDS HUMAN: &lt;task-id&gt; — &lt;summary&gt;</c>.
    /// <para>
    /// Pure (no IO) — the triage lookup is injected as <paramref name="triageFor"/> (the task-level log
    /// dir → <see cref="TriageSummary"/>), so the production path reads the sidecar and tests inject a
    /// fake. Public for the same reason <see cref="Hyperlink"/> is: the Cli assembly ships no
    /// InternalsVisibleTo.
    /// </para>
    /// </summary>
    public static void RenderNeedsHumanSections(
        IReadOnlyList<TaskResult> tasks,
        string logsRoot,
        TextWriter output,
        Func<string, TriageSummary?> triageFor)
    {
        // Issue #190: a rate-limited task is NOT "fix the action or guardrails" — it is a healthy task
        // waiting on a provider-side limit. Give it its own section with the correct advice ("re-run
        // later") instead of folding it into the generic NEEDS HUMAN loop below, whose guidance would
        // mislead an operator into debugging a task that isn't broken.
        foreach (TaskResult rateLimited in tasks.Where(t => t.Outcome is TaskOutcome.RateLimited))
        {
            output.WriteLine();
            output.WriteLine($"RATE LIMITED: {rateLimited.TaskId} — {rateLimited.Summary}");
            output.WriteLine("  Not a task defect — a provider-side limit did not clear in time. Re-run this plan");
            output.WriteLine("  later (the harness resumes from here), or raise transientPauseBudgetSeconds.");
        }

        // First task id seen per category, so a later same-category task can point back to it.
        var firstTaskForCategory = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (TaskResult needsHuman in tasks.Where(t =>
                     t.Outcome is TaskOutcome.ActionFailed or TaskOutcome.GuardrailFailed
                         or TaskOutcome.InvalidFragment or TaskOutcome.NeedsHuman))
        {
            output.WriteLine();
            output.WriteLine($"NEEDS HUMAN: {needsHuman.TaskId} — {needsHuman.Summary}");

            string taskLogDir = Path.Combine(logsRoot, needsHuman.TaskId);
            if (triageFor(taskLogDir) is { } triage)
            {
                string rootCause = string.IsNullOrWhiteSpace(triage.OneLine)
                    ? $"Root cause [{triage.Diagnosis}]"
                    : $"Root cause [{triage.Diagnosis}]: {triage.OneLine}";

                // Group annotation: a second-or-later task in the same category points back to the
                // first, making "one fix resolves several failures" visible without opening files.
                if (firstTaskForCategory.TryGetValue(triage.Diagnosis, out string? firstTask))
                {
                    rootCause += $" (same root cause as {firstTask})";
                }
                else
                {
                    firstTaskForCategory[triage.Diagnosis] = needsHuman.TaskId;
                }

                output.WriteLine($"  {rootCause}");
                if (!string.IsNullOrWhiteSpace(triage.GhIssueTitle)
                    && !string.Equals(triage.GhIssueTitle, triage.OneLine, StringComparison.Ordinal))
                {
                    output.WriteLine($"  Draft GH issue: {triage.GhIssueTitle}");
                }
            }

            output.WriteLine($"  Inspect {taskLogDir}{Path.DirectorySeparatorChar} (latest attempt's feedback.md has the full failure detail),");
            output.WriteLine("  fix the action or guardrails, then re-run to resume.");
        }
    }

    /// <summary>
    /// Print the run-level cost line (SSOT §7 <c>costUsd</c>) from the freshly-persisted
    /// journal. Omitted when no attempt recorded a cost, so deterministic-only plans stay
    /// noise-free.
    /// </summary>
    private static void PrintTotalCost(string planDirectory, TextWriter output)
    {
        string journalPath = RunJournal.PathFor(planDirectory);
        if (!File.Exists(journalPath))
        {
            return;
        }

        JournalDocument document = JournalReader.Read(journalPath);
        if (JournalCost.Total(document) is { } total)
        {
            output.WriteLine($"Total prompt cost: ${total:F4}");
        }
    }

    /// <summary>
    /// Render <paramref name="absolutePath"/> as an OSC 8 hyperlink (clickable in capable terminals —
    /// Windows Terminal, VS Code, iTerm2) targeting its <c>file://</c> URI, mirroring the per-task
    /// links in the live table. When <paramref name="enabled"/> is false — output redirected, the
    /// terminal can't render hyperlinks, or the target doesn't exist — the escape sequence would be
    /// noise, so emit the plain absolute path instead. The caller owns the capability decision so this
    /// stays a pure, testable function. Public (not private) because the Cli assembly ships no
    /// InternalsVisibleTo — same rationale as <see cref="LogsCommand"/>'s test seams.
    /// </summary>
    public static string Hyperlink(string absolutePath, bool enabled)
    {
        if (!enabled)
        {
            return absolutePath;
        }

        const string esc = "\u001b";
        string uri = new Uri(absolutePath).AbsoluteUri;
        return $"{esc}]8;;{uri}{esc}\\{absolutePath}{esc}]8;;{esc}\\";
    }

    internal static string StatusLabel(TaskOutcome outcome) => outcome switch
    {
        TaskOutcome.Succeeded => "OK",
        TaskOutcome.Skipped => "SKIPPED",
        TaskOutcome.ActionFailed => "ACTION FAILED",
        TaskOutcome.GuardrailFailed => "GUARDRAIL FAILED",
        TaskOutcome.InvalidFragment => "INVALID FRAGMENT",
        TaskOutcome.NeedsHuman => "NEEDS HUMAN",
        // Issue #190: distinct from a generic NEEDS HUMAN so the per-task summary line reads
        // "re-run later", not "something is broken here".
        TaskOutcome.RateLimited => "RATE LIMITED",
        TaskOutcome.Blocked => "BLOCKED",
        TaskOutcome.Cancelled => "CANCELLED",
        _ => outcome.ToString()
    };
}
