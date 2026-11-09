using System.Globalization;
using NekoVup.Core.Pipeline;
using NekoVup.Core.Processes;

namespace NekoVup.Core.Settings;

public enum CheckStatus
{
    Ok,
    Warning,
    Error,
}

public sealed record CheckItem(string Name, CheckStatus Status, string Detail);

/// <summary>"환경 점검": 외부 도구가 설치되어 있고 실행되는지 확인한다.</summary>
public sealed class EnvironmentChecker(AppSettings settings, IToolRunner runner)
{
    /// <summary>ProPainter가 첫 실행 때 내려받는 가중치 파일.</summary>
    public static IReadOnlyList<string> ProPainterWeights { get; } =
        ["ProPainter.pth", "recurrent_flow_completion.pth", "raft-things.pth"];

    public async Task<IReadOnlyList<CheckItem>> RunAsync(string? workDirectory, CancellationToken cancellationToken)
    {
        var items = new List<CheckItem>
        {
            await VersionAsync("ffmpeg", settings.Ffmpeg, cancellationToken).ConfigureAwait(false),
            await VersionAsync("ffprobe", settings.Ffprobe, cancellationToken).ConfigureAwait(false),
            await PythonAsync(cancellationToken).ConfigureAwait(false),
            ProPainterFolder(),
            await Video2XAsync(cancellationToken).ConfigureAwait(false),
        };
        if (DiskSpace(workDirectory) is { } disk) items.Add(disk);
        return items;
    }

    private async Task<CheckItem> VersionAsync(string name, string executable, CancellationToken cancellationToken)
    {
        try
        {
            string output = await runner.RunCaptureAsync(new ToolInvocation(executable, ["-version"]), cancellationToken).ConfigureAwait(false);
            string first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
            return new CheckItem(name, CheckStatus.Ok, first);
        }
        catch (ToolFailedException ex)
        {
            return new CheckItem(name, CheckStatus.Error, FirstLine(ex.Message));
        }
    }

    private async Task<CheckItem> PythonAsync(CancellationToken cancellationToken)
    {
        const string name = "Python + PyTorch";
        if (string.IsNullOrWhiteSpace(settings.Python))
            return new CheckItem(name, CheckStatus.Warning, "파이썬 경로가 설정되지 않았습니다 (ProPainter 모드에 필요).");
        try
        {
            var invocation = new ToolInvocation(settings.Python,
                ["-c", "import torch;print(torch.__version__, torch.cuda.is_available(), torch.cuda.get_device_name(0) if torch.cuda.is_available() else '-')"])
            {
                Environment = new Dictionary<string, string> { ["PYTHONUTF8"] = "1", ["PYTHONIOENCODING"] = "utf-8" },
            };
            string output = (await runner.RunCaptureAsync(invocation, cancellationToken).ConfigureAwait(false)).Trim();
            bool cuda = output.Contains(" True ", StringComparison.Ordinal);
            return new CheckItem(name, cuda ? CheckStatus.Ok : CheckStatus.Warning,
                cuda ? "torch " + output : $"torch {output} — CUDA를 쓸 수 없습니다. GPU 없이 돌리면 매우 느립니다.");
        }
        catch (ToolFailedException ex)
        {
            return new CheckItem(name, CheckStatus.Error, "torch를 불러오지 못했습니다: " + LastLine(ex.Message));
        }
    }

    private CheckItem ProPainterFolder()
    {
        const string name = "ProPainter";
        if (string.IsNullOrWhiteSpace(settings.ProPainterDir))
            return new CheckItem(name, CheckStatus.Warning, "ProPainter 폴더가 설정되지 않았습니다.");
        if (!File.Exists(Path.Combine(settings.ProPainterDir, JobRunner.ProPainterScript)))
            return new CheckItem(name, CheckStatus.Error, $"{JobRunner.ProPainterScript}가 없습니다: {settings.ProPainterDir}");

        string weights = Path.Combine(settings.ProPainterDir, "weights");
        var missing = ProPainterWeights.Where(w => !File.Exists(Path.Combine(weights, w))).ToList();
        return missing.Count == 0
            ? new CheckItem(name, CheckStatus.Ok, settings.ProPainterDir)
            : new CheckItem(name, CheckStatus.Warning, $"가중치 {string.Join(", ", missing)} 없음 — 첫 실행 때 자동으로 내려받습니다(인터넷 필요).");
    }

    private async Task<CheckItem> Video2XAsync(CancellationToken cancellationToken)
    {
        const string name = "Video2X";
        if (string.IsNullOrWhiteSpace(settings.Video2X))
            return new CheckItem(name, CheckStatus.Warning, "경로가 설정되지 않았습니다 (업스케일에 필요).");
        if (!File.Exists(settings.Video2X))
            return new CheckItem(name, CheckStatus.Error, $"파일이 없습니다: {settings.Video2X}");
        try
        {
            // video2x -l : 쓸 수 있는 GPU(Vulkan 장치) 목록
            var output = new System.Text.StringBuilder();
            var result = await runner.RunAsync(new ToolInvocation(settings.Video2X, ["-l"]),
                line => { lock (output) output.AppendLine(line); }, null, cancellationToken).ConfigureAwait(false);
            var devices = Media.Video2X.ParseDevices(output.ToString());
            if (devices.Count > 0) return new CheckItem(name, CheckStatus.Ok, "GPU " + string.Join(" / ", devices));
            return new CheckItem(name, CheckStatus.Warning,
                result.ExitCode == 0 ? "쓸 수 있는 GPU(Vulkan)를 찾지 못했습니다." : $"GPU 목록을 읽지 못했습니다 (종료 코드 {result.ExitCode}).");
        }
        catch (ToolFailedException ex)
        {
            return new CheckItem(name, CheckStatus.Error, FirstLine(ex.Message));
        }
    }

    private static CheckItem? DiskSpace(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!);
            double gb = drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
            return new CheckItem("디스크 여유 공간", gb < 10 ? CheckStatus.Warning : CheckStatus.Ok,
                string.Create(CultureInfo.InvariantCulture, $"{drive.Name} {gb:0.0} GB"));
        }
        catch (ArgumentException)
        {
            return new CheckItem("디스크 여유 공간", CheckStatus.Warning, "확인할 수 없는 경로입니다.");
        }
        catch (IOException ex)
        {
            return new CheckItem("디스크 여유 공간", CheckStatus.Warning, ex.Message);
        }
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? text;
    private static string LastLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? text;
}
