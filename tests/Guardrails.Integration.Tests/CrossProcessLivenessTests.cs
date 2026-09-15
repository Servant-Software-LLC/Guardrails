using System.Diagnostics;
using System.Text.Json;
using Guardrails.Core.Journal;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #704 — the run's start identity, as a DIFFERENT PROCESS reads it.
/// <para>
/// A run records its own identity, and <c>guardrails status</c> checks it later from another process. An in-process
/// test compares a process's reading of itself with that same process's reading, which proves nothing about the
/// real case: on Linux .NET rebuilds a wall-clock start time per reading process from a boot time it caches itself,
/// so two processes can disagree about the very same pid. This test makes the reader a genuinely separate process —
/// the shipped <c>guardrails status</c> — on every OS the CI matrix runs.
/// </para>
/// <para>
/// Two-sided on purpose: the live owner must read RUNNING, and the same owner one tick off must read EXITED. A
/// probe with any slack at all passes the first and fails the second.
/// </para>
/// </summary>
public sealed class CrossProcessLivenessTests
{
    [Fact]
    public async Task ASeparateStatusProcess_SeesThisProcessAsTheLiveOwner_AndOneTickOffAsGone()
    {
        using var plan = new StatePlanBuilder().AddTask("01-first");
        RunOwner self = RunLiveness.OwnerForThisProcess()
            ?? throw new InvalidOperationException("this platform could not read the test process's own identity");

        WriteJournal(plan, self);
        (int exit, string output, string error) = await StatusOutOfProcessAsync(plan.PlanDir);
        Assert.True(exit == 0, $"status exited {exit}: {error}");
        Assert.Contains("Run state: RUNNING", output, StringComparison.Ordinal);

        WriteJournal(plan, self with
        {
            ProcessStartedAt = self.ProcessStartedAt.AddTicks(1),
            ProcessStartTicks = self.ProcessStartTicks + 1
        });
        (exit, output, error) = await StatusOutOfProcessAsync(plan.PlanDir);
        Assert.True(exit == 0, $"status exited {exit}: {error}");
        Assert.Contains("Run state: EXITED WITHOUT FINISHING", output, StringComparison.Ordinal);
    }

    private static void WriteJournal(StatePlanBuilder plan, RunOwner owner)
    {
        var document = new JournalDocument
        {
            RunId = "2026-09-13T04-15-59Z-a1b2",
            PlanHash = "sha256:test",
            Tasks = new Dictionary<string, TaskJournalEntry>(StringComparer.Ordinal)
            {
                ["01-first"] = new() { Status = JournalTaskStatus.Running }
            },
            Owner = owner
        };

        string stateDir = Path.Combine(plan.PlanDir, "state");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(stateDir, "run.json"), JsonSerializer.Serialize(document, JournalJson.Options));
    }

    /// <summary><c>guardrails status</c> as a separate OS process — the idiom <c>AttachReplayTests</c> uses.</summary>
    private static async Task<(int ExitCode, string Output, string Error)> StatusOutOfProcessAsync(string planDir)
    {
        string appHost = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Guardrails.Cli.exe" : "Guardrails.Cli");
        ProcessStartInfo psi = File.Exists(appHost)
            ? new ProcessStartInfo(appHost)
            : new ProcessStartInfo("dotnet");
        if (!File.Exists(appHost))
        {
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Guardrails.Cli.dll"));
        }

        psi.ArgumentList.Add("status");
        psi.ArgumentList.Add(planDir);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{psi.FileName}'.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout, await stderr);
    }
}
