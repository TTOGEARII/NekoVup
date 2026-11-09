using NekoVup.Core.Media;
using NekoVup.Core.Regions;

namespace NekoVup.Tests;

public class RationalTests
{
    [Theory]
    [InlineData("30000/1001", 30000, 1001, "30000/1001")]
    [InlineData("24000/1001", 24000, 1001, "24000/1001")]
    [InlineData("25/1", 25, 1, "25")]
    [InlineData("60", 60, 1, "60")]
    [InlineData("50/2", 25, 1, "25")]
    [InlineData("29.97", 2997, 100, "2997/100")]
    [InlineData(" 30000/1001 ", 30000, 1001, "30000/1001")]
    public void Parses_And_Keeps_Ratio_Text(string text, long num, long den, string expected)
    {
        Assert.True(Rational.TryParse(text, out var r));
        Assert.Equal(new Rational(num, den), r);
        Assert.Equal(expected, r.ToString());
        Assert.True(r.IsValid);
    }

    [Fact]
    public void Zero_Over_Zero_Parses_But_Is_Not_Valid()
    {
        Assert.True(Rational.TryParse("0/0", out var r));
        Assert.False(r.IsValid);
        Assert.Equal(0, r.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("30/x")]
    [InlineData("-30/1")]
    public void Rejects_Garbage(string? text) => Assert.False(Rational.TryParse(text, out _));

    [Fact]
    public void Value_Is_Exact_Enough()
    {
        Assert.Equal(29.97002997, Rational.Parse("30000/1001").Value, 8);
    }
}

public class FfprobeParserTests
{
    // ffprobe 8.0 -print_format json -show_format -show_streams 출력에서 필요한 부분만 남긴 고정 샘플
    private const string Sample = """
        {
            "streams": [
                {
                    "index": 0, "codec_name": "h264", "codec_type": "video", "width": 1280, "height": 720,
                    "coded_width": 1280, "coded_height": 720, "pix_fmt": "yuv420p",
                    "color_range": "tv", "color_space": "bt709", "color_transfer": "bt709", "color_primaries": "bt709",
                    "r_frame_rate": "30000/1001", "avg_frame_rate": "30000/1001", "time_base": "1/30000",
                    "duration": "6.006000", "nb_frames": "180",
                    "disposition": { "default": 1, "attached_pic": 0 },
                    "tags": { "language": "und", "handler_name": "VideoHandler" }
                },
                {
                    "index": 1, "codec_name": "aac", "codec_type": "audio", "sample_rate": "44100", "channels": 1,
                    "r_frame_rate": "0/0", "avg_frame_rate": "0/0", "duration": "6.000000",
                    "disposition": { "default": 1, "attached_pic": 0 }
                }
            ],
            "format": { "filename": "src.mp4", "nb_streams": 2, "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "6.006000", "size": "1234567" }
        }
        """;

    [Fact]
    public void Parses_Fixed_Sample()
    {
        var info = FfprobeParser.Parse(Sample, @"C:\영상\src.mp4");

        Assert.Equal(@"C:\영상\src.mp4", info.Path);
        Assert.Equal(0, info.VideoStreamIndex);
        Assert.Equal((1280, 720), (info.Width, info.Height));
        Assert.Equal(0, info.Rotation);
        Assert.Equal("30000/1001", info.Fps.ToString());
        Assert.Equal(6.006, info.DurationSeconds, 6);
        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal("yuv420p", info.PixelFormat);
        Assert.Equal("bt709", info.ColorSpace);
        Assert.Equal("tv", info.ColorRange);
        Assert.Equal(["aac"], info.AudioCodecs);
        Assert.True(info.HasAudio);
        Assert.Equal(180, info.EstimateFrames());
        Assert.Equal(60, info.EstimateFrames(durationSeconds: 2));
        Assert.Equal(150, info.EstimateFrames(startSeconds: 1, durationSeconds: 100));
    }

