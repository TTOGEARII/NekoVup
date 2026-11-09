using NekoVup.Core.Media;
using NekoVup.Core.Pipeline;
using NekoVup.Core.Processes;
using NekoVup.Core.Regions;
using NekoVup.Core.Settings;

namespace NekoVup.Tests;

public class Video2XTests
{
    [Theory]
    [InlineData(UpscalePreset.Anime, 2, "realesrgan", "--realesrgan-model", "realesr-animevideov3", 2, false)]
    [InlineData(UpscalePreset.Anime, 4, "realesrgan", "--realesrgan-model", "realesr-animevideov3", 4, false)]
    [InlineData(UpscalePreset.AnimeHq, 3, "realcugan", "--realcugan-model", "models-se", 3, false)]
    [InlineData(UpscalePreset.Real, 4, "realesrgan", "--realesrgan-model", "realesrgan-plus", 4, false)]
    [InlineData(UpscalePreset.Real, 2, "realesrgan", "--realesrgan-model", "realesrgan-plus", 4, true)] // 4배 모델뿐 → 키운 뒤 줄인다
    [InlineData(UpscalePreset.Real, 3, "realesrgan", "--realesrgan-model", "realesrgan-plus", 4, true)]
    public void Plans_Preset(UpscalePreset preset, int scale, string processor, string option, string model, int modelScale, bool downscale)
    {
        var plan = Video2X.Plan(preset, scale);
        Assert.Equal((processor, option, model, modelScale, scale), (plan.Processor, plan.ModelOption, plan.Model, plan.ModelScale, plan.FinalScale));
        Assert.Equal(downscale, plan.NeedsDownscale);
    }

    [Fact]
    public void Rejects_Bad_Scale_And_None()
    {
        Assert.Throws<ArgumentException>(() => Video2X.Plan(UpscalePreset.Anime, 1));
        Assert.Throws<ArgumentException>(() => Video2X.Plan(UpscalePreset.Anime, 5));
        Assert.Throws<ArgumentException>(() => Video2X.Plan(UpscalePreset.None, 2));
    }

    [Fact]
    public void Builds_Arguments()
    {
        var args = Video2X.Arguments(Video2X.Plan(UpscalePreset.Anime, 2), @"C:\작업 폴더\cleaned.mkv", @"C:\작업 폴더\upscaled.partial.mkv", 1, new EncodeOptions(18));
        Assert.Equal(
        [
            "-i", @"C:\작업 폴더\cleaned.mkv", "-o", @"C:\작업 폴더\upscaled.partial.mkv",
            "-p", "realesrgan", "--realesrgan-model", "realesr-animevideov3", "-s", "2", "-d", "1",
            "-c", "libx264", "--pix-fmt", "yuv420p", "-e", "crf=18", "-e", "preset=veryfast", "--no-copy-streams",
        ], args);
    }

