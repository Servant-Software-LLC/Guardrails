namespace Guardrails.Core.Tests;

/// <summary>
/// A fake <c>claude</c> for the #782 gateway suites, spawned through the REAL <c>ProcessRunner</c>. Every
/// invocation records, in its own numbered folder under <see cref="CallsDirectory"/>, the argv it received (one
/// per line) and its WHOLE environment (<c>NAME=value</c> per line), writes a passing verdict when
/// <c>GUARDRAILS_VERDICT_OUT</c> is set (so a judge dispatched through it reaches a verdict), drains stdin, and
/// replays a canned stream whose result reports <c>total_cost_usd: 1.23</c> and 1000 input / 200 output tokens —
/// a cost the gateway path must null and tokens it must keep. OS-picked: a <c>.cmd</c> forwarding to PowerShell
/// on Windows, an executable bash script elsewhere.
/// <para>Calls are numbered by counting the folders already present, so the recording is ordered only for the
/// SERIAL dispatches these suites make.</para>
/// </summary>
internal sealed class GatewayFakeClaude
{
    /// <summary>The cost the fake's result line reports — fiction on a gateway dispatch.</summary>
    public const decimal ReportedCost = 1.23m;

    /// <summary>The input tokens the fake's result line reports.</summary>
    public const int InputTokens = 1_000;

    /// <summary>The output tokens the fake's result line reports.</summary>
    public const int OutputTokens = 200;

    private static readonly string Stream = string.Join("\n",
        """{"type":"system","subtype":"init","model":"Qwen"}""",
        """{"type":"result","subtype":"success","is_error":false,"result":"done","total_cost_usd":1.23,"num_turns":2,"usage":{"input_tokens":1000,"output_tokens":200}}""");

    public GatewayFakeClaude(string root)
    {
        Directory.CreateDirectory(root);
        CallsDirectory = Path.Combine(root, "calls");
        Directory.CreateDirectory(CallsDirectory);
        string streamPath = Path.Combine(root, "fake-stream.jsonl");
        File.WriteAllText(streamPath, Stream + "\n");
        Command = Write(Path.Combine(root, "fake-bin"), CallsDirectory, streamPath);
    }

    /// <summary>The executable to hand a runner as its <c>command</c>.</summary>
    public string Command { get; }

    /// <summary>Where each invocation's recording folder lands.</summary>
    public string CallsDirectory { get; }

    /// <summary>Every recorded invocation, in call order.</summary>
    public IReadOnlyList<FakeClaudeCall> Calls() =>
    [
        .. Directory.GetDirectories(CallsDirectory)
            .Order(StringComparer.Ordinal)
            .Select(FakeClaudeCall.Load)
    ];

    private static string Write(string dir, string calls, string streamPath)
    {
        Directory.CreateDirectory(dir);
        if (OperatingSystem.IsWindows())
        {
            string ps1 = Path.Combine(dir, "claude.ps1");
            string cmd = Path.Combine(dir, "claude.cmd");
            File.WriteAllText(ps1,
                $"$calls = '{calls}'\r\n" +
                "$n = (Get-ChildItem -LiteralPath $calls -Directory).Count\r\n" +
                "$d = Join-Path $calls ('{0:D4}' -f $n)\r\n" +
                "$null = New-Item -ItemType Directory -Path $d\r\n" +
                "[IO.File]::WriteAllLines((Join-Path $d 'args.txt'), [string[]]$args)\r\n" +
                "$e = Get-ChildItem env: | ForEach-Object { \"$($_.Name)=$($_.Value)\" }\r\n" +
                "[IO.File]::WriteAllLines((Join-Path $d 'env.txt'), [string[]]$e)\r\n" +
                "if ($env:GUARDRAILS_VERDICT_OUT) { [IO.File]::WriteAllText($env:GUARDRAILS_VERDICT_OUT, '{\"pass\": true, \"reason\": \"ok\"}') }\r\n" +
                "$null = [Console]::In.ReadToEnd()\r\n" +
                $"foreach ($l in [IO.File]::ReadAllLines('{streamPath}')) {{ [Console]::Out.WriteLine($l) }}\r\n" +
                "[Console]::Out.Flush()\r\n" +
                "exit 0\r\n");
            File.WriteAllText(cmd, $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" %*\r\nexit /b %ERRORLEVEL%\r\n");
            return cmd;
        }

        string sh = Path.Combine(dir, "claude");
        File.WriteAllText(sh,
            "#!/usr/bin/env bash\n" +
            $"calls='{calls}'\n" +
            "n=$(find \"$calls\" -mindepth 1 -maxdepth 1 -type d | wc -l | tr -d ' ')\n" +
            "d=\"$calls/$(printf '%04d' \"$n\")\"\n" +
            "mkdir -p \"$d\"\n" +
            "printf '%s\\n' \"$@\" > \"$d/args.txt\"\n" +
            "env > \"$d/env.txt\"\n" +
            "if [ -n \"$GUARDRAILS_VERDICT_OUT\" ]; then printf '{\"pass\": true, \"reason\": \"ok\"}' > \"$GUARDRAILS_VERDICT_OUT\"; fi\n" +
            "cat > /dev/null\n" +
            $"cat '{streamPath}'\n" +
            "exit 0\n");
        File.SetUnixFileMode(sh,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        return sh;
    }
}

/// <summary>One recorded invocation of <see cref="GatewayFakeClaude"/>.</summary>
internal sealed record FakeClaudeCall(string Directory, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env)
{
    public static FakeClaudeCall Load(string directory)
    {
        string[] args = File.Exists(Path.Combine(directory, "args.txt"))
            ? File.ReadAllLines(Path.Combine(directory, "args.txt"))
            : [];

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(Path.Combine(directory, "env.txt")))
        {
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                env[line[..eq]] = line[(eq + 1)..];
            }
        }

        return new FakeClaudeCall(directory, args, env);
    }

    /// <summary>
    /// The value of <paramref name="name"/> as the CHILD would read it: case-insensitively on Windows (where the
    /// OS folds environment names, so Claude Code would read <c>anthropic_api_key</c> as <c>ANTHROPIC_API_KEY</c>),
    /// exactly elsewhere. Null when absent.
    /// </summary>
    public string? Read(string name)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (KeyValuePair<string, string> entry in Env)
        {
            if (string.Equals(entry.Key, name, comparison))
            {
                return entry.Value;
            }
        }

        return null;
    }
}
