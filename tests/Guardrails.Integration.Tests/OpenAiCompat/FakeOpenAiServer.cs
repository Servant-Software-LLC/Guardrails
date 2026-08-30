using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Guardrails.Integration.Tests.OpenAiCompat;

/// <summary>
/// A loopback, OpenAI-compatible HTTP server driven by a SCRIPTED response plan — the fixture the
/// <c>openai-compat</c> runner is written against (plan <c>28-local-inference-runner.md</c> §8).
///
/// <para><b>Its job is to misbehave.</b> Every row of §8's table is a way a real local-inference
/// server fails while looking fine: it truncates the prompt and answers confidently, it omits
/// <c>usage</c> after being asked for it, it 404s a model that was never pulled, it accepts a
/// <c>tools</c> array and calls nothing while returning an immaculate <c>{"pass": true}</c> (§6.6 —
/// the false GREEN this plan otherwise ships). None of that is reachable through a well-behaved
/// stub, so the script is the point: a test states the misbehaviour it wants and the server
/// performs it on the wire.</para>
///
/// <para><b>Why a real socket.</b> The #382 doctrine — <i>a component certified only against a fake
/// of the seam the run exercises is a green light over a broken wire</i> — and the seam here is the
/// OpenAI HTTP wire, not <c>IPromptRunner</c>. A fake <c>HttpMessageHandler</c> would certify the
/// runner against our own idea of HTTP; SSE framing, chunked transfer, a 404 body, a connection the
/// server closes are exactly what it must survive. This follows <c>LogServerTests</c>' precedent (a
/// real listener on loopback, an ephemeral port, teardown in <c>DisposeAsync</c>) and matches its
/// lifecycle: start in a factory, cancel + stop + await the accept loop on dispose, swallow only the
/// exceptions shutdown itself causes.</para>
///
/// <para><b>Why a raw <see cref="TcpListener"/> rather than <c>HttpListener</c>.</b> §7's zero-cost
/// condition — <i>a plan that declares no <c>openai-compat</c> block must cost ZERO HTTP requests</i>
/// — is to be proven "by a listener that fails the test on ANY accepted connection, never by a
/// counter the production code increments". <c>HttpListener</c> cannot report a connection that was
/// accepted and then said nothing; it only surfaces completed requests. <see cref="AcceptedConnections"/>
/// is incremented at the accept call itself, so a preflight that opens a socket and sends nothing is
/// still caught. That is the whole reason for hand-rolling the HTTP/1.1 framing below.</para>
///
/// <para>Bound to <c>127.0.0.1</c> on an ephemeral port. Requests are recorded (see
/// <see cref="Requests"/>) so a test can assert what the runner actually PUT ON THE WIRE — the
/// <c>tools</c> array, <c>stream_options.include_usage</c>, the prompt text whose size the §6.1
/// bounds are computed over.</para>
/// </summary>
/// <example>
/// <code>
/// await using var server = FakeOpenAiServer.Start(
///     ScriptedResponse.ReadToolCall(@"C:\etc\passwd"),           // refused by containment
///     ScriptedResponse.Completion("```json\n{\"pass\": true}\n```"));
/// // ... point the runner's block at server.Endpoint ...
/// Assert.Equal(2, server.Requests.Count);
/// </code>
/// </example>
public sealed class FakeOpenAiServer : IDisposable, IAsyncDisposable
{
    private const string CompletionId = "chatcmpl-fake";
    private const long CreatedEpoch = 1_780_000_000;

    /// <summary>
    /// Assistant content is streamed in slices of this many characters, so a client that only ever
    /// sees one delta per response is visibly not accumulating them.
    /// </summary>
    private const int ContentSliceChars = 24;

    private static readonly byte[] Crlf = "\r\n"u8.ToArray();
    private static readonly byte[] FinalChunk = "0\r\n\r\n"u8.ToArray();

    private readonly TcpListener _listener;
    private readonly FakeOpenAiScript _script;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;

    // The accept loop hands each connection to its own task, so everything below is shared state.
    private readonly object _gate = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly List<TcpClient> _liveClients = [];
    private readonly List<Task> _connectionTasks = [];
    private int _nextChatResponse;
    private int _acceptedConnections;
    private bool _disposed;

