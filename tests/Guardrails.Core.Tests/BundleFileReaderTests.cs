using System.Text;
using Guardrails.Core.Bundle;

namespace Guardrails.Core.Tests;

/// <summary>
/// #799: the read discipline of SSOT §17.2 (items 1, 3 and 4) and the process-wide git settings (item 5). Decisions and
/// bytes are asserted, never durations: the backoff is observed through an injected delay, never slept through.
/// </summary>
public sealed class BundleFileReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "guardrails-bundle-reader-tests", Guid.NewGuid().ToString("N"));

    public BundleFileReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }

    private string Write(string name, byte[] content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void AFileUnderItsCapIsReadWhole()
    {
        string path = Write("small.log", "one\ntwo\n"u8.ToArray());
        BundleRead read = new BundleFileReader().Read(path, tailCap: 1024);

        Assert.Equal(BundleReadStatus.Whole, read.Status);
        Assert.Equal("one\ntwo\n", Encoding.UTF8.GetString(read.Bytes));
    }

    [Fact]
    public void ATailWindowDropsItsLeadingPartialLineBeforeAnyScan()
    {
        // The window (last 12 bytes) starts inside "line-two"; that fragment must never be scanned or shipped.
        string path = Write("tail.log", "line-one\nline-two\nline-3\n"u8.ToArray());
        BundleRead read = new BundleFileReader().Read(path, tailCap: 12);

        Assert.Equal(BundleReadStatus.Tail, read.Status);
        Assert.Equal("tail-window", read.Reason);
        Assert.Equal("line-3\n", Encoding.UTF8.GetString(read.Bytes));
    }

    [Fact]
    public void AWindowThatStartsExactlyAtALineKeepsThatLine()
    {
        string path = Write("aligned.log", "aaaa\nbbbb\ncccc\n"u8.ToArray());
        BundleRead read = new BundleFileReader().Read(path, tailCap: 10);

        Assert.Equal("bbbb\ncccc\n", Encoding.UTF8.GetString(read.Bytes));
    }

    [Fact]
    public void ANewlineFreeWindowShipsItsMarkedPartialLine()
    {
        // #805 N2: the last line exceeds the window; it ships marked instead of the whole file being excluded.
        string path = Write("oneline.log", Encoding.UTF8.GetBytes(new string('x', 100)));
        BundleRead read = new BundleFileReader().Read(path, tailCap: 20);

        Assert.Equal(BundleReadStatus.Tail, read.Status);
        Assert.Equal("partial-line", read.Reason);
        Assert.Equal(BundleFileReader.PartialLineMarker + new string('x', 20), Encoding.UTF8.GetString(read.Bytes));
    }

    [Fact]
    public void APartialLineStartsAtACharacterBoundary()
    {
        // 3-byte characters; a 10-byte window starts inside one, whose continuation bytes are skipped.
        string path = Write("wide.log", Encoding.UTF8.GetBytes(new string('€', 30)));
        BundleRead read = new BundleFileReader().Read(path, tailCap: 10);

        string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(read.Bytes);
        Assert.Equal(BundleFileReader.PartialLineMarker + "€€€", text);
    }

    [Fact]
    public void AMultiByteSequenceIsNeverSplit()
    {
        // Each line is 3-byte characters; any byte cut lands inside one, and the kept bytes must still decode strictly.
        var content = new StringBuilder();
        for (int i = 0; i < 40; i++)
        {
            content.Append("€€€€€ line ").Append(i).Append(" ✓\n");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(content.ToString());
        string path = Write("utf8.log", bytes);
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);

        // Every cap from just over one line (a line is at most 28 bytes) upward cuts somewhere inside a character.
        for (int cap = 60; cap < 400; cap += 7)
        {
            BundleRead read = new BundleFileReader().Read(path, tailCap: cap);
            Assert.Equal(BundleReadStatus.Tail, read.Status);
            string text = strict.GetString(read.Bytes);
            Assert.StartsWith("€€€€€ line ", text, StringComparison.Ordinal);
            Assert.EndsWith(" ✓\n", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AWindowWhoseOnlyNewlineEndsItShipsTheMarkedPartialLine()
    {
        string path = Write("long-line.log", Encoding.UTF8.GetBytes(new string('y', 50) + "\n"));
        BundleRead read = new BundleFileReader().Read(path, tailCap: 20);

        Assert.Equal("partial-line", read.Reason);
        Assert.Equal(BundleFileReader.PartialLineMarker + new string('y', 19) + "\n", Encoding.UTF8.GetString(read.Bytes));
    }

    [Fact]
    public void OnPosixAnAccessDenialIsAPermissionNotASharingViolation()
    {
        int opens = 0;
        var delays = new List<TimeSpan>();
        var posix = new BundleFileReader(_ =>
        {
            opens++;
            throw new UnauthorizedAccessException("denied");
        }, delays.Add, windows: false);

        BundleRead read = posix.Read("secret.log");

        Assert.Equal(("permission-denied", 1), (read.Reason, opens));
        Assert.Empty(delays);

        var windows = new BundleFileReader(_ => throw new UnauthorizedAccessException("held"), _ => { }, windows: true);
        Assert.Equal("sharing-violation", windows.Read("held.json").Reason);
    }

    [Fact]
    public void ARetailUsesTheSameLineRule()
    {
        byte[] bytes = "alpha\nbravo\ncharlie\n"u8.ToArray();
        (byte[] kept, bool cut) = BundleFileReader.Retail(bytes, 10)!.Value;

        Assert.True(cut);
        Assert.Equal("charlie\n", Encoding.UTF8.GetString(kept));
        (byte[] partial, bool _) = BundleFileReader.Retail("no-newline-at-all-here\n"u8.ToArray(), 5)!.Value;
        Assert.Equal(BundleFileReader.PartialLineMarker + "here\n", Encoding.UTF8.GetString(partial));
    }

    [Fact]
    public void APatchOverItsCapIsNotReadAtAll()
    {
        string path = Write("prior-attempt.patch", Encoding.UTF8.GetBytes(new string('p', 64) + "\n"));
        BundleRead read = new BundleFileReader().Read(path, wholeOrNothingCap: 10);

        Assert.Equal(BundleReadStatus.Excluded, read.Status);
        Assert.Equal("patch-over-cap", read.Reason);
        Assert.Equal(0, read.BytesRead);
    }

    [Fact]
    public void SharingErrorsAreRetriedFiveTimesWithTheBackoffThenExcluded()
    {
        int opens = 0;
        var delays = new List<TimeSpan>();
        var reader = new BundleFileReader(_ =>
        {
            opens++;
            throw new IOException("in use", unchecked((int)0x80070020)); // ERROR_SHARING_VIOLATION
        }, delays.Add);

        BundleRead read = reader.Read("held.json");

        Assert.Equal(BundleReadStatus.Excluded, read.Status);
        Assert.Equal("sharing-violation", read.Reason);
        Assert.Equal(1 + BundleFileReader.SharingRetries, opens);
        Assert.Equal([10, 20, 30, 40, 50], delays.Select(d => (int)d.TotalMilliseconds));
    }

    [Fact]
    public void ASharingErrorThatClearsIsReadNormally()
    {
        string path = Write("run.json", "{}\n"u8.ToArray());
        int opens = 0;
        var reader = new BundleFileReader(p =>
        {
            if (++opens <= 2)
            {
                throw new UnauthorizedAccessException("held");
            }

            return File.OpenRead(p);
        }, _ => { }, windows: true); // an access denial is a sharing error on Windows only

        BundleRead read = reader.Read(path);

        Assert.Equal(3, opens);
        Assert.Equal("{}\n", Encoding.UTF8.GetString(read.Bytes));
    }

    [Fact]
    public void ANonSharingIoErrorIsNotRetried()
    {
        Assert.False(BundleFileReader.IsSharingError(new FileNotFoundException("gone")));
        Assert.False(BundleFileReader.IsSharingError(new IOException("disk", unchecked((int)0x80070070))));
        Assert.True(BundleFileReader.IsSharingError(new IOException("lock", unchecked((int)0x80070021))));
    }

    [Fact]
    public void AFileThatGrowsDuringTheReadKeepsOnlyWholeLines()
    {
        var reader = new BundleFileReader(_ => new GrowingStream("done-1\ndone-2\npart"u8.ToArray()), _ => { });
        BundleRead read = reader.Read("events.jsonl");

        Assert.Equal("live-tail-cut", read.Reason);
        Assert.Equal("done-1\ndone-2\n", Encoding.UTF8.GetString(read.Bytes));
    }

    // ------------------------------------------------------------------ §17.2 item 5

    [Fact]
    public void GitSettingsAreAppendedAfterAnExistingSafeDirectory()
    {
        var environment = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "safe.directory",
            ["GIT_CONFIG_VALUE_0"] = "/work/repo",
        };

        BundleGitEnvironment.Plan plan = BundleGitEnvironment.Apply(
            name => environment.GetValueOrDefault(name), (name, value) => environment[name] = value);

        Assert.False(plan.CountWasUnparsable);
        Assert.Equal("safe.directory", environment["GIT_CONFIG_KEY_0"]);
        Assert.Equal("/work/repo", environment["GIT_CONFIG_VALUE_0"]);
        Assert.Equal("core.fsmonitor", environment["GIT_CONFIG_KEY_1"]);
        Assert.Equal("false", environment["GIT_CONFIG_VALUE_1"]);
        Assert.Equal("core.untrackedCache", environment["GIT_CONFIG_KEY_2"]);
        Assert.Equal("false", environment["GIT_CONFIG_VALUE_2"]);
        Assert.Equal("3", environment["GIT_CONFIG_COUNT"]);
        Assert.Equal("0", environment["GIT_OPTIONAL_LOCKS"]);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("two", true)]
    [InlineData("-1", true)]
    public void AnAbsentOrUnparsableCountStartsAtZero(string? count, bool unparsable)
    {
        BundleGitEnvironment.Plan plan = BundleGitEnvironment.Compute(name => name == "GIT_CONFIG_COUNT" ? count : null);

        Assert.Equal(unparsable, plan.CountWasUnparsable);
        Assert.Equal(0, plan.AppendedAt);
        Assert.Contains(new KeyValuePair<string, string>("GIT_CONFIG_KEY_0", "core.fsmonitor"), plan.Variables);
        Assert.Contains(new KeyValuePair<string, string>("GIT_CONFIG_COUNT", "2"), plan.Variables);
    }

    /// <summary>A stream whose reported length grows once the reader has consumed what it saw at open.</summary>
    private sealed class GrowingStream(byte[] content) : MemoryStream(content)
    {
        private bool _read;

        public override long Length => _read ? base.Length + 100 : base.Length;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = base.Read(buffer, offset, count);
            if (n == 0 || Position >= base.Length)
            {
                _read = true;
            }

            return n;
        }
    }
}
