using Guardrails.Cli;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #452, ask 2 — the operator must SEE that the supervisor produced nothing.
///
/// <para>The whole defect was that a billed no-op looked identical, on every operator surface, to a
/// healthy quiet run. So the surface itself is asserted here, in the plain <c>--no-ui</c> writer (the mode
/// CI and most unattended runs use) and, just as importantly, through the on-the-fly DECORATORS: a
/// transparent decorator that forgets to forward a new observer method resolves to the interface's empty
/// default body and swallows the line silently — re-creating exactly the bug, in exactly the mode most
/// operators run.</para>
/// </summary>
public sealed class OverwatchNoVerdictRenderingTests
{
    private const string Reason =
        "aborted after 3 consecutive permission-denied tool calls — the prompt has no granted tool for what it was asked to do";

    [Fact]
    public void ConsoleRunObserver_PrintsANoVerdictLine_NamingTheTaskAndTheReason()
    {
        var writer = new StringWriter();
        var observer = new ConsoleRunObserver(writer);

        observer.OverwatchNoVerdict("02-implement-runner-kind-and-axes", Reason);

        string output = writer.ToString();
        Assert.Contains("overwatch", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no verdict", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("02-implement-runner-kind-and-axes", output);
        Assert.Contains(Reason, output);
    }

    [Fact]
    public void ConsoleRunObserver_PrintsNothing_WhenNoOverwatchFailureIsRaised()
    {
        // The counterweight: the line must be evidence of a real event, not decoration. A run whose
        // supervisor worked (or never ran) prints nothing here at all.
        var writer = new StringWriter();
        _ = new ConsoleRunObserver(writer);

        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void OnTheFlyDecorators_ForwardTheNoVerdict_InsteadOfSwallowingItAsADefaultNoOp()
    {
        // IRunObserver gives every new member an empty default body, so a decorator that does not forward
        // it compiles clean and silently drops the surface. That is the #452 failure mode reproduced one
        // layer up, which is why this is asserted rather than assumed.
        string logsRoot = Path.Combine(Path.GetTempPath(), "gr-nv-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logsRoot);
        try
        {
            var inner = new RecordingObserver();
            IRunObserver logSite = new OnTheFlyLogSiteObserver(
                inner, logsRoot, runId: "run-1", tasks: [], liveUrlForTask: null);

            logSite.OverwatchNoVerdict("01-task", Reason);

            (string taskId, string reason) = Assert.Single(inner.NoVerdicts);
            Assert.Equal("01-task", taskId);
            Assert.Equal(Reason, reason);
        }
        finally
        {
            try { Directory.Delete(logsRoot, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    private sealed class RecordingObserver : IRunObserver
    {
        public List<(string TaskId, string Reason)> NoVerdicts { get; } = [];

        public void OverwatchNoVerdict(string taskId, string reason) => NoVerdicts.Add((taskId, reason));

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
    }
}
