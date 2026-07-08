using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="GuardrailFailureReason.Tail"/> — the plan-level gate reason extractor
/// (issue #272 Part 1, the plan-level analogue of #179). A plan-level gate's reason is the ONLY signal an
/// operator gets, so it must carry the ACTUAL failure detail (the TAIL of stdout, where the #179 convention
/// re-emits it), never the FIRST line, which is a guardrail's preamble noise (npm ci / dotnet restore /
/// an echo). These pin exactly that: preamble EXCLUDED, re-emitted tail INCLUDED, plus the bounds/edge cases.
/// </summary>
public sealed class GuardrailFailureReasonTests
{
    [Fact]
    public void Tail_CarriesReEmittedFailureLines_NotJustThePreambleFirstLine()
    {
        // The #272 repro shape: a guardrail whose FIRST line is npm-ci preamble noise, whose REAL failure
        // detail is re-emitted at the END (#179 convention). The pre-#272 bug surfaced ONLY line 1 as the
        // reason; the fix carries the tail, so the actionable detail is present and the reason is NOT just
        // the preamble line. (Preamble EXCLUSION for a long output is proven by
        // Tail_CarriesAtMostTheLast15NonEmptyLines below.)
        const string preamble = "added 464 packages, and audited 465 packages in 24s";
        string output = string.Join('\n',
            preamble,
            "some middle build output",
            "=== Failure details (re-emitted so they land in the harness feedback tail) ===",
            "FAIL  dsl-tools/dfd.test.ts > round-trips the DSL",
            "vitest suite is not green at the terminal gate");

        string? reason = GuardrailFailureReason.Tail(output);

        Assert.NotNull(reason);
        Assert.Contains("vitest suite is not green at the terminal gate", reason!, StringComparison.Ordinal);
        Assert.Contains("FAIL  dsl-tools/dfd.test.ts", reason!, StringComparison.Ordinal);
        // The reason is the actionable detail, NOT the bare preamble line the pre-#272 code reported.
        Assert.NotEqual(preamble, reason);
    }

    [Fact]
    public void Tail_SingleLine_ReturnsThatLine()
    {
        Assert.Equal("greeting.txt missing 'Hello'",
            GuardrailFailureReason.Tail("greeting.txt missing 'Hello'"));
    }

    [Fact]
    public void Tail_DropsInteriorAndTrailingBlankLines()
    {
        // Blank/whitespace lines are dropped so the tail reads cleanly; the LAST non-empty line wins.
        string? reason = GuardrailFailureReason.Tail("first\n\n   \nlast\n\n");

        Assert.Equal("first\nlast", reason);
    }

    [Fact]
    public void Tail_CarriesAtMostTheLast15NonEmptyLines()
    {
        // 40 non-empty lines "line-01".."line-40"; only the last 15 (line-26..line-40) are carried.
        string output = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line-{i:00}"));

        string? reason = GuardrailFailureReason.Tail(output);

        Assert.NotNull(reason);
        Assert.Contains("line-40", reason!, StringComparison.Ordinal);
        Assert.Contains("line-26", reason!, StringComparison.Ordinal);
        Assert.DoesNotContain("line-25", reason!, StringComparison.Ordinal);
        Assert.DoesNotContain("line-01", reason!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n \t\n")]
    public void Tail_EmptyOrWhitespace_ReturnsNull(string? text)
    {
        // Null lets the caller fall through to the next source (stderr tail, then the exit code).
        Assert.Null(GuardrailFailureReason.Tail(text));
    }

    [Fact]
    public void Tail_NormalizesCrlfAndBareCr()
    {
        Assert.Equal("a\nb\nc", GuardrailFailureReason.Tail("a\r\nb\rc"));
    }
}
