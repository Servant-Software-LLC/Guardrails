using Guardrails.Core.Loading;

namespace Guardrails.Core.Tests;

/// <summary>
/// The check-set provenance mechanism (issue #564, SSOT §16). The defect it pins: <c>validate</c>
/// reported CLEAN while silently skipping every check the running binary predated — the same plan,
/// the same command, the same exit code, <b>4 findings or 0 depending on which binary was on
/// PATH</b>, with nothing said either way.
///
/// <para>Every assertion here is <b>two-sided</b> on purpose. A mechanism that warns is worth
/// nothing unless it also stays quiet when the binary and the tree agree — a check that always
/// fires teaches a reviewer to skim it, which is the muting failure (#229) and would leave the
/// original silence in place behind a wall of noise.</para>
/// </summary>
public sealed class CheckSetProbeTests
{
    /// <summary>The version string is injected everywhere; nothing here reads a build-stamped attribute.</summary>
    private const string Version = "1.12.0";

    /// <summary>
    /// A minimal but REAL-SHAPED <c>DiagnosticCodes.cs</c> declaring exactly <paramref name="codes"/>.
    /// Each declaration is preceded by a doc comment that MENTIONS the same code in prose, so every
    /// test using this fixture also re-proves that a mention is not a declaration.
    /// </summary>
    private static string SourceDeclaring(params string[] codes)
    {
        var text = new System.Text.StringBuilder()
            .AppendLine("namespace Guardrails.Core.Loading;")
            .AppendLine()
            .AppendLine("public static class DiagnosticCodes")
            .AppendLine("{");

        foreach (string code in codes)
        {
            text.AppendLine($"    /// <summary>Doc comment mentioning {code} in prose.</summary>")
                .AppendLine($"    public const string Name{code} = \"{code}\";")
                .AppendLine();
        }

        return text.AppendLine("}").ToString();
    }

    // --- The running binary's census ---------------------------------------------------

    [Fact]
    public void ImplementedCodes_AreReflectedFromTheAssembly_SortedAndComplete()
    {
        IReadOnlyList<string> codes = CheckSetProbe.ImplementedCodes;

        // Reflected, not hand-listed: a newly authored code must join the census with no second
        // edit to forget, because forgetting it would reintroduce the silence being fixed.
        Assert.Contains(DiagnosticCodes.MissingFile, codes);              // GR1001, the first
        Assert.Contains(DiagnosticCodes.CheckSetPredatesSourceTree, codes); // GR2072, this change's own
        Assert.Equal([.. codes.OrderBy(c => c, StringComparer.Ordinal)], codes);
        Assert.Equal(codes.Distinct(StringComparer.Ordinal).Count(), codes.Count);
    }

    [Fact]
    public void ReadCodes_TakesOnlyGrStringConstants()
    {
        IReadOnlyList<string> codes = CheckSetProbe.ReadCodes(typeof(NotAllConstantsAreCodes));

        Assert.Equal(["GR9001"], codes);
    }

    private static class NotAllConstantsAreCodes
    {
        public const string ACode = "GR9001";
        public const string NotACode = "hello";
        public const int NotAString = 7;
    }

    // --- The source scanner --------------------------------------------------------------

    [Fact]
    public void ParseSource_ReadsDeclarationsOnly_NeverProseOrCommentedOutLines()
    {
        const string source = """
            public static class DiagnosticCodes
            {
                /// <summary>GR9998 is RESERVED BY NAME here and must not be counted.</summary>
                public const string Real = "GR9001";

                // public const string CommentedOut = "GR9002";
                //   GR9003 is discussed in prose only.
            }
            """;

        IReadOnlyList<DeclaredCode> declared = CheckSetProbe.ParseSource(source);

        Assert.Equal(["GR9001"], declared.Select(d => d.Code));
        Assert.Equal("Real", declared[0].Name);
    }

    [Fact]
    public void ParseSource_CarriesTheConstantName_SoTheWarningIsActionable()
    {
        IReadOnlyList<DeclaredCode> declared =
            CheckSetProbe.ParseSource(SourceDeclaring("GR2069", "GR2068"));

        // Sorted ordinal, name preserved: the operator reads "GR2068 (NameGR2068)", not a bare number.
        Assert.Equal(["GR2068", "GR2069"], declared.Select(d => d.Code));
        Assert.Equal(["NameGR2068", "NameGR2069"], declared.Select(d => d.Name));
    }

    // --- The self-hosting positive control ----------------------------------------------

