using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Core.Loading;

namespace Guardrails.Integration.Tests.Commands;

/// <summary>
/// <c>guardrails diagnostics</c> through the REAL composition root (issue #558).
///
/// <para>Driven through <see cref="CommandFactory.BuildRootCommand"/> rather than
/// <c>DiagnosticsCommand.Create</c>, deliberately: a command that is written, tested in isolation, and
/// never registered is a command the operator does not have. That gap is not hypothetical in this repo —
/// it is the same shape as #382, where fake-masked unit tests certified green while the composition root
/// was broken. Parsing here fails if the verb is not wired.</para>
/// </summary>
public sealed class DiagnosticsCommandTests
{
    private static async Task<(int Exit, string Output)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        RootCommand root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    [Fact]
    public async Task TheVerbIsWiredIntoTheRootCommand_AndListsEveryCodeTheCatalogueKnows()
    {
        (int exit, string output) = await InvokeAsync("diagnostics");

        Assert.Equal(ExitCodes.Success, exit);

        // Every code, not a sample of them — an operator who cannot find their code here is back where
        // they started, and the count is the only thing that makes "every" checkable.
        foreach (DiagnosticEntry entry in DiagnosticCatalogue.Entries)
        {
            Assert.Contains(entry.Code, output, StringComparison.Ordinal);
        }

        Assert.Contains($"{DiagnosticCatalogue.Entries.Length} code(s)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASingleCode_PrintsSeverityAndTheFullRationale_NotJustTheOneLiner()
    {
        // The rationale is the point. These doc comments carry the reasoning behind each check — why it
        // fires, what it measured, what the remedy is — and a lookup that printed only the summary would
        // leave that where it already was: in a source file the operator does not have.
        (int exit, string output) = await InvokeAsync("diagnostics", "GR2042");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("GR2042", output, StringComparison.Ordinal);
        Assert.Contains("StructuralOverScope", output, StringComparison.Ordinal);
        Assert.Contains("WARNING", output, StringComparison.Ordinal);

        DiagnosticEntry entry = DiagnosticCatalogue.Find("GR2042")!;
        Assert.True(
            output.Length > entry.Summary.Length * 3,
            "the single-code view printed about a summary's worth of text; the rationale is the reason "
            + "this command exists");
    }

    [Fact]
    public async Task ACodeIsAcceptedInTheCaseAnOperatorActuallyTypesIt()
    {
        // Read off a terminal and retyped, not copied from source.
        (int lower, string lowerText) = await InvokeAsync("diagnostics", "gr2042");
        (int upper, string upperText) = await InvokeAsync("diagnostics", "GR2042");

        Assert.Equal(ExitCodes.Success, lower);
        Assert.Equal(ExitCodes.Success, upper);
        Assert.Equal(upperText, lowerText);
    }

    [Fact]
    public async Task AnUnknownCode_FailsAndPointsSomewhere_RatherThanPrintingNothing()
    {
        (int exit, string output) = await InvokeAsync("diagnostics", "GR9999");

        Assert.NotEqual(ExitCodes.Success, exit);
        Assert.Contains("GR9999", output, StringComparison.Ordinal);
        Assert.Contains("guardrails diagnostics", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLadderFilterSelectsOneLadderAndExcludesTheOther()
    {
        (int exit, string output) = await InvokeAsync("diagnostics", "--ladder", "GR10");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("GR1001", output, StringComparison.Ordinal);

        // The filter has to actually EXCLUDE, or it is a listing with extra words. GR20xx is 60-odd codes,
        // so its absence is the whole signal.
        Assert.DoesNotContain("GR2001", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItNeedsNoPlanFolder_soItStillWorksWhenTheOperatorsPlanDoesNot()
    {
        // The moment an operator meets a code is the moment their plan is broken. A glossary that required
        // a loadable plan would be unavailable exactly when it is needed — and this is also why `validate`
        // can point at it.
        string elsewhere = Path.Combine(Path.GetTempPath(), $"gr558-{Guid.NewGuid():N}");
        Directory.CreateDirectory(elsewhere);
        string original = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(elsewhere);
            (int exit, string output) = await InvokeAsync("diagnostics", "GR1001");

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Contains("MissingFile", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            try { Directory.Delete(elsewhere, recursive: true); } catch (IOException) { }
        }
    }
}
