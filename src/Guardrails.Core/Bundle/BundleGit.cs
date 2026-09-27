using System.ComponentModel;
using System.Globalization;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Bundle;

/// <summary>
/// §17.2 item 5: git takes no optional locks anywhere in the bundling process. <c>GIT_OPTIONAL_LOCKS=0</c>, plus
/// <c>core.fsmonitor=false</c> and <c>core.untrackedCache=false</c> APPENDED through <c>GIT_CONFIG_COUNT</c>, so an
/// existing <c>GIT_CONFIG_KEY_*</c> / <c>GIT_CONFIG_VALUE_*</c> pair (a <c>safe.directory</c>, say) is never
/// overwritten. Every git child the process spawns — its own calls and the validate probes — inherits them.
/// </summary>
public static class BundleGitEnvironment
{
    /// <summary>What to set, and whether the existing count was unparsable (MANIFEST.md notes that).</summary>
    public sealed record Plan(IReadOnlyList<KeyValuePair<string, string>> Variables, bool CountWasUnparsable, int AppendedAt);

    /// <summary>
    /// The variables to set given the current environment (<paramref name="get"/>). With <c>N</c> the existing
    /// <c>GIT_CONFIG_COUNT</c> (0 when absent or unparsable), the keys land at <c>N</c> and <c>N+1</c> and the count
    /// becomes <c>N+2</c>.
    /// </summary>
    public static Plan Compute(Func<string, string?> get)
    {
        string? raw = get("GIT_CONFIG_COUNT");
        bool unparsable = false;
        int n = 0;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (!int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 0)
            {
                n = 0;
                unparsable = true;
            }
        }

        string at = n.ToString(CultureInfo.InvariantCulture);
        string next = (n + 1).ToString(CultureInfo.InvariantCulture);
        return new Plan(
        [
            new("GIT_OPTIONAL_LOCKS", "0"),
            new("GIT_CONFIG_KEY_" + at, "core.fsmonitor"),
            new("GIT_CONFIG_VALUE_" + at, "false"),
            new("GIT_CONFIG_KEY_" + next, "core.untrackedCache"),
            new("GIT_CONFIG_VALUE_" + next, "false"),
            new("GIT_CONFIG_COUNT", (n + 2).ToString(CultureInfo.InvariantCulture)),
        ], unparsable, n);
    }

    /// <summary>Apply <see cref="Compute"/>'s plan through <paramref name="set"/>. Returns the plan applied.</summary>
    public static Plan Apply(Func<string, string?> get, Action<string, string> set)
    {
        Plan plan = Compute(get);
        foreach (KeyValuePair<string, string> variable in plan.Variables)
        {
            set(variable.Key, variable.Value);
        }

        return plan;
    }

    /// <summary>Apply to THIS process's environment, which every child inherits.</summary>
    public static Plan ApplyToProcess() =>
        Apply(Environment.GetEnvironmentVariable, (name, value) => Environment.SetEnvironmentVariable(name, value));
}

/// <summary>The outcome of one git (or tool) invocation. <see cref="NotFound"/>: the executable is not on PATH.</summary>
public sealed record BundleProcessResult(
    int ExitCode, string StandardOutput, string StandardError, bool TimedOut, bool NotFound, bool Cancelled = false)
{
    /// <summary>Whether the call ran and exited 0.</summary>
    public bool Succeeded => !TimedOut && !NotFound && !Cancelled && ExitCode == 0;
}

/// <summary>The git calls the bundle makes (§17.2 item 5). A seam so tests decide git's answer.</summary>
public interface IBundleGit
{
    /// <summary>Run git with <paramref name="arguments"/> in <paramref name="workingDirectory"/>, under a 30 s timeout.</summary>
    BundleProcessResult Run(string workingDirectory, IReadOnlyList<string> arguments);
}

/// <summary>Runs a command through <see cref="ProcessRunner"/> (argument list, never a concatenated string).</summary>
public static class BundleProcess
{
    private static readonly AsyncLocal<CancellationToken> Ambient = new();

    /// <summary>
    /// #805 N-c: every child this flow starts (git, ps, pwsh, tool --version) is tied to <paramref name="token"/> until the
    /// returned scope is disposed; Ctrl-C cancels it, and <see cref="ProcessRunner"/> kills the child's whole tree.
    /// </summary>
    public static IDisposable CancelWith(CancellationToken token)
    {
        CancellationToken previous = Ambient.Value;
        Ambient.Value = token;
        return new Scope(() => Ambient.Value = previous);
    }

    /// <summary>Run <paramref name="executable"/>; a missing executable is <see cref="BundleProcessResult.NotFound"/>, not a throw.</summary>
    public static BundleProcessResult Run(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout)
    {
        CancellationToken token = Ambient.Value;
        if (token.IsCancellationRequested)
        {
            return new BundleProcessResult(-1, string.Empty, "cancelled", TimedOut: false, NotFound: false, Cancelled: true);
        }

        try
        {
            ProcessResult result = new ProcessRunner()
                .RunAsync(new ResolvedCommand { Executable = executable, Arguments = arguments }, workingDirectory,
                    new Dictionary<string, string>(), timeout, token)
                .GetAwaiter().GetResult();
            return new BundleProcessResult(result.ExitCode, result.StandardOutput, result.StandardError, result.TimedOut,
                NotFound: false, Cancelled: token.IsCancellationRequested && !result.TimedOut);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return new BundleProcessResult(-1, string.Empty, ex.Message, TimedOut: false, NotFound: true);
        }
    }
}

internal sealed class Scope(Action dispose) : IDisposable
{
    public void Dispose() => dispose();
}

/// <summary>The real git: <c>git</c> on PATH, 30 s per call.</summary>
public sealed class ProcessBundleGit : IBundleGit
{
    /// <summary>§17.2 item 5's per-call timeout.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public BundleProcessResult Run(string workingDirectory, IReadOnlyList<string> arguments) =>
        BundleProcess.Run("git", arguments, workingDirectory, Timeout);
}
