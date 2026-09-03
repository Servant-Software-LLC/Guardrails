using System.Net;
using System.Text.Json;
using Guardrails.Cli.Ui;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.RunEvents;

/// <summary>
/// Plan 34's third leg: a top-level <c>GET /events</c> route on the SAME loopback <see cref="LogServer"/>
/// that already tails <c>/tasks/{id}</c> logs and serves <c>/diagram.html</c> (issue #522). The reviewed
/// plan settled that v1 needs this HTTP endpoint, not just the durable <c>events.jsonl</c> file on disk: an
/// agent-side monitor takes a stream source NATIVELY, so a subscribable endpoint removes stdout-grep from
/// the supervision path entirely — the failure mode behind issue #585 — rather than merely mitigating it.
///
/// <para>A late subscriber must first receive whatever the run already wrote, then keep receiving new rows
/// as they are appended — the SAME open connection, not a poll-again-later contract like the existing
/// <c>/file</c> route. A run that has not emitted anything yet (no <c>events.jsonl</c> on disk) is still a
/// HEALTHY run, so that case must read as an empty stream, never a 404/500 — mirroring the established
/// "declared but not started yet ⇒ empty text, not an error" idiom <c>LogServer.WriteFile</c> already uses
/// for an attempt that has not begun.</para>
///
/// <para>Every test here is written to FAIL right now: <c>/events</c> is not a route <see cref="LogServer"/>
/// recognises yet (task 12 adds it), so every request below 404s via the existing
/// <c>segments[0] != "tasks"</c> catch-all.</para>
/// </summary>
public sealed class EventsEndpointTests
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>How long a single expected line is given to arrive over an already-open stream.</summary>
    private static readonly TimeSpan LineTimeout = TimeSpan.FromSeconds(5);

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task EventsEndpoint_StreamsExistingEventsToALateSubscriber()
    {
        // The event was written BEFORE anyone subscribed — a consumer attaching mid-run must still see it,
        // not just events emitted from the moment it connects onward.
        using var temp = new TempPlan();
        temp.WriteEventsFile(
            """{"kind":"attemptFinished","runId":"test-run","taskId":"01-alpha","attempt":1,"outcome":"succeeded"}""" + "\n");
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        using HttpResponseMessage response = await Http.GetAsync(
            $"{server.BaseUrl}events", HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var reader = new StreamReader(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        string line = await ReadLineWithTimeoutAsync(reader);

        using JsonDocument doc = JsonDocument.Parse(line);
        Assert.Equal("01-alpha", doc.RootElement.GetProperty("taskId").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("attempt").GetInt32());
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task EventsEndpoint_StreamsSubsequentEventsAsTheyAreAppended()
    {
        using var temp = new TempPlan();
        temp.WriteEventsFile(
            """{"kind":"attemptFinished","runId":"test-run","taskId":"01-alpha","attempt":1,"outcome":"succeeded"}""" + "\n");
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        using HttpResponseMessage response = await Http.GetAsync(
            $"{server.BaseUrl}events", HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var reader = new StreamReader(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));

        // Drain the row that was already on disk before this test's subscriber attached.
        string first = await ReadLineWithTimeoutAsync(reader);
        Assert.Contains("\"attempt\":1", first);

        // A row appended AFTER the subscriber is already attached must arrive over the very same open
        // connection — no second request, no re-poll.
        temp.AppendEventsFile(
            """{"kind":"attemptFinished","runId":"test-run","taskId":"01-alpha","attempt":2,"outcome":"maxTurns"}""" + "\n");

        string second = await ReadLineWithTimeoutAsync(reader);
        using JsonDocument doc = JsonDocument.Parse(second);
        Assert.Equal(2, doc.RootElement.GetProperty("attempt").GetInt32());
        Assert.Equal("maxTurns", doc.RootElement.GetProperty("outcome").GetString());
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task EventsEndpoint_EmitsOneParseableEventPerMessage()
    {
        using var temp = new TempPlan();
        temp.WriteEventsFile(string.Concat(
            """{"kind":"attemptFinished","runId":"test-run","taskId":"01-alpha","attempt":1,"outcome":"succeeded"}""" + "\n",
            """{"kind":"attemptFinished","runId":"test-run","taskId":"01-alpha","attempt":2,"outcome":"maxTurns"}""" + "\n",
            """{"kind":"attemptFinished","runId":"test-run","taskId":"02-beta","attempt":1,"outcome":"guardrailFailed"}""" + "\n"));
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First"), Task("02-beta", "Second")]);

        using HttpResponseMessage response = await Http.GetAsync(
            $"{server.BaseUrl}events", HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var reader = new StreamReader(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));

        (string TaskId, int Attempt)[] expected =
        [
            ("01-alpha", 1),
            ("01-alpha", 2),
            ("02-beta", 1)
        ];

        foreach ((string taskId, int attempt) in expected)
        {
            string line = await ReadLineWithTimeoutAsync(reader);

            // Independently parseable — one message is one COMPLETE JSON object on its own line, never a
            // fragment that only parses once concatenated with a sibling line.
            using JsonDocument doc = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.Equal(taskId, doc.RootElement.GetProperty("taskId").GetString());
            Assert.Equal(attempt, doc.RootElement.GetProperty("attempt").GetInt32());
        }
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task EventsEndpoint_OnAMissingEventsFile_ReturnsAnEmptyStreamNotAnError()
    {
        // No events.jsonl written at all — a run that has emitted nothing yet is still a HEALTHY run, and
        // must not read like a broken endpoint (mirrors LogServer.WriteFile's "not started yet" idiom).
        using var temp = new TempPlan();
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        HttpResponseMessage response = await Http.GetAsync(
            $"{server.BaseUrl}events", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, body);
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task LogServer_StillServesItsExistingRoutes()
    {
        using var temp = new TempPlan();
        temp.WriteEventsFile(
            """{"kind":"attemptFinished","runId":"test-run","taskId":"01-alpha","attempt":1,"outcome":"succeeded"}""" + "\n");
        temp.WriteDiagramFile("<!doctype html><html><body>diagram</body></html>");
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        // Exercise the NEW route first and let it close fully before touching the others, so this pins a
        // routing regression (the new top-level case must not shadow or otherwise disturb the pre-existing
        // ones) without assuming anything about how long a real /events connection stays open.
        using (HttpResponseMessage eventsResponse = await Http.GetAsync(
                   $"{server.BaseUrl}events", HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, eventsResponse.StatusCode);
        }

        string taskPageHtml = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha");
        Assert.Contains("01-alpha", taskPageHtml);

        HttpResponseMessage diagramResponse = await Http.GetAsync(
            $"{server.BaseUrl}diagram.html", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, diagramResponse.StatusCode);
        string diagramBody = await diagramResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("diagram", diagramBody);

        HttpResponseMessage unknownResponse = await Http.GetAsync(
            $"{server.BaseUrl}tasks/does-not-exist", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknownResponse.StatusCode);
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
        string? line = await reader.ReadLineAsync(cts.Token);
        Assert.NotNull(line); // null means the stream ended before delivering the expected row
        return line!;
    }

    private static LogServer Start(string planDir, IReadOnlyList<TaskNode> tasks)
    {
        LogServer? server = LogServer.TryStart(planDir, TempPlan.RunId, tasks, port: 0, TextWriter.Null);
        Assert.NotNull(server); // a normal host can bind a loopback ephemeral port
        return server!;
    }

    private static async Task<string> GetStringAsync(string url) =>
        await Http.GetStringAsync(url, TestContext.Current.CancellationToken);

    private static TaskNode Task(string id, string description) => new()
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

        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "gr-events-endpoint-" + Guid.NewGuid().ToString("N"));

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

        /// <summary>Write this run's <c>diagram.html</c> directly under <c>logs/&lt;runId&gt;/</c> (issue #522).</summary>
        public void WriteDiagramFile(string content)
        {
            string dir = Path.Combine(Dir, "logs", RunId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "diagram.html"), content);
        }

        public void Dispose()
        {
            // UnauthorizedAccessException is NOT a subtype of IOException on .NET — catch both so a
            // locked file on Windows doesn't mask the original test failure.
            try { Directory.Delete(Dir, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }
}
