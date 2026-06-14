using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

public sealed class InterpreterMapTests
{
    private static readonly IReadOnlyList<string> NoArgs = [];

    [Fact]
    public void ShScript_ResolvesToBash()
    {
        var map = new InterpreterMap(FakeExecutableProbe.With("bash"));

        InterpreterMap.Resolution resolution = map.Resolve("/plan/check.sh", NoArgs);

        Assert.Equal(InterpreterMap.Status.Resolved, resolution.Status);
        Assert.Equal("bash", resolution.Command!.Executable);
        Assert.Equal(["/plan/check.sh"], resolution.Command.Arguments);
    }

    [Fact]
    public void PyScript_PrefersPython3ThenFallsBackToPython()
    {
        var onlyPython = new InterpreterMap(FakeExecutableProbe.With("python"));

        InterpreterMap.Resolution resolution = onlyPython.Resolve("/plan/run.py", NoArgs);

        Assert.Equal(InterpreterMap.Status.Resolved, resolution.Status);
        Assert.Equal("python", resolution.Command!.Executable); // python3 missing → fallback
    }

    [Fact]
    public void PyScript_UsesPython3WhenAvailable()
    {
        var both = new InterpreterMap(FakeExecutableProbe.With("python3", "python"));

        InterpreterMap.Resolution resolution = both.Resolve("/plan/run.py", NoArgs);

        Assert.Equal("python3", resolution.Command!.Executable);
    }

    [Fact]
    public void Args_AreAppendedAfterScriptByDefault()
    {
        var map = new InterpreterMap(FakeExecutableProbe.With("bash"));

        InterpreterMap.Resolution resolution = map.Resolve("/plan/check.sh", ["--flag", "value"]);

        Assert.Equal(["/plan/check.sh", "--flag", "value"], resolution.Command!.Arguments);
    }

    [Fact]
    public void ConfigOverride_ReplacesBuiltInAndHonorsTokenPositions()
    {
        var overrides = new Dictionary<string, IReadOnlyList<string>>
        {
            [".ps1"] = ["pwsh", "-NoProfile", "-File", "{script}", "{args}"]
        };
        var map = new InterpreterMap(FakeExecutableProbe.With("pwsh"), overrides);

        InterpreterMap.Resolution resolution = map.Resolve("/plan/build.ps1", ["-Config", "Release"]);

        Assert.Equal("pwsh", resolution.Command!.Executable);
        Assert.Equal(["-NoProfile", "-File", "/plan/build.ps1", "-Config", "Release"], resolution.Command.Arguments);
    }

    [Fact]
    public void ExtensionlessFile_SpawnsDirectly()
    {
        var map = new InterpreterMap(FakeExecutableProbe.None); // nothing on PATH

        InterpreterMap.Resolution resolution = map.Resolve("/plan/tool", ["x"]);

        Assert.Equal(InterpreterMap.Status.Resolved, resolution.Status);
        Assert.Equal("/plan/tool", resolution.Command!.Executable);
        Assert.Equal(["x"], resolution.Command.Arguments);
    }

    [Fact]
    public void DllFile_ResolvesViaDotnet()
    {
        var map = new InterpreterMap(FakeExecutableProbe.With("dotnet"));

        InterpreterMap.Resolution resolution = map.Resolve("/plan/tool.dll", NoArgs);

        Assert.Equal("dotnet", resolution.Command!.Executable);
        Assert.Equal(["/plan/tool.dll"], resolution.Command.Arguments);
    }

    [Fact]
    public void MissingInterpreter_ReportsNotOnPath()
    {
        var map = new InterpreterMap(FakeExecutableProbe.None);

        InterpreterMap.Resolution resolution = map.Resolve("/plan/check.sh", NoArgs);

        Assert.Equal(InterpreterMap.Status.NotOnPath, resolution.Status);
        Assert.Contains("bash", resolution.ProbedExecutables);
    }

    [Fact]
    public void CmdScript_OffWindows_ReportsWrongPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            // On Windows .cmd is valid; this assertion only applies off-Windows.
            return;
        }

        var map = new InterpreterMap(FakeExecutableProbe.All);
        InterpreterMap.Resolution resolution = map.Resolve("/plan/run.cmd", NoArgs);

        Assert.Equal(InterpreterMap.Status.WrongPlatform, resolution.Status);
    }

    [Fact]
    public void Ps1OnWindows_FallsBackToPowershellWhenPwshMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // powershell.exe fallback only exists on Windows
        }

        var map = new InterpreterMap(FakeExecutableProbe.With("powershell.exe"));
        InterpreterMap.Resolution resolution = map.Resolve("/plan/build.ps1", NoArgs);

        Assert.Equal(InterpreterMap.Status.Resolved, resolution.Status);
        Assert.Equal("powershell.exe", resolution.Command!.Executable);
    }

    [Fact]
    public void ShScript_OnWindows_PrefersGitBashBinPathOverBareBash()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Git Bash candidate list only applies on Windows
        }

        const string gitBashBin = @"C:\Program Files\Git\bin\bash.exe";
        var map = new InterpreterMap(FakeExecutableProbe.With(gitBashBin, "bash"));

        InterpreterMap.Resolution resolution = map.Resolve(@"C:\plan\check.sh", NoArgs);

        Assert.Equal(InterpreterMap.Status.Resolved, resolution.Status);
        Assert.Equal(gitBashBin, resolution.Command!.Executable);
    }

    [Fact]
    public void ShScript_OnWindows_TriesGitBashUsrPathBeforeBareBash()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Git Bash candidate list only applies on Windows
        }

        const string gitBashUsr = @"C:\Program Files\Git\usr\bin\bash.exe";
        // bin/bash.exe absent, usr/bin/bash.exe present — should prefer usr over bare bash
        var map = new InterpreterMap(FakeExecutableProbe.With(gitBashUsr, "bash"));

        InterpreterMap.Resolution resolution = map.Resolve(@"C:\plan\check.sh", NoArgs);

        Assert.Equal(InterpreterMap.Status.Resolved, resolution.Status);
        Assert.Equal(gitBashUsr, resolution.Command!.Executable);
    }
}
