using System.Diagnostics;
using System.Globalization;
using NekoVup.Core.Processes;
using NekoVup.Core.Regions;
using NekoVup.Core.Settings;

namespace NekoVup.Tests;

/// <summary>실제 ffmpeg·python이 필요한 테스트의 공통 도구. 도구가 없으면 테스트를 건너뛴다.</summary>
internal static class TestTools
{
    public static readonly ToolRunner Runner = new();

    private static readonly Lazy<bool> Ffmpeg = new(() => Works("ffmpeg", "-version") && Works("ffprobe", "-version"));
    private static readonly Lazy<bool> Python = new(() => Works("python", "-c", "import numpy, PIL"));

    public static bool HasFfmpeg => Ffmpeg.Value;
    public static bool HasPython => Python.Value;

    /// <summary>테스트 출력 폴더에 복사된 가짜 ProPainter 폴더.</summary>
    public static string MockProPainterDir => Path.Combine(AppContext.BaseDirectory, "mock");

    /// <summary>가짜 Video2X 스크립트. <see cref="RecordingRunner"/>가 python으로 실행해 준다.</summary>
    public static string MockVideo2X => Path.Combine(MockProPainterDir, "video2x_mock.py");

    public static AppSettings MockSettings() => new() { Python = "python", ProPainterDir = MockProPainterDir, Video2X = MockVideo2X };

    public static void RequireFfmpeg() => Assert.SkipUnless(HasFfmpeg, "ffmpeg/ffprobe가 PATH에 없습니다.");

    public static void RequireFfmpegAndPython()
    {
        RequireFfmpeg();
        Assert.SkipUnless(HasPython, "numpy·Pillow가 설치된 python이 PATH에 없습니다.");
    }

    private static bool Works(string file, params string[] args)
    {
        try
        {
            var result = Runner.RunAsync(new ToolInvocation(file, args), null, null, CancellationToken.None).GetAwaiter().GetResult();
            return result.ExitCode == 0;
        }
        catch (ToolFailedException)
        {
            return false;
        }
    }

