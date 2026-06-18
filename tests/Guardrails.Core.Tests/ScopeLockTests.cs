using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="ScopeLock"/> — the generalisation of <see cref="WorkspaceLock"/>
/// where admission is keyed on write-scope intersection rather than shared/exclusive.
/// <c>["**"]</c> is the universal (exclusive-equivalent) scope; <c>[]</c> is disjoint from all.
/// Strict FIFO: a later waiter cannot skip ahead of a blocked earlier one.
///
/// Authored BEFORE the feature exists: <see cref="ScopeLock"/> does not yet exist in
/// <c>Guardrails.Core.Execution</c>. The suite will not compile against current
/// code — the intended failure that proves the behaviour is unbuilt.
/// </summary>
public sealed class ScopeLockTests
{
    private static WriteScope Scope(params string[] globs) => WriteScope.Parse(globs);
    private static readonly WriteScope Empty = WriteScope.Parse([]);
    private static readonly WriteScope Universal = WriteScope.Parse(["**"]);

    // ── Disjoint scope — admitted immediately ──────────────────────────────────

    [Fact]
    public async Task Acquire_DisjointScope_AdmittedImmediately()
    {
        var scopeLock = new ScopeLock();
        var scopeA = Scope("src/A/**");
        var scopeB = Scope("src/B/**");

        await scopeLock.AcquireAsync(scopeA);

        var acquireB = scopeLock.AcquireAsync(scopeB);
        Assert.True(acquireB.IsCompleted, "disjoint scope must be admitted without waiting");

        scopeLock.Release(scopeB);
        scopeLock.Release(scopeA);
    }

    // ── Overlapping scope — blocks until holder releases ───────────────────────

    [Fact]
    public async Task Acquire_OverlappingScope_BlocksUntilHolderReleases()
    {
        var scopeLock = new ScopeLock();
        var scopeA = Scope("src/Feature/**");
        var scopeB = Scope("src/Feature/Thing.cs"); // overlaps A

        await scopeLock.AcquireAsync(scopeA);

        var acquireB = scopeLock.AcquireAsync(scopeB);
        Assert.False(acquireB.IsCompleted, "overlapping scope must not be admitted while holder holds");

        scopeLock.Release(scopeA);
        await acquireB; // admitted after release
        scopeLock.Release(scopeB);
    }

    // ── Empty scope — disjoint from every held scope ───────────────────────────

    [Fact]
    public async Task Acquire_EmptyScope_AdmittedAgainstNarrowHolder()
    {
        var scopeLock = new ScopeLock();
        var narrow = Scope("src/A/**");
        var empty = Empty;

        await scopeLock.AcquireAsync(narrow);

        var acquireEmpty = scopeLock.AcquireAsync(empty);
        Assert.True(acquireEmpty.IsCompleted,
            "empty scope is disjoint from all — admitted immediately");

        scopeLock.Release(empty);
        scopeLock.Release(narrow);
    }

    [Fact]
    public async Task Acquire_EmptyScope_AdmittedAgainstUniversalHolder()
    {
        // [] short-circuits before ["**"] — the empty scope is admitted even while universal holds.
        var scopeLock = new ScopeLock();
        var universal = Universal;
        var empty = Empty;

        await scopeLock.AcquireAsync(universal);

        var acquireEmpty = scopeLock.AcquireAsync(empty);
        Assert.True(acquireEmpty.IsCompleted,
            "empty scope beats universal — admitted immediately");

        scopeLock.Release(empty);
        scopeLock.Release(universal);
    }

    // ── Universal scope — mutually exclusive ──────────────────────────────────

    [Fact]
    public async Task Acquire_TwoUniversal_MutuallyExclusive()
    {
        var scopeLock = new ScopeLock();
        var u1 = Universal;
        var u2 = Universal;

        await scopeLock.AcquireAsync(u1);

        var acquireU2 = scopeLock.AcquireAsync(u2);
        Assert.False(acquireU2.IsCompleted, "two universal holders must be mutually exclusive");

        scopeLock.Release(u1);
        await acquireU2;
        scopeLock.Release(u2);
    }

    [Fact]
    public async Task Acquire_UniversalAndNarrow_Incompatible()
    {
        var scopeLock = new ScopeLock();
        var universal = Universal;
        var narrow = Scope("src/A/**");

        await scopeLock.AcquireAsync(universal);

        var acquireNarrow = scopeLock.AcquireAsync(narrow);
        Assert.False(acquireNarrow.IsCompleted, "narrow scope must wait while universal holds");

        scopeLock.Release(universal);
        await acquireNarrow;
        scopeLock.Release(narrow);
    }

    // ── Multiple concurrent disjoint holders ──────────────────────────────────

    [Fact]
    public async Task Acquire_ThreeDisjointScopes_AllAdmittedConcurrently()
    {
        var scopeLock = new ScopeLock();
        var scopeA = Scope("src/A/**");
        var scopeB = Scope("src/B/**");
        var scopeC = Scope("src/C/**");

        await scopeLock.AcquireAsync(scopeA);

        var acquireB = scopeLock.AcquireAsync(scopeB);
        var acquireC = scopeLock.AcquireAsync(scopeC);

        Assert.True(acquireB.IsCompleted, "B is disjoint from A — admitted concurrently");
        Assert.True(acquireC.IsCompleted, "C is disjoint from A and B — admitted concurrently");

        scopeLock.Release(scopeC);
        scopeLock.Release(scopeB);
        scopeLock.Release(scopeA);
    }

    // ── Strict FIFO — no skip-ahead ───────────────────────────────────────────

    [Fact]
    public async Task Acquire_StrictFifo_LaterNonOverlapping_WaitsForEarlierBlocked()
    {
        // A holds src/A/**. B arrives and overlaps A (waits). C arrives disjoint from A
        // but AFTER blocked B — strict FIFO: C cannot skip ahead of B.
        var scopeLock = new ScopeLock();
        var scopeA = Scope("src/A/**");
        var scopeB = Scope("src/A/**"); // overlaps A — blocks
        var scopeC = Scope("src/B/**"); // disjoint from A — but arrives after blocked B

        await scopeLock.AcquireAsync(scopeA);

        var acquireB = scopeLock.AcquireAsync(scopeB);
        Assert.False(acquireB.IsCompleted, "B overlaps holder — must wait");

        // C is compatible with A's scope but arrives after B is already in the queue.
        // Strict FIFO means C must not be admitted while B is blocked.
        var acquireC = scopeLock.AcquireAsync(scopeC);
        Assert.False(acquireC.IsCompleted,
            "C must not skip ahead of earlier blocked waiter B (strict FIFO — no skip-ahead)");

        // Releasing A admits B (head of queue); C is disjoint from B so it's also admitted
        // in the same dispatch cycle.
        scopeLock.Release(scopeA);
        await acquireB;
        await acquireC;

        scopeLock.Release(scopeC);
        scopeLock.Release(scopeB);
    }
}
