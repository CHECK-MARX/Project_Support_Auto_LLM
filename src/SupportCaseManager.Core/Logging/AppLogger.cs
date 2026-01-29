using System;
using System.IO;
using System.Text;
using SupportCaseManager.Core.Compatibility;

namespace SupportCaseManager.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public interface IAppLogger
{
    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? exception = null);
}

public sealed class NullLogger : IAppLogger
{
    public static NullLogger Instance { get; } = new();

    private NullLogger()
    {
    }

    public void Debug(string message) { }

    public void Info(string message) { }

    public void Warning(string message) { }

    public void Error(string message, Exception? exception = null) { }
}

public sealed class FileLogger : IAppLogger
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _category;
    private readonly bool _alsoConsole;
    private readonly LogLevel _minLevel;

    public FileLogger(string logPath, string category = "support_case_manager", bool alsoConsole = true, LogLevel minLevel = LogLevel.Info)
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            throw new ArgumentException("Log path is required.", nameof(logPath));
        }

        _path = logPath;
        _category = category;
        _alsoConsole = alsoConsole;
        _minLevel = minLevel;

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public void Debug(string message) => Write(LogLevel.Debug, message, null);

    public void Info(string message) => Write(LogLevel.Info, message, null);

    public void Warning(string message) => Write(LogLevel.Warning, message, null);

    public void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);

    private void Write(LogLevel level, string message, Exception? exception)
    {
        if (level < _minLevel)
        {
            return;
        }

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var line = $"{timestamp} [{level.ToString().ToUpperInvariant()}] {_category} :: {message}";
        if (exception != null)
        {
            line = $"{line} | {exception}";
        }

        lock (_gate)
        {
            File.AppendAllText(_path, line + Environment.NewLine, EncodingPolicy.Utf8NoBom);
        }

        if (_alsoConsole)
        {
            Console.WriteLine(line);
        }
    }
}
