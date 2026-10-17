using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests;

/// <summary>
/// #810 at the CLI: the macOS idle-sleep assertion and the <c>--no-ui</c> line. The platform and the process start are
/// injected, so the decision is asserted on every OS and nothing is launched.
/// </summary>
public sealed class HostSleepCliTests
{
    [Fact]
    public void OnAMac_TheRunStartsCaffeinateTiedToItsOwnProcess_AndSaysSo()
    {
        var output = new StringWriter();
        ProcessStartInfo? started = null;

        using IDisposable? handle = SleepInhibitor.Acquire(
            allowSleep: false, output, isMacOS: true, start: info => { started = info; return ExitedProcess(); });

        Assert.NotNull(handle);
        ProcessStartInfo info = Assert.IsType<ProcessStartInfo>(started);
        Assert.EndsWith("caffeinate", info.FileName, StringComparison.Ordinal);   // /usr/bin/caffeinate when it exists
        Assert.Equal(["-i", "-w", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)], info.ArgumentList);
        Assert.Contains(SleepInhibitor.HeldNotice, output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--allow-sleep", SleepInhibitor.HeldNotice, StringComparison.Ordinal);
        Assert.Contains("lid", SleepInhibitor.HeldNotice, StringComparison.Ordinal);
    }

    /// <summary>A real process that has already exited, so disposing the handle has nothing to stop.</summary>
    private static Process ExitedProcess()
    {
        var process = Process.Start(new ProcessStartInfo("dotnet", "--version")
        {
            UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
        })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process;
    }

