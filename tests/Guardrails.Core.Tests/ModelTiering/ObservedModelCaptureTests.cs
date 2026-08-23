using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// Issue #349, first half: reading the model the Claude CLI <b>echoes</b> off its own
/// <c>stream-json</c> output. <c>AttemptProvenance.Model</c> today records the model the harness
/// <i>asked for</i> — the resolved route, or the <c>"(cli default)"</c> sentinel when nothing named one.
/// The CLI already tells us what actually <i>ran</i>: its stream opens with
/// <c>{"type":"system","subtype":"init","model":"claude-…"}</c>, and the harness already tees that stream
/// to <c>claude-stream.jsonl</c>. <c>ClaudeStreamParser.Feed</c> returns early on every line whose
/// <c>type != "result"</c>, throwing the init model away — that one discard is the entire gap.
///
/// <para><b>We parse the echo; we never force <c>--model</c> to find out.</b> Forcing one would pin the
/// zero-setup user who deliberately passes nothing, and would record the model we <i>requested</i> —
/// the weaker fact, and already what <c>AttemptProvenance.Model</c> holds.</para>
///
/// <para><b>Init WINS over a differing result-line model.</b> Without
/// <see cref="InitModel_Wins_OverADifferingResultLineModel"/>, "init, falling back to result" and
/// "result, falling back to init" are indistinguishable — and the two orderings disagree exactly when a
/// session switched models mid-run, which is the case the datum exists to expose.</para>
///
/// <para><b>TDD red (#155).</b> <c>ClaudeResult.Model</c> and <c>PromptResult.ObservedModel</c> are
/// DECLARED alongside these tests but never populated — <c>ClaudeStreamParser</c> still discards every
/// non-<c>result</c> line and <c>ClaudePromptRunner</c> does not map the member. The four behavioural
/// tests are therefore red until <c>02-implement-observed-model-capture</c> fills them in. Two are
/// deliberately not: <see cref="NoModelAnywhere_YieldsNull_NotAnEmptyString"/> (already true of an
/// unassigned member) and
/// <see cref="PromptResultObservedModel_DefaultsToNull_AndRoundTripsWhatIsAssigned"/> (the declaration
/// guard) — they are the regression half, keeping task 02 from buying the parse at the cost of the
/// absent-not-null discipline.</para>
///
/// <para><b>Idiom.</b> Synthetic JSONL fed to <c>ClaudeStreamParser.ParseAll</c>, exactly as
/// <c>ClaudeStreamParserTests</c> does. The one carry test drives the REAL
/// <see cref="ClaudePromptRunner"/> against a tiny OS-picked fake CLI (the
/// <c>ClaudePromptRunnerStreamLogTests</c> pattern — no <c>claude</c> binary is involved), because a
/// mapping asserted only on hand-constructed objects goes green against a runner that carries nothing,
/// which is precisely how <c>AttemptRecord.Usage</c> shipped structurally dead (#475). The CHILD PROCESS
/// underneath the real runner is faked; the runner never is.</para>
/// </summary>
[Trait("Category", "TierResolution")]
public sealed class ObservedModelCaptureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gr-omc-" + Guid.NewGuid().ToString("N"));

    public ObservedModelCaptureTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    /// <summary>The model echoed on the init line in every fixture below (and by the fake CLI).</summary>
    private const string EchoedModel = "claude-sonnet-5-20260101";

    /// <summary>A second, DIFFERENT model id — so "init wins" can never be satisfied by the two sources agreeing.</summary>
    private const string OtherModel = "claude-haiku-4-5-20251001";

    // --- 1. THE case: the init echo ------------------------------------------------------------

    [Fact]
    public void InitLine_Model_IsCaptured()
    {
        // The line ClaudeStreamParser.Feed discards today: its type is "system", not "result", so Feed
        // returns before reading anything — this model never reaches Build().
        ClaudeResult result = ClaudeStreamParser.ParseAll(Stream(initModel: EchoedModel, resultModel: null));

        Assert.Equal(EchoedModel, result.Model);

        // The members that already worked keep working on the same stream, so `Model` is read beside
        // them rather than at their expense.
        Assert.True(result.HasResult);
        Assert.False(result.IsError);
        Assert.Equal(0.0123m, result.CostUsd);
        Assert.Equal(4, result.NumTurns);
    }

    // --- 2. the fallback: the terminal result event -------------------------------------------

    [Fact]
    public void ResultLine_Model_IsTheFallback_WhenInitCarriedNone()
    {
        // An init event that names no model at all — an older CLI, a proxy, a different shim. The
        // terminal result event carries one, and that is the answer. Without the fallback the datum is
        // absent for a whole class of real streams, which reads identically to "nothing ran".
        ClaudeResult result = ClaudeStreamParser.ParseAll(Stream(initModel: null, resultModel: OtherModel));

        Assert.Equal(OtherModel, result.Model);

        Assert.True(result.HasResult);
        Assert.Equal(0.0123m, result.CostUsd);
        Assert.Equal(4, result.NumTurns);
    }

    // --- 3. the ORDERING between them ----------------------------------------------------------

    [Fact]
    public void InitModel_Wins_OverADifferingResultLineModel()
    {
        // Tests 1 and 2 each exercise one source with the other silent, so both pass under EITHER
        // precedence. Here both lines speak and they DISAGREE — the only shape that can tell "init,
        // falling back to result" from "result, falling back to init". The two orderings differ exactly
        // when a session switched models mid-run; the opening echo is the model the session was created
        // on, and that is the one this contract records.
        ClaudeResult result = ClaudeStreamParser.ParseAll(
            Stream(initModel: EchoedModel, resultModel: OtherModel));

        Assert.Equal(EchoedModel, result.Model);
        Assert.NotEqual(OtherModel, result.Model);
    }

    // --- 4. absent everywhere => null, never "" (regression half: GREEN from the start) --------

    [Fact]
    public void NoModelAnywhere_YieldsNull_NotAnEmptyString()
    {
        // The absent-not-null discipline. `""` would read as "the runner reported a model and it was
        // blank" — a claim about the attempt — where null is the truthful "the runner echoed none".
        // Green today (nothing populates the member) and it must STAY green: a parse that defaults the
        // field to string.Empty satisfies every test above while silently manufacturing that claim on
        // every stream that echoed nothing.
        ClaudeResult result = ClaudeStreamParser.ParseAll(Stream(initModel: null, resultModel: null));

        // Asserted FIRST: the stream really was read, so the null below is absence and not the silence
        // of a fixture the parser dropped whole.
        Assert.True(result.HasResult);
        Assert.Equal(4, result.NumTurns);

        Assert.Null(result.Model);
    }

    // --- 5. the carry onto PromptResult, at the type level (regression half: GREEN) ------------

    [Fact]
    public void PromptResultObservedModel_DefaultsToNull_AndRoundTripsWhatIsAssigned()
    {
        // Green on the declaration alone — deliberately. It pins the property of the SHAPE the
        // runner-level test below cannot distinguish from a lucky coincidence: the member defaults to
        // absent, so a script attempt or a runner that echoes nothing carries no observed model at all
        // rather than an empty one.
        PromptResult silent = Result();
        Assert.Null(silent.ObservedModel);

        PromptResult carried = Result() with { ObservedModel = EchoedModel };

        Assert.Equal(EchoedModel, carried.ObservedModel);

        // Carried verbatim, and beside the neighbouring facts rather than instead of one of them.
        Assert.Equal(0.0123m, carried.CostUsd);
        Assert.Equal(4, carried.NumTurns);
    }

    // --- 6. the carry onto PromptResult, through the REAL runner ------------------------------

    [Fact]
    public async Task ClaudePromptRunner_CarriesTheObservedModel_OffARealStream()
    {
        // Drives the REAL runner over a fake CLI that emits an init line carrying the model and a
        // terminal result line carrying none — the ClaudePromptRunnerStreamLogTests pattern, no `claude`
        // binary anywhere. The model deliberately appears on the init line ONLY, so this exercises the
        // whole chain (init event -> ClaudeResult.Model -> PromptResult.ObservedModel) across a real
        // child process, not merely the last hop.
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

        // The model in the stream the fake CLI emitted — a straight carry, with no substitution of the
        // model the harness asked for (which here is nothing at all).
        Assert.Equal(EchoedModel, result.ObservedModel);

        // The neighbouring carries still land, so ObservedModel was added beside them.
        Assert.Equal(0.0123m, result.CostUsd);
        Assert.Equal(4, result.NumTurns);
    }

    // --- fixtures -----------------------------------------------------------------------------

    // The axis under test is WHICH LINE carries the model, so the fixtures are built from one shape
    // parameterised on exactly that: `model` is omitted entirely when null (absent, not `"model":null`
    // and not `"model":""` — an absent key is what a real stream that names no model looks like).
    // Escaped rather than raw string literals because these lines are full of braces and quotes, and the
    // parser is line-oriented: each event must stay on ONE line.

    private const string AssistantLine =
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"working\"}]}}";

    private static string InitLine(string? model) =>
        "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"omc\",\"cwd\":\"/w\"" +
        (model is null ? string.Empty : ",\"model\":\"" + model + "\"") +
        ",\"permissionMode\":\"default\",\"tools\":[]}";

    private static string ResultLine(string? model) =>
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"observed-model-ok\"," +
        "\"total_cost_usd\":0.0123,\"num_turns\":4" +
        (model is null ? string.Empty : ",\"model\":\"" + model + "\"") + "}";

    /// <summary>A three-event stream — init, one assistant message, terminal result — with the model on
    /// whichever of the two bookends the caller names (or neither).</summary>
    private static string Stream(string? initModel, string? resultModel) =>
        string.Join("\n", InitLine(initModel), AssistantLine, ResultLine(resultModel));

    private static PromptResult Result() => new()
    {
        Completed = true,
        IsError = false,
        ResultText = "observed-model-ok",
        CostUsd = 0.0123m,
        NumTurns = 4,
        Summary = "claude completed"
    };

    /// <summary>
    /// A minimal OS-picked fake CLI that drains stdin and echoes the same init + result pair the parse
    /// fixtures use — enough for <see cref="ClaudePromptRunner"/> to see a real two-event stream and
    /// parse a terminal result. Emitted through <c>[Console]::Out.WriteLine</c> / <c>printf</c> rather
    /// than <c>Write-Output</c>: each event is one long line and PowerShell's object formatter is free to
    /// wrap it, which would split the JSON across two lines and fail the test for a reason that has
    /// nothing to do with the model.
    /// </summary>
    private string WriteFakeCli()
    {
        string initLine = InitLine(EchoedModel);
        string resultLine = ResultLine(null);

        if (OperatingSystem.IsWindows())
        {
            string ps1Path = Path.Combine(_root, "fake.ps1");
            string cmdPath = Path.Combine(_root, "fake.cmd");
            File.WriteAllText(ps1Path,
                "$null = [Console]::In.ReadToEnd()\r\n" +
                "[Console]::Out.WriteLine('" + initLine + "')\r\n" +
                "[Console]::Out.WriteLine('" + resultLine + "')\r\n");
            File.WriteAllText(cmdPath,
                $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1Path}\"\r\n");
            return cmdPath;
        }

        string shPath = Path.Combine(_root, "fake.sh");
        File.WriteAllText(shPath,
            "#!/usr/bin/env bash\nwhile IFS= read -r _drain; do :; done\n" +
            "printf '%s\\n' '" + initLine + "' '" + resultLine + "'\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(shPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return shPath;
    }
}
