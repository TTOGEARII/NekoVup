using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NekoVup.Core.Media;

/// <summary><c>ffprobe -print_format json -show_format -show_streams</c> 출력을 <see cref="MediaInfo"/>로 바꾼다.</summary>
public static class FfprobeParser
{
    /// <summary>ffprobe에 넘길 인자 (입력 경로 제외).</summary>
    public static IReadOnlyList<string> Arguments { get; } =
        ["-v", "error", "-print_format", "json", "-show_format", "-show_streams"];

    public static MediaInfo Parse(string json, string path)
    {
        FfprobeRoot root;
        try
        {
            root = JsonSerializer.Deserialize(json, FfprobeJsonContext.Default.FfprobeRoot)
                   ?? throw new InvalidDataException("ffprobe 출력이 비어 있습니다.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("ffprobe 출력을 해석할 수 없습니다: " + ex.Message, ex);
        }

        var streams = root.Streams ?? [];
        // 앨범 표지 같은 첨부 그림은 영상 스트림으로 치지 않는다.
        var video = streams.FirstOrDefault(s => s.CodecType == "video" && !IsAttachedPicture(s) && s.Width > 0 && s.Height > 0)
                    ?? throw new InvalidDataException("영상 스트림이 없는 파일입니다.");

        int rotation = ReadRotation(video);
        bool swap = rotation is 90 or 270;

        return new MediaInfo
        {
            Path = path,
            VideoStreamIndex = video.Index,
            Width = swap ? video.Height : video.Width,
            Height = swap ? video.Width : video.Height,
            CodedWidth = video.Width,
            CodedHeight = video.Height,
            Rotation = rotation,
            SampleAspectRatio = ReadSampleAspect(video.SampleAspectRatio, swap),
            Fps = ChooseFps(video.AvgFrameRate, video.RFrameRate),
            DurationSeconds = ReadDuration(video, root.Format),
            VideoCodec = video.CodecName,
            PixelFormat = video.PixFmt,
            ColorSpace = Known(video.ColorSpace),
            ColorRange = Known(video.ColorRange),
            ColorPrimaries = Known(video.ColorPrimaries),
            ColorTransfer = Known(video.ColorTransfer),
            AudioCodecs = [.. streams.Where(s => s.CodecType == "audio").Select(s => s.CodecName ?? "unknown")],
            SubtitleCodecs = [.. streams.Where(s => s.CodecType == "subtitle").Select(s => s.CodecName ?? "unknown")],
        };
    }

    /// <summary>
    /// 기본은 avg_frame_rate. 다만 r_frame_rate와 1% 이내로 같으면 r_frame_rate를 쓴다
    /// (avg는 "359640/12001"처럼 지저분하게 나오는 일이 잦아서, 사실상 같은 값이면 깔끔한 쪽을 고른다).
    /// </summary>
    public static Rational ChooseFps(string? avgFrameRate, string? rFrameRate)
    {
        bool hasAvg = Rational.TryParse(avgFrameRate, out var avg) && avg.IsValid;
        bool hasR = Rational.TryParse(rFrameRate, out var r) && r.IsValid;

        if (hasAvg && hasR)
            return Math.Abs(avg.Value - r.Value) <= r.Value * 0.01 ? r : avg;
        if (hasAvg) return avg;
        if (hasR) return r;
        throw new InvalidDataException("프레임레이트를 알 수 없는 영상입니다.");
    }

    /// <summary>회전 메타데이터를 0/90/180/270으로 정규화한다. 90도 단위가 아니면 0으로 본다.</summary>
    public static int NormalizeRotation(double degrees)
    {
        int rounded = (int)Math.Round(degrees);
        int normalized = ((rounded % 360) + 360) % 360;
        return normalized is 90 or 180 or 270 ? normalized : 0;
    }

    private static Rational ReadSampleAspect(string? text, bool swap)
    {
        if (!Rational.TryParse(text, out var sar) || !sar.IsValid) return new Rational(1, 1);
        return swap ? new Rational(sar.Den, sar.Num) : sar;
    }

    private static int ReadRotation(FfprobeStream video)
    {
        if (video.SideDataList is { } sideData)
        {
            foreach (var item in sideData)
                if (item.Rotation is { } rotation)
                    return NormalizeRotation(rotation);
        }

        // 옛 ffmpeg는 tags.rotate에 적었다.
        if (video.Tags is { } tags && tags.TryGetValue("rotate", out var text) &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double legacy))
            return NormalizeRotation(legacy);

        return 0;
    }

    private static double ReadDuration(FfprobeStream video, FfprobeFormat? format)
    {
        if (TryPositive(format?.Duration, out double d)) return d;
        if (TryPositive(video.Duration, out d)) return d;
        return 0;
    }

    private static bool TryPositive(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0 && double.IsFinite(value);

    private static bool IsAttachedPicture(FfprobeStream s) =>
        s.Disposition is { } d && d.TryGetValue("attached_pic", out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() != 0;

    private static string? Known(string? value) =>
        string.IsNullOrEmpty(value) || value is "unknown" or "unspecified" or "reserved" ? null : value;
}

internal sealed class FfprobeRoot
{
    public List<FfprobeStream>? Streams { get; set; }
    public FfprobeFormat? Format { get; set; }
}

internal sealed class FfprobeFormat
{
    public string? Duration { get; set; }
    public string? FormatName { get; set; }
}

internal sealed class FfprobeStream
{
    public int Index { get; set; }
    public string? CodecType { get; set; }
    public string? CodecName { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? RFrameRate { get; set; }
    public string? AvgFrameRate { get; set; }
    public string? Duration { get; set; }
    public string? PixFmt { get; set; }
    public string? SampleAspectRatio { get; set; }
    public string? ColorSpace { get; set; }
    public string? ColorRange { get; set; }
    public string? ColorPrimaries { get; set; }
    public string? ColorTransfer { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
    public Dictionary<string, JsonElement>? Disposition { get; set; }
    public List<FfprobeSideData>? SideDataList { get; set; }
}

internal sealed class FfprobeSideData
{
    public string? SideDataType { get; set; }
    public double? Rotation { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(FfprobeRoot))]
internal sealed partial class FfprobeJsonContext : JsonSerializerContext;