    /// <summary>
    /// The drift catcher, and the reason the scanner may be trusted at all: the REAL repository's
    /// <c>DiagnosticCodes.cs</c> must parse to exactly the set the compiled assembly carries. If the
    /// declaration form ever changes under the regex, this fails here rather than degrading into a
    /// quiet false "the tree agrees" in front of an operator.
    /// </summary>
    [Fact]
    public void RealRepositorySource_ParsesToExactlyTheCompiledCheckSet()
    {
        string root = RequireCheckoutRoot();

        IReadOnlyList<DeclaredCode> declared =
            CheckSetProbe.ParseSource(File.ReadAllText(CheckSetProbe.CodesPath(root)));

        Assert.Equal(CheckSetProbe.ImplementedCodes, declared.Select(d => d.Code).ToArray());

        CheckSetReport report = CheckSetProbe.Describe(Version, root);
        Assert.Equal(CheckSetComparison.Matches, report.Comparison);
        Assert.Null(report.StaleBinaryWarning);
    }

    /// <summary>
    /// The declared/mentioned distinction, asserted against the real file. <c>GR2054</c>,
    /// <c>GR2061</c> and <c>GR2070</c> are RESERVED BY NAME in design documents and appear in that
    /// file's prose only. Counting a reserved code as declared would make every released binary look
    /// permanently behind its own tree — a warning that always fires, which is no warning.
    /// </summary>
    [Fact]
    public void RealRepositorySource_DoesNotCountCodesReservedInProseOnly()
    {
        string root = RequireCheckoutRoot();
        string text = File.ReadAllText(CheckSetProbe.CodesPath(root));

        IReadOnlyList<string> declared = [.. CheckSetProbe.ParseSource(text).Select(d => d.Code)];

        foreach (string reserved in new[] { "GR2054", "GR2061", "GR2070" })
        {
            Assert.Contains(reserved, text);            // it IS in the file...
            Assert.DoesNotContain(reserved, declared);  // ...but only as prose.
        }
    }

    private static string RequireCheckoutRoot()
    {
        string? root = CheckSetProbe.FindCheckoutRoot(AppContext.BaseDirectory);

        // Deliberately an assertion, not a skip: the test assembly always builds under this repo, so
        // "not found" means the marker path moved and the whole mechanism has gone blind.
        Assert.True(root is not null, $"No Guardrails checkout found above {AppContext.BaseDirectory}.");
        return root!;
    }

    // --- The comparison, all five verdicts ------------------------------------------------

