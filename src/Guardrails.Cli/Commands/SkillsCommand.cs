using System.CommandLine;

namespace Guardrails.Cli.Commands;

/// <summary>
/// Installs the skill folders bundled inside the tool (next to the entry assembly, under
/// <c>skills/</c>) into Claude Code's skills directory so <c>/plan-breakdown</c> and
/// <c>/guardrail-review</c> become available. Default destination is <c>~/.claude/skills</c>
/// (every repo); <c>--project</c> targets <c>./.claude/skills</c> in the current directory;
/// <c>--target</c> overrides with an explicit path; <c>--force</c> overwrites existing folders.
///
/// Two spellings reach the same action: the canonical noun-verb <c>guardrails skills install</c>
/// (parallel to <c>dotnet tool install</c>), and a hidden verb-noun alias
/// <c>guardrails install skills</c> for muscle memory.
/// </summary>
public static class SkillsCommand
{
    /// <summary>The canonical <c>skills</c> command group (<c>guardrails skills install</c>).</summary>
    public static Command Create(IConsoleIo io)
    {
        var command = new Command("skills", "Manage the Guardrails Claude Code skills bundled with this tool.");
        command.Add(BuildInstallLeaf("install", io));
        return command;
    }

    /// <summary>
    /// Hidden alias so <c>guardrails install skills</c> works too. Same action as
    /// <c>skills install</c>; kept out of <c>--help</c> so there is one canonical spelling.
    /// </summary>
    public static Command CreateInstallAlias(IConsoleIo io)
    {
        var command = new Command("install", "Install bundled resources (alias for 'skills install').")
        {
            Hidden = true
        };
        command.Add(BuildInstallLeaf("skills", io));
        return command;
    }

    /// <summary>
    /// Build a leaf install command with the given name and the shared options + action. Fresh
    /// option instances per call (an Option cannot belong to two commands), so the canonical
    /// leaf and the alias leaf each get their own.
    /// </summary>
    private static Command BuildInstallLeaf(string name, IConsoleIo io)
    {
        var targetOption = new Option<string?>("--target")
        {
            Description = "Explicit directory to install the skills into (overrides the default and --project)."
        };

        var projectOption = new Option<bool>("--project")
        {
            Description = "Install into ./.claude/skills in the current directory (created if missing) instead of the user home."
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Overwrite a skill folder that already exists in the target (otherwise it is skipped)."
        };

        var command = new Command(name, "Install the bundled skills into Claude Code's skills directory.");
        command.Add(targetOption);
        command.Add(projectOption);
        command.Add(forceOption);

        command.SetAction(parseResult =>
        {
            string? target = parseResult.GetValue(targetOption);
            bool project = parseResult.GetValue(projectOption);
            bool force = parseResult.GetValue(forceOption);
            return RunInstall(target, project, force, io);
        });

        return command;
    }

    private static int RunInstall(string? target, bool project, bool force, IConsoleIo io)
    {
        string sourceSkillsDir = Path.Combine(AppContext.BaseDirectory, "skills");
        if (!Directory.Exists(sourceSkillsDir))
        {
            io.Error.WriteLine(
                $"No bundled skills found at '{sourceSkillsDir}'.");
            io.Error.WriteLine(
                "This build of guardrails does not carry its skills. Re-pack/re-install the tool, " +
                "or copy .claude/skills/ manually (see docs/DEPLOYMENT.md).");
            return ExitCodes.HarnessError;
        }

        if (project && !string.IsNullOrWhiteSpace(target))
        {
            io.Error.WriteLine("Specify either --target or --project, not both.");
            return ExitCodes.HarnessError;
        }

        string targetDir = SkillsInstaller.ResolveTargetDir(target, project);

        IReadOnlyList<SkillsInstaller.SkillResult> results =
            SkillsInstaller.InstallAll(sourceSkillsDir, targetDir, force);

        foreach (SkillsInstaller.SkillResult result in results)
        {
            string note = result.Outcome switch
            {
                SkillsInstaller.SkillOutcome.Installed => "installed",
                SkillsInstaller.SkillOutcome.Skipped => "skipped (already present; use --force to overwrite)",
                _ => result.Outcome.ToString()
            };
            io.Out.WriteLine($"  {result.Name,-28} {note}");
        }

        int installed = results.Count(r => r.Outcome == SkillsInstaller.SkillOutcome.Installed);
        int skipped = results.Count(r => r.Outcome == SkillsInstaller.SkillOutcome.Skipped);

        io.Out.WriteLine();
        io.Out.WriteLine($"{installed} skill(s) installed, {skipped} skipped → {targetDir}");
        io.Out.WriteLine("Restart Claude Code; /plan-breakdown and /guardrail-review are then available.");

        return ExitCodes.Success;
    }
}
