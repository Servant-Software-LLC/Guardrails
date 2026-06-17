using System.Text;
using Guardrails.Core.Execution;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Regression for issue #55: <see cref="ProcessRunner"/> must decode a child's stdout/stderr as
/// UTF-8 — and encode stdin as UTF-8 — regardless of the host console code page. The children here
/// write/echo RAW UTF-8 bytes for an em dash (<c>E2 80 94</c>, U+2014) straight to their standard
/// streams (bypassing the shell's own <c>$OutputEncoding</c>), so the tests isolate ProcessRunner's
/// own encode/decode. Before the fix, Windows decoded those bytes with the OEM code page
/// (CP437/850) and produced the mojibake "ΓÇö".
/// <para>
/// These spawn REAL child processes (pwsh/bash), hence Integration.Tests — Core.Tests is the
/// pure-CPU, fake-probe unit gate. NOTE: the regression only genuinely FAILS pre-fix on Windows,
/// where the OEM code page mis-decodes; on Linux/macOS the platform default is already UTF-8, so
/// there these assertions pass with or without the fix (a smoke test that capture isn't corrupted).
/// The 3-OS CI matrix — windows-latest included — is what makes this a real guard for #55.
/// </para>
/// </summary>
public sealed class ProcessRunnerEncodingTests
{
    // The em dash and the mojibake it becomes under a CP437/850 mis-decode.
    private const string EmDash = "—";        // —
    private const char MojibakeLead = 'Γ';    // Γ (first char of "ΓÇö")

    private static bool Windows => OperatingSystem.IsWindows();

    /// <summary>
    /// The Windows shell to launch: PowerShell 7 (<c>pwsh</c>) when present, else Windows
    /// PowerShell 5.1 (<c>powershell</c>) — mirrors <see cref="InterpreterMap"/>'s fallback so a
    /// box without pwsh still runs the test. Both expose <c>[Console]::OpenStandard*()</c> identically.
    /// </summary>
    private static readonly string WindowsShell = ResolveWindowsShell();

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

    /// <summary>A command that writes raw bytes <paramref name="bytes"/> to stdout or stderr.</summary>
    private static ResolvedCommand RawBytesCommand(byte[] bytes, bool toStderr)
    {
        if (Windows)
        {
            string list = string.Join(",", bytes.Select(b => "0x" + b.ToString("X2")));
            // OpenStandardOutput/Error write raw bytes, unaffected by the console code page.
            string stream = toStderr ? "OpenStandardError" : "OpenStandardOutput";
            string script =
                $"$o=[Console]::{stream}(); $b=[byte[]]({list}); $o.Write($b,0,$b.Length); $o.Flush()";
            return new ResolvedCommand
            {
                Executable = WindowsShell,
                Arguments = ["-NoProfile", "-NonInteractive", "-Command", script]
            };
        }

        string octal = string.Concat(bytes.Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0')));
        string redirect = toStderr ? " 1>&2" : string.Empty;
        return new ResolvedCommand
        {
            Executable = "bash",
            Arguments = ["-c", $"printf '{octal}'{redirect}"]
        };
    }

    /// <summary>
    /// A command that writes a literal ASCII <c>STDIN:</c> marker, then copies its raw stdin bytes
    /// to stdout unchanged. The marker keeps any (wrongly) injected leading BOM mid-stream, where a
    /// decoder won't silently strip it — so the stdin test can catch a BOM-emitting encoding.
    /// </summary>
    private static ResolvedCommand EchoStdinCommand()
    {
        if (Windows)
        {
            const string script =
                "$o=[Console]::OpenStandardOutput(); " +
                "$m=[Text.Encoding]::ASCII.GetBytes('STDIN:'); $o.Write($m,0,$m.Length); " +
                "[Console]::OpenStandardInput().CopyTo($o); $o.Flush()";
            return new ResolvedCommand
            {
                Executable = WindowsShell,
                Arguments = ["-NoProfile", "-NonInteractive", "-Command", script]
            };
        }

        return new ResolvedCommand
        {
            Executable = "bash",
            Arguments = ["-c", "printf 'STDIN:'; cat"]
        };
    }

    private static Task<ProcessResult> RunAsync(
        ResolvedCommand command, CancellationToken cancellationToken, string? standardInput = null) =>
        new ProcessRunner().RunAsync(
            command,
            Path.GetTempPath(),
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(30),
            standardInput,
            stdoutLineSink: null,
            cancellationToken);

    [Fact]
    public async Task Stdout_RawUtf8_IsDecodedAsUtf8_NotOemCodePage()
    {
        // bytes: em dash + LF
        ProcessResult result = await RunAsync(
            RawBytesCommand([0xE2, 0x80, 0x94, 0x0A], toStderr: false),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(EmDash, result.StandardOutput);
        Assert.DoesNotContain(MojibakeLead, result.StandardOutput);
        // Re-encoding the captured text must yield the original UTF-8 bytes, not CP437's CE 93 ….
        Assert.True(
            Encoding.UTF8.GetBytes(result.StandardOutput).ContainsSequence([0xE2, 0x80, 0x94]),
            "captured stdout should re-encode to the original UTF-8 em-dash bytes (E2 80 94)");
    }

    [Fact]
    public async Task Stderr_RawUtf8_IsDecodedAsUtf8_NotOemCodePage()
    {
        ProcessResult result = await RunAsync(
            RawBytesCommand([0xE2, 0x80, 0x94, 0x0A], toStderr: true),
            TestContext.Current.CancellationToken);

        Assert.Contains(EmDash, result.StandardError);
        Assert.DoesNotContain(MojibakeLead, result.StandardError);
        // Byte-level proof, symmetric with the stdout test — a wrong decode can't satisfy this.
        Assert.True(
            Encoding.UTF8.GetBytes(result.StandardError).ContainsSequence([0xE2, 0x80, 0x94]),
            "captured stderr should re-encode to the original UTF-8 em-dash bytes (E2 80 94)");
    }

    [Fact]
    public async Task StandardInput_NonAscii_IsEncodedAsUtf8_NotOemCodePage()
    {
        // Feed an em dash to the child over stdin; it prefixes "STDIN:" then echoes stdin bytes raw
        // back to stdout. ProcessRunner ENCODES stdin (the path under test) and DECODES stdout, so a
        // faithful round-trip proves StandardInputEncoding is UTF-8: under the OEM code page the em
        // dash (not representable in CP437/850) would have reached the child as '?'.
        ProcessResult result = await RunAsync(
            EchoStdinCommand(), TestContext.Current.CancellationToken, standardInput: EmDash);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("STDIN:" + EmDash, result.StandardOutput);
        Assert.DoesNotContain("?", result.StandardOutput); // CP437/850 lossy-substitution canary

        byte[] outBytes = Encoding.UTF8.GetBytes(result.StandardOutput);
        Assert.True(
            outBytes.ContainsSequence([0xE2, 0x80, 0x94]),
            "the stdin em dash should reach the child as UTF-8 (E2 80 94) and round-trip back");
        // A BOM-emitting stdin encoding would prepend EF BB BF to the composed prompt; the ASCII
        // "STDIN:" prefix keeps any such BOM mid-stream so it can't be stripped as a leading BOM.
        Assert.False(
            outBytes.ContainsSequence([0xEF, 0xBB, 0xBF]),
            "no UTF-8 BOM should be injected into the child's stdin");
    }
}

internal static class ByteSequenceExtensions
{
    /// <summary>True when <paramref name="haystack"/> contains <paramref name="needle"/> as a contiguous run.</summary>
    public static bool ContainsSequence(this byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
