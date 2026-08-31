using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #551 — the overwatcher discarded every verdict the judge wrapped in a markdown code fence.
///
/// <para><b>The bug, and why it hid.</b> <see cref="OverwatchProposal.TryParse"/> handed the raw result
/// text to <c>JsonDocument.Parse</c>. A chat model asked for JSON answers in a <c>```json</c> block unless
/// something stops it, so the leading backticks threw <c>JsonException</c>, <c>TryParse</c> returned null,
/// and the run recorded a fixed eleven-word "not a parseable verdict". Nothing kept the body, so the
/// failure looked identical whether the judge had rambled, timed out, or returned a perfect answer.</para>
///
/// <para><b>What it actually cost.</b> On plan 28's run the diagnose fired twice on task 20, was billed
/// both times, and both bodies were complete correct verdicts. One of them had already worked out the
/// harness bug filed as #550 — it read <c>action-result.json</c>, saw <c>exitCode 0 / summary "ok"</c>, and
/// wrote "a false-positive permission-wall escalation on an attempt that actually SUCCEEDED". The harness
/// had the answer in a local variable and dropped it over three backticks.</para>
///
/// <para><b>The line these tests hold.</b> Leniency now recovers a JSON object wherever it appears in the
/// message (plan 28 §3.3, via the shared <c>PromptJsonExtractor</c>), so the narrowness bound is no longer
/// "only a fenced block parses" — it is "only a well-SHAPED verdict parses, wherever it appears".
/// <see cref="Unfenced_ProseMentioningANonVerdictObject_StaysNull"/> is the counterweight: a stray object
/// recovered from prose that does not carry the required <c>diagnosis</c> string is still not a verdict, so
/// the overwatcher cannot manufacture one out of a model thinking out loud about something else entirely.</para>
/// </summary>
public sealed class OverwatchProposalFenceTests
{
    private const string Body =
        """{"classification":"retryable","diagnosis":"budget gap: no maxTurns declared","fixes":[]}""";

    /// <summary>
    /// The red bar. This is verbatim the shape both discarded plan-28 verdicts arrived in.
    /// </summary>
    [Fact]
    public void FencedWithJsonInfoString_Parses()
    {
        OverwatchProposal? parsed = OverwatchProposal.TryParse("```json\n" + Body + "\n```");

        Assert.NotNull(parsed);
        Assert.Equal(OverwatchClassification.Retryable, parsed.Classification);
        Assert.Contains("budget gap", parsed.Diagnosis);
    }

    /// <summary>A fence with no info string is the same body wearing a different hat.</summary>
    [Fact]
    public void FencedWithoutInfoString_Parses()
    {
        Assert.NotNull(OverwatchProposal.TryParse("```\n" + Body + "\n```"));
    }

    /// <summary>
    /// Leading/trailing chatter-free whitespace and blank lines around the fence are what a real result
    /// text carries; none of it should decide whether a verdict counts.
    /// </summary>
    [Fact]
    public void FencedWithSurroundingWhitespace_Parses()
    {
        Assert.NotNull(OverwatchProposal.TryParse("\n\n  ```json\n" + Body + "\n```  \n\n"));
    }

    /// <summary>
    /// A body whose stream was cut before the closing fence arrived is still a complete JSON object, and
    /// the verdict was still paid for. Tolerating the missing closer costs nothing and recovers a real
    /// case; requiring it would discard a usable answer for a formatting detail — which is this whole bug.
    /// </summary>
    [Fact]
    public void FencedWithNoClosingFence_Parses()
    {
        Assert.NotNull(OverwatchProposal.TryParse("```json\n" + Body));
    }

    /// <summary>The path that always worked must keep working — unfenced bare JSON is still the wire shape.</summary>
    [Fact]
    public void UnfencedJson_StillParses()
    {
        Assert.NotNull(OverwatchProposal.TryParse(Body));
    }

    /// <summary>
    /// The counterweight, now that leniency recovers a bare object from prose (plan 28 §3.3):
    /// <c>TryParse</c> requires a <c>diagnosis</c> string, so a stray object recovered from prose that
    /// merely mentions unrelated JSON is still not a verdict.
    /// </summary>
    [Fact]
    public void Unfenced_ProseMentioningANonVerdictObject_StaysNull()
    {
        Assert.Null(OverwatchProposal.TryParse(
            "I checked the config, it had {\"maxTurns\": 50} in it."));
    }

    /// <summary>A fence around something that is not JSON at all is still no verdict.</summary>
    [Fact]
    public void FencedNonJson_StaysNull()
    {
        Assert.Null(OverwatchProposal.TryParse("```json\nthe task ran out of turns\n```"));
    }

    /// <summary>
    /// Blank and absent stay null — unchanged behavior, pinned here because the shared extractor now runs
    /// first and a trim-then-parse could plausibly turn one of these into an exception rather than a null.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("```json\n```")]
    public void BlankInputs_StayNull(string? text)
    {
        Assert.Null(OverwatchProposal.TryParse(text));
    }
}
