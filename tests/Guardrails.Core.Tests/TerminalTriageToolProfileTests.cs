using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #452, applied to the overwatcher's OTHER branch. Per SSOT §9.2 the terminal-exhaustion triage is
/// the overwatcher's <c>TerminalExhaustion</c> trigger, and <see cref="NeedsHumanTriage"/> carried the
/// IDENTICAL one-line defect: it set only <c>MaxTurns</c> and inherited
/// <see cref="PromptRunnerSettings.AllowedTools"/>'s record default — an EMPTY list — so it was asked to
/// analyse a failure with permission to read nothing.
///
/// <para>Fixing only the diagnose would have left the same silent hole on the path that fires for EVERY
/// needs-human task, which is the path an operator actually reads. The turn cap is deliberately unchanged
/// at 10: the fix here is that the calls are granted rather than refused, not that the actor gets a bigger
/// budget.</para>
/// </summary>
public sealed class TerminalTriageToolProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gr-triage-prof-" + Guid.NewGuid().ToString("N"));
    private readonly string _planDir;
    private readonly string _taskLogDir;
    private readonly PlanDefinition _plan;
    private readonly RunJournal _journal;
    private readonly TaskNode _task;

    public TerminalTriageToolProfileTests()
    {
        _planDir = Path.Combine(_root, "plan");
        Directory.CreateDirectory(_planDir);
        File.WriteAllText(Path.Combine(_planDir, "guardrails.json"), """{ "version": 1 }""");

        string taskDir = Path.Combine(_planDir, "tasks", "01-impl");
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t", "dependsOn": [] }""");

        _task = new TaskNode
        {
            Id = "01-impl",
            Directory = taskDir,
            Description = "t",
            Action = new ActionDefinition { Path = Path.Combine(taskDir, "action.prompt.md"), Kind = ActionKind.Prompt },
            Guardrails = []
        };

        _plan = new PlanDefinition
        {
            PlanDirectory = _planDir,
            Workspace = _planDir,
            Config = new RunConfig { Version = 1 },
            Tasks = [_task]
        };

        _journal = RunJournal.LoadOrCreate(_plan);
        _taskLogDir = Path.Combine(_planDir, "logs", _journal.Document.RunId, _task.Id);
    }

    private sealed class CapturingRunner : IPromptRunner
    {
        public PromptInvocation? Seen { get; private set; }

        public string Name => "ai-triage";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Seen = invocation;
            return Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                ResultText = """{"diagnosis":"local-repo","analysis":"the test expectation is stale"}""",
                Summary = "triage complete"
            });
        }
    }

    [Fact]
    public async Task TriageInvocation_GrantsTheSameReadOnlyProfile_AndTheSameFailFastBound()
    {
        var runner = new CapturingRunner();
        var triage = new NeedsHumanTriage(runner);

        await triage.RunAsync(
            _task, _taskLogDir, _plan.PlanDirectory, _plan.Workspace, _journal,
            TestContext.Current.CancellationToken);

        Assert.NotNull(runner.Seen);
        PromptInvocation seen = runner.Seen;

        Assert.NotEmpty(seen.Settings.AllowedTools);                     // the #452 root cause: it was empty
        Assert.Equal(["Read", "Glob", "Grep"], seen.Settings.AllowedTools);
        Assert.Equal(3, seen.AbortAfterConsecutiveToolDenials);
        Assert.Equal(10, seen.Settings.MaxTurns);                        // unchanged on purpose
    }

    [Fact]
    public async Task TriagePrompt_NamesTheEvidenceDirectory()
    {
        // Granting read tools without saying WHERE to point them just moves the flailing from refused
        // calls to guessed paths.
        var runner = new CapturingRunner();
        var triage = new NeedsHumanTriage(runner);

        await triage.RunAsync(
            _task, _taskLogDir, _plan.PlanDirectory, _plan.Workspace, _journal,
            TestContext.Current.CancellationToken);

        Assert.NotNull(runner.Seen);
        Assert.Contains(_taskLogDir, runner.Seen.ComposedPrompt);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { }
    }
}