    private FakeOpenAiServer(TcpListener listener, FakeOpenAiScript script, int port)
    {
        _listener = listener;
        _script = script;
        Port = port;
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Start a server whose chat completions are served from <paramref name="chatResponses"/>, in order.</summary>
    public static FakeOpenAiServer Start(params ScriptedResponse[] chatResponses) =>
        Start(FakeOpenAiScript.Of(chatResponses));

    /// <summary>Start a server driven by <paramref name="script"/> (chat queue + the <c>/models</c> answer).</summary>
    public static FakeOpenAiServer Start(FakeOpenAiScript script)
    {
        ArgumentNullException.ThrowIfNull(script);

        // Bind to the numeric loopback address, never the name "localhost": the "never leaves this
        // machine" guarantee then holds regardless of a custom hosts file. Port 0 lets the OS pick a
        // free port and hand it back — no probe→bind TOCTOU window to retry around.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return new FakeOpenAiServer(listener, script, port);
    }

    /// <summary>The ephemeral loopback port this instance bound.</summary>
    public int Port { get; }

    /// <summary>The server root, e.g. <c>http://127.0.0.1:51234/</c>.</summary>
    public Uri BaseUri { get; }

    /// <summary>
    /// What goes in a block's <c>endpoint</c> field: <c>http://127.0.0.1:&lt;port&gt;/v1</c>, no trailing
    /// slash. Routing matches on the path's SUFFIX, so a caller that drops the <c>/v1</c> segment (or adds
    /// another) is still served — the fixture never fails a test over a base-URL convention.
    /// </summary>
    public string Endpoint => $"{BaseUri}v1";

    /// <summary>The chat-completions URL, for a test driving the socket directly.</summary>
    public string ChatCompletionsUrl => $"{Endpoint}/chat/completions";

    /// <summary>The model-listing URL (§7's reachability probe).</summary>
    public string ModelsUrl => $"{Endpoint}/models";

    /// <summary>
    /// How many TCP connections this listener has ACCEPTED, counted at the accept call — including a
    /// connection that was opened and then said nothing. §7's zero-cost condition ("a plan with no
    /// <c>openai-compat</c> block must cost zero HTTP requests") is asserted against this and nothing
    /// else: a counter the production code increments would measure our own bookkeeping.
    /// </summary>
    public int AcceptedConnections => Volatile.Read(ref _acceptedConnections);

    /// <summary>
    /// Every request served, in order — method, target, raw body, and the parsed facts a test needs to
    /// assert about the WIRE (whether a <c>tools</c> array was sent, whether <c>include_usage</c> was
    /// requested, the prompt text the §6.1 bounds are computed over).
    /// </summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_gate) { return [.. _requests]; } }
    }

    /// <summary>
    /// The chat requests only (i.e. excluding the <c>/models</c> probe), which is what a tool-loop test
    /// counts turns with.
    /// </summary>
    public IReadOnlyList<RecordedRequest> ChatRequests =>
        [.. Requests.Where(r => r.IsChatCompletion)];

    /// <summary>
    /// Wait until at least <paramref name="atLeast"/> connections have been accepted, or throw
    /// <see cref="TimeoutException"/>. An accept is observed asynchronously, so a test that connects a
    /// bare socket needs this rather than a race with the loop.
    /// </summary>
    public async Task WaitForAcceptedConnectionsAsync(
        int atLeast, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (AcceptedConnections < atLeast)
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Only {AcceptedConnections} connection(s) were accepted within {timeout}; expected at least {atLeast}.");
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    // --- accept + serve -----------------------------------------------------------------------

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                return; // shutdown — expected
            }
            catch (ObjectDisposedException)
            {
                return; // listener stopped
            }
            catch (SocketException)
            {
                return; // listener stopped mid-accept
            }

            // Counted HERE, before a single byte is read: a connection that sends nothing still counts.
            Interlocked.Increment(ref _acceptedConnections);

            Task served = Task.Run(() => ServeAsync(client));
            lock (_gate)
            {
                _liveClients.Add(client);
                _connectionTasks.Add(served);
            }
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();

            RawRequest? raw = await ReadRequestAsync(stream, _shutdown.Token);
            if (raw is null)
            {
                return; // connected and closed without a complete request head — still an accepted connection
            }

            var recorded = RecordedRequest.From(raw);
            lock (_gate) { _requests.Add(recorded); }

            await RouteAsync(stream, recorded);
            await stream.FlushAsync(_shutdown.Token);

