using System.Collections.Concurrent;

namespace Camzivo.Data.Repositories;

/// <summary>
/// Serialises preference writes off the UI thread.
///
/// The previous implementation started one task per change, so rapid layout switching could persist an
/// intermediate value: completion order was not input order. This writer keeps the newest value per key and writes
/// them in order from a single background loop, which makes "last value wins" true by construction.
/// </summary>
public sealed class PreferenceWriter : IAsyncDisposable
{
    private readonly PreferencesRepository _repository;
    private readonly Action<Exception>? _onError;
    private readonly ConcurrentDictionary<string, string> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _idleGate = new();
    private readonly Task _loop;
    private TaskCompletionSource? _idle;
    private volatile bool _disposed;

    public PreferenceWriter(PreferencesRepository repository, Action<Exception>? onError = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _onError = onError;
        _loop = Task.Run(RunAsync);
    }

    public int PendingCount => _pending.Count;

    public void Enqueue(string key, string value)
    {
        if (_disposed || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        _pending[key] = value;

        lock (_idleGate)
        {
            _idle = null;
        }

        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Waits until everything queued has been written. Returns false on timeout; never throws.</summary>
    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        if (_pending.IsEmpty)
        {
            return true;
        }

        TaskCompletionSource idle;
        lock (_idleGate)
        {
            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            idle = _idle;
        }

        _signal.Release();
        var completed = await Task.WhenAny(idle.Task, Task.Delay(timeout)).ConfigureAwait(false);
        return ReferenceEquals(completed, idle.Task);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await FlushAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        _shutdown.Cancel();
        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _shutdown.Dispose();
        _signal.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!_pending.IsEmpty)
            {
                foreach (var pair in _pending.ToArray())
                {
                    // Remove before writing: a value that arrives during the write stays queued for the next round.
                    _pending.TryRemove(pair.Key, out _);

                    try
                    {
                        await _repository.SetAsync(pair.Key, pair.Value, DateTimeOffset.UtcNow).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        _onError?.Invoke(exception);
                    }
                }
            }

            lock (_idleGate)
            {
                if (_pending.IsEmpty)
                {
                    _idle?.TrySetResult();
                }
            }
        }
    }
}
