namespace Guardrails.Core.Bundle;

/// <summary>How a bounded read ended (SSOT §17.2 items 1, 3 and 4).</summary>
public enum BundleReadStatus
{
    /// <summary>The whole file was read.</summary>
    Whole,

    /// <summary>A tail window was read; its leading partial line was dropped.</summary>
    Tail,

    /// <summary>Nothing usable was read; <see cref="BundleRead.Reason"/> says why.</summary>
    Excluded,
}

/// <summary>
/// One bounded read. <see cref="Bytes"/> holds only whole lines when the read was a tail or the file grew during it, so
/// its first byte always begins a line — which a later, smaller tail cut (§17.7 tier 4) relies on.
/// </summary>
public sealed record BundleRead
{
    /// <summary>How the read ended.</summary>
    public required BundleReadStatus Status { get; init; }

    /// <summary>The MANIFEST.md reason token, or null for a plain whole read.</summary>
    public string? Reason { get; init; }

    /// <summary>The bytes kept. Empty when excluded.</summary>
    public byte[] Bytes { get; init; } = [];

    /// <summary>How many bytes were read from disk.</summary>
    public long BytesRead { get; init; }

    /// <summary>The file's length when it was opened.</summary>
    public long Length { get; init; }
}

/// <summary>
/// The read discipline of SSOT §17.2, which exists because a reader can hurt a live run (#727): on Windows the atomic
/// replace <c>AtomicFile</c> performs fails while ANY handle has the target open.
/// <list type="number">
/// <item>One bounded read per file, then close: opened with <c>FileShare.ReadWrite | FileShare.Delete</c>, read into
/// memory, closed. No handle outlives <see cref="Read"/>.</item>
/// <item>Sharing errors (Win32 32/33, <see cref="UnauthorizedAccessException"/>) are retried 5 times with a 10–50 ms
/// backoff, then the file is excluded (<c>sharing-violation</c>), never a crash.</item>
/// <item>A file over its cap is read as a tail window whose leading partial line is dropped before any scan; a window
/// with no whole line in it is excluded (<c>no-newline-in-window</c>). Only whole lines are kept, so a UTF-8 sequence is never
/// split (0x0A never occurs inside one). A file that grew during the read keeps what was read minus the trailing
/// partial line (<c>live-tail-cut</c>).</item>
/// </list>
/// </summary>
public sealed class BundleFileReader
{
    /// <summary>Sharing-error retries after the first attempt (§17.2 item 3).</summary>
    public const int SharingRetries = 5;

    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    private readonly Func<string, Stream> _open;
    private readonly Action<TimeSpan> _delay;

    /// <summary>The real reader.</summary>
    public BundleFileReader()
        : this(OpenShared, delay => Thread.Sleep(delay))
    {
    }

    /// <summary>A reader over <paramref name="open"/> (a test can make it throw sharing errors) and <paramref name="delay"/>.</summary>
    public BundleFileReader(Func<string, Stream> open, Action<TimeSpan> delay)
    {
        _open = open;
        _delay = delay;
    }

    /// <summary>The backoff before retry <paramref name="retry"/> (1-based): 10, 20, 30, 40, 50 ms.</summary>
    public static TimeSpan Backoff(int retry) => TimeSpan.FromMilliseconds(Math.Min(10 * retry, 50));

