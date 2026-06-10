using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

public sealed class ClaudeStreamParserTests
{
    [Fact]
    public void SuccessTranscript_CapturesResultCostAndTurns()
    {
        const string stream =
            """
            {"type":"system","subtype":"init","session_id":"abc"}
            {"type":"assistant","message":{"content":[{"type":"text","text":"working"}]}}
            {"type":"result","subtype":"success","is_error":false,"result":"Done: wrote out/greeting.txt","total_cost_usd":0.0123,"num_turns":4}
            """;

        ClaudeResult result = ClaudeStreamParser.ParseAll(stream);

        Assert.True(result.HasResult);
        Assert.False(result.IsError);
        Assert.Equal("Done: wrote out/greeting.txt", result.ResultText);
        Assert.Equal(0.0123m, result.CostUsd);
        Assert.Equal(4, result.NumTurns);
    }

    [Fact]
    public void IsErrorResult_IsCaptured()
    {
        const string stream =
            """
            {"type":"assistant","message":{"content":[]}}
            {"type":"result","subtype":"error_max_turns","is_error":true,"result":"hit max turns","total_cost_usd":0.5,"num_turns":25}
            """;

        ClaudeResult result = ClaudeStreamParser.ParseAll(stream);

        Assert.True(result.HasResult);
        Assert.True(result.IsError);
        Assert.Equal("hit max turns", result.ResultText);
        Assert.Equal(0.5m, result.CostUsd);
    }

    [Fact]
    public void GarbageLinesInterleaved_AreSkipped_ResultStillParsed()
    {
        const string stream =
            """
            not json at all
            {"type":"assistant"
            {"partial":
            {"type":"result","is_error":false,"result":"ok","total_cost_usd":0.01,"num_turns":1}
            <html>error page</html>
            """;

        ClaudeResult result = ClaudeStreamParser.ParseAll(stream);

        Assert.True(result.HasResult);
        Assert.False(result.IsError);
        Assert.Equal("ok", result.ResultText);
        Assert.Equal(0.01m, result.CostUsd);
    }

    [Fact]
    public void NoTerminalResult_HasResultFalse()
    {
        const string stream =
            """
            {"type":"system","subtype":"init"}
            {"type":"assistant","message":{"content":[{"type":"text","text":"thinking"}]}}
            """;

        ClaudeResult result = ClaudeStreamParser.ParseAll(stream);

        Assert.False(result.HasResult);
        Assert.Null(result.CostUsd);
        Assert.Null(result.NumTurns);
    }

    [Fact]
    public void MissingCostAndTurns_AreNull_ResultStillSeen()
    {
        const string stream = """{"type":"result","is_error":false,"result":"done"}""";

        ClaudeResult result = ClaudeStreamParser.ParseAll(stream);

        Assert.True(result.HasResult);
        Assert.Equal("done", result.ResultText);
        Assert.Null(result.CostUsd);
        Assert.Null(result.NumTurns);
    }

    [Fact]
    public void MultipleResultMessages_LastOneWins()
    {
        const string stream =
            """
            {"type":"result","is_error":true,"result":"first","total_cost_usd":0.1,"num_turns":1}
            {"type":"result","is_error":false,"result":"second","total_cost_usd":0.2,"num_turns":2}
            """;

        ClaudeResult result = ClaudeStreamParser.ParseAll(stream);

        Assert.False(result.IsError);
        Assert.Equal("second", result.ResultText);
        Assert.Equal(0.2m, result.CostUsd);
        Assert.Equal(2, result.NumTurns);
    }
}