            // Send FIN so the client sees a clean end-of-body, then let the socket go.
            try { client.Client.Shutdown(SocketShutdown.Send); } catch (SocketException) { /* client gone */ }
        }
        catch (IOException) { /* client gone mid-exchange */ }
        catch (SocketException) { /* client gone mid-exchange */ }
        catch (ObjectDisposedException) { /* torn down under us */ }
        catch (OperationCanceledException) { /* shutdown */ }
        finally
        {
            lock (_gate) { _liveClients.Remove(client); }
            client.Dispose();
        }
    }

    private Task RouteAsync(Stream stream, RecordedRequest request)
    {
        // Suffix routing: "/v1/chat/completions", "/chat/completions" and "/openai/v1/chat/completions"
        // all reach the same place, because the base-URL convention differs per engine and none of that
        // is what any test here is about.
        if (request.IsChatCompletion) { return RespondToChatAsync(stream, request); }
        if (request.IsModelListing) { return RespondToModelsAsync(stream); }

        return WriteBodyAsync(
            stream,
            404,
            ErrorBody($"FakeOpenAiServer has no route for {request.Method} {request.Target}.", "invalid_request_error", "not_found"));
    }

    private async Task RespondToModelsAsync(Stream stream)
    {
        ScriptedModels models = _script.Models;
        await WriteBodyAsync(stream, models.StatusCode, models.RenderBody());
    }

    private async Task RespondToChatAsync(Stream stream, RecordedRequest request)
    {
        ScriptedResponse response = NextChatResponse();

        if (response.StatusCode != 200)
        {
            IReadOnlyList<(string Name, string Value)> headers = response.RetryAfterSeconds is { } retryAfter
                ? [("Retry-After", retryAfter.ToString(CultureInfo.InvariantCulture))]
                : [];
            await WriteBodyAsync(stream, response.StatusCode, response.Body ?? DefaultErrorBody(response.StatusCode), headers);
            return;
        }

        ScriptedUsage? usage = response.Usage?.Resolve(request.PromptText, response.Content ?? "");
        string model = request.Model ?? "fake-model";

        if (request.StreamRequested)
        {
            await WriteStreamedCompletionAsync(stream, response, usage, model, request.IncludeUsageRequested);
        }
        else
        {
            await WriteWholeCompletionAsync(stream, response, usage, model);
        }
    }

    private ScriptedResponse NextChatResponse()
    {
        lock (_gate)
        {
            if (_nextChatResponse < _script.Chat.Count)
            {
                return _script.Chat[_nextChatResponse++];
            }

            _nextChatResponse++;
            // A drained script is a TEST bug, and the loud direction is a 500 that names it — never a
            // plausible completion that lets an over-running tool loop look healthy.
            return _script.ChatAfterScript ?? ScriptedResponse.HttpStatus(
                500,
                ErrorBody(
                    $"FakeOpenAiServer: the script is exhausted — {_script.Chat.Count} chat response(s) were scripted and this is request {_nextChatResponse}. Script another response, or set ChatAfterScript.",
                    "server_error",
                    "script_exhausted"));
        }
    }

    // --- the OpenAI wire ----------------------------------------------------------------------

    private static async Task WriteStreamedCompletionAsync(
        Stream stream, ScriptedResponse response, ScriptedUsage? usage, string model, bool includeUsageRequested)
    {
        await WriteHeadAsync(stream, 200, [("Content-Type", "text/event-stream"), ("Cache-Control", "no-cache"), ("Transfer-Encoding", "chunked")]);

        await WriteSseFrameAsync(stream, ChunkJson(model, new { role = "assistant" }, finishReason: null));

        foreach (string slice in Slice(response.Content, ContentSliceChars))
        {
            if (response.SliceDelay > TimeSpan.Zero) { await Task.Delay(response.SliceDelay); }
            await WriteSseFrameAsync(stream, ChunkJson(model, new { content = slice }, finishReason: null));
        }

        foreach (string frame in ToolCallDeltaFrames(response, model))
        {
            if (response.SliceDelay > TimeSpan.Zero) { await Task.Delay(response.SliceDelay); }
            await WriteSseFrameAsync(stream, frame);
        }

        await WriteSseFrameAsync(stream, ChunkJson(model, new { }, response.FinishReason));

        // The usage chunk is emitted ONLY when the caller asked for it AND the script supplies one.
        // A scripted null is §8's "omits usage despite include_usage" row — the field must be ABSENT,
        // so the runner records Usage = null and never {0, 0}.
        if (usage is not null && includeUsageRequested)
        {
            await WriteSseFrameAsync(stream, JsonSerializer.Serialize(new
            {
                id = CompletionId,
                @object = "chat.completion.chunk",
                created = CreatedEpoch,
                model,
                choices = Array.Empty<object>(),
                usage = usage.Payload(),
            }));
        }

        await WriteSseFrameAsync(stream, "[DONE]");
        await stream.WriteAsync(FinalChunk);
        await stream.FlushAsync();
    }

    private static async Task WriteWholeCompletionAsync(
        Stream stream, ScriptedResponse response, ScriptedUsage? usage, string model)
    {
        object message = response.ToolCalls.Count > 0
            ? new { role = "assistant", content = response.Content, tool_calls = ToolCallPayload(response.ToolCalls) }
            : new { role = "assistant", content = response.Content ?? "" };

        var choices = new[] { new { index = 0, message, finish_reason = response.FinishReason } };

        // Two branches rather than a null-ignoring serializer: "usage": null and no "usage" key at all
        // are DIFFERENT wire facts, and this fixture exists to tell them apart.
        string body = usage is null
            ? JsonSerializer.Serialize(new { id = CompletionId, @object = "chat.completion", created = CreatedEpoch, model, choices })
            : JsonSerializer.Serialize(new { id = CompletionId, @object = "chat.completion", created = CreatedEpoch, model, choices, usage = usage.Payload() });

        await WriteBodyAsync(stream, 200, body);
    }

    private static IEnumerable<string> ToolCallDeltaFrames(ScriptedResponse response, string model)
    {
        for (int i = 0; i < response.ToolCalls.Count; i++)
        {
            ScriptedToolCall call = response.ToolCalls[i];

            if (!response.SplitToolCallArguments)
            {
                yield return ChunkJson(model, new { tool_calls = new[] { call.Payload(i, call.ArgumentsJson) } }, finishReason: null);
                continue;
            }

            // Real servers stream a tool call's arguments across several deltas. A runner that reads
            // only the first fragment parses invalid JSON, so the fixture can produce that shape too.
            int split = call.ArgumentsJson.Length / 2;
            yield return ChunkJson(model, new { tool_calls = new[] { call.Payload(i, call.ArgumentsJson[..split]) } }, finishReason: null);
            yield return ChunkJson(model, new { tool_calls = new[] { call.Payload(i, call.ArgumentsJson[split..], nameAndId: false) } }, finishReason: null);
        }
    }

    private static object[] ToolCallPayload(IReadOnlyList<ScriptedToolCall> calls) =>
        [.. calls.Select((c, i) => c.Payload(i, c.ArgumentsJson))];

    private static string ChunkJson(string model, object delta, string? finishReason) =>
        JsonSerializer.Serialize(new
        {
            id = CompletionId,
            @object = "chat.completion.chunk",
            created = CreatedEpoch,
            model,
            choices = new[] { new { index = 0, delta, finish_reason = finishReason } },
        });

    private static IEnumerable<string> Slice(string? content, int size)
    {
        if (string.IsNullOrEmpty(content)) { yield break; }

        for (int start = 0; start < content.Length; start += size)
        {
            yield return content.Substring(start, Math.Min(size, content.Length - start));
        }
    }

    internal static string ErrorBody(string message, string type, string code) =>
        JsonSerializer.Serialize(new { error = new { message, type, param = (string?)null, code } });

    private static string DefaultErrorBody(int status) =>
        ErrorBody($"FakeOpenAiServer scripted HTTP {status}.", "api_error", status.ToString(CultureInfo.InvariantCulture));

    // --- HTTP/1.1 framing, by hand ------------------------------------------------------------

    private static async Task<RawRequest?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        // Byte-at-a-time to the blank line, so nothing of the BODY is ever consumed by the head read;
        // the body is then a single bulk read of exactly Content-Length bytes. Heads are small, and
        // "no leftover buffer to reconcile" is worth far more here than throughput.
        var head = new MemoryStream();
        byte[] one = new byte[1];
        int matched = 0;
        while (matched < 4)
        {
            int read = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken);
            if (read == 0) { return null; }

            head.WriteByte(one[0]);
            matched = one[0] == "\r\n\r\n"u8[matched] ? matched + 1 : one[0] == (byte)'\r' ? 1 : 0;
        }

        string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) { return null; }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0) { headers[line[..colon].Trim()] = line[(colon + 1)..].Trim(); }
        }

        string body = "";
        if (headers.TryGetValue("Content-Length", out string? lengthText)
            && int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int length)
            && length > 0)
        {
            byte[] payload = new byte[length];
            await stream.ReadExactlyAsync(payload, cancellationToken);
            body = Encoding.UTF8.GetString(payload);
        }

        return new RawRequest(requestLine[0], requestLine[1], body);
    }

    private static async Task WriteBodyAsync(
        Stream stream, int status, string body, IReadOnlyList<(string Name, string Value)>? extraHeaders = null)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        List<(string Name, string Value)> headers =
        [
            ("Content-Type", "application/json"),
            ("Content-Length", payload.Length.ToString(CultureInfo.InvariantCulture)),
        ];
        if (extraHeaders is not null) { headers.AddRange(extraHeaders); }

        await WriteHeadAsync(stream, status, headers);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    private static async Task WriteHeadAsync(Stream stream, int status, IReadOnlyList<(string Name, string Value)> headers)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(ReasonPhrase(status)).Append("\r\n");
        foreach ((string name, string value) in headers)
        {
            head.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        // One request per connection: the response body's end is then unambiguous and
        // AcceptedConnections stays a faithful count of what the client actually did.
        head.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
        await stream.FlushAsync();
    }

    private static async Task WriteSseFrameAsync(Stream stream, string data)
    {
        byte[] payload = Encoding.UTF8.GetBytes($"data: {data}\n\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(payload.Length.ToString("x", CultureInfo.InvariantCulture) + "\r\n"));
        await stream.WriteAsync(payload);
        await stream.WriteAsync(Crlf);
        await stream.FlushAsync();
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        422 => "Unprocessable Entity",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Status",
    };

    // --- teardown -----------------------------------------------------------------------------

    /// <summary>
    /// Stop accepting, drop every live connection, and wait for the accept loop and the in-flight
    /// handlers to finish. Bounded: a wedged handler must not hang the suite, and a leaked listener
    /// must not survive into the next test.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
        }

        await _shutdown.CancelAsync();
        try { _listener.Stop(); } catch (SocketException) { /* already stopped */ }

        TcpClient[] live;
        Task[] pending;
        lock (_gate)
        {
            live = [.. _liveClients];
            pending = [.. _connectionTasks];
        }

        foreach (TcpClient client in live)
        {
            try { client.Close(); } catch (SocketException) { /* already gone */ }
        }

        try { await _acceptLoop; } catch (Exception) { /* loop ended */ }
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { /* handler ended or timed out */ }

        _listener.Dispose();
        _shutdown.Dispose();
    }

    /// <summary>Synchronous teardown, for a test that holds the fixture in a plain <c>using</c>.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    internal sealed record RawRequest(string Method, string Target, string Body);

    /// <summary>
    /// One request as it ARRIVED — the raw body plus the handful of wire facts a test asserts on. The
    /// parsed fields are read off the body, never off anything the fixture was told.
    /// </summary>
    public sealed record RecordedRequest
    {
        /// <summary>HTTP method.</summary>
        public required string Method { get; init; }

        /// <summary>Request target as sent, including any query string.</summary>
        public required string Target { get; init; }

        /// <summary>The raw request body (empty for a GET).</summary>
        public required string Body { get; init; }

        /// <summary><c>POST</c> to a path ending <c>/chat/completions</c>.</summary>
        public bool IsChatCompletion { get; init; }

        /// <summary><c>GET</c> to a path ending <c>/models</c> — §7's reachability probe.</summary>
        public bool IsModelListing { get; init; }

        /// <summary>The <c>model</c> field, echoed back on the response.</summary>
        public string? Model { get; init; }

        /// <summary>Whether <c>"stream": true</c> was requested (§6.3 requires it of the runner).</summary>
        public bool StreamRequested { get; init; }

        /// <summary>Whether <c>stream_options.include_usage</c> was requested.</summary>
        public bool IncludeUsageRequested { get; init; }

        /// <summary>
        /// Whether a <c>tools</c> array was on the wire. §6.6's false GREEN is precisely "tools were
        /// sent, none were called, the verdict was immaculate" — so a test must be able to prove the
        /// first half independently of the runner's own account of itself.
        /// </summary>
        public bool HasTools { get; init; }

        /// <summary>How many tools were offered.</summary>
        public int ToolCount { get; init; }

        /// <summary>The tool names offered, in order.</summary>
        public IReadOnlyList<string> ToolNames { get; init; } = [];

        /// <summary><c>options.num_ctx</c> if set — §6.1's "belt, never enforcement".</summary>
        public int? NumCtx { get; init; }

        /// <summary>Every message's <c>role</c>, in order.</summary>
        public IReadOnlyList<string> MessageRoles { get; init; } = [];

        /// <summary>
        /// Every message's textual content, concatenated. This is the size the §6.1 bounds are computed
        /// over, and what a truncation row is a lie ABOUT: the server received all of it and reported
        /// having read a fraction.
        /// </summary>
        public string PromptText { get; init; } = "";

        internal static RecordedRequest From(RawRequest raw)
        {
            string path = raw.Target.Split('?')[0];
            var request = new RecordedRequest
            {
                Method = raw.Method,
                Target = raw.Target,
                Body = raw.Body,
                IsChatCompletion = raw.Method == "POST" && path.EndsWith("/chat/completions", StringComparison.Ordinal),
                IsModelListing = raw.Method == "GET" && path.EndsWith("/models", StringComparison.Ordinal),
            };

            if (raw.Body.Length == 0) { return request; }

            JsonElement root;
            try
            {
                using JsonDocument document = JsonDocument.Parse(raw.Body);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return request; // a malformed body is itself something a test may want to assert on
            }

            if (root.ValueKind != JsonValueKind.Object) { return request; }

            var roles = new List<string>();
            var prompt = new StringBuilder();
            if (root.TryGetProperty("messages", out JsonElement messages) && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement message in messages.EnumerateArray())
                {
                    if (message.TryGetProperty("role", out JsonElement role) && role.ValueKind == JsonValueKind.String)
                    {
                        roles.Add(role.GetString()!);
                    }

                    if (message.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String)
                    {
                        prompt.Append(content.GetString());
                    }
                }
            }

            var toolNames = new List<string>();
            bool hasTools = root.TryGetProperty("tools", out JsonElement tools) && tools.ValueKind == JsonValueKind.Array;
            if (hasTools)
            {
                foreach (JsonElement tool in tools.EnumerateArray())
                {
                    if (tool.TryGetProperty("function", out JsonElement function)
                        && function.TryGetProperty("name", out JsonElement name)
                        && name.ValueKind == JsonValueKind.String)
                    {
                        toolNames.Add(name.GetString()!);
                    }
                }
            }

            bool includeUsage =
                root.TryGetProperty("stream_options", out JsonElement streamOptions)
                && streamOptions.ValueKind == JsonValueKind.Object
                && streamOptions.TryGetProperty("include_usage", out JsonElement flag)
                && flag.ValueKind == JsonValueKind.True;

            int? numCtx = null;
            if (root.TryGetProperty("options", out JsonElement options)
                && options.ValueKind == JsonValueKind.Object
                && options.TryGetProperty("num_ctx", out JsonElement ctx)
                && ctx.ValueKind == JsonValueKind.Number)
            {
                numCtx = ctx.GetInt32();
            }

            return request with
            {
                Model = root.TryGetProperty("model", out JsonElement model) && model.ValueKind == JsonValueKind.String
                    ? model.GetString()
                    : null,
                StreamRequested = root.TryGetProperty("stream", out JsonElement stream) && stream.ValueKind == JsonValueKind.True,
                IncludeUsageRequested = includeUsage,
                HasTools = hasTools,
                ToolCount = toolNames.Count,
                ToolNames = toolNames,
                NumCtx = numCtx,
                MessageRoles = roles,
                PromptText = prompt.ToString(),
            };
        }
    }
}