    [Theory]
    [InlineData("frame=37/184 (20.11%); fps=14.00; elapsed=00:00:05; remaining=00:00:10", 37)]
    [InlineData("\u001b[Kframe=119/120 (99.17%); fps=17.00; elapsed=00:00:07; remaining=00:00:00", 119)] // 실제 출력: 앞에 화면 지움 코드
    [InlineData("  frame=0/184 (0.00%); fps=0.00; elapsed=00:00:03; remaining=00:00:00", 0)]
    public void Parses_Progress_Lines(string line, long expected)
    {
        Assert.True(Video2X.TryParseProgress(line, out long frame));
        Assert.Equal(expected, frame);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[2026-11-09 19:18:43] [info] Video2X version 6.4.0")]
    [InlineData("[info] [FFmpeg] [libx264 @ 0x1] frame=  12 QP=18.00")]
    [InlineData("Total frames processed: 70")]
    [InlineData("frame=abc/10")]
    public void Ignores_Other_Lines(string? line) => Assert.False(Video2X.TryParseProgress(line, out _));

    [Fact]
    public void Strips_Terminal_Codes_For_Logging()
    {
        Assert.Equal("frame=1/2", Video2X.StripAnsi("\u001b[Kframe=1/2"));
        Assert.Equal("plain text", Video2X.StripAnsi("plain text"));
        Assert.Equal("red", Video2X.StripAnsi("\u001b[1;31mred\u001b[0m"));
    }

    [Fact]
    public void Parses_Device_List()
    {
        const string output = "0. NVIDIA GeForce RTX 2070 SUPER\n\tType: Discrete GPU\n\tVulkan API Version: 1.4.325\n\tDriver Version: 591.344.0\n\tDevice ID: 0x1e84\n\n1. Intel(R) UHD Graphics\n\tType: Integrated GPU\n";
        Assert.Equal(["0. NVIDIA GeForce RTX 2070 SUPER", "1. Intel(R) UHD Graphics"], Video2X.ParseDevices(output));
        Assert.Empty(Video2X.ParseDevices("No Vulkan devices found\n"));
    }

    [Theory]
    [InlineData(-1073741819, true)] // 0xC0000005 접근 위반
    [InlineData(-1073740791, true)] // 0xC0000409
    [InlineData(1, false)]
    [InlineData(0, false)]
    [InlineData(139, false)]
    public void Recognises_Native_Crash_Codes(int code, bool crash) => Assert.Equal(crash, JobRunner.IsNativeCrash(code));
}

public class FinalizeCommandTests
{
    private static MediaInfo Info(int width = 720, int height = 480, string? colorSpace = null, params string[] audio) => new()
    {
        Path = @"C:\영상\원본.mkv",
        VideoStreamIndex = 0,
        Width = width,
        Height = height,
        CodedWidth = width,
        CodedHeight = height,
        Fps = new Rational(24000, 1001),
        DurationSeconds = 100,
        ColorSpace = colorSpace,
        ColorPrimaries = colorSpace,
        ColorTransfer = colorSpace,
        AudioCodecs = audio,
    };

    private static string After(List<string> args, string option) => args[args.IndexOf(option) + 1];

    [Fact]
    public void Intermediate_Encode_Is_Video_Only_Without_B_Frames()
    {
        var args = FfmpegCommands.Prepare(Info(audio: "aac"), 300, 5, EncodeOptions.ForIntermediate(), @"C:\작업\cleaned.partial.mkv");

        Assert.Equal("300", After(args, "-ss"));
        Assert.Equal("5", After(args, "-t"));
        Assert.Equal("fps=24000/1001:start_time=0", After(args, "-vf"));
        Assert.Equal("12", After(args, "-crf"));
        Assert.Equal("0", After(args, "-bf")); // Video2X가 B프레임 입력의 끝 프레임을 잃는 문제를 피한다
        Assert.Contains("-an", args);
        Assert.DoesNotContain("-c:a", args);
        Assert.DoesNotContain("0:a?", args);
    }

    [Fact]
    public void Composite_And_Delogo_Honour_Intermediate_Flag()
    {
        var composite = FfmpegCommands.Composite(Info(audio: "aac"), [(new PixelRect(0, 0, 64, 64), "out")], null, null, EncodeOptions.ForIntermediate(), "cleaned.mkv");
        Assert.Contains("-an", composite);
        Assert.Equal("0", After(composite, "-bf"));
        Assert.DoesNotContain("0:s?", composite);

        var delogo = FfmpegCommands.Delogo(Info(), [new PixelRect(10, 10, 20, 20)], null, null, EncodeOptions.ForIntermediate(), "cleaned.mkv");
        Assert.Equal("fps=24000/1001:start_time=0,delogo=x=10:y=10:w=20:h=20", After(delogo, "-vf"));
    }

