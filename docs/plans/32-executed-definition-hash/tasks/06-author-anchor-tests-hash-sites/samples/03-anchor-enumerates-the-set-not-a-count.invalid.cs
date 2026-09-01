// The ONE defect 03-anchor-enumerates-the-set-not-a-count.ps1 exists to catch, and section 9 records
// it defeating a whole draft: the anchor written as a COUNT. It passes, it looks rigorous, and it
// tells nobody WHICH site moved - so an agent that meets a wrong number under retry pressure runs
// the grep and writes down whatever it says. The number the defeated draft used was 6 against a
// true 8. Identical to the .valid half apart from the first test and the deleted site table.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Guardrails.Core.Tests;

public sealed class ExecutedDefinitionHashAnchorTests
{
    // The invocation, tolerating the Journal. prefix and whitespace - NOT one literal expression. An
    // earlier draft of this anchor pinned a single spelling; it matched once on the unfixed tree and
    // zero times at three of the four write sites, so "fixing" one of them would have turned it green.
    private static readonly Regex ComputeCall =
        new(@"(?:\bJournal\s*\.\s*)?\bTaskDefinitionHash\s*\.\s*Compute\s*\(", RegexOptions.Compiled);

    private const int ExpectedComputeCallSites = 8;

    [Fact]
    public void TheRightNumberOfSitesStillRecomputeFromDisk()
    {
        int total = EnumerateSources().Sum(p => ComputeCall.Matches(File.ReadAllText(p)).Count);
        Assert.Equal(ExpectedComputeCallSites, total);
    }

    [Theory]
    [InlineData("src/Guardrails.Core/Execution/AttemptJournaler.cs")]
    [InlineData("src/Guardrails.Core/Execution/TaskExecutor.cs")]
    [InlineData("src/Guardrails.Core/Model/TaskNode.cs")]
    [InlineData("src/Guardrails.Core/Model/WaveNode.cs")]
    public void TheseFilesRecomputeNothing(string file)
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), file));
        Assert.False(ComputeCall.IsMatch(text), file + " recomputes a definition hash; it must not.");
    }

    [Fact]
    public void TheModelTypesCannotNameAHasherAtAll()
    {
        // Comments stripped first: WaveNode.cs carries a see-cref doc comment naming the wave hasher, so
        // an unstripped check false-reds a correct file on arrival.
        foreach (string file in new[] { "src/Guardrails.Core/Model/TaskNode.cs", "src/Guardrails.Core/Model/WaveNode.cs" })
        {
            string code = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), file)));
            Assert.DoesNotContain("TaskDefinitionHash", code, StringComparison.Ordinal);
            Assert.DoesNotContain("WaveDefinitionHash", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Lazy<", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BothCapturesAreBodilessAutoProperties()
    {
        string code = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "src/Guardrails.Core/Model/TaskNode.cs")));
        Assert.Matches(@"public\s+string\?\s+DefinitionHashAtLoad\s*\{[^}]*\bget\b[^}]*\}", code);
        Assert.Matches(@"public\s+.+?\?\s+DefinitionFilesAtLoad\s*\{[^}]*\bget\b[^}]*\}", code);
    }

    [Fact]
    public void NothingFallsBackToDiskOffThePin()
    {
        var offenders = new List<string>();
        foreach (string path in EnumerateSources())
        {
            // PlanLoader.cs is EXCLUDED, and the exclusion is load-bearing rather than a convenience:
            // the single capture site is literally
            //     return node with { DefinitionHashAtLoad = TaskDefinitionHash.Compute(node), ... };
            // one line carrying both tokens, and it is the ONE place that pairing is correct. Everywhere
            // else it is the coalescing fallback the plan calls its cheapest wrong implementation. Do not
            // "fix" this exclusion away; without it the anchor false-reds a correct tree forever.
            if (Relative(path).EndsWith("Loading/PlanLoader.cs", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Contains("DefinitionHashAtLoad", StringComparison.Ordinal)
                    && line.Contains("Compute(", StringComparison.Ordinal))
                {
                    offenders.Add(Relative(path) + ": " + line.Trim());
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoCloneRebindsTheIdentityOfANode()
    {
        var offenders = new List<string>();
        var clone = new Regex(@"with\s*\{[^}]*\b(Directory|Action)\s*=", RegexOptions.Singleline);
        foreach (string path in EnumerateSources())
        {
            if (clone.IsMatch(File.ReadAllText(path)))
            {
                offenders.Add(Relative(path));
            }
        }

        // A clone that rebound either member would carry a pin describing a different folder. The two
        // clones that exist rebind only DependsOn and Tasks, and DependsOn lives inside task.json and is
        // therefore already inside the hash.
        Assert.Empty(offenders);
    }

    private static IEnumerable<string> EnumerateSources() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private static string Relative(string absolute) =>
        Path.GetRelativePath(RepoRoot(), absolute).Replace(Path.DirectorySeparatorChar, '/');

    private static string StripComments(string text) =>
        Regex.Replace(Regex.Replace(text, @"/\*[\s\S]*?\*/", " "), @"(?m)//[^\r\n]*", " ");

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
