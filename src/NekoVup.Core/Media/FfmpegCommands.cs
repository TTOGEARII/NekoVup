using System.Globalization;
using System.Text;
using NekoVup.Core.Regions;

namespace NekoVup.Core.Media;

/// <summary>영상 인코딩 설정.</summary>
/// <param name="Intermediate">
/// 업스케일러에 넘길 중간 영상: 영상만 담고(오디오·자막은 마지막에 원본에서 붙인다) B프레임을 쓰지 않는다.
/// Video2X 6.4.0은 B프레임이 있는 입력의 끝 프레임을 잃어버리기 때문이다(실측: 72장 중 2장).
/// </param>
public sealed record EncodeOptions(int Crf, string Preset = "veryfast", bool Intermediate = false)
{
    /// <summary>중간 영상 화질. 한 번 더 인코딩되므로 손실이 거의 없게 높게 잡는다.</summary>
    public const int IntermediateCrf = 12;

    public static EncodeOptions ForIntermediate(string preset = "veryfast") => new(IntermediateCrf, preset, Intermediate: true);
}

/// <summary>ffmpeg 인자 목록을 만든다. 실행은 하지 않으므로 단위 테스트로 검증할 수 있다.</summary>
public static class FfmpegCommands
{
    /// <summary>libx264 영상을 담을 수 있는 출력 확장자.</summary>
    public static bool IsSupportedOutput(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mkv" or ".mov" or ".m4v";

    private static bool IsMp4Family(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov" or ".m4v";

    /// <summary>자막·첨부 글꼴까지 그대로 옮겨 담을 수 있는 형식인지 (MKV만).</summary>
    public static bool KeepsSubtitles(string path) => Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 처리 영역들을 RGB PNG 프레임 열로 뽑는다 (영역마다 폴더 하나, 000001.png부터).
    /// 프레임 시각은 <c>fps=RATE:start_time=0</c>로 0, 1/RATE, 2/RATE…에 고정한다. 합성 단계도 똑같이 하므로 프레임이 1:1로 맞는다.
    /// </summary>
    public static List<string> ExtractRegions(MediaInfo info, IReadOnlyList<(PixelRect Rect, string Directory)> regions, double? startSeconds, double? durationSeconds)
    {
        var args = Begin();
        AddInput(args, info.Path, startSeconds, durationSeconds);

        var graph = new StringBuilder();
        graph.Append(CultureInfo.InvariantCulture, $"[0:{info.VideoStreamIndex}]{FpsFilter(info.Fps)}");
        if (regions.Count == 1) graph.Append("[s0]");
        else
        {
            graph.Append(CultureInfo.InvariantCulture, $",split={regions.Count}");
            for (int i = 0; i < regions.Count; i++) graph.Append(CultureInfo.InvariantCulture, $"[s{i}]");
        }
        string toRgb = ToRgbFilter(info);
        for (int i = 0; i < regions.Count; i++)
        {
            var r = regions[i].Rect;
            graph.Append(CultureInfo.InvariantCulture, $";[s{i}]crop={r.Width}:{r.Height}:{r.X}:{r.Y},{toRgb}[r{i}]");
        }
        args.AddRange(["-filter_complex", graph.ToString()]);

        for (int i = 0; i < regions.Count; i++)
        {
            args.AddRange(["-map", $"[r{i}]", "-fps_mode", "passthrough", "-start_number", "1",
                Path.Combine(regions[i].Directory, "%06d.png")]);
        }
        return args;
    }

    /// <summary>복원된 RGBA 프레임 열을 원본 위에 얹어 인코딩한다. 알파가 0인 곳은 원본 픽셀이 그대로 간다.</summary>
    public static List<string> Composite(MediaInfo info, IReadOnlyList<(PixelRect Rect, string Directory)> overlays, double? startSeconds, double? durationSeconds, EncodeOptions encode, string outputPath)
    {
        var args = Begin();
        AddInput(args, info.Path, startSeconds, durationSeconds);
        string rate = info.Fps.ToString();
        foreach (var overlay in overlays)
            args.AddRange(["-framerate", rate, "-start_number", "1", "-i", Path.Combine(overlay.Directory, "%06d.png")]);
        int? subtitles = AddSubtitleInput(args, info, encode, outputPath, 1 + overlays.Count, startSeconds, durationSeconds);

        var graph = new StringBuilder();
        graph.Append(CultureInfo.InvariantCulture, $"[0:{info.VideoStreamIndex}]{FpsFilter(info.Fps)}[b0]");
        string toYuva = ToYuvaFilter(info);
        for (int i = 0; i < overlays.Count; i++)
        {
            var r = overlays[i].Rect;
            string result = i == overlays.Count - 1 ? "vout" : $"b{i + 1}";
            graph.Append(CultureInfo.InvariantCulture, $";[{i + 1}:v]{toYuva}[o{i}]");
            graph.Append(CultureInfo.InvariantCulture, $";[b{i}][o{i}]overlay={r.X}:{r.Y}:eof_action=pass:format=yuv420[{result}]");
        }
        args.AddRange(["-filter_complex", graph.ToString(), "-map", "[vout]"]);
        AddEncode(args, info, encode, outputPath, startSeconds, subtitles);
        return args;
    }

    /// <summary>빠른 모드: delogo 필터로 박스 안을 주변 픽셀로 메운다.</summary>
    public static List<string> Delogo(MediaInfo info, IReadOnlyList<PixelRect> boxes, double? startSeconds, double? durationSeconds, EncodeOptions encode, string outputPath)
    {
        var args = Begin();
        AddInput(args, info.Path, startSeconds, durationSeconds);
        int? subtitles = AddSubtitleInput(args, info, encode, outputPath, 1, startSeconds, durationSeconds);
        string filter = string.Join(',', boxes.Select(b => string.Create(CultureInfo.InvariantCulture, $"delogo=x={b.X}:y={b.Y}:w={b.Width}:h={b.Height}")));
        // 업스케일러에 넘길 때는 프레임 간격을 일정하게 맞춘다.
        if (encode.Intermediate) filter = FpsFilter(info.Fps) + "," + filter;
        args.AddRange(["-map", $"0:{info.VideoStreamIndex}", "-vf", filter]);
        AddEncode(args, info, encode, outputPath, startSeconds, subtitles);
        return args;
    }

    /// <summary>업스케일만 할 때: 원본(또는 그 구간)을 업스케일러용 중간 영상으로 바꾼다.</summary>
    public static List<string> Prepare(MediaInfo info, double? startSeconds, double? durationSeconds, EncodeOptions encode, string outputPath)
    {
        var args = Begin();
        AddInput(args, info.Path, startSeconds, durationSeconds);
        int? subtitles = AddSubtitleInput(args, info, encode, outputPath, 1, startSeconds, durationSeconds);
        args.AddRange(["-map", $"0:{info.VideoStreamIndex}", "-vf", FpsFilter(info.Fps)]);
        AddEncode(args, info, encode, outputPath, startSeconds, subtitles);
        return args;
    }

    /// <summary>
    /// 업스케일된 영상(영상만 있음)에 원본의 오디오·자막을 붙여 최종 파일을 만든다.
    /// <paramref name="scaleTo"/>가 없으면 영상은 다시 인코딩하지 않고 그대로 옮긴다. 있으면 그 크기로 줄여(lanczos) 인코딩한다.
    /// </summary>
    public static List<string> Finalize(MediaInfo info, string upscaledPath, (int Width, int Height)? scaleTo, int outputHeight,
        double? startSeconds, double? durationSeconds, EncodeOptions encode, string outputPath)
    {
        var args = Begin();
        args.AddRange(["-i", upscaledPath]);
        AddInput(args, info.Path, startSeconds, durationSeconds);
        int? subtitles = AddSubtitleInput(args, info, encode, outputPath, 2, startSeconds, durationSeconds);
        args.AddRange(["-map", "0:v:0"]);

        if (scaleTo is { } size)
        {
            args.AddRange(["-vf", string.Create(CultureInfo.InvariantCulture, $"scale={size.Width}:{size.Height}:flags=lanczos"),
                "-c:v", "libx264", "-crf", encode.Crf.ToString(CultureInfo.InvariantCulture), "-preset", encode.Preset, "-pix_fmt", "yuv420p"]);
        }
        else
        {
            args.AddRange(["-c:v", "copy"]);
        }

        if (ColorTags.H264Metadata(info, outputHeight) is { } metadata) args.AddRange(["-bsf:v", metadata]);
        AddStreams(args, info, outputPath, audioInput: 1, subtitles, startSeconds);
        args.Add(outputPath);
        return args;
    }

    /// <summary>미리보기용: T초 지점의 프레임 한 장을 PNG로 표준 출력에 쓴다.</summary>
    public static List<string> PreviewFrame(string inputPath, double seconds) =>
        ["-hide_banner", "-nostdin", "-loglevel", "error", "-ss", Seconds(seconds), "-i", inputPath,
         "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "-"];

    /// <summary>파일의 영상 프레임(패킷) 수를 세는 ffprobe 인자. 디코딩하지 않아 빠르다.</summary>
    public static List<string> CountFrames(string path) =>
        ["-v", "error", "-count_packets", "-select_streams", "v:0", "-show_entries", "stream=nb_read_packets", "-of", "csv=p=0", path];

    /// <summary>오디오 처리: 출력이 MP4 계열인데 원본 오디오가 MP4에 담을 수 없는 코덱이면 AAC로 바꾸고, 아니면 그대로 복사한다.</summary>
    public static IReadOnlyList<string> AudioArguments(MediaInfo info, string outputPath)
    {
        if (!info.HasAudio) return [];
        bool needsTranscode = IsMp4Family(outputPath) && info.AudioCodecs.Any(c => !Mp4AudioCodecs.Contains(c));
        return needsTranscode ? ["-c:a", "aac", "-b:a", "192k"] : ["-c:a", "copy"];
    }

    private static readonly HashSet<string> Mp4AudioCodecs = new(StringComparer.OrdinalIgnoreCase) { "aac", "mp3", "ac3", "eac3", "alac", "flac", "opus" };

    private static List<string> Begin()
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "warning" };
        args.AddRange(FfmpegProgressParser.Arguments);
        return args;
    }

    private static void AddInput(List<string> args, string path, double? startSeconds, double? durationSeconds)
    {
        if (startSeconds is > 0) args.AddRange(["-ss", Seconds(startSeconds.Value)]);
        if (durationSeconds is > 0) args.AddRange(["-t", Seconds(durationSeconds.Value)]);
        args.AddRange(["-i", path]);
    }

    private static void AddEncode(List<string> args, MediaInfo info, EncodeOptions encode, string outputPath, double? startSeconds, int? subtitleInput)
    {
        args.AddRange(["-c:v", "libx264", "-crf", encode.Crf.ToString(CultureInfo.InvariantCulture), "-preset", encode.Preset, "-pix_fmt", "yuv420p"]);
        // 원본의 색 정보 표기를 그대로 이어 붙인다 (없으면 붙이지 않는다).
        if (info.ColorSpace is { } space && SwsMatrix(space) is not null) args.AddRange(["-colorspace", space]);
        if (info.ColorPrimaries is { } primaries) args.AddRange(["-color_primaries", primaries]);
        if (info.ColorTransfer is { } transfer) args.AddRange(["-color_trc", transfer]);
        if (SwsRange(info.ColorRange) is { } range) args.AddRange(["-color_range", range]);

        if (encode.Intermediate) args.AddRange(["-bf", "0", "-an", "-sn"]);
        else AddStreams(args, info, outputPath, audioInput: 0, subtitleInput, startSeconds);
        args.Add(outputPath);
    }

    /// <summary>
    /// 자막·첨부 글꼴을 옮겨 담을 출력이면 원본을 자막 전용 입력으로 한 번 더 연다. 돌려주는 값은 그 입력 번호(필요 없으면 null).
    /// <para>
    /// 자막은 패킷이 드문드문하다. ffmpeg는 가장 뒤처진 출력 스트림의 입력부터 읽는데, 자막이 오디오와 같은 입력에 있고
    /// 영상이 다른 입력(업스케일 결과, 합성할 프레임 열)에 걸려 있으면 자막을 기다리는 동안 오디오만 끝까지 먼저 쓰인다.
    /// 그렇게 만들어진 파일은 오디오와 영상이 멀리 떨어져 있어 재생기가 멈칫거리다 몰아서 따라잡는다
    /// (실측: 16분짜리 영상에서 오디오 947초가 영상보다 먼저 쓰였다). 자막만 따로 읽히게 하면 오디오와 영상이 나란히 간다.
    /// </para>
    /// </summary>
    private static int? AddSubtitleInput(List<string> args, MediaInfo info, EncodeOptions encode, string outputPath, int index, double? startSeconds, double? durationSeconds)
    {
        if (encode.Intermediate || !KeepsSubtitles(outputPath)) return null;
        AddInput(args, info.Path, startSeconds, durationSeconds);
        return index;
    }

    /// <summary>오디오(전부)와, MKV라면 자막·첨부 글꼴까지 원본에서 옮겨 담는다.</summary>
    private static void AddStreams(List<string> args, MediaInfo info, string outputPath, int audioInput, int? subtitleInput, double? startSeconds)
    {
        string source = audioInput.ToString(CultureInfo.InvariantCulture);
        // 중간 지점부터 자를 때: 그대로 복사하는 스트림(오디오·자막)은 기본적으로 탐색 지점(그 앞 키프레임)부터 딸려 온다.
        // 그러면 영상이 몇 초 늦게 시작하는 파일이 되므로(실측 4.5초) 시작 지점 이전 패킷은 버린다.
        if (startSeconds is > 0) args.AddRange(["-copypriorss", "0"]);
        args.AddRange(["-map", source + ":a?"]);
        args.AddRange(AudioArguments(info, outputPath));
        if (subtitleInput is { } input)
        {
            string subtitles = input.ToString(CultureInfo.InvariantCulture);
            args.AddRange(["-map", subtitles + ":s?", "-map", subtitles + ":t?", "-c:s", "copy"]);
        }
        if (IsMp4Family(outputPath)) args.AddRange(["-movflags", "+faststart"]);
    }

    private static string FpsFilter(Rational fps) => $"fps={fps}:start_time=0";

    // 영상(YUV) → RGB → 영상으로 왕복할 때 양쪽이 같은 변환 행렬을 써야 색이 틀어지지 않는다.
    // 원본에 색 공간 표기가 있으면 양방향 모두 그 행렬을 명시하고, 없으면 양쪽 다 ffmpeg 기본값에 맡긴다(같은 기본값이라 왕복이 맞는다).
    private static string ToRgbFilter(MediaInfo info)
    {
        var options = new List<string>(2);
        if (SwsMatrix(info.ColorSpace) is { } matrix) options.Add("in_color_matrix=" + matrix);
        if (SwsRange(info.ColorRange) is { } range) options.Add("in_range=" + range);
        return options.Count == 0 ? "format=rgb24" : $"scale={string.Join(':', options)},format=rgb24";
    }

    private static string ToYuvaFilter(MediaInfo info)
    {
        var options = new List<string>(2);
        if (SwsMatrix(info.ColorSpace) is { } matrix) options.Add("out_color_matrix=" + matrix);
        if (SwsRange(info.ColorRange) is { } range) options.Add("out_range=" + range);
        return options.Count == 0 ? "format=yuva420p" : $"scale={string.Join(':', options)},format=yuva420p";
    }

    private static string? SwsMatrix(string? colorSpace) => colorSpace switch
    {
        "bt709" => "bt709",
        "smpte170m" => "smpte170m",
        "bt470bg" => "bt470",
        "bt2020nc" or "bt2020c" => "bt2020",
        "smpte240m" => "smpte240m",
        "fcc" => "fcc",
        _ => null,
    };

    private static string? SwsRange(string? colorRange) => colorRange switch
    {
        "tv" or "mpeg" or "limited" => "tv",
        "pc" or "jpeg" or "full" => "pc",
        _ => null,
    };

    private static string Seconds(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>업스케일 결과에 새길 색 정보 표기.</summary>
public static class ColorTags
{
    /// <summary>
    /// h264_metadata 비트스트림 필터 인자를 만든다. 표기할 것이 없으면 null.
    /// <para>
    /// 색 표기가 없는 영상은 재생기가 해상도로 짐작한다(SD는 BT.601, HD는 BT.709). 표기 없는 SD 영상을 HD 크기로 키우면
    /// 재생기의 짐작이 바뀌어 색이 미묘하게 달라지므로, 그 경우 원래 해석(BT.601)을 명시해 둔다.
    /// </para>
    /// </summary>
    public static string? H264Metadata(MediaInfo info, int outputHeight)
    {
        int? primaries = Code(info.ColorPrimaries, Primaries);
        int? transfer = Code(info.ColorTransfer, Transfers);
        int? matrix = Code(info.ColorSpace, Matrices);

        if (matrix is null && info.Height < HdHeight && outputHeight >= HdHeight)
        {
            matrix = 6;                                   // smpte170m (BT.601)
            primaries ??= info.Height is 576 or 288 ? 5 : 6; // PAL이면 bt470bg, 아니면 smpte170m
            transfer ??= 6;
        }

        var parts = new List<string>(4);
        if (primaries is { } p) parts.Add(string.Create(CultureInfo.InvariantCulture, $"colour_primaries={p}"));
        if (transfer is { } t) parts.Add(string.Create(CultureInfo.InvariantCulture, $"transfer_characteristics={t}"));
        if (matrix is { } m) parts.Add(string.Create(CultureInfo.InvariantCulture, $"matrix_coefficients={m}"));
        if (info.ColorRange is "pc" or "jpeg" or "full") parts.Add("video_full_range_flag=1");
        return parts.Count == 0 ? null : "h264_metadata=" + string.Join(':', parts);
    }

    private const int HdHeight = 720;

    private static int? Code(string? name, Dictionary<string, int> table) =>
        name is not null && table.TryGetValue(name, out int code) ? code : null;

    // ITU-T H.273 코드
    private static readonly Dictionary<string, int> Primaries = new()
    {
        ["bt709"] = 1, ["bt470m"] = 4, ["bt470bg"] = 5, ["smpte170m"] = 6, ["smpte240m"] = 7, ["film"] = 8, ["bt2020"] = 9,
    };

    private static readonly Dictionary<string, int> Transfers = new()
    {
        ["bt709"] = 1, ["gamma22"] = 4, ["bt470m"] = 4, ["gamma28"] = 5, ["bt470bg"] = 5, ["smpte170m"] = 6, ["smpte240m"] = 7, ["linear"] = 8,
        ["iec61966-2-1"] = 13, ["bt2020-10"] = 14, ["bt2020-12"] = 15, ["smpte2084"] = 16, ["arib-std-b67"] = 18,
    };

    private static readonly Dictionary<string, int> Matrices = new()
    {
        ["bt709"] = 1, ["fcc"] = 4, ["bt470bg"] = 5, ["smpte170m"] = 6, ["smpte240m"] = 7, ["bt2020nc"] = 9, ["bt2020c"] = 10,
    };
}
