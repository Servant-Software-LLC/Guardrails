using System.Text;

namespace Guardrails.Core.State;

/// <summary>
/// Atomic file writes (SSOT §0 intro: "all harness writes are atomic — write temp file,
/// then move over the target"). A crash mid-write can leave a stray <c>*.tmp</c> but
/// never a half-written target, so <c>state.json</c>/<c>run.json</c> are always either the
/// old or the new content, never a torn blend.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Write <paramref name="content"/> to <paramref name="path"/> atomically: a sibling temp
    /// file is written and flushed, then moved over the target (overwriting if present).
    /// The parent directory is created if missing.
    /// </summary>
    public static void WriteAllText(string path, string content)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException($"Path has no directory: {path}", nameof(path));
        Directory.CreateDirectory(directory);

        // Keep the temp file in the same directory so the move is a same-volume rename.
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(tempPath, content, Utf8NoBom);
            File.Move(tempPath, path, overwrite: true);
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
}
