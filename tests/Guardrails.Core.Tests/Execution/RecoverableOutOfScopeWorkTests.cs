using Guardrails.Core.Execution;
using Guardrails.Core.Graph;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.Execution;

/// <summary>
/// #705's SECOND audience (#707 review W4): the attempt that runs after a human widens the scope. #705 keeps a
/// write-scope violation's out-of-scope bytes as <c>out-of-scope.patch</c>, and until now told only the human about
/// it — the retry's own feedback calls it "not for you", which is true while the scope still excludes those paths.
/// Once a widened scope covers them, the next attempt must be pointed at the kept work as something to RECOVER, and
/// that pointer must say it supersedes the earlier "not for you". While the scope still excludes them, the copy must
/// stay out of the agent's instructions entirely.
///
/// <para>Driven through the two production seams, exactly as <see cref="EscalationSalvageTests"/> is: the log dir is
/// laid down by hand, <see cref="DependencyContextBuilder.BuildPriorAttempts"/> walks it, and
/// <see cref="PromptComposer.ComposeAction"/> renders the prompt with the ENFORCED scope the executor would pass.</para>
/// </summary>
public sealed class RecoverableOutOfScopeWorkTests : IDisposable
{
    private const string TaskId = "02-implement";
    private const string Heading = "## Out-of-scope work an earlier attempt left is now in scope";

    /// <summary>A real two-path patch: an upstream file the attempt implemented, and a stray new file.</summary>
    private const string KeptCopy =
        "diff --git a/src/Stub.cs b/src/Stub.cs\n" +
        "index e69de29..d95f3ad 100644\n" +
        "--- a/src/Stub.cs\n" +
        "+++ b/src/Stub.cs\n" +
        "@@ -1 +1 @@\n" +
        "-stub\n" +
        "+RESOLVE-BODY\n" +
        "diff --git a/docs/notes.md b/docs/notes.md\n" +
        "new file mode 100644\n" +
        "index 0000000..1111111\n" +
        "--- /dev/null\n" +
        "+++ b/docs/notes.md\n" +
        "@@ -0,0 +1 @@\n" +
        "+a stray note\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gr-oos-recover-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException) { /* best-effort teardown */ }
        catch (UnauthorizedAccessException) { /* best-effort teardown */ }
    }

    [Fact]
    public void AWidenedScopeCoversAKeptPath_ThePromptOffersItToRecover_AndSupersedesNotForYou()
    {
        string composed = ComposeNextAttemptPrompt(writeScope: ["src/Impl.cs", "src/Stub.cs"]);

        string section = SectionBody(composed, Heading);
        Assert.Contains("out-of-scope.patch", section);
        // Only the path the scope now covers is offered — never the stray one the scope still excludes.
        Assert.Equal(["src/Stub.cs"], BulletPaths(section));
        Assert.Contains("supersedes", section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("still outside", section, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheScopeStillExcludesEveryKeptPath_ThePromptNeverMentionsTheCopy()
    {
        // DECLARED CONTROL — green before the feature and after. Offering work the scope still forbids would
        // invite the exact write the check rejects.
        string composed = ComposeNextAttemptPrompt(writeScope: ["src/Impl.cs"]);

        Assert.Contains("Attempt 1 (", composed); // not vacuous: the prior attempt IS rendered
        Assert.DoesNotContain("out-of-scope.patch", composed);
        Assert.DoesNotContain("## Out-of-scope work", composed);
    }

    [Fact]
    public void NoEnforcedScope_NoRecoveryBlock()
    {
        // DECLARED CONTROL: serial mode passes no enforced scope, so nothing can be "now in scope".
        string composed = ComposeNextAttemptPrompt(writeScope: null);

        Assert.DoesNotContain("## Out-of-scope work", composed);
    }

    // ── fixture ─────────────────────────────────────────────────────────────────────────────────

    private string ComposeNextAttemptPrompt(IReadOnlyList<string>? writeScope)
    {
        string planDir = Path.Combine(_root, "plan");
        string taskDir = Path.Combine(planDir, "tasks", TaskId);
        Directory.CreateDirectory(taskDir);

        var task = new TaskNode
        {
            Id = TaskId,
            Directory = taskDir,
            Description = "implement against a stub an upstream task authored",
            Action = new ActionDefinition { Path = Path.Combine(taskDir, "action.prompt.md"), Kind = ActionKind.Prompt },
            Guardrails =
            [
                new GuardrailDefinition { Name = "01-ok", Path = Path.Combine(taskDir, "guardrails", "01-ok.sh"), Kind = ActionKind.Script }
            ],
            WriteScope = writeScope ?? ["src/Impl.cs"]
        };
        var plan = new PlanDefinition
        {
            PlanDirectory = planDir,
            Workspace = _root,
            Config = new RunConfig { Version = 1 },
            Tasks = [task]
        };

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        string relativeLogDir = $"logs/{journal.Document.RunId}/{TaskId}/attempt-1";
        string logDir = Path.Combine(planDir, "logs", journal.Document.RunId, TaskId, "attempt-1");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(Path.Combine(logDir, "out-of-scope.patch"), KeptCopy);
        File.WriteAllText(Path.Combine(logDir, "feedback.md"),
            "Before reverting, the harness kept a copy of the out-of-scope change(s). That copy is for a human — not for you.\n");

        DateTimeOffset at = DateTimeOffset.UtcNow;
        journal.RecordAttempt(
            TaskId,
            new AttemptRecord
            {
                Attempt = 1,
                StartedAt = at,
                EndedAt = at,
                ActionExitCode = 0,
                Outcome = AttemptOutcome.WriteScopeViolation,
                LogDir = relativeLogDir
            },
            Guardrails.Core.Journal.TaskStatus.NeedsHuman);

        var builder = new DependencyContextBuilder(
            plan, journal, new DependencyGraph(plan.Tasks),
            new Dictionary<string, TaskNode>(StringComparer.Ordinal) { [TaskId] = task });
        IReadOnlyList<PriorAttemptRef> priorAttempts = builder.BuildPriorAttempts(TaskId, currentAttemptNumber: 2);
        Assert.Single(priorAttempts); // sanity: the production walker saw the attempt this fixture wrote

        string stateIn = Path.Combine(_root, "state.json");
        File.WriteAllText(stateIn, "{}");
        return PromptComposer.ComposeAction(
            "Implement it.", stateIn, Path.Combine(_root, "fragment.json"), feedbackPath: null,
            priorAttempts: priorAttempts, isWorktreeMode: writeScope is not null, writeScope: writeScope);
    }

    private static string SectionBody(string composed, string heading)
    {
        int start = composed.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"expected a '{heading}' section in the composed prompt:\n{composed}");
        int bodyStart = start + heading.Length;
        int next = composed.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
        return next < 0 ? composed[bodyStart..] : composed[bodyStart..next];
    }

    private static List<string> BulletPaths(string section) =>
        section.Replace("\r\n", "\n").Split('\n')
            .Where(line => line.StartsWith("- `", StringComparison.Ordinal))
            .Select(line => line[3..line.IndexOf('`', 3)])
            .ToList();
}
