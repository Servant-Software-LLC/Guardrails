using System.IO.Compression;

namespace Guardrails.Core.Bundle;

/// <summary>
/// The deterministic zip of SSOT §17.10: entries sorted ordinally by path with <c>/</c> separators, every timestamp
/// 1980-01-01 00:00:00, one compression level (<see cref="CompressionLevel.Optimal"/>) for every entry. Identical
/// entries give identical bytes.
/// </summary>
public static class BundleZip
{
    /// <summary>The fixed entry timestamp.</summary>
    public static readonly DateTimeOffset EntryTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Lazy<long> EmptySize = new(() => Build([]).LongLength);

    /// <summary>The zip bytes for <paramref name="entries"/>.</summary>
    public static byte[] Build(IEnumerable<KeyValuePair<string, byte[]>> entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (KeyValuePair<string, byte[]> entry in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                ZipArchiveEntry zipEntry = archive.CreateEntry(entry.Key, CompressionLevel.Optimal);
                zipEntry.LastWriteTime = EntryTime;
                using Stream content = zipEntry.Open();
                content.Write(entry.Value);
            }
        }

        return stream.ToArray();
    }

    /// <summary>
    /// What one entry adds to the zip. Each entry is compressed independently and carries its own local and central
    /// headers, so the zip's size is the sum of these plus the empty archive's — which lets the trim loop (§17.7) decide
    /// against the exact zip without rebuilding it per step.
    /// </summary>
    public static long EntrySize(string path, byte[] content) =>
        Build([new KeyValuePair<string, byte[]>(path, content)]).LongLength - EmptySize.Value;

    /// <summary>The size of an archive with no entries.</summary>
    public static long EmptyArchiveSize => EmptySize.Value;
}
