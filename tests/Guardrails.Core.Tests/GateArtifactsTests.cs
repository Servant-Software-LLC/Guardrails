using Guardrails.Core.Execution;
using Guardrails.Core.Io;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit coverage for the gate-capture layout (issue #432, SSOT §8): the predictable path a post-mortem
/// reads, and the best-effort write contract. The end-to-end proof that the four gate paths actually USE
/// this lives in <c>GateFailurePersistenceTests</c> (Integration.Tests); these pin the pure shaping rules
/// the journal's <c>logDir</c> pointer promises.
/// </summary>
public sealed class GateArtifactsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gr-gate-artifacts-" + Guid.NewGuid().ToString("N"));

    public GateArtifactsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { SafeDelete.DeleteDirectory(_root); }
        catch { /* best-effort teardown */ }
    }

    [Fact]
    public void DirectoryFor_PlanScopedGate_IsUnderLogsRunId()
    {
        string? dir = GateArtifacts.DirectoryFor("/plans/p", "run-1", waveDir: null, GateArtifacts.PreflightsFolder);

        Assert.Equal(Path.Combine("/plans/p", "logs", "run-1", "preflights"), dir);
    }

    [Fact]
    public void DirectoryFor_WaveScopedGate_NestsUnderTheWaveDir()
    {
        string? dir = GateArtifacts.DirectoryFor("/plans/p", "run-1", "wave-02-build", GateArtifacts.GuardrailsFolder);

        Assert.Equal(Path.Combine("/plans/p", "logs", "run-1", "wave-02-build", "guardrails"), dir);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DirectoryFor_WithoutARunId_IsNull_SoNothingIsWrittenToAMisRootedPath(string? runId)
    {
        Assert.Null(GateArtifacts.DirectoryFor("/plans/p", runId, null, GateArtifacts.PreflightsFolder));
        Assert.Null(GateArtifacts.RelativeDirectoryFor(runId, null, GateArtifacts.PreflightsFolder));
    }

    [Fact]
    public void RelativeDirectoryFor_IsPlanRelative_WithForwardSlashes_OnEveryOs()
    {
        // Journaled paths must be portable — the per-attempt logDir convention of SSOT §7 is forward-slash
        // and plan-relative, and a gate's pointer has to read the same on Windows and Linux.
        Assert.Equal("logs/run-1/preflights",
            GateArtifacts.RelativeDirectoryFor("run-1", null, GateArtifacts.PreflightsFolder));
        Assert.Equal("logs/run-1/wave-02-build/guardrails",
            GateArtifacts.RelativeDirectoryFor("run-1", "wave-02-build", GateArtifacts.GuardrailsFolder));
    }

    [Fact]
    public void WriteCheck_WritesStdoutStderrAndResult_UnderAPerCheckDirectory()
    {
        GateArtifacts.WriteCheck(_root, "01-build", Result(exitCode: 1, "out-bytes", "err-bytes"), "the reason");

        string dir = Path.Combine(_root, "01-build");
        Assert.Equal("out-bytes", File.ReadAllText(Path.Combine(dir, "stdout.log")));
        Assert.Equal("err-bytes", File.ReadAllText(Path.Combine(dir, "stderr.log")));

        string result = File.ReadAllText(Path.Combine(dir, "result.json"));
        Assert.Contains("\"name\": \"01-build\"", result);
        Assert.Contains("\"passed\": false", result);
        Assert.Contains("\"exitCode\": 1", result);
        Assert.Contains("\"reason\": \"the reason\"", result);
    }

    [Fact]
    public void WriteCheck_ForAPassingCheck_RecordsPassedTrue_WithNoReason()
    {
        GateArtifacts.WriteCheck(_root, "01-build", Result(exitCode: 0, "all good", ""), failureReason: null);

        string result = File.ReadAllText(Path.Combine(_root, "01-build", "result.json"));
        Assert.Contains("\"passed\": true", result);
        Assert.Contains("\"reason\": null", result);
    }

    [Fact]
    public void WriteCheck_SanitizesTheCheckNameIntoOneSafeDirectorySegment()
    {
        GateArtifacts.WriteCheck(_root, "01 build/all:things", Result(exitCode: 0, "ok", ""), null);

        Assert.True(Directory.Exists(Path.Combine(_root, "01_build_all_things")));
    }

    [Fact]
    public void WriteCheck_WhenTheTargetCannotBeWritten_DoesNotThrow()
    {
        // Evidence is best-effort; a gate's verdict is a property of its child processes and must never
        // depend on the harness's ability to persist a log. A file where the directory must go is the
        // cheapest cross-platform way to make the write fail.
        string blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "not a directory");

        GateArtifacts.WriteCheck(blocked, "01-build", Result(exitCode: 1, "out", "err"), "reason");
    }

    private static ProcessResult Result(int exitCode, string stdout, string stderr) => new()
    {
        ExitCode = exitCode,
        StandardOutput = stdout,
        StandardError = stderr,
        TimedOut = false,
        Duration = TimeSpan.FromMilliseconds(12)
    };
}
