using System.Globalization;
using NekoVup.Core.Pipeline;

namespace NekoVup.Core.Media;

/// <summary>업스케일 한 번을 Video2X로 어떻게 돌릴지.</summary>
/// <param name="Processor">-p 값.</param>
/// <param name="ModelOption">모델 이름을 넘기는 옵션 (--realesrgan-model 등).</param>
/// <param name="Model">모델 이름.</param>
/// <param name="ModelScale">Video2X에 넘길 배율 (-s).</param>
/// <param name="FinalScale">사용자가 원한 최종 배율. <see cref="ModelScale"/>보다 작으면 업스케일 뒤에 줄인다.</param>
public sealed record UpscalePlan(string Processor, string ModelOption, string Model, int ModelScale, int FinalScale)
{
    /// <summary>모델 배율과 원하는 배율이 달라 업스케일 뒤에 ffmpeg로 줄여야 하는지.</summary>
    public bool NeedsDownscale => FinalScale != ModelScale;
}

/// <summary>Video2X 6.4.0 명령줄 조립과 출력 해석. 옵션은 실제 실행 파일의 --help와 출력으로 확인한 것이다.</summary>
public static class Video2X
{
    /// <summary>정상적으로 끝났을 때 마지막에 찍는 문구.</summary>
    public const string DoneMarker = "Output written to";

    public static UpscalePlan Plan(UpscalePreset preset, int scale)
    {
        if (scale is < 2 or > 4) throw new ArgumentException($"업스케일 배율은 2, 3, 4 중 하나여야 합니다: {scale}");
        return preset switch
        {
            UpscalePreset.Anime => new UpscalePlan("realesrgan", "--realesrgan-model", "realesr-animevideov3", scale, scale),
            UpscalePreset.AnimeHq => new UpscalePlan("realcugan", "--realcugan-model", "models-se", scale, scale),
            // realesrgan-plus는 4배 모델만 있다.
            UpscalePreset.Real => new UpscalePlan("realesrgan", "--realesrgan-model", "realesrgan-plus", 4, scale),
            _ => throw new ArgumentException("업스케일 방식을 고르세요."),
        };
    }

    /// <summary>
    /// 영상만 처리한다(<c>--no-copy-streams</c>). 오디오·자막은 마지막 단계에서 ffmpeg로 붙인다.
    /// GPU 번호 옵션은 <c>-d</c>다 (문서 일부에 적힌 -g가 아니다).
    /// </summary>
    public static List<string> Arguments(UpscalePlan plan, string inputPath, string outputPath, int gpuIndex, EncodeOptions encode) =>
    [
        "-i", inputPath, "-o", outputPath,
        "-p", plan.Processor, plan.ModelOption, plan.Model,
        "-s", plan.ModelScale.ToString(CultureInfo.InvariantCulture),
        "-d", gpuIndex.ToString(CultureInfo.InvariantCulture),
        "-c", "libx264", "--pix-fmt", "yuv420p",
        "-e", "crf=" + encode.Crf.ToString(CultureInfo.InvariantCulture),
        "-e", "preset=" + encode.Preset,
        "--no-copy-streams",
    ];

    /// <summary>
    /// 진행 줄 <c>frame=37/184 (20.11%); fps=14.00; elapsed=00:00:05; remaining=00:00:10</c>에서 처리한 프레임 수를 읽는다.
    /// (전체 수는 Video2X가 길이로 어림한 값이라 틀릴 수 있어 쓰지 않는다.)
    /// </summary>
    public static bool TryParseProgress(string? line, out long frame)
    {
        frame = 0;
        if (line is null) return false;
        // 실제 출력은 줄 앞에 화면 지움 코드(ESC[K)가 붙어 온다.
        var text = line.AsSpan();
        int start = text.IndexOf("frame=", StringComparison.Ordinal);
        if (start < 0 || text[..start].ContainsAnyExcept("\u001b[K \t")) return false;
        text = text[(start + "frame=".Length)..];
        int slash = text.IndexOf('/');
        return slash > 0 && long.TryParse(text[..slash], NumberStyles.None, CultureInfo.InvariantCulture, out frame);
    }

    /// <summary>로그에 남기기 전에 터미널 제어 코드(ESC[…)를 걷어 낸다.</summary>
    public static string StripAnsi(string line)
    {
        if (!line.Contains('\u001b')) return line;
        var result = new System.Text.StringBuilder(line.Length);
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '\u001b' && i + 1 < line.Length && line[i + 1] == '[')
            {
                i += 2;
                while (i < line.Length && !char.IsAsciiLetter(line[i])) i++;
                continue;
            }
            result.Append(line[i]);
        }
        return result.ToString();
    }

    /// <summary><c>video2x -l</c> 출력에서 GPU 이름 목록을 뽑는다 ("0. NVIDIA GeForce …").</summary>
    public static IReadOnlyList<string> ParseDevices(string output)
    {
        var devices = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            int dot = line.IndexOf(". ", StringComparison.Ordinal);
            if (dot > 0 && int.TryParse(line.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                devices.Add(line);
        }
        return devices;
    }
}
