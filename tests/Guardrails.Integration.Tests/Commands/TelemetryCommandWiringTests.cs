using Guardrails.Cli;
using Guardrails.Core.Telemetry;

namespace Guardrails.Integration.Tests.Commands;

/// <summary>
/// The AGREEMENT test for <c>guardrails telemetry</c>'s composition-root wiring (plan of record
/// <c>model-evidence-and-graduation</c>, #535, task 09/11): a source grep proving
/// <c>CommandFactory.BuildRootCommand</c> calls <c>TelemetryCommand.Create</c> is defeatable by a dead
/// call or a registration that never actually reaches the real root at runtime — either lets the shipped
/// binary lack the verb while a grep still passes. So this file proves an OBSERVABLE, not a spelling:
/// invoking <c>telemetry purge</c> through <see cref="CommandFactory.BuildRootCommand"/> — the REAL root
/// <c>Program.cs</c> builds, not a hand-built one — must actually empty a real corpus directory. An
/// unregistered verb makes that observable false, not merely a parse warning.
///
/// <para><b>Split from <see cref="TelemetryCommandTests"/> on purpose.</b> That class drives
/// <c>TelemetryCommand.Create</c> attached to a throwaway <c>RootCommand</c> and goes green in task 10,
/// BEFORE registration exists. This class can only go green in task 11, once
/// <c>rootCommand.Add(TelemetryCommand.Create(io))</c> actually lands in
/// <c>CommandFactory.BuildRootCommand</c>. Right now (task 09) — and still after task 10 — the
/// <c>telemetry</c> token is not a command <see cref="CommandFactory.BuildRootCommand"/> recognizes at
/// all, so parsing it fails before any action ever runs, and the observable below is provably false.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class TelemetryCommandWiringTests
{
    private static async Task<int> InvokeThroughRealRootAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        return await root.Parse(args).InvokeAsync();
    }

    [Fact]
    [Trait("Category", "ModelEvidence")]
    public async Task Telemetry_IsReachableFrom_CommandFactoryBuildRootCommand()
    {
        using var corpus = new TempDir();

        // Pre-populate the corpus DIRECTLY — never through the verb under test — so the only way the
        // file can be gone afterwards is that `telemetry purge`, reached through the REAL root, actually
        // ran and did its work.
        new TelemetryCorpusStore(corpus.Path).Append(SampleRow());
        Assert.NotEmpty(Directory.GetFiles(corpus.Path, "*.jsonl", SearchOption.AllDirectories));

        int exit = await InvokeThroughRealRootAsync("telemetry", "purge", "--corpus-root", corpus.Path);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.True(
            !Directory.Exists(corpus.Path)
                || !Directory.EnumerateFileSystemEntries(corpus.Path, "*", SearchOption.AllDirectories).Any(),
            $"expected the real root's 'telemetry purge' to have emptied '{corpus.Path}'");
    }

    private static TelemetryRow SampleRow() => new()
    {
        SchemaVersion = TelemetryRow.CurrentSchemaVersion,
        RunId = "run-wiring-fixture",
        TaskId = "01-wiring-fixture",
        Attempt = 1,
        StartedAt = DateTimeOffset.UtcNow,
        EndedAt = DateTimeOffset.UtcNow.AddMinutes(1),
        Outcome = "succeeded",
        Repo = "guardrails"
    };

    /// <summary>A fresh temp directory, deleted on <see cref="Dispose"/>. Never <c>~/.guardrails/telemetry/</c>.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gr-telemetrywiring-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                try { Directory.Delete(Path, recursive: true); }
                catch (IOException) { }
            }
        }
    }
}