/// <summary>
/// The response plan a <see cref="FakeOpenAiServer"/> performs: chat completions served in order, and
/// the one answer <c>GET /models</c> gives.
/// </summary>
public sealed class FakeOpenAiScript
{
    /// <summary>Chat completions, served one per <c>POST /chat/completions</c>, in order.</summary>
    public List<ScriptedResponse> Chat { get; } = [];

    /// <summary>
    /// Served once <see cref="Chat"/> is drained. Left null, a drained script answers HTTP 500 naming
    /// itself — a tool loop that ran longer than the test scripted for is a defect, not a completion.
    /// </summary>
    public ScriptedResponse? ChatAfterScript { get; set; }

    /// <summary>
    /// What <c>GET /models</c> answers. The default lists NOTHING: §7 asserts every declared model
    /// appears in the listing, so a test that wants that assertion to pass must say which models exist.
    /// Defaulting to "your model is present" would quietly pre-satisfy the check under test.
    /// </summary>
    public ScriptedModels Models { get; set; } = ScriptedModels.List();

    /// <summary>A script whose chat queue is <paramref name="chat"/>.</summary>
    public static FakeOpenAiScript Of(params ScriptedResponse[] chat)
    {
        var script = new FakeOpenAiScript();
        script.Chat.AddRange(chat);
        return script;
    }

