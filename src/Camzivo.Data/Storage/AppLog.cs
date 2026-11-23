using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Camzivo.Data.Storage;

/// <summary>
/// Minimal, dependency-free application log. It exists because the previous build had none: an unhandled exception
/// left nothing behind, so a failure in the field was undiagnosable.
///
/// Guarantees: never throws at the caller (logging must not break the app), bounded size (rotating, 5 × 2 MB),
/// newest-first tail for the in-app viewer, and every line passes through the injected redactor so credentials
/// cannot leak into a file the operator may email to support.
/// </summary>
public static class AppLog
{
    private const long MaxBytesPerFile = 2 * 1024 * 1024;

    private const int MaxFiles = 5;

    private const string CrashMarkerName = "last-crash.txt";

    private static readonly object Gate = new();

    private static readonly ConcurrentQueue<string> Recent = new();

    private static string? _root;

    private static string? _currentPath;

    private static long _currentBytes;

    private static Func<string, string> _redactor = static text => text;

    public static string? CurrentPath
    {
        get
        {
            lock (Gate)
            {
                return _currentPath;
            }
        }
    }

    public static bool IsEnabled
    {
        get
        {
            lock (Gate)
            {
                return _root is not null;
            }
        }
    }

    /// <summary>Points the log at a directory. Failure to create it disables logging instead of failing startup.</summary>
    public static void Initialize(string logRoot, Func<string, string>? redactor = null)
    {
        if (string.IsNullOrWhiteSpace(logRoot))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(logRoot);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException
                                              or NotSupportedException)
        {
            // Logging is optional: an unusable path disables it and the app still starts.
            return;
        }

        lock (Gate)
        {
            _root = logRoot;
            _currentPath = Path.Combine(logRoot, $"camzivo-{DateTime.Now:yyyyMMdd}.log");
            _redactor = redactor ?? (static text => text);
            _currentBytes = File.Exists(_currentPath) ? new FileInfo(_currentPath).Length : 0;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message} :: {exception.GetType().Name}: {exception.Message}");

    public static void Write(string level, string message)
    {
        string line;
        try
        {
            line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {_redactor(message ?? string.Empty)}");
        }
        catch (Exception)
        {
            return;
        }

        Recent.Enqueue(line);
        while (Recent.Count > 500 && Recent.TryDequeue(out _))
        {
        }

        lock (Gate)
        {
            if (_root is null || _currentPath is null)
            {
                return;
            }

            try
            {
                var payload = line + Environment.NewLine;
                var bytes = Encoding.UTF8.GetByteCount(payload);

                if (_currentBytes + bytes > MaxBytesPerFile)
                {
                    Rotate();
                }

                File.AppendAllText(_currentPath, payload, Encoding.UTF8);
                _currentBytes += bytes;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A full or locked log must never interrupt the operator.
            }
        }
    }

    /// <summary>Newest lines first, from the in-memory ring (no file read, safe to call from the UI thread).</summary>
    public static IReadOnlyList<string> Tail(int lines = 100) =>
        Recent.Reverse().Take(Math.Clamp(lines, 1, 500)).ToList();

    public static void WriteCrashMarker(string detail)
    {
        lock (Gate)
        {
            if (_root is null)
            {
                return;
            }

            try
            {
                File.WriteAllText(Path.Combine(_root, CrashMarkerName), _redactor(detail ?? string.Empty), Encoding.UTF8);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Reads and deletes the marker, so the next start reports a crash exactly once.</summary>
    public static string? ReadAndClearCrashMarker()
    {
        lock (Gate)
        {
            if (_root is null)
            {
                return null;
            }

            var path = Path.Combine(_root, CrashMarkerName);
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                var text = File.ReadAllText(path, Encoding.UTF8);
                File.Delete(path);
                return text;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private static void Rotate()
    {
        if (_root is null || _currentPath is null)
        {
            return;
        }

        try
        {
            var oldest = Path.Combine(_root, $"camzivo-{DateTime.Now:yyyyMMdd}.{MaxFiles - 1}.log");
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (var index = MaxFiles - 2; index >= 1; index--)
            {
                var from = Path.Combine(_root, $"camzivo-{DateTime.Now:yyyyMMdd}.{index}.log");
                var to = Path.Combine(_root, $"camzivo-{DateTime.Now:yyyyMMdd}.{index + 1}.log");
                if (File.Exists(from))
                {
                    File.Move(from, to, overwrite: true);
                }
            }

            var baseName = Path.Combine(_root, $"camzivo-{DateTime.Now:yyyyMMdd}.log");
            if (File.Exists(baseName))
            {
                File.Move(baseName, Path.Combine(_root, $"camzivo-{DateTime.Now:yyyyMMdd}.1.log"), overwrite: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        _currentBytes = 0;
    }
}
