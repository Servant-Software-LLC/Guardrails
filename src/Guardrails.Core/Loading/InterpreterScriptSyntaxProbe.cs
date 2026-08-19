using System.Diagnostics;
using System.Text;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Loading;

/// <summary>
/// The real <see cref="IScriptSyntaxProbe"/>: asks the language's own interpreter whether each script
/// PARSES, never whether it runs (issue #473).
///
/// <para><b>One invocation per language, not per script.</b> <c>validate</c> is run constantly and in
/// CI, and a plan can carry hundreds of guardrails — this repo has ~500. Spawning an interpreter per
/// file would add tens of seconds and the check would be turned off, which is the same as not having
/// it. The whole batch goes through a single process, so the cost is one startup.</para>
///
/// <para><b>A missing interpreter is silence, not a diagnostic.</b> If <c>pwsh</c> is absent the probe
/// reports nothing rather than flagging every script: a machine that cannot parse a <c>.ps1</c> also
/// cannot run one, so the plan's real problem will surface elsewhere, and failing validation for an
/// absent tool would punish the operator for something the plan author cannot control. The same is
/// true of every other failure mode here — a timeout, a crashed interpreter, an unreadable temp dir.
/// This probe's contract is "reports what it can prove invalid", and it holds that line.</para>
/// </summary>
public sealed class InterpreterScriptSyntaxProbe : IScriptSyntaxProbe
{
    /// <summary>How long the whole batch gets before the probe gives up and reports nothing.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private readonly IExecutableProbe _probe;

    /// <summary>Probe using the real PATH lookup for interpreter availability.</summary>
    public InterpreterScriptSyntaxProbe() : this(new PathExecutableProbe()) { }

    /// <summary>Probe with an injected PATH lookup, so availability is testable without a real interpreter.</summary>
    public InterpreterScriptSyntaxProbe(IExecutableProbe probe) => _probe = probe;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> FindSyntaxErrors(IReadOnlyList<string> scriptPaths)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        List<string> powershell = [.. scriptPaths.Where(p => p.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))];
        List<string> shell = [.. scriptPaths.Where(p => p.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))];

        // .cmd/.bat have no parse-only mode and .py is not emitted by the breakdown skill today; both
        // are simply not checked, per the "absence never means valid" contract on the interface.
        CollectPowerShell(powershell, errors);
        CollectShell(shell, errors);

        return errors;
    }

    private void CollectPowerShell(List<string> scripts, Dictionary<string, string> errors)
    {
        if (scripts.Count == 0 || !_probe.Exists("pwsh"))
        {
            return;
        }

        // The parser is the SAME one that would run the script, so agreement is exact — no
        // hand-written grammar to drift. Errors[0] is enough: the first parse error is the actionable
        // one and the rest are usually its cascade.
        const string Parse = """
            param([string]$ListFile)
            foreach ($p in (Get-Content -LiteralPath $ListFile)) {
                if (-not (Test-Path -LiteralPath $p)) { continue }
                $errs = $null
                [void][System.Management.Automation.Language.Parser]::ParseFile($p, [ref]$null, [ref]$errs)
                if ($errs -and $errs.Count -gt 0) {
                    Write-Output ($p + "`t" + $errs[0].Message)
                }
            }
            """;

        RunBatch("pwsh", Parse, scripts, static (script, list) =>
            ["-NoProfile", "-NonInteractive", "-File", script, "-ListFile", list], errors);
    }

    private void CollectShell(List<string> scripts, Dictionary<string, string> errors)
    {
        if (scripts.Count == 0 || !_probe.Exists("bash"))
        {
            return;
        }

        // `bash -n` reads and parses without executing a single command — the shell's own -n flag,
        // not an approximation.
        const string Parse = """
            while IFS= read -r p; do
              [ -f "$p" ] || continue
              msg=$(bash -n "$p" 2>&1) || printf '%s\t%s\n' "$p" "$(printf '%s' "$msg" | head -1)"
            done < "$1"
            """;

        RunBatch("bash", Parse, scripts, static (script, list) => [script, list], errors);
    }

    /// <summary>
    /// Write the script + the path list to temp files, run the interpreter once, and parse
    /// <c>path TAB message</c> lines off stdout. Any failure at all leaves <paramref name="errors"/>
    /// untouched.
    /// </summary>
    private static void RunBatch(
        string interpreter,
        string parseScript,
        List<string> scripts,
        Func<string, string, string[]> buildArgs,
        Dictionary<string, string> errors)
    {
        string dir = Path.Combine(Path.GetTempPath(), "gr-syntax-" + Guid.NewGuid().ToString("n")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            string scriptPath = Path.Combine(dir, interpreter == "bash" ? "parse.sh" : "parse.ps1");
            string listPath = Path.Combine(dir, "list.txt");
            File.WriteAllText(scriptPath, parseScript);
            File.WriteAllLines(listPath, scripts);

            var psi = new ProcessStartInfo(interpreter)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            foreach (string arg in buildArgs(scriptPath, listPath))
            {
                psi.ArgumentList.Add(arg);
            }

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return;
            }

            string stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit((int)Budget.TotalMilliseconds))
            {
                TryKill(process);
                return;
            }

            foreach (string line in stdout.Split('\n'))
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0)
                {
                    continue;
                }

                string path = line[..tab].Trim();
                string message = line[(tab + 1)..].Trim();
                if (path.Length > 0 && message.Length > 0)
                {
                    errors[path] = message;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // An interpreter that will not start, or a temp dir we cannot write, is not a plan defect.
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* best-effort temp cleanup */ }
            catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
        }
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already gone */ }
        catch (System.ComponentModel.Win32Exception) { /* cannot signal it; nothing more to do */ }
    }
}
