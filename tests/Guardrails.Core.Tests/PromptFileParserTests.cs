using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

public sealed class PromptFileParserTests
{
    [Fact]
    public void NoFrontmatter_BodyIsWholeFile_FrontmatterEmpty()
    {
        const string content = "You are a verifier.\nDo the thing.\n";

        PromptParseResult result = PromptFileParser.Parse(content);

        Assert.True(result.Success);
        Assert.Equal(content, result.File!.Body);
        Assert.Null(result.File.Frontmatter.Description);
        Assert.Null(result.File.Frontmatter.Runner);
        Assert.Null(result.File.Frontmatter.MaxTurns);
        Assert.Null(result.File.Frontmatter.TimeoutSeconds);
    }

    [Fact]
    public void PresentFrontmatter_ParsesAllKeys_AndStripsFromBody()
    {
        const string content =
            "---\n" +
            "description: LLM review of the report tone\n" +
            "runner: claude\n" +
            "maxTurns: 20\n" +
            "timeoutSeconds: 900\n" +
            "---\n" +
            "\n" +
            "You are a verifier. Judge the tone.\n";

        PromptParseResult result = PromptFileParser.Parse(content);

        Assert.True(result.Success);
        PromptFrontmatter fm = result.File!.Frontmatter;
        Assert.Equal("LLM review of the report tone", fm.Description);
        Assert.Equal("claude", fm.Runner);
        Assert.Equal(20, fm.MaxTurns);
        Assert.Equal(900, fm.TimeoutSeconds);

        Assert.Equal("You are a verifier. Judge the tone.\n", result.File.Body);
        Assert.DoesNotContain("---", result.File.Body);
    }

    [Fact]
    public void PartialFrontmatter_OnlyPresentKeysSet()
    {
        const string content = "---\nmaxTurns: 10\n---\nBody here.\n";

        PromptParseResult result = PromptFileParser.Parse(content);

        Assert.True(result.Success);
        Assert.Equal(10, result.File!.Frontmatter.MaxTurns);
        Assert.Null(result.File.Frontmatter.Runner);
        Assert.Null(result.File.Frontmatter.Description);
        Assert.Equal("Body here.\n", result.File.Body);
    }

    [Fact]
    public void MalformedYamlFrontmatter_ReportsError()
    {
        // A tab-indented mapping under a key is invalid YAML.
        const string content = "---\ndescription: ok\n\tbad: : : indentation\n---\nbody\n";

        PromptParseResult result = PromptFileParser.Parse(content);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("frontmatter", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnclosedFrontmatter_ReportsError()
    {
        const string content = "---\ndescription: never closed\nstill going\n";

        PromptParseResult result = PromptFileParser.Parse(content);

        Assert.False(result.Success);
        Assert.Contains("closing", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CrlfLineEndings_FrontmatterStillParses()
    {
        const string content = "---\r\nrunner: claude\r\n---\r\nBody line.\r\n";

        PromptParseResult result = PromptFileParser.Parse(content);

        Assert.True(result.Success);
        Assert.Equal("claude", result.File!.Frontmatter.Runner);
        Assert.StartsWith("Body line.", result.File.Body);
    }

    [Fact]
    public void EmptyFrontmatterBlock_IsTolerated()
    {
        const string content = "---\n---\nJust a body.\n";

        PromptParseResult result = PromptFileParser.Parse(content);

        Assert.True(result.Success);
        Assert.Null(result.File!.Frontmatter.Runner);
        Assert.Equal("Just a body.\n", result.File.Body);
    }
}
