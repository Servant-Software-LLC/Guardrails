namespace Guardrails.Integration.Tests;

/// <summary>
/// Picks the shell that integration tests use to spawn REAL child processes: PowerShell on
/// Windows, <c>bash</c> elsewhere — the same OS split <see cref="Guardrails.Core.Execution.InterpreterMap"/>
/// applies to <c>.ps1</c> / <c>.sh</c> guardrail scripts.
/// </summary>
internal static class TestShell
{
    /// <summary>
    /// The Windows shell to launch: PowerShell 7 (<c>pwsh</c>) when present, else Windows
    /// PowerShell 5.1 (<c>powershell</c>) — mirrors <see cref="Guardrails.Core.Execution.InterpreterMap"/>'s
    /// fallback so a box without pwsh still runs the tests. Both expose
    /// <c>[Console]::Out</c> / <c>[Console]::OpenStandard*()</c> identically.
    /// </summary>
    public static string WindowsShell { get; } = ResolveWindowsShell();

    private static string ResolveWindowsShell()
    {
        string[] path = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (string exe in new[] { "pwsh.exe", "powershell.exe" })
        {
            if (path.Any(dir => File.Exists(Path.Combine(dir, exe))))
            {
                return Path.GetFileNameWithoutExtension(exe);
            }
        }

        return "pwsh";
    }
}