    [Theory]
    [InlineData("""{ "side_data_type": "Display Matrix", "rotation": -90 }""", 270)]
    [InlineData("""{ "side_data_type": "Display Matrix", "rotation": 90 }""", 90)]
    [InlineData("""{ "side_data_type": "Display Matrix", "rotation": -90.0 }""", 270)]
    public void Rotation_By_90_Swaps_Width_And_Height(string sideData, int expectedRotation)
    {
        string json = $$"""
            { "streams": [ { "index": 0, "codec_type": "video", "codec_name": "hevc", "width": 1920, "height": 1080,
                "r_frame_rate": "30/1", "avg_frame_rate": "30/1", "side_data_list": [ {{sideData}} ] } ],
              "format": { "duration": "10.0" } }
            """;
        var info = FfprobeParser.Parse(json, "phone.mov");
        Assert.Equal(expectedRotation, info.Rotation);
        Assert.Equal((1080, 1920), (info.Width, info.Height));
        Assert.Equal((1920, 1080), (info.CodedWidth, info.CodedHeight));
    }

    [Fact]
    public void Rotation_180_And_Legacy_Rotate_Tag()
    {
        string upsideDown = """
            { "streams": [ { "index": 0, "codec_type": "video", "width": 1920, "height": 1080, "r_frame_rate": "30/1", "avg_frame_rate": "30/1",
                "side_data_list": [ { "rotation": 180 } ] } ], "format": { "duration": "1" } }
            """;
        var a = FfprobeParser.Parse(upsideDown, "a.mp4");
        Assert.Equal(180, a.Rotation);
        Assert.Equal((1920, 1080), (a.Width, a.Height));

        string legacy = """
            { "streams": [ { "index": 0, "codec_type": "video", "width": 1920, "height": 1080, "r_frame_rate": "30/1", "avg_frame_rate": "30/1",
                "tags": { "rotate": "90" } } ], "format": { "duration": "1" } }
            """;
        var b = FfprobeParser.Parse(legacy, "b.mp4");
        Assert.Equal(90, b.Rotation);
        Assert.Equal((1080, 1920), (b.Width, b.Height));
    }

    [Theory]
    [InlineData(-90, 270)]
    [InlineData(90, 90)]
    [InlineData(-270, 90)]
    [InlineData(450, 90)]
    [InlineData(180, 180)]
    [InlineData(0, 0)]
    [InlineData(45, 0)]
    [InlineData(89.9, 90)]
    public void Normalizes_Rotation(double degrees, int expected) =>
        Assert.Equal(expected, FfprobeParser.NormalizeRotation(degrees));

    [Theory]
    [InlineData("30000/1001", "30000/1001", "30000/1001")]
    [InlineData("359640/12001", "30000/1001", "30000/1001")] // 사실상 같으면(1% 이내) 깔끔한 r_frame_rate
    [InlineData("24000/1001", "60/1", "24000/1001")]          // 많이 다르면 avg_frame_rate (가변 프레임 등)
    [InlineData("0/0", "25/1", "25")]
    [InlineData("25/1", "0/0", "25")]
    [InlineData("2997/100", "30/1", "30")]                    // 0.1% 차이
    [InlineData("29/1", "30/1", "29")]                        // 3.3% 차이
    public void Chooses_Fps(string avg, string r, string expected) =>
        Assert.Equal(expected, FfprobeParser.ChooseFps(avg, r).ToString());

    [Fact]
    public void Skips_Cover_Art_And_Collects_All_Audio()
    {
        string json = """
            { "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "mjpeg", "width": 600, "height": 600, "r_frame_rate": "90000/1", "avg_frame_rate": "0/0",
                  "disposition": { "attached_pic": 1 } },
                { "index": 1, "codec_type": "audio", "codec_name": "dts" },
                { "index": 2, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080, "r_frame_rate": "24/1", "avg_frame_rate": "24/1",
                  "color_space": "unknown" },
                { "index": 3, "codec_type": "audio", "codec_name": "aac" } ],
              "format": { "duration": "N/A" } }
            """;
        var info = FfprobeParser.Parse(json, "movie.mkv");
        Assert.Equal(2, info.VideoStreamIndex);
        Assert.Equal(["dts", "aac"], info.AudioCodecs);
        Assert.Null(info.ColorSpace);
        Assert.Equal(0, info.DurationSeconds);
    }

