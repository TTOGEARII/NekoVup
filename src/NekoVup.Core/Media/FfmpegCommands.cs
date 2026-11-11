using System.Globalization;
using System.Text;
using NekoVup.Core.Pipeline;
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

    /// <summary>libx264가 받는 화질 값(CRF)의 최댓값. 0이 무손실, 클수록 거칠다.</summary>
    public const int MaxCrf = 51;

    /// <summary>libx264의 인코딩 속도 이름 (빠른 것부터).</summary>
    public static IReadOnlyList<string> Presets { get; } =
        ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow", "placebo"];

    private static bool IsMp4Family(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov" or ".m4v";

    /// <summary>자막·첨부 글꼴까지 그대로 옮겨 담을 수 있는 형식인지 (MKV만).</summary>
    public static bool KeepsSubtitles(string path) => Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 처리 영역들을 RGB PNG 프레임 열로 뽑는다 (영역마다 폴더 하나, 000001.png부터).
    /// 프레임 시각은 <c>fps=RATE:start_time=0</c>로 0, 1/RATE, 2/RATE…에 고정한다. 합성 단계도 똑같이 하므로 프레임이 1:1로 맞는다.
    /// </summary>
    public static List<string> ExtractRegions(MediaInfo info, IReadOnlyList<(PixelRect Rect, string Directory)> regions, double? startSeconds, double? durationSeconds, bool deinterlace = false)
    {
        var args = Begin();
        AddInput(args, info.Path, startSeconds, durationSeconds);

        var graph = new StringBuilder();
        graph.Append(CultureInfo.InvariantCulture, $"[0:{info.VideoStreamIndex}]{SourceFilter(info, deinterlace)}");
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
            args.AddRange(["-map", $"[r{i}]", "-fps_mode", "passthrough", "-start_number", "1", FramePattern(regions[i].Directory)]);
        }
        return args;
    }

    /// <summary>
    /// 폴더 안 프레임 열(000001.png…)을 가리키는 ffmpeg 이름 틀. 틀에서 <c>%</c>는 번호 자리 표시라,
    /// 폴더 이름에 든 <c>%</c>("100% 완성" 등)는 <c>%%</c>로 적어야 글자 그대로 읽힌다.
    /// </summary>
    public static string FramePattern(string directory) => Path.Combine(directory.Replace("%", "%%"), "%06d.png");

    /// <summary>복원된 RGBA 프레임 열을 원본 위에 얹어 인코딩한다. 알파가 0인 곳은 원본 픽셀이 그대로 간다.</summary>
    public static List<string> Composite(MediaInfo info, IReadOnlyList<(PixelRect Rect, string Directory)> overlays, double? startSeconds, double? durationSeconds, EncodeOptions encode, string outputPath, bool deinterlace = false)
    {
        var args = Begin();
        AddInput(args, info.Path, startSeconds, durationSeconds);
        string rate = info.Fps.ToString();
        foreach (var overlay in overlays)
            args.AddRange(["-framerate", rate, "-start_number", "1", "-i", FramePattern(overlay.Directory)]);
        int? subtitles = AddSubtitleInput(args, info, encode, outputPath, 1 + overlays.Count, startSeconds, durationSeconds);

        var graph = new StringBuilder();
        graph.Append(CultureInfo.InvariantCulture, $"[0:{info.VideoStreamIndex}]{SourceFilter(info, deinterlace)}[b0]");
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
    public static List<string> Delogo(MediaInfo info, IReadOnlyList<PixelRect> boxes, double? startSeconds, double? durationSeconds, EncodeOptions encode, string outputPath, bool deinterlace = false) =>
        Delogo(info, [.. boxes.Select(BoxPath.Fixed)], startSeconds, durationSeconds, encode, outputPath, deinterlace);

    /// <summary>빠른 모드, 박스가 움직일 수 있는 꼴: 움직이는 박스는 프레임마다 자리를 계산하는 식으로 적는다.</summary>
    public static List<string> Delogo(MediaInfo info, IReadOnlyList<BoxPath> paths, double? startSeconds, double? durationSeconds, EncodeOptions encode, string outputPath, bool deinterlace = false)
    {
        var args = Begin();
        AddInput(args, info.Path, startSeconds, durationSeconds);
        int? subtitles = AddSubtitleInput(args, info, encode, outputPath, 1, startSeconds, durationSeconds);
        string filter = DelogoFilter(paths, info.Fps.Value, startSeconds);
        // 업스케일러에 넘길 때는 프레임 간격을 일정하게 맞춘다.
        string pre = SourceFilter(info, deinterlace, fixFps: encode.Intermediate);
        if (pre.Length > 0) filter = pre + "," + filter;
        args.AddRange(["-map", $"0:{info.VideoStreamIndex}", "-vf", filter]);
        AddEncode(args, info, encode, outputPath, startSeconds, subtitles);
        return args;
    }

    /// <summary>
    /// 박스 경로마다 delogo 하나씩. 움직이는 박스는 x·y·w·h를 식으로 적는다 (delogo가 프레임마다 다시 계산한다):
    /// 먼저 그 프레임의 원본 기준 번호를 구해 두고(<c>st(0,…)</c> — 구간을 잘라 읽으면 시각이 0부터라 시작 시각만큼 더한다.
    /// <see cref="FrameGrid.SourceFrame"/>과 같은 계산이다), 네 변을 따로 "첫 자리 + 구간마다 옮겨 간 양"으로 더해 반올림한다.
    /// <see cref="BoxPath.At"/>과 같은 값이 나온다 — 경로가 프레임마다 잘리는 것이면(<see cref="BoxPath.Bounds"/>) 식에서도 자른다.
    /// </summary>
    public static string DelogoFilter(IReadOnlyList<BoxPath> paths, double fps, double? startSeconds)
    {
        double start = startSeconds ?? 0;
        string frame = string.Create(CultureInfo.InvariantCulture,
            $"0*st(0,max({FrameGrid.FrameAt(start, fps)},ceil(t*{fps:R}+{start * fps:R}-0.5)))");
        return string.Join(',', paths.Select(path =>
        {
            if (!path.Moves)
            {
                var b = path.At(0);
                return string.Create(CultureInfo.InvariantCulture, $"delogo=x={b.X}:y={b.Y}:w={b.Width}:h={b.Height}");
            }
            string left = Edge(path, r => r.X), top = Edge(path, r => r.Y);
            string right = Edge(path, r => r.Right), bottom = Edge(path, r => r.Bottom);
            string x = $"floor(0.5+{frame}+{left})", x0 = $"floor(0.5+{left})", x1 = $"floor(0.5+{frame}+{right})";
            string y = $"floor(0.5+{frame}+{top})", y0 = $"floor(0.5+{top})", y1 = $"floor(0.5+{frame}+{bottom})";
            if (path.Bounds is { } cut)
            {
                (x, x0, x1) = (Low(x, cut.X), Low(x0, cut.X), High(x1, cut.Right));
                (y, y0, y1) = (Low(y, cut.Y), Low(y0, cut.Y), High(y1, cut.Bottom));
            }
            return $"delogo=x='{x}':y='{y}':w='{x1}-{x0}':h='{y1}-{y0}'";
        }));

        static string Low(string value, int limit) => string.Create(CultureInfo.InvariantCulture, $"max({limit},{value})");
        static string High(string value, int limit) => string.Create(CultureInfo.InvariantCulture, $"min({limit},{value})");
    }

    /// <summary>박스의 한 변이 프레임 번호(<c>ld(0)</c>)에 따라 가는 값. 옮겨 가지 않는 구간은 적지 않는다.</summary>
    private static string Edge(BoxPath path, Func<PixelRect, int> edge)
    {
        var terms = new List<string> { edge(path.Rects[0]).ToString(CultureInfo.InvariantCulture) };
        for (int i = 0; i + 1 < path.Rects.Count; i++)
        {
            int delta = edge(path.Rects[i + 1]) - edge(path.Rects[i]);
            if (delta == 0) continue;
            long from = path.Frames[i], to = path.Frames[i + 1];
            terms.Add(path.Jump
                ? string.Create(CultureInfo.InvariantCulture, $"{delta}*gte(ld(0),{to})")
                : string.Create(CultureInfo.InvariantCulture, $"{delta}*clip((ld(0)-{from})/{to - from},0,1)"));
        }
        return Sum(terms, 0, terms.Count);
    }

    /// <summary>
    /// 항들을 더하는 식. ffmpeg은 괄호 없이 길게 이은 덧셈을 읽지 못하므로(90항쯤부터 실패한다) 길면 반씩 괄호로 묶는다.
    /// </summary>
    private static string Sum(List<string> terms, int from, int to)
    {
        if (to - from > 16)
        {
            int middle = from + (to - from) / 2;
            return $"({Sum(terms, from, middle)})+({Sum(terms, middle, to)})";
        }
        var text = new StringBuilder(terms[from]);
        for (int i = from + 1; i < to; i++)
        {
            if (terms[i][0] != '-') text.Append('+');
            text.Append(terms[i]);
        }
        return text.ToString();
    }

    /// <summary>업스케일만 할 때: 원본(또는 그 구간)을 업스케일러용 중간 영상으로 바꾼다.</summary>
    public static List<string> Prepare(MediaInfo info, double? startSeconds, double? durationSeconds, EncodeOptions encode, string outputPath, bool deinterlace = false)
    {
        var args = Begin();
        AddInput(args, info.Path, startSeconds, durationSeconds);
        int? subtitles = AddSubtitleInput(args, info, encode, outputPath, 1, startSeconds, durationSeconds);
        args.AddRange(["-map", $"0:{info.VideoStreamIndex}", "-vf", SourceFilter(info, deinterlace)]);
        AddEncode(args, info, encode, outputPath, startSeconds, subtitles);
        return args;
    }

    /// <summary>
    /// 업스케일된 영상(영상만 있음)에 원본의 오디오·자막을 붙여 최종 파일을 만든다.
    /// <paramref name="scaleTo"/>도 <paramref name="sharpen"/>도 없으면 영상은 다시 인코딩하지 않고 그대로 옮긴다.
    /// 있으면 그 크기로 줄이고(lanczos) 선명도 필터를 건 뒤 인코딩한다.
    /// </summary>
    public static List<string> Finalize(MediaInfo info, string upscaledPath, (int Width, int Height)? scaleTo, int outputHeight,
        double? startSeconds, double? durationSeconds, EncodeOptions encode, string outputPath, int sharpen = 0)
    {
        var args = Begin();
        args.AddRange(["-i", upscaledPath]);
        AddInput(args, info.Path, startSeconds, durationSeconds);
        int? subtitles = AddSubtitleInput(args, info, encode, outputPath, 2, startSeconds, durationSeconds);
        args.AddRange(["-map", "0:v:0"]);

        var filters = new List<string>();
        if (scaleTo is { } size) filters.Add(string.Create(CultureInfo.InvariantCulture, $"scale={size.Width}:{size.Height}:flags=lanczos"));
        if (SharpenFilter(sharpen) is { } cas) filters.Add(cas);
        // 픽셀 모양(가로세로 비율이 1:1이 아닌 픽셀 — DVD·캠코더 영상)은 업스케일을 거치며 사라질 수 있다
        // (옛 비디오 모드는 프레임을 날것으로 주고받아 표기가 남지 않는다). 원본 값을 다시 적어 두지 않으면 결과가 옆으로 눌리거나 늘어나 보인다.
        var sar = NonSquarePixels(info);
        if (filters.Count > 0)
        {
            if (sar is { } s) filters.Add(string.Create(CultureInfo.InvariantCulture, $"setsar={s.Num}/{s.Den}"));
            args.AddRange(["-vf", string.Join(',', filters),
                "-c:v", "libx264", "-crf", encode.Crf.ToString(CultureInfo.InvariantCulture), "-preset", encode.Preset, "-pix_fmt", "yuv420p"]);
        }
        else
        {
            args.AddRange(["-c:v", "copy"]);
            // 다시 인코딩하지 않을 때는 파일(컨테이너)에 화면 비율을 적는다. 영상 스트림 쪽은 아래 h264_metadata가 맡는다.
            if (sar is { } s) args.AddRange(["-aspect", DisplayAspect(info.Width, info.Height, s)]);
        }

        if (ColorTags.H264Metadata(info, outputHeight) is { } metadata) args.AddRange(["-bsf:v", metadata]);
        AddStreams(args, info, outputPath, audioInput: 1, subtitles, startSeconds);
        // 제목·날짜 같은 파일 정보는 원본(입력 1)에서 가져온다. 적지 않으면 첫 입력(업스케일된 영상)의 것이 쓰여 원본 정보가 사라진다.
        args.AddRange(["-map_metadata", "1"]);
        args.Add(outputPath);
        return args;
    }

    /// <summary>원본의 픽셀이 정사각형이 아니면 그 비율(약분한 값), 정사각형이거나 표기가 없으면 null.</summary>
    public static (long Num, long Den)? NonSquarePixels(MediaInfo info)
    {
        long num = info.SampleAspectRatio.Num, den = info.SampleAspectRatio.Den;
        if (num <= 0 || den <= 0 || num == den) return null;
        long divisor = Gcd(num, den);
        return (num / divisor, den / divisor);
    }

    /// <summary>화면 비율(가로:세로) = 픽셀 수 × 픽셀 모양. ffmpeg의 -aspect는 255 이하의 수만 받으므로 넘치면 가장 가까운 비율로 줄인다.</summary>
    private static string DisplayAspect(int width, int height, (long Num, long Den) sar)
    {
        long w = width * sar.Num, h = height * sar.Den;
        long divisor = Gcd(w, h);
        (w, h) = (w / divisor, h / divisor);
        if (w > 255 || h > 255)
        {
            double ratio = (double)w / h, bestError = double.MaxValue;
            for (long d = 1; d <= 255; d++)
            {
                long n = (long)Math.Round(ratio * d);
                if (n is < 1 or > 255) continue;
                double error = Math.Abs((double)n / d - ratio);
                if (error < bestError - 1e-12) (bestError, w, h) = (error, n, d);
            }
        }
        return string.Create(CultureInfo.InvariantCulture, $"{w}:{h}");
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return Math.Max(1, Math.Abs(a));
    }

    /// <summary>선명도 세기의 최댓값 (화면의 0~100).</summary>
    public const int MaxSharpen = 100;

    /// <summary>
    /// 선명도 필터. AMD FidelityFX의 CAS(대비 적응 선명화)로, 이미 또렷한 곳은 덜 건드리고 흐린 경계만 세워 unsharp보다 테가 덜 생긴다.
    /// 0~100을 cas의 strength 0~1로 옮긴다. 0이면 필터 없음(null).
    /// </summary>
    public static string? SharpenFilter(int sharpen)
    {
        if (sharpen <= 0) return null;
        double strength = Math.Min(sharpen, MaxSharpen) / (double)MaxSharpen;
        return string.Create(CultureInfo.InvariantCulture, $"cas=strength={strength:0.00}");
    }

    /// <summary>미리보기용: T초 지점의 프레임 한 장을 PNG로 표준 출력에 쓴다.</summary>
    public static List<string> PreviewFrame(string inputPath, double seconds) =>
        ["-hide_banner", "-nostdin", "-loglevel", "error", "-ss", Seconds(seconds), "-i", inputPath,
         "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "-"];

    /// <summary>파일의 영상 프레임(패킷) 수를 세는 ffprobe 인자. 디코딩하지 않아 빠르다.</summary>
    public static List<string> CountFrames(string path) =>
        ["-v", "error", "-count_packets", "-select_streams", "v:0", "-show_entries", "stream=nb_read_packets", "-of", "csv=p=0", path];

    /// <summary>
    /// 오디오 처리: 스트림마다, 출력 형식이 그대로 담을 수 있으면 복사하고 못 담으면 바꿔 담는다 (<see cref="AudioHandling"/>).
    /// 못 담는 것을 복사하라고 하면 몇 시간 처리한 끝의 마지막 단계에서야 실패한다.
    /// </summary>
    public static IReadOnlyList<string> AudioArguments(MediaInfo info, string outputPath)
    {
        if (!info.HasAudio) return [];
        var codecs = info.AudioCodecs.Select(c => AudioHandling(c, outputPath)).ToList();
        if (codecs.All(c => c == codecs[0])) return Codec("", codecs[0]);
        // 섞여 있으면 스트림마다 따로 정한다 (-map N:a 가 오디오를 전부 원래 순서대로 옮기므로 번호가 그대로 맞는다).
        var args = new List<string>();
        for (int i = 0; i < codecs.Count; i++) args.AddRange(Codec(string.Create(CultureInfo.InvariantCulture, $":{i}"), codecs[i]));
        return args;

        static string[] Codec(string stream, string codec) =>
            codec == "aac" ? ["-c:a" + stream, codec, "-b:a" + stream, "192k"] : ["-c:a" + stream, codec];
    }

    /// <summary>
    /// 오디오 스트림 하나를 이 출력에 어떻게 담을지: "copy"(그대로) 또는 바꿔 담을 형식.
    /// MKV는 잃는 것 없이 "flac"으로, MP4 계열은 "aac"로 바꾼다. 목록은 내장 ffmpeg(8.1)로 하나씩 복사해 보고 정했다 —
    /// 목록에 없는 형식은 담을 수 있더라도 바꿔 담는다 (마지막 단계에서 실패하는 것보다 낫다).
    /// </summary>
    public static string AudioHandling(string codec, string outputPath) =>
        Path.GetExtension(outputPath).ToLowerInvariant() switch
        {
            ".mkv" => MkvAudioCodecs.Contains(codec) ? "copy" : "flac",
            ".mov" => MovAudioCodecs.Contains(codec) ? "copy" : "aac",
            ".m4v" => M4vAudioCodecs.Contains(codec) ? "copy" : "aac",
            _ => Mp4AudioCodecs.Contains(codec) ? "copy" : "aac",
        };

    /// <summary>그대로 담지 못해 바꿔 담는 오디오들 ("pcm_dvd → FLAC" 꼴, 스트림마다 하나).</summary>
    public static IReadOnlyList<string> ConvertedAudio(MediaInfo info, string outputPath) =>
        [.. info.AudioCodecs.Select(c => (Codec: c, To: AudioHandling(c, outputPath))).Where(a => a.To != "copy")
            .Select(a => $"{(a.Codec is "" or "unknown" ? "알 수 없음" : a.Codec)} → {a.To.ToUpperInvariant()}")];

    private static readonly HashSet<string> Mp4AudioCodecs = new(StringComparer.OrdinalIgnoreCase) { "aac", "mp3", "ac3", "eac3", "alac", "flac", "opus" };

    // MOV는 FLAC·Opus를 받지 않는다 ("only supported in MP4").
    private static readonly HashSet<string> MovAudioCodecs = new(StringComparer.OrdinalIgnoreCase) { "aac", "mp3", "ac3", "eac3", "alac" };

    // M4V(ffmpeg의 ipod 형식)는 이 셋뿐이다.
    private static readonly HashSet<string> M4vAudioCodecs = new(StringComparer.OrdinalIgnoreCase) { "aac", "ac3", "alac" };

    // MKV가 못 담는 것: 디스크 전용 PCM 포장(DVD의 pcm_dvd, 블루레이의 pcm_bluray), 일부 PCM 변형, QuickTime·플래시 ADPCM 등.
    private static readonly HashSet<string> MkvAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "aac", "mp3", "mp2", "mp1", "ac3", "eac3", "dts", "truehd", "mlp", "flac", "alac", "opus", "vorbis", "wavpack", "tta",
        "pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_s16be", "pcm_s24be", "pcm_s32be", "pcm_u8", "pcm_f32le", "pcm_f64le", "pcm_mulaw", "pcm_alaw",
        "wmav1", "wmav2", "adpcm_ima_wav", "adpcm_ms", "adpcm_yamaha", "adpcm_g722", "amr_nb", "gsm_ms", "speex", "g723_1",
    };

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
            if (info.SubtitleCodecs.All(c => SubtitleHandling(c) == "copy"))
            {
                args.AddRange(["-map", subtitles + ":s?", "-map", subtitles + ":t?", "-c:s", "copy"]);
            }
            else
            {
                // MKV에 그대로 담을 수 없는 자막이 섞여 있다(MP4의 mov_text 등). 통째로 복사하면 몇 시간 처리한 끝에 마지막 단계가 실패하므로
                // 자막마다 따로 정한다: 그대로 복사 / 글자 자막으로 변환 / 뺌.
                int kept = 0;
                for (int i = 0; i < info.SubtitleCodecs.Count; i++)
                {
                    if (SubtitleHandling(info.SubtitleCodecs[i]) is not { } codec) continue;
                    args.AddRange(["-map", string.Create(CultureInfo.InvariantCulture, $"{subtitles}:s:{i}"),
                        string.Create(CultureInfo.InvariantCulture, $"-c:s:{kept++}"), codec]);
                }
                args.AddRange(["-map", subtitles + ":t?"]);
            }
        }
        if (IsMp4Family(outputPath)) args.AddRange(["-movflags", "+faststart"]);
    }

    // MKV가 원래 형식 그대로 담을 수 있는 자막.
    private static readonly HashSet<string> MkvSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ass", "ssa", "subrip", "srt", "text", "webvtt", "hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle", "hdmv_text_subtitle", "arib_caption",
    };

    /// <summary>MKV로 옮길 때 이 자막을 어떻게 할지: "copy"(그대로), "srt"(글자 자막으로 변환), null(담을 수 없어 뺌).</summary>
    private static string? SubtitleHandling(string codec) =>
        MkvSubtitleCodecs.Contains(codec) ? "copy"
        : codec.Equals("mov_text", StringComparison.OrdinalIgnoreCase) ? "srt"
        : null;

    /// <summary>MKV 출력에 옮겨 담지 못하고 빠지는 자막들의 형식 이름 (자막마다 하나).</summary>
    public static IReadOnlyList<string> DroppedSubtitleCodecs(MediaInfo info) =>
        [.. info.SubtitleCodecs.Where(c => SubtitleHandling(c) is null).Select(c => c is "" or "unknown" ? "알 수 없음" : c)];

    private static string FpsFilter(Rational fps) => $"fps={fps}:start_time=0";

    /// <summary>
    /// 인터레이스 해제: bwdif(yadif보다 세로 해상도를 더 살린다), 프레임 수는 그대로(send_frame).
    /// 필드 순서는 파일 표기를 따르고(parity), 표기가 없는데 "항상"을 골랐으면 ffmpeg가 짐작한다(auto). 모든 프레임에 건다(deint=all) — 옛 녹화는 프레임 플래그가 엉터리인 일이 많다.
    /// </summary>
    public static string DeinterlaceFilter(MediaInfo info) =>
        $"bwdif=mode=send_frame:parity={(info.IsInterlaced ? (info.TopFieldFirst ? "tff" : "bff") : "auto")}:deint=all";

    /// <summary>원본을 읽는 모든 단계가 똑같이 쓰는 앞 필터 (인터레이스 해제 → 프레임 시각 고정). 모든 경로가 같은 필터를 거쳐야 추출한 조각과 합성 바탕이 1:1로 맞는다.</summary>
    private static string SourceFilter(MediaInfo info, bool deinterlace, bool fixFps = true)
    {
        var parts = new List<string>(2);
        if (deinterlace) parts.Add(DeinterlaceFilter(info));
        if (fixFps) parts.Add(FpsFilter(info.Fps));
        return string.Join(',', parts);
    }

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

    /// <summary>ffprobe의 색 공간 이름 → scale 필터의 변환 행렬 이름. 모르는 값이면 null(ffmpeg 기본값에 맡긴다).</summary>
    public static string? SwsMatrix(string? colorSpace) => colorSpace switch
    {
        "bt709" => "bt709",
        "smpte170m" => "smpte170m",
        "bt470bg" => "bt470",
        "bt2020nc" or "bt2020c" => "bt2020",
        "smpte240m" => "smpte240m",
        "fcc" => "fcc",
        _ => null,
    };

    /// <summary>ffprobe의 색 범위 이름 → scale 필터의 범위 이름(tv/pc). 표기가 없으면 null.</summary>
    public static string? SwsRange(string? colorRange) => colorRange switch
    {
        "tv" or "mpeg" or "limited" => "tv",
        "pc" or "jpeg" or "full" => "pc",
        _ => null,
    };

    private static string Seconds(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>업스케일 결과에 새길 표시 정보 (색 표기와 픽셀 모양).</summary>
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
        // 픽셀 모양도 같은 자리(영상 스트림의 표시 정보)에 적는다 — 업스케일을 거치며 사라진 값을 되살린다.
        if (FfmpegCommands.NonSquarePixels(info) is { } sar) parts.Add(string.Create(CultureInfo.InvariantCulture, $"sample_aspect_ratio={sar.Num}/{sar.Den}"));
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
