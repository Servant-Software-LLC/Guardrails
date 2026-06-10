namespace Guardrails.Core.Execution;

/// <summary>
/// An async shared/exclusive lock over the workspace. Non-exclusive tasks hold it
/// shared (any number at once); a task with <c>exclusive: true</c> (the default for
/// prompt actions — SSOT §3) holds it exclusively, running alone. Waiters are served
/// strictly FIFO so a stream of shared tasks cannot starve a waiting exclusive one,
/// and vice versa.
/// </summary>
public sealed class WorkspaceLock
{
    private readonly object _gate = new();
    private readonly Queue<Waiter> _waiters = new();
    private int _activeShared;
    private bool _activeExclusive;

    /// <summary>
    /// Acquire the lock. Completes immediately when compatible with current holders and
    /// no earlier waiter is queued; otherwise waits FIFO. Always pair with
    /// <see cref="Release"/> for the same <paramref name="exclusive"/> value.
    /// </summary>
    public Task AcquireAsync(bool exclusive, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_waiters.Count == 0 && CanEnter(exclusive))
            {
                Enter(exclusive);
                return Task.CompletedTask;
            }

            var waiter = new Waiter(exclusive);
            _waiters.Enqueue(waiter);

            if (cancellationToken.CanBeCanceled)
            {
                waiter.CancellationRegistration = cancellationToken.Register(() =>
                {
                    // Mark cancelled; the dispatcher discards cancelled waiters as it
                    // reaches them (lazy removal keeps the queue strictly FIFO).
                    if (waiter.Source.TrySetCanceled(cancellationToken))
                    {
                        lock (_gate)
                        {
                            Dispatch();
                        }
                    }
                });
            }

            return waiter.Source.Task;
        }
    }

    /// <summary>Release a hold previously acquired with the same <paramref name="exclusive"/> value.</summary>
    public void Release(bool exclusive)
    {
        lock (_gate)
        {
            if (exclusive)
            {
                _activeExclusive = false;
            }
            else
            {
                _activeShared--;
            }

            Dispatch();
        }
    }

    private bool CanEnter(bool exclusive) =>
        exclusive ? _activeShared == 0 && !_activeExclusive : !_activeExclusive;

    private void Enter(bool exclusive)
    {
        if (exclusive)
        {
            _activeExclusive = true;
        }
        else
        {
            _activeShared++;
        }
    }

    /// <summary>Admit queued waiters, in order, while they remain compatible. Caller holds the gate.</summary>
    private void Dispatch()
    {
        while (_waiters.TryPeek(out Waiter? next))
        {
            if (next.Source.Task.IsCanceled)
            {
                _waiters.Dequeue();
                next.CancellationRegistration?.Dispose();
                continue;
            }

            if (!CanEnter(next.Exclusive))
            {
                return; // strict FIFO: nothing behind an incompatible head may jump it
            }

            _waiters.Dequeue();
            Enter(next.Exclusive);
            next.CancellationRegistration?.Dispose();

            // RunContinuationsAsynchronously on the TCS keeps continuations off this
            // thread while we still hold the gate.
            next.Source.TrySetResult();
        }
    }

    private sealed class Waiter(bool exclusive)
    {
        public bool Exclusive { get; } = exclusive;
        public TaskCompletionSource Source { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? CancellationRegistration { get; set; }
    }
}
