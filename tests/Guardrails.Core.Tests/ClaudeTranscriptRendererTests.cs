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
    public void Truncate_DoesNotSplitSurrogatePair_AtMaxBoundary()
    {
        // Regression: transcript-truncate-splits-surrogates.
        // MaxArgValueChars == 80. Pad with 79 ASCII chars so the astral-plane emoji "😀"
        // (U+1F600, a UTF-16 surrogate PAIR occupying two code units) sits at indices 79..80 —
        // straddling the truncation boundary. A naive value[..80] keeps index 79 (the lone HIGH
        // surrogate) and drops index 80 (the low surrogate); that orphan renders as U+FFFD '�'.
        const string emoji = "😀"; // U+1F600 😀
        string value = new string('x', 79) + emoji + new string('y', 50);
        string stream =
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Write\"," +
            "\"input\":{\"content\":\"" + value + "\"}}]}}";

        string transcript = ClaudeTranscriptRenderer.Render(stream);

        // No replacement char must appear, and no rendered line may end on a lone surrogate.
        Assert.DoesNotContain('�', transcript);
        foreach (string ch in transcript.Select(c => c.ToString()))
        {
            Assert.False(char.IsHighSurrogate(ch[0]) && ch.Length == 1, "stray high surrogate");
        }

        foreach (string outLine in transcript.Split('\n'))
        {
            if (outLine.Length > 0)
            {
                Assert.False(char.IsHighSurrogate(outLine[^1]), "line ends on a lone high surrogate");
            }
        }

        // The fix backs the cut off by one, so the orphaned high surrogate is dropped entirely
        // (the pair is not kept because its low half is past the cap) and the ellipsis follows.
        Assert.Contains("content: " + new string('x', 79) + "…", transcript);
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

    // ---- StreamingWriter: incremental rendering (issue #41) --------------------------------
    // CONTRACT: feeding the stream line-by-line through StreamingWriter and calling Complete()
    // produces output BYTE-IDENTICAL to a batch Render() over the same stream — while writing
    // the transcript to disk as each line arrives (so "view log" can tail it live).

    [Fact]
    public void StreamingWriter_LineByLine_IsByteIdenticalToBatchRender()
    {
        const string stream =
            """
            {"type":"system","subtype":"init","session_id":"abc"}
            {"type":"assistant","message":{"content":[{"type":"thinking","thinking":"plan"},{"type":"text","text":"I'll read the wizard state first."}]}}
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Glob","input":{"pattern":"src/**/*.cs"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","content":"a.cs\nb.cs\nc.cs"}]}}
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"dotnet build","description":"build it"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":"error CS5001: no Main\nmore detail"}]}}
            {"type":"result","subtype":"success","is_error":false,"result":"Build succeeded.","total_cost_usd":1.31,"num_turns":35}
            """;

        string batch = ClaudeTranscriptRenderer.Render(stream);

        var buffer = new StringWriter();
        var streaming = new ClaudeTranscriptRenderer.StreamingWriter(buffer);
        foreach (string line in stream.Split('\n'))
        {
            streaming.Feed(line);
        }

        streaming.Complete();

        Assert.Equal(batch, buffer.ToString());
    }

    [Fact]
    public void StreamingWriter_FullCoverage_IsByteIdenticalToBatch()
    {
        // The load-bearing byte-identity test: a stream that exercises EVERY path the streaming
        // newline-carry and garbage-skip have to get right, all in one feed sequence:
        //   - an assistant/text block (RenderAssistant appends "\n\n") immediately followed by the
        //     terminal `result` line (RenderResult prepends "\n"), so the bytes before the result
        //     bullet are a run of THREE newlines ("...prose\n\n" + "\n⏺...") that Normalize/
        //     EmitClamped must collapse to a single blank line. In the streaming path that 3-run is
        //     carried ACROSS feeds: the text feed leaves two pending newlines, the result feed adds
        //     a third, and the clamp to 2 fires only when the bullet char is finally written — the
        //     exact cross-feed carry.
        //   - a text value with an EMBEDDED newline (multi-line prose) rendered verbatim;
        //   - an interleaved GARBAGE / non-JSON line that must be skipped (per-line independence:
        //     it must not poison the valid line that follows it);
        //   - a tool_use and a tool_result.
        const string stream =
            """
            {"type":"assistant","message":{"content":[{"type":"text","text":"first block of prose"}]}}
            this is not json — it must be skipped, not glued onto the next line
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"x.cs"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","content":"out1\nout2\nout3"}]}}
            {"type":"assistant","message":{"content":[{"type":"text","text":"second block\nwith an embedded newline"}]}}
            {"type":"result","subtype":"success","is_error":false,"result":"all done","total_cost_usd":0.42,"num_turns":7}
            """;

        string batch = ClaudeTranscriptRenderer.Render(stream);

        // Guard the guard #1 — the 3+→blank-line collapse genuinely fired. Pre-collapse, the bytes
        // before "⏺" were the text block's trailing "\n\n" then RenderResult's leading "\n" = a run
        // of THREE '\n'. After collapse the output shows exactly ONE blank line ("...newline\n\n⏺"),
        // and — the discriminating check — NO run of 3+ newlines survives anywhere. If a refactor
        // stopped collapsing, that 3+ run would reappear and BOTH of these would fail.
        Assert.Contains("with an embedded newline\n\n⏺ all done\n", batch);
        Assert.DoesNotContain("\n\n\n", batch);
        // Guard the guard #2 — the embedded newline survived as multi-line prose (a 2-newline run is
        // legal and must be preserved; only 3+ collapse).
        Assert.Contains("second block\nwith an embedded newline", batch);
        // Guard the guard #3 — the garbage line left no trace (per-line independence held).
        Assert.DoesNotContain("not json", batch);

        var buffer = new StringWriter();
        var streaming = new ClaudeTranscriptRenderer.StreamingWriter(buffer);
        foreach (string line in stream.Split('\n'))
        {
            streaming.Feed(line);
        }

        streaming.Complete();

        Assert.Equal(batch, buffer.ToString());
    }

    [Fact]
    public void StreamingWriter_PartialJsonLine_IsSkipped_LikeBatch()
    {
        // A line that is NOT a complete JSON object on its own must be skipped — exactly as batch
        // Render skips it — and must not poison the valid lines around it. (This replaces the old
        // "object split across feeds" test: AsyncStreamReader delivers complete lines, so the
        // streaming writer no longer buffers partial objects; per-line independence is the contract.)
        const string stream =
            """
            {"type":"assistant","message":{"content":[{"type":"text","text":"before"}]}}
            {"type":"assistant","message":{"content":[{"type":"text",
            {"type":"assistant","message":{"content":[{"type":"text","text":"after"}]}}
            """;

        string batch = ClaudeTranscriptRenderer.Render(stream);

        var buffer = new StringWriter();
        var streaming = new ClaudeTranscriptRenderer.StreamingWriter(buffer);
        foreach (string line in stream.Split('\n'))
        {
            streaming.Feed(line);
        }

        streaming.Complete();

        // The partial middle line is dropped; the valid lines before and after both render.
        Assert.Equal(batch, buffer.ToString());
        Assert.Equal("before\n\nafter\n", buffer.ToString());
    }

    [Fact]
    public void StreamingWriter_NoContent_WritesNothing()
    {
        // Only telemetry fed → no transcript content → no trailing newline (matches Render == "").
        var buffer = new StringWriter();
        var streaming = new ClaudeTranscriptRenderer.StreamingWriter(buffer);

        streaming.Feed("{\"type\":\"system\",\"subtype\":\"init\"}");
        streaming.Feed("{\"type\":\"rate_limit_event\",\"rate_limit_info\":{}}");
        streaming.Complete();

        Assert.Equal(string.Empty, buffer.ToString());
    }

    [Fact]
    public void StreamingWriter_CompleteWithoutFeeds_WritesNothing()
    {
        // Complete() with zero feeds must write nothing (no spurious trailing newline) — matches
        // Render("") == "".
        var buffer = new StringWriter();
        var streaming = new ClaudeTranscriptRenderer.StreamingWriter(buffer);

        streaming.Complete();

        Assert.Equal(string.Empty, buffer.ToString());
    }
}
