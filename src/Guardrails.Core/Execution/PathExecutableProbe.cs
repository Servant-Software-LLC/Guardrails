using System.Collections.Concurrent;

namespace Guardrails.Core.Execution;

/// <summary>
/// Default <see cref="IExecutableProbe"/>: resolves a command against the real PATH
/// (honoring PATHEXT on Windows), or treats an existing absolute/relative file as
/// runnable. Results are cached for the lifetime of the instance.
/// </summary>
public sealed class PathExecutableProbe : IExecutableProbe
{
    private readonly ConcurrentDictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);

    public bool Exists(string command) => _cache.GetOrAdd(command, Resolve);

    private static bool Resolve(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        // An explicit path (absolute or containing a separator) is resolved as a file.
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(command) || FileExistsWithPathExt(command);
        }

        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
        {
            return false;
        }

        foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = dir.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            string candidate = Path.Combine(trimmed, command);
            if (File.Exists(candidate) || FileExistsWithPathExt(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool FileExistsWithPathExt(string pathWithoutExtension)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string? pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        string[] extensions = string.IsNullOrEmpty(pathExt)
            ? [".EXE", ".CMD", ".BAT", ".COM"]
            : pathExt.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (string ext in extensions)
        {
            if (File.Exists(pathWithoutExtension + ext))
            {
                return true;
            }
        }

        return false;
    }
}