    /// <summary>
    /// Read <paramref name="path"/>. <paramref name="tailCap"/> null reads the whole file; otherwise a file longer than
    /// it is read as a tail window of that many bytes. <paramref name="wholeOrNothingCap"/> set: a longer file is not
    /// read at all (<c>patch-over-cap</c>), because a tail of a patch misleads.
    /// </summary>
    public BundleRead Read(string path, long? tailCap = null, long? wholeOrNothingCap = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using Stream stream = _open(path);
                return ReadOpen(stream, tailCap, wholeOrNothingCap);
            }
            catch (Exception ex) when (IsSharingError(ex))
            {
                if (attempt >= SharingRetries)
                {
                    return new BundleRead { Status = BundleReadStatus.Excluded, Reason = "sharing-violation" };
                }

                _delay(Backoff(attempt + 1));
            }
        }
    }

    /// <summary>Whether <paramref name="ex"/> is a sharing error §17.2 item 3 retries.</summary>
    public static bool IsSharingError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => true,
        IOException io when io is not FileNotFoundException and not DirectoryNotFoundException =>
            (io.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation,
        _ => false
    };

    /// <summary>
    /// A tail of at most <paramref name="cap"/> bytes of <paramref name="bytes"/>, cut on a line boundary — the same rule a
    /// tail read applies, used again when a trim tier halves a cap (§17.7 tier 4). Null when the window has no newline.
    /// </summary>
    public static (byte[] Bytes, bool Cut)? Retail(byte[] bytes, long cap)
    {
        if (bytes.LongLength <= cap)
        {
            return (bytes, false);
        }

        int windowStart = (int)(bytes.LongLength - cap);
        bool atLineStart = bytes[windowStart - 1] == (byte)'\n';
        return DropLeadingPartialLine(bytes.AsSpan(windowStart), atLineStart) is { } kept ? (kept, true) : null;
    }

    private static BundleRead ReadOpen(Stream stream, long? tailCap, long? wholeOrNothingCap)
    {
        long length = stream.Length;
        if (wholeOrNothingCap is { } limit && length > limit)
        {
            return new BundleRead { Status = BundleReadStatus.Excluded, Reason = "patch-over-cap", Length = length, BytesRead = 0 };
        }

        if (tailCap is not { } cap || length <= cap)
        {
            byte[] whole = ReadExactly(stream, 0, length);
            if (stream.Length > length)
            {
                // It grew while we read: keep only whole lines.
                return TrimTrailingPartial(whole, length, length, BundleReadStatus.Whole);
            }

            return new BundleRead { Status = BundleReadStatus.Whole, Bytes = whole, BytesRead = length, Length = length };
        }

        // Read one byte BEFORE the window, so a window that begins exactly at a line start keeps its first line.
        long windowStart = length - cap;
        byte[] withPrevious = ReadExactly(stream, windowStart - 1, cap + 1);
        bool atLineStart = withPrevious[0] == (byte)'\n';
        byte[]? kept = DropLeadingPartialLine(withPrevious.AsSpan(1), atLineStart);
        if (kept is null)
        {
            return new BundleRead
            {
                Status = BundleReadStatus.Excluded, Reason = "no-newline-in-window", BytesRead = cap + 1, Length = length
            };
        }

        if (stream.Length > length)
        {
            return TrimTrailingPartial(kept, cap + 1, length, BundleReadStatus.Tail);
        }

        return new BundleRead
        {
            Status = BundleReadStatus.Tail, Reason = "tail-window", Bytes = kept, BytesRead = cap + 1, Length = length
        };
    }

    private static byte[]? DropLeadingPartialLine(ReadOnlySpan<byte> window, bool atLineStart)
    {
        if (atLineStart)
        {
            return window.ToArray();
        }

        // A window whose only newline ends it holds no whole line: that is a newline-free window too.
        int newline = window.IndexOf((byte)'\n');
        return newline < 0 || newline == window.Length - 1 ? null : window[(newline + 1)..].ToArray();
    }

    private static BundleRead TrimTrailingPartial(byte[] bytes, long bytesRead, long length, BundleReadStatus status)
    {
        int last = Array.LastIndexOf(bytes, (byte)'\n');
        byte[] kept = last < 0 ? [] : bytes[..(last + 1)];
        return new BundleRead
        {
            Status = status, Reason = "live-tail-cut", Bytes = kept, BytesRead = bytesRead, Length = length
        };
    }

    private static byte[] ReadExactly(Stream stream, long offset, long count)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[count];
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, total, (int)count - total);
            if (read == 0)
            {
                // The file shrank under us (a truncate): keep what exists.
                return buffer[..total];
            }

            total += read;
        }

        return buffer;
    }

    private static Stream OpenShared(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 81920);
}
