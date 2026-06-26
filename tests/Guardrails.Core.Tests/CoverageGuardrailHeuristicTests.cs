using Guardrails.Core.Loading;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit coverage for <see cref="CoverageGuardrailHeuristic.ExtractCoverageTokens"/> — the pure
/// archetype recogniser behind GR2026 (issue #157 §1). Pins the two recognised shapes (the
/// <c>$hits -lt N</c> counter form and the canonically-named per-term early-exit form), the
/// metachar-free clear-keyword filter, and the conservative "not the archetype → no tokens" path.
/// </summary>
public sealed class CoverageGuardrailHeuristicTests
{
    [Fact]
    public void HitsThresholdForm_ExtractsEveryMatchedToken()
    {
        string body =
            "$content = Get-Content $f -Raw\n" +
            "$hits = 0\n" +
            "if ($content -match \"XtcFileOnly\") { $hits++ }\n" +
            "if ($content -match \"TcApiLocal\") { $hits++ }\n" +
            "if ($content -match \"CommanderRest\") { $hits++ }\n" +
            "if ($hits -lt 3) { Write-Output 'missing'; exit 1 }\n" +
            "exit 0\n";

        IReadOnlyList<string> tokens =
            CoverageGuardrailHeuristic.ExtractCoverageTokens(body, "03-covers-key-behaviors");

        Assert.Equal(new[] { "XtcFileOnly", "TcApiLocal", "CommanderRest" }, tokens);
    }

    [Fact]
    public void CanonicalNamedPerTermForm_ExtractsTokens_WithoutHitsThreshold()
    {
        // The catalogue/dotnet realization: one `-notmatch ... exit 1` per term, no $hits counter.
        string body =
            "$content = Get-Content $f -Raw\n" +
            "if ($content -notmatch 'ProcessId') { Write-Output 'no ProcessId'; exit 1 }\n" +
            "if ($content -notmatch 'RollupCount') { Write-Output 'no RollupCount'; exit 1 }\n" +
            "exit 0\n";

        IReadOnlyList<string> tokens =
            CoverageGuardrailHeuristic.ExtractCoverageTokens(body, "03-covers-key-behaviors");

        Assert.Equal(new[] { "ProcessId", "RollupCount" }, tokens);
    }

    [Fact]
    public void PerTermForm_WithoutCanonicalName_AndNoHitsThreshold_IsNotTheArchetype()
    {
        // No $hits threshold and a non-canonical name ⇒ not confidently the archetype ⇒ no tokens.
        string body =
            "$content = Get-Content $f -Raw\n" +
            "if ($content -notmatch 'ProcessId') { exit 1 }\n" +
            "exit 0\n";

        Assert.Empty(CoverageGuardrailHeuristic.ExtractCoverageTokens(body, "01-some-other-check"));
    }

    [Fact]
    public void SkipsRegexMetacharLiterals_KeepsClearKeywords()
    {
        // A literal carrying regex syntax is not a plain keyword we can confidently keyword-match.
        string body =
            "$hits = 0\n" +
            "if ($content -match \"^public\\s+class\") { $hits++ }\n" +   // metachars → skipped
            "if ($content -match \"CommanderRest\") { $hits++ }\n" +       // clear keyword → kept
            "if ($hits -lt 2) { exit 1 }\n";

        IReadOnlyList<string> tokens =
            CoverageGuardrailHeuristic.ExtractCoverageTokens(body, "03-covers-key-behaviors");

        Assert.Equal(new[] { "CommanderRest" }, tokens);
    }

    [Fact]
    public void DeduplicatesTokens_CaseInsensitive_FirstSeenOrder()
    {
        string body =
            "$hits = 0\n" +
            "if ($content -match 'ProcessId') { $hits++ }\n" +
            "if ($content -match 'ProcessId') { $hits++ }\n" +
            "if ($hits -lt 1) { exit 1 }\n";

        Assert.Equal(new[] { "ProcessId" },
            CoverageGuardrailHeuristic.ExtractCoverageTokens(body, "03-covers-key-behaviors"));
    }

    [Fact]
    public void IgnoresMatchAgainstUnrelatedVariables()
    {
        // The archetype scans $content/$tn/$code/$text/$file. A match against some other variable is
        // not a coverage token (it is, e.g., a build-output scan), so it is not extracted.
        string body =
            "$hits = 0\n" +
            "if ($whatever -match 'NotAToken') { $hits++ }\n" +
            "if ($content -match 'RealToken') { $hits++ }\n" +
            "if ($hits -lt 2) { exit 1 }\n";

        Assert.Equal(new[] { "RealToken" },
            CoverageGuardrailHeuristic.ExtractCoverageTokens(body, "03-covers-key-behaviors"));
    }

    [Fact]
    public void NonCoverageScript_YieldsNoTokens()
    {
        string body = "dotnet build\nif ($LASTEXITCODE -ne 0) { exit 1 }\nexit 0\n";
        Assert.Empty(CoverageGuardrailHeuristic.ExtractCoverageTokens(body, "01-build-passes"));
    }
}
