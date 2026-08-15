using System.Diagnostics;
using Guardrails.Core.Io;

namespace Guardrails.Integration.Tests;

/// <summary>
/// A throwaway git repository holding a <c>.claude/skills</c> directory — the fixture behind the issue
/// #461 tests, which turn on a distinction only real git can answer: is this directory an INSTALL, or is
/// it authored SOURCE under version control?
/// <para>
/// Deliberately a REAL repo rather than a faked probe. The whole defect was a category error about a
/// directory's relationship to git, and the harness reads that relationship by running git — so a test
/// that stubs the answer would restate the assumption instead of checking it. Cross-platform: <c>git</c>
/// is already a hard dependency of this suite (dozens of tests init repos), and identity is configured
/// locally so a machine with no global git identity still commits.
/// </para>
/// </summary>
public sealed class TempSkillsRepo : IDisposable
{
    private readonly string _root;

    public TempSkillsRepo()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-skills-repo-" + Guid.NewGuid().ToString("N"));
        RepoPath = Path.Combine(_root, "repo");
        Directory.CreateDirectory(RepoPath);

        Git("init");
        Git("config", "user.email", "test@guardrails.local");
        Git("config", "user.name", "Guardrails Test");
        File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# Test repo");
        Git("add", ".");
        Git("commit", "-m", "Initial commit");
    }

    /// <summary>The repository's working-tree root.</summary>
    public string RepoPath { get; }

    /// <summary>The skills directory inside the repo — the shape this repo itself has.</summary>
    public string SkillsRoot => Path.Combine(RepoPath, ".claude", "skills");

    /// <summary>
    /// Author <paramref name="skillNames"/> under <see cref="SkillsRoot"/> and COMMIT them, so git tracks
    /// the directory and the working tree is clean. Returns <see cref="SkillsRoot"/>.
    /// </summary>
    public string CommitSkillsRoot(params string[] skillNames)
    {
        WriteSkills(skillNames);
        Git("add", ".");
        Git("commit", "-m", "Author skills");
        return SkillsRoot;
    }

    /// <summary>
    /// Author <paramref name="skillNames"/> under <see cref="SkillsRoot"/> WITHOUT committing, so the
    /// directory sits inside a repo that tracks nothing under it — an ordinary install location that
    /// happens to be in a working copy. Returns <see cref="SkillsRoot"/>.
    /// </summary>
    public string SkillsRootWithoutCommitting(params string[] skillNames)
    {
        WriteSkills(skillNames);
        return SkillsRoot;
    }

    /// <summary>Give a committed skill an uncommitted edit — the state whose loss git cannot undo.</summary>
    public void EditWithoutCommitting(string skillName, string newBody)
    {
        File.WriteAllText(Path.Combine(SkillsRoot, skillName, "SKILL.md"), newBody);
    }

    /// <summary>Add an untracked file inside a tracked skill folder — never committed, so unrecoverable.</summary>
    public void AddUntrackedFile(string skillName, string fileName, string content)
    {
        File.WriteAllText(Path.Combine(SkillsRoot, skillName, fileName), content);
    }

    /// <summary>The current on-disk text of a skill's <c>SKILL.md</c>.</summary>
    public string ReadSkillMd(string skillName) =>
        File.ReadAllText(Path.Combine(SkillsRoot, skillName, "SKILL.md"));

    /// <summary>The authored body written by <see cref="WriteSkills"/> for <paramref name="skillName"/>.</summary>
    public static string AuthoredBody(string skillName) =>
        $"---\nname: {skillName}\ndescription: authored in this repo, not installed\n---\n# {skillName}\n";

    private void WriteSkills(IEnumerable<string> skillNames)
    {
        foreach (string name in skillNames)
        {
            string dir = Path.Combine(SkillsRoot, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), AuthoredBody(name));
        }
    }

    public string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} (in {RepoPath}) exited {proc.ExitCode}: {stderr}");
        }

        return stdout;
    }

    public void Dispose()
    {
        // Issue #109: git marks loose objects read-only on Windows, so a plain recursive delete throws.
        try { SafeDelete.DeleteDirectory(_root); } catch { /* best-effort temp cleanup */ }
    }
}
