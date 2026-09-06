using System.ComponentModel;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests.Execution;

/// <summary>
/// Issue #650 — on Linux, a script the harness has just written can refuse to launch with
/// <c>ETXTBSY</c> ("Text file busy"): the kernel will not <c>exec</c> a file that some process still
/// holds open for WRITING.
///
/// <para>
/// The harness meets this shape constantly — it writes the guardrail shim at startup, and writes action
/// and guardrail scripts into a worktree, while other segments spawn children in parallel. <c>fork</c>
/// copies the whole descriptor table, so a child spawned while another thread still holds the script open
/// for writing inherits that descriptor, and the exec fails until it is closed. <c>O_CLOEXEC</c> closes it
/// on the child's OWN exec, which is not the exec that is failing.
/// </para>
///
/// <para>
/// Measured in CI (ubuntu-latest, run 34053123998): a fixture wrote <c>fake-claude.sh</c>, set its exec
/// bit, and the harness reported <i>"An error occurred trying to start process ... Text file busy"</i>.
/// The same job was green on the previous run — the tell that the condition is transient and clears in
/// milliseconds. Left alone it surfaces as a task that failed to launch for a reason no reader can act on,
/// which is indistinguishable from a real launch failure.
/// </para>
/// </summary>
public sealed class TextFileBusyLaunchRetryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gr-etxtbsy-" + Guid.NewGuid().ToString("N"));

    public TextFileBusyLaunchRetryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The real condition, produced deliberately and then RELEASED — so the assertion is that the retry
    /// carried the launch through, not that a race happened to resolve.
    ///
    /// <para>
    /// A background writer holds the script open for writing, the launch is attempted, and the handle is
    /// closed shortly after. Without the retry the very first <c>exec</c> throws and the run reports a
    /// launch failure; with it, the launch succeeds and the process's retry counter has moved.
    /// </para>
    ///
    /// <para>
    /// Linux-only, and that is stated rather than hidden: <c>ETXTBSY</c> is a Linux behaviour, and Windows
    /// and macOS do not produce it. The suite runs on ubuntu in CI, so this executes there rather than
    /// being permanently skipped evidence.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ATransientlyBusyScript_IsRetried_AndLaunches()
    {
        // A bare `return` here would report PASSED while measuring nothing — the same "green while doing
        // nothing" shape this whole change is about. Skip VISIBLY instead, so a Windows-only run cannot be
        // mistaken for evidence that the retry works.
        Assert.SkipUnless(OperatingSystem.IsLinux(),
            "ETXTBSY is a Linux behaviour; Windows and macOS do not refuse to exec an open-for-write file. "
            + "This runs on the ubuntu leg in CI.");

        string script = WriteExecutableScript();

        int before = ProcessRunner.TextFileBusyRetries;

        // Hold it open for WRITE — the exact condition an inherited descriptor creates.
        var holder = new FileStream(script, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        // Release it when the retry loop has DEMONSTRABLY entered, not after a fixed delay. The first
        // version of this test released on a 40 ms timer against a ~100 ms retry budget, and lost the race
        // on CI: the launch exhausted its attempts while the handle was still open, and the test failed
        // for a reason that had nothing to do with the behaviour under test. Waiting on the counter makes
        // it a handshake — the same "assert the decision, never the duration" rule (#518) that the
        // assertions below already follow, applied to the fixture that produces them.
        //
        // The handshake only works because the retry loop AWAITS its backoff. With a blocking sleep,
        // everything before this async method's first await ran on THIS thread, so RunAsync did not
        // return a Task until the retries were exhausted and the loop below never got to run — the
        // second way this test failed on CI, and a real thread-pool cost in production besides.
        var launch = new ProcessRunner().RunAsync(
            new ResolvedCommand { Executable = script, Arguments = [] },
            _root,
            new Dictionary<string, string>(StringComparer.Ordinal),
            TimeSpan.FromSeconds(30),
            standardInput: null,
            stdoutLineSink: null,
            TestContext.Current.CancellationToken);

        while (Volatile.Read(ref ProcessRunner.TextFileBusyRetries) == before && !launch.IsCompleted)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        holder.Dispose();
        ProcessResult result = await launch;

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("launched", result.StandardOutput, StringComparison.Ordinal);
        Assert.True(
            ProcessRunner.TextFileBusyRetries > before,
            "the launch succeeded without ever observing ETXTBSY, so this test measured nothing — the "
            + "writer's handle must be held across the first Start() attempt for the retry to be exercised");
    }

    /// <summary>
    /// Write the fixture script and make it executable. Split out so the platform guard the analyzer needs
    /// (<c>CA1416</c>) sits on the one call that is genuinely Unix-only, rather than suppressing it.
    /// </summary>
    private string WriteExecutableScript()
    {
        string script = Path.Combine(_root, "busy.sh");
        File.WriteAllText(script, "#!/bin/sh\necho launched\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return script;
    }

    /// <summary>
    /// The property the retry must NOT acquire, and the one that would be expensive to lose: every OTHER
    /// launch failure still throws on the first attempt.
    ///
    /// <para>
    /// A broad "retry any start failure" would turn a deterministic misconfiguration — a missing
    /// interpreter, a wrong path, a permission denial — into a slow one, and a misconfiguration that takes
    /// five attempts and a backoff to report itself is strictly worse than one that fails immediately.
    /// This runs on every platform, because the narrowness is the risk.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AGenuinelyUnlaunchableExecutable_FailsImmediately_WithoutRetrying()
    {
        int before = ProcessRunner.TextFileBusyRetries;
        string missing = Path.Combine(_root, "no-such-executable-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAnyAsync<Win32Exception>(() => new ProcessRunner().RunAsync(
            new ResolvedCommand { Executable = missing, Arguments = [] },
            _root,
            new Dictionary<string, string>(StringComparer.Ordinal),
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken));

        // The DECISION, not the duration: no ETXTBSY was claimed, so no retry was spent. (The counter is
        // process-global; a concurrent test producing a real ETXTBSY could move it, which is possible only
        // on Linux and vanishingly unlikely to coincide.)
        Assert.Equal(before, ProcessRunner.TextFileBusyRetries);
    }
}
