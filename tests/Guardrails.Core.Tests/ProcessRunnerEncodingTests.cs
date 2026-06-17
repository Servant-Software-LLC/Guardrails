using System.Text;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Regression for issue #55: ProcessRunner must decode a child's stdout/stderr as UTF-8
/// regardless of the host console code page. The child here writes RAW UTF-8 bytes for an em
/// dash (<c>E2 80 94</c>, U+2014) straight to its standard streams (bypassing the shell's own
/// $OutputEncoding), so the test isolates ProcessRunner's decode. Before the fix, Windows decoded
/// those bytes with the OEM code page (CP437/850) and produced the mojibake "ΓÇö".
/// </summary>
public sealed class ProcessRunnerEncodingTests
{
    // The em dash and the mojibake it becomes under a CP437/850 mis-decode.
    private const string EmDash = "—";        // —
    private const char MojibakeLead = 'Γ';    // Γ (first char of "ΓÇö")

    private static bool Windows => OperatingSystem.IsWindows();

    /// <summary>A command that writes raw bytes <paramref name="hex"/> (+ newline) to the given stream.</summary>
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
                Executable = "pwsh",
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

    private static Task<ProcessResult> RunAsync(ResolvedCommand command) =>
        new ProcessRunner().RunAsync(
            command,
            Path.GetTempPath(),
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Stdout_RawUtf8_IsDecodedAsUtf8_NotOemCodePage()
    {
        // bytes: em dash + LF
        ProcessResult result = await RunAsync(RawBytesCommand([0xE2, 0x80, 0x94, 0x0A], toStderr: false));

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
        ProcessResult result = await RunAsync(RawBytesCommand([0xE2, 0x80, 0x94, 0x0A], toStderr: true));

        Assert.Contains(EmDash, result.StandardError);
        Assert.DoesNotContain(MojibakeLead, result.StandardError);
    }
}

internal static class ByteSequenceAssertExtensions
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
