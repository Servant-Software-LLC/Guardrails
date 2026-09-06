using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Guardrails.Core.Execution;

/// <summary>
/// Spawns child processes per the child-process contract (SSOT §5.1): arguments via
/// <see cref="ProcessStartInfo.ArgumentList"/> (never a concatenated shell string),
/// cwd = the resolved workspace, env vars injected, stdout/stderr captured, and a
/// timeout enforced with <c>Kill(entireProcessTree: true)</c>.
/// </summary>
public sealed class ProcessRunner
{
    /// <summary>Exit-code sentinel reported when a process is killed for exceeding its timeout.</summary>
    public const int TimeoutExitCode = -1;

    /// <summary>
    /// UTF-8 for every redirected stream (issue #55). Without an explicit encoding, .NET decodes a
    /// redirected child's stdout/stderr with <see cref="Console.OutputEncoding"/> — on Windows the
    /// host OEM console code page (CP437/850), NOT UTF-8 — so UTF-8 output from a child (e.g. Claude's
    /// em dash <c>—</c> = bytes <c>E2 80 94</c>) is mis-decoded into mojibake (<c>ΓÇö</c>) and
    /// persisted that way to claude-stream.jsonl / transcript.md / *.log. Pinning UTF-8 makes capture
    /// host-console-independent and round-trip-faithful.
    /// <para>
    /// The instance is shared with every OTHER child-process boundary in the harness via
    /// <see cref="ChildProcessEncoding"/> (issue #457) — the git runners that were still unpinned
    /// corrupted a tracked file through exactly this mechanism, so there is deliberately ONE definition
    /// of "the encoding the harness pins" rather than a copy per call site. See that type for the full
    /// rationale, including why the no-BOM form is load-bearing on
    /// <see cref="ProcessStartInfo.StandardInputEncoding"/>.
    /// </para>
    /// </summary>
    private static readonly Encoding Utf8NoBom = ChildProcessEncoding.Utf8NoBom;

