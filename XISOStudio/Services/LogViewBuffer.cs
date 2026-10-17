using System.Collections.Concurrent;

namespace XISOStudio.Services;

/// <summary>
/// Thread-safe, bounded buffer behind the on-screen log viewer. Producers enqueue rendered
/// lines from any thread; the UI thread drains them in bounded batches and keeps only the
/// newest lines, so a logging burst can neither queue unbounded dispatcher work nor grow
/// the viewer without limit. The full log is always written to the rolling file log.
/// </summary>
public sealed class LogViewBuffer
{
    /// <summary>Maximum number of lines kept in the viewer (the oldest are dropped first).</summary>
    private const int DefaultMaxLines = 2000;

    /// <summary>Maximum length of a single displayed line; longer lines are ellipsized.</summary>
    private const int DefaultMaxLineLength = 1000;

    /// <summary>Maximum number of lines allowed to wait for a UI flush before the oldest are dropped.</summary>
    private const int DefaultMaxPendingLines = 10000;

    private readonly ConcurrentQueue<string> _pending = new();
    private readonly Queue<string> _lines = new();
    private readonly int _maxLines;
    private readonly int _maxLineLength;
    private readonly int _maxPendingLines;
    private int _pendingCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogViewBuffer"/> class.
    /// </summary>
    /// <param name="maxLines">Maximum number of lines kept for display.</param>
    /// <param name="maxLineLength">Maximum length of a single displayed line.</param>
    /// <param name="maxPendingLines">Maximum number of lines allowed to wait for a UI flush.</param>
    public LogViewBuffer(int maxLines = DefaultMaxLines, int maxLineLength = DefaultMaxLineLength,
        int maxPendingLines = DefaultMaxPendingLines)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPendingLines, 1);

        _maxLines = maxLines;
        _maxLineLength = maxLineLength;
        _maxPendingLines = maxPendingLines;
    }

    /// <summary>Number of lines currently held for display.</summary>
    public int Count
    {
        get
        {
            lock (_lines)
            {
                return _lines.Count;
            }
        }
    }

    /// <summary>True when lines are waiting for the next UI flush.</summary>
    public bool HasPending => !_pending.IsEmpty;

    /// <summary>
    /// Adds a line for display. The line is ellipsized when it is longer than the per-line
    /// limit, and the oldest pending line is dropped when the backlog exceeds its limit.
    /// </summary>
    /// <param name="line">Rendered log line to display.</param>
    public void Enqueue(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var displayLine = line.Length > _maxLineLength
            ? string.Concat(line.AsSpan(0, _maxLineLength - 1), "…")
            : line;

        _pending.Enqueue(displayLine);

        // Keep the backlog bounded: a burst must not grow memory while the UI catches up.
        if (Interlocked.Increment(ref _pendingCount) > _maxPendingLines && _pending.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingCount);
        }
    }

    /// <summary>
    /// Removes and returns up to <paramref name="maxCount"/> pending lines, oldest first, so
    /// the UI can update the viewer once per batch instead of once per line.
    /// </summary>
    /// <param name="maxCount">Maximum number of lines to return.</param>
    /// <returns>The drained lines, oldest first; empty when nothing is pending.</returns>
    public List<string> Drain(int maxCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);

        var drained = new List<string>(Math.Min(maxCount, 256));
        while (drained.Count < maxCount && _pending.TryDequeue(out var line))
        {
            Interlocked.Decrement(ref _pendingCount);
            drained.Add(line);
        }

        return drained;
    }

    /// <summary>
    /// Adds drained lines to the display buffer, dropping the oldest lines when the
    /// display limit is exceeded.
    /// </summary>
    /// <param name="lines">Lines to add, oldest first.</param>
    /// <returns>
    /// <c>true</c> when lines were dropped, so the caller must rebuild the whole viewer
    /// text instead of appending.
    /// </returns>
    public bool Commit(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var trimmed = false;
        lock (_lines)
        {
            foreach (var line in lines)
            {
                _lines.Enqueue(line);
            }

            while (_lines.Count > _maxLines)
            {
                _lines.Dequeue();
                trimmed = true;
            }
        }

        return trimmed;
    }

    /// <summary>Returns a snapshot of the lines currently held for display, oldest first.</summary>
    /// <returns>The current display lines.</returns>
    public List<string> Snapshot()
    {
        lock (_lines)
        {
            return [.. _lines];
        }
    }

    /// <summary>Clears both the display buffer and any lines waiting for a flush.</summary>
    public void Clear()
    {
        lock (_lines)
        {
            _lines.Clear();
        }

        while (_pending.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingCount);
        }
    }
}