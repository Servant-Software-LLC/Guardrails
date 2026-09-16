using System.Text;

namespace Guardrails.Core.State;

/// <summary>
/// Atomic file writes (SSOT §0 intro: "all harness writes are atomic — write temp file,
/// then move over the target"). A crash mid-write can leave a stray <c>*.tmp</c> but
/// never a half-written target, so <c>state.json</c>/<c>run.json</c> are always either the
/// old or the new content, never a torn blend.
///
/// <para><b>The move is retried while another handle holds the target (issue #727).</b> On Windows,
/// replacing a file fails while any other process has it open, even for reading. Before #727 that failure
/// went straight to the caller, and for <c>run.json</c> the caller is the Scheduler, which aborts the run on
/// it. Every reader a run may have alongside it therefore depends on this retry (SSOT §7, journal writes):
/// <c>guardrails logs</c> re-reads the journal on every page load, <c>guardrails attach</c> polls it every
/// 250 ms, and <c>guardrails status</c> reads it on demand. Measured: a 25-task plan aborted at task 9 with
/// "Access to the path is denied" after 1,383 page loads against the old build, and ran 25 of 25 across 3,810
/// loads against this one. A read holds the file for microseconds, so a short, bounded retry outlasts it. A
/// handle that never lets go still fails the write, loudly: once every retry is spent the failure names the
/// likely cause and carries the move's own exception as its inner exception.</para>
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// How many times the final move is attempted before its failure is surfaced (issue #727). With the backoff
    /// in <see cref="RetryDelay"/> the retries add at most about a third of a second.
    /// </summary>
    internal const int ReplaceAttempts = 10;

    // Win32 error codes carried in the low word of an IOException's HResult. A sharing or lock violation clears
    // when the other handle closes; anything else (a missing directory, a bad path) will not, so it is not retried.
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Write <paramref name="content"/> to <paramref name="path"/> atomically: a sibling temp
    /// file is written and flushed, then moved over the target (overwriting if present).
    /// The parent directory is created if missing.
    /// </summary>
    public static void WriteAllText(string path, string content) => WriteAllText(path, content, onRetry: null);

    /// <summary>
    /// <see cref="WriteAllText(string, string)"/>, reporting each failed move that is about to be retried to
    /// <paramref name="onRetry"/> with its attempt number. That is the test seam: a test can release a held handle
    /// exactly while a retry is pending, instead of guessing at a delay.
    /// </summary>
    internal static void WriteAllText(string path, string content, Action<int>? onRetry)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException($"Path has no directory: {path}", nameof(path));
        Directory.CreateDirectory(directory);

        // Keep the temp file in the same directory so the move is a same-volume rename.
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(tempPath, content, Utf8NoBom);
            MoveOver(tempPath, path, onRetry);
        }
        finally
        {
            // Best-effort cleanup if the move failed and left the temp behind.
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // The stray temp is harmless; never fail the write over cleanup.
                }
            }
        }
    }

    /// <summary>
    /// Move <paramref name="tempPath"/> over <paramref name="path"/>, retrying while another handle holds the target.
    /// The last attempt's exception propagates unchanged.
    /// </summary>
    private static void MoveOver(string tempPath, string path, Action<int>? onRetry)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < ReplaceAttempts && IsHeldByAnotherHandle(ex))
            {
                onRetry?.Invoke(attempt);
                Thread.Sleep(RetryDelay(attempt));
            }
            catch (Exception ex) when (IsHeldByAnotherHandle(ex))
            {
                // Every retry spent and the file is STILL held. What surfaced before was the bare "Access to the
                // path is denied", which names no cause — and for run.json the run is already lost by the time
                // anyone reads it, so the message has to point at what actually holds files open (#727 review).
                // Loud either way: this throws, and the original exception rides along as the inner one, so the
                // underlying Win32 error is still there to read.
                throw new IOException(
                    $"Could not replace '{path}' after {ReplaceAttempts} attempts: another process is holding it "
                    + "open. That is usually a virus scanner, a backup agent, a running `guardrails attach` or "
                    + "`guardrails logs` session, or an editor with the file open. Close it and run again.",
                    ex);
            }
        }
    }

    /// <summary>Linear backoff, 10 ms per attempt, capped at 50 ms.</summary>
    private static int RetryDelay(int attempt) => Math.Min(10 * attempt, 50);

    /// <summary>
    /// Whether a failed move is the kind another handle's close will clear. On Windows, replacing a file that is open
    /// elsewhere surfaces as "access denied" or as a sharing/lock violation.
    /// </summary>
    private static bool IsHeldByAnotherHandle(Exception ex) =>
        ex is UnauthorizedAccessException
        || (ex is IOException && (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation);
}
