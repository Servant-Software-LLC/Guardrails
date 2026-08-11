using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Regression gate for issue #443: the released macOS binaries must NOT be published with
/// single-file compression.
/// <para>
/// A compressed bundle entry is the only case that routes image loading through
/// <c>FlatImageLayout::LoadImageByCopyingParts</c> on Unix (coreclr <c>peimagelayout.cpp</c>:
/// <c>PEImageLayout::Load</c> takes the direct-mmap path when <c>!IsInBundle() || !IsCompressed()</c>).
/// That copying path reserves the image MAP_JIT/read-write, copies the section bytes in, then
/// promotes executable sections RW → RWX; on Apple Silicon that transition intermittently reports
/// success while leaving the pages non-executable in the kernel, so the next call into the
/// (ReadyToRun-precompiled, self-contained) framework code dies with a fatal
/// <c>AccessViolationException</c> at an arbitrary frame. Upstream: dotnet/runtime#123324,
/// #112167 and #88288, fixed by dotnet/runtime#127355 in .NET 11 and NOT backported to 10.
/// </para>
/// <para>
/// This is a STRUCTURAL gate on purpose. The crash is a non-deterministic memory-corruption fault
/// that macOS 26 happens to make near-certain and older macOS runners barely reproduce, so a
/// "spawn a child N times and see if it survives" test on our CI's macOS image would pass either
/// way and certify nothing. Asserting the publish flag is deterministic on every OS.
/// </para>
/// </summary>
public sealed class ReleasePackagingRegressionTests
{
    private static readonly string ReleaseWorkflow =
        Path.Combine(RepoRoot(), ".github", "workflows", "release.yml");

    /// <summary>Repo root, resolved from this source file (tests/Guardrails.Integration.Tests/…).</summary>
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string WorkflowText()
    {
        Assert.True(File.Exists(ReleaseWorkflow), $"release workflow not found at {ReleaseWorkflow}");
        return File.ReadAllText(ReleaseWorkflow);
    }

    [Fact]
    public void ReleaseWorkflow_DoesNotHardCodeSingleFileCompressionOn()
    {
        Match hardCoded = Regex.Match(
            WorkflowText(),
            """EnableCompressionInSingleFile\s*=\s*"?\s*true\s*"?""",
            RegexOptions.IgnoreCase);

        Assert.False(
            hardCoded.Success,
            "release.yml enables single-file compression unconditionally, which corrupts the macOS "
            + "binaries (#443 — fatal AccessViolationException on Apple Silicon). Drive the flag from "
            + "the per-RID COMPRESS variable instead, and keep it false for every osx-* RID.");
    }

    [Fact]
    public void ReleaseWorkflow_DisablesSingleFileCompression_ForMacOsRids()
    {
        string workflow = WorkflowText();

        Assert.Matches(@"osx-\*\)[^\r\n]*COMPRESS=false", workflow);
        Assert.Contains("EnableCompressionInSingleFile=\"$COMPRESS\"", workflow, StringComparison.Ordinal);

        // Keeps the gate from going vacuous if the macOS legs are ever renamed out from under the
        // osx-* glob the COMPRESS switch matches on.
        List<string> rids = Regex.Matches(workflow, @"rid:\s*(?<rid>[a-z0-9\-]+)")
            .Select(match => match.Groups["rid"].Value)
            .ToList();
        Assert.Contains("osx-arm64", rids);
        Assert.All(
            rids.Where(rid => rid.StartsWith("osx", StringComparison.Ordinal)),
            rid => Assert.StartsWith("osx-", rid, StringComparison.Ordinal));
    }
}
