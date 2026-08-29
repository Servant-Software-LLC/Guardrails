using System.CommandLine;
using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Samples;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails samples verify [folder]</c> — walks every committed <c>tasks/&lt;id&gt;/samples/</c>
/// pair and runs it through the SHARED <see cref="SampleVerifier"/>, the same type the pre-DAG
/// preflight phase runs (plan of record 26 §3), so the verb and the phase can never disagree about
/// whether a committed pair is sound. Read-only apart from its own temp dirs; CI-runnable.
/// </summary>
public static class SamplesCommand
{
    private static readonly TimeSpan PerSampleTimeout = TimeSpan.FromSeconds(60);

    public static Command Create(IConsoleIo io)
    {
        var command = new Command("samples", "Work with the committed tasks/<id>/samples/ pairs.");
        command.Add(BuildVerifyLeaf(io));
        return command;
    }

    private static Command BuildVerifyLeaf(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        var command = new Command("verify", "Execute every committed sample pair against its guardrail.");
        command.Add(folderArgument);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out);
            return await RunAsync(folder, io, cancellationToken);
        });

        return command;
    }

    private static async Task<int> RunAsync(string folder, IConsoleIo io, CancellationToken cancellationToken)
    {
        // PlanLoader, not PlanProbe.LoadAndValidate: this verb verifies SAMPLES, it is not a second
        // validate. A folder that loads but carries validation diagnostics is still sample-verifiable —
        // exactly the mid-authoring case this is useful in. Refuse only when no PlanDefinition resulted.
        PlanLoadResult loaded = new PlanLoader().Load(folder);
        if (loaded.Plan is not PlanDefinition plan)
        {
            io.Out.WriteLine($"Could not load a plan from \"{folder}\" — nothing to verify.");
            return ExitCodes.HarnessError;
        }

        SampleVerifyResult result = await SampleVerifier.VerifyAsync(
            plan, new ProcessRunner(), PerSampleTimeout, cancellationToken);

        foreach (SampleFinding finding in result.Findings)
        {
            io.Out.WriteLine(
                $"{finding.Kind}: {finding.SamplePath} against {finding.GuardrailPath} → "
                + $"exit {finding.ObservedExitCode?.ToString() ?? "(none)"}");
            io.Out.WriteLine($"  {finding.Message}");
        }

        if (result.Findings.Count > 0)
        {
            io.Out.WriteLine();
            io.Out.WriteLine(
                $"FAILED: {result.Findings.Count} finding(s) over {result.PairsVerified} pair(s). "
                + "The harness lints the guardrail that can never PASS (GR2055); running the .invalid half "
                + "is the only detector for the opposite and more dangerous polarity — the guardrail that "
                + "can never FAIL. Fix the pair or the guardrail; do not delete the pair.");
            return ExitCodes.HarnessError;
        }

        io.Out.WriteLine($"OK: {result.PairsVerified} sample pair(s) verified, 0 findings.");
        return ExitCodes.Success;
    }
}
