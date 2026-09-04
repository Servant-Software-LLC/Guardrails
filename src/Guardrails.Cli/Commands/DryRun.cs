using Guardrails.Core.Execution;
using Guardrails.Core.Graph;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails run &lt;folder&gt; --dry-run</c> — validate the plan, print the execution
/// tiers (identical to <c>plan</c>), the per-task action resolution (kind, runner,
/// retry budget), and which tasks a resume would SKIP (read from the journal
/// without normalizing or persisting it). Exits 0 having run nothing and touched no state.
/// </summary>
public static class DryRun
{
    public static int Execute(string folder, IConsoleIo io, bool skipReviewCheck = false)
    {
        TextWriter output = io.Out;

        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, output);
            output.WriteLine("\nValidation failed; nothing would be run.");
            return ExitCodes.HarnessError;
        }

        // Surface warnings (e.g. GR2009 prompt-runner-not-on-PATH) even on a clean dry run.
        PlanProbe.PrintDiagnostics(probe.Diagnostics, output);

        PlanDefinition plan = probe.Plan;

        // Review-marker nudge (warn, never block — SSOT §13, issue #79), same as a real run.
        RunCommand.WarnIfUnreviewed(plan, skipReviewCheck, io);

        // Resume awareness: read the journal read-only (NO LoadOrCreate — a dry run must not
        // normalize statuses or persist anything). Only journaled 'succeeded' tasks would be
        // skipped by a real run; everything else would run with a fresh budget.
        JournalDocument? journal = ReadJournalDocument(plan.PlanDirectory);
        IReadOnlyDictionary<string, JournalTaskStatus> statuses = journal is null
            ? new Dictionary<string, JournalTaskStatus>(StringComparer.Ordinal)
            : journal.Tasks.ToDictionary(p => p.Key, p => p.Value.Status, StringComparer.Ordinal);

        // §7.2 drift-preview parity: a real resume compares the CURRENT definition against the hash
        // recorded on the journal OR the plan-branch trailer (a journal-reset resume survives only via the
        // trailer). Consult the trailer too via a READ-ONLY git query — no integration worktree, touches
        // nothing — so the preview does not under-predict a drift halt. Empty for a non-git plan folder /
        // absent plan branch (then the preview is journal-only, exactly as before).
        IReadOnlyDictionary<string, PlanBranchTaskRecord> trailerHashes =
            GitWorktreeProvider.ReadPlanBranchTaskHashes(plan.Workspace, Path.GetFileName(plan.PlanDirectory));

        output.WriteLine($"Dry run — {plan.Tasks.Count} task(s); validation passed. Nothing was executed; no state was touched.");
        output.WriteLine();

        PrintTiers(plan, output);
        PrintResolution(plan, statuses, journal, trailerHashes, output);
        PrintResumeSkips(plan, statuses, journal, trailerHashes, output);

        return ExitCodes.Success;
    }

    private static void PrintTiers(PlanDefinition plan, TextWriter output)
    {
        var graph = new DependencyGraph(plan.Tasks);
        IReadOnlyList<IReadOnlyList<TaskNode>> tiers = graph.Tiers();

        output.WriteLine($"Execution plan — {plan.Tasks.Count} task(s), {tiers.Count} tier(s), maxParallelism {plan.Config.MaxParallelism}");
        output.WriteLine();

        for (int i = 0; i < tiers.Count; i++)
        {
            output.WriteLine($"Tier {i}:");
            foreach (TaskNode task in tiers[i])
            {
                string kind = task.Action.Kind == ActionKind.Prompt ? "prompt" : "script";
                string deps = task.DependsOn.Count == 0 ? "" : $"  (after: {string.Join(", ", task.DependsOn)})";
                output.WriteLine($"  {task.Id,-36} {kind,-7}{deps}");
            }

            output.WriteLine();
        }
    }

    private static void PrintResolution(
        PlanDefinition plan, IReadOnlyDictionary<string, JournalTaskStatus> statuses,
        JournalDocument? journal, IReadOnlyDictionary<string, PlanBranchTaskRecord> trailerHashes, TextWriter output)
    {
        output.WriteLine("Per-task resolution:");
        output.WriteLine($"  {"TASK",-36} {"KIND",-7} {"RUNNER",-10} {"TIER",-26} {"RETRY BUDGET",-13} RESUME");
        output.WriteLine(new string('-', 105));

        foreach (TaskNode task in plan.Tasks)
        {
            string kind = task.Action.Kind == ActionKind.Prompt ? "prompt" : "script";
            TierResolution? route = PreviewRoute(plan, task);
            int retries = task.Retries ?? plan.Config.DefaultRetries;
            int budget = 1 + retries; // SSOT §2: defaultRetries are AFTER the first attempt.
            // §7.2 (#274 Part A): an already-succeeded task whose definition changed since it settled would
            // HALT a real resume (a definition-drift halt), not skip — preview that honestly instead of a
            // stale "SKIP (succeeded)".
            string resume = IsDrifted(task, journal, trailerHashes)
                ? "HALT (definition drift)"
                : WouldSkip(task, statuses) ? "SKIP (succeeded)" : "run";

            output.WriteLine(
                $"  {task.Id,-36} {kind,-7} {RunnerCell(plan, task, route),-10} " +
                $"{TierCell(task, route),-26} {budget,-13} {resume}");
        }

        output.WriteLine();
    }

    private static void PrintResumeSkips(
        PlanDefinition plan, IReadOnlyDictionary<string, JournalTaskStatus> statuses,
        JournalDocument? journal, IReadOnlyDictionary<string, PlanBranchTaskRecord> trailerHashes, TextWriter output)
    {
        IReadOnlyList<string> drifted = plan.Tasks
            .Where(t => IsDrifted(t, journal, trailerHashes))
            .Select(t => t.Id)
            .ToList();

        // A drifted succeeded task would halt a real run, so it is NOT a skip — exclude it from the skip
        // list and call it out separately with the remediation the halt itself prints.
        IReadOnlyList<string> skips = plan.Tasks
            .Where(t => WouldSkip(t, statuses) && !IsDrifted(t, journal, trailerHashes))
            .Select(t => t.Id)
            .ToList();

        output.WriteLine(skips.Count == 0
            ? "Resume: no tasks would be skipped (no journaled successes; a real run would execute every task)."
            : $"Resume: {skips.Count} task(s) would be SKIPPED (already succeeded): {string.Join(", ", skips)}.");

        if (drifted.Count > 0)
        {
            output.WriteLine(
                $"Resume: {drifted.Count} already-succeeded task(s) have a CHANGED definition — a real run would " +
                $"HALT on definition drift (SSOT §7.2): {string.Join(", ", drifted)}.");
            output.WriteLine(
                "  Fix: `guardrails reset <folder> -y` (full rebuild), then re-run.");
        }
    }

    /// <summary>
    /// True when <paramref name="task"/> has a recorded <c>TaskDefinitionHash</c> — on the journal
    /// (status <c>succeeded</c>) OR the plan-branch trailer (a journal-reset resume survives only via the
    /// trailer, mirroring the real pre-pass, §7.2) — that no longer matches its current on-disk
    /// definition, i.e. a real resume would HALT on definition drift rather than skip. The journal hash is
    /// preferred; an absent recorded hash (a pre-upgrade journal/trailer) is treated as "unknown, assume
    /// unchanged". Journal-only when the trailer query returned empty (non-git plan folder).
    /// </summary>
    private static bool IsDrifted(
        TaskNode task, JournalDocument? journal, IReadOnlyDictionary<string, PlanBranchTaskRecord> trailerHashes)
    {
        string? recorded = null;
        if (journal is not null
            && journal.Tasks.TryGetValue(task.Id, out TaskJournalEntry? entry)
            && entry.Status == JournalTaskStatus.Succeeded)
        {
            recorded = entry.DefinitionHash;
        }

        recorded ??= trailerHashes.TryGetValue(task.Id, out PlanBranchTaskRecord? trailer) ? trailer.DefinitionHash : null;

        if (recorded is null)
        {
            return false;
        }

        // A dry run is advisory and must never crash or touch state: if a definition file can't be read
        // (e.g. a transient lock), omit this task from the preview rather than throwing. A real run would
        // honestly abort (§7.2) — the preview is not the gate.
        try
        {
            return !string.Equals(recorded, TaskDefinitionHash.Compute(task), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The route a prompt task's first attempt would launch on — <b>resolved by CALLING the resolver the
    /// run itself calls</b> (<see cref="TierResolver.Resolve"/>), with the same arguments
    /// <c>TaskExecutor.ResolveRoute</c> passes, so the preview cannot answer §6.1 differently from the
    /// attempt it is previewing (issue #549).
    ///
    /// <para><b>This method deliberately decides nothing.</b> The shipped preview re-implemented the
    /// PRE-TIERING precedence here — <c>action.runner</c> else <c>promptRunners.default</c> — which on a
    /// well-formed tiered config named exactly the block the operator did NOT tag the task for, and said
    /// so with no hint it was guessing. A second copy of §6.1 is how the two drifted apart; there is now
    /// one, and this is not it.</para>
    ///
    /// <para>Null for a SCRIPT action: no model, no route, nothing to resolve — the same null
    /// <c>TaskExecutor.ResolveRoute</c> returns there. The CLI-level default model is null because the
    /// harness exposes no such setting, exactly as both run-time call sites pass it.</para>
    /// </summary>
    private static TierResolution? PreviewRoute(PlanDefinition plan, TaskNode task) =>
        task.Action.Kind == ActionKind.Prompt
            ? TierResolver.Resolve(task.Action, plan.Config, cliDefaultModel: null)
            : null;

    /// <summary>
    /// The RUNNER cell: the <c>promptRunners</c> block this task's first attempt would dispatch to,
    /// asked of <see cref="PromptRunnerRegistry.DispatchNameFor"/> — the one expression
    /// <c>ActionRunner</c> hands the registry — so the preview names the block the run would actually
    /// invoke rather than one derived alongside it.
    ///
    /// <para>Two honest non-answers, both distinguishable from a block name: <c>(no route)</c> is §6.2's
    /// <see cref="TierResolution.NoRoute"/> — nothing serves the rung asked for or any stronger one — and
    /// <c>(unresolved)</c> is a plan that names no runner and has no default to fall back on. Neither is
    /// ever silently replaced by a plausible block name: reporting <c>sonnet</c> for work that will not
    /// run is the failure shape this whole fix is about.</para>
    ///
    /// <para><b><c>(no route)</c> is a defensive residual, and deliberately so.</b> A dry run VALIDATES
    /// before it prints, and GR2048 is that same condition asked at validate time — so an unservable rung
    /// exits before any row is rendered (pinned by
    /// <c>DryRunRoutePreviewTests.AnUnservableRungIsRefusedBeforeAnyRowIsPrinted</c>). It is spelled
    /// anyway because the alternative when the two gates ever disagree is printing the default block,
    /// which is #549 again.</para>
    /// </summary>
    private static string RunnerCell(PlanDefinition plan, TaskNode task, TierResolution? route)
    {
        if (route is null)
        {
            return "-";   // a script action dispatches no runner at all
        }

        if (route.NoRoute)
        {
            return "(no route)";
        }

        return PromptRunnerRegistry.DispatchNameFor(
            plan.Config, route, task.Action.Runner, FrontmatterRunner(task)) ?? "(unresolved)";
    }

    /// <summary>
    /// The TIER cell — the rung this attempt resolved at and WHICH SITE supplied it, in the same
    /// vocabulary the attempt's own <c>attempt-route.log</c> and <c>run.json</c> provenance use
    /// (<c>task</c> / <c>plan-default</c> / <c>override</c>, via
    /// <see cref="TierProvenance.SourceFor"/> and <see cref="JournalJson.TierSourceToken"/>). Without it
    /// the RUNNER column is an answer with no working shown: <c>opus</c> beside <c>hard (task)</c> says
    /// the task asked for it, while <c>opus</c> beside <c>hard (plan-default)</c> says the plan did — and
    /// those have different fixes.
    ///
    /// <para>A §6.2 CLIMB prints BOTH rungs (<c>easy -&gt; medium (task)</c>): "served at medium" alone
    /// reads as an ordinary medium task unless the rung it replaced is sitting beside it — the same
    /// reason the route log carries them as a pair.</para>
    ///
    /// <para><c>-</c> means no rung was resolved and none was asked for: a script action, or the LEGACY
    /// path (Invariant 7 — an untagged task in a plan with no <c>tiering.defaultTier</c>, which runs
    /// exactly as it did before tiering existed). A PIN prints <c>(override)</c> rather than a dash,
    /// because "no rung" and "a human named the block outright" are different facts.</para>
    /// </summary>
    private static string TierCell(TaskNode task, TierResolution? route)
    {
        if (route is null)
        {
            return "-";
        }

        string? source = TierProvenance.SourceFor(task.Action, route) is { } tierSource
            ? JournalJson.TierSourceToken(tierSource)
            : null;

        // A pin resolves no rung at all (§6.1 item 1 bypasses resolution), so there is nothing to print
        // but the provenance — which is exactly the fact that distinguishes it from the legacy path.
        if (route.Pinned)
        {
            return source is null ? "-" : $"({source})";
        }

        // The rung ASKED for, which survives even a no-route settle; the served rung is null there.
        string? requested = route.RequestedTier;
        if (requested is null)
        {
            return "-";   // the legacy path: no action.tier, no tiering.defaultTier, nothing resolved
        }

        string rungs = route.Climbed && route.Tier is { } served ? $"{requested} -> {served}" : requested;
        return source is null ? rungs : $"{rungs} ({source})";
    }

    /// <summary>
    /// The prompt file's frontmatter <c>runner</c>, the LAST name in the dispatch order — read here
    /// because the preview must reproduce the whole expression, not the part that is cheap to reach.
    ///
    /// <para>Best-effort by design, like <see cref="IsDrifted"/> above: a dry run is advisory and must
    /// never crash or touch state, so an unreadable or malformed <c>action.prompt.md</c> yields null (the
    /// registry default is then previewed) rather than an exception. The real run would fail honestly on
    /// the same file; the preview is not the gate.</para>
    /// </summary>
    private static string? FrontmatterRunner(TaskNode task)
    {
        try
        {
            return PromptFileParser.Parse(File.ReadAllText(task.Action.Path)).File?.Frontmatter.Runner;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool WouldSkip(TaskNode task, IReadOnlyDictionary<string, JournalTaskStatus> statuses) =>
        statuses.TryGetValue(task.Id, out JournalTaskStatus status) && status == JournalTaskStatus.Succeeded;

    /// <summary>
    /// Read <c>run.json</c> straight off disk without the resume normalization or persistence that
    /// <see cref="RunJournal.LoadOrCreate"/> performs — a dry run must leave state byte-for-byte
    /// untouched. Returns null when the journal is absent (a first run). Carries the full document so the
    /// caller can read both per-task status AND the recorded <c>definitionHash</c> (§7.2 drift preview).
    /// </summary>
    private static JournalDocument? ReadJournalDocument(string planDirectory)
    {
        string journalPath = RunJournal.PathFor(planDirectory);
        return File.Exists(journalPath) ? JournalReader.Read(journalPath) : null;
    }
}
