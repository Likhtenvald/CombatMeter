using System;
using System.Collections.Generic;

namespace DiagnosticDamageProbe.Encounter;

// Pure timestamp-based union of one player's offensive activity intervals.
internal sealed class PlayerDpsActivityTracker
{
    internal const int MaxRetainedClosedIntervals = 128;

    private readonly List<Interval> _closed = new List<Interval>();
    private double _compactedDuration;
    private double _compactedThrough;
    private bool _hasCurrent;
    private double _currentStart;
    private double _currentHistoricalEnd;
    private double _lastOffensiveEventTime;

    internal int RetainedClosedIntervalCount => _closed.Count;
    internal double LastOffensiveEventTime => _hasCurrent ? _lastOffensiveEventTime : double.NaN;

    internal void Add(double eventTime, double timeout)
    {
        Validate(eventTime); ValidateTimeout(timeout);
        double end = eventTime + timeout;

        if (!_hasCurrent)
        {
            StartCurrent(eventTime);
            return;
        }

        double deadline = Math.Max(_currentHistoricalEnd, _lastOffensiveEventTime + timeout);
        if (eventTime >= _currentStart && eventTime <= deadline)
        {
            if (eventTime > _lastOffensiveEventTime) _lastOffensiveEventTime = eventTime;
            return;
        }

        if (eventTime > deadline)
        {
            CloseCurrent(deadline);
            StartCurrent(eventTime);
            return;
        }

        AddClosed(eventTime, end);
        AbsorbClosedTailIntoCurrent();
    }

    internal double ActiveTime(double now, double timeout, bool mayGrow)
    {
        Validate(now); ValidateTimeout(timeout);
        if (_hasCurrent)
        {
            double deadline = Math.Max(_currentHistoricalEnd, _lastOffensiveEventTime + timeout);
            if (!mayGrow) CloseCurrent(Math.Min(now, deadline));
            else if (now >= deadline) CloseCurrent(deadline);
        }

        double total = _compactedDuration;
        for (int i = 0; i < _closed.Count; i++) total += _closed[i].End - _closed[i].Start;
        if (_hasCurrent)
        {
            double deadline = Math.Max(_currentHistoricalEnd, _lastOffensiveEventTime + timeout);
            total += Math.Max(0d, Math.Min(now, deadline) - _currentStart);
        }
        return total;
    }

    internal void Freeze(double boundary, double timeout) => ActiveTime(boundary, timeout, false);

    internal void Reset()
    {
        _closed.Clear(); _compactedDuration = _compactedThrough = 0d;
        _hasCurrent = false; _currentStart = _currentHistoricalEnd = _lastOffensiveEventTime = 0d;
    }

    private void StartCurrent(double eventTime)
    {
        _hasCurrent = true; _currentStart = eventTime;
        _currentHistoricalEnd = eventTime; _lastOffensiveEventTime = eventTime;
        AbsorbClosedTailIntoCurrent();
    }

    private void CloseCurrent(double end)
    {
        if (!_hasCurrent) return;
        AddClosed(_currentStart, Math.Max(_currentStart, end));
        _hasCurrent = false;
    }

    private void AddClosed(double start, double end)
    {
        if (!(end > start) || end <= _compactedThrough) return;
        start = Math.Max(start, _compactedThrough);
        int index = 0;
        while (index < _closed.Count && _closed[index].End < start) index++;
        while (index < _closed.Count && _closed[index].Start <= end)
        {
            start = Math.Min(start, _closed[index].Start);
            end = Math.Max(end, _closed[index].End);
            _closed.RemoveAt(index);
        }
        _closed.Insert(index, new Interval(start, end));
        Compact();
    }

    private void AbsorbClosedTailIntoCurrent()
    {
        while (_hasCurrent && _closed.Count > 0)
        {
            int index = _closed.Count - 1;
            Interval interval = _closed[index];
            if (interval.End < _currentStart) break;
            _closed.RemoveAt(index);
            _currentStart = Math.Min(_currentStart, interval.Start);
            _currentHistoricalEnd = Math.Max(_currentHistoricalEnd, interval.End);
        }
    }

    private void Compact()
    {
        while (_closed.Count > MaxRetainedClosedIntervals)
        {
            Interval oldest = _closed[0]; _closed.RemoveAt(0);
            _compactedDuration += oldest.End - oldest.Start;
            _compactedThrough = Math.Max(_compactedThrough, oldest.End);
        }
    }

    private static void Validate(double value)
    { if (double.IsNaN(value) || double.IsInfinity(value) || value < 0d) throw new ArgumentOutOfRangeException(nameof(value)); }
    private static void ValidateTimeout(double value)
    { if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d) throw new ArgumentOutOfRangeException(nameof(value)); }

    private readonly struct Interval
    {
        internal readonly double Start;
        internal readonly double End;
        internal Interval(double start, double end) { Start = start; End = end; }
    }
}