    /// <summary>한글과 공백이 들어간 임시 폴더 (경로 처리 검증을 겸한다).</summary>
    public static string NewTempDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nekoVup 테스트 {label} {Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void DeleteDir(string dir)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                return;
            }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }

    public static async Task<string> Ffmpeg_(CancellationToken ct, params string[] args)
    {
        var result = await Runner.RunAsync(new ToolInvocation("ffmpeg", ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", .. args]), null, null, ct);
        Assert.True(result.ExitCode == 0, "ffmpeg 실패: " + result.ErrorTail);
        return result.ErrorTail;
    }

    public static async Task<string> Ffprobe(CancellationToken ct, params string[] args) =>
        (await Runner.RunCaptureAsync(new ToolInvocation("ffprobe", ["-v", "error", .. args]), ct)).Trim();

    /// <summary>
    /// 테스트 영상: 1280x720, 30000/1001 fps, 6초, 사인파 오디오, 오른쪽 위에 반투명 흰 상자(워터마크 역할).
    /// </summary>
    public static readonly PixelRect Watermark = new(1040, 40, 190, 56);

    public static async Task MakeSourceAsync(string path, CancellationToken ct, bool tagged = true, int seconds = 6, string audioCodec = "aac", PixelRect? secondBox = null, bool scroll = false)
    {
        // scroll: 화면 전체를 프레임마다 가로 1%(약 13px)씩 흘려보낸다 → 어느 자리든 프레임마다 내용이 달라진다
        string filter = (scroll ? "scroll=horizontal=0.01," : "") + "drawbox=x=1040:y=40:w=190:h=56:color=white@0.55:t=fill";
        if (secondBox is { } b) filter += string.Create(CultureInfo.InvariantCulture, $",drawbox=x={b.X}:y={b.Y}:w={b.Width}:h={b.Height}:color=white@0.55:t=fill");
        filter += ",format=yuv420p";

        var args = new List<string>
        {
            "-f", "lavfi", "-i", $"testsrc2=size=1280x720:rate=30000/1001:duration={seconds}",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}",
            "-vf", filter, "-c:v", "libx264", "-crf", "16",
        };
        if (tagged) args.AddRange(["-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709"]);
        args.AddRange(["-c:a", audioCodec, "-shortest", path]);
        await Ffmpeg_(ct, [.. args]);
    }

    public static async Task<int> CountFramesAsync(string path, CancellationToken ct) =>
        int.Parse(await Ffprobe(ct, "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", path), CultureInfo.InvariantCulture);

    public static async Task<string> StreamEntryAsync(string path, string stream, string entry, CancellationToken ct) =>
        await Ffprobe(ct, "-select_streams", stream, "-show_entries", "stream=" + entry, "-of", "csv=p=0", path);

    /// <summary>스트림의 첫 패킷 시각(초).</summary>
    public static async Task<double> FirstTimestampAsync(string path, string stream, CancellationToken ct)
    {
        string text = await Ffprobe(ct, "-select_streams", stream, "-show_entries", "packet=pts_time", "-of", "csv=p=0", path);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => double.Parse(line.TrimEnd(','), CultureInfo.InvariantCulture)).Min();
    }

    /// <summary>
    /// 파일 안에서 오디오와 영상이 가장 멀리 떨어져 쓰인 간격(초). 패킷을 파일에 놓인 순서대로 읽으며
    /// "지금까지 나온 마지막 영상 시각"과 "마지막 오디오 시각"의 차이가 가장 컸던 값을 돌려준다. 잘 섞인 파일은 1초를 넘지 않는다.
    /// </summary>
    public static async Task<double> MaxInterleaveGapAsync(string path, CancellationToken ct)
    {
        string text = await Ffprobe(ct, "-show_entries", "packet=codec_type,pts_time", "-of", "csv=p=0", path);
        double video = double.NaN, audio = double.NaN, worst = 0;
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split(',');
            if (parts.Length < 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double time)) continue;
            if (parts[0] == "video") video = time;
            else if (parts[0] == "audio") audio = time;
            if (!double.IsNaN(video) && !double.IsNaN(audio)) worst = Math.Max(worst, Math.Abs(video - audio));
        }
        return worst;
    }

    public static async Task<double> DurationAsync(string path, CancellationToken ct) =>
        double.Parse(await Ffprobe(ct, "-show_entries", "format=duration", "-of", "csv=p=0", path), CultureInfo.InvariantCulture);

    /// <summary>영상 전체를 밝기(Y) 평면 원시 파일로 푼다. 프레임당 width×height 바이트.</summary>
    public static async Task<string> DecodeLumaAsync(string video, string rawPath, CancellationToken ct, double? seconds = null)
    {
        var args = new List<string> { "-i", video };
        if (seconds is { } s) args.AddRange(["-t", s.ToString(CultureInfo.InvariantCulture)]);
        // passthrough: 타임스탬프 반올림 때문에 프레임이 복제·삭제되지 않게 디코딩된 그대로 쓴다
        args.AddRange(["-map", "0:v:0", "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "gray", rawPath]);
        await Ffmpeg_(ct, [.. args]);
        return rawPath;
    }

    /// <summary>n번째 프레임 한 장을 RGB로 읽는다.</summary>
    public static async Task<byte[]> DecodeRgbFrameAsync(string video, int frame, string dir, CancellationToken ct)
    {
        string raw = Path.Combine(dir, $"frame-{Guid.NewGuid():N}.rgb");
        await Ffmpeg_(ct, "-i", video, "-vf", $"select=eq(n\\,{frame})", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", raw);
        var bytes = await File.ReadAllBytesAsync(raw, ct);
        File.Delete(raw);
        return bytes;
    }

    /// <summary>
    /// 두 밝기 원시 파일의 PSNR(dB). <paramref name="include"/>가 true를 돌려주는 픽셀만 센다.
    /// </summary>
    public static (double Psnr, int Frames) Psnr(string rawA, string rawB, int width, int height, Func<int, int, bool> include)
    {
        int frameBytes = width * height;
        var mask = new bool[frameBytes];
        long counted = 0;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            if (mask[y * width + x] = include(x, y)) counted++;

        using var a = File.OpenRead(rawA);
        using var b = File.OpenRead(rawB);
        Assert.Equal(a.Length, b.Length);
        var bufferA = new byte[frameBytes];
        var bufferB = new byte[frameBytes];
        double squared = 0;
        int frames = 0;
        while (a.ReadAtLeast(bufferA, frameBytes, throwOnEndOfStream: false) == frameBytes)
        {
            b.ReadExactly(bufferB);
            long sum = 0;
            for (int i = 0; i < frameBytes; i++)
            {
                if (!mask[i]) continue;
                int d = bufferA[i] - bufferB[i];
                sum += d * d;
            }
            squared += sum;
            frames++;
        }
        double mse = squared / ((double)counted * frames);
        return (mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse), frames);
    }

    /// <summary>RGB 프레임 두 장의 사각형 안 평균 절대 오차.</summary>
    public static double MeanAbsDiff(byte[] rgbA, byte[] rgbB, int width, PixelRect rect)
    {
        long sum = 0;
        for (int y = rect.Y; y < rect.Bottom; y++)
        for (int x = rect.X; x < rect.Right; x++)
        for (int c = 0; c < 3; c++)
            sum += Math.Abs(rgbA[(y * width + x) * 3 + c] - rgbB[(y * width + x) * 3 + c]);
        return (double)sum / (rect.Area * 3);
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
