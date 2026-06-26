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
    /// Strip a single trailing directory separator from a plan-folder argument so the plan name
    /// derived downstream (<c>Path.GetFileName</c> in branch naming, log paths, workspace
    /// derivation) is never empty. Issue #160: <c>guardrails run .\plans\0009-foo\</c> with a
    /// trailing separator made <c>Path.GetFileName</c> return <c>""</c>, the harness built the
    /// invalid branch name <c>guardrails/</c>, and git exited 128. <see
    /// cref="Path.TrimEndingDirectorySeparator(string)"/> covers both
    /// <see cref="Path.DirectorySeparatorChar"/> and <see cref="Path.AltDirectorySeparatorChar"/>
    /// and deliberately leaves a bare root (e.g. <c>C:\</c> or <c>/</c>) untouched.
    /// </summary>
    public static string Normalize(string value) =>
        string.IsNullOrEmpty(value) ? value : Path.TrimEndingDirectorySeparator(value);

    /// <summary>
    /// Resolve as <see cref="Resolve"/> does, then apply the plan-file → task-folder fixup
    /// (<see cref="ResolveMarkdownArgument"/>): when the folder was omitted, print one line
    /// naming the current directory; when the user passed the plan SOURCE FILE (a <c>.md</c>
    /// path, or any existing file) whose sibling task folder exists, silently switch to that
    /// folder and print one info line. Used by every plan command's action so the behaviour
    /// stays consistent. A genuinely-bad path falls through unchanged so the existing
    /// <c>GR1001</c> "Plan folder does not exist" error still fires downstream.
    ///
    /// <para>A trailing directory separator is stripped first (<see cref="Normalize"/>, issue
    /// #160) so the plan name derived downstream is never empty.</para>
    /// </summary>
    public static string ResolveAndAnnounce(string? value, TextWriter output)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            string current = Directory.GetCurrentDirectory();
            output.WriteLine($"Using current directory: {current}");
            return current;
        }

        return ResolveMarkdownArgument(Normalize(value), output);
    }

    /// <summary>
    /// The two-artifact fixup for issue #16. The <c>/plan-breakdown</c> workflow produces two
    /// adjacent artifacts with the same stem — the authored source <c>plans/0003-foo.md</c> and
    /// the generated task folder <c>plans/0003-foo/</c> — and a user naturally has the <c>.md</c>
    /// path in mind (or in shell history). When the given path is the plan file rather than the
    /// task folder AND a sibling folder with the same stem exists, resolve to that folder and
    /// announce it once; otherwise return the value untouched so the genuine bad-path case still
    /// reaches the downstream <c>GR1001</c> error.
    ///
    /// <para>Resolution fires when the path either ends with <c>.md</c> (ordinal,
    /// case-insensitive) OR resolves to an existing FILE rather than a directory — the issue's
    /// "resolves to a file rather than a directory" clause covers a plan file with a different
    /// extension. The candidate folder is the path with its extension stripped (concretely, the
    /// containing directory plus the filename without its extension); it is used only when it
    /// already exists as a directory. A value that is already a directory has no extension to
    /// strip and is a directory, so it passes through unchanged.</para>
    /// </summary>
    public static string ResolveMarkdownArgument(string value, TextWriter output)
    {
        if (!TryResolveMarkdownArgument(value, out string folder))
        {
            return value;
        }

        output.WriteLine($"info: resolved plan file → task folder \"{folder}\"");
        return folder;
    }

    /// <summary>
    /// Pure core of <see cref="ResolveMarkdownArgument"/>: decide whether <paramref name="value"/>
    /// is a plan SOURCE FILE whose sibling task folder exists, and if so produce that folder.
    /// Returns <see langword="true"/> and sets <paramref name="folder"/> to the resolved task
    /// folder only when resolution applies; otherwise returns <see langword="false"/> and leaves
    /// <paramref name="folder"/> as <paramref name="value"/> (the caller then proceeds with the
    /// original path, preserving the existing missing-folder error). Touches the filesystem only
    /// to probe for the candidate directory; emits no output.
    /// </summary>
    public static bool TryResolveMarkdownArgument(string value, out string folder)
    {
        folder = value;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        bool looksLikeMarkdown = value.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

        // Either an explicit .md suffix, or any path that exists on disk as a file (not a
        // directory) — the issue's "resolves to a file rather than a directory" clause. A path
        // that is already a directory, or does not exist at all and has no .md suffix, is left
        // alone.
        if (!looksLikeMarkdown && !(File.Exists(value) && !Directory.Exists(value)))
        {
            return false;
        }

        // Candidate task folder = directory part + filename WITHOUT its extension. Strip the
        // trailing separator that Path.Combine would otherwise leave when the file sits at the
        // path root (e.g. "foo.md" → directory "" → candidate "foo").
        string? directory = Path.GetDirectoryName(value);
        string stem = Path.GetFileNameWithoutExtension(value);
        if (string.IsNullOrEmpty(stem))
        {
            return false;
        }

        string candidate = string.IsNullOrEmpty(directory) ? stem : Path.Combine(directory, stem);

        if (!Directory.Exists(candidate))
        {
            return false;
        }

        folder = candidate;
        return true;
    }
}
