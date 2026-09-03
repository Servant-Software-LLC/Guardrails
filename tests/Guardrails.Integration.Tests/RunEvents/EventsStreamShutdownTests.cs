using System.Net;
using System.Text.Json;
using Guardrails.Cli.Ui;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.RunEvents;

/// <summary>
/// Plan 34's <c>run-finished</c> row (task 05) exists to be the LAST thing a live <c>GET /events</c>
/// subscriber sees — the signal that the run is truly over. It is appended microseconds before the log
/// server is disposed. <see cref="LogServer.WriteEventsStream"/>'s tail loop reads the file, and on an
/// empty read waits on <c>_shutdown.Token.WaitHandle</c> for up to its ~150ms poll interval; if shutdown
/// is signalled WHILE it is parked in that wait, it returns immediately with NO further read of the file
/// — so a row landing on disk in that same narrow window is written, but never makes it onto the wire.
/// That silently defeats the entire payoff of <c>run-finished</c>: a subscriber never learns the run
/// ended.
///
/// <para>The first test below pins exactly that gap and is written to FAIL right now (task 13 fixes
/// <see cref="LogServer"/>, not this file). The second test pins the adjacent, ALREADY-correct behaviour
/// that must never regress while fixing the first: a run that has not written <c>events.jsonl</c> at all
/// completes its <c>/events</c> request with an empty 200 immediately, rather than holding the connection
/// open — an implementer chasing the first test who instead makes every <c>/events</c> response wait for
/// a terminal row would turn this second test into a HANG, not a failure, which is worse than either red
/// or green.</para>
/// </summary>
public sealed class EventsStreamShutdownTests
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>How long a single expected line is given to arrive over an already-open stream.</summary>
    private static readonly TimeSpan LineTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// <see cref="LogServer.WriteEventsStream"/> re-polls <c>events.jsonl</c> for growth every ~150ms
    /// once it has caught up (an empty read parks it in <c>_shutdown.Token.WaitHandle.WaitOne</c> for
    /// that long before it re-reads). Waiting comfortably longer than that — AFTER the subscriber has
    /// already drained every byte on disk at the time it connected — guarantees the tail loop has done
    /// its first empty read since and settled into that poll wait, so the append-then-shutdown below
    /// lands inside the exact window the defect lives in every time, deterministically, instead of
    /// racing a real multi-process run to land a row in a ~150ms gap by chance.
    /// </summary>
    private static readonly TimeSpan PastOnePollCycle = TimeSpan.FromMilliseconds(400);

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task ASubscriberReceivesARowAppendedJustBeforeShutdown()
    {
        using var temp = new TempPlan();
        // A row already on disk before the server starts, so the FIRST fs.Read in the tail loop returns
        // data immediately and flushes response headers (HttpListener does not flush chunked headers
        // until the first write) — an empty starting file would leave GetAsync(ResponseHeadersRead)
        // hanging until the poll loop happens to write something, which is not what this test is about.
        temp.WriteEventsFile(
            """{"kind":"attemptFinished","runId":"test-run","taskId":"01-alpha","attempt":1,"outcome":"succeeded"}""" + "\n");
        LogServer server = Start(temp.Dir, [MakeTask("01-alpha", "First")]);

        using HttpResponseMessage response = await Http.GetAsync(
            $"{server.BaseUrl}events", HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var reader = new StreamReader(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));

        // Drain the row that was already on disk before this subscriber attached — once this returns,
        // the tail loop has consumed every byte written so far and its NEXT read is guaranteed empty.
        string first = await ReadLineWithTimeoutAsync(reader);
        Assert.Contains("\"attempt\":1", first);

        // Let the tail loop settle into its poll wait (see PastOnePollCycle) before landing the row.
        await Task.Delay(PastOnePollCycle, TestContext.Current.CancellationToken);

        // The run-finished row, written the instant before the server goes away — exactly the sequence
        // a real run's Dispose-in-a-finally performs around RunEventStream.RunFinished.
        temp.AppendEventsFile(
            """{"kind":"runFinished","runId":"test-run","exitCode":0}""" + "\n");

        // Shut down immediately behind the append, with no further delay — the defect is that shutdown
        // racing in right behind a fresh append drops that row on the floor instead of flushing it first.
        await server.DisposeAsync();

        string line = await ReadLineWithTimeoutAsync(reader);
        using JsonDocument doc = JsonDocument.Parse(line);
        Assert.Equal("runFinished", doc.RootElement.GetProperty("kind").GetString());
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task AMissingEventsFileStillCompletesWithAnEmptyBody()
    {
        // No events.jsonl written at all — a run that has emitted nothing yet is still a HEALTHY run,
        // and /events must complete immediately with an empty 200, never hang waiting for a terminal
        // row that will never come. This is deliberate (LogServer.WriteEventsStream's doc comment) and
        // already pinned in EventsEndpointTests; it is re-asserted here as a declared exemption from
        // this file's red census, and as a guardrail against "fix the first test by blocking on
        // run-finished" — that change would turn this test into a hang, not a failure.
        using var temp = new TempPlan();
        await using LogServer server = Start(temp.Dir, [MakeTask("01-alpha", "First")]);

        HttpResponseMessage response = await Http.GetAsync(
            $"{server.BaseUrl}events", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, body);
    }

    // --- helpers ----------------------------------------------------------------------------

    /// <summary>
    /// Reads one line with a bounded wait, so a stalled/never-arriving row fails the test on a timeout
    /// instead of hanging the suite forever.
    /// </summary>
    private static async Task<string> ReadLineWithTimeoutAsync(StreamReader reader)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(LineTimeout);
        string? line;
        try
        {
            line = await reader.ReadLineAsync(cts.Token);
        }
        catch (IOException)
        {
            // A forceful listener teardown mid-shutdown tears the socket down instead of ending the
            // chunked response cleanly — the same "row never arrived" outcome as a graceful end of
            // stream, just noisier, so it collapses to the same assertion below.
            line = null;
        }

        Assert.NotNull(line); // null means the stream ended (or was torn down) before delivering the expected row
        return line!;
    }

    private static LogServer Start(string planDir, IReadOnlyList<TaskNode> tasks)
    {
        LogServer? server = LogServer.TryStart(planDir, TempPlan.RunId, tasks, port: 0, TextWriter.Null);
        Assert.NotNull(server); // a normal host can bind a loopback ephemeral port
        return server!;
    }

    private static TaskNode MakeTask(string id, string description) => new()
    {
        Id = id,
        Directory = id,
        Description = description,
        Action = new ActionDefinition { Path = "action.ps1", Kind = ActionKind.Script },
        Guardrails = [new GuardrailDefinition { Name = "01-x", Path = "01-x.ps1", Kind = ActionKind.Script }]
    };

    /// <summary>A throwaway plan directory under the temp path; cleaned up on dispose.</summary>
    private sealed class TempPlan : IDisposable
    {
        /// <summary>A fixed run id so the fixtures and the server agree on which logs/&lt;runId&gt;/ tree to use.</summary>
        public const string RunId = "test-run";

        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "gr-events-shutdown-" + Guid.NewGuid().ToString("N"));

        public TempPlan() => Directory.CreateDirectory(Dir);

        /// <summary>Write (overwrite) this run's <c>events.jsonl</c>, the file the /events route serves.</summary>
        public void WriteEventsFile(string content)
        {
            string dir = Path.Combine(Dir, "logs", RunId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "events.jsonl"), content);
        }

        /// <summary>
        /// Append to the existing <c>events.jsonl</c>, mirroring how a live run's observer grows it — the
        /// exact mutation an already-open subscriber must notice.
        /// </summary>
        public void AppendEventsFile(string extra)
        {
            string path = Path.Combine(Dir, "logs", RunId, "events.jsonl");
            File.AppendAllText(path, extra);
        }

        public void Dispose()
        {
            // UnauthorizedAccessException is NOT a subtype of IOException on .NET — catch both so a
            // locked file on Windows doesn't mask the original test failure.
            try { Directory.Delete(Dir, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }
}
