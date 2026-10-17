using System.Text.RegularExpressions;
using Guardrails.Core.Execution;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #816's write-time half: the generated write-scope PreToolUse hook, run as the REAL generated script
/// standalone with synthetic <c>PreToolUse</c> stdin (no <c>claude</c> binary), through the same
/// <see cref="InterpreterMap"/> + <see cref="ProcessRunner"/> the harness uses for any script — the pattern
/// <see cref="WorktreeContainmentHookTests"/> established. An out-of-scope <c>Edit</c>/<c>Write</c> inside the
/// workspace is refused with a message naming the scope and the needsHuman door; an in-scope write, the
/// harness-provisioned folders, a path outside the workspace, and every Bash call are allowed.
/// </summary>
public sealed class WriteScopeHookTests : IDisposable
{
    private static readonly string[] Scope = ["src/Impl.cs", "src/Feature/", "docs/*.md"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gr-wsh-" + Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly string _logDir;

    public WriteScopeHookTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        _logDir = Path.Combine(_root, "logs");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_logDir);
        WriteScopeHook.WriteScript(_logDir, new WriteScopeHook.Spec(_workspace, Scope));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    // --- refused ------------------------------------------------------------------------------

    [Theory]
    [InlineData("Edit", "tests/UpstreamTests.cs")]
    [InlineData("Write", "tests/UpstreamTests.cs")]
    [InlineData("MultiEdit", "src/Other.cs")]
    [InlineData("Write", "src/../tests/UpstreamTests.cs")] // a '..' walk back out of an in-scope folder
    [InlineData("Write", "src/FeatureX/Z.cs")]             // a sibling of a directory entry is not under it
    [InlineData("Write", "docs/deep/notes.md")]            // '*' stays within one segment
    public async Task AnOutOfScopeWriteInsideTheWorkspace_IsRefused_NamingTheScopeAndTheNeedsHumanDoor(
        string tool, string relative)
    {
        (int exit, string stderr) = await RunAsync(tool, "file_path", Path.Combine(_workspace, relative));

        Assert.Equal(2, exit);
        Assert.Contains("write-scope hook", stderr);
        Assert.Contains("outside this task's writeScope", stderr);
        Assert.Contains("src/Impl.cs, src/Feature/, docs/*.md", stderr);
        Assert.Contains("needsHuman", stderr);
    }

    [Fact]
    public async Task ARelativeOutOfScopePath_IsResolvedAgainstTheWorkspace_AndRefused()
    {
        (int exit, string stderr) = await RunAsync("Edit", "file_path", "tests/UpstreamTests.cs");

        Assert.Equal(2, exit);
        Assert.Contains("'tests/UpstreamTests.cs'", stderr);
    }

    [Fact]
    public async Task AnOutOfScopeNotebookEdit_IsRefused()
    {
        (int exit, _) = await RunAsync("NotebookEdit", "notebook_path", Path.Combine(_workspace, "analysis.ipynb"));

        Assert.Equal(2, exit);
    }

    // --- allowed -------------------------------------------------------------------------------

    [Theory]
    [InlineData("src/Impl.cs")]
    [InlineData("SRC/impl.CS")]                 // WriteScope.IsInScope is case-insensitive; so is the hook
    [InlineData("src/Feature/Deep/Thing.cs")]   // a directory entry covers its whole subtree
    [InlineData("docs/readme.md")]
    public async Task AnInScopeWrite_IsAllowed(string relative)
    {
        (int exit, string stderr) = await RunAsync("Write", "file_path", Path.Combine(_workspace, relative));

        Assert.True(exit == 0, $"expected allow for '{relative}', got exit {exit}: {stderr}");
    }

    [Theory]
    [InlineData(".guardrails-agent-io/01-impl/attempt-1/action-out-fragment.json")] // the staged GUARDRAILS_STATE_OUT
    [InlineData(".guardrails-staging/01-impl/skill/SKILL.md")]                     // the stagingOutputs staging tree
    public async Task AHarnessProvisionedPath_IsAllowed_WhateverTheScopeSays(string relative)
    {
        (int exit, string stderr) = await RunAsync("Write", "file_path", Path.Combine(_workspace, relative));

        Assert.True(exit == 0, $"expected allow for '{relative}', got exit {exit}: {stderr}");
    }

