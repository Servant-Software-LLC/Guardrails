using System.Collections;
using System.CommandLine;
using System.Globalization;
using System.Runtime.InteropServices;
using Guardrails.Core.Bundle;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Cli.Commands;

/// <summary>
/// Everything <c>guardrails bundle</c> asks the machine, so a test decides it: the home directory, the environment,
/// the clock, tool versions, the process table, git, the working-tree check, and the process-wide git settings.
/// </summary>
public sealed record BundleCommandHost
{
    /// <summary>The home directory (the default destination is <c>~/guardrails-bundles/</c>).</summary>
    public required Func<string> HomeDirectory { get; init; }

    /// <summary>The bundling shell's environment, read at bundle time.</summary>
    public required Func<IReadOnlyDictionary<string, string>> Environment { get; init; }

    /// <summary>The <c>Bundled at</c> clock.</summary>
    public required Func<DateTimeOffset> Now { get; init; }

    /// <summary><c>claude</c>/<c>agent</c>/<c>dotnet</c>/<c>git</c> <c>--version</c>.</summary>
    public required Func<IReadOnlyList<BundleToolVersion>> ToolVersions { get; init; }

    /// <summary>The process table liveness is assessed against.</summary>
    public required IProcessProbe ProcessProbe { get; init; }

    /// <summary>The git calls of §17.2 item 5.</summary>
    public required IBundleGit Git { get; init; }

    /// <summary>§17.8: whether an EXISTING directory lies inside a git working tree.</summary>
    public required Func<string, bool> IsInsideGitWorkTree { get; init; }

    /// <summary>§17.2 item 5: set the process-wide git settings; returns what was applied.</summary>
    public required Func<BundleGitEnvironment.Plan> ApplyGitEnvironment { get; init; }

    /// <summary>The owner process's descendants for SUMMARY's Process tree block (#805 S1). Defaults to the real table.</summary>
    public Func<int, BundleProcessTree> ProcessTree { get; init; } = SystemBundleProcessTree.Capture;

    /// <summary>The OS user name pass 3 anonymizes (#805). Defaults to the real one; a test injects its own.</summary>
    public Func<string> UserName { get; init; } = () => System.Environment.UserName;

    /// <summary>A file's size and last-write time for SUMMARY's Live files block (#805 S1). Defaults to the real stat.</summary>
    public Func<string, BundleFileStat?> Stat { get; init; } = BundleFileStat.Of;

    /// <summary>The real machine.</summary>
    public static BundleCommandHost Real { get; } = new()
    {
        HomeDirectory = () => System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
        Environment = ReadEnvironment,
        Now = () => DateTimeOffset.UtcNow,
        ToolVersions = RealToolVersions,
        ProcessProbe = SystemProcessProbe.Instance,
        Git = new ProcessBundleGit(),
        IsInsideGitWorkTree = directory =>
            BundleProcess.Run("git", ["rev-parse", "--is-inside-work-tree"], directory, ProcessBundleGit.Timeout) is
                { Succeeded: true } result
            && result.StandardOutput.Trim() == "true",
        ApplyGitEnvironment = BundleGitEnvironment.ApplyToProcess,
    };

    private static IReadOnlyDictionary<string, string> ReadEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry variable in System.Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string name && variable.Value is string value)
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    private static IReadOnlyList<BundleToolVersion> RealToolVersions()
    {
        var versions = new List<BundleToolVersion>();
        string? path = System.Environment.GetEnvironmentVariable("PATH");
        foreach (string tool in new[] { "claude", "agent", "dotnet", "git" })
        {
            string? resolved = PathExecutableProbe.ResolveFullPath(tool, path);
            if (resolved is null)
            {
                versions.Add(new BundleToolVersion(tool, "not on PATH"));
                continue;
            }

            BundleProcessResult result = BundleProcess.Run(resolved, ["--version"], Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(10));
            string version = result switch
            {
                { NotFound: true } => "not on PATH",
                { TimedOut: true } => "timed out",
                _ => FirstLine(result.StandardOutput) ?? FirstLine(result.StandardError) ?? $"exited {result.ExitCode}",
            };
            versions.Add(new BundleToolVersion(tool, version));
        }

        return versions;
    }

    private static string? FirstLine(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
}

/// <summary>
/// <c>guardrails bundle [folder]</c> (SSOT §17, issue #799): one run's evidence, packaged deterministically into one zip
/// that is safe to attach to a public GitHub issue by default — credentials scrubbed, proprietary content included
/// under a loud warning unless <c>--lean</c>. Read-only toward the run: it writes nothing under the plan folder, the
/// logs or any worktree, takes no lock, opens no network connection, and runs no model.
/// </summary>
public static class BundleCommand
{
    /// <summary>The default destination directory under the home directory (§17.8).</summary>
    public const string DefaultDirectoryName = "guardrails-bundles";

