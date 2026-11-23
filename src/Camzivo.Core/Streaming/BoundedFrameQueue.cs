namespace Camzivo.Core.Streaming;

/// <summary>Counters for one queue; plain properties so they can be sampled by telemetry without locking.</summary>
public readonly record struct QueueStatistics(long Enqueued, long Dequeued, long Dropped, int Count, int Capacity)
{
    public long Pending => Enqueued - Dequeued - Dropped;
}

/// <summary>
/// Fixed-capacity, thread-safe queue that drops the oldest item on overflow.
///
/// Live surveillance must show "now": falling behind and catching up later is worse than dropping. Every drop
/// is counted so the UI can show it instead of pretending the stream is perfect.
/// </summary>
public sealed class BoundedFrameQueue<T>
{
    private readonly object _gate = new();
    private readonly Queue<T> _items;
    private long _enqueued;
    private long _dequeued;
    private long _dropped;

    public BoundedFrameQueue(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 1.");
        }

        Capacity = capacity;
        _items = new Queue<T>(capacity);
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public QueueStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                return new QueueStatistics(_enqueued, _dequeued, _dropped, _items.Count, Capacity);
            }
        }
    }

    /// <summary>Adds an item, evicting the oldest when full. Returns true when a drop occurred.</summary>
    public bool TryEnqueue(T item)
    {
        lock (_gate)
        {
            var dropped = false;
            if (_items.Count == Capacity)
            {
                _items.Dequeue();
                _dropped++;
                dropped = true;
            }

            _items.Enqueue(item);
            _enqueued++;
            return dropped;
        }
    }

    public bool TryDequeue(out T? item)
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                item = default;
                return false;
            }

            item = _items.Dequeue();
            _dequeued++;
            return true;
        }
    }

    /// <summary>Removes everything (used on stream teardown) without touching lifetime counters.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }
}