    [Fact]
    public void Reports_Clear_Errors()
    {
        Assert.Throws<InvalidDataException>(() => FfprobeParser.Parse("""{ "streams": [ { "index": 0, "codec_type": "audio" } ] }""", "a.mp3"));
        Assert.Throws<InvalidDataException>(() => FfprobeParser.Parse("not json", "x"));
        Assert.Throws<InvalidDataException>(() => FfprobeParser.Parse("{}", "x"));
    }
}

public class FfmpegProgressParserTests
{
    [Fact]
    public void Emits_One_Value_Per_Block()
    {
        var parser = new FfmpegProgressParser();
        string[] lines =
        [
            "frame=42", "fps=120.5", "stream_0_0_q=28.0", "bitrate= 812.3kbits/s", "total_size=143360",
            "out_time_us=1401400", "out_time_ms=1401400", "out_time=00:00:01.401400", "dup_frames=0", "drop_frames=0", "speed=4.01x",
        ];
        foreach (var line in lines) Assert.False(parser.Feed(line, out _));

        Assert.True(parser.Feed("progress=continue", out var p));
        Assert.Equal(42, p.Frame);
        Assert.Equal(1.4014, p.OutTime.TotalSeconds, 6);
        Assert.False(p.IsEnd);

        Assert.False(parser.Feed("frame=180", out _));
        Assert.False(parser.Feed("out_time_us=6006000", out _));
        Assert.True(parser.Feed("progress=end", out p));
        Assert.Equal(180, p.Frame);
        Assert.True(p.IsEnd);
    }

    [Fact]
    public void Ignores_Noise()
    {
        var parser = new FfmpegProgressParser();
        Assert.False(parser.Feed(null, out _));
        Assert.False(parser.Feed("", out _));
        Assert.False(parser.Feed("no equals sign", out _));
        Assert.False(parser.Feed("out_time_us=N/A", out _));
        Assert.False(parser.Feed("frame=abc", out _));
        Assert.True(parser.Feed("progress=continue", out var p));
        Assert.Equal(0, p.Frame);
        Assert.Equal(TimeSpan.Zero, p.OutTime);
    }
}

public class FfmpegCommandsTests
{
    private static MediaInfo Info(string? colorSpace = "bt709", string? range = "tv", params string[] audio) => new()
    {
        Path = @"C:\영상 모음\원본 영상.mp4",
        VideoStreamIndex = 0,
        Width = 1280,
        Height = 720,
        CodedWidth = 1280,
        CodedHeight = 720,
        Fps = new Rational(30000, 1001),
        DurationSeconds = 6.006,
        ColorSpace = colorSpace,
        ColorRange = range,
        AudioCodecs = audio,
    };

    private static string After(List<string> args, string option) => args[args.IndexOf(option) + 1];

    [Fact]
    public void Extract_Uses_Fixed_Rate_Crop_And_Rgb()
    {
        var args = FfmpegCommands.ExtractRegions(Info(), [(new PixelRect(944, 0, 336, 192), @"C:\작업\r0\src")], 0, 2);

        Assert.DoesNotContain("-ss", args);
        Assert.Equal("2", After(args, "-t"));
        Assert.True(args.IndexOf("-t") < args.IndexOf("-i"));
        Assert.Equal(@"C:\영상 모음\원본 영상.mp4", After(args, "-i"));
        Assert.Equal("[0:0]fps=30000/1001:start_time=0[s0];[s0]crop=336:192:944:0,scale=in_color_matrix=bt709:in_range=tv,format=rgb24[r0]", After(args, "-filter_complex"));
        Assert.Equal("passthrough", After(args, "-fps_mode"));
        Assert.Equal(@"C:\작업\r0\src\%06d.png", args[^1]);
        Assert.Contains("-progress", args);
        Assert.Contains("-nostats", args);
    }

    [Fact]
    public void Extract_Splits_For_Several_Regions()
    {
        var args = FfmpegCommands.ExtractRegions(Info(colorSpace: null, range: null),
            [(new PixelRect(0, 0, 64, 64), "a"), (new PixelRect(100, 100, 64, 64), "b")], 1.5, null);

        Assert.Equal("1.5", After(args, "-ss"));
        Assert.DoesNotContain("-t", args);
        Assert.Equal("[0:0]fps=30000/1001:start_time=0,split=2[s0][s1];[s0]crop=64:64:0:0,format=rgb24[r0];[s1]crop=64:64:100:100,format=rgb24[r1]", After(args, "-filter_complex"));
        Assert.Equal(2, args.Count(a => a == "-map"));
        Assert.Contains(Path.Combine("a", "%06d.png"), args);
        Assert.Contains(Path.Combine("b", "%06d.png"), args);
    }