    [Fact]
    public void Final_Mkv_Keeps_Audio_Subtitles_And_Fonts()
    {
        var args = FfmpegCommands.Composite(Info(audio: "aac"), [(new PixelRect(0, 0, 64, 64), "out")], null, null, new EncodeOptions(18), "결과.mkv");
        Assert.Contains("0:a?", args);
        // 자막·글꼴은 원본을 한 번 더 연 전용 입력에서 가져온다 (0 = 원본, 1 = 합성할 프레임 열, 2 = 원본).
        // 오디오와 같은 입력에서 가져오면 오디오가 영상보다 훨씬 먼저 쓰인 파일이 된다.
        var inputs = Enumerable.Range(0, args.Count).Where(i => args[i] == "-i").Select(i => args[i + 1]).ToList();
        Assert.Equal(3, inputs.Count);
        Assert.Equal(inputs[0], inputs[2]);
        Assert.Contains("2:s?", args);
        Assert.Contains("2:t?", args);
        Assert.DoesNotContain("0:s?", args);
        Assert.Equal("copy", After(args, "-c:s"));
        Assert.DoesNotContain("-bf", args);
        Assert.DoesNotContain("-copypriorss", args); // 처음부터 처리할 때는 필요 없다

        var mp4 = FfmpegCommands.Composite(Info(audio: "aac"), [(new PixelRect(0, 0, 64, 64), "out")], null, null, new EncodeOptions(18), "결과.mp4");
        Assert.Equal(2, mp4.Count(a => a == "-i")); // 자막을 못 담는 형식이면 전용 입력도 열지 않는다
        Assert.DoesNotContain("2:s?", mp4);
        Assert.DoesNotContain("2:t?", mp4);
    }

    [Fact]
    public void Finalize_Copies_Video_And_Takes_Audio_From_The_Source_Section()
    {
        var args = FfmpegCommands.Finalize(Info(audio: "aac"), @"C:\작업\upscaled.mkv", null, 960, 300, 5, new EncodeOptions(18), @"C:\out\결과.mkv");

        // 입력 0 = 업스케일 결과(구간 지정 없음), 입력 1 = 원본(같은 구간, 오디오), 입력 2 = 원본(같은 구간, 자막·글꼴)
        var inputs = Enumerable.Range(0, args.Count).Where(i => args[i] == "-i").ToList();
        Assert.Equal(3, inputs.Count);
        Assert.Equal(@"C:\작업\upscaled.mkv", args[inputs[0] + 1]);
        Assert.Equal(@"C:\영상\원본.mkv", args[inputs[1] + 1]);
        Assert.Equal(@"C:\영상\원본.mkv", args[inputs[2] + 1]);
        Assert.True(inputs[0] < args.IndexOf("-ss") && args.IndexOf("-ss") < inputs[1]);
        Assert.Equal(2, args.Count(a => a == "-ss"));
        Assert.Equal(2, args.Count(a => a == "-t"));
        Assert.Equal("0", After(args, "-copypriorss")); // 구간 시작 이전의 오디오·자막 패킷은 버린다
        Assert.Equal("copy", After(args, "-c:v"));
        Assert.Contains("0:v:0", args);
        Assert.Contains("1:a?", args);
        Assert.Contains("2:s?", args);
        Assert.Contains("2:t?", args);
        Assert.DoesNotContain("1:s?", args);
        Assert.DoesNotContain("-vf", args);
        // 표기 없는 SD 영상을 HD 크기로 키웠으므로 원래 해석(BT.601)을 새긴다
        Assert.Equal("h264_metadata=colour_primaries=6:transfer_characteristics=6:matrix_coefficients=6", After(args, "-bsf:v"));
        Assert.Equal(@"C:\out\결과.mkv", args[^1]);
    }

    [Fact]
    public void Finalize_Downscales_With_Lanczos_When_Model_Scale_Is_Too_Big()
    {
        var args = FfmpegCommands.Finalize(Info(1280, 720, "bt709", "pcm_s16le"), "up4x.mkv", (2560, 1440), 1440, null, null, new EncodeOptions(20, "fast"), "결과.mp4");

        Assert.Equal("scale=2560:1440:flags=lanczos", After(args, "-vf"));
        Assert.Equal("libx264", After(args, "-c:v"));
        Assert.Equal("20", After(args, "-crf"));
        Assert.Equal("fast", After(args, "-preset"));
        Assert.Equal("aac", After(args, "-c:a")); // mp4에 못 담는 오디오는 바꾼다
        Assert.DoesNotContain("2:s?", args);
        Assert.Equal(2, args.Count(a => a == "-i"));
        Assert.Contains("+faststart", args);
        Assert.Equal("h264_metadata=colour_primaries=1:transfer_characteristics=1:matrix_coefficients=1", After(args, "-bsf:v"));
    }

