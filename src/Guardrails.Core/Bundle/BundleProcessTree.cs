using System.Globalization;
using System.Text.Json;

namespace Guardrails.Core.Bundle;

/// <summary>A file's size and last-write time, read from metadata (no handle is held).</summary>
/// <param name="Length">Bytes.</param>
/// <param name="LastWriteUtc">The last write.</param>
public sealed record BundleFileStat(long Length, DateTimeOffset LastWriteUtc)
{
    /// <summary>The real stat, or null when the file is gone or unreadable.</summary>
    public static BundleFileStat? Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new BundleFileStat(info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>One process in the owner's tree. Fields the platform cannot give read <c>-</c>.</summary>
public sealed record BundleProcessRow(int Pid, int ParentPid, string State, string Elapsed, string Cpu, string Command);

/// <summary>The owner process's descendants, or why they could not be listed (#805 S1).</summary>
public sealed record BundleProcessTree(IReadOnlyList<BundleProcessRow> Rows, string? Unavailable)
{
    /// <summary>The most rows the block lists.</summary>
    public const int MaxRows = 100;

    /// <summary>A tree that could not be listed, and why.</summary>
    public static BundleProcessTree Failed(string reason) => new([], reason);

    /// <summary>
    /// The owner <paramref name="rootPid"/> and its descendants among <paramref name="all"/>, breadth first, capped at
    /// <see cref="MaxRows"/>. Pure, so the POSIX and Windows parsers share it and a test can drive it.
    /// </summary>
    public static IReadOnlyList<BundleProcessRow> Descendants(int rootPid, IReadOnlyList<BundleProcessRow> all)
    {
        var byParent = all.GroupBy(r => r.ParentPid).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Pid).ToList());
        var result = new List<BundleProcessRow>();
        var queue = new Queue<int>();
        if (all.FirstOrDefault(r => r.Pid == rootPid) is { } root)
        {
            result.Add(root);
        }

        queue.Enqueue(rootPid);
        var seen = new HashSet<int> { rootPid };
        while (queue.Count > 0 && result.Count < MaxRows)
        {
            foreach (BundleProcessRow child in byParent.GetValueOrDefault(queue.Dequeue()) ?? [])
            {
                if (seen.Add(child.Pid) && result.Count < MaxRows)
                {
                    result.Add(child);
                    queue.Enqueue(child.Pid);
                }
            }
        }

        return result;
    }
}

/// <summary>
/// The real process tree (#805 S1): POSIX <c>ps -A -o pid=,ppid=,stat=,etime=,pcpu=,command=</c>; Windows the CIM
/// <c>Win32_Process</c> table through <c>pwsh</c> (or Windows PowerShell). Bounded (10 s, <see cref="BundleProcessTree.MaxRows"/>),
/// and fail-soft: any failure is a noted reason, never an exception.
/// </summary>
public static class SystemBundleProcessTree
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Capture the tree under <paramref name="ownerPid"/>.</summary>
    public static BundleProcessTree Capture(int ownerPid)
    {
        try
        {
            return OperatingSystem.IsWindows() ? CaptureWindows(ownerPid) : CapturePosix(ownerPid);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return BundleProcessTree.Failed($"the process table could not be read ({ex.GetType().Name})");
        }
    }

    private static BundleProcessTree CapturePosix(int ownerPid)
    {
        BundleProcessResult result = BundleProcess.Run("ps", ["-A", "-o", "pid=,ppid=,stat=,etime=,pcpu=,command="],
            Directory.GetCurrentDirectory(), Timeout);
        if (!result.Succeeded)
        {
            return BundleProcessTree.Failed(result.NotFound ? "ps is not on PATH" : result.TimedOut ? "ps timed out" : $"ps exited {result.ExitCode}");
        }

        return new BundleProcessTree(BundleProcessTree.Descendants(ownerPid, ParsePs(result.StandardOutput)), null);
    }

    /// <summary>Parse <c>ps -o pid=,ppid=,stat=,etime=,pcpu=,command=</c> output.</summary>
    public static IReadOnlyList<BundleProcessRow> ParsePs(string output)
    {
        var rows = new List<BundleProcessRow>();
        foreach (string line in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string[] parts = line.Trim().Split((char[]?)null, 6, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 5
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int ppid))
            {
                rows.Add(new BundleProcessRow(pid, ppid, parts[2], parts[3], parts[4], parts.Length > 5 ? parts[5] : string.Empty));
            }
        }

        return rows;
    }

    private static BundleProcessTree CaptureWindows(int ownerPid)
    {
        const string script =
            "Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,@{n='Started';e={$_.CreationDate.ToUniversalTime().ToString('o')}},CommandLine | ConvertTo-Json -Compress";
        BundleProcessResult result = BundleProcess.Run("pwsh", ["-NoProfile", "-NonInteractive", "-Command", script],
            Directory.GetCurrentDirectory(), Timeout);
        if (result.NotFound)
        {
            result = BundleProcess.Run("powershell", ["-NoProfile", "-NonInteractive", "-Command", script],
                Directory.GetCurrentDirectory(), Timeout);
        }

        if (!result.Succeeded)
        {
            return BundleProcessTree.Failed(result.NotFound ? "neither pwsh nor powershell is on PATH"
                : result.TimedOut ? "the CIM query timed out" : $"the CIM query exited {result.ExitCode}");
        }

        return new BundleProcessTree(
            BundleProcessTree.Descendants(ownerPid, ParseCim(result.StandardOutput, DateTimeOffset.UtcNow)), null);
    }

    /// <summary>Parse the CIM query's JSON. State and %cpu are not in <c>Win32_Process</c>; they read <c>-</c>.</summary>
    public static IReadOnlyList<BundleProcessRow> ParseCim(string json, DateTimeOffset now)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        IEnumerable<JsonElement> items = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray()
            : [document.RootElement];
        var rows = new List<BundleProcessRow>();
        foreach (JsonElement item in items)
        {
            if (!item.TryGetProperty("ProcessId", out JsonElement pid) || !item.TryGetProperty("ParentProcessId", out JsonElement ppid))
            {
                continue;
            }

            string elapsed = item.TryGetProperty("Started", out JsonElement started) && started.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(started.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at)
                    ? Elapsed(now - at)
                    : "-";
            string command = item.TryGetProperty("CommandLine", out JsonElement line) && line.ValueKind == JsonValueKind.String
                ? line.GetString()!
                : string.Empty;
            rows.Add(new BundleProcessRow(pid.GetInt32(), ppid.GetInt32(), "-", elapsed, "-", command));
        }

        return rows;
    }

    private static string Elapsed(TimeSpan span) =>
        span < TimeSpan.Zero ? "-" : string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}");
}
