using System.CommandLine;

namespace Guardrails.Cli.Commands;

/// <summary>
/// Shared factory + resolution for the optional <c>folder</c> positional argument every
/// plan command accepts. Omitting it defaults to the current working directory, so a user
/// standing inside a plan folder can run <c>guardrails &lt;command&gt;</c> with no path.
/// </summary>
public static class FolderArgument
{
    /// <summary>
    /// Create the <c>folder</c> argument: optional (<see cref="ArgumentArity.ZeroOrOne"/>),
    /// defaulting to the current directory when omitted.
    /// </summary>
    public static Argument<string?> Create() => new("folder")
    {
        Description = "Path to the plan folder (contains guardrails.json). Defaults to the current directory when omitted.",
        Arity = ArgumentArity.ZeroOrOne
    };

    /// <summary>
    /// Resolve a parsed folder value to a concrete path: the value as given, or the current
    /// working directory when null/empty/whitespace.
    /// </summary>
    public static string Resolve(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Directory.GetCurrentDirectory() : value;

    /// <summary>
    /// Resolve as <see cref="Resolve"/> does, and — only when the folder was omitted — print
    /// one line naming the current directory (to <paramref name="output"/>) so the user can
    /// confirm what the command ran against. Used by every plan command's action so the
    /// behaviour stays consistent.
    /// </summary>
    public static string ResolveAndAnnounce(string? value, TextWriter output)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        string resolved = Directory.GetCurrentDirectory();
        output.WriteLine($"Using current directory: {resolved}");
        return resolved;
    }
}
