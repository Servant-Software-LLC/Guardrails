// A COMPLETE, representative CORRECT artifact for 03-anchor-enumerates-the-set-not-a-count.ps1
// (#468/#302): the committed anchor test as section 9 asks for it. Every site named by file AND member,
// both directions of the set assertion, the four zero-occurrence files named, the shape anchors present,
// the single capture site excluded by name - and no assertion about how MANY of anything there are.
// Kept complete rather than a fragment; this header names none of the tokens the clauses key on.
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

    private static readonly (string File, string Member, string Why)[] SurvivingSites =
    [
        ("src/Guardrails.Core/Execution/Scheduler.cs", "DetectDefinitionDrift", "the resume drift pre-pass"),
        ("src/Guardrails.Core/Execution/Scheduler.cs", "BuildResolvedTasks", "Part C audit rows"),
        ("src/Guardrails.Core/Execution/Scheduler.cs", "ConsumePendingAnswers", "answer-file anti-stale key"),
        ("src/Guardrails.Core/Execution/Scheduler.cs", "ClassifyTaskGateAsync", "escalation record binding"),
        ("src/Guardrails.Cli/Commands/DryRun.cs", "IsDrifted", "the dry-run preview"),
        ("src/Guardrails.Cli/Commands/DefinitionDriftProbe.cs", "Evaluate", "the pre-run probe"),
        ("src/Guardrails.Core/State/RunReset.cs", "SafeComputeHash", "reset audit rows"),
        ("src/Guardrails.Core/Journal/WaveDefinitionHash.cs", "Compute", "the disk form's task fold"),
    ];

    public static TheoryData<string, string, string> Sites()
    {
        TheoryData<string, string, string> data = [];
        foreach ((string file, string member, string why) in SurvivingSites)
        {
            data.Add(file, member, why);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void EachSurvivingSiteStillRecomputesFromDisk(string file, string member, string why)
    {
        string body = MemberBody(File.ReadAllText(Path.Combine(RepoRoot(), file)), member);
        Assert.True(
            ComputeCall.IsMatch(body),
            file + " :: " + member + " no longer recomputes the definition hash from disk. It is a READ ("
            + why + "); pinning it would silence definition drift entirely.");
    }

    [Fact]
    public void NoOtherSiteRecomputesAnywhereInSrc()
    {
        var unexpected = new List<string>();
        foreach (string path in EnumerateSources())
        {
            string text = File.ReadAllText(path);
            foreach (Match hit in ComputeCall.Matches(text))
            {
                string relative = Relative(path);
                string member = EnclosingMember(text, hit.Index);
                if (!SurvivingSites.Any(s => s.File == relative && s.Member == member))
                {
                    unexpected.Add(relative + " :: " + member);
                }
            }
        }

        // The direction that catches the seventh site. It NAMES the offender rather than reporting that
        // a number moved, which is the whole reason this anchor is a set.
        Assert.Empty(unexpected);
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

    [Fact]
    public void TheSiteTableIsSelfConsistent()
    {
        // Set hygiene, borrowed from the repo's other anchor suites: no two rows may pin the same site,
        // or the set would look broader than it is.
        Assert.Equal(SurvivingSites.Length, SurvivingSites.Select(s => s.File + "::" + s.Member).Distinct().Count());
    }

    private static IEnumerable<string> EnumerateSources() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private static string Relative(string absolute) =>
        Path.GetRelativePath(RepoRoot(), absolute).Replace(Path.DirectorySeparatorChar, '/');

    private static string StripComments(string text) =>
        Regex.Replace(Regex.Replace(text, @"/\*[\s\S]*?\*/", " "), @"(?m)//[^\r\n]*", " ");

    private static string MemberBody(string text, string member) => SourceRegions.BodyOf(text, member);

    private static string EnclosingMember(string text, int index) => SourceRegions.MemberAt(text, index);

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
