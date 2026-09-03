using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Guardrails.Core.Loading;

namespace Guardrails.Core.Tests;

/// <summary>
/// GR2071 (issue #587 check A) — a task's prompt instructs a shell command its own <c>allowedTools</c>
/// refuse. Every fixture is a real plan folder in a temp dir — a <c>guardrails.json</c> carrying a
/// <c>promptRunners</c> block with real <c>allowedTools</c>, and a task with a real
/// <c>action.prompt.md</c> — run through <see cref="PlanValidator.Validate"/>.
///
/// <para><b>Two positive controls, and the second is the one that runs in CI.</b>
/// <see cref="RecoveredFromGit_Plan33Task09_FiresOnceOnGitLsTree"/> reads the ACTUAL bytes of plan 33 task
/// 09's prompt and config at <c>2281ece^</c> — the commit before the fix — and is the proof that this check
/// would have caught the defect it was built for. It skips on a shallow clone, so
/// <see cref="MeasuredDefect_Plan33Task09_Verbatim"/> re-states the same sentence and the same grant array
/// inline and runs everywhere; the git test asserts the inline copy appears VERBATIM in the recovered
/// bytes, so the fixture cannot drift from history while the suite stays green.</para>
///
/// <para><b>The third positive control was found by the corpus sweep, not by the issue.</b>
/// <see cref="MeasuredDefect_Plan33Task02_FencedPipeline"/> is plan 33 task 02's live, uncorrected
/// "Verify that count yourself before and after:" fence — <c>grep -rn … | wc -l</c> against grants holding
/// neither <c>grep</c> nor <c>wc</c>. It is the reason the fence arm and the segment split exist at all;
/// the inline-only version of this check was silent on it.</para>
///
/// <para><b>MOST of these tests are FALSE-POSITIVE guards</b>, for the reason
/// <c>GuardrailRequiresForbiddenTokenTests</c> gives: a lint nobody trusts loses its true positives along
/// with its false ones. The measured false-positive class this check exists to refuse is a prompt
/// describing what the ARTIFACT the agent authors must do — see
/// <see cref="InstructionAboutTheAuthoredArtifact_StaysSilent"/>, which is the shape of all five findings
/// the check produced over the corpus before the second-person narrowing landed.</para>
/// </summary>
[Collection(GitEnvironmentCollection.Name)]
public sealed class PromptToolGrantCoverageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gr2071-").FullName;

    /// <summary>The read-only grant list plan 33 shipped, and the one the measured defect ran against.</summary>
    private static readonly string[] ReadOnlyGitGrants =
    [
        "Read", "Grep", "Glob", "Write", "Edit",
        "Bash(dotnet *)", "Bash(git log*)", "Bash(git diff*)", "Bash(git show*)", "Bash(git status*)"
    ];

    /// <summary>
    /// The measured sentence, verbatim from plan 33 task 09's prompt at <c>2281ece^</c>, hard-wrapped
    /// exactly as it was committed — the wrap is load-bearing, because "you" and "enumerate them with" sit
    /// on different physical lines and a line-scoped second-person test would miss it.
    /// </summary>
    private const string Task09Sentence =
        "**The population is ALL 850 COMMITTED `.ps1` under `docs/plans/`, waved folders included — and you\n" +
        "enumerate them with `git ls-tree`, never by walking the working tree.** A working-tree walk finds";

    /// <summary>The measured fence, verbatim from plan 33 task 02's prompt at HEAD.</summary>
    private const string Task02Fence =
        "**The N3 gate — read this before you touch the constructor.** There are **73** `new PlanValidator(`\n" +
        "call sites across `tests/` and `src/Guardrails.Cli`. Verify that count yourself before and after:\n" +
        "\n" +
        "```\n" +
        "grep -rn \"new PlanValidator(\" src tests --include=*.cs | wc -l\n" +
        "```";

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    // ── the code itself ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCodeIsGr2071() =>
        Assert.Equal("GR2071", DiagnosticCodes.PromptInstructsUngrantedCommand);

    // ── positive controls ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE MEASURED INSTANCE, recovered from git: plan 33 task 09's prompt and <c>guardrails.json</c> as
    /// they stood at <c>2281ece^</c>, the parent of "fix(33): the prompt told task 09 to run a command I
    /// never granted". EXACTLY ONE finding — the prompt also names <c>git show &lt;commit&gt;:&lt;path&gt;</c>
    /// and <c>git diff</c>, both granted, so a check that fired twice would be reporting prose.
    ///
    /// <para>Skips only when git cannot answer for that commit (no git on PATH, or a shallow clone).
    /// <see cref="MeasuredDefect_Plan33Task09_Verbatim"/> carries the same assertion without git, so the
    /// behaviour is still gated on every machine; what this test adds is that the inline copy is FAITHFUL.</para>
    /// </summary>
    [Fact]
    public void RecoveredFromGit_Plan33Task09_FiresOnceOnGitLsTree()
    {
        const string Defect = "2281ece^";
        const string Folder = "docs/plans/33-unproducible-requirements";

        Assert.SkipUnless(GitCanRead(Defect),
            $"git cannot read {Defect} here (no git on PATH, or a shallow clone) — the verbatim twin covers it.");

        string prompt = GitShow($"{Defect}:{Folder}/tasks/09-author-corpus-sweep/action.prompt.md");
        string config = GitShow($"{Defect}:{Folder}/guardrails.json");

        // The inline fixtures must be what history actually holds, or the always-running twin proves nothing.
        Assert.Contains(Task09Sentence.Replace("\n", "\n", StringComparison.Ordinal), Normalize(prompt),
            StringComparison.Ordinal);
        Assert.Contains("\"Bash(git log*)\"", config, StringComparison.Ordinal);
        Assert.DoesNotContain("git ls-tree*", config, StringComparison.Ordinal);

        string plan = PlanFolder("recovered", config, ("09-author-corpus-sweep", prompt));

        Diagnostic d = Assert.Single(Findings(plan));
        Assert.Contains("git ls-tree", d.Message, StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
    }

    /// <summary>
    /// The same defect, inline: the committed sentence against the committed grants. Runs everywhere.
    /// The finding must name the command, the LINE, and the grants — a reader told only "something is
    /// ungranted" still has to find the contradiction, and finding it is the whole difficulty.
    /// </summary>
    [Fact]
    public void MeasuredDefect_Plan33Task09_Verbatim()
    {
        string plan = Plan(Task09Sentence, ReadOnlyGitGrants);

        Diagnostic d = Assert.Single(Findings(plan));
        Assert.Contains("git ls-tree", d.Message, StringComparison.Ordinal);
        Assert.Contains("line 2", d.Message, StringComparison.Ordinal);
        Assert.Contains("Bash(git log*)", d.Message, StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
    }

    /// <summary>
    /// The second real defect — LIVE at HEAD when this check was written, and found by the corpus sweep
    /// rather than by the issue. A colon-introduced fence the prompt hands over, holding a pipeline whose
    /// BOTH halves are ungranted. One finding, naming both segments: one authoring defect, one fix.
    /// </summary>
    [Fact]
    public void MeasuredDefect_Plan33Task02_FencedPipeline()
    {
        string plan = Plan(Task02Fence, ReadOnlyGitGrants);

        Diagnostic d = Assert.Single(Findings(plan));
        Assert.Contains("grep -rn", d.Message, StringComparison.Ordinal);
        Assert.Contains("wc -l", d.Message, StringComparison.Ordinal);
        Assert.Contains("SPLITS a compound", d.Message, StringComparison.Ordinal);
    }

    // ── the grant comparison, both polarities ─────────────────────────────────────────────────────

    [Fact]
    public void UngrantedInlineCommand_Fires() =>
        Assert.Single(Findings(Plan("You should run `git ls-tree -r HEAD` first.", ReadOnlyGitGrants)));

    [Fact]
    public void GrantedInlineCommand_StaysSilent() =>
        Assert.Empty(Findings(Plan("You should run `git log --oneline` first.", ReadOnlyGitGrants)));

    /// <summary>
    /// The HARNESS-injected grant counts. <c>ClaudePromptRunner</c> adds <c>Bash(git show*)</c> to every
    /// invocation, so a prompt naming <c>git show</c> is instructing something that really does run — and a
    /// check reading only the DECLARED list would report a wall that does not exist. This test is the pin
    /// that the effective set is what the comparison uses.
    /// </summary>
    [Fact]
    public void HarnessInjectedGitShowGrant_IsHonoured() =>
        Assert.Empty(Findings(Plan(
            "You can read it with `git show HEAD:src/Program.cs`.",
            ["Read", "Write", "Bash(dotnet *)"])));

    /// <summary>The <c>Bash(git show:*)</c> colon spelling is the same grant and must match the same command.</summary>
    [Fact]
    public void ColonGlobSpelling_Matches() =>
        Assert.Empty(Findings(Plan(
            "You should run `git status --porcelain` first.",
            ["Read", "Bash(git status:*)"])));

    // ── the two silence gates ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gate 1 — an UNCONSTRAINED task cannot violate a grant. The prompt names a command no grant permits,
    /// and the check must still say nothing, because the plan declared no tool policy at all.
    /// </summary>
    [Fact]
    public void NoDeclaredAllowedTools_StaysSilent() =>
        Assert.Empty(Findings(Plan("You should run `git ls-tree -r HEAD` first.", [])));

    /// <summary>
    /// Gate 2 — a declared list with no <c>Bash(...)</c> entry expresses no SHELL policy. Since
    /// <c>allowedTools</c> is a floor and not a ceiling (#252), measuring prose against a policy nobody
    /// wrote would report a wall the operator's own settings may not have.
    /// </summary>
    [Fact]
    public void NoBashGrantDeclared_StaysSilent() =>
        Assert.Empty(Findings(Plan("You should run `git ls-tree -r HEAD` first.", ["Read", "Write", "Edit"])));

    [Fact]
    public void UnscopedBashGrant_StaysSilent() =>
        Assert.Empty(Findings(Plan("You should run `git ls-tree -r HEAD` first.", ["Read", "Bash"])));

    [Fact]
    public void BashStarGrant_StaysSilent() =>
        Assert.Empty(Findings(Plan("You should run `git ls-tree -r HEAD` first.", ["Read", "Bash(*)"])));

    // ── the false-positive guards ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The commonest inline-command shape in the whole corpus — 45% of a stratified sample — and pure
    /// prose ABOUT a command the agent is not being asked to run. Third-person "runs" is deliberately not
    /// an instruction trigger, which is what keeps this silent.
    /// </summary>
    [Fact]
    public void ProseAboutWhatTheHarnessDoes_StaysSilent() =>
        Assert.Empty(Findings(Plan(
            "You write only that path. After this task the harness runs a `git ls-tree` check on your work.",
            ReadOnlyGitGrants)));

    /// <summary>
    /// The shape the FIX for the measured defect wrote into the very same file. A check that fired on the
    /// remediation for its own motivating bug would be worse than no check.
    /// </summary>
    [Fact]
    public void ProhibitionExample_StaysSilent() =>
        Assert.Empty(Findings(Plan(
            "You must not use a pipeline: `git ls-tree -r HEAD | grep foo` is refused, because `grep` is\n" +
            "not a granted binary.",
            ReadOnlyGitGrants)));

    /// <summary>
    /// THE load-bearing guard. All five findings this check produced over the committed corpus before the
    /// second-person narrowing were this shape: the prompt instructs the agent about what the ARTIFACT it
    /// authors must do. The imperative is perfect, the command is ungranted, and the agent must never run
    /// it — the subject of the clause is the test, not the agent.
    /// </summary>
    [Fact]
    public void InstructionAboutTheAuthoredArtifact_StaysSilent() =>
        Assert.Empty(Findings(Plan(
            "Whatever fixture helper the suite writes inline must:\n" +
            "\n" +
            "- For rollback use `git ls-tree -r HEAD`, and clean up in the teardown.",
            ReadOnlyGitGrants)));

    /// <summary>A backticked tool NAME is a noun. Without the verb requirement this reads as a command.</summary>
    [Fact]
    public void BareBinaryInBackticks_StaysSilent() =>
        Assert.Empty(Findings(Plan("You may use `git` here, or the Grep tool.", ReadOnlyGitGrants)));

    /// <summary>A backticked PATH is not a command, however imperative the sentence around it.</summary>
    [Fact]
    public void BacktickedPath_StaysSilent() =>
        Assert.Empty(Findings(Plan("You should run the sweep with `docs/plans/33-x/tasks`.", ReadOnlyGitGrants)));

    /// <summary>
    /// A fence carrying an ARTIFACT the task must author, not a command it must run. The language tag is
    /// what refuses it — and it is refused even though the introducer is colon-terminated and the paragraph
    /// addresses the agent, because those two alone describe every "write this file:" hand-over as well.
    /// </summary>
    [Fact]
    public void FenceCarryingAnAuthoredArtifact_StaysSilent() =>
        Assert.Empty(Findings(Plan(
            "You write this guardrail:\n" +
            "\n" +
            "```csharp\n" +
            "git ls-tree -r HEAD\n" +
            "```",
            ReadOnlyGitGrants)));

    /// <summary>An untagged fence with no colon-terminated introducer is not a hand-over.</summary>
    [Fact]
    public void FenceWithNoColonIntroducer_StaysSilent() =>
        Assert.Empty(Findings(Plan(
            "You are writing the sweep.\n" +
            "\n" +
            "```\n" +
            "git ls-tree -r HEAD\n" +
            "```",
            ReadOnlyGitGrants)));

    /// <summary>A hand-over shape in a paragraph that never addresses the agent stays silent.</summary>
    [Fact]
    public void FenceIntroducedWithoutSecondPerson_StaysSilent() =>
        Assert.Empty(Findings(Plan(
            "The sweep enumerates scripts like this:\n" +
            "\n" +
            "```\n" +
            "git ls-tree -r HEAD\n" +
            "```",
            ReadOnlyGitGrants)));

    // ── compound splitting ────────────────────────────────────────────────────────────────────────

    /// <summary>Every half granted ⇒ the pipeline really does run, so a "pipes are always refused" rule would be wrong.</summary>
    [Fact]
    public void PipelineWithBothHalvesGranted_StaysSilent() =>
        Assert.Empty(Findings(Plan(
            "You should run `git log --oneline | grep fix` first.",
            ["Read", "Bash(git log*)", "Bash(grep*)"])));

    /// <summary>One ungranted half is a refusal of the whole command; the finding names the half.</summary>
    [Fact]
    public void PipelineWithOneUngrantedHalf_FiresNamingThatHalf()
    {
        Diagnostic d = Assert.Single(Findings(Plan(
            "You should run `git log --oneline | grep fix` first.",
            ["Read", "Bash(git log*)"])));

        Assert.Contains("grep fix", d.Message, StringComparison.Ordinal);
        Assert.Contains("SPLITS a compound", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AndChainedCompoundWithAnUngrantedHalf_Fires() =>
        Assert.Single(Findings(Plan(
            "You should run `git log --oneline && git ls-tree -r HEAD` first.", ReadOnlyGitGrants)));

    /// <summary>
    /// A <c>|</c> inside a quoted argument is an alternation, not a shell operator. Splitting on it would
    /// manufacture a segment (<c>Usage" GuardrailRunner.cs</c>) that no grant could ever match — a finding
    /// invented entirely by the splitter. The corpus carries exactly this shape.
    /// </summary>
    [Fact]
    public void PipeInsideQuotes_IsNotAnOperator() =>
        Assert.Empty(Findings(Plan(
            "You should run `grep \"CostUsd\\|Usage\" GuardrailRunner.cs` first.",
            ["Read", "Bash(grep*)"])));

    /// <summary>A redirect carries a single <c>&amp;</c> and must not split; only the doubled form is an operator.</summary>
    [Fact]
    public void RedirectIsNotAChain() =>
        Assert.Empty(Findings(Plan(
            "You should run `dotnet build 2>&1` first.", ["Read", "Bash(dotnet *)"])));

    // ── scope, resolution and shape ───────────────────────────────────────────────────────────────

    /// <summary>One defect, one finding — a prompt that repeats an instruction is not three defects.</summary>
    [Fact]
    public void RepeatedInstruction_IsReportedOnce() =>
        Assert.Single(Findings(Plan(
            "You should run `git ls-tree -r HEAD` first.\n" +
            "\n" +
            "Then you run `git ls-tree -r HEAD` again to confirm.",
            ReadOnlyGitGrants)));

    /// <summary>
    /// An <c>action.runner</c> pin selects a DIFFERENT block, and the check must measure against that
    /// block's grants — the pin is the only per-task override of <c>allowedTools</c> the schema has.
    /// </summary>
    [Fact]
    public void ActionRunnerPin_SelectsThatRunnersGrants()
    {
        string config = $$"""
            {
              "version": 1,
              "maxParallelism": 1,
              "promptRunners": {
                "default": "wide",
                "wide": { "command": "claude", "allowedTools": ["Read", "Bash(git *)"] },
                "narrow": { "command": "claude", "allowedTools": ["Read", "Bash(dotnet *)"] }
              }
            }
            """;

        string onWide = PlanFolder("wide-plan", config,
            ("01-x", "You should run `git ls-tree -r HEAD` first."));
        Assert.Empty(Findings(onWide));

        string onNarrow = PlanFolder("narrow-plan", config,
            ("01-x", "You should run `git ls-tree -r HEAD` first."), runner: "narrow");
        Assert.Single(Findings(onNarrow));
    }

    /// <summary>
    /// <c>guardrailOverrides.allowedTools</c> governs prompt GUARDRAILS, not the action. Reading it here
    /// would measure the action prompt against a grant list it never runs on — silently, and in the
    /// direction that produces a confident wrong finding.
    /// </summary>
    [Fact]
    public void GuardrailOverrides_DoNotGovernTheActionPrompt()
    {
        string config = """
            {
              "version": 1,
              "maxParallelism": 1,
              "promptRunners": {
                "claude": {
                  "command": "claude",
                  "allowedTools": ["Read", "Bash(git *)"],
                  "guardrailOverrides": { "allowedTools": ["Read", "Bash(dotnet *)"] }
                }
              }
            }
            """;

        Assert.Empty(Findings(PlanFolder("overrides", config,
            ("01-x", "You should run `git ls-tree -r HEAD` first."))));
    }

    /// <summary>A SCRIPT action has no prompt to read, and the check must never look at one.</summary>
    [Fact]
    public void ScriptAction_IsNotConsidered()
    {
        string plan = Path.Combine(_root, "script-plan");
        Directory.CreateDirectory(Path.Combine(plan, "tasks", "01-x", "guardrails"));
        File.WriteAllText(Path.Combine(plan, "guardrails.json"), DefaultConfig(ReadOnlyGitGrants));
        File.WriteAllText(Path.Combine(plan, "tasks", "01-x", "task.json"),
            """{ "description": "fixture", "dependsOn": [], "writeScope": [] }""");
        File.WriteAllText(Path.Combine(plan, "tasks", "01-x", "action.sh"),
            "# You should run `git ls-tree -r HEAD` first.\nexit 0\n");
        File.WriteAllText(Path.Combine(plan, "tasks", "01-x", "guardrails", "01-verifies.sh"),
            "# catches: a change that was never verified\nexit 0\n");

        Assert.Empty(Findings(plan));
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A one-task plan carrying <paramref name="prompt"/> and <paramref name="grants"/>.</summary>
    private string Plan(string prompt, string[] grants) =>
        PlanFolder("p" + Guid.NewGuid().ToString("N")[..6], DefaultConfig(grants), ("01-x", prompt));

    private static string DefaultConfig(string[] grants) => $$"""
        {
          "version": 1,
          "maxParallelism": 1,
          "promptRunners": {
            "claude": { "command": "claude", "allowedTools": {{JsonSerializer.Serialize(grants)}} }
          }
        }
        """;

    /// <summary>
    /// Write a plan folder: the given <c>guardrails.json</c> verbatim plus one prompt task per entry.
    /// <c>maxParallelism: 1</c> in every config keeps GR2015 (workspace must be a git root) out of the
    /// diagnostic list, so a fixture's findings are a function of the fixture and not of where TMP lives.
    /// </summary>
    private string PlanFolder(
        string name,
        string config,
        params (string Id, string Prompt)[] tasks) => PlanFolder(name, config, tasks, runner: null);

    private string PlanFolder(string name, string config, (string Id, string Prompt) task, string runner)
        => PlanFolder(name, config, [task], runner);

    private string PlanFolder(
        string name,
        string config,
        (string Id, string Prompt)[] tasks,
        string? runner)
    {
        string planDirectory = Path.Combine(_root, name);
        Directory.CreateDirectory(planDirectory);
        File.WriteAllText(Path.Combine(planDirectory, "guardrails.json"), config);

        foreach ((string id, string prompt) in tasks)
        {
            string taskDirectory = Path.Combine(planDirectory, "tasks", id);
            Directory.CreateDirectory(Path.Combine(taskDirectory, "guardrails"));

            string action = runner is null
                ? """{ "kind": "prompt" }"""
                : $$"""{ "kind": "prompt", "runner": "{{runner}}" }""";

            File.WriteAllText(Path.Combine(taskDirectory, "task.json"), $$"""
                {
                  "description": "fixture task",
                  "dependsOn": [],
                  "writeScope": [],
                  "action": {{action}}
                }
                """);

            File.WriteAllText(Path.Combine(taskDirectory, "action.prompt.md"), prompt);
            File.WriteAllText(Path.Combine(taskDirectory, "guardrails", "01-verifies.sh"),
                "# catches: a change that was never verified\nexit 0\n");
        }

        return planDirectory;
    }

    /// <summary>Load, validate, and keep only GR2071 — every other code is another check's business.</summary>
    private static IReadOnlyList<Diagnostic> Findings(string planDirectory)
    {
        PlanLoadResult result = new PlanLoader().Load(planDirectory);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.NotNull(result.Plan);

        return
        [
            .. new PlanValidator(
                    FakeExecutableProbe.All,
                    BannedPatternRegistry.Load(),
                    NullScriptSyntaxProbe.Instance)
                .Validate(result.Plan)
                .Where(d => d.Code == DiagnosticCodes.PromptInstructsUngrantedCommand)
        ];
    }

    // ── git recovery ──────────────────────────────────────────────────────────────────────────────

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static bool GitCanRead(string commit) =>
        RunGit("--version").ExitCode == 0 && RunGit("cat-file", "-e", commit + "^{commit}").ExitCode == 0;

    private static string GitShow(string spec)
    {
        (int exitCode, string stdout, string stderr) = RunGit("show", spec);
        Assert.True(exitCode == 0, $"git show {spec} exited {exitCode}: {stderr}");
        return stdout;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunGit(params string[] arguments)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (string argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        psi.Environment.Remove("GIT_DIR");
        psi.Environment.Remove("GIT_WORK_TREE");

        try
        {
            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return (-1, string.Empty, "git could not be started.");
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdout, stderr);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    /// <summary>
    /// Anchored on the SOURCE path (<see cref="TestPaths.ProjectDir"/> is a <c>[CallerFilePath]</c>), which
    /// is the same anchor <c>ProducerCoverageCorpusTests</c> uses and is the only one that survives a git
    /// WORKTREE. The previous form walked up from <c>AppContext.BaseDirectory</c> looking for a <c>.git</c>
    /// DIRECTORY — but in a worktree <c>.git</c> is a FILE holding a <c>gitdir:</c> pointer, so the walk ran
    /// to the filesystem root and asserted on a null parent. That made every recovered-from-git control in
    /// this file unrunnable in exactly the setup this repository dogfoods in (worktree-per-task, plan 08),
    /// and it hid behind a shallow-clone SKIP in CI, so nothing reported it.
    /// </summary>
    private static readonly string RepositoryRoot =
        Path.GetFullPath(Path.Combine(TestPaths.ProjectDir, "..", ".."));
}
