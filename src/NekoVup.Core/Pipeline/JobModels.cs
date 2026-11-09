using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NekoVup.Core.Regions;
using NekoVup.Core.Settings;

namespace NekoVup.Core.Pipeline;

public enum CleanMode
{
    /// <summary>ProPainter로 복원 (느리지만 품질이 좋다).</summary>
    ProPainter,

    /// <summary>ffmpeg delogo 필터 (빠르지만 뭉개진 자국이 남는다).</summary>
    Delogo,

    /// <summary>워터마크 제거 없이 업스케일만.</summary>
    None,
}

public enum UpscalePreset
{
    None,

    /// <summary>애니메이션용, 빠름 (RealESRGAN realesr-animevideov3).</summary>
    Anime,

    /// <summary>애니메이션용, 고품질 (RealCUGAN models-se).</summary>
    AnimeHq,

    /// <summary>실사용 (RealESRGAN realesrgan-plus, 4배 모델이라 2·3배는 4배 후 줄인다).</summary>
    Real,
}

public enum JobStage
{
    Probe,
    Extract,
    Inpaint,
    Merge,
    Composite,
    Delogo,

    /// <summary>업스케일만 할 때: 업스케일러에 넣을 중간 영상 만들기.</summary>
    Prepare,
    Upscale,

    /// <summary>업스케일된 영상에 오디오·자막을 붙여 최종 파일로.</summary>
    Finalize,
    Finished,
}

public sealed record JobOptions
{
    public CleanMode Mode { get; init; } = CleanMode.ProPainter;

    /// <summary>처리 시작 지점(초). 없으면 처음부터.</summary>
    public double? StartSeconds { get; init; }

    /// <summary>처리할 길이(초). "앞 N초만 테스트"에 쓴다. 없으면 끝까지.</summary>
    public double? DurationSeconds { get; init; }

    public RegionOptions Region { get; init; } = new();

    /// <summary>ProPainter에 한 번에 넘기는 프레임 수. 길수록 VRAM을 많이 쓴다.</summary>
    public int ChunkLength { get; init; } = 100;

    /// <summary>이웃 조각이 겹치는 프레임 수.</summary>
    public int Overlap { get; init; } = 10;

    public int SubvideoLength { get; init; } = 40;
    public int MaskDilation { get; init; } = 4;
    public bool Fp16 { get; init; } = true;

    /// <summary>최종 파일 화질 (낮을수록 고화질·대용량).</summary>
    public int Crf { get; init; } = 18;

    public string Preset { get; init; } = "veryfast";

    public UpscalePreset Upscale { get; init; } = UpscalePreset.None;

    /// <summary>업스케일 배율 (2, 3, 4).</summary>
    public int Scale { get; init; } = 2;

    /// <summary>Video2X가 쓸 GPU 번호 (video2x -l 로 확인).</summary>
    public int GpuIndex { get; init; }
}

public sealed record JobRequest
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }

    /// <summary>워터마크 박스 (화면에 보이는 영상 기준 픽셀 좌표).</summary>
    public required IReadOnlyList<PixelRect> Boxes { get; init; }

    public JobOptions Options { get; init; } = new();

    /// <summary>중간 파일 폴더. 없으면 출력 폴더 아래 .nekovup에 만든다.</summary>
    public string? WorkDirectory { get; init; }

    /// <summary>끝난 뒤에도 중간 파일을 남긴다.</summary>
    public bool KeepWorkDirectory { get; init; }

    public bool SkipDiskSpaceCheck { get; init; }
}

public readonly record struct JobProgress(JobStage Stage, double StageFraction, double OverallFraction, string? Detail = null);

public sealed record JobResult(string OutputPath, int FrameCount, TimeSpan Elapsed, string? WorkDirectory);

/// <summary>사용자가 고칠 수 있는 문제(입력 오류, 공간 부족 등). 메시지를 그대로 보여 주면 된다.</summary>
public sealed class JobException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>작업 폴더에 두는 진행 기록. 중단된 작업을 이어서 할 때 읽는다.</summary>
public sealed class JobManifest
{
    public const string FileName = "job.json";

    public int Version { get; set; } = 1;

    /// <summary>입력 파일과 결과에 영향을 주는 옵션으로 만든 지문. 다르면 다른 작업이다.</summary>
    public string JobKey { get; set; } = "";

    public string InputPath { get; set; } = "";
    public string Fps { get; set; } = "";

    /// <summary>프레임 추출이 끝났는지와 그 프레임 수.</summary>
    public bool Extracted { get; set; }

    public int FrameCount { get; set; }

    /// <summary>처리 영역 "x,y,w,h" 목록.</summary>
    public List<string> Regions { get; set; } = [];

    /// <summary>병합까지 끝난 영역 번호.</summary>
    public List<int> MergedRegions { get; set; } = [];

    /// <summary>업스케일러에 넣을 중간 영상(cleaned.mkv)이 완성됐는지와 그 프레임 수.</summary>
    public bool Cleaned { get; set; }

    public int CleanedFrames { get; set; }

    /// <summary>완성된 업스케일 결과(upscaled.mkv)를 만든 설정. 같으면 다시 하지 않는다.</summary>
    public string UpscaledKey { get; set; } = "";

    public DateTime UpdatedUtc { get; set; }

    public static JobManifest? TryLoad(string workDirectory)
    {
        string path = Path.Combine(workDirectory, FileName);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), CoreJsonContext.Default.JobManifest)
                : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    public void Save(string workDirectory)
    {
        UpdatedUtc = DateTime.UtcNow;
        string path = Path.Combine(workDirectory, FileName);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, CoreJsonContext.Default.JobManifest));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>중간 결과가 달라지는 요소만 모아 지문을 만든다 (최종 화질 설정은 포함하지 않는다).</summary>
    public static string ComputeKey(JobRequest request)
    {
        var file = new FileInfo(request.InputPath);
        var o = request.Options;
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{file.FullName.ToUpperInvariant()}|{(file.Exists ? file.Length : 0)}|{(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)}");
        text.Append(CultureInfo.InvariantCulture, $"|{o.Mode}|{o.StartSeconds ?? 0:R}|{o.DurationSeconds ?? 0:R}");
        text.Append(CultureInfo.InvariantCulture, $"|{o.Region.Pad}|{o.Region.MaxArea}|{o.Region.MaskGrow}");
        text.Append(CultureInfo.InvariantCulture, $"|{o.ChunkLength}|{o.Overlap}|{o.SubvideoLength}|{o.MaskDilation}|{o.Fp16}");
        foreach (var box in request.Boxes) text.Append('|').Append(box.ToString());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