    [Theory]
    [InlineData(480, null, 960, "h264_metadata=colour_primaries=6:transfer_characteristics=6:matrix_coefficients=6")]   // SD → HD, 표기 없음
    [InlineData(576, null, 1152, "h264_metadata=colour_primaries=5:transfer_characteristics=6:matrix_coefficients=6")]  // PAL
    [InlineData(480, null, 480, null)]                                                                                  // 크기가 그대로면 손대지 않는다
    [InlineData(1080, null, 2160, null)]                                                                                // 원래 HD면 재생기 짐작이 그대로다
    [InlineData(480, "bt709", 960, "h264_metadata=colour_primaries=1:transfer_characteristics=1:matrix_coefficients=1")] // 표기가 있으면 그대로 잇는다
    public void Colour_Tags_For_Upscaled_Output(int height, string? colorSpace, int outputHeight, string? expected) =>
        Assert.Equal(expected, ColorTags.H264Metadata(Info(height * 3 / 2, height, colorSpace), outputHeight));

    [Fact]
    public void Full_Range_Is_Flagged()
    {
        var info = Info(1920, 1080, "bt709") with { ColorRange = "pc" };
        Assert.EndsWith(":video_full_range_flag=1", ColorTags.H264Metadata(info, 2160));
    }
}

[Collection("pipeline")]
public class UpscalePipelineTests(SourceFixture fixture)
{
    private const int W = SourceFixture.Width, H = SourceFixture.Height;
    private static readonly PixelRect Box = TestTools.Watermark;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private JobRequest Request(string outputName, JobOptions options, IReadOnlyList<PixelRect>? boxes = null) => new()
    {
        InputPath = fixture.Source,
        OutputPath = Path.Combine(fixture.Dir, outputName),
        Boxes = boxes ?? [],
        Options = options,
    };

    private static JobRunner Runner(IToolRunner runner, List<string>? log = null) =>
        new(TestTools.MockSettings(), runner, new DelegateJobLog((level, message) =>
        {
            if (log is null) return;
            lock (log) log.Add($"{level}: {message}");
        }));