    [Fact]
    public void Composite_Overlays_With_Matching_Rate_And_Matrix()
    {
        var args = FfmpegCommands.Composite(Info(audio: "aac"), [(new PixelRect(944, 0, 336, 192), @"C:\작업\r0\out")], null, 2, new EncodeOptions(18), @"C:\out\결과.mp4");

        Assert.Equal("30000/1001", After(args, "-framerate"));
        Assert.Equal(
            "[0:0]fps=30000/1001:start_time=0[b0];[1:v]scale=out_color_matrix=bt709:out_range=tv,format=yuva420p[o0];[b0][o0]overlay=944:0:eof_action=pass:format=yuv420[vout]",
            After(args, "-filter_complex"));
        Assert.Contains("[vout]", args);
        Assert.Contains("0:a?", args);
        Assert.Equal("libx264", After(args, "-c:v"));
        Assert.Equal("18", After(args, "-crf"));
        Assert.Equal("yuv420p", After(args, "-pix_fmt"));
        Assert.Equal("bt709", After(args, "-colorspace"));
        Assert.Equal("copy", After(args, "-c:a"));
        Assert.Equal(@"C:\out\결과.mp4", args[^1]);
    }

    [Fact]
    public void Composite_Chains_Several_Overlays()
    {
        var args = FfmpegCommands.Composite(Info(colorSpace: null, range: null),
            [(new PixelRect(0, 0, 64, 64), "a"), (new PixelRect(100, 200, 64, 64), "b")], null, null, new EncodeOptions(12), "x.mkv");

        Assert.Equal(
            "[0:0]fps=30000/1001:start_time=0[b0];[1:v]format=yuva420p[o0];[b0][o0]overlay=0:0:eof_action=pass:format=yuv420[b1];" +
            "[2:v]format=yuva420p[o1];[b1][o1]overlay=100:200:eof_action=pass:format=yuv420[vout]",
            After(args, "-filter_complex"));
        Assert.Equal(4, args.Count(a => a == "-i")); // 원본 + 프레임 열 2개 + 자막 전용 입력(mkv 출력)
        Assert.Contains("3:s?", args);
        Assert.DoesNotContain("-colorspace", args);
        Assert.DoesNotContain("-c:a", args); // 오디오 없음
        Assert.DoesNotContain("-movflags", args);
    }

    [Fact]
    public void Delogo_Filter()
    {
        var args = FfmpegCommands.Delogo(Info(), [new PixelRect(1040, 40, 190, 56), new PixelRect(10, 10, 20, 20)], null, null, new EncodeOptions(18), "o.mp4");
        Assert.Equal("delogo=x=1040:y=40:w=190:h=56,delogo=x=10:y=10:w=20:h=20", After(args, "-vf"));
        Assert.Contains("+faststart", args);
    }

    [Theory]
    [InlineData("out.mp4", "aac", "copy")]
    [InlineData("out.mp4", "opus", "copy")]
    [InlineData("out.mp4", "pcm_s16le", "aac")]
    [InlineData("out.mp4", "vorbis", "aac")]
    [InlineData("out.mp4", "dts", "aac")]
    [InlineData("out.mkv", "pcm_s16le", "copy")]
    [InlineData("out.mkv", "dts", "copy")]
    [InlineData("OUT.MOV", "wmav2", "aac")]
    public void Audio_Is_Copied_Unless_Container_Cannot_Hold_It(string output, string codec, string expected)
    {
        var args = FfmpegCommands.AudioArguments(Info(audio: codec), output);
        Assert.Equal(expected, args[1]);
        if (expected == "aac") Assert.Equal(["-c:a", "aac", "-b:a", "192k"], args);
    }

    [Fact]
    public void Audio_Transcodes_If_Any_Stream_Is_Incompatible()
    {
        Assert.Equal("aac", FfmpegCommands.AudioArguments(Info(audio: ["aac", "truehd"]), "o.mp4")[1]);
        Assert.Empty(FfmpegCommands.AudioArguments(Info(), "o.mp4"));
    }

    [Theory]
    [InlineData("a.mp4", true)]
    [InlineData("a.MKV", true)]
    [InlineData("a.mov", true)]
    [InlineData("a.webm", false)]
    [InlineData("a.avi", false)]
    [InlineData("a", false)]
    public void Output_Extension_Check(string path, bool ok) => Assert.Equal(ok, FfmpegCommands.IsSupportedOutput(path));
}
