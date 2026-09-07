using System.CommandLine;
using Guardrails.Cli;

namespace Guardrails.Integration.Tests.Commands;

/// <summary>
/// Every verb the CLI registers is named in the README (issue #600).
///
/// <para><b>Why a test and not a review habit.</b> The drift this catches is structural, not an oversight:
/// nothing connected shipping a command to documenting it, so five verbs — <c>attach</c>, <c>providers</c>,
/// <c>samples</c>, <c>mark-reviewed</c>, <c>plan-hash</c> — reached users with <b>zero</b> occurrences of
/// <c>guardrails &lt;verb&gt;</c> in the front door. <c>attach</c> appeared once as an English word and never
/// as an invocation, so a reader could not learn that watching an unattended run from a second terminal was
/// possible at all.</para>
///
/// <para>Two of those are the ones a user is most likely to need and least likely to discover, which is the
/// shape of the cost: an undocumented command is, for practical purposes, an unshipped one. And this is not
/// a gap a SELF-UPDATING instruction closed — the domain-knowledge skill carries exactly such a clause and
/// still missed the run streams until #595 forced it.</para>
///
/// <para>So it is a closed-world check, the same shape this repo already uses for drift it does not rely on
/// people to remember: enumerate what the composition root ACTUALLY registers, and require each to appear.
/// Reading <see cref="CommandFactory.BuildRootCommand"/> rather than a list means a command added tomorrow
/// is covered the day it lands, with no second inventory to keep in step.</para>
/// </summary>
public sealed class ReadmeCommandCoverageTests
{
    [Fact]
    public void EveryRegisteredVerbIsNamedInTheReadme()
    {
        string readmePath = Path.Combine(RepoRoot(), "README.md");
        Assert.True(File.Exists(readmePath), $"README not found at {readmePath}");

        string readme = File.ReadAllText(readmePath);
        RootCommand root = CommandFactory.BuildRootCommand(new StringConsoleIo());

        List<string> verbs = [.. root.Subcommands.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal)];

        // Fixture sanity: an empty enumeration would make this test pass while checking nothing, which is
        // the exact failure mode it exists to prevent one level down.
        Assert.True(verbs.Count >= 15, $"only {verbs.Count} verbs enumerated — the composition root moved?");

        // "guardrails <verb>" specifically, not the bare word: `attach` and `merge` and `run` and `lock` are
        // all ordinary English, and matching them loose would let a README that never shows an invocation
        // report full coverage.
        List<string> undocumented =
        [
            .. verbs.Where(v => !readme.Contains($"guardrails {v}", StringComparison.Ordinal))
        ];

        Assert.True(
            undocumented.Count == 0,
            "the CLI registers verbs the README never shows as an invocation — an undocumented command is, "
            + "for practical purposes, an unshipped one:\n  "
            + string.Join("\n  ", undocumented.Select(v => $"guardrails {v}")));
    }

    [Fact]
    public void TheReadmeCoversTheUnattendedWorkflowAndItsCostTrap()
    {
        // The combination that is how a long plan is actually run today, and which was entirely absent.
        // The $20 default is called out by name because it is the one that bites: --autonomous applies it
        // SILENTLY when no --max-cost-usd is given, and a real plan halts early on a cap nobody set.
        string readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));

        foreach (string token in new[] { "--autonomous", "--dial", "--max-cost-usd", "$20" })
        {
            Assert.True(
                readme.Contains(token, StringComparison.Ordinal),
                $"the README does not mention {token} — the unattended workflow is how long plans are run, "
                + "and the silent cost cap is the part that halts one unexpectedly");
        }
    }

    [Fact]
    public void TheReadmeNamesTheConceptsAPlanAuthorMeetsOnDayOne()
    {
        // writeScope is REQUIRED on every task (GR2041) — an author meets it before anything else — and the
        // four-folder model decides where a check even goes. Neither appeared.
        string readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));

        foreach (string token in new[] { "writeScope", "preflights" })
        {
            Assert.True(
                readme.Contains(token, StringComparison.Ordinal),
                $"the README never mentions '{token}', which a plan author meets on day one");
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root (.git) not found above the test binary");
    }
}
