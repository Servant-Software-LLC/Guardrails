using System.Net;
using System.Text.Json;
using Guardrails.Cli.Ui;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Exercises the loopback <see cref="LogServer"/> end-to-end over a temp plan folder with
/// hand-written attempt logs: the landing page lists tasks, the per-task <c>/files</c> and
/// <c>/file</c> endpoints surface the latest attempt's log files, unknown tasks 404, and the
/// file endpoint refuses to escape the attempt directory. The server binds to localhost only.
/// </summary>
public sealed class LogServerTests
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task Landing_ListsEveryTask_WithLinks()
    {
        using var temp = new TempPlan();
        IReadOnlyList<TaskNode> tasks = [Task("01-alpha", "First task"), Task("02-beta", "Second task")];
        await using LogServer server = Start(temp.Dir, tasks);

        string html = await GetStringAsync(server.BaseUrl);

        Assert.Contains("01-alpha", html);
        Assert.Contains("02-beta", html);
        Assert.Contains("/tasks/01-alpha", html);
        Assert.Contains("Second task", html);
    }

    [Fact]
    public async Task UnknownTask_Returns404()
    {
        using var temp = new TempPlan();
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        HttpResponseMessage response = await Http.GetAsync(
            $"{server.BaseUrl}tasks/99-does-not-exist", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Files_ReportsLatestAttempt_AndPrefersClaudeStream()
    {
        using var temp = new TempPlan();
        temp.WriteLog("01-alpha", attempt: 1, "action-stdout.log", "old attempt");
        temp.WriteLog("01-alpha", attempt: 2, "action-stdout.log", "hello");
        temp.WriteLog("01-alpha", attempt: 2, "claude-stream.jsonl", "{}");
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        string json = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha/files");
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("attempt").GetInt32());
        Assert.Equal("claude-stream.jsonl", root.GetProperty("preferred").GetString());
        string[] files = root.GetProperty("files").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains("action-stdout.log", files);
        Assert.Contains("claude-stream.jsonl", files);
    }

    [Fact]
    public async Task File_ReturnsRawContent_OfLatestAttempt()
    {
        using var temp = new TempPlan();
        temp.WriteLog("01-alpha", attempt: 1, "action-stdout.log", "first attempt output");
        temp.WriteLog("01-alpha", attempt: 2, "action-stdout.log", "second attempt output");
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        string body = await GetStringAsync($"{server.BaseUrl}tasks/01-alpha/file?name=action-stdout.log");

        Assert.Equal("second attempt output", body); // latest attempt wins
    }

    [Fact]
    public async Task File_NoAttemptYet_ReturnsEmptyButOk()
    {
        using var temp = new TempPlan();
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        HttpResponseMessage response = await Http.GetAsync(
            $"{server.BaseUrl}tasks/01-alpha/file?name=action-stdout.log", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task File_PathTraversal_IsRejected()
    {
        using var temp = new TempPlan();
        temp.WriteLog("01-alpha", attempt: 1, "action-stdout.log", "safe");
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        HttpResponseMessage response = await Http.GetAsync(
            $"{server.BaseUrl}tasks/01-alpha/file?name=..%2F..%2Fstate.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UrlForTask_KnownReturnsUrl_UnknownReturnsNull()
    {
        using var temp = new TempPlan();
        await using LogServer server = Start(temp.Dir, [Task("01-alpha", "First")]);

        Assert.NotNull(server.UrlForTask("01-alpha"));
        Assert.StartsWith(server.BaseUrl, server.UrlForTask("01-alpha")!);
        Assert.Null(server.UrlForTask("nope"));
    }

    // --- helpers ----------------------------------------------------------------------------

    private static LogServer Start(string planDir, IReadOnlyList<TaskNode> tasks)
    {
        LogServer? server = LogServer.TryStart(planDir, tasks, port: 0, TextWriter.Null);
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
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "gr-logsrv-" + Guid.NewGuid().ToString("N"));

        public TempPlan() => Directory.CreateDirectory(Dir);

        public void WriteLog(string taskId, int attempt, string fileName, string content)
        {
            string attemptDir = Path.Combine(Dir, "state", "logs", taskId, $"attempt-{attempt}");
            Directory.CreateDirectory(attemptDir);
            File.WriteAllText(Path.Combine(attemptDir, fileName), content);
        }

        public void Dispose()
        {
            // UnauthorizedAccessException is NOT a subtype of IOException on .NET — catch both
            // so a locked file on Windows doesn't mask the original test failure.
            try { Directory.Delete(Dir, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }
}