    [Fact]
    public async Task APathOutsideTheWorkspace_IsNotThisHooksQuestion()
    {
        // Containment is the containment hook's job (worktree mode); this hook answers only "inside the
        // workspace, is it in scope?".
        (int exit, _) = await RunAsync("Write", "file_path", Path.Combine(_root, "elsewhere", "scratch.txt"));

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task ABashCall_IsNeverPolicedByThisHook()
    {
        // Builds and test runs write outside the scope legitimately; the retrospective check covers Bash.
        (int exit, _) = await RunAsync(
            "Bash", "command", "echo x > tests/UpstreamTests.cs");

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task AnEmptyScope_RefusesEveryWorkspaceWrite_ButStillAllowsTheStateOutFragment()
    {
        string emptyLogDir = Path.Combine(_root, "logs-empty");
        WriteScopeHook.WriteScript(emptyLogDir, new WriteScopeHook.Spec(_workspace, []));

        (int refused, string stderr) = await RunAsync(
            "Write", "file_path", Path.Combine(_workspace, "src", "Impl.cs"), emptyLogDir);
        (int allowed, _) = await RunAsync(
            "Write", "file_path", Path.Combine(_workspace, ".guardrails-agent-io", "t", "attempt-1", "f.json"), emptyLogDir);

        Assert.Equal(2, refused);
        Assert.Contains("EMPTY", stderr);
        Assert.Equal(0, allowed);
    }

    // --- settings composition -----------------------------------------------------------------

    [Fact]
    public void Serial_WriteHookFiles_WritesTheScopeHookAlone_UnderItsOwnSettingsName()
    {
        string logDir = Path.Combine(_root, "logs-serial");
        string settingsPath = WriteScopeHook.WriteHookFiles(logDir, new WriteScopeHook.Spec(_workspace, Scope));

        Assert.Equal(WriteScopeHook.SettingsFileName, Path.GetFileName(settingsPath));
        string json = File.ReadAllText(settingsPath);
        Assert.Contains($"\"matcher\": \"{WriteScopeHook.Matcher}\"", json);
        Assert.Equal(1, MatcherGroups(json));
        Assert.DoesNotContain("containment-hook", json);
        Assert.False(File.Exists(Path.Combine(logDir, WorktreeContainmentHook.SettingsFileName)));
    }

    private static int MatcherGroups(string json) =>
        json.Split("\"matcher\"", StringSplitOptions.None).Length - 1;

    [Fact]
    public void Worktree_ContainmentSettings_CarryBothHooks_InOneSettingsFile()
    {
        // A runner is handed exactly ONE --settings file, so worktree mode composes the scope hook into the
        // containment hook's settings rather than passing a second file.
        string logDir = Path.Combine(_root, "logs-worktree");
        string settingsPath = WorktreeContainmentHook.WriteHookFiles(
            logDir, _workspace, writeScope: new WriteScopeHook.Spec(_workspace, Scope));

        string json = File.ReadAllText(settingsPath);
        Assert.Equal(2, MatcherGroups(json));
        Assert.Contains($"\"matcher\": \"{WorktreeContainmentHook.Matcher}\"", json);
        Assert.Contains($"\"matcher\": \"{WriteScopeHook.Matcher}\"", json);
        string scopeScript = Path.Combine(logDir,
            OperatingSystem.IsWindows() ? WriteScopeHook.ScriptFileNameWindows : WriteScopeHook.ScriptFileNameUnix);
        Assert.True(File.Exists(scopeScript));
        Assert.Contains(scopeScript.Replace("\\", "\\\\"), json);
    }

    [Fact]
    public void Worktree_ContainmentSettings_WithoutAScope_CarryTheContainmentHookAlone()
    {
        // DECLARED CONTROL: with no scope the settings are the pre-#816 containment settings — one group.
        string logDir = Path.Combine(_root, "logs-plain");
        string json = File.ReadAllText(WorktreeContainmentHook.WriteHookFiles(logDir, _workspace, writeScope: null));

        Assert.Equal(1, MatcherGroups(json));
        Assert.Contains($"\"matcher\": \"{WorktreeContainmentHook.Matcher}\"", json);
        Assert.DoesNotContain("write-scope-hook", json);
    }

    // --- one matcher ---------------------------------------------------------------------------

    [Fact]
    public void TheBakedPatterns_AreTheCompiledScope_NotASecondGlobImplementation()
    {
        string script = File.ReadAllText(Path.Combine(_logDir,
            OperatingSystem.IsWindows() ? WriteScopeHook.ScriptFileNameWindows : WriteScopeHook.ScriptFileNameUnix));

        foreach (string pattern in WriteScope.ToAnchoredPatterns(Scope))
        {
            Assert.Contains(pattern, script);
        }
    }

    private Task<(int ExitCode, string StandardError)> RunAsync(
        string tool, string field, string path, string? logDir = null)
    {
        string input = $$"""{"{{field}}":"{{ContainmentHookScript.ForJson(path)}}"}""";
        return RunScriptAsync(logDir ?? _logDir, ContainmentHookScript.ToolCall(tool, input));
    }

    private async Task<(int ExitCode, string StandardError)> RunScriptAsync(string logDir, string toolCallJson)
    {
        string scriptPath = Path.Combine(logDir,
            OperatingSystem.IsWindows() ? WriteScopeHook.ScriptFileNameWindows : WriteScopeHook.ScriptFileNameUnix);

        var interpreterMap = new InterpreterMap(new PathExecutableProbe());
        InterpreterMap.Resolution resolution = interpreterMap.Resolve(scriptPath, []);
        Assert.Equal(InterpreterMap.Status.Resolved, resolution.Status);

        ProcessResult result = await new ProcessRunner().RunAsync(
            resolution.Command!,
            _workspace,
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(30),
            standardInput: toolCallJson,
            stdoutLineSink: null,
            TestContext.Current.CancellationToken);

        return (result.ExitCode, result.StandardError);
    }
}

/// <summary>
/// Issue #816: <see cref="WriteScope.ToAnchoredPatterns"/> is the ONE translation of the write-scope glob rule into
/// regular expressions the hook scripts apply, so it must agree with <see cref="WriteScope.IsInScope"/> on every
/// path. Proved over the matcher's own truth-table shapes plus a seeded generative corpus, compiled with the
/// case-insensitive option both script dialects use.
/// </summary>
public sealed class WriteScopePatternTests
{
    public static TheoryData<string, string> Pairs => new()
    {
        { "src/Feat*/**", "src/OtherDir/Z.cs" },
        { "src/Feat*/**", "src/Feature/Z.cs" },
        { "marks/left*", "marks/left.start" },
        { "marks/left*", "marks/right.start" },
        { "src/**/*.cs", "src/x/secrets.json" },
        { "src/**/*.cs", "src/x/y/Thing.cs" },
        { "src/**/*.cs", "src/Thing.cs" },
        { "src/Feature/**", "src/FeatureX/Z.cs" },
        { "src/Feature/**", "src/Feature/a/b.cs" },
        { "src/Feature", "src/Feature/x.cs" },
        { "src/Feature", "src/Feature" },
        { "src/Feature/", "src/Feature/x/y.cs" },
        { "src/Foo.Bar/", "src/Foo.Bar/x.cs" },
        { "src/Thing.cs", "src/Thing.cs" },
        { "src/Thing.cs", "SRC/thing.CS" },
        { "src/Thing.cs", "src/Thing.csx" },
        { ".gitignore", ".gitignore" },
        { ".gitignore", ".gitignore/x" },
        { ".github", ".github/workflows/ci.yml" },
        { ".env.local", ".env.local" },
        { "**", "a/b/c.txt" },
        { "**/x.cs", "x.cs" },
        { "**/x.cs", "a/x.cs" },
        { "a*b*c", "aXbYc" },
        { "a*b*c", "abc" },
        { "a*b*c", "acb" },
        { "a*a", "a" },
        { "a*a", "aa" },
        { "docs/(draft)+[1].md", "docs/(draft)+[1].md" },
        { "docs/(draft)+[1].md", "docs/draft1.md" },
        { "tests/**", "tests/Upstream/UpstreamTests.cs" },
        { "", "anything/x.cs" }
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void EveryPattern_AgreesWithIsInScope(string glob, string path)
    {
        IReadOnlyList<string> scope = glob.Length == 0 ? [] : [glob];
        Assert.Equal(WriteScope.IsInScope(path, scope), MatchesAny(WriteScope.ToAnchoredPatterns(scope), path));
    }

    [Fact]
    public void AGenerativeCorpus_AgreesWithIsInScope()
    {
        // Seeded, so a counterexample replays exactly.
        var random = new Random(816);
        string[] segments = ["src", "Src", "tests", "a", "b", "x.cs", "Thing.cs", ".github", "lib.v2", "n_m"];
        string[] globSegments = ["src", "tests", "*", "**", "*.cs", "a*", "*b", "T*g.cs", ".github", "lib.v2", "x.cs"];

        for (int i = 0; i < 4000; i++)
        {
            string glob = string.Join("/", Enumerable.Range(0, random.Next(1, 4))
                .Select(_ => globSegments[random.Next(globSegments.Length)]));
            if (random.Next(5) == 0)
            {
                glob += "/";
            }

            string path = string.Join("/", Enumerable.Range(0, random.Next(1, 5))
                .Select(_ => segments[random.Next(segments.Length)]));

            bool expected = WriteScope.IsInScope(path, [glob]);
            bool actual = MatchesAny(WriteScope.ToAnchoredPatterns([glob]), path);
            Assert.True(expected == actual, $"glob '{glob}' vs path '{path}': IsInScope={expected}, pattern={actual}");
        }
    }

    private static bool MatchesAny(IReadOnlyList<string> patterns, string path) =>
        patterns.Any(p => Regex.IsMatch(path, p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
}
