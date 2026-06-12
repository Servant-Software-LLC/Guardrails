using System.CommandLine;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails skills install [--target &lt;dir&gt;] [--force]</c> — copy the skill folders
/// bundled inside the tool (next to the entry assembly, under <c>skills/</c>) into Claude
/// Code's user skills directory so <c>/plan-breakdown</c> and <c>/guardrail-review</c> become
/// available in any repo. <c>--target</c> defaults to <c>~/.claude/skills</c>; <c>--force</c>
/// overwrites an already-present skill folder (without it, it is skipped with a note).
/// </summary>
public static class SkillsCommand
{
    public static Command Create()
    {
        var command = new Command("skills", "Manage the Guardrails Claude Code skills bundled with this tool.");
        command.Add(CreateInstall());
        return command;
    }

    private static Command CreateInstall()
    {
        var targetOption = new Option<string?>("--target")
        {
            Description = "Directory to install the skills into (default: ~/.claude/skills)."
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Overwrite a skill folder that already exists in the target (otherwise it is skipped)."
        };

        var command = new Command("install", "Install the bundled skills into Claude Code's user skills directory.");
        command.Add(targetOption);
        command.Add(forceOption);

        command.SetAction(parseResult =>
        {
            string? target = parseResult.GetValue(targetOption);
            bool force = parseResult.GetValue(forceOption);
            return RunInstall(target, force);
        });

        return command;
    }

    private static int RunInstall(string? target, bool force)
    {
        string sourceSkillsDir = Path.Combine(AppContext.BaseDirectory, "skills");
        if (!Directory.Exists(sourceSkillsDir))
        {
            Console.Error.WriteLine(
                $"No bundled skills found at '{sourceSkillsDir}'.");
            Console.Error.WriteLine(
                "This build of guardrails does not carry its skills. Re-pack/re-install the tool, " +
                "or copy .claude/skills/ manually (see docs/DEPLOYMENT.md).");
            return ExitCodes.HarnessError;
        }

        string targetDir = ResolveTarget(target);

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
            Console.WriteLine($"  {result.Name,-28} {note}");
        }

        int installed = results.Count(r => r.Outcome == SkillsInstaller.SkillOutcome.Installed);
        int skipped = results.Count(r => r.Outcome == SkillsInstaller.SkillOutcome.Skipped);

        Console.WriteLine();
        Console.WriteLine($"{installed} skill(s) installed, {skipped} skipped → {targetDir}");
        Console.WriteLine("Restart Claude Code; /plan-breakdown and /guardrail-review are then available.");

        return ExitCodes.Success;
    }

    /// <summary>
    /// Resolve the install target: the explicit <c>--target</c> when given, otherwise
    /// <c>~/.claude/skills</c> via <see cref="Environment.SpecialFolder.UserProfile"/>
    /// (cross-platform).
    /// </summary>
    private static string ResolveTarget(string? target)
    {
        if (!string.IsNullOrWhiteSpace(target))
        {
            return target;
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".claude", "skills");
    }
}