    /// <summary>
    /// Run <paramref name="command"/> in <paramref name="workingDirectory"/> with the given
    /// environment overlay and per-process timeout.
    /// </summary>
    public Task<ProcessResult> RunAsync(
        ResolvedCommand command,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        RunAsync(command, workingDirectory, environment, timeout, standardInput: null, stdoutLineSink: null, cancellationToken);

    /// <summary>
    /// Run <paramref name="command"/> with optional STDIN text and an optional per-stdout-line
    /// sink (used by prompt runners to feed the prompt via stdin and tee the raw output stream
    /// to a log). Existing callers use the simpler overload — behaviour there is unchanged.
    /// </summary>
    /// <param name="command">The resolved executable + arguments to launch.</param>
    /// <param name="workingDirectory">Working directory for the child process.</param>
    /// <param name="environment">Environment variables applied to the child process.</param>
    /// <param name="timeout">Whole-process timeout; the process tree is killed when it elapses.</param>
    /// <param name="standardInput">Text written to the child's stdin (then closed); null = no stdin redirect.</param>
    /// <param name="stdoutLineSink">Invoked for each stdout line as it arrives (line excludes the newline); null = no tee.</param>
    /// <param name="cancellationToken">Cancels the run; the process tree is killed on cancellation.</param>
    public async Task<ProcessResult> RunAsync(
        ResolvedCommand command,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        string? standardInput,
        Action<string>? stdoutLineSink,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Decode the child's bytes as UTF-8 regardless of the host console code page (issue #55).
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom
        };

        // StandardInputEncoding may be set ONLY when stdin is redirected; assigning it otherwise
        // throws. Pin it too so a composed prompt with non-ASCII (em dashes, quotes) is sent UTF-8.
        if (standardInput is not null)
        {
            startInfo.StandardInputEncoding = Utf8NoBom;
        }

        foreach (string argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ApplyEnvironment(startInfo.Environment, environment);

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var stdoutDone = new SemaphoreSlim(0, 1);
        using var stderrDone = new SemaphoreSlim(0, 1);

        process.OutputDataReceived += (_, e) => Collect(e.Data, stdout, stdoutDone, stdoutLineSink);
        process.ErrorDataReceived += (_, e) => Collect(e.Data, stderr, stderrDone, lineSink: null);

        var stopwatch = Stopwatch.StartNew();
        await StartWithTextFileBusyRetryAsync(process, cancellationToken).ConfigureAwait(false);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (standardInput is not null)
        {
            await WriteStandardInputAsync(process, standardInput).ConfigureAwait(false);
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested;
            KillTree(process);
        }

        // Drain the async readers so captured output is complete before we return.
        await stdoutDone.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        await stderrDone.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        stopwatch.Stop();

        int exitCode = timedOut ? TimeoutExitCode : SafeExitCode(process);

        return new ProcessResult
        {
            ExitCode = exitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
            TimedOut = timedOut,
            Duration = stopwatch.Elapsed
        };
    }

    /// <summary>
    /// The env-var namespace the harness owns and fully specifies for a child (SSOT §5.1). Everything
    /// outside it — <c>PATH</c>, <c>HOME</c>, git and toolchain configuration — is inherited untouched,
    /// because a child action genuinely needs its ambient toolchain.
    /// </summary>
    internal const string HarnessEnvPrefix = "GUARDRAILS_";

    /// <summary>
    /// Environment-variable NAME semantics, matched to the platform's own: Windows env names are
    /// case-insensitive, POSIX names are case-sensitive. <see cref="ProcessStartInfo.Environment"/> is
    /// keyed the same way, so the hermetic sweep below has to agree with it — an ordinal-only match on
    /// Windows would walk straight past an inherited <c>Guardrails_State_Out</c> that the child would
    /// nonetheless read back as <c>GUARDRAILS_STATE_OUT</c>.
    /// </summary>
    private static readonly StringComparison EnvNameComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary><see cref="EnvNameComparison"/> as a comparer, for the declared-key set.</summary>
    private static readonly StringComparer EnvNameComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// How many times a launch is retried when Linux answers <c>ETXTBSY</c> ("Text file busy") — the
    /// kernel refusing to <c>exec</c> a file some process still holds open for WRITING (#650).
    /// </summary>
    /// <remarks>
    /// Ten, not five. The first shipped value gave a ~100 ms total budget, which is thin for a window whose
    /// length is "however long until some other process closes a descriptor" — and it duly ran out on CI
    /// while the descriptor was still open. A permanent ETXTBSY now costs ~500 ms before failing loudly,
    /// which is nothing against a task, and a transient one has room to clear.
    /// </remarks>
    private const int TextFileBusyAttempts = 10;

    /// <summary>The wait between those attempts. The window is another process closing a descriptor, not work.</summary>
    private static readonly TimeSpan TextFileBusyBackoff = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// The count of <c>ETXTBSY</c> retries this process has performed, exposed so a test can assert the
    /// DECISION rather than time a race it cannot reproduce on demand (#518).
    /// </summary>
    internal static int TextFileBusyRetries;

    /// <summary>
    /// Start the child, retrying ONLY on <c>ETXTBSY</c>.
    ///
    /// <para>
    /// The harness writes scripts and then executes them — the guardrail shim at startup, action and
    /// guardrail scripts inside a worktree — while other segments are spawning children in parallel. On
    /// Linux that combination can lose a race the writer never entered: <c>fork</c> copies the whole
    /// descriptor table, so a child spawned while ANOTHER thread still holds the script open for writing
    /// inherits that descriptor, and the kernel refuses to <c>exec</c> the file until it is closed.
    /// <c>O_CLOEXEC</c> closes it on the child's own exec, which does not help the exec that is failing.
    /// </para>
    ///
    /// <para>
    /// Measured in CI (ubuntu-latest, run 34053123998): a fixture wrote <c>fake-claude.sh</c>, set its exec
    /// bit, and the harness could not launch it — <i>"An error occurred trying to start process ...
    /// Text file busy"</i>. The same job was green on the previous run, which is the tell: the condition
    /// is transient and clears in milliseconds. Left alone it surfaces as a task that failed to launch for
    /// no reason a reader can act on, which is indistinguishable from a real launch failure.
    /// </para>
    ///
    /// <para>
    /// <b>Narrow on purpose.</b> Only <c>ETXTBSY</c> is retried. A missing interpreter, a bad path, a
    /// permission denial and every other launch failure still throws on the first attempt, loudly and
    /// immediately — a broad "retry any start failure" would turn a deterministic misconfiguration into a
    /// slow one, which is the more expensive bug.
    /// </para>
    /// </summary>
    private static async Task StartWithTextFileBusyRetryAsync(Process process, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                process.Start();
                return;
            }
            catch (Win32Exception ex) when (IsTextFileBusy(ex) && attempt < TextFileBusyAttempts)
            {
                Interlocked.Increment(ref TextFileBusyRetries);

                // AWAIT, not Thread.Sleep. Two reasons, and the second is the one that bit: a blocking
                // sleep here parks a thread-pool thread for up to the whole budget, and — because
                // everything before an async method's first await runs synchronously on the CALLER's
                // thread — it also meant `RunAsync` did not return a Task until the retries were
                // exhausted. Nothing concurrent could observe a retry in progress, which is precisely
                // what this loop's own test has to do to release the file it is holding open.
                await Task.Delay(TextFileBusyBackoff, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// <c>ETXTBSY</c> is errno 26 on Linux. The errno is checked FIRST because it is the fact; the message
    /// match is a fallback for a runtime that surfaces the text without the number, and is deliberately
    /// not the primary test — an error string is a localizable thing to key a retry on.
    /// </summary>
    private static bool IsTextFileBusy(Win32Exception ex) =>
        OperatingSystem.IsLinux()
        && (ex.NativeErrorCode == 26
            || ex.Message.Contains("Text file busy", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Merge <paramref name="overlay"/> into a child's environment block <b>hermetically</b> for the
    /// harness-owned <c>GUARDRAILS_*</c> namespace (SSOT §5.1, issue #442): after this call the child's
    /// view of that namespace is EXACTLY <paramref name="overlay"/> — no more, no less.
    /// <para>
    /// The bug this closes is a mismatch between how callers reason and how the OS behaves.
    /// <see cref="ProcessStartInfo.Environment"/> starts as a COPY of the harness's own environment and
    /// the overlay is merged ON TOP, so <i>absence from the overlay was never absence in the child</i>.
    /// A key a caller deliberately withheld —
    /// <c>TaskExecutor.BuildGuardrailEnvironment</c>'s <c>env.Remove("GUARDRAILS_STATE_OUT")</c>, or
    /// <c>NeedsHumanTriage</c>'s deliberately empty dictionary — still reached the child by inheritance
    /// whenever the harness process itself carried that variable (i.e. whenever the harness was launched
    /// from inside another run). That is not hypothetical: it is precisely how issue #253's triage child
    /// inherited the OUTER run's <c>GUARDRAILS_WORKSPACE</c>, wrote into a foreign <c>_integration</c>
    /// worktree, and got an innocent agent blamed for a write-scope violation.
    /// </para>
    /// <para>
    /// Fixed HERE, at the one seam where "a dictionary" becomes "a child environment", rather than at
    /// the two known call sites — so the guarantee holds for actions, script guardrails, prompt runners,
    /// triage, the overwatcher, and for every call site not written yet that reasonably assumes its
    /// dictionary is the whole story. Clearing (rather than blanking to <c>""</c>) is the operative
    /// detail: an empty value is still a SET variable to a shell, and <c>test -n</c>/<c>-z</c> style
    /// probes and role-detection branches read it as present.
    /// </para>
    /// </summary>
    /// <param name="childEnvironment">The child's environment block — inherited copy, mutated in place.</param>
    /// <param name="overlay">The harness's complete, authoritative <c>GUARDRAILS_*</c> declaration (plus any non-harness vars the caller wants set).</param>
    internal static void ApplyEnvironment(
        IDictionary<string, string?> childEnvironment,
        IReadOnlyDictionary<string, string> overlay)
    {
        var declared = new HashSet<string>(overlay.Keys, EnvNameComparer);

        // Materialize first: Keys is a live view over the dictionary being mutated.
        List<string> inheritedButUndeclared = childEnvironment.Keys
            .Where(name => name.StartsWith(HarnessEnvPrefix, EnvNameComparison) && !declared.Contains(name))
            .ToList();

        foreach (string name in inheritedButUndeclared)
        {
            childEnvironment.Remove(name);
        }

        foreach (KeyValuePair<string, string> variable in overlay)
        {
            childEnvironment[variable.Key] = variable.Value;
        }
    }

    private static void Collect(string? data, StringBuilder buffer, SemaphoreSlim done, Action<string>? lineSink)
    {
        if (data is null)
        {
            // Null line = stream closed; signal that this reader has drained.
            done.Release();
            return;
        }

        buffer.AppendLine(data);
        lineSink?.Invoke(data);
    }

    private static async Task WriteStandardInputAsync(Process process, string input)
    {
        try
        {
            await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The child may close stdin early (e.g. it has all it needs); that is not a failure.
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the HasExited check and Kill — nothing to do.
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return TimeoutExitCode;
        }
    }
}
