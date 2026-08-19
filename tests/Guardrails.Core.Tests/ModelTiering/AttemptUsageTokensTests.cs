using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// The DoR §12.4 per-attempt <c>usage</c> block (#230-lite): the TOKENS axis that lets a costless
/// local provider still show volume. <c>AttemptRecord.Usage</c> is a schema member nothing populates
/// yet and <c>11-implement-per-tier-spend</c> aggregates it, so these tests pin the two hops the
/// datum must survive before it reaches the journal — <c>ClaudeStreamParser</c> mining the terminal
/// result event, and <c>ClaudePromptRunner</c> carrying it onto <see cref="PromptResult"/>.
///
/// <para><b>The trap.</b> The obvious implementation reads <c>usage.input_tokens</c> and is wrong by
/// three orders of magnitude. On a REAL terminal result taken verbatim from this plan's own wave-1
/// run (<see cref="RealResultEvent"/>), <c>input_tokens</c> is <b>3,706</b> while the attempt actually
/// consumed <b>4,627,863</b> input tokens — the rest is <c>cache_creation_input_tokens</c> +
/// <c>cache_read_input_tokens</c>. Cache reads are cheap, not free, and they are unambiguously
/// <i>volume</i>, which is what this axis measures. A naive read understates it by ~1250x and does so
/// SILENTLY, producing a per-tier line that looks plausible and means nothing. §9.3 calls #230-lite
/// "the evidence base for whether the deferred subsystems are ever worth building", so a
/// 1250x-wrong evidence base is worse than none. Hence <see cref="ClaudeUsage.InputTokens"/> is the
/// TOTAL: <c>input_tokens + cache_creation_input_tokens + cache_read_input_tokens</c>.</para>
///
/// <para><b>TDD red (#155).</b> <c>ClaudeResult.Usage</c> and <c>PromptResult.Usage</c> are DECLARED
/// here but never populated — <c>ClaudeStreamParser.Build()</c> does not set the member and
/// <c>ClaudePromptRunner</c> does not map it. Every behavioural test below is therefore red until
/// <c>13-implement-attempt-usage-tokens</c> fills them in. Two are deliberately not:
/// <see cref="NoUsageObject_YieldsNull_RatherThanAZeroedRecord"/> (already true of an unassigned
/// member) and <see cref="PromptResultUsage_DefaultsToNull_AndPreservesBothNumbersWhenCarried"/> (the
/// declaration guard) — they are the regression half, keeping task 13 from buying the parse at the
/// cost of the absent-not-null discipline.</para>
///
/// <para><b>Idiom.</b> Synthetic JSONL fed to <c>ClaudeStreamParser.ParseAll</c>, exactly as
/// <c>ClaudeStreamParserTests</c> does. The one carry test drives the REAL
/// <see cref="ClaudePromptRunner"/> against a tiny OS-picked fake CLI (the
/// <c>ClaudePromptRunnerStreamLogTests</c> pattern — no <c>claude</c> binary is involved), because a
/// mapping asserted only on constructed objects would go green against a runner that never carries
/// anything.</para>
/// </summary>
[Trait("Category", "TierResolution")]
public sealed class AttemptUsageTokensTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gr-aut-" + Guid.NewGuid().ToString("N"));

    public AttemptUsageTokensTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    // --- 1. THE case: the cache-inclusive input total -----------------------------------------

    [Fact]
    public void RealResultEvent_InputTokens_IsTheCacheInclusiveTotal_NotTheBareInputTokensField()
    {
        ClaudeResult result = ClaudeStreamParser.ParseAll(
            """
            {"type":"system","subtype":"init","session_id":"abc"}
            {"type":"assistant","message":{"content":[{"type":"text","text":"working"}]}}
            """ + "\n" + RealResultEvent);

        Assert.NotNull(result.Usage);
        ClaudeUsage usage = result.Usage!;

        // 4,627,863 — the number the attempt actually consumed. Reading `input_tokens` alone yields
        // 3,706 and understates this by ~1250x.
        Assert.Equal(3_706 + 148_337 + 4_475_820, usage.InputTokens);

        // output_tokens verbatim. NOT plus output_tokens_details.thinking_tokens (26,670): the real
        // payload shows thinking tokens are already INSIDE output_tokens, so adding them double-counts.
        Assert.Equal(51_465, usage.OutputTokens);

        // The members that already worked keep working on the same line.
        Assert.True(result.HasResult);
        Assert.Equal(5.026434999999999m, result.CostUsd);
        Assert.Equal(55, result.NumTurns);
    }

    // --- 2. the two redundant shapes: `usage` is canonical ------------------------------------

    [Fact]
    public void ModelUsageBlock_IsIgnored_WhenTheCanonicalUsageBlockIsPresent()
    {
        // The runner emits the same numbers twice, differently cased: `usage` (snake_case, canonical,
        // whole-attempt) and `modelUsage.<model>` (camelCase, per-model). They agree in the real payload,
        // so test 1 cannot tell which one was read. Here they DISAGREE — and `usage` must win, since a
        // parser that silently prefers `modelUsage` reports only the last model on a multi-model attempt.
        const string stream =
            """
            {"type":"result","is_error":false,"result":"ok","total_cost_usd":0.5,"num_turns":3,"usage":{"input_tokens":10,"cache_creation_input_tokens":20,"cache_read_input_tokens":30,"output_tokens":5},"modelUsage":{"claude-haiku-4-5-20251001":{"inputTokens":999999,"outputTokens":888888,"cacheReadInputTokens":777777,"cacheCreationInputTokens":666666,"costUSD":0.5}}}
            """;

        ClaudeResult result = ClaudeStreamParser.ParseAll(stream);

        Assert.NotNull(result.Usage);
        Assert.Equal(60, result.Usage!.InputTokens);
        Assert.Equal(5, result.Usage!.OutputTokens);
    }

    // --- 3. partial fields: absent is zero, never a crash -------------------------------------

    [Fact]
    public void UsageWithoutCacheFields_ParsesExactlyTheFieldsPresent()
    {
        // An older runner (or a different provider's shim) reports only the two uncached counters.
        // Missing SUB-fields are zero — the block itself is present and truthful, just smaller.
        const string stream =
            """
            {"type":"result","is_error":false,"result":"ok","total_cost_usd":0.02,"num_turns":2,"usage":{"input_tokens":812,"output_tokens":1904}}
            """;

        ClaudeResult result = ClaudeStreamParser.ParseAll(stream);

        Assert.NotNull(result.Usage);
        Assert.Equal(812, result.Usage!.InputTokens);
        Assert.Equal(1904, result.Usage!.OutputTokens);
    }

    // --- 4. absent `usage` => null, not { 0, 0 } ----------------------------------------------

    [Fact]
    public void NoUsageObject_YieldsNull_RatherThanAZeroedRecord()
    {
        // §12.4's absent-not-null discipline. `AttemptUsage { 0, 0 }` is a CLAIM that nothing was
        // consumed; null is the truthful "the runner did not report". Task 11's aggregator keys its
        // tokens-only degradation on null, so a zeroed record would make a costless provider — the exact
        // case this axis exists for — report `0 tok` instead of its real volume.
        const string stream =
            """
            {"type":"assistant","message":{"content":[{"type":"text","text":"working"}]}}
            {"type":"result","subtype":"success","is_error":false,"result":"done","total_cost_usd":0.0123,"num_turns":4}
            """;

        ClaudeResult result = ClaudeStreamParser.ParseAll(stream);

        Assert.True(result.HasResult);
        Assert.Null(result.Usage);
    }

    // --- 5. the parser's last-result-wins contract extends to usage ---------------------------

    [Fact]
    public void MultipleResultMessages_LastResultWins_ForUsageToo()
    {
        // ClaudeStreamParser's documented contract (class doc: "the last such message wins").
        const string twoResults =
            """
            {"type":"result","is_error":true,"result":"first","total_cost_usd":0.1,"num_turns":1,"usage":{"input_tokens":10,"cache_creation_input_tokens":20,"cache_read_input_tokens":30,"output_tokens":5}}
            {"type":"result","is_error":false,"result":"second","total_cost_usd":0.2,"num_turns":2,"usage":{"input_tokens":100,"cache_creation_input_tokens":200,"cache_read_input_tokens":300,"output_tokens":50}}
            """;

        ClaudeResult latest = ClaudeStreamParser.ParseAll(twoResults);
        Assert.NotNull(latest.Usage);
        Assert.Equal(600, latest.Usage!.InputTokens);
        Assert.Equal(50, latest.Usage!.OutputTokens);

        // ...and the OTHER half of that contract, which `_costUsd`/`_numTurns` already implement as
        // `?? _existing`: a later result that reports NO usage does not erase what an earlier one
        // reported. Overwriting with null here would drop a real measurement on the floor.
        const string secondResultHasNoUsage =
            """
            {"type":"result","is_error":true,"result":"first","total_cost_usd":0.1,"num_turns":1,"usage":{"input_tokens":10,"cache_creation_input_tokens":20,"cache_read_input_tokens":30,"output_tokens":5}}
            {"type":"result","is_error":false,"result":"second","total_cost_usd":0.2,"num_turns":2}
            """;

        ClaudeResult retained = ClaudeStreamParser.ParseAll(secondResultHasNoUsage);
        Assert.NotNull(retained.Usage);
        Assert.Equal(60, retained.Usage!.InputTokens);
        Assert.Equal(5, retained.Usage!.OutputTokens);
    }

    // --- 6. untrusted input: malformed usage must never throw ---------------------------------

    [Theory]
    [InlineData("\"4627863 tokens\"", "a string")]
    [InlineData("1234567", "a bare number")]
    [InlineData("[3706, 51465]", "an array")]
    [InlineData(
        "{\"input_tokens\":\"3706\",\"cache_creation_input_tokens\":\"148337\"," +
        "\"cache_read_input_tokens\":\"4475820\",\"output_tokens\":\"51465\"}",
        "an object whose token fields are all non-numeric")]
    public void MalformedUsage_LeavesUsageNull_WithoutDisturbingCostOrTurnsOnTheSameLine(
        string usageJson, string shape)
    {
        // The parser is fed UNTRUSTED runner output on every attempt (a CLI upgrade, a proxy, a
        // different provider's shim). A throw here fails an otherwise-successful task, and a partially
        // abandoned line would lose cost and turns as collateral. Null, not { 0, 0 }: nothing numeric
        // was reported, so nothing is claimed.
        const string head =
            """{"type":"result","subtype":"success","is_error":false,"result":"ok","total_cost_usd":0.25,"num_turns":7,"usage":""";

        ClaudeResult result = ClaudeStreamParser.ParseAll(head + usageJson + "}");

        Assert.True(result.HasResult, $"the result line carrying {shape} as `usage` was dropped entirely");
        Assert.Null(result.Usage);
        Assert.Equal(0.25m, result.CostUsd);
        Assert.Equal(7, result.NumTurns);
    }

    // --- 7. the carry onto PromptResult, at the type level ------------------------------------

    [Fact]
    public void PromptResultUsage_DefaultsToNull_AndPreservesBothNumbersWhenCarried()
    {
        // Green on the declaration alone — deliberately. It pins the two properties of the SHAPE that
        // the runner-level test below cannot distinguish from a lucky coincidence: the member defaults
        // to absent (so a script attempt or a silent runner journals no usage key at all), and the pair
        // is carried verbatim rather than being narrowed to a single "tokens" scalar.
        PromptResult silent = Result();
        Assert.Null(silent.Usage);

        PromptResult carried = Result() with
        {
            Usage = new PromptUsage { InputTokens = 4_627_863, OutputTokens = 51_465 }
        };

        Assert.Equal(4_627_863, carried.Usage!.InputTokens);
        Assert.Equal(51_465, carried.Usage!.OutputTokens);
    }

    // --- 8. the carry onto PromptResult, through the real runner ------------------------------

    [Fact]
    public async Task ClaudePromptRunner_CarriesTheParsedUsageOntoPromptResult_Unrecomputed()
    {
        // Drives the REAL runner over a fake CLI that echoes the real result event verbatim — the
        // ClaudePromptRunnerStreamLogTests pattern, no `claude` binary anywhere. This is the hop that
        // decides whether the number ever leaves the parser; the journaling hop after it is task 13's
        // and no test in this project can observe it.
        var runner = new ClaudePromptRunner("claude", WriteFakeCli(), new ProcessRunner());

        PromptResult result = await runner.RunAsync(
            new PromptInvocation
            {
                ComposedPrompt = "do the thing\n",
                WorkingDirectory = _root,
                PlanDirectory = _root,
                Environment = new Dictionary<string, string>(StringComparer.Ordinal),
                Settings = new PromptRunnerSettings { MaxTurns = 5 },
                Timeout = TimeSpan.FromSeconds(60),
                StreamLogPath = Path.Combine(_root, "logs", "claude-stream.jsonl"),
                TranscriptLogPath = null
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Completed, result.Summary);
        Assert.NotNull(result.Usage);

        // The SAME numbers the parser produced — a straight carry, no recomputation and no defaulting.
        Assert.Equal(3_706 + 148_337 + 4_475_820, result.Usage!.InputTokens);
        Assert.Equal(51_465, result.Usage!.OutputTokens);

        // The neighbouring carries still land, so `Usage` was added beside them rather than instead.
        Assert.Equal(5.026434999999999m, result.CostUsd);
        Assert.Equal(55, result.NumTurns);
    }

    // --- fixtures -----------------------------------------------------------------------------

    /// <summary>
    /// A real terminal result event, verbatim from this plan's own wave-1 run
    /// (<c>logs/2026-08-17T05-10-23Z-d2e9/.../01-author-tests-candidate-selection/attempt-1/claude-stream.jsonl</c>),
    /// with only the <c>result</c> text shortened. Both redundant shapes are present, as they are in
    /// reality. Single-line by necessity: the parser is line-oriented.
    /// </summary>
    private const string RealResultEvent =
        """{"type":"result","subtype":"success","is_error":false,"result":"usage-carry-ok","total_cost_usd":5.026434999999999,"num_turns":55,"usage":{"input_tokens":3706,"cache_creation_input_tokens":148337,"cache_read_input_tokens":4475820,"output_tokens":51465,"output_tokens_details":{"thinking_tokens":26670},"service_tier":"standard"},"modelUsage":{"claude-opus-5[1m]":{"inputTokens":3706,"outputTokens":51465,"cacheReadInputTokens":4475820,"cacheCreationInputTokens":148337,"costUSD":5.026434999999999}}}""";

    private static PromptResult Result() => new()
    {
        Completed = true,
        IsError = false,
        ResultText = "done",
        CostUsd = 5.026434999999999m,
        NumTurns = 55,
        Summary = "claude completed"
    };

    /// <summary>
    /// A minimal OS-picked fake CLI that drains stdin and echoes <see cref="RealResultEvent"/> — enough
    /// for <see cref="ClaudePromptRunner"/> to parse a terminal result. Emitted through
    /// <c>[Console]::Out.WriteLine</c> / <c>printf</c> rather than <c>Write-Output</c>: the event is one
    /// long line and PowerShell's object formatter is free to wrap it, which would split the JSON across
    /// two lines and make this test fail for a reason that has nothing to do with usage.
    /// </summary>
    private string WriteFakeCli()
    {
        if (OperatingSystem.IsWindows())
        {
            string ps1Path = Path.Combine(_root, "fake.ps1");
            string cmdPath = Path.Combine(_root, "fake.cmd");
            File.WriteAllText(ps1Path,
                "$null = [Console]::In.ReadToEnd()\r\n[Console]::Out.WriteLine('" + RealResultEvent + "')\r\n");
            File.WriteAllText(cmdPath,
                $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1Path}\"\r\n");
            return cmdPath;
        }

        string shPath = Path.Combine(_root, "fake.sh");
        File.WriteAllText(shPath,
            "#!/usr/bin/env bash\nwhile IFS= read -r _drain; do :; done\nprintf '%s\\n' '" + RealResultEvent + "'\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(shPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return shPath;
    }
}
