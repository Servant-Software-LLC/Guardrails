using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Guardrails.Integration.Tests.ClaudeGateway;

/// <summary>
/// A minimal loopback HTTP/1.1 server for the #782 gateway preflight tests — a raw <see cref="TcpListener"/>, like
/// <c>FakeOpenAiServer</c>, so the tests can count ACCEPTED CONNECTIONS (the zero-connection proof) and every request
/// the preflight actually sent, rather than trusting a counter the code under test increments. Each response closes
/// its connection; routes are keyed by <c>"METHOD /path"</c>, and an unrouted request answers 404.
/// </summary>
public sealed class FakeGatewayServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _accepted;

    private FakeGatewayServer(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The loopback port.</summary>
    public int Port { get; }

    /// <summary><c>http://127.0.0.1:&lt;port&gt;</c>.</summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>Connections accepted so far — the zero-connection proof reads this.</summary>
    public int AcceptedConnections => Volatile.Read(ref _accepted);

    /// <summary>Every request received, in arrival order.</summary>
    public ConcurrentQueue<FakeRequest> Requests { get; } = new();

    /// <summary>The routes: <c>"GET /v1/models"</c> → (status, body).</summary>
    public ConcurrentDictionary<string, (int Status, string Body)> Routes { get; } = new(StringComparer.Ordinal);

    /// <summary>How many requests hit <paramref name="methodAndPath"/> (query string excluded).</summary>
    public int Count(string methodAndPath) => Requests.Count(r => r.Key == methodAndPath);

    /// <summary>Start a server on an ephemeral loopback port.</summary>
    public static FakeGatewayServer Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new FakeGatewayServer(listener);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            Interlocked.Increment(ref _accepted);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                string head = await ReadHeadAsync(stream).ConfigureAwait(false);
                string[] lines = head.Split("\r\n");
                string[] requestLine = lines[0].Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in lines.Skip(1))
                {
                    int colon = line.IndexOf(':', StringComparison.Ordinal);
                    if (colon > 0)
                    {
                        headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                    }
                }

                string body = string.Empty;
                if (headers.TryGetValue("Content-Length", out string? lengthText) && int.TryParse(lengthText, out int length) && length > 0)
                {
                    byte[] buffer = new byte[length];
                    int read = 0;
                    while (read < length)
                    {
                        int n = await stream.ReadAsync(buffer.AsMemory(read, length - read)).ConfigureAwait(false);
                        if (n == 0)
                        {
                            break;
                        }

                        read += n;
                    }

                    body = Encoding.UTF8.GetString(buffer, 0, read);
                }

                string path = requestLine[1].Split('?')[0];
                var request = new FakeRequest(requestLine[0], path, headers, body);
                Requests.Enqueue(request);

                (int status, string responseBody) = Routes.TryGetValue(request.Key, out var route)
                    ? route
                    : (404, """{"error":"not found"}""");
                byte[] payload = Encoding.UTF8.GetBytes(responseBody);
                string responseHead =
                    $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(responseHead)).ConfigureAwait(false);
                await stream.WriteAsync(payload).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or IndexOutOfRangeException)
            {
                // A client that hung up mid-request is not the test's subject.
            }
        }
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        byte[] one = new byte[1];
        while (true)
        {
            int n = await stream.ReadAsync(one).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            bytes.Add(one[0]);
            int c = bytes.Count;
            if (c >= 4 && bytes[c - 4] == '\r' && bytes[c - 3] == '\n' && bytes[c - 2] == '\r' && bytes[c - 1] == '\n')
            {
                break;
            }
        }

        return Encoding.ASCII.GetString([.. bytes]).TrimEnd('\r', '\n');
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* stopping */ }
        _stop.Dispose();
    }
}

/// <summary>One request the fake received.</summary>
public sealed record FakeRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body)
{
    /// <summary><c>"METHOD /path"</c>.</summary>
    public string Key => $"{Method} {Path}";
}
