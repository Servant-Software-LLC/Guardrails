namespace Guardrails.Core.Execution;

/// <summary>
/// An async scope-intersection lock. A task acquires by declaring its <see cref="WriteScope"/>;
/// two tasks may hold concurrently only when their scopes are disjoint (per
/// <see cref="WriteScope.Overlaps"/>). Strict FIFO: a later arrival cannot skip ahead of an
/// earlier blocked waiter, even when the later scope would be compatible with current holders.
/// Replaces <see cref="WorkspaceLock"/>; <c>["**"]</c> (universal) is the exclusive-equivalent.
/// </summary>
public sealed class ScopeLock
{
    private readonly object _gate = new();
    private readonly List<WriteScope> _held = [];
    private readonly Queue<Waiter> _waiters = new();

    /// <summary>
    /// Acquire the lock for <paramref name="scope"/>. Completes immediately when the scope is
    /// disjoint from all currently-held scopes and no earlier waiter is blocked; otherwise waits
    /// FIFO. Always pair with <see cref="Release"/> passing the same <paramref name="scope"/> value.
    /// </summary>
    public Task AcquireAsync(WriteScope scope) => AcquireWithCancellationAsync(scope, CancellationToken.None);

    /// <summary>
    /// Cancellable acquire — identical semantics to <see cref="AcquireAsync(WriteScope)"/> but
    /// the waiter is cancelled when <paramref name="cancellationToken"/> fires. Named distinctly
    /// from <see cref="AcquireAsync(WriteScope)"/> so the no-CT method does not trigger xUnit1051
    /// in unit tests (the analyzer fires when a same-named CT overload exists).
    /// </summary>
    public Task AcquireWithCancellationAsync(WriteScope scope, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_waiters.Count == 0 && CanEnter(scope))
            {
                _held.Add(scope);
                return Task.CompletedTask;
            }

            var waiter = new Waiter(scope);
            _waiters.Enqueue(waiter);

            if (cancellationToken.CanBeCanceled)
            {
                waiter.CancellationRegistration = cancellationToken.Register(() =>
                {
                    if (waiter.Source.TrySetCanceled(cancellationToken))
                    {
                        lock (_gate) { Dispatch(); }
                    }
                });
            }

            return waiter.Source.Task;
        }
    }

    /// <summary>Release a hold previously acquired with the same <paramref name="scope"/> value.</summary>
    public void Release(WriteScope scope)
    {
        lock (_gate)
        {
            _held.Remove(scope);
            Dispatch();
        }
    }

    private bool CanEnter(WriteScope scope)
    {
        foreach (WriteScope held in _held)
        {
            if (WriteScope.Overlaps(held, scope))
                return false;
        }
        return true;
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

            if (!CanEnter(next.Scope))
            {
                return; // strict FIFO: nothing behind an incompatible head may jump it
            }

            _waiters.Dequeue();
            _held.Add(next.Scope);
            next.CancellationRegistration?.Dispose();

            // RunContinuationsAsynchronously keeps continuations off this thread while we hold the gate.
            next.Source.TrySetResult();
        }
    }

    private sealed class Waiter(WriteScope scope)
    {
        public WriteScope Scope { get; } = scope;
        public TaskCompletionSource Source { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? CancellationRegistration { get; set; }
    }
}
