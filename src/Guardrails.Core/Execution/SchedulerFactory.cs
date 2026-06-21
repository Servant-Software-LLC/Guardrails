using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Core.Execution;

/// <summary>
/// Production wiring for a run: state manager initialized (seeding <c>state.json</c>),
/// journal loaded with resume rules applied (reporting a plan-hash mismatch to the
/// observer), interpreter map from config, and a <see cref="TaskExecutor"/> feeding a
/// <see cref="Scheduler"/>.
/// </summary>
/// <remarks>
/// Worktree mode (plan 08 §1) is CONDITIONAL on <c>maxParallelism &gt; 1</c> AND the workspace
/// being a real git repository. When both hold, the factory wires a <see cref="GitWorktreeProvider"/>
/// + <see cref="GuardrailReVerifier"/> into the <see cref="Scheduler"/> so tasks run in isolated
/// segment worktrees and integrate onto a <c>guardrails/&lt;plan&gt;</c> branch. Otherwise it wires
/// neither (serial shared-workspace, the pre-plan-08 model); the Scheduler's F7 guard additionally
/// clamps a parallel request with no provider down to serial so a non-git parallel run can never
/// race.
/// </remarks>
public static class SchedulerFactory
{
    /// <summary>Build a ready-to-run scheduler for <paramref name="plan"/>.</summary>
    public static Scheduler Create(
        PlanDefinition plan,
        ProcessRunner processRunner,
        IExecutableProbe probe,
        IRunObserver observer)
    {
        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        if (journal.PlanHashMismatch)
        {
            observer.PlanHashMismatch(journal.PreviousPlanHash ?? "(unknown)");
        }

        var interpreterMap = new InterpreterMap(probe, plan.Config.Interpreters);
        PromptRunnerRegistry registry = PromptRunnerRegistry.FromConfig(plan.Config, processRunner);

        // Plan 08 §9.2 / defect #120-class: the needs-human triage is the advisory step that fires
        // ONCE when a task exhausts its retry budget (in BOTH serial and worktree mode), writing the
        // task-level feedback.md with a tool-vs-local diagnosis. Without it constructed here the
        // TaskExecutor's `_triage is not null` guard short-circuits and the feature is dead from the
        // CLI. Build it over the triage-profile prompt runner resolved from the plan's `promptRunners`;
        // a script-only plan that declares NO prompt runner leaves it null — no advisory triage, never
        // a crash. It is NOT worktree-specific, so wire it unconditionally whenever a runner exists.
        NeedsHumanTriage? triage = null;
        IPromptRunner? triageRunner = ResolveTriageRunner(registry);
        if (triageRunner is not null)
        {
            triage = new NeedsHumanTriage(triageRunner, plan.Config.TriageAutoFile);
        }

        var executor = new TaskExecutor(plan, processRunner, interpreterMap, stateManager, journal, observer, registry, triage);

        // Plan 08 §1 wiring policy, by (parallelism, git):
        //   • parallel + git repo  → WORKTREE mode: real GitWorktreeProvider + GuardrailReVerifier
        //       (plan branch, segment worktrees, per-union re-verify §3/§4, terminal gate §3.3).
        //   • serial (==1)         → shared-workspace, NO provider (the pre-plan-08 path).
        //   • parallel + NON-git   → NO provider. Production blocks this at validation (GR2015 —
        //       "workspace must be a git top-level for maxParallelism>1"); a caller that bypassed
        //       validate hits the Scheduler's F7 clamp, which demotes a no-provider parallel request
        //       to serial (and tells the observer) rather than running shared-workspace-parallel —
        //       the exact unisolated race worktrees exist to prevent. There is NO silent
        //       shared-workspace-parallel fallback.
        IWorktreeProvider? worktreeProvider = null;
        IReVerifier? reVerifier = null;
        IAiMergeWorker? aiMergeWorker = null;
        if (plan.Config.MaxParallelism > 1 && IsGitRepository(plan.Workspace))
        {
            worktreeProvider = new GitWorktreeProvider(plan.Workspace, WorktreeRootFor(plan));
            reVerifier = new GuardrailReVerifier(processRunner, interpreterMap);

            // Plan 08 §9.1 / defect #120-followup: the AI-merge worker is the conflict-resolution
            // path for a non-FF union. Without it the Scheduler's `_aiMergeWorker != null && …`
            // short-circuits straight to needs-human on EVERY conflict (the worker is dead from the
            // CLI). Build it over the merge-profile prompt runner resolved from the plan's
            // `promptRunners`. A worktree-mode plan that declares NO prompt runner at all leaves the
            // worker null — the safe halt-to-needs-human behavior, never a silent vacuous pass.
            IPromptRunner? mergeRunner = ResolveMergeRunner(registry);
            if (mergeRunner is not null)
            {
                aiMergeWorker = new AiMergeWorker(mergeRunner);
            }
        }

        return new Scheduler(
            plan, executor, journal,
            worktreeProvider: worktreeProvider,
            observer: observer,
            reVerifier: reVerifier,
            aiMergeWorker: aiMergeWorker);
    }

