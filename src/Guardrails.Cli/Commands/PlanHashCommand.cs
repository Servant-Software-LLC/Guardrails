using System.CommandLine;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails plan-hash [folder]</c> — print the plan's <c>PlanDefinitionHash</c>
/// (<c>sha256:…</c>, SSOT §12.2, issue #366). A read-only affordance the <c>/guardrails-review</c>
/// skill needs to embed the exact plan hash it reviewed into its attestation (F2a) — the skill
/// cannot compute the hash itself.
///
/// <para>STUB (TDD red): the handler throws <see cref="NotImplementedException"/>. The command is
/// wired into production dispatch (<see cref="CommandFactory.BuildRootCommand"/>) so the failing
/// <c>PlanHashCliTests</c> can drive it through the real factory; the hash-printing body is
/// implemented in the follow-up task, not here.</para>
/// </summary>
public static class PlanHashCommand
{
    public static Command Create(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        var command = new Command(
            "plan-hash",
            "Print the plan's PlanDefinitionHash (sha256:…) — read-only.");
        command.Add(folderArgument);

        command.SetAction(parseResult =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out);
            return Run(folder, io);
        });

        return command;
    }

    // TODO(#366): load the plan and write PlanDefinitionHash.Compute(plan) to io.Out. Not
    // implemented yet — PlanHashCliTests are RED against this stub by design.
    private static int Run(string folder, IConsoleIo io) => throw new NotImplementedException();
}
