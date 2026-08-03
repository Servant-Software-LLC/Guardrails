using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #419 — the PROCESS lifetime of the Windows short-junction LINK
/// (<see cref="WorktreeJunctionLifetime"/>). These pin the release contract that makes the junction
/// process-scoped: it RELEASES the link on exit (link-only, target intact), is AT-MOST-ONCE (the Ctrl-C
/// double-fire safety), and REFUSES a link a successor run has re-pointed to a different target. All against
/// UNIQUE TEMP link targets (never the real <c>C:\</c> root); the Windows-only junction assertions are gated
/// on <see cref="OperatingSystem.IsWindows"/>, with a cross-OS non-reparse-point guard test.
/// </summary>
public sealed class WorktreeJunctionLifetimeTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _links = [];

    public WorktreeJunctionLifetimeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-junclife-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Dispose_ReleasesLink_LinkOnly_TargetIntact()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string realRoot = Path.Combine(_root, "realroot");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(realRoot).FullName, "keep.txt"), "KEEP");
        string link = Track(Path.Combine(_root, "base", ".a"));
        Assert.True(WorktreeJunction.TryCreateJunction(link, realRoot));

        using (var lifetime = new WorktreeJunctionLifetime(link, realRoot, TextWriter.Null))
        {
            lifetime.ReleaseOnce();
            Assert.False(Directory.Exists(link));               // link released
        }

        Assert.True(Directory.Exists(realRoot));                // target survives (link-only)
        Assert.Equal("KEEP", File.ReadAllText(Path.Combine(realRoot, "keep.txt")));
    }

    [Fact]
    public void ReleaseOnce_IsIdempotent_AndProtectsASuccessorsReallocatedLink()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The Ctrl-C double-fire proof: after this run releases its link, a SUCCESSOR run grabs the freed
        // .a slot for a DIFFERENT target. A late-firing handler (ProcessExit / CancelKeyPress) calling
        // ReleaseOnce again must NEVER remove the successor's link — the Interlocked once-guard short-circuits
        // first (and the IsJunctionTo target guard is belt-and-suspenders).
        string realRoot = Path.Combine(_root, "realroot");
        string successorRoot = Path.Combine(_root, "successor");
        Directory.CreateDirectory(successorRoot);
        string link = Track(Path.Combine(_root, "base", ".a"));
        Assert.True(WorktreeJunction.TryCreateJunction(link, realRoot));

        var lifetime = new WorktreeJunctionLifetime(link, realRoot, TextWriter.Null);
        lifetime.ReleaseOnce();
        Assert.False(Directory.Exists(link)); // our link released

        // A successor run re-allocates the SAME .a slot to its own target.
        Assert.True(WorktreeJunction.TryCreateJunction(link, successorRoot));

        // The late double-fire (ReleaseOnce again, then Dispose) must leave the successor's link alone.
        lifetime.ReleaseOnce();
        lifetime.Dispose();

        Assert.True(WorktreeJunction.IsJunctionTo(link, successorRoot)); // successor's link untouched
    }

    [Fact]
    public void ReleaseOnce_RefusesLinkPointingElsewhere()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The target guard on its own: the link points at OTHER, but the lifetime believes it owns
        // realRoot → ReleaseOnce must NOT remove it (IsJunctionTo(link, realRoot) is false).
        string realRoot = Path.Combine(_root, "realroot");
        string otherRoot = Path.Combine(_root, "otherroot");
        Directory.CreateDirectory(otherRoot);
        string link = Track(Path.Combine(_root, "base", ".a"));
        Assert.True(WorktreeJunction.TryCreateJunction(link, otherRoot));

        using var lifetime = new WorktreeJunctionLifetime(link, realRoot, TextWriter.Null);
        lifetime.ReleaseOnce();

        Assert.True(WorktreeJunction.IsJunctionTo(link, otherRoot)); // refused — points elsewhere
    }

    [Fact]
    public async Task ReleaseOnce_ConcurrentDoubleFire_RemovesExactlyOnce_NoThrow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Ctrl-C fires CancelKeyPress AND (via the cancelled token) the finally→Dispose concurrently; the
        // Interlocked once-guard must make simultaneous ReleaseOnce calls safe.
        string realRoot = Path.Combine(_root, "realroot");
        Directory.CreateDirectory(realRoot);
        string link = Track(Path.Combine(_root, "base", ".a"));
        Assert.True(WorktreeJunction.TryCreateJunction(link, realRoot));

        using var lifetime = new WorktreeJunctionLifetime(link, realRoot, TextWriter.Null);
        using var start = new Barrier(8);
        Task[] racers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            lifetime.ReleaseOnce();
        })).ToArray();

        await Task.WhenAll(racers); // no exception from the race
        Assert.False(Directory.Exists(link)); // released
    }

    [Fact]
    public void ReleaseOnce_NonReparsePoint_IsNoOp_CrossOS()
    {
        // Cross-OS guard: a plain (non-reparse-point) directory is NEVER removed — the lifetime can only ever
        // release an actual junction. Construct + Dispose must also register/unregister handlers without throwing.
        string realDir = Path.Combine(_root, "not-a-junction");
        Directory.CreateDirectory(realDir);
        File.WriteAllText(Path.Combine(realDir, "keep.txt"), "x");

        using (var lifetime = new WorktreeJunctionLifetime(realDir, realDir, TextWriter.Null))
        {
            lifetime.ReleaseOnce();
        }

        Assert.True(Directory.Exists(realDir));
        Assert.True(File.Exists(Path.Combine(realDir, "keep.txt")));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private string Track(string link)
    {
        _links.Add(link);
        return link;
    }

    public void Dispose()
    {
        foreach (string link in _links)
        {
            WorktreeJunction.RemoveJunctionLink(link); // link-only, so the tree delete never follows a reparse point
        }

        try { Io.SafeDelete.DeleteDirectory(_root); } catch { /* best-effort temp cleanup */ }
    }
}