    /// <summary>The upload hint printed after the path (§17.1): <c>gh</c> cannot attach files to an issue.</summary>
    public const string UploadHint =
        "`gh` cannot attach files to an issue. Drag this zip into the issue's comment box in the browser, or attach\n" +
        "it to a gist or a release and paste the link.";

    /// <summary>Build the command. <paramref name="host"/> null means the real machine.</summary>
    public static Command Create(IConsoleIo io, BundleCommandHost? host = null)
    {
        var folderArgument = FolderArgument.Create();
        var runOption = new Option<string?>("--run") { Description = "Bundle an earlier run's logs/<runId>/ (default: the journal's run)." };
        var taskOption = new Option<string[]>("--task")
        {
            Description = "Narrow per-task evidence to this task. Repeatable. Run-level files are always included.",
            AllowMultipleArgumentsPerToken = false,
        };
        var outOption = new Option<string?>("--out") { Description = "The zip to write (default ~/guardrails-bundles/<plan>-<runId>[-<task>].zip)." };
        var dirOption = new Option<string?>("--dir") { Description = "Write the bundle tree unzipped into this directory (absent or empty)." };
        var forcePathOption = new Option<bool>("--force-path") { Description = "Allow a destination inside a git working tree." };
        var maxSizeOption = new Option<string?>("--max-size") { Description = "Cap on the finished zip, in MiB (default 20)." };
        var leanOption = new Option<bool>("--lean")
        {
            Description = "Withhold prompts, transcripts, stream logs, gateway sessions and patches (for a public issue when the code is private).",
        };
        var diffOption = new Option<bool>("--include-worktree-diff") { Description = "Add the full git diff of the integration worktree and each selected segment. Refused with --lean." };
        var withoutAgentTextOption = new Option<bool>("--without-agent-text")
        {
            Description = "Remove all agent-derived free text, run-wide. Clears the D1 refusal without the token value.",
        };
        var keepPathsOption = new Option<bool>("--keep-paths") { Description = "Do not anonymize paths." };
        var noRedactOption = new Option<bool>("--no-redact") { Description = "Skip ALL credential scrubbing (this also clears the D1 refusal). The output's name always gets -UNREDACTED, --out and --dir included. Only for a bundle that stays private; never for a public issue." };

        var command = new Command("bundle", "Package one run's evidence into a redacted zip for a GitHub issue (read-only).");
        command.Add(folderArgument);
        foreach (Option option in new Option[]
                 {
                     runOption, taskOption, outOption, dirOption, forcePathOption, maxSizeOption, leanOption, diffOption,
                     withoutAgentTextOption, keepPathsOption, noRedactOption,
                 })
        {
            command.Add(option);
        }

        command.SetAction((parseResult, cancellationToken) => Task.FromResult(Run(
            new Arguments
            {
                Folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Error),
                RunId = parseResult.GetValue(runOption),
                Tasks = parseResult.GetValue(taskOption) ?? [],
                Out = parseResult.GetValue(outOption),
                Dir = parseResult.GetValue(dirOption),
                ForcePath = parseResult.GetValue(forcePathOption),
                MaxSize = parseResult.GetValue(maxSizeOption),
                Lean = parseResult.GetValue(leanOption),
                IncludeWorktreeDiff = parseResult.GetValue(diffOption),
                WithoutAgentText = parseResult.GetValue(withoutAgentTextOption),
                KeepPaths = parseResult.GetValue(keepPathsOption),
                NoRedact = parseResult.GetValue(noRedactOption),
            },
            io,
            host ?? BundleCommandHost.Real,
            cancellationToken)));
        return command;
    }

    private sealed record Arguments
    {
        public required string Folder { get; init; }
        public string? RunId { get; init; }
        public IReadOnlyList<string> Tasks { get; init; } = [];
        public string? Out { get; init; }
        public string? Dir { get; init; }
        public bool ForcePath { get; init; }
        public string? MaxSize { get; init; }
        public bool Lean { get; init; }
        public bool IncludeWorktreeDiff { get; init; }
        public bool WithoutAgentText { get; init; }
        public bool KeepPaths { get; init; }
        public bool NoRedact { get; init; }
    }

    private static int Refuse(IConsoleIo io, string message)
    {
        io.Error.WriteLine($"guardrails bundle: refused: {message}");
        return ExitCodes.HarnessError;
    }

    private static int Run(Arguments args, IConsoleIo io, BundleCommandHost host, CancellationToken cancellationToken)
    {
        // #805 N-c: Ctrl-C kills every child the bundle started (git, ps, pwsh, tool --version) and writes nothing.
        using IDisposable children = BundleProcess.CancelWith(cancellationToken);
        // Argument refusals: all exit 1, all before anything is read (§17.1).
        if (args.Out is not null && args.Dir is not null)
        {
            return Refuse(io, "--out and --dir are mutually exclusive.");
        }

        // #814: a root ("C:\", "C:", "\\srv\share\", "/") or a "."/".." last segment is never a sane bundle destination,
        // and it has no name to mark under --no-redact. Refused, redacted or not, before anything is read.
        foreach ((string flag, string? value) in new[] { ("--out", args.Out), ("--dir", args.Dir) })
        {
            if (value is not null && !HasNamedLastSegment(value))
            {
                return Refuse(io, $"{flag} {value} is not a usable bundle destination: its last path segment is a root, '.' or '..'. "
                    + $"Name a {(flag == "--out" ? "file" : "folder")} inside it instead.");
            }
        }

        // #814 W1: --no-redact ALWAYS marks what it writes. An explicit --out or --dir gets the -UNREDACTED suffix too,
        // so an unscrubbed bundle is never named like a scrubbed one; every later check sees the marked path.
        if (args.NoRedact)
        {
            args = args with
            {
                Out = args.Out is { } unmarkedOut ? MarkUnredacted(unmarkedOut, directory: false) : null,
                Dir = args.Dir is { } unmarkedDir ? MarkUnredacted(unmarkedDir, directory: true) : null,
            };
        }

        if (args.Lean && args.IncludeWorktreeDiff)
        {
            return Refuse(io, "--include-worktree-diff adds source code, which --lean withholds; pass one or the other.");
        }

        long maxSizeBytes = 20L * 1024 * 1024;
        if (args.MaxSize is not null)
        {
            if (!double.TryParse(args.MaxSize, NumberStyles.Float, CultureInfo.InvariantCulture, out double mib)
                || !double.IsFinite(mib) || mib <= 0)
            {
                return Refuse(io, $"--max-size must be a positive number of MiB (got '{args.MaxSize}').");
            }

            maxSizeBytes = (long)Math.Floor(mib * 1024 * 1024);
        }

        // #805 S4: --run names one directory directly under logs/, never a path.
        if (args.RunId is { } requestedRun && !BundleBuilder.IsSafeRunId(requestedRun))
        {
            return Refuse(io, $"--run must be a single run id, not a path ('{requestedRun}').");
        }

        // §17.2 item 5: before ANY git runs (the path refusal's rev-parse, validate's probes, the evidence calls).
        BundleGitEnvironment.Plan gitPlan = host.ApplyGitEnvironment();

        // The plan's model, loaded without validate's probes: validate re-runs later, after run.json is read first.
        PlanLoadResult load = new PlanLoader().Load(args.Folder);
        if (load.Plan is not { } plan)
        {
            PlanProbe.PrintDiagnostics(load.Diagnostics, io.Error);
            return Refuse(io, "the plan could not be loaded.");
        }

        List<string> tasks = [.. args.Tasks.Distinct(StringComparer.Ordinal)];
        List<string> unknown = [.. tasks.Where(t => plan.Tasks.All(task => task.Id != t))];
        if (unknown.Count > 0)
        {
            return Refuse(io, $"the plan declares no task {string.Join(", ", unknown.Select(t => $"'{t}'"))}.");
        }

        if (args.RunId is { } runId && !Directory.Exists(Path.Combine(plan.PlanDirectory, "logs", runId)))
        {
            return Refuse(io, $"there is no logs/{runId}/ under {plan.PlanDirectory}.");
        }

        if (args.RunId is null && !File.Exists(RunJournal.PathFor(plan.PlanDirectory)))
        {
            return Refuse(io, "this plan has no run journal (state/run.json), so there is no run to bundle. Pass --run <id> for an earlier run's logs.");
        }

        string home = host.HomeDirectory();
        string destinationDirectory = args.Dir is { } dir
            ? Path.GetFullPath(dir)
            : args.Out is { } outPath ? Path.GetDirectoryName(Path.GetFullPath(outPath))! : Path.Combine(home, DefaultDirectoryName);

        if (args.Dir is not null && Directory.Exists(destinationDirectory) && Directory.EnumerateFileSystemEntries(destinationDirectory).Any())
        {
            return Refuse(io, $"--dir {destinationDirectory} is not empty; the bundle never merges into, or deletes from, a directory it did not create.");
        }

        // #805 N4: never under the plan directory or the worktree root, even with --force-path: the bundle writes nothing
        // where the run writes.
        string destinationPath = args.Out is { } outFile ? Path.GetFullPath(outFile) : destinationDirectory;
        foreach ((string root, string what) in RunRoots(plan))
        {
            if (Core.Io.RealPath.IsUnder(destinationPath, root))
            {
                return Refuse(io, $"{destinationPath} is under the {what} ({root}); the bundle never writes where the run writes, "
                    + "and --force-path does not change that.");
            }
        }

        // §17.8: every destination, the default included, is refused inside a git working tree.
        if (!args.ForcePath && NearestExistingAncestor(destinationDirectory) is { } ancestor && host.IsInsideGitWorkTree(ancestor))
        {
            return Refuse(io,
                $"{destinationDirectory} is inside a git working tree ({ancestor}), where the bundle could be committed. "
                + "Choose a destination outside every repository, or pass --force-path.");
        }

        IReadOnlyDictionary<string, string> environment = host.Environment();

        // §17.6.5 D1, run-scoped: the scrub would be blind to a token this shell cannot see.
        if (!args.NoRedact && !args.WithoutAgentText && BundleD1.UnsetVariables(plan, environment) is { Count: > 0 } unset)
        {
            foreach (string line in BundleD1.RefusalLines(unset, environment))
            {
                io.Error.WriteLine(line);
            }

            return ExitCodes.HarnessError;
        }

        if (args.NoRedact)
        {
            io.Error.WriteLine(BundleBuilder.NotRedactedLine);
        }

        if (!args.Lean)
        {
            io.Error.WriteLine(BundleBuilder.FullBundleWarning);
        }

        var options = new BundleOptions
        {
            RunId = args.RunId,
            Tasks = tasks,
            MaxSizeBytes = maxSizeBytes,
            Lean = args.Lean,
            IncludeWorktreeDiff = args.IncludeWorktreeDiff,
            WithoutAgentText = args.WithoutAgentText,
            KeepPaths = args.KeepPaths,
            NoRedact = args.NoRedact,
        };

        BundleOutcome outcome;
        try
        {
            outcome = BundleBuilder.Build(
                new BundleRequest { Plan = plan, Options = options },
                Probes(args.Folder, plan, environment, home, host, gitPlan));
        }
        catch (BundleRefusedException ex)
        {
            return Refuse(io, ex.Message);
        }

        if (args.Lean)
        {
            io.Error.WriteLine(BundleBuilder.LeanNote(outcome.LeanWithheld.Count));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Refuse(io, "cancelled; the child processes were stopped and nothing was written.");
        }

        string destination;
        long size;
        if (args.Dir is not null)
        {
            destination = destinationDirectory;
            WriteTree(destination, outcome.Entries);
            size = outcome.Entries.Sum(e => (long)e.Value.Length);
        }
        else
        {
            destination = args.Out is { } explicitOut
                ? Path.GetFullPath(explicitOut)
                : Path.Combine(destinationDirectory, FileName(plan, outcome.RunId, tasks, args.NoRedact));
            WriteZip(destination, outcome.Zip);
            size = outcome.Zip.LongLength;
        }

        if (args.NoRedact)
        {
            io.Error.WriteLine(BundleBuilder.NotRedactedLine);
        }

        io.Out.WriteLine($"{destination} ({FormatSize(size)})");
        io.Out.WriteLine(UploadHint);

        if (outcome.OverCap)
        {
            io.Error.WriteLine(
                $"guardrails bundle: the bundle is {FormatSize(outcome.Zip.LongLength)}, still over --max-size "
                + $"{FormatSize(maxSizeBytes)} after every trim tier. It was written anyway; narrow it with --task <id>.");
            return ExitCodes.HarnessError;
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// The default file name (§17.1): <c>&lt;plan&gt;-&lt;runId&gt;</c>, then <c>-&lt;task&gt;</c> for exactly one
    /// <c>--task</c> or <c>-tasks-&lt;N&gt;</c> for several, then <c>-UNREDACTED</c> under <c>--no-redact</c>.
    /// </summary>
    public static string FileName(PlanDefinition plan, string runId, IReadOnlyList<string> distinctTasks, bool noRedact)
    {
        string name = $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.PlanDirectory))}-{runId}";
        name += distinctTasks.Count switch
        {
            0 => string.Empty,
            1 => $"-{distinctTasks[0]}",
            _ => $"-tasks-{distinctTasks.Count.ToString(CultureInfo.InvariantCulture)}",
        };
        return name + (noRedact ? UnredactedSuffix : string.Empty) + ".zip";
    }

    /// <summary>
    /// Whether a <c>--out</c>/<c>--dir</c> value ends in a real name: not empty, <c>.</c> or <c>..</c> as typed, and not
    /// a root once resolved (<c>C:\</c>, <c>C:</c>, <c>\\srv\share\</c>, <c>/</c>, or <c>a/..</c> reaching one).
    /// </summary>
    public static bool HasNamedLastSegment(string path)
    {
        string typed = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (typed.Length == 0 || typed is "." or "..")
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return Path.GetFileName(Path.TrimEndingDirectorySeparator(full)).Length > 0;
    }

    /// <summary>The <c>-UNREDACTED</c> suffix of a <c>--no-redact</c> bundle's name.</summary>
    public const string UnredactedSuffix = "-UNREDACTED";

    /// <summary>
    /// #814 W1: an explicit <c>--out</c> or <c>--dir</c> under <c>--no-redact</c>, marked. A file gets
    /// <c>-UNREDACTED</c> before its extension (<c>run.zip</c> → <c>run-UNREDACTED.zip</c>); a directory gets it
    /// appended to its last segment. A name that already contains <c>-UNREDACTED</c> is returned unchanged.
    /// </summary>
    public static string MarkUnredacted(string path, bool directory)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(path);
        string name = Path.GetFileName(trimmed);
        if (name.Contains(UnredactedSuffix, StringComparison.Ordinal))
        {
            return path;
        }

        string marked = directory
            ? name + UnredactedSuffix
            : Path.GetFileNameWithoutExtension(name) + UnredactedSuffix + Path.GetExtension(name);
        string? parent = Path.GetDirectoryName(trimmed);
        return string.IsNullOrEmpty(parent) ? marked : Path.Combine(parent, marked);
    }

    private static IEnumerable<(string Root, string What)> RunRoots(PlanDefinition plan)
    {
        yield return (Path.GetFullPath(plan.PlanDirectory), "plan directory");
        string? worktreeRoot = null;
        try
        {
            worktreeRoot = SchedulerFactory.WorktreeRootFor(plan);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // No worktree root to refuse under.
        }

        if (worktreeRoot is not null)
        {
            yield return (Path.GetFullPath(worktreeRoot), "worktree root");
        }
    }

    /// <summary>The nearest existing ancestor of <paramref name="path"/> (itself when it exists), or null.</summary>
    public static string? NearestExistingAncestor(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null && !Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current);
        }

        return current;
    }

    private static BundleProbes Probes(
        string folder, PlanDefinition plan, IReadOnlyDictionary<string, string> environment, string home,
        BundleCommandHost host, BundleGitEnvironment.Plan gitPlan)
    {
        string? worktreeRoot = null;
        try
        {
            worktreeRoot = SchedulerFactory.WorktreeRootFor(plan);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Anonymization simply leaves that root alone.
        }

        return new BundleProbes
        {
            Environment = environment,
            Now = host.Now,
            HarnessVersion = GuardrailsVersion.Current,
            BundlingOs = RuntimeInformation.OSDescription,
            ToolVersions = host.ToolVersions,
            Liveness = owner => RunLiveness.Assess(owner, RunLiveness.ThisHost(), host.ProcessProbe),
            ProcessTree = host.ProcessTree,
            Stat = host.Stat,
            Git = host.Git,
            Validate = () =>
            {
                var output = new StringWriter();
                int exit = ValidateCommand.Run(folder, new CapturingConsoleIo(output));
                output.WriteLine($"(validate exit code {exit.ToString(CultureInfo.InvariantCulture)})");
                return output.ToString();
            },
            Home = home,
            UserName = host.UserName(),
            CaseInsensitivePaths = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
            WorktreeRoot = worktreeRoot,
            Notes = gitPlan.CountWasUnparsable
                ? ["GIT_CONFIG_COUNT was not a number; it was treated as 0 and the bundle's git settings were appended at index 0."]
                : [],
        };
    }

    private static void WriteZip(string destination, byte[] zip)
    {
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, zip);
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static void WriteTree(string root, IReadOnlyList<KeyValuePair<string, byte[]>> entries)
    {
        Directory.CreateDirectory(root);
        foreach ((string path, byte[] content) in entries)
        {
            string target = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content);
        }
    }

    private static string FormatSize(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.0} MiB, {bytes} bytes");

    private sealed class CapturingConsoleIo(TextWriter output) : IConsoleIo
    {
        public TextWriter Out { get; } = output;

        public TextWriter Error { get; } = output;
    }
}