    private static async Task<(int Width, int Height)> SizeAsync(string path)
    {
        var parts = (await TestTools.StreamEntryAsync(path, "v:0", "width,height", Ct)).Split(',');
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    private static string Option(ToolInvocation call, string name) => call.Arguments[call.Arguments.ToList().IndexOf(name) + 1];

    [Fact]
    public async Task Upscale_Only_Doubles_Size_And_Keeps_Frames_And_Audio()
    {
        TestTools.RequireFfmpegAndPython();
        var runner = new RecordingRunner();
        var progress = new ProgressRecorder();
        var log = new List<string>();
        var request = Request("업스케일만.mp4", new JobOptions { Mode = CleanMode.None, Upscale = UpscalePreset.Anime, Scale = 2 });

        var result = await Runner(runner, log).RunAsync(request, progress, Ct);

        Assert.Equal(fixture.Frames, result.FrameCount);
        Assert.Equal((W * 2, H * 2), await SizeAsync(request.OutputPath));
        Assert.Equal(fixture.Frames, await TestTools.CountFramesAsync(request.OutputPath, Ct));
        Assert.Equal("30000/1001", await TestTools.StreamEntryAsync(request.OutputPath, "v:0", "r_frame_rate", Ct));
        Assert.Equal("aac", await TestTools.StreamEntryAsync(request.OutputPath, "a:0", "codec_name", Ct));
        Assert.InRange(await TestTools.DurationAsync(request.OutputPath, Ct), fixture.Seconds - 0.08, fixture.Seconds + 0.08);
        Assert.Equal("bt709", await TestTools.StreamEntryAsync(request.OutputPath, "v:0", "color_space", Ct));

        var call = Assert.Single(runner.Video2XCalls);
        Assert.Equal("realesrgan", Option(call, "-p"));
        Assert.Equal("realesr-animevideov3", Option(call, "--realesrgan-model"));
        Assert.Equal("2", Option(call, "-s"));
        Assert.Contains("crf=18", call.Arguments);
        Assert.Contains("--no-copy-streams", call.Arguments);
        Assert.Empty(runner.ProPainterCalls);

        var stages = progress.Items.Select(p => p.Stage).Distinct().ToList();
        Assert.Equal([JobStage.Probe, JobStage.Prepare, JobStage.Upscale, JobStage.Finalize, JobStage.Finished], stages);
        Assert.Contains(progress.Items, p => p.Stage == JobStage.Upscale && p.StageFraction is > 0.3 and < 0.7); // 진행 줄(ESC[K 포함)을 읽었다
        var overall = progress.Items.Select(p => p.OverallFraction).ToList();
        Assert.True(overall.Zip(overall.Skip(1)).All(pair => pair.Second >= pair.First - 1e-9));
        Assert.False(Directory.Exists(Path.Combine(fixture.Dir, ".nekovup")));
        Assert.Empty(Directory.GetFiles(fixture.Dir, "*.partial.*"));
    }

    [Fact]
    public async Task Accepts_Crash_After_Completion_Like_The_Real_Tool()
    {
        TestTools.RequireFfmpegAndPython();
        // 진짜 Video2X 6.4.0은 "Output written to"까지 찍고 나서 0xC0000005로 죽는다
        var runner = new RecordingRunner(new() { ["NEKOVUP_MOCK_V2X_EXIT"] = "3221225477" });
        var log = new List<string>();
        var request = Request("종료 크래시.mp4", new JobOptions { Mode = CleanMode.None, Upscale = UpscalePreset.Anime, DurationSeconds = 1 });

        var result = await Runner(runner, log).RunAsync(request, null, Ct);

        Assert.Equal(30, result.FrameCount);
        Assert.Equal(30, await TestTools.CountFramesAsync(request.OutputPath, Ct));
        Assert.Contains(log, line => line.Contains("종료 코드 -1073741819"));
    }

    [Fact]
    public async Task Fails_When_Upscaler_Fails_Or_Loses_Frames()
    {
        TestTools.RequireFfmpegAndPython();
        var options = new JobOptions { Mode = CleanMode.None, Upscale = UpscalePreset.Anime, DurationSeconds = 1 };

        var failed = await Assert.ThrowsAsync<ToolFailedException>(() =>
            Runner(new RecordingRunner(new() { ["NEKOVUP_MOCK_V2X_FAIL"] = "1" })).RunAsync(Request("실패.mp4", options) with { WorkDirectory = Path.Combine(fixture.Dir, "업스케일 실패") }, null, Ct));
        Assert.Contains("Video2X", failed.Message);

        var dropped = await Assert.ThrowsAsync<ToolFailedException>(() =>
            Runner(new RecordingRunner(new() { ["NEKOVUP_MOCK_V2X_DROP"] = "2" })).RunAsync(Request("프레임 손실.mp4", options) with { WorkDirectory = Path.Combine(fixture.Dir, "업스케일 손실") }, null, Ct));
        Assert.Contains("기대 30, 실제 28", dropped.Message);

        Assert.False(File.Exists(Path.Combine(fixture.Dir, "실패.mp4")));
        Assert.False(File.Exists(Path.Combine(fixture.Dir, "프레임 손실.mp4")));
    }

    [Fact]
    public async Task Real_Preset_At_2x_Upscales_4x_Then_Shrinks()
    {
        TestTools.RequireFfmpegAndPython();
        var runner = new RecordingRunner();
        var request = Request("실사 2배.mkv", new JobOptions { Mode = CleanMode.None, Upscale = UpscalePreset.Real, Scale = 2, DurationSeconds = 1 });

        await Runner(runner).RunAsync(request, null, Ct);

        var call = Assert.Single(runner.Video2XCalls);
        Assert.Equal("realesrgan-plus", Option(call, "--realesrgan-model"));
        Assert.Equal("4", Option(call, "-s"));
        Assert.Contains("crf=12", call.Arguments); // 한 번 더 인코딩하므로 중간 화질
        Assert.Equal((W * 2, H * 2), await SizeAsync(request.OutputPath));
        Assert.Equal(30, await TestTools.CountFramesAsync(request.OutputPath, Ct));
        Assert.Equal("aac", await TestTools.StreamEntryAsync(request.OutputPath, "a:0", "codec_name", Ct));
    }

    [Fact]
    public async Task Delogo_Then_Upscale()
    {
        TestTools.RequireFfmpegAndPython();
        var request = Request("delogo 업스케일.mp4", new JobOptions { Mode = CleanMode.Delogo, Upscale = UpscalePreset.AnimeHq, Scale = 3, DurationSeconds = 1 }, [Box]);
        var runner = new RecordingRunner();
        var progress = new ProgressRecorder();

        await Runner(runner).RunAsync(request, progress, Ct);

        Assert.Equal("realcugan", Option(Assert.Single(runner.Video2XCalls), "-p"));
        Assert.Equal((W * 3, H * 3), await SizeAsync(request.OutputPath));
        Assert.Equal(30, await TestTools.CountFramesAsync(request.OutputPath, Ct));
        Assert.Equal([JobStage.Probe, JobStage.Delogo, JobStage.Upscale, JobStage.Finalize, JobStage.Finished], progress.Items.Select(p => p.Stage).Distinct());

        // 워터마크 자리가 바뀌었는지 (3배 크기에서 같은 자리)
        var source = await TestTools.DecodeRgbFrameAsync(fixture.Source, 10, fixture.Dir, Ct);
        var output = await TestTools.DecodeRgbFrameAsync(request.OutputPath, 10, fixture.Dir, Ct);
        long sum = 0;
        var inner = Box.Inflate(-8);
        for (int y = inner.Y; y < inner.Bottom; y++)
        for (int x = inner.X; x < inner.Right; x++)
        for (int c = 0; c < 3; c++)
            sum += Math.Abs(source[(y * W + x) * 3 + c] - output[((y * 3 + 1) * W * 3 + x * 3 + 1) * 3 + c]);
        Assert.True((double)sum / (inner.Area * 3) > 8, "워터마크 자리가 바뀌지 않았습니다.");
    }

    [Fact]
    public async Task Inpaint_Then_Upscale_And_Resume_Skips_Finished_Parts()
    {
        TestTools.RequireFfmpegAndPython();
        string work = Path.Combine(fixture.Dir, "복원 업스케일 작업");
        var options = new JobOptions { Upscale = UpscalePreset.Anime, Scale = 2, DurationSeconds = 2 };
        var request = Request("복원 업스케일.mp4", options, [Box]) with { WorkDirectory = work, KeepWorkDirectory = true };

        var first = new RecordingRunner(new() { ["NEKOVUP_MOCK_MODE"] = "identity" });
        var progress = new ProgressRecorder();
        var result = await Runner(first).RunAsync(request, progress, Ct);

        Assert.Equal(60, result.FrameCount);
        Assert.Equal((W * 2, H * 2), await SizeAsync(request.OutputPath));
        Assert.Equal(60, await TestTools.CountFramesAsync(request.OutputPath, Ct));
        Assert.Single(first.ProPainterCalls);
        Assert.Single(first.Video2XCalls);
        Assert.Equal(
            [JobStage.Probe, JobStage.Extract, JobStage.Inpaint, JobStage.Merge, JobStage.Composite, JobStage.Upscale, JobStage.Finalize, JobStage.Finished],
            progress.Items.Select(p => p.Stage).Distinct());

        // 업스케일러 입력은 영상만 있고 B프레임이 없어야 한다 (가짜도 진짜처럼 B프레임 입력의 끝 2장을 버린다)
        string cleaned = Path.Combine(work, "cleaned.mkv");
        Assert.Equal("0", await TestTools.StreamEntryAsync(cleaned, "v:0", "has_b_frames", Ct));
        Assert.Equal("", await TestTools.StreamEntryAsync(cleaned, "a:0", "codec_name", Ct));
        var manifest = JobManifest.TryLoad(work)!;
        Assert.True(manifest.Cleaned);
        Assert.Equal(60, manifest.CleanedFrames);
        Assert.NotEqual("", manifest.UpscaledKey);

        // 같은 요청을 다시: 복원도 업스케일도 건너뛰고 마지막 붙이기만
        var second = new RecordingRunner(new() { ["NEKOVUP_MOCK_MODE"] = "identity" });
        var log = new List<string>();
        File.Delete(request.OutputPath);
        await Runner(second, log).RunAsync(request, null, Ct);
        Assert.Empty(second.ProPainterCalls);
        Assert.Empty(second.Video2XCalls);
        Assert.Empty(second.ExtractCalls);
        Assert.Contains(log, line => line.Contains("업스케일용 중간 영상: 이미 끝나 있어 건너뜁니다"));
        Assert.Contains(log, line => line.Contains("업스케일: 이미 끝나 있어 건너뜁니다"));
        Assert.Equal(60, await TestTools.CountFramesAsync(request.OutputPath, Ct));

        // 배율만 바꾸면: 복원은 건너뛰고 업스케일만 다시
        var third = new RecordingRunner(new() { ["NEKOVUP_MOCK_MODE"] = "identity" });
        await Runner(third).RunAsync(request with { Options = options with { Scale = 3 }, KeepWorkDirectory = false }, null, Ct);
        Assert.Empty(third.ProPainterCalls);
        Assert.Equal("3", Option(Assert.Single(third.Video2XCalls), "-s"));
        Assert.Equal((W * 3, H * 3), await SizeAsync(request.OutputPath));
        Assert.False(Directory.Exists(work));
    }

    [Fact]
    public async Task Mkv_Output_Keeps_Subtitles_And_Mp4_Warns()
    {
        TestTools.RequireFfmpegAndPython();
        // 자막이 든 원본
        string srt = Path.Combine(fixture.Dir, "자막.srt"), source = Path.Combine(fixture.Dir, "자막 든 원본.mkv");
        await File.WriteAllTextAsync(srt, "1\n00:00:00,200 --> 00:00:00,800\n안녕하세요\n\n", Ct);
        await TestTools.Ffmpeg_(Ct, "-t", "1", "-i", fixture.Source, "-i", srt, "-map", "0:v", "-map", "0:a", "-map", "1:s", "-c", "copy", "-c:s", "srt", source);
        var options = new JobOptions { Mode = CleanMode.None, Upscale = UpscalePreset.Anime };

        string mkv = Path.Combine(fixture.Dir, "자막 유지.mkv"), mp4 = Path.Combine(fixture.Dir, "자막 없음.mp4");
        var log = new List<string>();
        await Runner(new RecordingRunner()).RunAsync(new JobRequest { InputPath = source, OutputPath = mkv, Boxes = [], Options = options }, null, Ct);
        await Runner(new RecordingRunner(), log).RunAsync(new JobRequest { InputPath = source, OutputPath = mp4, Boxes = [], Options = options }, null, Ct);

        Assert.Equal("subrip", await TestTools.StreamEntryAsync(mkv, "s:0", "codec_name", Ct));
        Assert.Equal("", await TestTools.StreamEntryAsync(mp4, "s:0", "codec_name", Ct));
        Assert.Contains(log, line => line.StartsWith("Warning") && line.Contains("자막"));

        // delogo만 하는 경로도 mkv면 자막을 옮긴다
        string delogo = Path.Combine(fixture.Dir, "자막 delogo.mkv");
        await Runner(new RecordingRunner()).RunAsync(new JobRequest { InputPath = source, OutputPath = delogo, Boxes = [Box], Options = new JobOptions { Mode = CleanMode.Delogo } }, null, Ct);
        Assert.Equal("subrip", await TestTools.StreamEntryAsync(delogo, "s:0", "codec_name", Ct));
    }

    [Fact]
    public async Task Audio_And_Video_Stay_Interleaved_When_The_Source_Has_Sparse_Subtitles()
    {
        TestTools.RequireFfmpegAndPython();
        // 자막이 처음과 끝에만 있는 원본. 자막을 오디오와 같은 입력에서 가져오면, 영상이 다른 입력에 걸린 경로(업스케일·합성)에서
        // ffmpeg가 다음 자막을 기다리는 동안 오디오만 먼저 써 버린다 → 재생이 멈칫거리다 몰아서 지나간다.
        // 자막 사이가 10초(ffmpeg가 한쪽 스트림을 기다려 주는 한도)를 넘어야 드러나므로 16초짜리 작은 영상을 따로 만든다.
        string srt = Path.Combine(fixture.Dir, "드문 자막.srt"), source = Path.Combine(fixture.Dir, "드문 자막 원본.mkv");
        await File.WriteAllTextAsync(srt, "1\n00:00:00,300 --> 00:00:00,900\n처음\n\n2\n00:00:15,000 --> 00:00:15,500\n끝\n\n", Ct);
        await TestTools.Ffmpeg_(Ct, "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30000/1001:duration=16", "-f", "lavfi", "-i", "sine=frequency=440:duration=16",
            "-i", srt, "-map", "0:v", "-map", "1:a", "-map", "2:s", "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-c:s", "srt", source);
        Assert.InRange(await TestTools.MaxInterleaveGapAsync(source, Ct), 0, 1);
        int frames = await TestTools.CountFramesAsync(source, Ct);

        var box = new PixelRect(240, 12, 48, 24);
        var cases = new (string Name, JobOptions Options, PixelRect[] Boxes)[]
        {
            ("섞임 업스케일.mkv", new JobOptions { Mode = CleanMode.None, Upscale = UpscalePreset.Anime }, []),
            ("섞임 복원.mkv", new JobOptions { Region = new RegionOptions { Pad = 16 } }, [box]),
            ("섞임 delogo.mkv", new JobOptions { Mode = CleanMode.Delogo }, [box]),
        };
        foreach (var (name, options, boxes) in cases)
        {
            string output = Path.Combine(fixture.Dir, name);
            var runner = new RecordingRunner(new() { ["NEKOVUP_MOCK_MODE"] = "identity" });
            await Runner(runner).RunAsync(new JobRequest { InputPath = source, OutputPath = output, Boxes = boxes, Options = options }, null, Ct);

            Assert.True(await TestTools.MaxInterleaveGapAsync(output, Ct) < 1, name + ": 오디오와 영상이 떨어져 쓰였습니다.");
            // 다시 담은 mkv는 마지막 프레임의 길이가 달라져 fps 필터가 끝에 한 장을 더 채울 수 있다
            Assert.InRange(await TestTools.CountFramesAsync(output, Ct), frames, frames + 1);
            Assert.Equal("2", await TestTools.Ffprobe(Ct, "-count_packets", "-select_streams", "s:0", "-show_entries", "stream=nb_read_packets", "-of", "csv=p=0", output));
        }
    }

    [Fact]
    public async Task Native_Crash_Of_Inpainter_Is_Retried()
    {
        TestTools.RequireFfmpegAndPython();
        string marker = Path.Combine(fixture.Dir, "crash-once.txt");
        var runner = new RecordingRunner(new() { ["NEKOVUP_MOCK_MODE"] = "identity", ["NEKOVUP_MOCK_CRASH_ONCE"] = marker });
        var log = new List<string>();
        var request = Request("재시도.mp4", new JobOptions { DurationSeconds = 1 }, [Box]);

        var result = await Runner(runner, log).RunAsync(request, null, Ct);

        Assert.Equal(30, result.FrameCount);
        Assert.Equal(2, runner.ProPainterCalls.Count); // 한 번 죽고, 다시 해서 성공
        Assert.Contains(log, line => line.StartsWith("Warning") && line.Contains("0xC0000409"));
        Assert.Equal(30, await TestTools.CountFramesAsync(request.OutputPath, Ct));
    }

    [Fact]
    public async Task Validates_Upscale_Requests()
    {
        TestTools.RequireFfmpeg();

        async Task<string> Fails(JobRequest request, AppSettings? settings = null) =>
            (await Assert.ThrowsAsync<JobException>(() =>
                new JobRunner(settings ?? TestTools.MockSettings(), new RecordingRunner(), NullJobLog.Instance).RunAsync(request, null, Ct))).Message;

        Assert.Contains("할 일이 없습니다", await Fails(Request("x.mp4", new JobOptions { Mode = CleanMode.None })));
        Assert.Contains("배율", await Fails(Request("x.mp4", new JobOptions { Mode = CleanMode.None, Upscale = UpscalePreset.Anime, Scale = 8 })));
        Assert.Contains("Video2X", await Fails(Request("x.mp4", new JobOptions { Mode = CleanMode.None, Upscale = UpscalePreset.Anime }), new AppSettings()));
        Assert.Contains("Video2X", await Fails(Request("x.mp4", new JobOptions { Mode = CleanMode.None, Upscale = UpscalePreset.Anime }), new AppSettings { Video2X = @"C:\없음\video2x.exe" }));
        Assert.False(Directory.Exists(Path.Combine(fixture.Dir, ".nekovup")));
    }
}
