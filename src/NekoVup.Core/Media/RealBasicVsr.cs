using System.Globalization;
using System.Security.Cryptography;
using NekoVup.Core.Settings;

namespace NekoVup.Core.Media;

/// <summary>
/// RealBasicVSR — 옛 비디오용 영상 복원 업스케일(4배). Video2X 대신 ProPainter 파이썬 환경(torch)으로 <c>scripts\realbasicvsr.py</c>를 돌린다.
/// 모델 가중치(148 MB)는 처음 쓸 때 내려받아 <see cref="ModelDirectory"/>에 둔다.
/// </summary>
public static class RealBasicVsr
{
    public const string Processor = "realbasicvsr";
    public const string ModelName = "realbasicvsr_x4";
    public const string WeightsFileName = "realbasicvsr_c64b20_reds.pth";
    public const string OfficialWeightsSha256 = "52F77C2C835AAA3FE675B3959B2F85010A6C6F63F77F7E279394646E55A4E376";

    /// <summary>기대하는 가중치 해시 (테스트가 가짜 파일에 맞춰 바꾼다).</summary>
    public static string WeightsSha256 { get; set; } = OfficialWeightsSha256;

    /// <summary>차례로 시도한다: 우리 릴리스(models) → OpenMMLab 모델 저장소(원본). 둘 다 같은 파일이다 (해시로 확인).</summary>
    public static readonly Uri[] WeightsUrls =
    [
        new("https://github.com/TTOGEARII/NekoVup/releases/download/models/" + WeightsFileName),
        new("https://download.openmmlab.com/mmediting/restorers/real_basicvsr/realbasicvsr_c64b20_1x30x8_lr5e-5_150k_reds_20211104-52f77c2c.pth"),
    ];

    /// <summary>가중치를 두는 폴더 (기본 %AppData%\NekoVup\models — 제거 때 같이 지워진다). 테스트가 바꾼다.</summary>
    public static string ModelDirectory { get; set; } = Path.Combine(SettingsStore.DataDirectory, "models");

    public static string WeightsPath => Path.Combine(ModelDirectory, WeightsFileName);

    /// <summary>프로그램 폴더에 함께 들어 있는 추론 스크립트.</summary>
    public static string ScriptPath => Path.Combine(AppContext.BaseDirectory, "scripts", "realbasicvsr.py");

    /// <summary>
    /// 스크립트에 넘길 인자. 모델은 RGB로 일하므로 스크립트가 영상↔RGB 변환을 한다. 입력에 색 공간·범위 표기가 있으면
    /// (<paramref name="colorSpace"/>, <paramref name="colorRange"/> — ffprobe 이름) 양쪽 변환에 그 값을 쓰게 넘긴다.
    /// 넘기지 않으면 되돌릴 때 ffmpeg 기본값(BT.601)으로 바뀌는데, 마지막 단계는 원본 표기(BT.709 등)를 붙이므로 색이 틀어진다.
    /// </summary>
    public static List<string> Arguments(string ffmpeg, string inputPath, string outputPath, int width, int height, int frames, Rational fps, EncodeOptions encode,
        string? colorSpace = null, string? colorRange = null)
    {
        List<string> args = [ScriptPath, "--ffmpeg", ffmpeg, "--weights", WeightsPath, "--input", inputPath, "--output", outputPath];
        if (FfmpegCommands.SwsMatrix(colorSpace) is { } matrix) args.AddRange(["--matrix", matrix]);
        if (FfmpegCommands.SwsRange(colorRange) is { } range) args.AddRange(["--range", range]);
        args.AddRange([
            "--width", width.ToString(CultureInfo.InvariantCulture), "--height", height.ToString(CultureInfo.InvariantCulture),
            "--frames", frames.ToString(CultureInfo.InvariantCulture), "--fps", fps.ToString(),
            "--crf", encode.Crf.ToString(CultureInfo.InvariantCulture), "--preset", encode.Preset,
        ]);
        return args;
    }

    /// <summary>스크립트의 진행 줄 <c>frame=48/120</c>.</summary>
    public static bool TryParseProgress(string? line, out long frame)
    {
        frame = 0;
        if (line is null || !line.StartsWith("frame=", StringComparison.Ordinal)) return false;
        int slash = line.IndexOf('/');
        return slash > 6 && long.TryParse(line.AsSpan(6, slash - 6), NumberStyles.None, CultureInfo.InvariantCulture, out frame);
    }

    /// <summary>가중치가 없거나 해시가 다르면 내려받는다. 진행은 받은 바이트/전체로 알린다.</summary>
    public static async Task EnsureWeightsAsync(HttpClient http, Action<long, long>? progress, Action<string> log, CancellationToken cancellationToken)
    {
        string path = WeightsPath;
        if (File.Exists(path) && Matches(path, WeightsSha256)) return;
        Directory.CreateDirectory(ModelDirectory);
        Exception? last = null;
        foreach (var url in WeightsUrls)
        {
            string partial = path + ".part";
            try
            {
                log($"모델 내려받기: {url}");
                await Runtime.Download.ToFileAsync(http, url, partial, progress, cancellationToken).ConfigureAwait(false);
                if (!Matches(partial, WeightsSha256))
                {
                    File.Delete(partial);
                    last = new InvalidDataException("내려받은 모델 파일의 해시가 다릅니다: " + url);
                    continue;
                }
                File.Move(partial, path, overwrite: true);
                return;
            }
            catch (HttpRequestException ex) { last = ex; }
            catch (IOException ex) { last = ex; }
            catch (TimeoutException ex) { last = ex; } // 응답 없음 — 다음 주소로 (사용자가 취소한 것은 그대로 빠져나간다)
        }
        throw new Pipeline.JobException("RealBasicVSR 모델을 내려받지 못했습니다: " + (last?.Message ?? "알 수 없는 오류"), last);
    }

    private static bool Matches(string path, string sha256)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }
}
