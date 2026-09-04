using Guardrails.Core.Telemetry;

namespace Guardrails.Core.Tests.Telemetry;

/// <summary>
/// The opt-out switch (SSOT §15.6) and — the reason this file exists — the proof that
/// <see cref="TelemetryCorpusStore.Append"/> no longer consults PROCESS-GLOBAL state at write time.
///
/// <para><b>The measured failure this pins.</b> On the concurrent whole-solution profile (#566),
/// <c>solution-wide-test (windows-latest)</c> failed six telemetry tests at once — five in
/// <c>TelemetryReportPhase1Tests</c> and <c>TelemetryCommandWiringTests</c> — each reporting that the
/// corpus it had just written to was empty:
/// <c>"The corpus holds no attempt yet, so there is nothing to report."</c> Every one of those tests had
/// PERFECT isolation: its own GUID-named temp corpus root, passed explicitly. They failed anyway, because
/// <c>Append</c> read <c>GUARDRAILS_TELEMETRY</c> from the environment on every write, and a concurrently
/// running test — <c>TelemetryCommandTests.Ingest_WhenOptedOut_WritesNothing</c> — set that variable to
/// <c>off</c> process-wide around its own invocation. Inside that window every write anywhere in the
/// process silently became a no-op.</para>
///
/// <para><b>Why it was invisible until #566.</b> Sequential per-project runs never overlap the window, so
/// the defect needed the concurrent profile to surface — and it surfaced on Windows first only because of
/// scheduling. Serializing the tests would have hidden exactly the class of defect that profile was added
/// to expose, and would have left the production hazard in place: two <c>guardrails run</c> processes are
/// not affected (separate environments), but any in-process host embedding the harness would inherit the
/// same coupling.</para>
///
/// <para><b>The repair is the seam, not the schedule.</b> The decision is resolved once at a composition
/// root (<see cref="TelemetryCollectionSwitch.IsEnabledFromEnvironment"/>) and handed to the store as
/// constructor state, so a store's behavior is a function of how it was BUILT. This is the same repair
/// <c>GitEnvironmentCollection</c>'s doc names as the real fix for the analogous <c>GIT_DIR</c> defect:
/// stop letting the ambient environment be the anchor.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class TelemetryCollectionSwitchTests : IDisposable
{
    private readonly string corpusRoot =
        Path.Combine(Path.GetTempPath(), "gr-telemetry-switch", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(corpusRoot))
        {
            try { Directory.Delete(corpusRoot, recursive: true); }
            catch (IOException) { }
        }
    }

    // --- the pin: a write is independent of ambient process state ------------------------------------

    /// <summary>
    /// <b>THE REGRESSION PIN.</b> With <c>GUARDRAILS_TELEMETRY=off</c> set process-wide — exactly the state
    /// a concurrent opt-out test used to create — a store constructed to collect STILL WRITES. The store's
    /// behavior follows its constructor, not the environment.
    ///
    /// <para>This test is the only place left that mutates the variable, and it restores it in a
    /// <c>finally</c>. It is now harmless to run concurrently precisely BECAUSE of what it asserts: after
    /// the repair, no write path reads the variable, so no other test can be poisoned by this window. Before
    /// the repair it fails here — which is the two-sided evidence.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void AStoreConstructedToCollect_WritesEvenWhileTheProcessWideOptOutIsSet()
    {
        string? original = Environment.GetEnvironmentVariable(TelemetryCollectionSwitch.OptOutEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(TelemetryCollectionSwitch.OptOutEnvVar, "off");

            var store = new TelemetryCorpusStore(corpusRoot);
            Assert.True(store.CollectionEnabled);
            store.Append(Row());

            Assert.NotEmpty(Directory.GetFiles(corpusRoot, "*.jsonl", SearchOption.AllDirectories));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TelemetryCollectionSwitch.OptOutEnvVar, original);
        }
    }

    /// <summary>
    /// The other direction, and with no environment mutation at all: a store constructed NOT to collect
    /// writes nothing — no row, and no corpus root brought into being either.
    /// </summary>
    [Fact]
    [Trait("Category", "ModelEvidence")]
    public void AStoreConstructedNotToCollect_WritesNothing()
    {
        var store = new TelemetryCorpusStore(corpusRoot, collectionEnabled: false);
        Assert.False(store.CollectionEnabled);

        store.Append(Row());

        Assert.False(
            Directory.Exists(corpusRoot) && Directory.EnumerateFileSystemEntries(corpusRoot).Any(),
            $"expected an opted-out store to have written nothing under '{corpusRoot}'");
    }

    // --- the token semantics, as a pure function ------------------------------------------------------

    /// <summary>
    /// Only the exact token <c>off</c> disables collection, case-insensitively. Everything else — including
    /// the values an operator might REASONABLY expect to work — leaves collection on. Pinned as a pure
    /// function so the vocabulary is proven exhaustively without touching the process at all.
    ///
    /// <para>The generous readings are deliberately absent: an operator who typed <c>false</c> and believed
    /// collection was off for months is a worse outcome than one whose typo did nothing, because the first
    /// silently invalidates whatever the corpus is later used to decide.</para>
    /// </summary>
    [Theory]
    [Trait("Category", "ModelEvidence")]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("off", false)]
    [InlineData("OFF", false)]
    [InlineData("Off", false)]
    [InlineData("on", true)]
    [InlineData("0", true)]
    [InlineData("false", true)]
    [InlineData("no", true)]
    [InlineData(" off ", true)]
    public void IsEnabled_TreatsOnlyTheExactOffTokenAsDisabled(string? rawValue, bool expected) =>
        Assert.Equal(expected, TelemetryCollectionSwitch.IsEnabled(rawValue));

    private static TelemetryRow Row() => new()
    {
        SchemaVersion = TelemetryRow.CurrentSchemaVersion,
        RunId = "gr-switch-run",
        TaskId = "01-switch",
        Attempt = 1,
        StartedAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
        EndedAt = new DateTimeOffset(2026, 9, 1, 12, 1, 0, TimeSpan.Zero),
        Outcome = "succeeded",
        Repo = "gr-switch-repo"
    };
}