    /// <summary>Fluent: set <see cref="Models"/>.</summary>
    public FakeOpenAiScript Listing(ScriptedModels models)
    {
        Models = models;
        return this;
    }

    /// <summary>Fluent: set <see cref="ChatAfterScript"/>.</summary>
    public FakeOpenAiScript ThenRepeat(ScriptedResponse response)
    {
        ChatAfterScript = response;
        return this;
    }
}

/// <summary>What <c>GET {endpoint}/models</c> answers (plan §7).</summary>
/// <param name="StatusCode">HTTP status.</param>
/// <param name="ModelIds">Model ids for a 200 listing.</param>
/// <param name="Body">An explicit body, overriding the rendered listing.</param>
public sealed record ScriptedModels(int StatusCode, IReadOnlyList<string> ModelIds, string? Body = null)
{
    /// <summary>A 200 listing carrying <paramref name="ids"/>.</summary>
    public static ScriptedModels List(params string[] ids) => new(200, ids);

    /// <summary>
    /// 404. §7's downgrade case: an engine that serves chat perfectly but has no listing endpoint must
    /// produce a WARNING and a skipped model-presence assertion — never a halt.
    /// </summary>
    public static ScriptedModels NotFound() => new(
        404,
        [],
        FakeOpenAiServer.ErrorBody("Not Found", "invalid_request_error", "not_found"));

