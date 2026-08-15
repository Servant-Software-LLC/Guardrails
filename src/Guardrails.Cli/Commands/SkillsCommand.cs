using System.CommandLine;
using Guardrails.Core.Io;

namespace Guardrails.Cli.Commands;

/// <summary>
/// Installs the skill folders bundled inside the tool (next to the entry assembly, under
/// <c>skills/</c>) into Claude Code's skills directory so <c>/plan-breakdown</c> and
/// <c>/guardrails-review</c> become available. Default destination is <c>~/.claude/skills</c>
/// (every repo); <c>--project</c> targets <c>./.claude/skills</c> in the current directory;
/// <c>--target</c> overrides with an explicit path; <c>--force</c> overwrites existing folders — except
/// where that would destroy authored source, which <c>--overwrite-tracked</c> alone unlocks (issue #461,
/// see <see cref="TrackedSourceCheckPasses"/>).
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
            Description = "Overwrite a skill folder that already exists in the target — required to refresh "
                + "an installed skill (and remove any stale version sidecar), otherwise it is skipped. "
                + "`guardrails --version` warns about skipped/stale skills."
        };

        var overwriteTrackedOption = new Option<bool>("--overwrite-tracked")
        {
            Description = "Allow --force to replace destination skill folders that git TRACKS and that have "
                + "uncommitted changes. Without it such an install is REFUSED, because those folders are "
                + "authored source, not an install (issue #461)."
        };

        var command = new Command(name, "Install the bundled skills into Claude Code's skills directory.");
        command.Add(targetOption);
        command.Add(projectOption);
        command.Add(forceOption);
        command.Add(overwriteTrackedOption);

        command.SetAction(parseResult =>
        {
            string? target = parseResult.GetValue(targetOption);
            bool project = parseResult.GetValue(projectOption);
            bool force = parseResult.GetValue(forceOption);
            bool overwriteTracked = parseResult.GetValue(overwriteTrackedOption);
            return RunInstall(target, project, force, overwriteTracked, io);
        });

        return command;
    }

    private static int RunInstall(string? target, bool project, bool force, bool overwriteTracked, IConsoleIo io)
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
        string toolVersion = GuardrailsVersion.Current;

        if (force && !TrackedSourceCheckPasses(sourceSkillsDir, targetDir, toolVersion, overwriteTracked, io))
        {
            return ExitCodes.HarnessError;
        }

        IReadOnlyList<SkillsInstaller.SkillResult> results =
            SkillsInstaller.InstallAll(sourceSkillsDir, targetDir, force, toolVersion);

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
        io.Out.WriteLine($"{installed} skill(s) installed (v{toolVersion}), {skipped} skipped → {targetDir}");
        io.Out.WriteLine("Restart Claude Code; /plan-breakdown and /guardrails-review are then available.");

        return ExitCodes.Success;
    }

    /// <summary>
    /// Issue #461 — the guard between installing a payload and destroying source. A <c>--force</c> install
    /// DELETES each destination skill folder before copying the bundled one in, and the bundled one is
    /// reconstructable from any tool of the same version. A git-tracked destination is not: it is where
    /// someone AUTHORS skills, and this repository is exactly that case (<c>./.claude/skills</c> holds the
    /// skills that get packed into the shipped tool). The two are different operations that happened to
    /// share one flag, and the drift warning steered authors straight into it — most reliably while
    /// mid-edit, since that is when the versions differ.
    /// <para>
    /// So: a destination folder that git tracks AND that has uncommitted changes is REFUSED — that content
    /// exists nowhere else and no <c>git checkout</c> brings it back. Tracked-but-clean is allowed with a
    /// note, because git can restore it and refusing would break the legitimate "refresh my project skills"
    /// flow. Untracked destinations are untouched by this check — the pre-#461 behaviour.
    /// </para>
    /// <para>
    /// The probe answers "no" when git is missing or the destination is in no repository
    /// (<see cref="GitWorkingTree"/>), so a machine without git installs exactly as it always did.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> to proceed with the install; <c>false</c> when it was refused (message written).</returns>
    private static bool TrackedSourceCheckPasses(
        string sourceSkillsDir, string targetDir, string toolVersion, bool overwriteTracked, IConsoleIo io)
    {
        List<string> tracked = SkillsInstaller
            .PlannedOverwrites(sourceSkillsDir, targetDir)
            .Where(GitWorkingTree.TracksAnyFileUnder)
            .ToList();

        if (tracked.Count == 0)
        {
            return true;
        }

        List<string> dirty = tracked.Where(GitWorkingTree.HasLocalChangesUnder).ToList();

        if (dirty.Count > 0 && !overwriteTracked)
        {
            io.Error.WriteLine(
                "Refusing to overwrite git-tracked skill source(s) with uncommitted changes:");
            foreach (string dir in dirty)
            {
                io.Error.WriteLine($"  - {dir}");
            }

            io.Error.WriteLine(
                $"--force would delete each of those folders and replace them with the copy bundled in "
                + $"guardrails v{toolVersion}, destroying work git cannot restore.");
            io.Error.WriteLine(
                "Commit or stash those changes first, or pass --overwrite-tracked to proceed anyway.");
            return false;
        }

        foreach (string dir in tracked)
        {
            io.Error.WriteLine(
                $"Note: overwriting git-tracked skill source '{dir}' with the v{toolVersion} bundled copy; "
                + "`git diff` shows the change and `git checkout --` reverts it.");
        }

        return true;
    }
}
