using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

public sealed class WorkspaceLockTests
{
    [Fact]
    public async Task SharedHolders_CoexistConcurrently()
    {
        var ws = new WorkspaceLock();

        await ws.AcquireAsync(exclusive: false, TestContext.Current.CancellationToken);
        Task second = ws.AcquireAsync(exclusive: false, TestContext.Current.CancellationToken);

        Assert.True(second.IsCompletedSuccessfully);
        ws.Release(false);
        ws.Release(false);
    }

    [Fact]
    public async Task Exclusive_WaitsForAllSharedHolders()
    {
        var ws = new WorkspaceLock();
        await ws.AcquireAsync(false, TestContext.Current.CancellationToken);
        await ws.AcquireAsync(false, TestContext.Current.CancellationToken);

        Task exclusive = ws.AcquireAsync(exclusive: true, TestContext.Current.CancellationToken);
        Assert.False(exclusive.IsCompleted);

        ws.Release(false);
        Assert.False(exclusive.IsCompleted);

        ws.Release(false);
        await exclusive; // both shared gone → exclusive admitted
        ws.Release(true);
    }

    [Fact]
    public async Task Shared_QueuedBehindWaitingExclusive_DoesNotStarveIt()
    {
        var ws = new WorkspaceLock();
        await ws.AcquireAsync(false, TestContext.Current.CancellationToken);                  // holder

        Task exclusive = ws.AcquireAsync(true, TestContext.Current.CancellationToken);        // queued first
        Task lateShared = ws.AcquireAsync(false, TestContext.Current.CancellationToken);      // queued second

        // FIFO: the late shared must NOT jump the waiting exclusive.
        Assert.False(exclusive.IsCompleted);
        Assert.False(lateShared.IsCompleted);

        ws.Release(false);
        await exclusive;
        Assert.False(lateShared.IsCompleted);          // exclusive holds alone

        ws.Release(true);
        await lateShared;
        ws.Release(false);
    }

    [Fact]
    public async Task ExclusiveHolder_BlocksEveryoneUntilReleased()
    {
        var ws = new WorkspaceLock();
        await ws.AcquireAsync(true, TestContext.Current.CancellationToken);

        Task shared = ws.AcquireAsync(false, TestContext.Current.CancellationToken);
        Task exclusive = ws.AcquireAsync(true, TestContext.Current.CancellationToken);
        Assert.False(shared.IsCompleted);
        Assert.False(exclusive.IsCompleted);

        ws.Release(true);
        await shared;                                   // FIFO: shared queued first
        Assert.False(exclusive.IsCompleted);

        ws.Release(false);
        await exclusive;
        ws.Release(true);
    }

    [Fact]
    public async Task CancelledWaiter_IsSkippedByDispatch()
    {
        var ws = new WorkspaceLock();
        await ws.AcquireAsync(true, TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        Task cancelled = ws.AcquireAsync(true, cts.Token);
        Task survivor = ws.AcquireAsync(false, TestContext.Current.CancellationToken);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        ws.Release(true);
        await survivor; // the cancelled head was discarded, not deadlocked
        ws.Release(false);
    }
}