    [Fact]
    public void NoCheckout_IsNotCompared_AndTheSummarySaysSoRatherThanImplyingAgreement()
    {
        CheckSetReport report = CheckSetProbe.Compare(Version, ["GR1001"], sourceRoot: null, sourceText: null);

        Assert.Equal(CheckSetComparison.NotCompared, report.Comparison);
        Assert.Null(report.StaleBinaryWarning);

        // The honest label for the common case. "No news" must never read as "good news".
        Assert.Contains("no Guardrails source tree found", report.SummaryLine, StringComparison.Ordinal);
        Assert.Contains("cannot tell you whether newer checks exist", report.SummaryLine, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceThatParsesToZeroCodes_IsUnreadable_NeverMatches()
    {
        // A found-but-unparseable source is a SCANNER failure. Reporting "agrees" here would be this
        // issue's own defect wearing the fix's clothes, so it gets its own verdict and its own words.
        CheckSetReport report = CheckSetProbe.Compare(
            Version, ["GR1001"], sourceRoot: "/fake/checkout", sourceText: "// nothing declared here");

        Assert.Equal(CheckSetComparison.SourceUnreadable, report.Comparison);
        Assert.Null(report.StaleBinaryWarning);
        Assert.Contains("could not be read", report.SummaryLine, StringComparison.Ordinal);
        Assert.DoesNotContain("declares the same set", report.SummaryLine, StringComparison.Ordinal);
    }

    [Fact]
    public void AgreeingBinaryAndTree_EmitNoWarning_AndSayTheySawTheSameSet()
    {
        CheckSetReport report = CheckSetProbe.Compare(
            Version, ["GR1001", "GR2068"], "/fake/checkout", SourceDeclaring("GR1001", "GR2068"));

        Assert.Equal(CheckSetComparison.Matches, report.Comparison);
        Assert.Null(report.StaleBinaryWarning);
        Assert.Empty(report.MissingFromBinary);
        Assert.Contains("declares the same set", report.SummaryLine, StringComparison.Ordinal);
        Assert.DoesNotContain(DiagnosticCodes.CheckSetPredatesSourceTree, report.SummaryLine, StringComparison.Ordinal);
    }

    [Fact]
    public void BinaryAheadOfTree_EmitsNoWarning_BecauseNothingWasSkipped()
    {
        CheckSetReport report = CheckSetProbe.Compare(
            Version, ["GR1001", "GR2068"], "/fake/checkout", SourceDeclaring("GR1001"));

        // A NEWER tool against an older tree runs MORE checks, not fewer. Reported for orientation,
        // never warned about: the failure shape #564 is about is silence, not surplus.
        Assert.Equal(CheckSetComparison.BinaryAheadOfSource, report.Comparison);
        Assert.Null(report.StaleBinaryWarning);
        Assert.Equal(["GR2068"], report.MissingFromSource);
        Assert.Contains("AHEAD of the source tree", report.SummaryLine, StringComparison.Ordinal);
        Assert.Contains("Nothing was skipped", report.SummaryLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// The measured defect itself, reconstructed: the installed <c>1.12.0</c> against a tree that had
    /// already merged <c>GR2068</c>/<c>GR2069</c> at <c>9bc285c</c>. Those two checks produced 4
    /// findings on <c>docs/plans/28-local-inference-runner</c> from a build of master and 0 from the
    /// installed tool. This is the assertion that fails without the fix.
    /// </summary>
    [Fact]
    public void Issue564_InstalledToolPredatingTwoMergedChecks_NamesBothAndSaysTheyDidNotRun()
    {
        string[] treeCodes = [.. CheckSetProbe.ImplementedCodes];
        string[] binaryCodes = [.. treeCodes.Except(["GR2068", "GR2069"], StringComparer.Ordinal)];

        CheckSetReport report = CheckSetProbe.Compare(
            Version, binaryCodes, @"C:\DevAI\Guardrails", SourceDeclaring(treeCodes));

        Assert.Equal(CheckSetComparison.BinaryBehindSource, report.Comparison);
        Assert.Equal(["GR2068", "GR2069"], report.MissingFromBinary.Select(d => d.Code));

        Diagnostic warning = Assert.IsType<Diagnostic>(report.StaleBinaryWarning);
        Assert.Equal(DiagnosticCodes.CheckSetPredatesSourceTree, warning.Code);

        // A WARNING, never an ERROR: an older tool against a newer tree is legitimate (a release
        // build, a pinned CI, a deliberate reproduction), and refusing to validate would be a worse
        // cure than the disease. Warnings do not move validate's exit code.
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);

        Assert.Contains("GR2068", warning.Message, StringComparison.Ordinal);
        Assert.Contains("GR2069", warning.Message, StringComparison.Ordinal);
        Assert.Contains(Version, warning.Message, StringComparison.Ordinal);
        Assert.Contains("did NOT run", warning.Message, StringComparison.Ordinal);
        Assert.Contains("does not cover them", warning.Message, StringComparison.Ordinal);
        Assert.Contains("dotnet tool update -g ServantSoftware.Guardrails", warning.Message, StringComparison.Ordinal);

        // The diagnostic points at the exact file that disagrees, so the claim is checkable.
        Assert.Equal(CheckSetProbe.CodesPath(@"C:\DevAI\Guardrails"), warning.Path);
    }

    [Fact]
    public void BinaryBothBehindAndAhead_IsClassifiedBehind_BecauseSomethingWasStillSkipped()
    {
        CheckSetReport report = CheckSetProbe.Compare(
            Version, ["GR1001", "GR9001"], "/fake/checkout", SourceDeclaring("GR1001", "GR2068"));

        Assert.Equal(CheckSetComparison.BinaryBehindSource, report.Comparison);
        Assert.Equal(["GR2068"], report.MissingFromBinary.Select(d => d.Code));
        Assert.Equal(["GR9001"], report.MissingFromSource);
        Assert.NotNull(report.StaleBinaryWarning);
    }

    [Fact]
    public void ALongMissingList_IsTruncatedWithACount_NotDumpedWhole()
    {
        string[] missing = [.. Enumerable.Range(1, CheckSetReport.MaxEnumeratedCodes + 5).Select(i => $"GR9{i:D3}")];

        CheckSetReport report = CheckSetProbe.Compare(
            Version, ["GR1001"], "/fake/checkout", SourceDeclaring([.. missing, "GR1001"]));

        Diagnostic warning = Assert.IsType<Diagnostic>(report.StaleBinaryWarning);
        Assert.Contains("and 5 more", warning.Message, StringComparison.Ordinal);
        Assert.Contains(missing[0], warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(missing[^1], warning.Message, StringComparison.Ordinal);
    }

    // --- The always-printed summary line ---------------------------------------------------

    [Fact]
    public void SummaryLine_AlwaysCarriesTheVersion_TheCount_AndTheHighestCode()
    {
        // The cheap, always-correct half: even where no comparison is possible, the reader is handed
        // something to compare between two runs. Before #564 there was nothing.
        CheckSetReport report = CheckSetProbe.Compare(Version, ["GR1001", "GR2072"], null, null);

        Assert.Contains($"guardrails {Version}", report.SummaryLine, StringComparison.Ordinal);
        Assert.Contains("2 diagnostic codes", report.SummaryLine, StringComparison.Ordinal);
        Assert.Contains("highest GR2072", report.SummaryLine, StringComparison.Ordinal);
        Assert.Equal("GR2072", report.HighestCode);
    }

    [Fact]
    public void SummaryLine_OnAStaleBinary_PointsAtTheWarningRatherThanRepeatingIt()
    {
        CheckSetReport report = CheckSetProbe.Compare(
            Version, ["GR1001"], "/fake/checkout", SourceDeclaring("GR1001", "GR2068", "GR2069"));

        Assert.Contains("declares 2 code(s) this binary does not carry", report.SummaryLine, StringComparison.Ordinal);
        Assert.Contains(DiagnosticCodes.CheckSetPredatesSourceTree, report.SummaryLine, StringComparison.Ordinal);
    }

    // --- Checkout location (the only filesystem-touching part) -----------------------------

    [Fact]
    public void FindCheckoutRoot_WalksUpToTheMarkerFile_AndStopsAtTheNearestOne()
    {
        using var temp = new TempCheckout("GR1001");
        string nested = Path.Combine(temp.Root, "docs", "plans", "28-local-inference-runner");
        Directory.CreateDirectory(nested);

        Assert.Equal(temp.Root, CheckSetProbe.FindCheckoutRoot(nested));
    }

    [Fact]
    public void FindCheckoutRoot_OutsideACheckout_ReturnsNull()
    {
        string outside = Path.Combine(Path.GetTempPath(), "guardrails-nocheckout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            Assert.Null(CheckSetProbe.FindCheckoutRoot(outside));
            Assert.Null(CheckSetProbe.FindCheckoutRoot(null));
            Assert.Null(CheckSetProbe.FindCheckoutRoot("   "));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Describe_FallsBackToTheSecondStartDirectory_SoAPlanValidatedFromElsewhereIsStillCovered()
    {
        using var temp = new TempCheckout("GR1001", "GR9999");
        string elsewhere = Path.Combine(Path.GetTempPath(), "guardrails-elsewhere-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elsewhere);
        try
        {
            // First candidate (a plan outside any checkout) finds nothing; the second (the working
            // directory, inside the checkout) does — and the comparison still happens.
            CheckSetReport report = CheckSetProbe.Describe(Version, elsewhere, temp.Root);

            Assert.Equal(temp.Root, report.SourceRoot);
            Assert.Equal(CheckSetComparison.BinaryBehindSource, report.Comparison);
            Assert.Equal(["GR9999"], report.MissingFromBinary.Select(d => d.Code));
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public void Describe_WithNoUsableStartDirectory_IsNotCompared_AndDoesNotThrow()
    {
        CheckSetReport report = CheckSetProbe.Describe(Version, null, "   ");

        Assert.Equal(CheckSetComparison.NotCompared, report.Comparison);
        Assert.Null(report.SourceRoot);
    }

    /// <summary>A throwaway directory shaped like a Guardrails checkout, declaring exactly the given codes.</summary>
    private sealed class TempCheckout : IDisposable
    {
        public TempCheckout(params string[] codes)
        {
            Root = Path.Combine(Path.GetTempPath(), "guardrails-checkout-" + Guid.NewGuid().ToString("N"));
            string codesPath = CheckSetProbe.CodesPath(Root);
            Directory.CreateDirectory(Path.GetDirectoryName(codesPath)!);
            File.WriteAllText(codesPath, SourceDeclaring(codes));
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort — a leaked temp dir must never fail a test.
            }
        }
    }
}
