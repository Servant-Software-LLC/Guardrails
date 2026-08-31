using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Guardrails.Cli;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #552 — <b>what starts the live log server, and what the run says when nothing did.</b>
/// <para>
/// The server used to be gated on <c>live</c> (<c>!--no-ui</c> AND an ANSI-interactive console AND
/// stdout not redirected) — the Spectre progress table's gate. An HTTP listener on loopback needs
/// none of those, and the coupling inverted the feature with respect to need: a headless,
/// backgrounded or CI run has no console to watch, so it is exactly the run that most needs a browser
/// page, and it was the only one that could never have one. Worse, the failure was silent — an
/// operator who launched <c>guardrails run … &gt; run.log 2&gt;&amp;1</c> and passed no UI flag at all
/// still lost the server, and nothing said so or named a remedy.
/// </para>
/// <para>
/// These tests pin all three halves of the fix: the server starts on the headless path and writes its
/// URL to the stream a redirect captures; <c>--no-log-server</c> still suppresses it; and whenever
/// there is no server — opted out, or a start that failed — the run names <c>guardrails logs</c>,
/// the shipped verb that serves the same live view against a run already in flight.
/// </para>
/// <para>
/// Every test here runs through the real composition root with an <see cref="StringConsoleIo"/>,
/// which is precisely the headless case: <c>Console.IsOutputRedirected</c> is true under
/// <c>dotnet test</c> and <c>--no-ui</c> forces <c>live</c> false regardless, so the old gate could
/// not have started a server for any of them.
/// </para>
/// </summary>
public sealed class LogServerRunGateTests
{
    /// <summary>Matches the loopback base URL the run advertises (the port is chosen at bind time).</summary>
    private static readonly Regex LoopbackUrl = new(@"http://127\.0\.0\.1:\d+/", RegexOptions.None, TimeSpan.FromSeconds(5));

    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync(configuration: null, TestContext.Current.CancellationToken);
        return (exit, io.OutText);
    }

    [Fact]
    public async Task Run_NoUi_WithRedirectedOutput_StartsTheLogServer_AndPrintsItsUrl()
    {
        // THE regression pin for #552. --no-ui plus redirected output is the shape of every
        // unattended launch, and it is the shape that used to be guaranteed serverless.
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, string output) = await InvokeAsync("run", plan.PlanDir, "--no-ui");

        Assert.Equal(ExitCodes.Success, exit);

        // The server started, and said so.
        Assert.Contains("Live tailing server (active tasks):", output);

        // It advertised a real loopback URL — and it went to io.Out, which in production is
        // Console.Out. That is the whole point of printing it there: under `> run.log 2>&1` the URL
        // lands in the log file, which is the only place an unattended operator will look for it.
        Match url = LoopbackUrl.Match(output);
        Assert.True(url.Success, $"expected an http://127.0.0.1:<port>/ URL in the run output; got:\n{output}");

        // The headless path is genuinely in play — the plain static "all tasks" link is printed, so
        // this is the no-live-table branch and not an accidentally-interactive run.
        Assert.Contains("All tasks (static log site):", output);

        // And with a server running, the run must NOT also be telling the operator to go start one.
        Assert.DoesNotContain("Live log viewer not started", output);
    }

    [Fact]
    public async Task Run_NoLogServer_SuppressesTheServer_AndNamesGuardrailsLogsAsTheRemedy()
    {
        // The opt-out still opts out — widening the gate must not take the flag's meaning with it.
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, string output) = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.Success, exit);

        // Nothing bound, nothing advertised.
        Assert.DoesNotContain("Live tailing server", output);
        Assert.False(
            LoopbackUrl.IsMatch(output),
            $"--no-log-server must not advertise a server URL; got:\n{output}");

        // But the operator is not left guessing: the message states the reason and names the verb.
        Assert.Contains("Live log viewer not started (--no-log-server)", output);

        // The command must name THIS plan folder. Asserting the verb and the path separately would
        // pass on a remedy that named neither together — the run already echoes the folder when it
        // resolves the argument, so a bare path assertion proves nothing about the remedy line.
        // Accept either spelling: the folder is quoted only when it contains a space, and whether a
        // temp path does is a property of the machine (see the spaced-folder test, which pins the
        // quoted form against a folder guaranteed to have one).
        bool namesThisFolder =
            output.Contains($"guardrails logs {plan.PlanDir}", StringComparison.Ordinal)
            || output.Contains($"guardrails logs \"{plan.PlanDir}\"", StringComparison.Ordinal);
        Assert.True(namesThisFolder, $"expected the remedy to name '{plan.PlanDir}'; got:\n{output}");
    }

    [Fact]
    public async Task Run_SuppressionMessage_QuotesAPlanFolderContainingASpace_SoItCanBePasted()
    {
        // The remedy line's whole job is to be copied into another terminal. An unquoted path with a
        // space would be split by the shell, so the message we print to fix a broken experience would
        // itself be a broken command — and the operator would conclude the verb does not work.
        string parent = Path.Combine(Path.GetTempPath(), "gr 552 spaced " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(parent);
        try
        {
            using var plan = new ScriptPlanBuilder(parent).AddTask("01-first");
            Assert.Contains(' ', plan.PlanDir);

            (int exit, string output) = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Contains($"guardrails logs \"{plan.PlanDir}\"", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task Run_WhenTheLogServerCannotBind_StillSucceeds_AndNamesGuardrailsLogs()
    {
        // Now that a listener is attempted for EVERY run that did not opt out, a bind that fails must
        // stay a lost convenience rather than a lost run — in CI, in a service, in a sandbox with no
        // socket permission. Force the failure deterministically by holding the port ourselves and
        // pinning the run to it with --log-port (a caller-chosen port gets a single bind attempt, so
        // there is no ephemeral-port retry to route around the conflict).
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        var occupier = new TcpListener(IPAddress.Loopback, 0);
        occupier.Start();
        int busyPort = ((IPEndPoint)occupier.LocalEndpoint).Port;
        try
        {
            (int exit, string output) = await InvokeAsync(
                "run", plan.PlanDir, "--no-ui", "--log-port", busyPort.ToString());

            // The run itself is untouched by the viewer's failure.
            Assert.Equal(ExitCodes.Success, exit);

            // One warning explaining what happened...
            Assert.Contains("Log server not started", output);
            Assert.DoesNotContain("Live tailing server", output);

            // ...and the same remedy, because "no server" is one operator problem however it arose.
            Assert.Contains("Live log viewer not started", output);
            Assert.Contains("guardrails logs", output);
        }
        finally
        {
            occupier.Stop();
            occupier.Dispose();
        }
    }
}
