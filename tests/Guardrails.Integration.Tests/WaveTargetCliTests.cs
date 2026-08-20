using System.CommandLine;
using System.Text.Json;
using Guardrails.Cli;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;

namespace Guardrails.Integration.Tests;

/// <summary>
/// The <c>/guardrails-review</c> per-wave stamp flow, end to end through the REAL production dispatch
/// (<see cref="CommandFactory.BuildRootCommand"/>) — issue #472, whose measured symptom was that all three
/// documented moves failed <c>GR1001</c> on a nested wave because a wave carries no <c>guardrails.json</c>.
///
/// <para>Also the CLI-visible half of #488: on a waved plan the review nudge is per WAVE, so stamping the
/// wave that was reviewed silences exactly that wave and nothing else.</para>
/// </summary>
public sealed class WaveTargetCliTests
{
    private const string Wave1 = "wave-01-scaffold";
    private const string Wave2 = "wave-02-provision";

    private static ScriptPlanBuilder TwoWavePlan()
    {
        var builder = new ScriptPlanBuilder();
        builder.AddWave(Wave1).AddTask("01-init");
        builder.AddWave(Wave2).AddTask("01-provision");
        return builder;
    }

    [Fact]
    public async Task PlanHash_OnAWaveFolder_PrintsThatWavesDefinitionHash()
    {
        using ScriptPlanBuilder plan = TwoWavePlan();
        PlanLoadResult load = new PlanLoader().Load(plan.PlanDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        (int exit, string output, _) = await InvokeAsync("plan-hash", Path.Combine(plan.PlanDir, Wave1));

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains(
            WaveDefinitionHash.Compute(load.Plan!.Waves.Single(w => w.Dir == Wave1)),
            output,
            StringComparison.Ordinal);
        // Emphatically NOT the plan hash: keying a wave marker on that is the #488 defect.
        Assert.DoesNotContain(PlanDefinitionHash.Compute(load.Plan!), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkReviewed_OnAWaveFolder_StampsThatWaveOnly()
    {
        using ScriptPlanBuilder plan = TwoWavePlan();

        (int exit, string output, _) = await InvokeAsync("mark-reviewed", Path.Combine(plan.PlanDir, Wave1));

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("waveDefinitionHash", output, StringComparison.Ordinal);
        Assert.Contains(Wave1, output, StringComparison.Ordinal);

        string markerPath = Path.Combine(plan.PlanDir, Wave1, "state", "guardrails-review.json");
        Assert.True(File.Exists(markerPath), $"expected the wave marker at {markerPath}");
        // Nothing else is attested: not the plan, not the other wave.
        Assert.False(File.Exists(Path.Combine(plan.PlanDir, "state", "guardrails-review.json")));
        Assert.False(File.Exists(Path.Combine(plan.PlanDir, Wave2, "state", "guardrails-review.json")));

        PlanLoadResult load = new PlanLoader().Load(plan.PlanDir);
        Assert.Equal(
            WaveDefinitionHash.Compute(load.Plan!.Waves.Single(w => w.Dir == Wave1)),
            JsonDocument.Parse(File.ReadAllText(markerPath)).RootElement.GetProperty("planHash").GetString());
    }

    [Fact]
    public async Task Validate_OnAWaveFolder_FailsWithGR1010_NamingTheParentPlan()
    {
        using ScriptPlanBuilder plan = TwoWavePlan();

        (int exit, string output, _) = await InvokeAsync("validate", Path.Combine(plan.PlanDir, Wave1));

        // Still an ERROR — a wave is not independently loadable — but no longer a dead end.
        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("GR1010", output, StringComparison.Ordinal);
        Assert.DoesNotContain("GR1001", output, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(plan.PlanDir), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_OnAWavedPlan_NudgesPerWave_AndNeverAtPlanLevel()
    {
        using ScriptPlanBuilder plan = TwoWavePlan();

        (int before, string beforeOut, _) = await InvokeAsync("validate", plan.PlanDir);
        Assert.Equal(ExitCodes.Success, before);
        Assert.Equal(2, CountOccurrences(beforeOut, "GR2025"));

        // Stamp only the wave that was actually reviewed — the move #472 made impossible.
        await InvokeAsync("mark-reviewed", Path.Combine(plan.PlanDir, Wave1));

        (int after, string afterOut, _) = await InvokeAsync("validate", plan.PlanDir);

        Assert.Equal(ExitCodes.Success, after);
        Assert.Equal(1, CountOccurrences(afterOut, "GR2025"));
        Assert.Contains(Wave2, afterOut, StringComparison.Ordinal);
        // The reviewed wave is silent, and no plan-level line reappears to speak for it.
        Assert.DoesNotContain($"wave '{Wave1}'", afterOut, StringComparison.Ordinal);
        Assert.DoesNotContain("this plan hasn't been through", afterOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheThreeMoveFlow_RecordsAReviewArtifact_AgainstTheWavesOwnReviewsTree()
    {
        // The whole §7 flow the skill documents, on a wave: plan-hash → write the report under THAT wave's
        // state/reviews/ → mark-reviewed --evidence. The #366 F2 checks must resolve against the wave, not
        // the plan root, or every per-wave stamp silently downgrades to `bare`.
        using ScriptPlanBuilder plan = TwoWavePlan();
        string waveFolder = Path.Combine(plan.PlanDir, Wave1);

        (int hashExit, string hashOut, _) = await InvokeAsync("plan-hash", waveFolder);
        Assert.Equal(ExitCodes.Success, hashExit);
        string waveHash = ExtractHash(hashOut);

        string reportPath = Path.Combine(waveFolder, "state", "reviews", "review-wave-01.md");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, $"# Review\n\nPlan-Definition-Hash: {waveHash}\n");

        (int stampExit, string stampOut, string stampErr) =
            await InvokeAsync("mark-reviewed", waveFolder, "--evidence", reportPath);

        Assert.Equal(ExitCodes.Success, stampExit);
        Assert.Contains("source: review-artifact", stampOut, StringComparison.Ordinal);
        Assert.DoesNotContain("DOWNGRAD", stampOut, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WARNING", stampErr, StringComparison.Ordinal);

        JsonElement attestation = JsonDocument
            .Parse(File.ReadAllText(Path.Combine(waveFolder, "state", "guardrails-review.json")))
            .RootElement.GetProperty("attestation");
        Assert.Equal("review-artifact", attestation.GetProperty("source").GetString());
        // Recorded relative to the WAVE, which is where a wave's report lives (§13).
        Assert.Equal(
            Path.Combine("state", "reviews", "review-wave-01.md"),
            attestation.GetProperty("evidence").GetProperty("reportPath").GetString());
    }

    private static int CountOccurrences(string text, string token)
    {
        int count = 0;
        for (int i = text.IndexOf(token, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string ExtractHash(string output)
    {
        int start = output.IndexOf("sha256:", StringComparison.Ordinal);
        Assert.True(start >= 0, $"expected a sha256:… hash in the output but found none:\n{output}");

        int end = start + "sha256:".Length;
        while (end < output.Length && Uri.IsHexDigit(output[end]))
        {
            end++;
        }

        return output[start..end];
    }

    private static async Task<(int ExitCode, string Output, string Error)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        RootCommand root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText, io.ErrorText);
    }
}
