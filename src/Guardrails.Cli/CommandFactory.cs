using System.CommandLine;
using Guardrails.Cli.Commands;

namespace Guardrails.Cli;

/// <summary>
/// Builds the fully-wired root command, injecting the <see cref="IConsoleIo"/> output seam
/// into every command factory. <c>Program.cs</c> calls this with
/// <see cref="SystemConsoleIo.Instance"/>; tests call it (or the individual factories) with a
/// <see cref="System.IO.StringWriter"/>-backed double so output is captured per-invocation and
/// no test shares the process-global console.
/// </summary>
public static class CommandFactory
{
    public static RootCommand BuildRootCommand(IConsoleIo io)
    {
        ArgumentNullException.ThrowIfNull(io);

        var rootCommand = new RootCommand("Guardrails — run a plan folder's task DAG to green.");
        rootCommand.Add(ValidateCommand.Create(io));
        rootCommand.Add(MarkReviewedCommand.Create(io));
        rootCommand.Add(RunCommand.Create(io));
        rootCommand.Add(PlanCommand.Create(io));
        rootCommand.Add(GraphCommand.Create(io));
        rootCommand.Add(StatusCommand.Create(io));
        rootCommand.Add(LogsCommand.Create(io));
        rootCommand.Add(ResetCommand.Create(io));
        rootCommand.Add(LockCommand.Create(io));
        rootCommand.Add(MergeCommand.Create(io));
        rootCommand.Add(SkillsCommand.Create(io));
        rootCommand.Add(SkillsCommand.CreateInstallAlias(io));

        WireVersionDriftWarning(rootCommand, io);
        return rootCommand;
    }

    /// <summary>
    /// Replace the built-in <c>--version</c> action with one that, after printing the version to
    /// stdout, warns on stderr about installed skills whose stamped version has drifted from this
    /// harness (issue #152). The version line on stdout is unchanged; the exit code stays 0.
    /// </summary>
    private static void WireVersionDriftWarning(RootCommand rootCommand, IConsoleIo io)
    {
        VersionOption? versionOption = rootCommand.Options.OfType<VersionOption>().FirstOrDefault();
        if (versionOption is not null)
        {
            versionOption.Action = new VersionWithDriftAction(io);
        }
    }
}
