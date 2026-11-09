using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Windows.Win32;
using Windows.Win32.System.Diagnostics.Debug;

namespace NekoVup.Core.Processes;

/// <summary>외부 도구 실행 한 번. 인자는 따옴표 처리 없이 낱개로 넘긴다(<see cref="ProcessStartInfo.ArgumentList"/>).</summary>
public sealed record ToolInvocation(string FileName, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>로그에 남길 한 줄 표기. 실제 실행에는 쓰지 않는다.</summary>
    public string DisplayText
    {
        get
        {
            var sb = new StringBuilder(Quote(FileName));
            foreach (var arg in Arguments) sb.Append(' ').Append(Quote(arg));
            return sb.ToString();

            static string Quote(string s) => s.Length == 0 || s.AsSpan().IndexOfAny(' ', '"', ';') >= 0 ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;
        }
    }
}

public sealed record ToolResult(int ExitCode, string ErrorTail);

/// <summary>도구가 실행되지 않았거나 0이 아닌 코드로 끝났을 때.</summary>
public sealed class ToolFailedException(string message, int? exitCode = null, Exception? inner = null) : Exception(message, inner)
{
    public int? ExitCode { get; } = exitCode;
}

public interface IToolRunner
{
    /// <summary>
    /// 도구를 실행하고 끝날 때까지 기다린다. 표준 출력·오류는 줄 단위로 콜백에 전달된다(콜백은 스레드풀에서 불린다).
    /// 취소되면 자식 프로세스 트리를 통째로 종료하고 <see cref="OperationCanceledException"/>을 던진다.
    /// </summary>
    Task<ToolResult> RunAsync(ToolInvocation invocation, Action<string>? onOutput, Action<string>? onError, CancellationToken cancellationToken);
}

public sealed class ToolRunner : IToolRunner
{
    private const int ErrorTailLines = 40;

    /// <summary>도구가 끝난 뒤 남은 출력을 기다려 주는 한도. 파이프를 붙잡은 프로세스가 남아 있어도 여기서 손을 뗀다.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    static ToolRunner()
    {
        // 자식 프로세스가 죽었을 때 "프로그램이 중지되었습니다" 창이 떠서 작업이 멈추지 않게 한다 (이 설정은 자식에게 물려진다).
        // Video2X 6.4.0이 작업을 다 끝낸 뒤 종료 과정에서 죽는 일이 있어 필요하다.
        const THREAD_ERROR_MODE quiet = THREAD_ERROR_MODE.SEM_NOGPFAULTERRORBOX | THREAD_ERROR_MODE.SEM_FAILCRITICALERRORS;
        var previous = PInvoke.SetErrorMode(quiet);
        PInvoke.SetErrorMode(previous | quiet);
    }

    public async Task<ToolResult> RunAsync(ToolInvocation invocation, Action<string>? onOutput, Action<string>? onError, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(invocation.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in invocation.Arguments) startInfo.ArgumentList.Add(arg);
        if (invocation.WorkingDirectory is { Length: > 0 } cwd) startInfo.WorkingDirectory = cwd;
        if (invocation.Environment is { } env)
            foreach (var (key, value) in env) startInfo.Environment[key] = value;

        // 이 실행 전용 Job Object: 취소하거나 이 프로그램이 죽으면 도구와 그 자손 프로세스가 모두 정리된다.
        using var job = ChildProcessJob.TryCreate();
        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new ToolFailedException($"'{invocation.FileName}'을(를) 실행할 수 없습니다: {ex.Message}", null, ex);
        }

        job?.Add(process);
        // 입력을 기다리며 멈추는 도구가 없도록 표준 입력은 바로 닫는다.
        try { process.StandardInput.Close(); } catch (IOException) { }

        var tail = new Queue<string>(ErrorTailLines);
        var pumps = Task.WhenAll(PumpAsync(process.StandardOutput, onOutput, null), PumpAsync(process.StandardError, onError, tail));

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            job?.Terminate();
            Kill(process);
            await DrainAsync(pumps).ConfigureAwait(false);
            throw;
        }

        // 도구는 끝났는데 그 자손이 남아 출력 파이프를 붙잡고 있으면 읽기가 끝나지 않는다. 잠깐 기다린 뒤 남은 자손을 정리한다.
        if (!await DrainAsync(pumps).ConfigureAwait(false))
        {
            job?.Terminate();
            await DrainAsync(pumps).ConfigureAwait(false);
        }

        string errorTail;
        lock (tail) errorTail = string.Join(Environment.NewLine, tail);
        return new ToolResult(process.ExitCode, errorTail);
    }

    /// <summary>
    /// 도구를 실행해 표준 출력을 바이트 그대로 받는다 (ffmpeg가 그림을 표준 출력으로 내보낼 때).
    /// 종료 코드가 0이 아니면 표준 오류 끝부분을 담아 예외를 던진다.
    /// </summary>
    public async Task<byte[]> CaptureBytesAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(invocation.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in invocation.Arguments) startInfo.ArgumentList.Add(arg);
        if (invocation.WorkingDirectory is { Length: > 0 } cwd) startInfo.WorkingDirectory = cwd;

        using var job = ChildProcessJob.TryCreate();
        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new ToolFailedException($"'{invocation.FileName}'을(를) 실행할 수 없습니다: {ex.Message}", null, ex);
        }

        job?.Add(process);
        try { process.StandardInput.Close(); } catch (IOException) { }

        var tail = new Queue<string>(ErrorTailLines);
        var errors = PumpAsync(process.StandardError, null, tail);
        using var buffer = new MemoryStream();
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            job?.Terminate();
            Kill(process);
            throw;
        }

        await DrainAsync(errors).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string errorTail;
            lock (tail) errorTail = string.Join(Environment.NewLine, tail);
            throw new ToolFailedException(
                $"'{Path.GetFileName(invocation.FileName)}'이(가) 오류 코드 {process.ExitCode}로 끝났습니다.{Environment.NewLine}{errorTail}", process.ExitCode);
        }
        return buffer.ToArray();
    }

    private static async Task<bool> DrainAsync(Task pumps)
    {
        try
        {
            await pumps.WaitAsync(DrainTimeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task PumpAsync(StreamReader reader, Action<string>? sink, Queue<string>? tail)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (tail is not null && !string.IsNullOrWhiteSpace(line))
                {
                    lock (tail)
                    {
                        if (tail.Count == ErrorTailLines) tail.Dequeue();
                        tail.Enqueue(line);
                    }
                }
                sink?.Invoke(line);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }

        try { process.WaitForExit(5000); }
        catch (InvalidOperationException) { }
    }
}

public static class ToolRunnerExtensions
{
    /// <summary>실행하고, 종료 코드가 0이 아니면 표준 오류 끝부분을 담아 예외를 던진다.</summary>
    public static async Task RunCheckedAsync(this IToolRunner runner, ToolInvocation invocation, Action<string>? onOutput, Action<string>? onError, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(invocation, onOutput, onError, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ToolFailedException(
                $"'{Path.GetFileName(invocation.FileName)}'이(가) 오류 코드 {result.ExitCode}로 끝났습니다.{Environment.NewLine}{result.ErrorTail}",
                result.ExitCode);
    }

    /// <summary>실행해서 표준 출력 전체를 문자열로 돌려준다 (ffprobe처럼 출력이 짧은 도구용).</summary>
    public static async Task<string> RunCaptureAsync(this IToolRunner runner, ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        await runner.RunCheckedAsync(invocation, line => { lock (output) output.AppendLine(line); }, null, cancellationToken).ConfigureAwait(false);
        return output.ToString();
    }
}
