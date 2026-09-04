using System.CommandLine;
using System.Text.Json;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using Guardrails.Core.Telemetry;
using Spectre.Console;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails run [folder] [--fresh] [--no-ui]</c> — validate then execute the plan
/// DAG (parallel, retry-aware, resume-aware). <c>--fresh</c> wipes runtime state first
/// (SSOT §6.1). Live Spectre progress when interactive; plain lines otherwise. Exit codes
/// per SSOT §7: 0 green, 1 error, 2 needs-human/failed, 3 cancelled, 4 escalations-pending
/// (an autonomous-mode answer-required halt, §7.1), 5 proceeded-unreviewed (a wholly-green run that
/// proceeded through one or more waves unreviewed, §7.1 / Option P §5.2). Defaults to the
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

        var allTasksOption = new Option<bool>("--all-tasks")
        {
            Description = "Live table only (issue #379): show EVERY task's row across ALL waves, even completed ones. By default a waved run collapses each COMPLETED wave to a one-line summary so the active wave stays on-screen; this restores the full flat table. No effect on a flat plan or under --no-ui."
        };

        // ── Autonomous-mode flags (issue #361, doc 12 §3.4; decided §10 I/N) ──────────────────────────
        // Task 06-author-tests-autonomous-cli added these option stubs so `--autonomous`, `--dial <level>`,
        // and `--max-cost-usd <n>` PARSE; task 07 (this one) wires their RESOLUTION in ResolveAutonomousMode
        // below — the resolved-autonomy summary line, the built-in-$20 maxCostUsd default + loud warning,
        // --dial validation, and the GR2040 re-check on the POST-FLAG effective config — and applies the
        // resulting overrides to the executing run in RunAsync.
        var autonomousOption = new Option<bool>("--autonomous")
        {
            Description = "Run unattended (doc 12 §3.4): set autonomyPolicy 'auto' and, when the config omits an autonomy block, apply one with escalationThreshold 'high' (best-guess only low/moderate — the conservative default, §10 N). REQUIRES an effective maxCostUsd; when none is set a built-in $20 default applies with a loud warning."
        };

        var dialOption = new Option<string?>("--dial")
        {
            Description = "Override the run-wide autonomy escalationThreshold (doc 12 §3.3/§3.4): 'low', 'moderate', 'high', or 'critical' — the lowest criticality that still escalates. 'critical' is fully autonomous (floors always escalate). An unrecognized value is a usage error."
        };

        var maxCostUsdOption = new Option<decimal?>("--max-cost-usd")
        {
            Description = "Set the effective maxCostUsd ceiling for this run (overrides guardrails.json). Under --autonomous this satisfies the required cost cap, so the built-in $20 default is not applied."
        };

        // ── Webhook delivery (issue #585 layer 3, design doc 36) ──────────────────────────────────
        // Task 08 declares and parses these two; it wires neither. Multi-valued and NOT Option<string?>:
        // a single-arity option would have System.CommandLine 2.0.9 reject a second `--on-event` itself,
        // with a generic arity message and exit code 1 — measured against this repo's own CLI. That would
        // detect a repeat but never name the reason §6.4 requires, and it would make
        // ARepeatedOnEventFlagIsRejected pass against a tree with no validation at all. Task 09 does its
        // own count check (zero -> env fallback, one -> use it, more than one -> a named validation error)
        // over the array this declaration parses into. ArgumentArity.OneOrMore (not ZeroOrMore) so a bare
        // `--on-event` with no value stays a usage error rather than silently meaning "no webhook".
        // AllowMultipleArgumentsPerToken is left at its default false, so `--on-event a b` cannot silently
        // collect two values from one occurrence.
        var onEventOption = new Option<string[]>("--on-event")
        {
            Description = "POST each events.jsonl row to <url> as it is written (design doc 36 §8.3). Not repeatable — a second occurrence is a validation error naming the reason (§6.4). Falls back to GUARDRAILS_ON_EVENT when absent.",
            Arity = ArgumentArity.OneOrMore
        };

        var onEventDetailOption = new Option<bool>("--on-event-detail")
        {
            Description = "Include the free-text 'detail' field in webhook deliveries (design doc 36 §6.3). Withheld by default, carrying the fixed marker '(detail withheld; pass --on-event-detail)'."
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
        command.Add(allTasksOption);

        // Autonomous-mode options (see the block where they are declared): resolved in ResolveAutonomousMode
        // and applied to the executing run in RunAsync.
        command.Add(autonomousOption);
        command.Add(dialOption);
        command.Add(maxCostUsdOption);

        // #585 layer 3 (design doc 36): parsed cleanly, then ignored — task 09 reads these and wires
        // validation, the env fallback, and the WebhookEventSink construction.
        command.Add(onEventOption);
        command.Add(onEventDetailOption);

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
            bool allTasks = parseResult.GetValue(allTasksOption);
            bool autonomous = parseResult.GetValue(autonomousOption);
            string? dial = parseResult.GetValue(dialOption);
            decimal? maxCostUsd = parseResult.GetValue(maxCostUsdOption);

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

            // Autonomous-mode resolution (doc 12 §3.4): validate --dial, apply --autonomous/--dial to the
            // EFFECTIVE post-flag config, re-check GR2040 on that end-state (B1 — the load-time check ran
            // before these flags mutated the config), enforce the required cost cap, and print the resolved
            // summary + warning. Surfaced BEFORE the --dry-run branch so it is observable without executing the
            // DAG; a usage error (bad dial) or a GR2040 violation exits non-zero here without running anything.
            if (ResolveAutonomousMode(
                    folder, autonomous, dial, maxCostUsd,
                    out Core.Model.EscalationThreshold? dialOverride, out decimal? maxCostOverride, io) is { } autonomyExit)
            {
                return autonomyExit;
            }

            if (dryRun)
            {
                return DryRun.Execute(folder, io, skipReviewCheck);
            }

            return await RunAsync(folder, fresh, noUi, noLogServer, logPort, mergeOnSuccessOverride, autonomy, reprocessDrift, autonomous, dialOverride, maxCostOverride, skipReviewCheck, allTasks, io, cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    private static async Task<int> RunAsync(
        string folder, bool fresh, bool noUi, bool noLogServer, int logPort, bool? mergeOnSuccessOverride, string? autonomy, bool reprocessDrift, bool autonomous, Core.Model.EscalationThreshold? dialOverride, decimal? maxCostOverride, bool skipReviewCheck, bool allTasks, IConsoleIo io, CancellationToken cancellationToken)
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

        // --reprocess-drift is the legacy alias for `auto`; --autonomous (doc 12 §3.4) likewise forces the
        // unified policy to `auto` (unattended). An UNSAFE action still halts regardless.
        if (reprocessDrift || autonomous)
        {
            autonomyOverride = Core.Model.AutonomyPolicy.Auto;
        }

        if (autonomyOverride is { } policy && probe.Plan.Config.AutonomyPolicy != policy)
        {
            probe = probe with { Plan = probe.Plan with { Config = probe.Plan.Config with { AutonomyPolicy = policy } } };
        }

        // Apply the autonomous-mode dial/cost overrides (doc 12 §3.4) to the EFFECTIVE run config. The command
        // layer already validated these and surfaced the resolved summary + required-cost-cap warning + GR2040
        // re-check (ResolveAutonomousMode); here we only APPLY the resolved end-state so the scheduled run
        // honours it. --autonomous with no config autonomy block installs the conservative default dial
        // (escalationThreshold high — best-guess only low/moderate, §10 N); --dial overrides the run-wide
        // threshold (preserving any per-gate overrides); the resolved cost cap (an explicit --max-cost-usd, or
        // the built-in $20 default under --autonomous) caps the run.
        if (autonomous && probe.Plan.Config.Autonomy is null)
        {
            probe = probe with { Plan = probe.Plan with { Config = probe.Plan.Config with { Autonomy = new Core.Model.AutonomyConfig() } } };
        }

        if (dialOverride is { } dialLevel)
        {
            Core.Model.AutonomyConfig dialBase = probe.Plan.Config.Autonomy ?? new Core.Model.AutonomyConfig();
            probe = probe with { Plan = probe.Plan with { Config = probe.Plan.Config with { Autonomy = dialBase with { EscalationThreshold = dialLevel } } } };
        }

        if (maxCostOverride is { } costCap && probe.Plan.Config.MaxCostUsd != costCap)
        {
            probe = probe with { Plan = probe.Plan with { Config = probe.Plan.Config with { MaxCostUsd = costCap } } };
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

        // #383/#407/#419 worktree-mode run-start setup: the startup GC (a crash BACKSTOP now, #419), the
        // liveness lock, and — on Windows — a FRESH short junction for this run. The junction is a
        // PROCESS-SCOPED cwd alias (issue #419): threaded IN-MEMORY (no longer journaled), released on every
        // recoverable exit by `junctionLifetime` (the method-scoped `using` below covers ALL return/throw
        // paths), and re-allocated FRESH each run — a resume takes any free .a..z letter, since git
        // canonicalized the link away and the deterministic segment subpath resolves the same tree under it.
        bool worktreeMode = SchedulerFactory.WouldUseWorktreeMode(probe.Plan);

        // Plan 30 §3.4 — the machine/concurrency/version profile, probed ONCE per run and stamped
        // BEFORE SchedulerFactory.CreateExecutor's OWN, LATER RunJournal.LoadOrCreate (reached when it
        // builds the executor). That ordering is load-bearing (RunEnvironmentProbe/RunJournal.RecordEnvironment):
        // a stamp placed after the second load would be silently overwritten by a document read before the
        // stamp existed. MaxParallelism records the EFFECTIVE concurrency, not the configured one — derived
        // from the same WouldUseWorktreeMode predicate the Scheduler's own no-provider clamp is keyed on
        // (ParallelismClampedNoProvider), so the two can never disagree.
        journal.RecordEnvironment(RunEnvironmentProbe.Probe(
            maxParallelism: worktreeMode ? probe.Plan.Config.MaxParallelism : 1,
            harnessVersion: GuardrailsVersion.Current,
            skillVersion: ResolveInstalledSkillVersion()));

        WorktreeJunctionSetup? worktreeSetup = worktreeMode
            ? PrepareWorktreeJunction(probe.Plan, runId, io.Out)
            : null;
        using WorktreeJunctionLifetime? junctionLifetime = worktreeSetup?.Lifetime;
        string? realWorktreeRootForRun = worktreeSetup?.RealRoot;
        string? junctionRootForRun = worktreeSetup?.JunctionRoot;

        // #383 (Windows only): GR2038 measures the EFFECTIVE root — the junction (so it almost never fires),
        // or the real root on a lazy-skip / graceful fallback (where it may fire with the actionable
        // GUARDRAILS_WORKTREE_ROOT remedy). An early return here still releases any allocated link (the
        // `using` above).
        if (worktreeMode && OperatingSystem.IsWindows())
        {
            Diagnostic? pathHalt = WorktreePathPreflight.Check(
                junctionRootForRun ?? realWorktreeRootForRun!, runId, probe.Plan.Tasks.Select(t => t.Id));
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
            .EvaluateAsync(probe.Plan, journal, new ProcessRunner(), io.Out, cancellationToken, junctionRootForRun)
            .ConfigureAwait(false);

        if (hasPlanPreflights && preflightsPassed)
        {
            io.Out.WriteLine("Full Flight Checks: passed.");
        }

        if (!preflightsPassed)
        {
            // Issue #436: this phase halts BEFORE the log site is ever created below, so a failed Full
            // Flight Check used to leave logs/<runId>/ holding the captured gate output (#432) and NO
            // index.html at all — the entry point the rest of the harness advertises simply did not exist
            // for the one outcome that most needs a post-mortem. Render the durable site now: all tasks
            // pending (none was scheduled) behind the gate-halt banner that says exactly that. Best-effort
            // and silent — the console lines below are unchanged.
            WriteDurableFinalSite(
                Path.Combine(probe.Plan.PlanDirectory, "logs", runId), probe.Plan, probe.Plan.PlanDirectory);

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

        // The log server's ONLY gate is --no-log-server (issue #552). It used to also require `live`,
        // which coupled an HTTP listener on loopback to the Spectre table's needs — an interactive,
        // ANSI-capable, non-redirected console. The listener needs none of those, and the coupling made
        // the consequence backwards: a headless / backgrounded / CI run has no console to watch, so it
        // is precisely the run that most needs a browser page, and it was the only one that could never
        // have had one. `live` still governs the table alone. A binding failure never aborts the run —
        // TryStart returns null and prints one warning. TryStart is passed the proceed-unreviewed posture
        // so the #387 v2 pick endpoints enforce the same non-answerable floor as the resume consumer.
        bool proceedUnreviewed =
            probe.Plan.Config.Autonomy?.GateThresholds?.ReviewGate == Core.Model.ReviewGateDecision.ProceedUnreviewed;
        LogServer? logServer = !noLogServer
            ? LogServer.TryStart(probe.Plan.PlanDirectory, runId, probe.Plan.Tasks, logPort, io.Out, proceedUnreviewed)
            : null;

        try
        {
            if (logServer is not null)
            {
                // The canonical "all tasks" page is the static index file (printed by
                // PrintStaticIndexLink below); this http server is just the tailing backend that the
                // static index links a RUNNING task to (issue #143). De-emphasised accordingly.
                // This goes to io.Out, so under `> run.log 2>&1` it lands in the redirected stream —
                // which is exactly where an unattended operator goes looking for it (#552).
                io.Out.WriteLine($"Live tailing server (active tasks): {logServer.BaseUrl}\n");
            }
            else
            {
                // #552: a run without a server must NAME the remedy rather than leave the operator to
                // discover a shipped verb from --help. `guardrails logs <folder>` starts the same live
                // tailing server against a run ALREADY IN FLIGHT (it serves the persisted logs and the
                // journal from disk), so it covers both reasons there is no server here: the operator
                // opted out with --no-log-server, or the bind failed (TryStart already printed why).
                string reason = noLogServer ? "--no-log-server" : "it could not start";
                // The line exists to be PASTED, so a folder path containing a space must arrive quoted
                // — otherwise the remedy we print is itself a broken command, and the operator's first
                // experience of the fix is a shell splitting their plan folder in half.
                string logsTarget = folder.Contains(' ', StringComparison.Ordinal) ? $"\"{folder}\"" : folder;
                io.Out.WriteLine(
                    $"Live log viewer not started ({reason}). Run `guardrails logs {logsTarget}` in another "
                    + "terminal for a live view.\n");
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
            Scheduler scheduler;

            // Issue #(event-vocabulary plan 35) — run-finished must fire on EVERY exit path, including an
            // unhandled fault out of the Scheduler (the largest fault surface in the process). The prior
            // `finally` below (issue #333, still unchanged and nested inside) sits INSIDE this try, with no
            // catch between it and the two ExecuteAsync call sites — so a throw out of ExecuteAsync unwound
            // straight past it. This outer try/catch/finally is the actual run-finished bracket: the chain
            // (diagramObserver), the resolved exit code, and the fault kind are all hoisted above it so the
            // finally can report on them regardless of which path was taken, including a throw before the
            // chain even exists (diagramObserver stays null, so `?.` below correctly raises nothing).
            OnTheFlyDiagramObserver? diagramObserver = null;
            int? resolvedExitCode = null;
            string? faultKind = null;
            try
            {
                if (live)
                {
                    // Write the initial all-pending index + the seeded live diagram AND print their links
                    // BEFORE constructing LiveRunObserver — its ctor starts the Spectre AnsiConsole.Live
                    // region, and any console write into an active Live region corrupts the table (#145 Bug 1).
                    // So both static writes + their links must precede the live region.
                    OnTheFlyLogSiteObserver.WriteInitialIndex(logsRoot, runId, probe.Plan.Tasks, logUrlForTask, probe.Plan.Waves);
                    PrintStaticIndexLink(logsRoot, io);    // "all tasks" page link at run START
                    OnTheFlyDiagramObserver.WriteInitialDiagram(logsRoot, probe.Plan, diagramSeed);
                    PrintDiagramLink(logsRoot, io);        // live status diagram link at run START

                    await using var liveObserver = new LiveRunObserver(
                        probe.Plan.Tasks, logUrlForTask, probe.Plan.PlanDirectory, runId,
                        probe.Plan.Waves, allTasks); // #379: collapse completed waves unless --all-tasks
                    diagramObserver = BuildObserverChain(liveObserver, logsRoot, runId, probe.Plan, logUrlForTask, diagramSeed, null, false);
                    (report, scheduler) = await ExecuteAsync(probe.Plan, diagramObserver, driftAuthorization, waveDriftAuthorized, breakdownConfirmations, junctionRootForRun, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    diagramObserver = BuildObserverChain(new ConsoleRunObserver(io.Out), logsRoot, runId, probe.Plan, logUrlForTask, diagramSeed, null, false);
                    OnTheFlyLogSiteObserver.WriteInitialIndex(logsRoot, runId, probe.Plan.Tasks, logUrlForTask, probe.Plan.Waves);
                    PrintStaticIndexLink(logsRoot, io);
                    diagramObserver.WriteInitialDiagram();
                    PrintDiagramLink(logsRoot, io);
                    (report, scheduler) = await ExecuteAsync(probe.Plan, diagramObserver, driftAuthorization, waveDriftAuthorized, breakdownConfirmations, junctionRootForRun, cancellationToken).ConfigureAwait(false);
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

                    // Issue #556 (plan 32 §6.5 correction 1) — NOT EVALUATED is a third state, and it is not
                    // "passed". The expression below used to be `!report.AllSucceeded || await EvaluateAsync(…)`,
                    // so every run that never reached the gate recorded that the gate PASSED. For an ordinary
                    // failed run that short-circuit is harmless shorthand — nothing downstream turns on it. For an
                    // executed-definition DIVERGENCE run it is a verdict that never happened, on a run whose every
                    // task settled `succeeded`, in the one change whose whole purpose is that the harness stops
                    // claiming verifications it did not perform. So this is a TRI-STATE: true = passed,
                    // false = failed, null = NOT EVALUATED. Only the divergence case is ever null, so every other
                    // run is byte-identical to before (`is true` / `is false` on a non-null bool? is the bool).
                    // The gate is deliberately not RUN either: evaluating a gate whose result cannot change the
                    // outcome spends real money for a number nobody acts on (§6.5).
                    bool? planGuardrailsPassed = report.HasExecutedDefinitionDivergence
                        ? null
                        : !report.AllSucceeded
                          || await PlanGuardrailPhase
                              .EvaluateAsync(probe.Plan, new ProcessRunner(), io.Out, runId, cancellationToken, junctionRootForRun)
                              .ConfigureAwait(false);

                    if (willEvaluateTerminalGate)
                    {
                        // willEvaluateTerminalGate implies report.AllSucceeded, which implies no divergence — so
                        // the tri-state is never null in here.
                        diagramObserver.PlanGuardrailsFinished(planGuardrailsPassed is true); // settle the bracket badge
                        if (planGuardrailsPassed is true)
                        {
                            io.Out.WriteLine("Terminal Gate: passed.");
                        }
                    }

                    // Issue #457 — DELIVERY HAPPENS HERE, NOT INSIDE THE SCHEDULER, for any plan declaring a
                    // <plan>/guardrails/ terminal gate. The Scheduler HELD the merge back (report
                    // .DeliveryPendingTerminalGate) precisely because its own `AllSucceeded` is TASKS ONLY and
                    // this gate's verdict did not exist yet. Now it does: a PASSED gate delivers exactly as
                    // before (#340 delivered-by-default is unchanged for a genuinely green run), and a FAILED
                    // gate simply never reaches this call — nothing merges to the user's branch, and the
                    // verified-but-ungated work stays on the plan branch where the halt message says it is.
                    //
                    // Placed BEFORE WriteFinalStatic/Finish so the outcome is in the report every downstream
                    // consumer reads: the exit-code mapping for a halted delivery (HookRejected /
                    // DirtyWorkingTree / Conflict / BranchMoved), the final static pages, the #340 notices,
                    // and the #407 reclaim predicate.
                    if (report.DeliveryPendingTerminalGate && planGuardrailsPassed is true)
                    {
                        report = scheduler.CompleteDeferredDelivery(report, cancellationToken);
                    }

                    // The FINAL, settled live diagram (no meta refresh, no spinner) — the durable post-mortem of
                    // the run, sourced from the observer's own in-memory map, mirroring the durable final log site
                    // Finish writes. Best-effort; never changes the exit code (issue #219, SSOT §10.1).
                    diagramObserver.WriteFinalStatic();

                    int exitCode = Finish(report, probe.Plan, runId, io); // also writes the durable final log site
                    finalSitesSettled = true; // both final pages are now settled on the normal path

                    // #387 v1: in an attended TTY, offer a one-click pick for any OPEN, options-carrying needsHuman
                    // escalation this run raised — the choice is written to the SAME reply channel (an answer file)
                    // and injected on the next resume (halt/resume). A no-op when not interactive or nothing is
                    // pickable; a NON-answerable escalation is never offered a pick (§7.3).
                    EscalationPickPrompt.OfferPicksInteractive(probe.Plan, runId, io);

                    // Issue #361 Phase 4 (doc 12 §5.2 Option P / §7.1): a run that PROCEEDED THROUGH one or more
                    // waves unreviewed is INDELIBLY flagged — render the permanent "ran with N unreviewed wave(s)"
                    // warning so the run can never read as clean green, regardless of the verdict resolved above.
                    // Placed before the exit-path branches below so it fires on every outcome (the fact is
                    // permanent, not conditional on the final verdict); the distinct ExitCodes.ProceededUnreviewed
                    // (5) that a wholly-green such run returns is mapped in Finish — this is its console companion
                    // (SSOT §7 rendering lives behind the CLI seam).
                    RenderUnreviewedWavesWarning(report, io.Out);

                    // Issue #545 part 3 (plan 31 §5.1/§5.4): the terminal surface of the mid-run plan-folder
                    // edit advisory. Placed beside the unreviewed-waves warning and BEFORE the exit-path
                    // branches below, so an operator who edited the plan folder is told regardless of how the
                    // run ended — including the terminal-gate-failure early return.
                    RenderPlanEditWarning(report, io.Out);

                    if (report.AllSucceeded && planGuardrailsPassed is false)
                    {
                        PrintTerminalGateFailure(probe.Plan.PlanDirectory, io);
                        resolvedExitCode = ExitCodes.TaskFailed; // overrides Finish's own (greener) verdict — never read that instead.
                        return ExitCodes.TaskFailed;
                    }

                    // Issue #542: journal the delivery outcome BEFORE rendering the banner, so the durable
                    // record exists even if the console is never read (or never seen at all — #496's unattended
                    // pipeline has no console). Written here, at the end, because delivery only fully resolves
                    // once the terminal gate's verdict is in (the DeliveryPendingTerminalGate path). Best-effort:
                    // a journal write must never flip a run's verdict this late.
                    try
                    {
                        // `is not false` rather than `?? true`: the parameter is consumed as "did the terminal
                        // gate REFUSE?", and a gate that was never evaluated did not refuse. The divergence run's
                        // own reason branch inside DescribeDelivery is reached first, so a NOT-EVALUATED gate
                        // never reaches the terminal-gate wording either way.
                        journal.RecordDelivery(
                            DescribeDelivery(report, planGuardrailsPassed is not false, probe.Plan.PlanDirectory));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        // The run's outcome stands; the banner below still tells the operator what happened.
                        // JsonException is in scope because RecordDelivery re-reads the journal from disk before
                        // writing (so it cannot clobber the run it is describing) — a corrupt journal must not
                        // turn a finished run into a harness error at the very last step.
                    }

                    // Issue #340: a WHOLLY-GREEN run (the DAG green AND the terminal gate passed) whose
                    // verified work was NOT delivered — mergeOnSuccess resolved off — must be impossible to
                    // miss. The plan branch alone carries the work, one --fresh/reset -y away from destruction.
                    RenderUndeliveredWorkWarning(report, planGuardrailsPassed is true, probe.Plan.PlanDirectory, io.Out);

                    // Issue #340 complement: when delivery fired PURELY because of the new default (neither the
                    // config key nor a CLI flag was set), print a one-time notice naming the branch + the opt-out,
                    // so the breaking default is observable/self-documenting rather than a silent surprise. Never
                    // fires together with the undelivered warning (that requires delivery OFF; this requires it ran).
                    RenderDeliveredByDefaultNotice(report, deliveryFromDefaultOnly, io.Out);

                    // #407 A — terminal-completion cleanup. A wholly-green, terminal-gate-passed, DELIVERED run
                    // is non-resumable, so reclaim its junction LINK + worktree root NOW. KEEP both for every
                    // RESUMABLE outcome — needs-human / halt / cancelled (exitCode != green) and the wholly-green
                    // -but-UNDELIVERED opt-out (its verified work sits on the plan branch for the user to inspect /
                    // deliver, so its integration worktree must survive) — where a resume needs them. The startup
                    // GC (B) is the backstop for crashed/abandoned leaks that never reach here. Cross-platform: the
                    // gr-wt root leaks on every OS; the junction is Windows-only (RemoveJunctionLink no-ops else).
                    // #419: the junction link comes from the IN-MEMORY run-scoped value now (no longer journaled);
                    // A removes it here for a green-delivered run, and the `junctionLifetime` Dispose (below) is
                    // then a no-op. For a resumable outcome A is skipped and `junctionLifetime` removes just the
                    // link, leaving the root for the resume.
                    bool terminalGreen = WorktreeReclaim.ShouldReclaimOnCompletion(report, planGuardrailsPassed is true);
                    if (terminalGreen && worktreeMode && realWorktreeRootForRun is { } completedRoot)
                    {
                        WorktreeReclaim.CleanupCompletedRun(
                            probe.Plan.Workspace, completedRoot, junctionRootForRun, io.Out);
                    }

                    resolvedExitCode = exitCode; // the exact value about to be returned — never overridden after this point.
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
            catch (Exception ex)
            {
                // #(event-vocabulary plan 35) — the fault surface this bracket exists for: an unhandled throw
                // out of ExecuteAsync (most notably Scheduler.RunAsync) unwinds straight through the DAG and the
                // terminal-gate phase above with no catch until here. Record ONLY the exception's TYPE NAME —
                // never ex.Message, which can carry an absolute path, a token, or a fragment of source destined
                // for an operator-supplied webhook URL — then rethrow bare so the exception propagates unchanged;
                // this catch exists solely to observe it, never to handle it.
                faultKind = ex.GetType().Name;
                throw;
            }
            finally
            {
                // diagramObserver is null only if the throw happened before the chain was built (BuildObserverChain
                // never ran) — `?.` makes that correctly raise nothing rather than a NullReferenceException.
                diagramObserver?.RunFinished(resolvedExitCode, faultKind);
            }
        }
        finally
        {
            if (logServer is not null)
            {
                await logServer.DisposeAsync().ConfigureAwait(false);
            }

            // #419 exit-root sweep — a worktree-mode run reclaims this session's ABANDONED roots on its way
            // out (the #408 gap: the startup GC B fires only at the START of a run, so a session's LAST run
            // never swept). ROOT-only (the junction link is released by `junctionLifetime`), count-capped +
            // best-effort so it never delays the visible exit, and it EXCLUDES this run's own root (kept for
            // a resumable outcome, and doubly protected by its still-live process lock).
            if (worktreeMode && realWorktreeRootForRun is { } exitRoot)
            {
                WorktreeReclaim.ReclaimRootsOnExit(probe.Plan.Workspace, exitRoot, io.Out);
            }
        }
    }

    /// <summary>
    /// The installed skill version for the run-environment record (plan 30 §3.4): the first bundled
    /// skill (<c>plan-breakdown</c> / <c>guardrails-review</c> / <c>guardrails-domain-knowledge</c> — read
    /// from beside the running tool, same as <see cref="VersionWithDriftAction"/>'s drift check) found
    /// installed under the user-level or project-level <c>.claude/skills</c> scan root, in that order.
    /// Reuses <see cref="SkillVersionReport.Build"/> — the shipped locate-and-read pipeline over
    /// <see cref="SkillFrontmatter.ReadGuardrailsVersion"/> — rather than re-walking paths by hand. Null
    /// when no bundled-skills folder ships with this build, or nothing is installed, or nothing installed
    /// carries a version: a null skill version is a true and useful answer ("no skill installed"), never
    /// fabricated.
    /// </summary>
    private static string? ResolveInstalledSkillVersion()
    {
        string bundledSkillsDir = Path.Combine(AppContext.BaseDirectory, "skills");
        if (!Directory.Exists(bundledSkillsDir))
        {
            return null;
        }

        IReadOnlyList<string> knownSkillNames = Directory
            .EnumerateDirectories(bundledSkillsDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();

        if (knownSkillNames.Count == 0)
        {
            return null;
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        IReadOnlyList<string> scanRoots =
        [
            Path.Combine(userProfile, ".claude", "skills"),
            Path.Combine(Directory.GetCurrentDirectory(), ".claude", "skills")
        ];

        return SkillVersionReport.Build(GuardrailsVersion.Current, knownSkillNames, scanRoots)
            .Select(status => status.InstalledVersion)
            .FirstOrDefault(version => version is not null);
    }

    /// <summary>
    /// The built-in <c>maxCostUsd</c> ceiling applied when <c>--autonomous</c> is used but neither the config
    /// nor a cost flag sets one (doc 12 §3.4, decided §10 I). An unattended run must never run uncapped, so
    /// this conservative default is applied with a LOUD warning rather than left absent.
    /// </summary>
    private const decimal AutonomousDefaultMaxCostUsd = 20m;

    /// <summary>
    /// Resolve the autonomous-mode flags (<c>--autonomous</c> / <c>--dial</c> / <c>--max-cost-usd</c>) into the
    /// effective run-wide autonomy end-state (doc 12 §3.4; decided §10 I/N), BEFORE the plan runs so the
    /// resolution is observable under <c>--dry-run</c>. It:
    /// <list type="bullet">
    /// <item>validates <c>--dial</c> (an unrecognised level is a usage error naming the offending token);</item>
    /// <item>applies the conservative <c>--autonomous</c> default dial (<c>escalationThreshold: high</c> — used
    ///   only when the config omits an autonomy block) and lets <c>--dial</c> override the run-wide threshold;</item>
    /// <item>re-runs the reusable GR2040 predicate
    ///   (<see cref="Core.Loading.PlanValidator.ViolatesCompoundConfig"/>) on the POST-FLAG effective config
    ///   (B1) — the flags mutate the config AFTER load-time validation, so the load-time GR2040 never saw this
    ///   end-state; the forbidden <c>critical</c> + <c>proceed-unreviewed</c> compound is refused here;</item>
    /// <item>enforces the required cost cap under <c>--autonomous</c>: when neither the config nor
    ///   <c>--max-cost-usd</c> sets one, applies the built-in <c>$20</c> default with a LOUD warning;</item>
    /// <item>prints a concise resolved-autonomy summary line.</item>
    /// </list>
    /// Returns a non-null exit code when the run must STOP (an invalid dial, or a GR2040 violation); returns
    /// <c>null</c> to proceed, with <paramref name="dialOverride"/> / <paramref name="maxCostOverride"/> carrying
    /// the resolved overrides for the executing run (<see cref="RunAsync"/>) to apply. When neither
    /// <c>--autonomous</c> nor <c>--dial</c> is present the dial is inert (doc 12 §3.2 back-compat): nothing is
    /// printed, and only a bare <c>--max-cost-usd</c> flag (if any) is passed through as a cost override.
    /// </summary>
    private static int? ResolveAutonomousMode(
        string folder, bool autonomous, string? dial, decimal? maxCostFlag,
        out Core.Model.EscalationThreshold? dialOverride, out decimal? maxCostOverride, IConsoleIo io)
    {
        dialOverride = null;
        maxCostOverride = maxCostFlag; // a bare --max-cost-usd still overrides the run's cost cap.

        // --dial validation is a pure usage error (no plan needed) — reject an unrecognised level, naming it.
        if (!string.IsNullOrWhiteSpace(dial))
        {
            if (!Core.Model.EscalationThresholds.TryParse(dial, out Core.Model.EscalationThreshold parsedDial))
            {
                io.Out.WriteLine(
                    $"Unknown --dial value '{dial}'. Expected 'low', 'moderate', 'high', or 'critical' " +
                    "(the lowest criticality that still escalates; doc 12 §3.4).");
                return ExitCodes.HarnessError;
            }

            dialOverride = parsedDial;
        }

        // Neither knob engaged ⇒ the dial is inert; the run behaves byte-for-byte as before (doc 12 §3.2).
        if (!autonomous && dialOverride is null)
        {
            return null;
        }

        // The effective end-state needs the config (its autonomy block + its cost cap). A plan that fails
        // validation is left to the normal run/dry-run path to report — resolution is moot for a plan that
        // will not run, and re-reporting the diagnostics here would be noise.
        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            return null;
        }

        Core.Model.RunConfig config = probe.Plan.Config;

        // Build the POST-FLAG effective autonomy config. --autonomous installs the conservative default block
        // (escalationThreshold high, §10 N) ONLY when the config omits one; --dial then overrides the run-wide
        // threshold, preserving any per-gate overrides the block carries.
        Core.Model.AutonomyConfig? effective = config.Autonomy;
        if (autonomous && effective is null)
        {
            effective = new Core.Model.AutonomyConfig();
        }

        if (dialOverride is { } level)
        {
            effective = (effective ?? new Core.Model.AutonomyConfig()) with { EscalationThreshold = level };
        }

        // GR2040 on the EFFECTIVE post-flag config (B1). The reusable predicate is the single source of the
        // message so this re-check reports identically to the load-time one.
        if (effective is not null &&
            Core.Loading.PlanValidator.ViolatesCompoundConfig(effective, out string compoundDiagnostic))
        {
            io.Out.WriteLine();
            io.Out.WriteLine($"{Core.Loading.DiagnosticCodes.IncompatibleAutonomyCompoundConfig}: {compoundDiagnostic}");
            io.Out.WriteLine("Refusing to run; nothing was executed.");
            return ExitCodes.HarnessError;
        }

        // Required cost cap under --autonomous (§3.4 liveness floor): an unattended run must never run uncapped.
        // When neither the config nor --max-cost-usd sets one, apply the built-in $20 default and LOUDLY warn;
        // when a cap IS set, no default and no warning.
        decimal? effectiveMaxCost = maxCostFlag ?? config.MaxCostUsd;
        bool appliedDefaultCap = autonomous && effectiveMaxCost is null;
        if (appliedDefaultCap)
        {
            effectiveMaxCost = AutonomousDefaultMaxCostUsd;
        }

        maxCostOverride = maxCostFlag ?? (appliedDefaultCap ? AutonomousDefaultMaxCostUsd : (decimal?)null);

        if (appliedDefaultCap)
        {
            io.Out.WriteLine();
            io.Out.WriteLine(
                "WARNING: --autonomous requires a cost cap but none is set — no maxCostUsd in guardrails.json " +
                $"and no --max-cost-usd flag. Applying the built-in default maxCostUsd=${AutonomousDefaultMaxCostUsd} " +
                "so this unattended run cannot run uncapped. Pass --max-cost-usd (or set \"maxCostUsd\" in " +
                "guardrails.json) to choose your own ceiling.");
            io.Out.WriteLine();
        }

        // Concise resolved-autonomy summary — observable under --dry-run (the tests assert on it).
        Core.Model.EscalationThreshold threshold =
            effective?.EscalationThreshold ?? Core.Model.EscalationThreshold.High;
        string costPart = effectiveMaxCost is { } cost
            ? $", maxCostUsd=${cost}" + (appliedDefaultCap ? " (built-in default)" : string.Empty)
            : string.Empty;
        string policyPart = autonomous ? ", autonomyPolicy=auto" : string.Empty;
        io.Out.WriteLine(
            $"Resolved autonomy: escalationThreshold={threshold.ToString().ToLowerInvariant()}{costPart}{policyPart}.");

        return null;
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
    ///
    /// <para>On a WAVED plan this is one line PER AUTHORED WAVE (issues #472/#488) — un-authored JIT stubs
    /// are silent here and are picked up at their own checkpoint, where <c>Scheduler.EscalateReviewGate</c>
    /// already raises the review gate on the same <c>WaveDefinitionHash</c> the marker keys on. So the
    /// promise in §13 — the nudge is evaluated for a wave before that wave runs — is kept by the two
    /// surfaces together, without a plan-level line that fires on every healthy JIT run.</para>
    /// </summary>
    public static void WarnIfUnreviewed(Core.Model.PlanDefinition plan, bool skipReviewCheck, IConsoleIo io)
    {
        if (skipReviewCheck)
        {
            return;
        }

        IReadOnlyList<Core.Loading.Diagnostic> nudges =
            Core.Loading.PlanValidator.ReviewMarkerDiagnostics(plan, Core.Review.ReviewNudgeSurface.Run);
        if (nudges.Count == 0)
        {
            return;
        }

        foreach (Core.Loading.Diagnostic nudge in nudges)
        {
            io.Out.WriteLine(nudge.ToString());
        }

        io.Out.WriteLine();
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

        // Run-end telemetry ingest (#535 charter §9) — the same trip, one seam over, and for the same
        // reason: the journal is final here, so this run's own attempts can go into the local corpus with
        // nobody typing `guardrails telemetry ingest`. Placed BELOW the definition-drift early return above
        // (that halt ran nothing and wrote no logs, so it has nothing to ingest) and ABOVE every remaining
        // exit path, so a green run, a needs-human run, an aborted run and a halted one all ingest alike —
        // the failed attempts are precisely the evidence a model comparison is made of. Best-effort in the
        // strongest sense: it cannot change the exit code, and it cannot suppress the summary below.
        IngestRunTelemetry(plan, io);

        PrintSummary(report, planDirectory, runId, io);

        // The "all tasks" static page link at run END (alongside the post-mortem logs pointer).
        PrintStaticIndexLink(logsRoot, io);

        // Issue #556 — the executed-definition divergence halt (plan 32 §6.5). Rendered HERE, in the NORMAL
        // end-of-run path AFTER the summary, and deliberately NOT at the DefinitionDrift early return above:
        // that return is correct for drift precisely because nothing ran and no logs were written, whereas a
        // divergence run EXECUTED EVERY TASK. Returning there would skip WriteDurableFinalSite,
        // IngestRunTelemetry (#535), PrintSummary and PrintStaticIndexLink — discarding the logs, telemetry
        // and summary of a run that did the whole plan's work. So this changes only the HEADLINE; the exit
        // code follows from AllSucceeded's new term (§6.5's consumer table) and lands on ExitCodes.TaskFailed
        // (2 — actionable/needs-human) below, never 1, which is reserved for infrastructure faults.
        //
        // It is placed BEFORE the abort / wave-halt / cancelled branches rather than competing with them:
        // each of those still prints its own headline last and keeps its own exit code, and the divergence
        // block is additive on the runs that carry both. The run this plan exists for — every task green,
        // delivery blocked — reaches none of them, and would otherwise go completely QUIET, since
        // AllSucceeded going false also suppresses the "*** WORK NOT DELIVERED ***" banner.
        if (report.ExecutedDefinitionDivergence is { } divergence)
        {
            PrintExecutedDefinitionDivergence(divergence, plan, io);
        }

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
        // (a git hook rejected the user-facing merge, a conflict, a dirty user tree, or — #588 — a
        // checkout that moved off the branch the run pinned) is NOT a
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

        if (report.AllSucceeded)
        {
            // Autonomous-mode proceeded-unreviewed run (SSOT §7.1, issue #361 Phase 4; Option P, §5.2). A run
            // that drained WHOLLY GREEN but PROCEEDED THROUGH one or more waves unreviewed
            // (RunReport.UnreviewedWaveCount > 0 — the count RunOutcomePolicy derived from the recorded
            // proceeded-unreviewed decisions, stamped by the Scheduler's Finalize) is NOT clean green: surface
            // the DISTINCT ProceededUnreviewed (5) so a firstmate consumer can tell it apart from an ordinary
            // success (0). This is the otherwise-green case only — an unresolved escalation ends the run
            // NON-green and is handled below (EscalationsPending, 4), so the two never mask each other.
            if (report.UnreviewedWaveCount > 0)
            {
                return ExitCodes.ProceededUnreviewed;
            }

            return ExitCodes.Success;
        }

        // Autonomous-mode answer-required halt (SSOT §7.1, issue #361 Phase 3). A run driven under
        // classify-then-act escalation ends NON-green with the escalated task settled needs-human — but that
        // is DISTINCT from a plain needs-human: the wired FileEscalationSink left an OPEN escalation record
        // awaiting a firstmate answer (logs/<runId>/escalations/<seq>-<gate>.json, status "open", §7.6).
        // Surface the distinct EscalationsPending code so a consumer never reads an answer-required halt as a
        // plain needs-human (2) — and never as green (0). A non-autonomous needs-human writes no such record
        // and still returns TaskFailed.
        if (HasUnresolvedEscalation(planDirectory, runId))
        {
            return ExitCodes.EscalationsPending;
        }

        return ExitCodes.TaskFailed;
    }

    /// <summary>
    /// True when this run ended with at least one UNRESOLVED escalation (SSOT §7.1/§7.6, issue #361 Phase 3):
    /// an autonomous-mode <see cref="Core.Execution.FileEscalationSink"/> record under
    /// <c>logs/&lt;runId&gt;/escalations/</c> whose <c>status</c> is still <c>open</c> — not yet flipped to
    /// <c>consumed</c> by a resume's answer-injection. This is the answer-required-halt signal the exit-code
    /// mapping branches on to return <see cref="ExitCodes.EscalationsPending"/> instead of a plain
    /// <see cref="ExitCodes.TaskFailed"/>. Best-effort: an unreadable/corrupt record is skipped (a read hiccup
    /// must neither manufacture nor mask the distinct code), and a run with no <c>escalations/</c> dir returns
    /// false. The sibling <c>&lt;seq&gt;-&lt;gate&gt;.answer.json</c> reply files carry no <c>status</c>, so
    /// they are naturally ignored.
    /// </summary>
    private static bool HasUnresolvedEscalation(string planDirectory, string runId)
    {
        string escalationsDir = Path.Combine(planDirectory, "logs", runId, "escalations");
        if (!Directory.Exists(escalationsDir))
        {
            return false;
        }

        foreach (string recordPath in Directory.EnumerateFiles(escalationsDir, "*.json"))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(recordPath));
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("status", out JsonElement status)
                    && status.ValueEquals("open"))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // A record that cannot be read must not manufacture the distinct code — skip it.
            }
            catch (UnauthorizedAccessException)
            {
                // As above — best-effort.
            }
            catch (JsonException)
            {
                // A corrupt/partial record is skipped, not treated as open.
            }
        }

        return false;
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

        /// <summary>
        /// The operator answered <c>a</c> (issue #545) — ACCEPT the drift: re-baseline the drifted tasks'
        /// recorded definition hashes without re-running them, and carry on from where the run stopped.
        /// </summary>
        Accepted,

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

        // Issue #545: state the COST OF EACH BRANCH, because that is what the decision actually turns on.
        // The harness knows both numbers and used to print only one of them - "re-run set: 14 tasks" is a
        // very different proposition once you can see "remaining: 2 tasks" beside it, and an operator who
        // cannot see the second number has no way to tell a cheap rewind from an expensive one.
        int remaining = journal.Document.Tasks.Count(t => t.Value.Status != Core.Journal.TaskStatus.Succeeded);
        string rewind = drift.Decision.Outcome == SafeSuffixOutcome.Safe
            ? $"rewind the plan branch ({drift.Decision.RemovedCommitCount} commit(s)) and re-run {drift.SafeSet.Count} task(s)"
            : $"reset and re-run {drift.SafeSet.Count} task(s)";

        // Issue #556 (plan 32 §6.6) — [a] is REFUSED for a DIVERGENCE-ORIGINATED drift, and this is the
        // branch that would otherwise re-create the exact lie #556 is about. [a] calls RecordDriftAccepted,
        // which OVERWRITES the recorded hash with current disk and does NOT re-run the task; reached from a
        // divergence halt that leaves the journal saying the task was built against the new definition when
        // it was built against the old one. It is worse than the original defect, because it also
        // UN-CORROBORATES the plan branch: the task's commit still carries the old Guardrails-Task-Hash:
        // trailer while the journal now carries the new hash, so SafeSuffixEvaluator's trailer-corroboration
        // rule refuses any later Part C rewind covering that task and steers the operator to a full
        // `guardrails reset -y`.
        //
        // The condition needs NO new state: a task whose journal entry carries definitionHashAtSettle (§6.3)
        // is BY CONSTRUCTION one that ran a definition it does not match, and accepting its current disk hash
        // is never sound. Computed over drift.Drifted — precisely the entries [a] would re-baseline below.
        //
        // [a]'s behaviour for an ORDINARY between-runs edit is UNCHANGED (§12): that trade is already
        // reviewed and is not this change's to relitigate. So this is a branch AROUND the accept handler for
        // one class of task, never its removal.
        string folder = Path.GetFileName(
            plan.PlanDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        List<string> divergenceOriginated = drift.Drifted
            .Where(d => journal.Document.Tasks.TryGetValue(d.TaskId, out Core.Journal.TaskJournalEntry? entry)
                        && entry.DefinitionHashAtSettle is not null)
            .Select(d => d.TaskId)
            .ToList();
        bool acceptOffered = divergenceOriginated.Count == 0;

        io.Out.WriteLine();
        io.Out.WriteLine($"  [y] {rewind}.");
        if (acceptOffered)
        {
            io.Out.WriteLine(
                $"  [a] ACCEPT the drift and continue: re-baseline the drifted task(s) WITHOUT re-running them, "
                + $"then finish the {remaining} task(s) that remain.");
            io.Out.WriteLine(
                "      The delivered artifact then predates its own definition - a real trade, recorded in");
            io.Out.WriteLine(
                "      decisions[] and named in the run report, because nothing else would show it afterwards.");
        }
        else
        {
            io.Out.WriteLine(
                $"  [a] is NOT offered here. {divergenceOriginated.Count} of these task(s) settled against a definition that had");
            io.Out.WriteLine(
                "      ALREADY moved during their own run, so accepting the current hash would record a");
            io.Out.WriteLine(
                "      verification that never happened - and would leave their plan-branch trailer");
            io.Out.WriteLine(
                "      uncorroborated, so a later scoped rewind refuses and only a full reset gets you out.");
            io.Out.WriteLine(
                $"      Re-run them instead:  guardrails reset {folder} {string.Join(" ", divergenceOriginated)}");
        }

        io.Out.WriteLine("  [N] abort - change nothing and stop.");
        io.Out.Write(acceptOffered ? "Choose [y/a/N] " : "Choose [y/N] ");

        string answer = (Console.ReadLine() ?? "").Trim();

        if (acceptOffered && answer.Equals("a", StringComparison.OrdinalIgnoreCase))
        {
            foreach (DefinitionDriftProbe.DriftedEntry d in drift.Drifted)
            {
                journal.RecordDriftAccepted(d.TaskId, d.NewHash);
                journal.RecordDecision(new DecisionEntry
                {
                    Boundary = "drift",
                    Policy = "prompt",
                    Decision = DecisionTokens.DriftAccepted,
                    Subject = d.TaskId,
                    Headline =
                        $"Definition drift ACCEPTED (not re-run): '{d.TaskId}' was re-baselined "
                        + $"{ShortHash(d.OldHash)} -> {ShortHash(d.NewHash)} and the run continued.",
                    Detail =
                        "The operator chose accept-and-continue over a rewind of "
                        + $"{drift.SafeSet.Count} task(s). This task's delivered output was produced against "
                        + "the OLD definition and was NOT rebuilt against the new one."
                });
            }

            io.Out.WriteLine();
            io.Out.WriteLine(
                $"Accepted — {drift.Drifted.Count} task(s) re-baselined without re-running; continuing with "
                + $"{remaining} task(s) remaining.");
            io.Out.WriteLine(
                "  Recorded in decisions[] as 'drift-accepted'. Their output predates the current definition.");
            return (DriftPromptDecision.Accepted, null);
        }

        if (!answer.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            // Issue #545: the old message was "Declined - nothing was changed", which reads as a safe no-op
            // and is not - the run is STRANDED, and the operator has to work out for themselves that the
            // only way forward is a BYTE-EXACT restore. Say that, because a semantic revert hits this same
            // halt and looks like the harness ignoring the fix.
            io.Out.WriteLine();
            if (!acceptOffered && answer.Equals("a", StringComparison.OrdinalIgnoreCase))
            {
                // The operator typed the option that was withdrawn above. Say so, rather than letting the
                // generic abort read as if the keystroke was simply not understood (#556 §6.6).
                io.Out.WriteLine(
                    "[a] was refused: re-baselining a task that settled against a definition it does not match");
                io.Out.WriteLine(
                    "would record a verification that never happened.");
            }

            io.Out.WriteLine("Aborted — nothing was changed (definition-drift halt, SSOT §7.2).");
            io.Out.WriteLine("  This run CANNOT resume while the drifted definition(s) differ. To get moving:");
            if (acceptOffered)
            {
                io.Out.WriteLine(
                    "    - re-run and answer [a] to accept the drift and finish the remaining task(s); or");
            }
            else
            {
                // Do not advertise a route that was just refused above (#556 §6.6).
                io.Out.WriteLine(
                    $"    - guardrails reset {folder} {string.Join(" ", divergenceOriginated)} — re-run the task(s) that settled");
                io.Out.WriteLine(
                    "      against a definition that had already moved; or");
            }

            io.Out.WriteLine(
                "    - re-run and answer [y] to rewind and rebuild the re-run set; or");
            io.Out.WriteLine(
                "    - restore the drifted file(s) to their previous content and re-run. The comparison is a");
            io.Out.WriteLine(
                "      HASH, so the restore must be BYTE-EXACT - a semantically equivalent edit still drifts.");
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
            WaveHaltKind.BreakdownIncomplete => "WAVE BREAKDOWN INCOMPLETE",
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
                or WaveHaltKind.BreakdownIncomplete
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

    /// <summary>
    /// Render the issue #556 executed-definition divergence halt (plan 32 §6.5/§9): one or more tasks
    /// SETTLED against a definition that had already moved on disk, so the run is green on every task and
    /// still must not deliver.
    ///
    /// <para><b>It names all three facts an operator needs</b>, because this is the one place a half-true
    /// message actively misleads: (1) WHICH definition files moved — the gate's own map diff, not a
    /// re-derivation; (2) that the attempt ran the <b>pinned</b> bytes, the ones its guardrails actually
    /// verified, so the journal records no verification that did not happen; and (3) that <c>task.json</c> and
    /// the DAG are held from LOAD for the whole run while action prompts and guardrail scripts are RE-READ per
    /// attempt — the asymmetry that makes "your edit was ignored" false.</para>
    ///
    /// <para><b>It carries no remediation vocabulary of its own.</b> §6.6: <i>"C is A's finding delivered one
    /// run earlier"</i> — the next run's §7.2 drift pre-pass halts on exactly this task set with exactly these
    /// paths, so naming a fourth one here would invent a remedy the other halt does not honour.</para>
    /// </summary>
    private static void PrintExecutedDefinitionDivergence(
        Core.Execution.ExecutedDefinitionDivergenceReport divergence, Core.Model.PlanDefinition plan, IConsoleIo io)
    {
        TextWriter output = io.Out;
        string folder = Path.GetFileName(
            plan.PlanDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        int count = divergence.Tasks.Count;
        string taskWord = count == 1 ? "task" : "tasks";

        output.WriteLine();
        output.WriteLine(
            $"DEFINITION MOVED DURING THIS RUN — halting; {count} {taskWord} settled against a definition that");
        output.WriteLine("had already changed on disk (SSOT §7.2, issue #556).");
        output.WriteLine("Nothing was discarded: each task below settled 'succeeded' and its work is retained.");
        output.WriteLine("Nothing was delivered: the end-of-run merge to your branch did not run.");

        foreach (Core.Execution.DivergedTask t in divergence.Tasks)
        {
            output.WriteLine();
            output.WriteLine($"  {t.TaskId}");
            output.WriteLine(
                $"    definition hash: {ShortHash(t.HashAtLoad)} (executed) -> {ShortHash(t.HashAtSettle)} (on disk at settle)");

            if (t.MovedFiles.Count > 0)
            {
                output.WriteLine("    moved files:");
                foreach (Core.Execution.ChangedDefinitionFile f in t.MovedFiles)
                {
                    output.WriteLine($"      - {f.Path}  {f.Change}{FormatDelta(f)}");
                }
            }
        }

        output.WriteLine();
        output.WriteLine("What actually ran:");
        output.WriteLine(
            "  - Each task above ran the PINNED definition — the bytes as this run loaded them, which are the");
        output.WriteLine(
            "    bytes its guardrails verified. That pinned hash is what the journal records, so nothing claims");
        output.WriteLine(
            "    a verification against the definition now on disk.");
        output.WriteLine(
            "  - task.json (writeScope, dependsOn, retries, maxTurns) and the DAG are HELD FROM LOAD for the");
        output.WriteLine(
            "    whole run. An action prompt and a guardrail script are RE-READ on every attempt, so an edit to");
        output.WriteLine(
            "    either was already in force from that task's next attempt onward — it was not ignored.");

        if (plan.PlanGuardrails.Count > 0)
        {
            output.WriteLine(
                "  - The terminal plan-guardrail gate was NOT EVALUATED: its verdict could not change this");
            output.WriteLine(
                "    outcome, so it was not spent. It is not recorded as passed.");
        }

        output.WriteLine();
        output.WriteLine("Remediation — the next run reports this same set as definition drift (SSOT §7.2):");
        output.WriteLine($"  guardrails run {folder} --autonomy auto    — rewind the plan branch past the drifted set + re-run");
        output.WriteLine($"  guardrails reset {folder} <taskId>...      — scoped reset of only the task(s) above + descendants");
        output.WriteLine($"  guardrails reset {folder} -y               — full correct rebuild (always sound)");
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
    /// <summary>
    /// Derive the durable delivery record (SSOT §7 <c>delivery</c>, issue #542) from the finished run — the
    /// machine-readable, on-disk counterpart of <see cref="RenderUndeliveredWorkWarning"/>'s banner.
    /// <para>
    /// <b>Why this exists.</b> The banner is the right OPERATOR surface and it works; it is also
    /// terminal-only. Once the terminal is closed nothing on disk answered "did this run deliver?", so the
    /// one outcome that determines whether the work is anywhere was the one outcome the otherwise-complete
    /// journal did not record. That is not hypothetical: a wholly-green run was read as shipped and two
    /// issues were closed against a plan branch that had never been merged.
    /// </para>
    /// <para>
    /// Pure, and public + unit-tested for the same reason <see cref="RenderUndeliveredWorkWarning"/> is —
    /// the Cli assembly ships no <c>InternalsVisibleTo</c>.
    /// </para>
    /// </summary>
    public static DeliverySection DescribeDelivery(
        RunReport report, bool terminalGatePassed, string planDirectory)
    {
        ArgumentNullException.ThrowIfNull(report);

        string planBranch = "guardrails/" + Path.GetFileName(
            planDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        // The merge-back RAN: its own result is the whole story, success or refusal.
        if (report.MergeOnSuccessOutcome is { } outcome)
        {
            bool delivered = outcome is MergeOnSuccessResult.FastForwarded or MergeOnSuccessResult.Merged;
            DeliveryOutcome token = outcome switch
            {
                MergeOnSuccessResult.FastForwarded => DeliveryOutcome.FastForwarded,
                MergeOnSuccessResult.Merged => DeliveryOutcome.Merged,
                MergeOnSuccessResult.Conflict => DeliveryOutcome.Conflict,
                MergeOnSuccessResult.DirtyWorkingTree => DeliveryOutcome.DirtyWorkingTree,
                MergeOnSuccessResult.HookRejected => DeliveryOutcome.HookRejected,
                MergeOnSuccessResult.BranchMoved => DeliveryOutcome.BranchMoved,
                _ => DeliveryOutcome.NotAttempted
            };

            return new DeliverySection
            {
                Delivered = delivered,
                Outcome = token,
                Reason = delivered
                    ? null
                    : $"the end-of-run merge to your branch was refused ({JournalJson.DeliveryOutcomeToken(token)}); "
                      + $"the verified work is on '{planBranch}'",
                PlanBranch = delivered ? null : planBranch,
                DeliveredToBranch = report.DeliveredToBranch,
                Detail = report.MergeOnSuccessDetail,
            };
        }

        // The merge-back never ran. WHY is the part a later reader cannot reconstruct, and the ordering here
        // matters: the undelivered flag is the case that strands work, so it is reported ahead of the
        // reasons that merely mean there was nothing to deliver.
        // Issue #556 (plan 32 §6.5 correction 2): the divergence case needs its OWN reason and gets it ahead
        // of the generic non-green one, which would otherwise write "the run was not wholly green" into
        // run.json for a run whose tasks{} shows every task `succeeded` — a record that contradicts the
        // document it is written into. #542 exists so an unattended pipeline with no console has a
        // machine-readable answer, and a wrong one is worse than none. It names no plan branch, for the same
        // reason serial mode does not: in serial mode there is no plan branch to send anyone to.
        string reason = report.WhollyGreenButUndelivered
            ? $"mergeOnSuccess resolved off, so this wholly-green run's verified work is sitting on "
              + $"'{planBranch}' and NOT on your checkout; a later --fresh or 'reset -y' destroys it"
            : report.ExecutedDefinitionDivergence is { } divergence
                ? $"{divergence.Tasks.Count} task(s) settled against a definition that had already moved on "
                  + "disk, so delivery was blocked (issue #556); every task succeeded and its verified work "
                  + "is retained, but it was verified against bytes that are no longer there — re-run to "
                  + "rebuild against the current definition"
                : !terminalGatePassed
                    ? "the terminal gate did not pass, so delivery was never attempted and the work stayed on the plan branch"
                    : !report.AllSucceeded
                        ? "the run was not wholly green, so delivery was never attempted"
                        : "no separate plan branch was in play (serial mode), so there was nothing pending delivery — "
                          + "the work is already in your checkout";

        return new DeliverySection
        {
            Delivered = false,
            Outcome = DeliveryOutcome.NotAttempted,
            Reason = reason,
            PlanBranch = report.WhollyGreenButUndelivered ? planBranch : null,
        };
    }

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
    /// Render the permanent "ran with N unreviewed wave(s)" flag (issue #361 Phase 4; doc 12 §5.2 Option P /
    /// §7.1) when the run PROCEEDED THROUGH one or more waves UNREVIEWED
    /// (<see cref="RunReport.UnreviewedWaveCount"/> &gt; 0). The wave(s) ran under
    /// <c>autonomy.gateThresholds.review-gate: proceed-unreviewed</c> with NO human review and NO forged review
    /// marker (§5 floor 3); the run is INDELIBLY flagged so neither an operator nor an automated firstmate
    /// consumer can mistake it for a clean green run — the loud console companion of the distinct
    /// <see cref="ExitCodes.ProceededUnreviewed"/> exit code that a wholly-green such run returns. Silent for a
    /// run that never advanced past an unreviewed wave (count 0). Pure (writes only to
    /// <paramref name="output"/>) and public + unit-tested with a <see cref="StringWriter"/> — the Cli assembly
    /// ships no InternalsVisibleTo (same rationale as <see cref="Hyperlink"/>).
    /// </summary>
    public static void RenderUnreviewedWavesWarning(RunReport report, TextWriter output)
    {
        if (report.UnreviewedWaveCount <= 0)
        {
            return;
        }

        int count = report.UnreviewedWaveCount;
        string waveWord = count == 1 ? "wave" : "waves";
        const string rule = "==============================================================================";

        output.WriteLine();
        output.WriteLine(rule);
        output.WriteLine($"*** RAN WITH {count} UNREVIEWED {waveWord.ToUpperInvariant()} ***");
        output.WriteLine(
            $"This run proceeded through {count} unreviewed {waveWord} (review-gate: proceed-unreviewed) — no");
        output.WriteLine(
            "human reviewed the work and the harness wrote no review marker (§5 floor 3). The run is");
        output.WriteLine(
            $"permanently flagged and exits {ExitCodes.ProceededUnreviewed} when otherwise green — it is NOT a clean green run.");
        output.WriteLine(rule);
    }

    /// <summary>
    /// Render the end-of-run mid-run-plan-edit advisory (issue #545 part 3, plan 31 §5.4) — the terminal
    /// third of the three surfaces the observation reaches (live/<c>--no-ui</c> and the durable
    /// <c>decisions[]</c> are the other two, both through the shipped <c>DecisionRecorded</c> event).
    ///
    /// <para><b>The advisory itself still halts nothing</b> — it is a rendering, and the workflow it exists to
    /// support (fixing a defective guardrail while the rest of the DAG runs) is untouched. What changed with
    /// issue #556 is what it may CLAIM: a task that settles after a real definition edit now diverges, so the
    /// run is halted and its delivery blocked by the settle-time gate. The shipped sentence <i>"Nothing was
    /// halted and nothing was re-run."</i> would sit beside <c>exit 2</c> and a blocked delivery, so the halt
    /// half is stated from <see cref="RunReport.HasExecutedDefinitionDivergence"/> and the re-run half — which
    /// is still true, in both branches — is kept.</para>
    ///
    /// <para><b>It must state all three §5.1 consequences and overstate none.</b> "Your edit was ignored" is
    /// FALSE: action prompts and guardrail scripts ARE re-read per attempt, and only <c>task.json</c> and the
    /// DAG were frozen at load. The third consequence INVERTED with issue #556: the settling task records its
    /// PRE-edit definition hash — the pin it actually ran and was verified against — so a later resume DOES
    /// flag this as drift. Saying so is the whole point; the old text disclosed a false green this plan has
    /// since closed.</para>
    ///
    /// <para>Each observation's <c>Detail</c> is already grouped BY FILE by
    /// <see cref="PlanEditDecisions.Observed"/>, so a single action script shared by N tasks prints once
    /// while still naming all N ids — §11 risk 7 accepts the N-task report as literally correct and refuses
    /// to de-duplicate away which tasks are affected.</para>
    ///
    /// <para>Pure (writes only to <paramref name="output"/>) and public, like its two sibling renderers —
    /// the Cli assembly ships no <c>InternalsVisibleTo</c>.</para>
    /// </summary>
    public static void RenderPlanEditWarning(RunReport report, TextWriter output)
    {
        if (report.Observations is not { Count: > 0 } observations)
        {
            return;
        }

        IReadOnlyList<DecisionEntry> planEdits = observations
            .Where(o => o.Boundary == PlanEditDecisions.Boundary)
            .ToList();
        if (planEdits.Count == 0)
        {
            return;
        }

        // Distinct across observations: a task edited at two separate boundaries is ONE task whose
        // definition changed, not two.
        int taskCount = planEdits
            .SelectMany(e => e.Subject.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .Count();
        string taskWord = taskCount == 1 ? "task definition" : "task definitions";

        output.WriteLine();
        output.WriteLine(
            $"PLAN FOLDER EDITED DURING THIS RUN (SSOT §7.2) - {taskCount} {taskWord} changed since the run started.");

        foreach (DecisionEntry entry in planEdits)
        {
            foreach (string line in (entry.Detail ?? "").Split('\n'))
            {
                if (line.Trim().Length > 0)
                {
                    output.WriteLine("  " + line.TrimEnd());
                }
            }
        }

        output.WriteLine(
            "  What your edit reaches: a task's action prompt and its guardrail scripts are re-read on every");
        output.WriteLine(
            "    attempt, so an edit to either applies from that task's next attempt onward.");
        output.WriteLine(
            "  What it does NOT reach: task.json (writeScope, dependsOn, retries, maxTurns) and the DAG were");
        output.WriteLine(
            "    loaded when this run started; edits to those apply only to a later run.");
        output.WriteLine(
            "  What gets recorded: each task above records its PRE-edit definition hash when it settles - the");
        output.WriteLine(
            "    bytes its guardrails actually verified, never the ones you just wrote. A later resume");
        output.WriteLine(
            "    compares current disk against that pinned hash and DOES report this as drift (issue #556).");

        // Issue #556 (plan 32 §15.1 rows 4-5). The shipped sentence here was "Nothing was halted and nothing
        // was re-run." Both halves inverted with this plan: the PRE-edit hash is recorded now (above), and a
        // task that settles after a real definition edit HALTS the run. Printing the old sentence beside
        // exit 2 and a blocked delivery would be a false claim on the exact surface this change exists to make
        // honest. The re-run half stays true and is still said, in whichever branch applies.
        if (report.HasExecutedDefinitionDivergence)
        {
            output.WriteLine(
                "  What was halted: task(s) that settled after your edit ran a definition that had already");
            output.WriteLine(
                "    moved, so this run is HALTED and its verified work is held back from delivery - see the");
            output.WriteLine(
                "    block above. Nothing was re-run and nothing was discarded.");
        }
        else
        {
            output.WriteLine(
                "  What happens next: no task settled against the edited definition in this run, so it was not");
            output.WriteLine(
                "    halted and nothing was re-run - but the next run's drift pre-pass compares current disk");
            output.WriteLine(
                "    against those pinned hashes and halts there instead (SSOT §7.2).");
        }
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
    /// verbatim so they see exactly why and can resolve it or disable the hook for the merge; for a
    /// moved checkout (#588) that same channel carries the two branch names.
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
                // Issue #448 part B: NAME the blocking paths. The gate only refuses on files this
                // merge would actually update, so the list is short and directly actionable — the
                // user no longer has to run `git status` and guess which change was in the way.
                if (report.MergeOnSuccessDetail is { Length: > 0 } dirtyPaths)
                {
                    output.WriteLine(
                        $"All tasks passed and are on branch `{planBranch}`. The final merge into your " +
                        "branch was refused because these tracked files have uncommitted changes that " +
                        "block it:");
                    foreach (string path in dirtyPaths.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        output.WriteLine($"  {path.Trim()}");
                    }
                    output.WriteLine(
                        "  Commit or stash them, then merge `" + planBranch + "` manually.");
                }
                else
                {
                    output.WriteLine(
                        $"All tasks passed and are on branch `{planBranch}`. The final merge into your " +
                        "branch was refused because your working tree has uncommitted changes. Commit or " +
                        "stash them, then merge `" + planBranch + "` manually.");
                }
                break;

            case MergeOnSuccessResult.BranchMoved:
                // Issue #588: the delivery target is pinned at run start, so a checkout that moved
                // mid-run would otherwise have merged somewhere the report never named. NAME BOTH
                // branches — the surprise is precisely that they differ — and say why the harness
                // declines rather than "fixing" it by checking a branch out.
                output.WriteLine(
                    report.MergeOnSuccessDetail is { Length: > 0 } move
                        ? $"All tasks passed and are on branch `{planBranch}`. The final merge into your " +
                          $"branch was NOT attempted because your checkout moved during the run — {move}."
                        : $"All tasks passed and are on branch `{planBranch}`. The final merge into your " +
                          "branch was NOT attempted because your checkout is no longer on the branch this " +
                          "run started on.");
                output.WriteLine(
                    "  Merging into a branch you did not start the run on would deliver the work somewhere " +
                    "you never asked for, and checking the original branch back out would stomp the one you " +
                    "switched to — so the harness does neither.");
                output.WriteLine(
                    "  Nothing was merged and your checkout is unchanged — merge `" + planBranch +
                    "` wherever you want it.");
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
            LogSiteRenderer.ExportSite(logsRoot, plan.Tasks, plan.Waves, document);
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
    /// The corpus-root override for RUN-END ingest: set to a non-blank value it is handed VERBATIM to
    /// <see cref="TelemetryCommand.ResolveCorpusRoot"/> in place of the real <c>~/.guardrails/telemetry/</c>;
    /// unset or blank falls through to that real default — the same env-override-wins-when-non-blank idiom
    /// <c>SchedulerFactory.WorktreeRootFor</c> uses for <c>GUARDRAILS_WORKTREE_ROOT</c>. It is an environment
    /// variable rather than a <c>run</c> flag on purpose: <c>--corpus-root</c> belongs to the <c>telemetry</c>
    /// verb, and what needs redirecting here is not an operator's choice but a test's (or a sandboxed bench's)
    /// need to keep a real run off the operator's own corpus.
    ///
    /// <para>This names WHERE the corpus lives, never WHETHER collection happens. The opt-out
    /// (<see cref="TelemetryCorpusStore.OptOutEnvVar"/><c>=off</c>) is read inside the store and nowhere
    /// else — least of all here, where a second copy of that rule could silently disagree with the verb.</para>
    /// </summary>
    private const string TelemetryCorpusRootEnvVar = "GUARDRAILS_TELEMETRY_CORPUS_ROOT";

    /// <summary>
    /// Run-end telemetry ingest (#535 charter §9): hand this run's own — now final — journal to the REAL
    /// <see cref="TelemetryIngest"/> so the local corpus fills itself, without anyone ever typing
    /// <c>guardrails telemetry ingest</c>. None of the ETL is re-implemented here; this is a call site.
    ///
    /// <para><b>Nothing may escape.</b> A full disk, a locked corpus file, a corpus root occupied by a file:
    /// none of them may change the run's exit code, throw out of <see cref="Finish"/>, or suppress the
    /// summary — a telemetry feature that can fail a delivered run is worse than no telemetry feature. So
    /// every fault is swallowed, exactly as <see cref="TrySettleFinalSitesAfterFault"/> swallows a render
    /// fault. Swallowed is NOT silent, though: a failure prints one line naming itself and the root it was
    /// writing to, because a telemetry mechanism failing in the direction that merely LOOKS fine — a machine
    /// that has quietly recorded nothing for months — is the exact defect this work exists to prevent.</para>
    ///
    /// <para><b>Both policy questions are settled elsewhere, by calling rather than re-deriving.</b> WHERE
    /// the corpus lives comes from <see cref="TelemetryCommand.ResolveCorpusRoot"/> — the very member the
    /// <c>telemetry</c> verb resolves through, so the verb and the run can never point at two different
    /// corpora — and WHETHER collection is on is honoured by going through
    /// <see cref="TelemetryCorpusStore.Append"/>, which checks the opt-out itself and writes nothing when it
    /// is set. Neither rule is restated in this file.</para>
    /// </summary>
    private static void IngestRunTelemetry(Core.Model.PlanDefinition plan, IConsoleIo io)
    {
        // Declared outside the try so the failure line can still name the root when the fault came from the
        // store rather than from resolution.
        string? corpusRoot = null;
        try
        {
            corpusRoot = TelemetryCommand.ResolveCorpusRoot(
                Environment.GetEnvironmentVariable(TelemetryCorpusRootEnvVar));

            TelemetryIngest.IngestPlanFolder(
                plan.PlanDirectory, new TelemetryCorpusStore(corpusRoot), TelemetryRepoDimension(plan));
        }
        catch (Exception ex)
        {
            // Deliberately catch-all, matching TrySettleFinalSitesAfterFault: the promise made above is that
            // NOTHING escapes, and a narrower filter would keep that promise only for the faults we happened
            // to think of. The run's verdict is already decided; this is the one place it must not be revisited.
            io.Out.WriteLine();
            io.Out.WriteLine(
                $"Telemetry ingest failed ({corpusRoot ?? "corpus root unresolved"}): {ex.Message}");
            io.Out.WriteLine(
                "  The run's outcome, exit code and logs below are unaffected — this run simply left no "
                + $"evidence in the local telemetry corpus. Set {TelemetryCorpusStore.OptOutEnvVar}=off to "
                + "stop collecting altogether.");
            io.Out.WriteLine();
        }
    }

    /// <summary>
    /// The <see cref="TelemetryRow.Repo"/> dimension every row of this run carries: the NAME of the run's
    /// workspace directory. That is the repository as the harness itself understands it — the git working
    /// tree the plan branch, the per-task worktrees and the end-of-run delivery all operate on
    /// (<c>WorktreeReclaim</c> and the Scheduler take the same value as the repo root) — so it is a fact
    /// already resolved for this run, not a path re-guessed at reporting time. Charter §9 records repo as a
    /// DIMENSION and never as a pooling key, so the bare directory name is the whole of what is wanted; the
    /// absolute path is not recorded.
    /// </summary>
    private static string TelemetryRepoDimension(Core.Model.PlanDefinition plan) =>
        new DirectoryInfo(plan.Workspace).Name;

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
    /// Compose the observer decorator chain both the live-UI and <c>--no-ui</c> branches run behind
    /// (issue #478): stack the diagram observer AROUND the log-site observer, which forwards every event
    /// down to <paramref name="inner"/> (the live table or the plain console) — so every event re-renders
    /// both <c>logs/&lt;runId&gt;/index.html</c> and <c>logs/&lt;runId&gt;/diagram.html</c> after each.
    /// Pure composition, no behaviour of its own; a test can call it directly. Public because
    /// <c>Guardrails.Cli</c> ships no <c>InternalsVisibleTo</c>, and the tests exercising this seam live in
    /// the <c>Guardrails.Integration.Tests</c> assembly.
    /// </summary>
    public static OnTheFlyDiagramObserver BuildObserverChain(
        IRunObserver inner,
        string logsRoot,
        string runId,
        Core.Model.PlanDefinition plan,
        Func<string, string?>? logUrlForTask,
        JournalDocument? diagramSeed)
    {
        var eventsProjection = new RunEventStream(inner, logsRoot, runId);
        var observerProjection = new ObserverProjection(eventsProjection, logsRoot);
        var siteObserver = new OnTheFlyLogSiteObserver(observerProjection, logsRoot, runId, plan.Tasks, logUrlForTask, plan.Waves);
        return new OnTheFlyDiagramObserver(siteObserver, logsRoot, plan, diagramSeed);
    }

    /// <summary>
    /// #585 layer 3 (design doc 36 §3.1) overload: adds <paramref name="onRow"/> and
    /// <paramref name="includeDetail"/> — task 08's stub; task 09 wires them for real. A NEW overload
    /// rather than two parameters added onto the six-argument member above: <c>RunCommandObserverWiringTests</c>,
    /// <c>RunFinishedExitPathTests</c> and <c>ObserverForwardingSweepTests</c> (plan 34, predating this
    /// plan) call the six-argument shape directly and sit outside this task's write scope, so widening
    /// that member in place would break their compilation for a change none of them asked for. <see cref="RunAsync"/>'s
    /// own two call sites use THIS overload instead, which — like the six-argument one — takes
    /// <paramref name="onRow"/>/<paramref name="includeDetail"/> with NO default value: a defaulted
    /// parameter would let a production call site silently deliver nothing (the plan-34 §3 swallow
    /// hazard), so the compiler forces both call sites to state their answer explicitly. The body below
    /// ignores both and delegates to the unchanged six-argument overload — byte-for-byte the same chain
    /// today's behaviour builds. Task 09 makes this constructor pass them into <see cref="RunEventStream"/>
    /// instead of discarding them.
    /// </summary>
    public static OnTheFlyDiagramObserver BuildObserverChain(
        IRunObserver inner,
        string logsRoot,
        string runId,
        Core.Model.PlanDefinition plan,
        Func<string, string?>? logUrlForTask,
        JournalDocument? diagramSeed,
        Action<EventDelivery>? onRow,
        bool includeDetail)
    {
        _ = onRow;
        _ = includeDetail;
        return BuildObserverChain(inner, logsRoot, runId, plan, logUrlForTask, diagramSeed);
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

    /// <summary>
    /// One run's result plus the <see cref="Scheduler"/> that produced it (issue #457). The Scheduler
    /// is threaded out because end-of-run delivery is now DEFERRED for any plan declaring a
    /// <c>&lt;plan&gt;/guardrails/</c> terminal gate: only this command knows the gate's verdict, and
    /// only the Scheduler holds the worktree provider + integration handle that perform the merge.
    /// </summary>
    private readonly record struct RunExecution(RunReport Report, Scheduler Scheduler);

    private static async Task<RunExecution> ExecuteAsync(
        Core.Model.PlanDefinition plan,
        IRunObserver observer,
        DriftAuthorization? driftAuthorization,
        IReadOnlySet<string>? waveDriftAuthorized,
        IReadOnlyDictionary<string, bool>? breakdownConfirmations,
        string? junctionRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            Scheduler scheduler = SchedulerFactory.Create(
                plan, new ProcessRunner(), new PathExecutableProbe(), observer, driftAuthorization, waveDriftAuthorized,
                breakdownConfirmations: breakdownConfirmations, junctionRoot: junctionRoot);
            RunReport report = await scheduler.RunAsync(plan, cancellationToken).ConfigureAwait(false);
            return new RunExecution(report, scheduler);
        }
        catch (Exception ex)
        {
            // #(event-vocabulary plan 35) — this is the largest fault surface in the process: a validated
            // plan can never make the Scheduler itself throw (every internal fault is converted to an
            // honest-halt Abort, issue #150), but this method is also reachable directly (embedded, or by
            // an unvalidated plan carrying e.g. a genuine dependency cycle — Scheduler.RunAsync's own cycle
            // guard exists exactly for that case). Record ONLY the exception's TYPE NAME on the caller's
            // observer — never ex.Message, which can carry an absolute path, a token, or a fragment of
            // source destined for an operator-supplied webhook URL — then rethrow bare so the exception
            // (and its original stack trace) propagates unchanged.
            observer.RunFinished(exitCode: null, faultKind: ex.GetType().Name);
            throw;
        }
    }

    /// <summary>
    /// The result of <see cref="PrepareWorktreeJunction"/> (issue #419): this run's real worktree root, the
    /// FRESH short junction link allocated for it (or null — non-Windows / a #407 C lazy-skip / a graceful
    /// fallback), and the process-scoped <see cref="WorktreeJunctionLifetime"/> that releases that link on
    /// every recoverable exit (null when no link was created).
    /// </summary>
    private readonly record struct WorktreeJunctionSetup(
        string RealRoot, string? JunctionRoot, WorktreeJunctionLifetime? Lifetime);

    /// <summary>
    /// Worktree-mode run-start setup (issues #383/#407/#419): run the startup GC (a crash BACKSTOP now),
    /// stamp the liveness lock, and — on Windows — allocate a FRESH short junction for this run. The junction
    /// is a process-scoped cwd alias, released on every recoverable exit by the returned
    /// <see cref="WorktreeJunctionLifetime"/> and NEVER journaled (the #419 decouple). Caller detects
    /// "a junction was created" by a non-null <see cref="WorktreeJunctionSetup.JunctionRoot"/>.
    /// </summary>
    private static WorktreeJunctionSetup PrepareWorktreeJunction(
        Core.Model.PlanDefinition plan, string runId, TextWriter log)
    {
        string realRoot = SchedulerFactory.WorktreeRootFor(plan);

        // #407 B — startup GC (crash backstop, #419): reclaim LEAKED junctions + roots from crashed/killed/
        // abandoned runs, never THIS run's own (excluded) tree. A fresh run holds no junction yet (it
        // allocates one below, AFTER the sweep), so the current-junction argument is null here.
        WorktreeReclaim.Reclaim(plan.Workspace, realRoot, currentJunctionRoot: null, log);

        // #407 F1 — stamp this run's liveness lock so a concurrent run's GC keeps this tree even while idle.
        WorktreeReclaim.WriteRunLock(realRoot);

        if (!OperatingSystem.IsWindows())
        {
            return new WorktreeJunctionSetup(realRoot, JunctionRoot: null, Lifetime: null);
        }

        // #383 + #419: allocate a FRESH short junction (<drive>:\.a..z → real root) unless the #407 C lazy
        // predicate says the real root already clears MAX_PATH for every task. git canonicalizes it away, so
        // a resume just re-allocates a free letter — no same-letter restore, no journal record.
        string effectiveRoot = WorktreeJunction.ResolveForRun(
            realRoot, Path.GetPathRoot(realRoot) ?? string.Empty, log,
            runId, plan.Tasks.Select(t => t.Id).ToList());

        if (string.Equals(effectiveRoot, realRoot, StringComparison.Ordinal))
        {
            return new WorktreeJunctionSetup(realRoot, JunctionRoot: null, Lifetime: null); // lazy-skip / fallback
        }

        return new WorktreeJunctionSetup(
            realRoot, effectiveRoot, new WorktreeJunctionLifetime(effectiveRoot, realRoot, log));
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

            // #485: the agent's own classification of the halt, immediately under the headline that carries
            // its question — the agent-asserted half of the section, above the harness's AI triage below.
            // Unclassified prints nothing, so a pre-#485 escalation renders byte-for-byte as before.
            if (NeedsHumanClaimLine(needsHuman.NeedsHumanKind) is { } claim)
            {
                output.WriteLine(claim);
            }

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
            output.WriteLine(NeedsHumanClosingLine(needsHuman.NeedsHumanKind));
        }
    }

    /// <summary>
    /// The agent's needs-human classification (issue #485) as one indented line in the existing
    /// <c>  Root cause [...]</c> idiom, or null when UNCLASSIFIED — in which case nothing is printed and the
    /// section is byte-identical to every pre-#485 run.
    /// <para>The wording names the harness's posture explicitly ("records this claim; it does not verify
    /// it") because the harness genuinely cannot adjudicate which kind a halt is: it can only report what
    /// the agent asserted, the same way #481's evidence requirement works.</para>
    /// </summary>
    private static string? NeedsHumanClaimLine(string? kind) => NeedsHumanKinds.Parse(kind) switch
    {
        NeedsHumanKinds.BlockedWork =>
            $"  Agent's claim [{NeedsHumanKinds.BlockedWork}] — look at the TASK. The harness records this claim; it does not verify it.",
        NeedsHumanKinds.DefectiveGuardrail =>
            $"  Agent's claim [{NeedsHumanKinds.DefectiveGuardrail}] — look at the CHECK, not the task. The harness records this claim; it does not verify it. Evidence: the latest attempt's action-out-fragment.json.",
        _ => null
    };

    /// <summary>
    /// The section's closing guidance line, which is REPLACED (never appended to) for a classified halt:
    /// "fix the action or guardrails" actively MISDIRECTS for both kinds — a <c>blocked-work</c> halt needs
    /// a decision or a re-scope rather than a fix, and a <c>defective-guardrail</c> halt is a claim that the
    /// work is already right and the CHECK is wrong. Unclassified keeps the shipped line verbatim.
    /// </summary>
    private static string NeedsHumanClosingLine(string? kind) => NeedsHumanKinds.Parse(kind) switch
    {
        NeedsHumanKinds.BlockedWork =>
            "  answer the question or re-scope the task (action, writeScope, dependencies), then re-run to resume.",
        NeedsHumanKinds.DefectiveGuardrail =>
            "  and if the claim holds fix the guardrail (/guardrails-review) — the work may already be complete.",
        _ => "  fix the action or guardrails, then re-run to resume."
    };

    /// <summary>
    /// Print the run-level cost line (SSOT §7 <c>costUsd</c>) from the freshly-persisted
    /// journal, plus the #230-lite PER-TIER split of that same spend (DoR §9.3) and the #349
    /// MODELS-USED summary of what served the run. Each line is omitted when there is nothing to
    /// report — no attempt recorded a cost, no attempt resolved through routing, and no attempt
    /// recorded a model — so deterministic-only plans stay noise-free and a tiering-inactive run
    /// prints EXACTLY the cost line it prints today.
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

        // The per-tier split is ADDITIVE to the total above, never a replacement — the two answer
        // different questions (the total includes overhead spend that resolved no rung). Suppression is
        // this PATTERN-MATCH on "there is nothing to report", deliberately not a string-emptiness test on
        // a rendered line: on a run where nothing resolved through routing there is no section, no
        // header, and no bucket for the attempts that routed nowhere (§9.3 Invariant 7) — and keying on
        // the null keeps that true the day some future edit gives the renderer a prefix.
        if (JournalTierSpend.Render(document) is { } perTier)
        {
            output.WriteLine($"Per-tier spend: {perTier}");
        }

        // What actually SERVED the run, which the per-tier line above cannot answer: one rung can be
        // served by several models over a run's lifetime, and a pinned or legacy-fallback attempt names a
        // model while resolving no rung at all. Same suppression pattern-match as its siblings — a run
        // where no attempt recorded a model prints no line, not a labelled empty one.
        if (JournalModelsUsed.Render(document) is { } models)
        {
            output.WriteLine($"Models used: {models}");
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
    /// <summary>
    /// Render an operator-facing path as the most openable form the terminal supports, in THREE states
    /// (issue #514): an OSC-8 hyperlink when the terminal advertises links, else a <c>file://</c> URI, and
    /// a bare path only when the value cannot be made into one.
    ///
    /// <para><b>The middle state is the fix.</b> This used to fall straight from OSC-8 to the raw path, so
    /// on a terminal without link support the wave-review halt printed
    /// <c>C:\…\wave-04-report-and-cleanup\diagram.html</c> while <c>guardrails graph</c> — which carried a
    /// URI fallback of its own — printed <c>file:///C:/…/diagram.html</c> in the same run. The bare form is
    /// the worse one even where nothing makes it clickable: it is not paste-able into a browser unmodified,
    /// and on Windows its backslashes make it awkward to copy out of a terminal. Every caller of this
    /// method is printing something the operator is expected to OPEN.</para>
    /// </summary>
    public static string Hyperlink(string absolutePath, bool enabled)
    {
        // A value we cannot turn into a URI (relative, malformed, empty) is returned untouched rather than
        // throwing: this is a convenience line in the middle of a report and must never be the thing that
        // fails a run.
        string? fileUri = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(absolutePath) && Path.IsPathFullyQualified(absolutePath))
            {
                fileUri = new Uri(absolutePath).AbsoluteUri;
            }
        }
        catch (UriFormatException)
        {
            fileUri = null;
        }

        if (fileUri is null)
        {
            return absolutePath;
        }

        if (!enabled)
        {
            return fileUri;
        }

        const string esc = "\u001b";
        return $"{esc}]8;;{fileUri}{esc}\\{absolutePath}{esc}]8;;{esc}\\";
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
