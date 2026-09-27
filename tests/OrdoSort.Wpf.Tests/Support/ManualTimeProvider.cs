namespace OrdoSort.Wpf.Tests;

/// <summary>A clock that only moves when the test moves it, for code that
/// takes a <see cref="TimeProvider"/>. Timers fire on the test's own thread
/// inside <see cref="Advance"/>, in due order, so a debounce is tested
/// without sleeping (docs/testing.md). A timer set to fire after zero time
/// fires at once, inside the <c>Change</c> call that set it, which is the
/// deterministic stand-in for "as soon as possible".</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward, firing every timer that falls due
    /// on the way, earliest first.</summary>
    public void Advance(TimeSpan by)
    {
        var target = _now + by;
        while (true)
        {
            var next = _timers.Where(t => t.DueAt is { } due && due <= target).MinBy(t => t.DueAt);
            if (next is null) break;
            _now = next.DueAt!.Value;
            next.Fire();
        }
        _now = target;
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _time;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public ManualTimer(ManualTimeProvider time, TimerCallback callback, object? state)
        {
            _time = time;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                DueAt = null;
                return true;
            }
            DueAt = _time._now + dueTime;
            if (dueTime == TimeSpan.Zero) Fire();
            return true;
        }

        public void Fire()
        {
            DueAt = _period == Timeout.InfiniteTimeSpan ? null : _time._now + _period;
            _callback(_state);
        }

        public void Dispose()
        {
            DueAt = null;
            _time._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
