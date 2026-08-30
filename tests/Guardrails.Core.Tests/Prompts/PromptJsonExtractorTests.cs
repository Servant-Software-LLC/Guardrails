using System.Text.Json;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.Prompts;

/// <summary>
/// Pins the contract of <see cref="PromptJsonExtractor"/> (plan 28 §3.3/§6.4): the last fenced
/// <c>```json</c> block, else the last top-level JSON object; the candidate must parse or nothing
/// is extracted. All of these fail against the throwing stub until the extractor is implemented.
/// </summary>
public sealed class PromptJsonExtractorTests
{
    /// <summary>
    /// The no-regression case: what a strong instruction-following model emits today.
    /// <c>OverwatchProposal</c> and the triage sidecar parse this strictly and must not get worse.
    /// </summary>
    [Fact]
    public void BareJsonObject_ExtractedUnchanged()
    {
        const string json = """{ "pass": true, "reason": "tone is friendly" }""";

        string? extracted = PromptJsonExtractor.Extract(json);

        Assert.Equal(json, extracted);
    }

    /// <summary>The §3.3 payoff: a weaker model's prose-wrapped object is still recovered.</summary>
    [Fact]
    public void ProseAroundJsonObject_ObjectIsRecovered()
    {
        const string json = """{ "classification": "retryable", "diagnosis": "budget gap: no maxTurns declared" }""";
        string text = "Let me look at the evidence first.\n\n" + json + "\n\nThat is my full assessment.";

        string? extracted = PromptJsonExtractor.Extract(text);

        Assert.NotNull(extracted);
        using JsonDocument doc = JsonDocument.Parse(extracted);
        Assert.Equal("retryable", doc.RootElement.GetProperty("classification").GetString());
    }

    [Fact]
    public void FencedJsonBlock_WithProseBeforeAndAfter_FencedBlockWins()
    {
        const string json = """{ "pass": true, "reason": "fenced verdict" }""";
        string text = "Here is my reasoning first.\n\n```json\n" + json + "\n```\n\nHope that helps.";

        string? extracted = PromptJsonExtractor.Extract(text);

        Assert.NotNull(extracted);
        using JsonDocument doc = JsonDocument.Parse(extracted);
        Assert.Equal("fenced verdict", doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void TwoFencedBlocks_LastOneWins()
    {
        const string first = """{ "pass": false, "reason": "first draft, ignore this" }""";
        const string second = """{ "pass": true, "reason": "final answer" }""";
        string text = "Let me draft this.\n\n```json\n" + first + "\n```\n\n" +
                       "Actually, revising:\n\n```json\n" + second + "\n```";

        string? extracted = PromptJsonExtractor.Extract(text);

        Assert.NotNull(extracted);
        using JsonDocument doc = JsonDocument.Parse(extracted);
        Assert.Equal("final answer", doc.RootElement.GetProperty("reason").GetString());
    }

    /// <summary>
    /// Plan 28 §6.4 (docs/plans/28-local-inference-runner.md:578-580): "the last fenced ```json
    /// block, else the last top-level JSON object" — the fenced block is tried FIRST, as a whole
    /// category, so it wins even though the bare object appears later in the text.
    /// </summary>
    [Fact]
    public void FencedBlockAndLaterBareObject_FencedBlockWins()
    {
        const string fenced = """{ "pass": true, "reason": "the fenced verdict" }""";
        const string bare = """{ "pass": false, "reason": "a bare object mentioned afterward" }""";
        string text = "```json\n" + fenced + "\n```\n\n" +
                       "On reflection, something like " + bare + " also crossed my mind.";

        string? extracted = PromptJsonExtractor.Extract(text);

        Assert.NotNull(extracted);
        using JsonDocument doc = JsonDocument.Parse(extracted);
        Assert.Equal("the fenced verdict", doc.RootElement.GetProperty("reason").GetString());
    }

    /// <summary>Fail closed: a candidate that does not parse yields nothing — never a partial or a guess.</summary>
    [Fact]
    public void MalformedJson_NothingExtracted()
    {
        string? extracted = PromptJsonExtractor.Extract("{ this is not valid json }");

        Assert.Null(extracted);
    }

    [Fact]
    public void NoJsonAtAll_NothingExtracted()
    {
        string? extracted = PromptJsonExtractor.Extract("I read the files and everything looks fine, no issues found.");

        Assert.Null(extracted);
    }
}
