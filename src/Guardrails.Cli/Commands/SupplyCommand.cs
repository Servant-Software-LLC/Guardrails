using System.CommandLine;

namespace Guardrails.Cli.Commands;

/// <summary>
/// STUB — <c>guardrails supply &lt;plan&gt; &lt;path&gt;...</c> (design 40 §1/§6): stage one or
/// more workspace-relative files into <c>logs/&lt;runId&gt;/supplied/</c> for the harness to drain
/// onto the run's base at its next boundary (a task boundary while the run is live; run start,
/// before scheduling, when the run has already halted and exited — §1's measured case).
/// <para>
/// The staging/refusal logic AND the <see cref="CommandFactory"/> registration land in design 40
/// task 11. This declares only the CLI shape, so <c>SupplyCommandTests</c> compiles against a real
/// (if inert) command — it is not yet reachable from <see cref="CommandFactory.BuildRootCommand"/>,
/// so every invocation in that suite fails to parse rather than reaching <see cref="Run"/>.
/// </para>
/// </summary>
public static class SupplyCommand
{
    public static Command Create(IConsoleIo io)
    {
        var planArgument = FolderArgument.Create(
            "Path to the plan folder (contains guardrails.json) whose run to supply.");

        var pathArgument = new Argument<string[]>("path")
        {
            Description = "Workspace-relative path(s) to stage for the run's next drain boundary.",
            Arity = ArgumentArity.OneOrMore
        };

        var command = new Command("supply",
            "Stage file(s) for an in-flight or halted run to pick up at its next drain boundary.");
        command.Add(planArgument);
        command.Add(pathArgument);

        command.SetAction(_ => Run());

        return command;
    }

    private static int Run() => throw new NotImplementedException(
        "guardrails supply: staging, the resumable-run refusal, and the caller-scope check land in design 40 task 11.");
}