    /// <summary>405 — the other shape of "the server answered, but does not offer this".</summary>
    public static ScriptedModels MethodNotAllowed() => new(
        405,
        [],
        FakeOpenAiServer.ErrorBody("Method Not Allowed", "invalid_request_error", "method_not_allowed"));

    /// <summary>
    /// Any other status, e.g. a 500 — which §7 says must stay a HALT, because "the server is broken" is
    /// not "the server does not offer this".
    /// </summary>
    public static ScriptedModels Status(int statusCode, string body) => new(statusCode, [], body);

    internal string RenderBody() =>
        Body ?? JsonSerializer.Serialize(new
        {
            @object = "list",
            data = ModelIds.Select(id => new { id, @object = "model", created = 1_780_000_000L, owned_by = "fake" }).ToArray(),
        });
}

/// <summary>
/// <c>usage</c> for one completion. <see cref="Measured"/> reports numbers derived from what actually
/// arrived — the honest baseline — so an explicit <see cref="Of"/> reads as the deliberate lie it is.
/// </summary>
/// <param name="PromptTokens">Explicit prompt tokens, or null to measure.</param>
/// <param name="CompletionTokens">Explicit completion tokens, or null to measure.</param>
public sealed record ScriptedUsage(int? PromptTokens, int? CompletionTokens)
{
    /// <summary>
    /// Derived from the request and the answer: <c>chars / 3</c>, which sits between §6.1's pessimistic
    /// pre-send estimate (<c>ceil(chars/3)</c>) and its optimistic post-check floor
    /// (<c>floor(chars/4)</c>) — i.e. a server telling the truth clears the truncation check.
    /// </summary>
    public static ScriptedUsage Measured { get; } = new(null, null);

