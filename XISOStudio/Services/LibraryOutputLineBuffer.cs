using System.Text;
using Serilog;

namespace XISOStudio.Services;

/// <summary>
/// Reassembles XISOSharp's chunked console output into complete, sanitized lines before
/// forwarding it to Serilog. The library writes progress fragments one call at a time —
/// including runs of backspaces and spaces used for its console animation — so forwarding
/// each call as its own log line floods the viewer with "[xiso]" noise and control
/// characters. Only complete lines are emitted, control characters are stripped, and runs
/// of whitespace are collapsed. A fragment that never receives a newline is flushed once it
/// grows beyond <see cref="MaxBufferedLength"/> so the buffer cannot grow without bound.
/// </summary>
internal sealed class LibraryOutputLineBuffer
{
    /// <summary>Flushes an unterminated fragment once it grows beyond this length.</summary>
    internal const int MaxBufferedLength = 4096;

    private readonly ILogger _logger;
    private readonly string _prefix;
    private readonly StringBuilder _buffer = new();
    private readonly Lock _sync = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryOutputLineBuffer"/> class.
    /// </summary>
    /// <param name="logger">Logger that receives the complete lines.</param>
    /// <param name="prefix">Prefix added to every forwarded line.</param>
    internal LibraryOutputLineBuffer(ILogger logger, string prefix)
    {
        _logger = logger;
        _prefix = prefix;
    }

    /// <summary>
    /// Appends a raw library output chunk and forwards every complete line it contains.
    /// </summary>
    /// <param name="chunk">Raw text written by the library; may be empty.</param>
    internal void Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;

        List<string>? completed = null;
        lock (_sync)
        {
            _buffer.Append(chunk);

            int newline;
            while ((newline = IndexOfNewline(_buffer)) >= 0)
            {
                var line = Sanitize(_buffer.ToString(0, newline));
                _buffer.Remove(0, newline + 1);
                if (line.Length > 0) (completed ??= []).Add(line);
            }

            // A fragment without a newline (for example a very long path) is flushed
            // rather than buffered forever.
            if (_buffer.Length > MaxBufferedLength)
            {
                var partial = Sanitize(_buffer.ToString());
                _buffer.Clear();
                if (partial.Length > 0) (completed ??= []).Add(partial + " …");
            }
        }

        if (completed is null) return;
        foreach (var line in completed)
        {
            // ":l" on both properties keeps Serilog from quoting the prefix or the line.
            _logger.Information("{Prefix:l}{Line:l}", _prefix, line);
        }
    }

    /// <summary>Returns the index of the first newline in the buffer, or <c>-1</c> when there is none.</summary>
    /// <param name="builder">Buffer to scan.</param>
    /// <returns>Index of the first <c>'\n'</c>, or <c>-1</c>.</returns>
    private static int IndexOfNewline(StringBuilder builder)
    {
        for (var i = 0; i < builder.Length; i++)
        {
            if (builder[i] == '\n') return i;
        }

        return -1;
    }

    /// <summary>
    /// Removes control characters (the library's backspace animation, carriage returns, and
    /// escape sequences) and collapses whitespace runs so a complete line stays readable.
    /// </summary>
    /// <param name="text">Raw line to sanitize.</param>
    /// <returns>The sanitized, trimmed line; empty when nothing printable remains.</returns>
    internal static string Sanitize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var raw in text)
        {
            var c = raw == '\t' ? ' ' : raw;
            if (char.IsControl(c)) continue;

            var isSpace = c == ' ';
            if (isSpace && lastWasSpace) continue;

            builder.Append(c);
            lastWasSpace = isSpace;
        }

        return builder.ToString().Trim();
    }
}