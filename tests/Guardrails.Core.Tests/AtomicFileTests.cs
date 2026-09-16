using Guardrails.Core.State;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #727. On Windows, the final step of an atomic write, <c>File.Move(temp, target, overwrite: true)</c>, fails
/// while another handle holds the target open. The journal is written this way with no retry, and the Scheduler
/// treats that failure as fatal. A long-lived reader of <c>run.json</c> could therefore abort a healthy run: a
/// <c>guardrails logs</c> page re-reading the journal on every load, <c>attach</c>'s 250 ms poll, or
/// <c>status</c>. Measured: a 25-task script plan went 25/25 with no reader, and aborted at task 9 with "Access to
/// the path is denied" while a logs page was fetched in a loop.
///
/// <para><b>Windows only.</b> On Linux and macOS, <c>rename(2)</c> over an open file succeeds, because the reader
/// keeps the old inode. The failure these tests pin cannot be produced there, so a POSIX run would prove nothing
/// either way.</para>
///
/// <para>No test here waits a fixed time for the retry path to be reached. The retry callback holds the retry loop
/// until the test has released the reader, so the release always lands while a retry is actually pending.</para>
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private const string WindowsOnly =
        "A rename over a file another handle holds open only fails on Windows; POSIX rename succeeds, so there is no failure to retry.";

    /// <summary>A bound on each wait, so a broken retry fails the test instead of hanging it. Never an assertion about speed.</summary>
    private static readonly TimeSpan WaitBound = TimeSpan.FromSeconds(30);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gr-727-" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    [Fact]
    public async Task AReplaceBlockedByAReader_IsRetried_AndLandsOnceTheReaderLetsGo()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(_dir, "run.json");
        File.WriteAllText(path, "old");

        var retrying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var readerReleased = new ManualResetEventSlim();

        // FileShare.Read is what the journal's reader (File.ReadAllText) holds while it reads.
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task write;
        try
        {
            write = Task.Run(
                () => AtomicFile.WriteAllText(path, "new", onRetry: _ =>
                {
                    retrying.TrySetResult();
                    readerReleased.Wait(WaitBound);
                }),
                ct);

            Task first = await Task.WhenAny(retrying.Task, write).WaitAsync(WaitBound, ct);
            Assert.True(
                first == retrying.Task,
                "the replace was not retried while a reader held the target open; it failed with: "
                + write.Exception?.GetBaseException().Message);
        }
        finally
        {
            reader.Dispose();
            readerReleased.Set();
        }

        await write.WaitAsync(WaitBound, ct); // rethrows if the write still failed after the reader let go
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void AReplaceBlockedForGood_FailsLoudly_AfterEveryRetryIsSpent_AndNamesTheLikelyCause()
    {
        // The control: a retry must never turn a permanent failure into a silent one. The write still throws after
        // the bounded retries, and leaves neither a torn target nor a stray temp. It must also say what is wrong:
        // the bare "Access to the path is denied" it used to surface named no cause, and for run.json the run is
        // already lost by the time an operator reads it (#727 review).
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        string path = Path.Combine(_dir, "run.json");
        File.WriteAllText(path, "old");
        int retries = 0;

        Exception? failure;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            failure = Record.Exception(() => AtomicFile.WriteAllText(path, "new", onRetry: _ => retries++));
        }

        Assert.True(
            failure is UnauthorizedAccessException or IOException,
            $"expected the move's own failure to surface, got: {failure?.GetType().Name ?? "no exception"}");
        Assert.Equal(AtomicFile.ReplaceAttempts - 1, retries);
        Assert.Equal("old", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        // It names the file, says another process is holding it open, and lists the suspects an operator can act on.
        Assert.Contains(path, failure!.Message, StringComparison.Ordinal);
        Assert.Contains("holding it open", failure.Message, StringComparison.Ordinal);
        foreach (string suspect in new[] { "virus scanner", "backup agent", "guardrails attach", "guardrails logs", "editor" })
        {
            Assert.Contains(suspect, failure.Message, StringComparison.Ordinal);
        }

        // The original failure rides along, so the underlying Win32 error is still there to read.
        Assert.True(
            failure.InnerException is UnauthorizedAccessException or IOException,
            $"expected the move's own exception as the inner one, got: {failure.InnerException?.GetType().Name ?? "none"}");
    }
}
