using System.Globalization;
using System.Text;

namespace NekoVup.Core.Pipeline;

public enum LogLevel
{
    /// <summary>외부 도구가 내놓은 출력 줄 등 자세한 기록.</summary>
    Detail,
    Info,
    Warning,
    Error,
}

public interface IJobLog
{
    void Write(LogLevel level, string message);
}

public static class JobLogExtensions
{
    public static void Detail(this IJobLog log, string message) => log.Write(LogLevel.Detail, message);
    public static void Info(this IJobLog log, string message) => log.Write(LogLevel.Info, message);
    public static void Warning(this IJobLog log, string message) => log.Write(LogLevel.Warning, message);
    public static void Error(this IJobLog log, string message) => log.Write(LogLevel.Error, message);
}

public sealed class NullJobLog : IJobLog
{
    public static NullJobLog Instance { get; } = new();
    public void Write(LogLevel level, string message) { }
}

/// <summary>콜백으로 넘기는 로그 (화면 표시용).</summary>
public sealed class DelegateJobLog(Action<LogLevel, string> sink) : IJobLog
{
    public void Write(LogLevel level, string message) => sink(level, message);
}

/// <summary>여러 곳에 동시에 쓰는 로그.</summary>
public sealed class CompositeJobLog(params IJobLog[] targets) : IJobLog
{
    public void Write(LogLevel level, string message)
    {
        foreach (var target in targets) target.Write(level, message);
    }
}

/// <summary>파일에 덧붙여 쓰는 로그. 여러 스레드에서 불러도 된다.</summary>
public sealed class FileJobLog : IJobLog, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();

    public FileJobLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void Write(LogLevel level, string message)
    {
        string prefix = level switch
        {
            LogLevel.Detail => "    ",
            LogLevel.Info => "INFO",
            LogLevel.Warning => "WARN",
            _ => "FAIL",
        };
        lock (_gate)
        {
            _writer.Write(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
            _writer.Write(' ');
            _writer.Write(prefix);
            _writer.Write(' ');
            _writer.WriteLine(message);
        }
    }

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }
}