    /// <summary>Exactly these numbers, whatever arrived.</summary>
    public static ScriptedUsage Of(int promptTokens, int completionTokens) => new(promptTokens, completionTokens);

    internal ScriptedUsage Resolve(string promptText, string content) => new(
        PromptTokens ?? Math.Max(1, promptText.Length / 3),
        CompletionTokens ?? Math.Max(1, content.Length / 3));

    internal object Payload()
    {
        int prompt = PromptTokens ?? 0;
        int completion = CompletionTokens ?? 0;
        return new { prompt_tokens = prompt, completion_tokens = completion, total_tokens = prompt + completion };
    }
}

/// <summary>One tool call the scripted model asks for.</summary>
/// <param name="Id">The <c>tool_call_id</c> the client must echo back.</param>
/// <param name="Name">The function name.</param>
/// <param name="ArgumentsJson">The arguments, as the JSON STRING the protocol carries (not an object).</param>
public sealed record ScriptedToolCall(string Id, string Name, string ArgumentsJson)
{
    /// <summary>
    /// A read of <paramref name="absolutePath"/>. Pass a path outside the permitted roots to drive §5's
    /// containment refusal, and three of these in a row to drive #452's
    /// <c>AbortAfterConsecutiveToolDenials</c> bound.
    /// </summary>
    public static ScriptedToolCall Read(string absolutePath, string toolName = "Read", string argumentName = "file_path") =>
        new(
            $"call_{Guid.NewGuid():N}"[..12],
            toolName,
            JsonSerializer.Serialize(new Dictionary<string, string> { [argumentName] = absolutePath }));

    /// <summary>A call with arbitrary arguments — for a tool this fixture does not model.</summary>
    public static ScriptedToolCall Of(string name, string argumentsJson) =>
        new($"call_{Guid.NewGuid():N}"[..12], name, argumentsJson);

    internal object Payload(int index, string arguments, bool nameAndId = true) => nameAndId
        ? new { index, id = Id, type = "function", function = new { name = Name, arguments } }
        : new { index, function = new { arguments } };
}

/// <summary>
/// One scripted answer to <c>POST /chat/completions</c>. Every factory here is a row of plan §8's
/// table, named after the failure it performs rather than the bytes it emits.
/// </summary>
public sealed record ScriptedResponse
{
    /// <summary>HTTP status. Anything other than 200 short-circuits to <see cref="Body"/>.</summary>
    public int StatusCode { get; init; } = 200;

    /// <summary>An explicit body — the error payload for a non-200.</summary>
    public string? Body { get; init; }

    /// <summary>The assistant's message content.</summary>
    public string? Content { get; init; }

    /// <summary>Tool calls the model asks for. Non-empty ⇒ <c>finish_reason</c> defaults to <c>tool_calls</c>.</summary>
    public IReadOnlyList<ScriptedToolCall> ToolCalls { get; init; } = [];

    /// <summary><c>finish_reason</c> on the final choice.</summary>
    public string FinishReason { get; init; } = "stop";

    /// <summary>
    /// The <c>usage</c> block, or null to OMIT it entirely — §8's "omits usage despite include_usage".
    /// Null means the key is absent, not <c>"usage": null</c>, and never <c>{0, 0}</c>.
    /// </summary>
    public ScriptedUsage? Usage { get; init; } = ScriptedUsage.Measured;

    /// <summary>Split each tool call's arguments across two SSE deltas, as real servers do.</summary>
    public bool SplitToolCallArguments { get; init; }

    /// <summary>Delay between streamed frames — for a test about streaming liveness rather than content.</summary>
    public TimeSpan SliceDelay { get; init; }

    /// <summary>A <c>Retry-After</c> header, in seconds, on a non-200.</summary>
    public int? RetryAfterSeconds { get; init; }

    /// <summary>An ordinary completion that answers <paramref name="content"/> and reports honest usage.</summary>
    public static ScriptedResponse Completion(string content) =>
        new() { Content = content };

    /// <summary>An ordinary completion reporting the usage numbers given.</summary>
    public static ScriptedResponse Completion(string content, int promptTokens, int completionTokens) =>
        new() { Content = content, Usage = ScriptedUsage.Of(promptTokens, completionTokens) };

    /// <summary>
    /// §8 row 2, and §6.1's whole reason for existing: the server SILENTLY TRUNCATED the prompt and
    /// answered confidently anyway. The full prompt still arrives (assert it on
    /// <see cref="FakeOpenAiServer.RecordedRequest.PromptText"/>) — the lie is
    /// <paramref name="reportedPromptTokens"/>, below the <c>floor(chars/4)</c> after-check.
    /// </summary>
    public static ScriptedResponse SilentlyTruncatedPrompt(string content, int reportedPromptTokens) =>
        new() { Content = content, Usage = ScriptedUsage.Of(reportedPromptTokens, Math.Max(1, content.Length / 3)) };