    [Theory]
    [InlineData(true, true)]    // --allow-sleep on a Mac
    [InlineData(false, false)]  // not a Mac
    public void NothingIsStartedWhenSleepIsAllowedOrOffAMac(bool allowSleep, bool isMacOS)
    {
        var output = new StringWriter();
        bool started = false;

        IDisposable? handle = SleepInhibitor.Acquire(allowSleep, output, isMacOS, _ => { started = true; return null; });

        Assert.Null(handle);
        Assert.False(started);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void AMissingCaffeinateIsANotice_NotAFailure()
    {
        var output = new StringWriter();

        IDisposable? handle = SleepInhibitor.Acquire(
            allowSleep: false, output, isMacOS: true,
            start: _ => throw new System.ComponentModel.Win32Exception("No such file or directory"));

        Assert.Null(handle);
        Assert.Contains("Could not start caffeinate", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NoUi_PrintsTheHostSleptLine()
    {
        var output = new StringWriter();
        var observer = new ConsoleRunObserver(output);
        var from = new DateTimeOffset(2026, 9, 26, 15, 3, 52, TimeSpan.Zero);

        observer.HostSlept(from, from.AddHours(6), TimeSpan.FromHours(6), ["02-x/attempt-1"]);

        Assert.Contains(
            "[host-slept] the host was asleep for 6h00m (between 15:03:52 and 21:03:52 UTC); in flight: 02-x/attempt-1",
            output.ToString(), StringComparison.Ordinal);
    }

    // ─── attach replays the sleep (#810 review W1, the #722 precedent) ─────────────────

    [Fact]
    public void AttachReplaysHostSlept()
    {
        var recorder = new SleepRecorder();
        JsonNode node = JsonNode.Parse(
            """{"member":"HostSlept","from":"2026-09-26T15:03:52+00:00","to":"2026-09-26T21:03:52+00:00","sleptForSeconds":21600,"inFlight":["02-x/attempt-1"]}""")!;

        AttachCommand.Dispatch(node, recorder, new Dictionary<string, TaskNode>());

        (DateTimeOffset from, DateTimeOffset to, TimeSpan slept, IReadOnlyList<string> inFlight) = Assert.Single(recorder.Sleeps);
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 15, 3, 52, TimeSpan.Zero), from);
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 21, 3, 52, TimeSpan.Zero), to);
        Assert.Equal(TimeSpan.FromHours(6), slept);
        Assert.Equal(["02-x/attempt-1"], inFlight);
    }

    [Fact]
    public void TheProjectionAndTheReplayAgreeOnTheShape()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gr-host-slept-projection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var plan = new PlanDefinition
            {
                PlanDirectory = dir, Workspace = dir, Config = new RunConfig { Version = 1 }, Tasks = []
            };
            var inner = new SleepRecorder();
            IRunObserver chain = RunCommand.BuildObserverChain(inner, dir, "run-810", plan, logUrlForTask: null, diagramSeed: null);
            var from = new DateTimeOffset(2026, 9, 26, 15, 3, 52, TimeSpan.Zero);

            chain.HostSlept(from, from.AddHours(6), TimeSpan.FromHours(6), ["02-x/attempt-1"]);

            string line = Assert.Single(File.ReadAllLines(Path.Combine(dir, "observer.jsonl")), l => l.Contains("HostSlept", StringComparison.Ordinal));
            var replayed = new SleepRecorder();
            AttachCommand.Dispatch(JsonNode.Parse(line)!, replayed, new Dictionary<string, TaskNode>());
            var sent = Assert.Single(inner.Sleeps);
            var got = Assert.Single(replayed.Sleeps);
            Assert.Equal((sent.From, sent.To, sent.Slept), (got.From, got.To, got.Slept));
            Assert.Equal(sent.InFlight, got.InFlight);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ─── the composition root (#810 review W2) ─────────────────────────────────────────

    /// <summary>
    /// `guardrails run` on a real script plan with injected host services. Fails if RunCommand stops taking the keep-awake
    /// assertion, stops starting the heartbeat, or stops passing the monitor into SchedulerFactory.Create (then no
    /// executor hears the sleep and events.jsonl has no host-slept row).
    /// </summary>
    [Fact]
    public async Task TheRunTakesTheAssertion_StartsTheHeartbeat_AndWiresTheMonitorIntoTheExecutor()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        var keepAwake = new List<bool>();
        int heartbeats = 0;
        var started = Stopwatch.StartNew();
        int wallReads = 0;
        var services = new RunHostServices
        {
            KeepAwake = (allowSleep, _) => { keepAwake.Add(allowSleep); return null; },

            // The wall clock runs two hours ahead from its second reading on: the first check anywhere sees a sleep.
            HostSleep = () => new HostSleepMonitor(
                () => DateTimeOffset.UtcNow + (Interlocked.Increment(ref wallReads) > 1 ? TimeSpan.FromHours(2) : TimeSpan.Zero),
                () => started.Elapsed),
            HeartbeatInterval = TimeSpan.FromMilliseconds(20),
            HeartbeatDelay = (interval, ct) => { Interlocked.Increment(ref heartbeats); return Task.Delay(interval, ct); }
        };
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(RunCommand.Create(io, TelemetryOverrides.None, services));

        int exit = await root.Parse(["run", plan.PlanDir, "--no-ui", "--no-log-server"]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(exit == 0, io.OutText);
        Assert.Equal([false], keepAwake);
        Assert.True(heartbeats > 0, "the heartbeat never ran");
        Assert.Contains("[host-slept] the host was asleep for 2h00m", io.OutText, StringComparison.Ordinal);

        string events = Assert.Single(Directory.GetFiles(Path.Combine(plan.PlanDir, "logs"), "events.jsonl", SearchOption.AllDirectories));
        Assert.Contains(File.ReadAllLines(events), line =>
            JsonDocument.Parse(line).RootElement.GetProperty("kind").GetString() == "host-slept");
    }

    [Fact]
    public async Task AllowSleepIsPassedToTheAssertion()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        var keepAwake = new List<bool>();
        var services = new RunHostServices { KeepAwake = (allowSleep, _) => { keepAwake.Add(allowSleep); return null; } };
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(RunCommand.Create(io, TelemetryOverrides.None, services));

        int exit = await root.Parse(["run", plan.PlanDir, "--no-ui", "--no-log-server", "--allow-sleep"]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(exit == 0, io.OutText);
        Assert.Equal([true], keepAwake);
    }

    private sealed class SleepRecorder : IRunObserver
    {
        public List<(DateTimeOffset From, DateTimeOffset To, TimeSpan Slept, IReadOnlyList<string> InFlight)> Sleeps { get; } = [];

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }

        public void HostSlept(DateTimeOffset from, DateTimeOffset to, TimeSpan sleptFor, IReadOnlyList<string> inFlight) =>
            Sleeps.Add((from, to, sleptFor, [.. inFlight]));
    }
}
