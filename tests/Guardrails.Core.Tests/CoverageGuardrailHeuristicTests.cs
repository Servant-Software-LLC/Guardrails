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

    // --- polarity (issue #177): a `-match … exit 1` block is a NEGATIVE assertion (token must be
    // ABSENT), so its token is NOT a coverage token; only require-PRESENT tokens are extracted. ----

    [Fact]
    public void NegativeAssertion_MatchThenFailExit_IsNotACoverageToken()
    {
        // The #177 case: fail when CommanderRest is PRESENT ⇒ the token must be ABSENT ⇒ not coverage.
        string body =
            "$content = Get-Content $f -Raw\n" +
            "if ($content -match \"CommanderRest\") {\n" +
            "    Write-Output \"contains a CommanderRest reference — Mode C is wizard-blocked\"\n" +
            "    exit 1\n" +
            "}\n" +
            "exit 0\n";

        Assert.Empty(
            CoverageGuardrailHeuristic.ExtractCoverageTokens(body, "03-covers-key-behaviors"));
    }

    [Fact]
    public void PositiveAssertion_MultiLineNotMatchExit_IsACoverageToken()
    {
        // Preserve #157: the canonical multi-line `-notmatch … exit 1` per-term form (the catalogue's
        // actual shape, where the literal and exit are on different lines) requires the token PRESENT.
        string body =
            "$content = Get-Content $f -Raw\n" +
            "if ($content -notmatch 'ProcessId') {\n" +
            "    Write-Output \"does not test ProcessID keying\"\n" +
            "    exit 1\n" +
            "}\n" +
            "if ($content -notmatch 'RollupCount') {\n" +
            "    Write-Output \"does not test rollup counts\"\n" +
            "    exit 1\n" +
            "}\n" +
            "exit 0\n";

        Assert.Equal(new[] { "ProcessId", "RollupCount" },
            CoverageGuardrailHeuristic.ExtractCoverageTokens(body, "03-covers-key-behaviors"));
    }

    [Fact]
    public void MixedPolarity_KeepsRequirePresentTokens_ExcludesNegativeAssertion()
    {
        // Some require-present `-notmatch … exit 1` tokens alongside a `-match … exit 1` negative token.
        // Only the require-present tokens are coverage tokens; the negative assertion is excluded.
        string body =
            "$content = Get-Content $f -Raw\n" +
            "if ($content -notmatch 'XtcFileOnly') { exit 1 }\n" +
            "if ($content -notmatch 'TcApiLocal') { exit 1 }\n" +
            "if ($content -match 'CommanderRest') { Write-Output 'forbidden'; exit 1 }\n" +
            "exit 0\n";

        Assert.Equal(new[] { "XtcFileOnly", "TcApiLocal" },
            CoverageGuardrailHeuristic.ExtractCoverageTokens(body, "03-covers-key-behaviors"));
    }

    [Fact]
    public void HitsCountingForm_LastTokenNotSwallowedByThresholdExit()
    {
        // Regression guard for the polarity windowing: the trailing `if ($hits -lt N) { … exit 1 }`
        // threshold's exit must NOT be read as the last `-match … $hits++` block's decision, or the
        // last counted token would be wrongly excluded.
        string body =
            "$hits = 0\n" +
            "if ($content -match 'XtcFileOnly') { $hits++ }\n" +
            "if ($content -match 'TcApiLocal') { $hits++ }\n" +
            "if ($content -match 'CommanderRest') { $hits++ }\n" +
            "if ($hits -lt 3) { Write-Output 'missing a scenario'; exit 1 }\n" +
            "exit 0\n";

        Assert.Equal(new[] { "XtcFileOnly", "TcApiLocal", "CommanderRest" },
            CoverageGuardrailHeuristic.ExtractCoverageTokens(body, "03-covers-key-behaviors"));
    }

    [Fact]
    public void MatchWithoutHitsOrExit_IsNotConfidentlyRequirePresent_Excluded()
    {
        // A bare `-match` block that neither increments $hits nor fails the guardrail can't be
        // confidently classed require-present ⇒ excluded (conservatism). The $hits -lt threshold keeps
        // the body recognised as the archetype.
        string body =
            "$hits = 0\n" +
            "if ($content -match 'Ambiguous') { Write-Output 'noted' }\n" +
            "if ($content -match 'RealToken') { $hits++ }\n" +
            "if ($hits -lt 1) { exit 1 }\n";

        Assert.Equal(new[] { "RealToken" },
            CoverageGuardrailHeuristic.ExtractCoverageTokens(body, "03-covers-key-behaviors"));
    }
}
