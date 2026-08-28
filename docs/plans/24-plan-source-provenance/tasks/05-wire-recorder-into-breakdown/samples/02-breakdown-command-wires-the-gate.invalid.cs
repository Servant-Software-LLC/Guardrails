using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Loading;

namespace Guardrails.Cli.Commands;

/// <summary>
/// THE ONE DEFECT THIS SAMPLE CARRIES: DeclaredCountGate is NAMED but never USED. It appears in this
/// doc comment, in a `//` comment beside the call site it never got, and inside an operator-facing
/// message string — the three places a bare-word grep would accept and a USE-anchored, $scan-based
/// clause must not. The gate is dead code reachable only from xUnit; a breakdown that under-records
/// delegated decisions still exits 0 from the CLI.
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

        // TODO: run DeclaredCountGate.Evaluate here once the gate lands.
        io.Out.WriteLine("note: DeclaredCountGate.Evaluate is not enforced on this path yet");

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
