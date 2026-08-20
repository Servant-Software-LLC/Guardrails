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

    // --- case-sensitivity operator prefixes: PowerShell spells every comparison operator with an
    // optional leading `c` (case-SENSITIVE) or `i` (explicitly case-INsensitive). The authoring
    // doctrine now MANDATES `-cmatch`/`-cnotmatch` for a required-present identifier clause, so a
    // heuristic blind to the prefix would silently stop recognising EVERY correctly-authored
    // coverage guardrail — GR2026 would go quiet exactly where the doctrine is being followed. ----

    [Fact]
    public void CaseSensitivePrefix_CNotMatchPerTermForm_ExtractsTokens_ExactlyAsNotMatch()
    {
        // Byte-for-byte the CanonicalNamedPerTermForm body with the doctrine-mandated -cnotmatch.
        string body =
            "$content = Get-Content $f -Raw\n" +
            "if ($content -cnotmatch 'ProcessId') { Write-Output 'no ProcessId'; exit 1 }\n" +
            "if ($content -cnotmatch 'RollupCount') { Write-Output 'no RollupCount'; exit 1 }\n" +
            "exit 0\n";

        IReadOnlyList<string> tokens =
            CoverageGuardrailHeuristic.ExtractCoverageTokens(body, "03-covers-key-behaviors");

        Assert.Equal(new[] { "ProcessId", "RollupCount" }, tokens);
    }

    [Fact]
    public void ExplicitlyCaseInsensitivePrefix_INotMatch_IsAdmitted()
    {
        string body =
            "$content = Get-Content $f -Raw\n" +
            "if ($content -inotmatch 'ProcessId') { exit 1 }\n" +
            "if ($content -imatch 'RollupCount') { exit 1 }\n" + // -imatch … exit 1 ⇒ negative assertion
            "exit 0\n";

        // The -i prefix is admitted on BOTH spellings, and polarity is still read from the operator:
        // -inotmatch is require-present (kept), -imatch … exit 1 is a negative assertion (excluded).
        Assert.Equal(new[] { "ProcessId" },
            CoverageGuardrailHeuristic.ExtractCoverageTokens(body, "03-covers-key-behaviors"));
    }

    [Fact]
    public void CaseSensitivePrefix_CMatchCountingForm_AndCltThreshold_AreRecognised()
    {
        // The counting form under a fully case-sensitive spelling: -cmatch … $hits++ with a -clt floor.
        // The threshold regex is the gate that decides the counting form IS the archetype, so a blind
        // spot there costs every token in the guardrail, not one.
        string body =
            "$hits = 0\n" +
            "if ($content -cmatch 'XtcFileOnly') { $hits++ }\n" +
            "if ($content -cmatch 'TcApiLocal') { $hits++ }\n" +
            "if ($hits -clt 2) { Write-Output 'missing a scenario'; exit 1 }\n" +
            "exit 0\n";

        Assert.Equal(new[] { "XtcFileOnly", "TcApiLocal" },
            CoverageGuardrailHeuristic.ExtractCoverageTokens(body, "01-not-canonically-named"));
    }

    [Fact]
    public void CaseSensitivePrefix_CMatchThenFailExit_IsStillANegativeAssertion()
    {
        // Polarity must not be lost when the prefix is admitted: -cmatch … exit 1 fails on PRESENCE,
        // so the token is intentionally absent from the prompt and must NOT become a coverage token.
        string body =
            "$content = Get-Content $f -Raw\n" +
            "if ($content -cmatch 'CommanderRest') {\n" +
            "    Write-Output 'contains a CommanderRest reference'\n" +
            "    exit 1\n" +
            "}\n" +
            "exit 0\n";

        Assert.Empty(
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
