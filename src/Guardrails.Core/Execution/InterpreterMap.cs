using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// Maps a script/executable file to the concrete command that runs it (SSOT §5.2).
/// Built-in defaults are extended/overridden by <c>guardrails.json: interpreters</c>.
/// Each candidate is a template of tokens where <c>{script}</c> is replaced by the
/// script path and <c>{args}</c> by the action/guardrail args (defaulting to args
/// appended after the script). The first candidate whose executable resolves on PATH
/// wins; resolution is cached via the injected <see cref="IExecutableProbe"/>.
/// </summary>
public sealed class InterpreterMap
{
    /// <summary>Outcome categories for an interpreter resolution attempt.</summary>
    public enum Status
    {
        /// <summary>An interpreter was found and substituted.</summary>
        Resolved,

        /// <summary>The extension is known but no candidate executable resolves on PATH.</summary>
        NotOnPath,

        /// <summary>The extension is only valid on a different OS (e.g. .cmd off Windows).</summary>
        WrongPlatform
    }

    /// <summary>Result of resolving an interpreter for one script file.</summary>
    public sealed record Resolution
    {
        public required Status Status { get; init; }

        /// <summary>The runnable command when <see cref="Status"/> is <see cref="Status.Resolved"/>; otherwise null.</summary>
        public ResolvedCommand? Command { get; init; }

        /// <summary>The candidate executable names that were probed (for diagnostics).</summary>
        public required IReadOnlyList<string> ProbedExecutables { get; init; }
    }

    private const string ScriptToken = "{script}";
    private const string ArgsToken = "{args}";

    private readonly IExecutableProbe _probe;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _overrides;

    public InterpreterMap(
        IExecutableProbe probe,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? configInterpreters = null)
    {
        _probe = probe;
        _overrides = configInterpreters ?? new Dictionary<string, IReadOnlyList<string>>();
    }

    /// <summary>Convenience factory wiring the real PATH probe.</summary>
    public static InterpreterMap CreateDefault(RunConfig config) =>
        new(new PathExecutableProbe(), config.Interpreters);

    /// <summary>
    /// Resolve the command to run <paramref name="scriptPath"/> with <paramref name="args"/>.
    /// Extensionless / <c>.exe</c> files and <c>.dll</c> files always resolve (direct spawn
    /// or via dotnet) without a PATH probe of the script itself.
    /// </summary>
    public Resolution Resolve(string scriptPath, IReadOnlyList<string> args)
    {
        string extension = Path.GetExtension(scriptPath).ToLowerInvariant();

        // Wrong-platform extensions fail loudly regardless of config.
        if ((extension == ".cmd" || extension == ".bat") && !OperatingSystem.IsWindows())
        {
            return new Resolution { Status = Status.WrongPlatform, ProbedExecutables = ["cmd"] };
        }

        IReadOnlyList<IReadOnlyList<string>> candidates = CandidateTemplates(extension);
        var probed = new List<string>();

        foreach (IReadOnlyList<string> template in candidates)
        {
            string executable = template[0];
            probed.Add(executable);

            // Direct-spawn of the script/dll: the "executable" IS the script (or dotnet,
            // already covered as a normal candidate). Probe the launcher, not the script.
            bool isDirectScript = executable == ScriptToken;
            if (isDirectScript || _probe.Exists(executable))
            {
                return new Resolution
                {
                    Status = Status.Resolved,
                    Command = Substitute(template, scriptPath, args),
                    ProbedExecutables = probed
                };
            }
        }

        return new Resolution { Status = Status.NotOnPath, ProbedExecutables = probed };
    }

    /// <summary>The ordered candidate templates for an extension. Config overrides replace the built-ins.</summary>
    private IReadOnlyList<IReadOnlyList<string>> CandidateTemplates(string extension)
    {
        if (_overrides.TryGetValue(extension, out IReadOnlyList<string>? overrideTemplate))
        {
            return [overrideTemplate];
        }

        return BuiltInTemplates(extension);
    }

    private static IReadOnlyList<IReadOnlyList<string>> BuiltInTemplates(string extension) => extension switch
    {
        ".ps1" => OperatingSystem.IsWindows()
            ? [PwshTemplate, PowershellTemplate]
            : [PwshTemplate],
        ".sh" => [["bash", ScriptToken, ArgsToken]],
        ".py" => [["python3", ScriptToken, ArgsToken], ["python", ScriptToken, ArgsToken]],
        ".cmd" or ".bat" => [["cmd", "/c", ScriptToken, ArgsToken]],
        ".dll" => [["dotnet", ScriptToken, ArgsToken]],
        // none / .exe / anything else → direct spawn of the file itself.
        _ => [[ScriptToken, ArgsToken]]
    };

    private static IReadOnlyList<string> PwshTemplate =>
        ["pwsh", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptToken, ArgsToken];

    private static IReadOnlyList<string> PowershellTemplate =>
        ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptToken, ArgsToken];

    private static ResolvedCommand Substitute(
        IReadOnlyList<string> template,
        string scriptPath,
        IReadOnlyList<string> args)
    {
        var expanded = new List<string>(template.Count + args.Count);
        foreach (string token in template)
        {
            if (token == ScriptToken)
            {
                expanded.Add(scriptPath);
            }
            else if (token == ArgsToken)
            {
                expanded.AddRange(args);
            }
            else
            {
                expanded.Add(token);
            }
        }

        return new ResolvedCommand
        {
            Executable = expanded[0],
            Arguments = expanded.Skip(1).ToList()
        };
    }
}
