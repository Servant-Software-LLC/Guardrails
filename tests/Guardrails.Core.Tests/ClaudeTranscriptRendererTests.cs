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

    // ---- CRLF→LF normalization pin (safety net: crlf-normalization-unpinned) ---------------
    // CONTRACT being pinned: a stream-json log read on Windows (CRLF) and on Linux (LF) renders
    // to a BYTE-IDENTICAL, \r-free transcript. That cross-OS byte-stability is the user-facing
    // guarantee behind the CRLF-hash bug class fixed in PR #3.
    //
    // HONEST CAVEAT (verified empirically — see this change's report): removing the
    // Replace("\r\n","\n") at ClaudeTranscriptRenderer.cs ~line 49 (stream split) OR ~line 238
    // (tool_result content split) does NOT change Render's output, so these tests do NOT fail on
    // that specific line deletion. Two downstream defenses mask it: JsonDocument.Parse treats a
    // trailing '\r' on a JSONL line as whitespace, and every rendered segment is Trim()'d /
    // CollapseWhitespace'd, which strips trailing '\r'. The normalizations are therefore
    // defensive belt-and-suspenders, not the sole guarantor. These tests still pin the OBSERVABLE
    // contract (CRLF-in == LF-in, no '\r' out); a regression that DID surface '\r' (e.g. a future
    // refactor that dropped a Trim or appended raw content) would fail here. The byte-stable
    // GOLDEN-FIXTURE guarantee across OS is enforced separately by the root .gitattributes.

    [Fact]
    public void CrlfInput_RendersWithoutCarriageReturns()
    {
        // A stream whose JSON lines are separated by CRLF, with an embedded CRLF inside a
        // multi-line tool_result. The rendered transcript must be free of carriage returns.
        const string crlfStream =
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"hello\"}]}}\r\n" +
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Read\",\"input\":{\"file_path\":\"x\"}}]}}\r\n" +
            "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"a\r\nb\r\nc\"}]}}\r\n" +
            "{\"type\":\"result\",\"is_error\":false,\"result\":\"done\"}\r\n";

        string transcript = ClaudeTranscriptRenderer.Render(crlfStream);

        Assert.DoesNotContain('\r', transcript);
    }

    [Fact]
    public void CrlfAndLfInputs_ProduceByteIdenticalTranscripts()
    {
        // The same logical stream, once with LF separators and once with CRLF, must render to
        // byte-identical transcripts. This is the cross-OS stability contract: a Windows
        // checkout (CRLF) and a Linux checkout (LF) of the same stream produce the same bytes.
        const string lfStream =
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"line one\"}]}}\n" +
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"dotnet build\"}}]}}\n" +
            "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"out1\nout2\"}]}}\n" +
            "{\"type\":\"result\",\"is_error\":false,\"result\":\"all done\"}\n";

        string crlfStream = lfStream.Replace("\n", "\r\n");

        string fromLf = ClaudeTranscriptRenderer.Render(lfStream);
        string fromCrlf = ClaudeTranscriptRenderer.Render(crlfStream);

        Assert.Equal(fromLf, fromCrlf);
        Assert.DoesNotContain('\r', fromCrlf);
    }

    [Fact]
    public void TrailingCarriageReturn_OnJsonLine_DoesNotBreakParsing()
    {
        // A CRLF terminator after a complete JSON object renders the expected prose with no stray
        // '\r'. (The normalization turns "…}\r\n" into "…}\n"; JsonDocument.Parse also tolerates a
        // trailing '\r' as whitespace, so this passes with or without the Replace — it is the
        // observable contract, not a line-deletion tripwire. See the caveat above.)
        const string stream =
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"working\"}]}}\r\n";

        string transcript = ClaudeTranscriptRenderer.Render(stream);

        Assert.Equal("working\n", transcript);
        Assert.DoesNotContain('\r', transcript);
    }
}
