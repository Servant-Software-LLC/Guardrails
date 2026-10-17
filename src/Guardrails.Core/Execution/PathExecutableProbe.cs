using System.Collections.Concurrent;

namespace Guardrails.Core.Execution;

/// <summary>
/// Default <see cref="IExecutableProbe"/>: resolves a command against the real PATH
/// (honoring PATHEXT on Windows), or treats an existing absolute/relative file as
/// runnable. Results are cached for the lifetime of the instance.
/// <para>
/// <see cref="ResolveFullPath"/> is the same rule returning WHERE the command resolved, so a runner that
/// must launch by full path (Cursor's Windows <c>agent.cmd</c> shim, #764) and <c>guardrails validate</c>'s
/// GR2009 PATH probe answer from ONE rule: <see cref="Exists"/> is true exactly when
/// <see cref="ResolveFullPath"/> returns a path.
/// </para>
/// </summary>
public sealed class PathExecutableProbe : IExecutableProbe
{
    private readonly ConcurrentDictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _pathVariable;

    /// <summary>Probe against the process's own <c>PATH</c>.</summary>
    public PathExecutableProbe()
    {
    }

    /// <summary>
    /// Probe against an explicit <c>PATH</c> value instead of the process's — a seam so a test can put a
    /// fixture directory on "the PATH" without mutating process-wide environment state.
    /// </summary>
    public PathExecutableProbe(string pathVariable) => _pathVariable = pathVariable;

    public bool Exists(string command) =>
        _cache.GetOrAdd(command, c => ResolveFullPath(c, _pathVariable ?? Environment.GetEnvironmentVariable("PATH")) is not null);

    /// <summary>
    /// The full path <paramref name="command"/> resolves to, or null when it resolves nowhere.
    /// <list type="bullet">
    /// <item>An explicit path (containing a separator) is resolved as a file: as given, then with each
    /// PATHEXT extension on Windows.</item>
    /// <item>A bare name is looked up directory by directory along <paramref name="pathVariable"/>. On
    /// Windows each directory tries the PATHEXT extensions FIRST and the exact name last. That order is what
    /// makes the result LAUNCHABLE: an npm-style install puts an extensionless POSIX shell script
    /// (<c>agent</c>) beside <c>agent.cmd</c>, and only the <c>.cmd</c> can be started by
    /// <c>CreateProcess</c>. The SET of commands that resolve is unchanged by the order, so GR2009's verdict
    /// is the same as before; only which file wins moved.</item>
    /// </list>
    /// </summary>
    /// <param name="command">The command as written in <c>guardrails.json</c>.</param>
    /// <param name="pathVariable">The <c>PATH</c> value to search (null or empty ⇒ nothing resolves).</param>
    public static string? ResolveFullPath(string command, string? pathVariable)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        // An explicit path (absolute or containing a separator) is resolved as a file.
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(command) ? Path.GetFullPath(command) : WithPathExt(command);
        }

        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        foreach (string dir in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = dir.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            string candidate = Path.Combine(trimmed, command);
            if (WithPathExt(candidate) is { } withExtension)
            {
                return withExtension;
            }

            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static string? WithPathExt(string pathWithoutExtension)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        string? pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        string[] extensions = string.IsNullOrEmpty(pathExt)
            ? [".EXE", ".CMD", ".BAT", ".COM"]
            : pathExt.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (string ext in extensions)
        {
            string candidate = pathWithoutExtension + ext;
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}
