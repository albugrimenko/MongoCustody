using MongoCustody.Core.Models;

namespace MongoCustody.Cli;

/// <summary>
/// Renders a live progress bar to <see cref="Console.Error"/> while a
/// long-running backup or restore operation runs. Uses <c>\r</c> to
/// overwrite the current line in place so the terminal stays clean.
///
/// <para>
/// Only activates when stderr is attached to an interactive terminal
/// (<c>!Console.IsErrorRedirected</c>). When output is piped or
/// redirected, progress is silently suppressed.
/// </para>
///
/// <para>
/// Thread-safe: <see cref="IProgress{T}.Report"/> may be called from
/// any thread (collection exports run in parallel).
/// </para>
/// </summary>
internal sealed class ConsoleProgress : IDisposable
{
    private readonly string _label;
    private readonly bool   _active;
    private readonly object _lock = new();
    private int             _lastLineLength;

    private const int BarWidth = 20;

    // Braille spinner frames — fall back to ASCII on narrow encodings.
    private static readonly string[] SpinnerFrames =
        Console.OutputEncoding.GetMaxByteCount(1) >= 3
            ? ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"]
            : ["|", "/", "-", "\\"];

    private int _spinnerIndex;

    internal ConsoleProgress(string label)
    {
        _label  = label;
        _active = !Console.IsErrorRedirected;

        if (_active)
            // Hide cursor to avoid flicker during in-place updates.
            try { Console.CursorVisible = false; } catch { /* not all terminals support this */ }
    }

    /// <summary>
    /// Returns an <see cref="IProgress{T}"/> that feeds reports into this renderer.
    /// Pass the result to <c>RunFullBackupAsync</c> or <c>RestoreAsync</c>.
    /// </summary>
    internal IProgress<OperationProgress> AsProgress() =>
        new Progress<OperationProgress>(Render);

    private void Render(OperationProgress p)
    {
        if (!_active) return;

        lock (_lock)
        {
            var spinner = SpinnerFrames[_spinnerIndex % SpinnerFrames.Length];
            _spinnerIndex++;

            var bar  = BuildBar(p.Percentage);
            var line = $"{spinner} {_label}  {bar}  {p.Completed}/{p.Total}  {p.Percentage:F0}%  — {p.CurrentItem}";

            // Pad to overwrite any leftover chars from a longer previous line.
            if (line.Length < _lastLineLength)
                line = line.PadRight(_lastLineLength);
            _lastLineLength = line.Length;

            Console.Error.Write($"\r{line}");
        }
    }

    /// <summary>
    /// Clears the progress line and restores the cursor. Call after the
    /// operation completes so the result summary prints on a clean line.
    /// </summary>
    public void Dispose()
    {
        if (!_active) return;

        lock (_lock)
        {
            // Erase progress line.
            Console.Error.Write($"\r{new string(' ', _lastLineLength)}\r");
            try { Console.CursorVisible = true; } catch { /* ignore */ }
        }
    }

    private static string BuildBar(double percentage)
    {
        var filled = (int)Math.Round(percentage / 100.0 * BarWidth);
        filled = Math.Clamp(filled, 0, BarWidth);
        return $"[{new string('█', filled)}{new string('░', BarWidth - filled)}]";
    }
}
