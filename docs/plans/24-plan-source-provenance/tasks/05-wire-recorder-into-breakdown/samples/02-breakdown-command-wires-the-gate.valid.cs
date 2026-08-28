using Guardrails.Core.Breakdown;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Loading;

namespace Guardrails.Cli.Commands;

/// <summary>
/// Representative CORRECT shape: the declared-count gate is USED on the post-return path, so a
/// breakdown that under-records delegated decisions fails from the CLI.
/// </summary>
internal static class BreakdownCommand
{
    public static async Task<int> RunAsync(string plan, string outputFolder, string logDir, IConsoleIo io, CancellationToken ct)
    {
        Directory.CreateDirectory(outputFolder);
        BreakdownInvocationPlan prepared = InitialBreakdownInvoker.PrepareInvocation(plan, outputFolder, logDir);

        var invoker = new InitialBreakdownInvoker(RunnerFactory.Resolve());
        WaveBreakdownOutcome outcome = await invoker.InvokeAsync(prepared, outputFolder, ct);
        if (!outcome.Succeeded)
        {
            io.Out.WriteLine($"Breakdown did not complete — anything authored is on disk at {outputFolder}.");
            return 1;
        }

        // The declared-count gate: what the harness READ vs what the agent PRODUCED. It runs outside
        // the agent it polices, which is the whole point — a breakdown that never scanned records
        // nothing, so M = 0 and this reds.
        DeclaredCountVerdict verdict = DeclaredCountGate.Evaluate(prepared.DeclaredDelegatedDecisions, outputFolder);
        if (!verdict.Passed)
        {
            io.Out.WriteLine(verdict.Message);
            return 1;
        }

        PlanProbe.Result result = PlanProbe.LoadAndValidate(outputFolder);
        if (!result.Ok)
        {
            io.Out.WriteLine($"Authored {outputFolder}, but it does NOT validate — see the errors above.");
            return 1;
        }

        io.Out.WriteLine($"OK: authored and validated {outputFolder}");
        return 0;
    }
}
