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
        var executor = new TaskExecutor(plan, processRunner, interpreterMap, stateManager, journal, observer, registry);

        return new Scheduler(plan, executor, journal, observer);
    }
}
