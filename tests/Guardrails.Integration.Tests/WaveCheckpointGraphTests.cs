using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Verifies that the JIT wave checkpoint (SSOT §14.4, issue #359) automatically generates a
/// wave-scoped diagram into the stub wave folder and surfaces a "Wave diagram (focused):" link
/// in the checkpoint console output. Also covers the <see cref="GraphCommand.RenderWaveScoped"/>
/// public helper directly (unit-style) so CI can catch the render path without a full CLI run.
/// </summary>
public sealed class WaveCheckpointGraphTests
{
    private static readonly bool Ps = OperatingSystem.IsWindows();
    private static string Ext => Ps ? ".ps1" : ".sh";

    private static async Task<(int ExitCode, string Output)> InvokeRunAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(RunCommand.Create(io));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    /// <summary>Write a script that immediately exits 0 (with shebang on Unix).</summary>
    private static void WritePassScript(string path, string catchesComment = "always passes")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string content = Ps
            ? $"# catches: {catchesComment}\nexit 0\n"
            : $"#!/usr/bin/env bash\n# catches: {catchesComment}\nexit 0\n";
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    /// <summary>
    /// Build a minimal 2-wave plan under <paramref name="planDir"/>:
    /// wave-01-foundation has one trivial task (always succeeds);
    /// wave-02-build is an empty JIT stub.
    /// </summary>
    private static void BuildTwoWavePlan(string planDir)
    {
        Directory.CreateDirectory(planDir);
        File.WriteAllText(
            Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": ".",
              "defaultRetries": 0,
              "maxParallelism": 1
            }
            """);

        // Wave 1 — one trivial task: action exits 0, guardrail exits 0.
        string taskDir = Path.Combine(planDir, "wave-01-foundation", "tasks", "01-setup");
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "setup" }""");

        // Action does not need catches:; task-level guardrails/ does not enforce it either.
        string actionContent = Ps ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n";
        string actionPath = Path.Combine(taskDir, "action" + Ext);
        File.WriteAllText(actionPath, actionContent);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(actionPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        // Guardrail: task-level guardrails/ does not enforce catches:, but best practice to include it.
        WritePassScript(Path.Combine(taskDir, "guardrails", "01-check" + Ext), "task not run");

        // Wave 2 — empty JIT stub (no tasks, no preflights, no guardrails).
        Directory.CreateDirectory(Path.Combine(planDir, "wave-02-build"));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The core guarantee: when guardrails run hits the JIT checkpoint for an empty wave, the
    /// CLI exits 2 (actionable), the checkpoint output contains "Wave diagram (focused):", AND
    /// <c>wave-02-build/diagram.html</c> exists.
    /// </summary>
    [Fact]
    public async Task JitCheckpoint_GeneratesWaveDiagram_AndCheckpointOutputContainsLink()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "gr-wave-359-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            string planDir = Path.Combine(tempDir, "plan");
            BuildTwoWavePlan(planDir);

            (int exit, string output) = await InvokeRunAsync(
                "run", planDir, "--no-ui");

            // JIT checkpoint exits 2 (actionable — SSOT §7).
            Assert.Equal(ExitCodes.TaskFailed, exit);

            // WAVE CHECKPOINT headline must appear.
            Assert.Contains("WAVE CHECKPOINT", output);

            // The "Wave diagram (focused):" link must appear in the checkpoint block.
            Assert.Contains("Wave diagram (focused):", output);

            // The wave-02 diagram.html must be written to the wave folder.
            string waveDiagramHtml = Path.Combine(planDir, "wave-02-build", "diagram.html");
            Assert.True(File.Exists(waveDiagramHtml),
                $"Expected wave-02 diagram.html at: {waveDiagramHtml}\nConsole output:\n{output}");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Unit-style: <see cref="GraphCommand.RenderWaveScoped"/> writes both <c>diagram.md</c>
    /// and <c>diagram.html</c> into a real wave folder when the parent plan loads cleanly.
    /// The wave may have zero tasks (JIT stub shape) — the renderer produces a valid empty
    /// flowchart for both outputs.
    /// </summary>
    [Fact]
    public void RenderWaveScoped_OnJitStubWave_WritesBothDiagramFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "gr-wave-rws-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            string planDir = Path.Combine(tempDir, "plan");
            BuildTwoWavePlan(planDir);

            string waveDir = Path.Combine(planDir, "wave-02-build");
            bool success = GraphCommand.RenderWaveScoped(waveDir, TextWriter.Null);

            Assert.True(success, "RenderWaveScoped should return true for a valid JIT stub wave folder");
            Assert.True(File.Exists(Path.Combine(waveDir, "diagram.md")),
                "diagram.md must be written");
            Assert.True(File.Exists(Path.Combine(waveDir, "diagram.html")),
                "diagram.html must be written");

            // diagram.md must carry the provenance comment + fenced mermaid block.
            string md = File.ReadAllText(Path.Combine(waveDir, "diagram.md"));
            Assert.Contains("<!-- guardrails:graph v1 source-sha256=", md);
            Assert.Contains("```mermaid", md);
            Assert.Contains("flowchart TD", md);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// <see cref="GraphCommand.RenderWaveScoped"/> returns <c>false</c> (gracefully) when the
    /// supplied directory is not a wave folder (e.g. a flat plan root).
    /// </summary>
    [Fact]
    public void RenderWaveScoped_OnNonWaveFolder_ReturnsFalse()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "gr-wave-nw-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            // A flat plan (no wave-NN-slug naming) — not a wave folder.
            string planDir = Path.Combine(tempDir, "flat-plan");
            Directory.CreateDirectory(planDir);
            File.WriteAllText(
                Path.Combine(planDir, "guardrails.json"),
                """{ "version": 1, "workspace": ".", "defaultRetries": 0, "maxParallelism": 1 }""");

            bool result = GraphCommand.RenderWaveScoped(planDir, TextWriter.Null);

            Assert.False(result, "A flat plan root is not a wave folder — must return false");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Re-run (after the human authors wave-02): <see cref="ConsoleRunObserver.WaveStarting"/>
    /// regenerates the wave diagram via <see cref="GraphCommand.RenderWaveScoped"/>. Verify
    /// indirectly: a full run of a now-complete two-wave plan via CLI with --no-ui produces
    /// "Wave diagram (focused):" for each wave that actually executes.
    /// </summary>
    [Fact]
    public async Task WaveStart_WhenWaveHasTasks_RegeneratesDiagramAndPrintsLink()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "gr-wave-ws-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            string planDir = Path.Combine(tempDir, "plan");

            // Build a fully-authored 2-wave plan (both waves have tasks).
            Directory.CreateDirectory(planDir);
            File.WriteAllText(
                Path.Combine(planDir, "guardrails.json"),
                """
                {
                  "version": 1,
                  "guardrailMode": "failFast",
                  "workspace": ".",
                  "defaultRetries": 0,
                  "maxParallelism": 1
                }
                """);

            // Wave 1: one trivial task.
            string w1TaskDir = Path.Combine(planDir, "wave-01-foundation", "tasks", "01-setup");
            Directory.CreateDirectory(w1TaskDir);
            File.WriteAllText(Path.Combine(w1TaskDir, "task.json"), """{ "description": "setup" }""");
            string w1Action = Path.Combine(w1TaskDir, "action" + Ext);
            File.WriteAllText(w1Action, Ps ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(w1Action,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            WritePassScript(Path.Combine(w1TaskDir, "guardrails", "01-check" + Ext));

            // Wave 2: one trivial task (fully authored — no JIT stub).
            string w2TaskDir = Path.Combine(planDir, "wave-02-build", "tasks", "01-compile");
            Directory.CreateDirectory(w2TaskDir);
            File.WriteAllText(Path.Combine(w2TaskDir, "task.json"), """{ "description": "compile" }""");
            string w2Action = Path.Combine(w2TaskDir, "action" + Ext);
            File.WriteAllText(w2Action, Ps ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(w2Action,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            WritePassScript(Path.Combine(w2TaskDir, "guardrails", "01-check" + Ext));

            (int exit, string output) = await InvokeRunAsync("run", planDir, "--no-ui");

            // Fully green run exits 0 (mergeOnSuccess is a no-op in serial mode — no plan branch).
            // (In serial mode there is no plan branch, so WhollyGreenButUndelivered is suppressed.)
            Assert.Equal(ExitCodes.Success, exit);

            // ConsoleRunObserver.WaveStarting (forwarded through OnTheFlyLogSiteObserver → OnTheFlyDiagramObserver)
            // prints the diagram link when the render succeeds.
            Assert.Contains("Wave diagram (focused):", output);

            // Both wave diagrams must exist.
            Assert.True(File.Exists(Path.Combine(planDir, "wave-01-foundation", "diagram.html")),
                "wave-01 diagram.html must be written by WaveStarting");
            Assert.True(File.Exists(Path.Combine(planDir, "wave-02-build", "diagram.html")),
                "wave-02 diagram.html must be written by WaveStarting");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