    /// <summary>
    /// §8 row 3: no <c>usage</c> at all, even though <c>stream_options.include_usage</c> was requested.
    /// The runner must record <c>Usage = null</c>, never <c>{0, 0}</c>.
    /// </summary>
    public static ScriptedResponse CompletionWithoutUsage(string content) =>
        new() { Content = content, Usage = null };

    /// <summary>
    /// §8 row 4: 404 <c>model not found</c>. Classified <c>Error</c>, never <c>Transient</c> — a pause
    /// waits for a human action no waiting produces.
    /// </summary>
    public static ScriptedResponse ModelNotFound(string model) => new()
    {
        StatusCode = 404,
        Body = FakeOpenAiServer.ErrorBody(
            $"model '{model}' not found, try pulling it first", "invalid_request_error", "model_not_found"),
    };

    /// <summary>§8 row 5: 429. Classified <c>Transient</c>; the shipped pause runs, the retry budget is untouched.</summary>
    public static ScriptedResponse RateLimited(int? retryAfterSeconds = null) => new()
    {
        StatusCode = 429,
        RetryAfterSeconds = retryAfterSeconds,
        Body = FakeOpenAiServer.ErrorBody("rate limit exceeded", "rate_limit_error", "rate_limit_exceeded"),
    };

    /// <summary>Auth failure — <c>Error</c> naming <c>apiKeyEnv</c>, because retrying a bad key is a loop.</summary>
    public static ScriptedResponse Unauthorized() => new()
    {
        StatusCode = 401,
        Body = FakeOpenAiServer.ErrorBody("invalid api key", "invalid_request_error", "invalid_api_key"),
    };

    /// <summary>
    /// §8: the server REJECTS <c>tools</c> outright with a 400. A server with no tool support cannot host
    /// a verifier, so §7's preflight must HALT naming the block, the endpoint and the model.
    /// </summary>
    public static ScriptedResponse ToolsRejected() => new()
    {
        StatusCode = 400,
        Body = FakeOpenAiServer.ErrorBody(
            "unsupported parameter: 'tools' is not supported by this model", "invalid_request_error", "unsupported_parameter"),
    };

    /// <summary>§8 row 6: <c>finish_reason: "length"</c> — the shipped <c>OutputCap</c>.</summary>
    public static ScriptedResponse OutputCapped(string content) =>
        new() { Content = content, FinishReason = "length" };

    /// <summary>
    /// §8 row 7: a ```json block carrying <paramref name="json"/> that is NOT the last block, followed by
    /// prose and a SECOND fenced block. The strict extractor must take the LAST block.
    /// </summary>
    public static ScriptedResponse JsonBlockThenProse(string json, string trailingProse, string trailingJson) =>
        Completion($"Here is my reasoning.\n\n```json\n{json}\n```\n\n{trailingProse}\n\n```json\n{trailingJson}\n```\n");

    /// <summary>§8 row 8: prose with no JSON at all. No verdict file may be written.</summary>
    public static ScriptedResponse ProseWithNoJson(string prose = "I read the files and everything looks correct to me.") =>
        Completion(prose);

    /// <summary>
    /// §8 row 9: prose AROUND a bare JSON object — no fence. <c>PromptJsonExtractor</c>'s
    /// last-top-level-object fallback is what recovers it (§3.3's payoff).
    /// </summary>
    public static ScriptedResponse ProseAroundJson(string json) =>
        Completion($"After reviewing the evidence my verdict is {json} — I hope that helps.");

    /// <summary>
    /// §8 row 10: a tool call for <paramref name="absolutePath"/>. Point it outside both roots to drive
    /// §5's containment refusal; script three in a row for #452's denial bound.
    /// </summary>
    public static ScriptedResponse ReadToolCall(string absolutePath) =>
        ToolCallTurn(ScriptedToolCall.Read(absolutePath));

    /// <summary>A turn that asks for these tool calls and finishes with <c>finish_reason: "tool_calls"</c>.</summary>
    public static ScriptedResponse ToolCallTurn(params ScriptedToolCall[] calls) =>
        new() { ToolCalls = calls, FinishReason = "tool_calls", Content = null };

    /// <summary>
    /// §6.6 — THE false GREEN. The server accepted a <c>tools</c> array, called NOTHING, and returned an
    /// immaculate verdict: valid JSON, a real boolean, in the last fenced block. Every malformedness
    /// check passes and the guardrail goes green over evidence nobody read. The request's
    /// <see cref="FakeOpenAiServer.RecordedRequest.HasTools"/> proves the tools were on the wire.
    /// </summary>
    public static ScriptedResponse AcceptsToolsButCallsNone(
        string verdictJson = """{ "pass": true, "summary": "the implementation satisfies the criterion" }""") =>
        Completion($"```json\n{verdictJson}\n```\n");

    /// <summary>Any other status and body — 503, 529, or a shape this fixture does not name.</summary>
    public static ScriptedResponse HttpStatus(int statusCode, string body) =>
        new() { StatusCode = statusCode, Body = body };
}