    /// <summary>
    /// The reserved <c>promptRunners</c> profile name for the AI-merge worker (SSOT §9.1: "a reserved
    /// merge runner profile (e.g. <c>ai-merge</c>)"). When a plan declares it, the merge worker uses
    /// exactly that profile; otherwise it falls back to the default/sole prompt runner so a plan that
    /// only configured <c>claude</c> still gets AI-merge instead of an immediate needs-human halt.
    /// </summary>
    private const string MergeRunnerProfile = "ai-merge";

    /// <summary>
    /// The reserved <c>promptRunners</c> profile name for the needs-human triage step (SSOT §9.2: "a
    /// distinct <c>ai-triage</c> prompt profile in <c>promptRunners</c>"). When a plan declares it, the
    /// triage uses exactly that profile; otherwise it falls back to the default/sole prompt runner so a
    /// plan that only configured <c>claude</c> still gets advisory triage on exhaustion.
    /// </summary>
    private const string TriageRunnerProfile = "ai-triage";

    /// <summary>
    /// Resolve the <see cref="IPromptRunner"/> the AI-merge worker should drive: the reserved
    /// <c>ai-merge</c> profile when declared, else the registry's default (or sole) runner, else
    /// <c>null</c> when the plan declares no prompt runner at all (script-only plan run in parallel —
    /// no agent to call, so a conflict must halt to needs-human, never pass vacuously).
    /// </summary>
    private static IPromptRunner? ResolveMergeRunner(PromptRunnerRegistry registry) =>
        ResolveReservedRunner(registry, MergeRunnerProfile);

    /// <summary>
    /// Resolve the <see cref="IPromptRunner"/> the needs-human triage should drive: the reserved
    /// <c>ai-triage</c> profile when declared, else the registry's default (or sole) runner, else
    /// <c>null</c> when the plan declares no prompt runner at all (a script-only plan simply gets no
    /// advisory triage — never a crash, never a verdict change).
    /// </summary>
    private static IPromptRunner? ResolveTriageRunner(PromptRunnerRegistry registry) =>
        ResolveReservedRunner(registry, TriageRunnerProfile);

    /// <summary>
    /// Shared resolution for a reserved prompt-runner profile: the reserved <paramref name="profile"/>
    /// when declared, else the registry's default (or sole) runner, else <c>null</c> when the plan
    /// declares no prompt runner at all.
    /// </summary>
    private static IPromptRunner? ResolveReservedRunner(PromptRunnerRegistry registry, string profile)
    {
        if (registry.Contains(profile))
        {
            return registry.Resolve(profile);
        }

        return registry.DefaultRunnerName is { } name ? registry.Resolve(name) : null;
    }

    /// <summary>
    /// True when <paramref name="workspace"/> is inside a git working tree (worktree mode's hard
    /// dependency). Runs <c>git rev-parse --is-inside-work-tree</c>; any failure (git absent, not a
    /// repo) yields false so the run falls back to serial rather than throwing.
    /// </summary>
    private static bool IsGitRepository(string workspace)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("--is-inside-work-tree");
            using Process? proc = Process.Start(psi);
            if (proc is null) return false;
            string stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0 && stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A stable temp root for this plan's harness-owned worktrees (plan 08 §1: "all under one temp
    /// root"). Keyed by a hash of the plan directory so re-runs of the same plan reuse the root
    /// (prune/resume see prior worktrees) while distinct plans never collide. Public so
    /// <see cref="State.RunReset"/> can prune the same root on <c>--fresh</c> (F3).
    /// </summary>
    public static string WorktreeRootFor(PlanDefinition plan)
    {
        string planName = Path.GetFileName(plan.PlanDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(plan.PlanDirectory));
        string shortHash = Convert.ToHexString(hash)[..8].ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), "guardrails-worktrees", $"{planName}-{shortHash}");
    }
}
