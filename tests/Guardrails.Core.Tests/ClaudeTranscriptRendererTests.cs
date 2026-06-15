using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

public sealed class ClaudeTranscriptRendererTests
{
    [Fact]
    public void GoldenStream_RendersCliEquivalentTranscript()
    {
        // A representative stream: init + thinking-token telemetry + assistant prose + tool_use,
        // a tool_result, an error tool_result, and the terminal result. The transcript must keep
        // only the CLI-equivalent signal and be byte-identical for this input.
        const string stream =
            """
            {"type":"system","subtype":"init","session_id":"abc","tools":["Read","Edit"]}
            {"type":"rate_limit_event","rate_limit_info":{"status":"allowed"}}
            {"type":"system","subtype":"thinking_tokens","estimated_tokens":47}
            {"type":"assistant","message":{"content":[{"type":"thinking","thinking":"plan it out"},{"type":"text","text":"I'll read the wizard state first."}]}}
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Glob","input":{"pattern":"src/**/*.cs"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","content":"a.cs\nb.cs\nc.cs"}]}}
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"WizardState.cs"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","content":[{"type":"text","text":"line one only"}]}]}}
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"dotnet build","description":"build it"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":"error CS5001: no Main\nmore detail"}]}}
            {"type":"result","subtype":"success","is_error":false,"result":"Build succeeded; wrote OnPremStep.cs.","total_cost_usd":1.31,"num_turns":35,"usage":{"input_tokens":24}}
            """;

        string transcript = ClaudeTranscriptRenderer.Render(stream);

        const string expected =
            "I'll read the wizard state first.\n" +
            "\n" +
            "● Glob(pattern: src/**/*.cs)\n" +
            "  ⎿ a.cs … (+2 more lines)\n" +
            "● Read(file_path: WizardState.cs)\n" +
            "  ⎿ line one only\n" +
            "● Bash(command: dotnet build, description: build it)\n" +
            "  ⎿ Error: error CS5001: no Main … (+1 more line)\n" +
            "\n" +
            "⏺ Build succeeded; wrote OnPremStep.cs.\n";

        Assert.Equal(expected, transcript);
    }

    [Fact]
    public void Telemetry_AndThinking_AreDropped()
    {
        const string stream =
            """
            {"type":"system","subtype":"init"}
            {"type":"system","subtype":"thinking_tokens","estimated_tokens":12}
            {"type":"assistant","message":{"content":[{"type":"thinking","thinking":"secret reasoning"}]}}
            {"type":"rate_limit_event","rate_limit_info":{}}
            """;

        string transcript = ClaudeTranscriptRenderer.Render(stream);

        Assert.DoesNotContain("secret reasoning", transcript);
        Assert.DoesNotContain("thinking_tokens", transcript);
        Assert.DoesNotContain("rate_limit", transcript);
        Assert.Equal(string.Empty, transcript);
    }

    [Fact]
    public void GarbageLines_AreSkipped()
    {
        const string stream =
            """
            not json
            {"type":"assistant","message":{"content":[{"type":"text","text":"hello"}]}}
            {"partial":
            """;

        string transcript = ClaudeTranscriptRenderer.Render(stream);

        Assert.Equal("hello\n", transcript);
    }

    [Fact]
    public void LongToolArgValue_IsTruncated()
    {
        string longContent = new string('x', 500);
        string stream =
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Write\"," +
            "\"input\":{\"file_path\":\"a.cs\",\"content\":\"" + longContent + "\"}}]}}";

        string transcript = ClaudeTranscriptRenderer.Render(stream);

        Assert.Contains("● Write(file_path: a.cs, content: xxx", transcript);
        Assert.Contains("…", transcript);
        Assert.DoesNotContain(longContent, transcript);
    }

    [Fact]
    public void ComplexToolArgs_AreElided()
    {
        const string stream =
            """
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"MultiEdit","input":{"file_path":"a.cs","edits":[{"old":"x","new":"y"}]}}]}}
            """;

        string transcript = ClaudeTranscriptRenderer.Render(stream);

        Assert.Equal("● MultiEdit(file_path: a.cs, edits: […])\n", transcript);
    }

    [Fact]
    public void EmptyToolResult_ShowsNoOutput()
    {
        const string stream =
            """
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"true"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","content":""}]}}
            """;

        string transcript = ClaudeTranscriptRenderer.Render(stream);

        Assert.Contains("  ⎿ (no output)", transcript);
    }

    [Fact]
    public void NoResultMessage_StillRendersConversation()
    {
        const string stream =
            """
            {"type":"assistant","message":{"content":[{"type":"text","text":"working"}]}}
            """;

        string transcript = ClaudeTranscriptRenderer.Render(stream);

        Assert.Equal("working\n", transcript);
    }

    [Fact]
    public void Render_IsDeterministic()
    {
        const string stream =
            """
            {"type":"assistant","message":{"content":[{"type":"text","text":"hi"},{"type":"tool_use","name":"Read","input":{"file_path":"x"}}]}}
            {"type":"result","is_error":false,"result":"done"}
            """;

        Assert.Equal(ClaudeTranscriptRenderer.Render(stream), ClaudeTranscriptRenderer.Render(stream));
    }
}
