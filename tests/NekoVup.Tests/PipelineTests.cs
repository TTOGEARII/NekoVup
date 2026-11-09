using NekoVup.Core.Pipeline;
using NekoVup.Core.Processes;
using NekoVup.Core.Regions;
using NekoVup.Core.Settings;

namespace NekoVup.Tests;

/// <summary>테스트 영상과 비교 기준(같은 설정으로 그냥 다시 인코딩한 것)을 한 번만 만든다.</summary>
public sealed class SourceFixture : IAsyncLifetime
{
    public const int Width = 1280, Height = 720;

    /// <summary>실제 프레임 수와 길이 (오디오 길이에 맞춰 잘리므로 6초 × fps보다 한 장 적을 수 있다).</summary>
    public int Frames { get; private set; }

    public double Seconds { get; private set; }

    public string Dir { get; private set; } = "";
    public string Source { get; private set; } = "";
    public string Plain { get; private set; } = "";
    public string SourceLuma { get; private set; } = "";
    public string PlainLuma { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        if (!TestTools.HasFfmpeg) return;
        var ct = TestContext.Current.CancellationToken;
        Dir = TestTools.NewTempDir("파이프라인");
        Source = Path.Combine(Dir, "원본 영상.mp4");
        Plain = Path.Combine(Dir, "plain.mp4");
        await TestTools.MakeSourceAsync(Source, ct);
        Frames = await TestTools.CountFramesAsync(Source, ct);
        Seconds = await TestTools.DurationAsync(Source, ct);
        await TestTools.Ffmpeg_(ct, "-i", Source, "-c:v", "libx264", "-crf", "18", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-c:a", "copy", Plain);
        SourceLuma = await TestTools.DecodeLumaAsync(Source, Path.Combine(Dir, "source.y"), ct);
        PlainLuma = await TestTools.DecodeLumaAsync(Plain, Path.Combine(Dir, "plain.y"), ct);
    }

    public ValueTask DisposeAsync()
    {
        if (Dir.Length > 0) TestTools.DeleteDir(Dir);
        return ValueTask.CompletedTask;
    }
}

/// <summary>실행된 명령을 기록하고, 가짜 ProPainter에 환경 변수를 넘기는 실행기.</summary>
internal sealed class RecordingRunner(Dictionary<string, string>? mockEnvironment = null) : IToolRunner
{
    private readonly List<ToolInvocation> _invocations = [];

    public Dictionary<string, string> MockEnvironment { get; } = mockEnvironment ?? [];

    public IReadOnlyList<ToolInvocation> Invocations
    {
        get { lock (_invocations) return [.. _invocations]; }
    }

    public IReadOnlyList<ToolInvocation> ProPainterCalls => [.. Invocations.Where(i => i.Arguments.Count > 0 && i.Arguments[0].EndsWith(JobRunner.ProPainterScript, StringComparison.Ordinal))];
    public IReadOnlyList<ToolInvocation> Video2XCalls => [.. Invocations.Where(i => i.FileName.EndsWith("video2x_mock.py", StringComparison.Ordinal))];
    public IReadOnlyList<ToolInvocation> ExtractCalls => [.. Invocations.Where(i => i.Arguments.Contains("-fps_mode"))];

    public Task<ToolResult> RunAsync(ToolInvocation invocation, Action<string>? onOutput, Action<string>? onError, CancellationToken cancellationToken)
    {
        lock (_invocations) _invocations.Add(invocation);
        // 가짜 Video2X는 파이썬 스크립트다: 실행 파일 자리에 스크립트가 오면 python으로 돌린다.
        if (invocation.FileName.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        {
            invocation = new ToolInvocation("python", [invocation.FileName, .. invocation.Arguments])
            {
                WorkingDirectory = invocation.WorkingDirectory,
                Environment = new Dictionary<string, string> { ["PYTHONUTF8"] = "1", ["PYTHONIOENCODING"] = "utf-8" },
            };
        }
        if (invocation.Environment is { } env && MockEnvironment.Count > 0)
        {
            var merged = new Dictionary<string, string>(env);
            foreach (var (key, value) in MockEnvironment) merged[key] = value;
            invocation = invocation with { Environment = merged };
        }
        return TestTools.Runner.RunAsync(invocation, onOutput, onError, cancellationToken);
    }
}

internal sealed class ProgressRecorder : IProgress<JobProgress>
{
    private readonly List<JobProgress> _items = [];

    public IReadOnlyList<JobProgress> Items
    {
        get { lock (_items) return [.. _items]; }
    }

    public void Report(JobProgress value)
    {
        lock (_items) _items.Add(value);
    }
}

/// <summary>실제 도구를 돌리는 테스트들은 같은 원본·같은 가짜 ProPainter 폴더를 쓰므로 한 줄로 세워 차례로 돌린다.</summary>
[CollectionDefinition("pipeline")]
public sealed class PipelineCollection : ICollectionFixture<SourceFixture>;

[Collection("pipeline")]
public class PipelineTests(SourceFixture fixture)
{
    private const int W = SourceFixture.Width, H = SourceFixture.Height;
    private static readonly PixelRect Box = TestTools.Watermark;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string> Mode(string mode) => new() { ["NEKOVUP_MOCK_MODE"] = mode };

    private JobRequest Request(string outputName, JobOptions? options = null, IReadOnlyList<PixelRect>? boxes = null) => new()
    {
        InputPath = fixture.Source,
        OutputPath = Path.Combine(fixture.Dir, outputName),
        Boxes = boxes ?? [Box],
        Options = options ?? new JobOptions(),
    };

    private static JobRunner Runner(IToolRunner runner, List<string>? log = null) =>
        new(TestTools.MockSettings(), runner, new DelegateJobLog((level, message) =>
        {
            if (log is null) return;
            lock (log) log.Add($"{level}: {message}");
        }));

    /// <summary>프레임 수·fps·길이·오디오가 원본과 같은지.</summary>
    private static async Task AssertSameShapeAsync(string output, int frames, double seconds, string audio = "aac")
    {
        Assert.True(File.Exists(output), "출력 파일이 없습니다.");
        Assert.Equal(frames, await TestTools.CountFramesAsync(output, Ct));
        Assert.Equal("30000/1001", await TestTools.StreamEntryAsync(output, "v:0", "r_frame_rate", Ct));
        Assert.Equal("h264", await TestTools.StreamEntryAsync(output, "v:0", "codec_name", Ct));
        Assert.Equal("yuv420p", await TestTools.StreamEntryAsync(output, "v:0", "pix_fmt", Ct));
        Assert.Equal(audio, await TestTools.StreamEntryAsync(output, "a:0", "codec_name", Ct));
        Assert.InRange(await TestTools.DurationAsync(output, Ct), seconds - 0.08, seconds + 0.08);
    }

    /// <summary>워터마크 둘레 바깥 화질이 "그냥 다시 인코딩한 것"보다 0.1dB 넘게 나쁘지 않은지.</summary>
    private async Task AssertOutsideUntouchedAsync(string output, params PixelRect[] changed)
    {
        bool Outside(int x, int y)
        {
            foreach (var r in changed)
                if (x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom) return false;
            return true;
        }

        string luma = await TestTools.DecodeLumaAsync(output, Path.Combine(fixture.Dir, Path.GetFileNameWithoutExtension(output) + ".y"), Ct);
        var (plain, _) = TestTools.Psnr(fixture.SourceLuma, fixture.PlainLuma, W, H, Outside);
        var (ours, frames) = TestTools.Psnr(fixture.SourceLuma, luma, W, H, Outside);
        File.Delete(luma);

        Assert.Equal(fixture.Frames, frames);
        Assert.True(ours >= plain - 0.1, $"마스크 밖 PSNR이 떨어졌습니다: 결과 {ours:0.00} dB, 단순 재인코딩 {plain:0.00} dB");
    }

    /// <summary>복원 결과가 얹히는 최대 범위: 박스 + 마스크 키움 + 알파 퍼짐.</summary>
    private static PixelRect Touched(PixelRect box, double resizeRatio = 1.0) =>
        box.Inflate(Core.Imaging.MaskBuilder.AlphaReach(2, 4, resizeRatio) + 2);

    // ───────────────────────── 빠른 모드 ─────────────────────────

    [Fact]
    public async Task Delogo_Mode_Removes_Box_And_Keeps_Everything_Else()
    {
        TestTools.RequireFfmpeg();
        var request = Request("delogo 결과.mp4", new JobOptions { Mode = CleanMode.Delogo });
        var progress = new ProgressRecorder();

        var result = await Runner(new RecordingRunner()).RunAsync(request, progress, Ct);

        Assert.Equal(request.OutputPath, result.OutputPath);
        Assert.Null(result.WorkDirectory);
        await AssertSameShapeAsync(request.OutputPath, fixture.Frames, fixture.Seconds);
        await AssertOutsideUntouchedAsync(request.OutputPath, Box.Inflate(8));

        var source = await TestTools.DecodeRgbFrameAsync(fixture.Source, 30, fixture.Dir, Ct);
        var output = await TestTools.DecodeRgbFrameAsync(request.OutputPath, 30, fixture.Dir, Ct);
        Assert.True(TestTools.MeanAbsDiff(source, output, W, Box.Inflate(-8)) > 8, "워터마크 자리가 바뀌지 않았습니다.");

        Assert.Contains(progress.Items, p => p.Stage == JobStage.Delogo && p.StageFraction > 0.5);
        Assert.Equal(JobStage.Finished, progress.Items[^1].Stage);
        Assert.Equal(1.0, progress.Items[^1].OverallFraction);
        Assert.Empty(Directory.GetFiles(fixture.Dir, "*.partial.*"));
    }

    [Fact]
    public async Task Audio_Is_Reencoded_Only_When_Container_Needs_It()
    {
        TestTools.RequireFfmpeg();
        string source = Path.Combine(fixture.Dir, "pcm 오디오.mkv");
        await TestTools.MakeSourceAsync(source, Ct, seconds: 2, audioCodec: "pcm_s16le");
        var options = new JobOptions { Mode = CleanMode.Delogo, DurationSeconds = 1 };

        string mp4 = Path.Combine(fixture.Dir, "pcm→mp4.mp4"), mkv = Path.Combine(fixture.Dir, "pcm→mkv.mkv");
        await Runner(new RecordingRunner()).RunAsync(new JobRequest { InputPath = source, OutputPath = mp4, Boxes = [Box], Options = options }, null, Ct);
        await Runner(new RecordingRunner()).RunAsync(new JobRequest { InputPath = source, OutputPath = mkv, Boxes = [Box], Options = options }, null, Ct);

        Assert.Equal("aac", await TestTools.StreamEntryAsync(mp4, "a:0", "codec_name", Ct));
        Assert.Equal("pcm_s16le", await TestTools.StreamEntryAsync(mkv, "a:0", "codec_name", Ct));
        Assert.Equal(30, await TestTools.CountFramesAsync(mp4, Ct));
    }

    // ───────────────────────── ProPainter 모드 (가짜 스크립트) ─────────────────────────

    [Fact]
    public async Task Identity_Inpainter_Reproduces_The_Source()
    {
        TestTools.RequireFfmpegAndPython();
        var runner = new RecordingRunner(Mode("identity"));
        var request = Request("identity 결과.mp4");
        var progress = new ProgressRecorder();
        var log = new List<string>();

        var result = await Runner(runner, log).RunAsync(request, progress, Ct);

        Assert.Equal(fixture.Frames, result.FrameCount);
        await AssertSameShapeAsync(request.OutputPath, fixture.Frames, fixture.Seconds);
        await AssertOutsideUntouchedAsync(request.OutputPath, Touched(Box));

        // 복원기가 "아무것도 안 함"이면 마스크 안도 원본과 같아야 한다 → 프레임 순서·색 변환(왕복)이 맞다는 뜻
        foreach (int frame in new[] { 0, 95, fixture.Frames - 1 })
        {
            var plain = await TestTools.DecodeRgbFrameAsync(fixture.Plain, frame, fixture.Dir, Ct);
            var output = await TestTools.DecodeRgbFrameAsync(request.OutputPath, frame, fixture.Dir, Ct);
            double diff = TestTools.MeanAbsDiff(plain, output, W, Box);
            Assert.True(diff < 4, $"프레임 {frame}: 마스크 안이 원본과 다릅니다 (평균 오차 {diff:0.00})");
        }

        // ProPainter 호출 규칙
        var calls = runner.ProPainterCalls;
        Assert.Equal(2, calls.Count); // 약 180장 → [0,100) + [90,끝)
        Assert.All(calls, call =>
        {
            Assert.Equal(TestTools.MockProPainterDir, call.WorkingDirectory);
            Assert.Equal("1", call.Environment!["PYTHONUTF8"]);
            Assert.Equal("utf-8", call.Environment!["PYTHONIOENCODING"]);
            Assert.Contains("--save_frames", call.Arguments);
            Assert.Contains("--fp16", call.Arguments);
            Assert.Equal("40", call.Arguments[call.Arguments.ToList().IndexOf("--subvideo_length") + 1]);
            Assert.DoesNotContain("--resize_ratio", call.Arguments);
            // 한글 경로를 못 읽는 OpenCV 때문에 경로는 ProPainter 폴더 기준 상대 경로(영문만)로 넘긴다
            foreach (string option in new[] { "-i", "-m", "-o" })
            {
                string value = call.Arguments[call.Arguments.ToList().IndexOf(option) + 1];
                Assert.True(value.All(char.IsAscii) && !Path.IsPathRooted(value), $"{option} {value}");
            }
        });

        // 진행률: 단계가 순서대로, 전체 진행률은 뒤로 가지 않는다
        var stages = progress.Items.Select(p => p.Stage).Distinct().ToList();
        Assert.Equal([JobStage.Probe, JobStage.Extract, JobStage.Inpaint, JobStage.Merge, JobStage.Composite, JobStage.Finished], stages);
        var items = progress.Items;
        for (int i = 1; i < items.Count; i++)
        {
            Assert.True(items[i].OverallFraction >= items[i - 1].OverallFraction - 1e-9,
                $"전체 진행률이 뒤로 갔습니다: {items[i - 1].Stage} {items[i - 1].StageFraction:0.000} ({items[i - 1].OverallFraction:0.0000}) → {items[i].Stage} {items[i].StageFraction:0.000} ({items[i].OverallFraction:0.0000})");
        }

        // 성공하면 중간 파일은 남지 않는다 (ProPainter 폴더 안의 임시 폴더 포함)
        Assert.False(Directory.Exists(Path.Combine(TestTools.MockProPainterDir, JobRunner.ProPainterScratch)));
        Assert.Null(result.WorkDirectory);
        Assert.False(Directory.Exists(Path.Combine(fixture.Dir, ".nekovup")));
        Assert.Contains(log, line => line.Contains("처리 영역 1"));
    }

    [Fact]
    public async Task Inpainter_Output_Replaces_Watermark_Area_Only()
    {
        TestTools.RequireFfmpegAndPython();
        var request = Request("inpaint 결과.mkv");

        await Runner(new RecordingRunner(Mode("inpaint"))).RunAsync(request, null, Ct);

        await AssertSameShapeAsync(request.OutputPath, fixture.Frames, fixture.Seconds);
        await AssertOutsideUntouchedAsync(request.OutputPath, Touched(Box));
        var source = await TestTools.DecodeRgbFrameAsync(fixture.Source, 60, fixture.Dir, Ct);
        var output = await TestTools.DecodeRgbFrameAsync(request.OutputPath, 60, fixture.Dir, Ct);
        Assert.True(TestTools.MeanAbsDiff(source, output, W, Box.Inflate(-8)) > 8, "워터마크 자리가 바뀌지 않았습니다.");
    }

    [Fact]
    public async Task Large_Region_Is_Processed_Shrunken_And_Scaled_Back()
    {
        TestTools.RequireFfmpegAndPython();
        var runner = new RecordingRunner(Mode("identity"));
        var options = new JobOptions { Region = new RegionOptions { MaxArea = 20_000 } }; // 영역 336x192 = 64,512px → 약 0.557배
        var request = Request("축소 처리.mp4", options);

        await Runner(runner).RunAsync(request, null, Ct);

        var call = runner.ProPainterCalls[0];
        string ratio = call.Arguments[call.Arguments.ToList().IndexOf("--resize_ratio") + 1];
        Assert.Equal("0.5567", ratio);

        await AssertSameShapeAsync(request.OutputPath, fixture.Frames, fixture.Seconds);
        await AssertOutsideUntouchedAsync(request.OutputPath, Touched(Box, 0.5567));
        // 마스크 안은 줄였다 늘린 화면이라 흐리지만, 같은 장면이어야 한다
        var plain = await TestTools.DecodeRgbFrameAsync(fixture.Plain, 95, fixture.Dir, Ct);
        var output = await TestTools.DecodeRgbFrameAsync(request.OutputPath, 95, fixture.Dir, Ct);
        Assert.True(TestTools.MeanAbsDiff(plain, output, W, Box) < 25);
    }

    [Fact]
    public async Task Two_Separate_Regions_On_Untagged_Source()
    {
        TestTools.RequireFfmpegAndPython();
        var second = new PixelRect(60, 600, 120, 60);
        string source = Path.Combine(fixture.Dir, "색 표기 없음.mp4"), plain = Path.Combine(fixture.Dir, "색 표기 없음 plain.mp4");
        await TestTools.MakeSourceAsync(source, Ct, tagged: false, seconds: 2, secondBox: second);
        await TestTools.Ffmpeg_(Ct, "-i", source, "-c:v", "libx264", "-crf", "18", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-c:a", "copy", plain);

        var runner = new RecordingRunner(Mode("identity"));
        string output = Path.Combine(fixture.Dir, "두 영역.mp4");
        var result = await Runner(runner).RunAsync(new JobRequest { InputPath = source, OutputPath = output, Boxes = [second, Box] }, null, Ct);

        int frames = await TestTools.CountFramesAsync(source, Ct);
        Assert.Equal(frames, result.FrameCount);
        Assert.Equal(2, runner.ProPainterCalls.Count); // 영역 2개 × 조각 1개
        Assert.Single(runner.ExtractCalls);            // 추출은 한 번에
        Assert.Equal(frames, await TestTools.CountFramesAsync(output, Ct));

        var expected = await TestTools.DecodeRgbFrameAsync(plain, 30, fixture.Dir, Ct);
        var actual = await TestTools.DecodeRgbFrameAsync(output, 30, fixture.Dir, Ct);
        Assert.True(TestTools.MeanAbsDiff(expected, actual, W, Box) < 4, "첫 영역 색이 틀어졌습니다.");
        Assert.True(TestTools.MeanAbsDiff(expected, actual, W, second) < 4, "둘째 영역 색이 틀어졌습니다.");
        Assert.True(TestTools.MeanAbsDiff(expected, actual, W, new PixelRect(400, 300, 300, 200)) < 4);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1.98)] // 프레임 경계 사이에서 시작
    public async Task Frames_Stay_Aligned_Across_Chunks_And_Odd_Start_Times(double? start)
    {
        TestTools.RequireFfmpegAndPython();
        // 화면 전체가 옆으로 흘러가는 영상을 쓴다. 추출·조각·병합·합성 중 어디서든 한 장만 밀려도 "워터마크" 자리에서 바로 드러난다.
        string source = Path.Combine(fixture.Dir, "흘러가는 영상.mp4");
        if (!File.Exists(source)) await TestTools.MakeSourceAsync(source, Ct, seconds: 5, scroll: true);
        var moving = new PixelRect(600, 300, 190, 56);
        var options = new JobOptions { StartSeconds = start, DurationSeconds = 2, ChunkLength = 25, Overlap = 5 }; // 60장 → 조각 3개
        var request = Request($"정렬 {start ?? 0}.mp4", options, [moving]) with { InputPath = source };
        string reference = Path.Combine(fixture.Dir, $"정렬 기준 {start ?? 0}.mp4");
        string[] seek = start is { } s ? ["-ss", s.ToString(System.Globalization.CultureInfo.InvariantCulture)] : [];
        await TestTools.Ffmpeg_(Ct, [.. seek, "-t", "2", "-i", source, "-vf", "fps=30000/1001:start_time=0",
            "-c:v", "libx264", "-crf", "18", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-c:a", "copy", reference]);

        var runner = new RecordingRunner(Mode("identity"));
        var result = await Runner(runner).RunAsync(request, null, Ct);

        int frames = await TestTools.CountFramesAsync(reference, Ct);
        Assert.InRange(frames, 59, 61);
        Assert.Equal(frames, result.FrameCount);
        Assert.Equal(frames, await TestTools.CountFramesAsync(request.OutputPath, Ct));
        Assert.Equal(3, runner.ProPainterCalls.Count);

        // 영상과 오디오가 모두 0초 근처에서 시작하고 길이가 구간(2초)과 같아야 한다 (앞에 딸려 온 오디오로 영상이 밀리면 안 된다)
        Assert.InRange(await TestTools.FirstTimestampAsync(request.OutputPath, "v:0", Ct), -0.001, 0.05);
        Assert.InRange(await TestTools.FirstTimestampAsync(request.OutputPath, "a:0", Ct), -0.03, 0.08);
        Assert.InRange(await TestTools.DurationAsync(request.OutputPath, Ct), 1.9, 2.15);

        // 조각 안쪽(첫 장, 30), 겹치는 구간(22, 42), 끝(frames - 2).
        // 프레임 사이에서 시작하면 ffmpeg가 시각 0을 첫 프레임의 복제로 채우므로(0번 = 1번) 그때는 1번부터 본다.
        int firstFrame = start is null ? 0 : 1;
        foreach (int frame in new[] { firstFrame, 22, 30, 42, frames - 2 })
        {
            var expected = await TestTools.DecodeRgbFrameAsync(reference, frame, fixture.Dir, Ct);
            var next = await TestTools.DecodeRgbFrameAsync(reference, frame + 1, fixture.Dir, Ct);
            var actual = await TestTools.DecodeRgbFrameAsync(request.OutputPath, frame, fixture.Dir, Ct);
            double same = TestTools.MeanAbsDiff(expected, actual, W, moving);
            double shifted = TestTools.MeanAbsDiff(next, actual, W, moving);
            Assert.True(shifted > 4, $"프레임 {frame}: 이 자리는 움직임이 있어야 검증이 됩니다 ({shifted:0.00})");
            Assert.True(same < 4 && same * 2 < shifted, $"프레임 {frame}: 원본과 다릅니다 (평균 오차 {same:0.00}, 한 장 밀렸을 때 {shifted:0.00})");
        }
    }

    [Fact]
    public async Task Failed_Job_Resumes_Without_Redoing_Finished_Chunks()
    {
        TestTools.RequireFfmpegAndPython();
        var options = new JobOptions { ChunkLength = 60, Overlap = 10 }; // 약 180장 → 조각 4개
        string work = Path.Combine(fixture.Dir, "재개 작업 폴더");
        var request = Request("재개.mp4", options) with { WorkDirectory = work };

        // 1차: 세 번째 조각에서 ProPainter가 죽는다
        var first = new RecordingRunner(new() { ["NEKOVUP_MOCK_MODE"] = "identity", ["NEKOVUP_MOCK_FAIL_AT"] = "c0002" });
        var failure = await Assert.ThrowsAsync<ToolFailedException>(() => Runner(first).RunAsync(request, null, Ct));
        Assert.Contains("일부러 낸 오류", failure.Message);
        Assert.Equal(3, first.ProPainterCalls.Count);
        Assert.Single(first.ExtractCalls);
        Assert.False(File.Exists(request.OutputPath));
        Assert.Empty(Directory.GetFiles(fixture.Dir, "*.partial.*"));

        var manifest = JobManifest.TryLoad(work);
        Assert.NotNull(manifest);
        Assert.True(manifest.Extracted);
        Assert.Equal(fixture.Frames, manifest.FrameCount);
        Assert.True(File.Exists(Path.Combine(work, "job.log")));

        // 2차: 같은 요청 → 추출과 끝난 조각 2개는 건너뛰고 나머지 2개만
        var second = new RecordingRunner(Mode("identity"));
        var log = new List<string>();
        var result = await Runner(second, log).RunAsync(request, null, Ct);

        Assert.Empty(second.ExtractCalls);
        Assert.Equal(["c0002", "c0003"], second.ProPainterCalls.Select(c => Path.GetFileName(c.Arguments[c.Arguments.ToList().IndexOf("-i") + 1])));
        Assert.Contains(log, line => line.Contains("건너뜁니다"));
        Assert.Equal(fixture.Frames, result.FrameCount);
        await AssertSameShapeAsync(request.OutputPath, fixture.Frames, fixture.Seconds);
        await AssertOutsideUntouchedAsync(request.OutputPath, Touched(Box));
        Assert.False(Directory.Exists(work));
    }

    [Fact]
    public async Task Cancel_Stops_The_Inpainter_And_Leaves_No_Partial_Output()
    {
        TestTools.RequireFfmpegAndPython();
        string pidFile = Path.Combine(fixture.Dir, "pids.txt");
        var runner = new RecordingRunner(new() { ["NEKOVUP_MOCK_MODE"] = "identity", ["NEKOVUP_MOCK_DELAY"] = "0.5", ["NEKOVUP_MOCK_PIDFILE"] = pidFile });
        var request = Request("취소.mp4") with { WorkDirectory = Path.Combine(fixture.Dir, "취소 작업 폴더") };

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = Runner(runner).RunAsync(request, null, cancel.Token);
        for (int i = 0; i < 300 && !(File.Exists(pidFile) && File.ReadAllLines(pidFile).Length >= 2); i++) await Task.Delay(100, Ct);
        var pids = File.ReadAllLines(pidFile).Select(int.Parse).ToArray();
        Assert.All(pids, pid => Assert.True(TestTools.IsAlive(pid)));

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        for (int i = 0; i < 50 && pids.Any(TestTools.IsAlive); i++) await Task.Delay(100, Ct);
        Assert.All(pids, pid => Assert.False(TestTools.IsAlive(pid), $"프로세스 {pid}가 남아 있습니다."));
        Assert.False(File.Exists(request.OutputPath));
        Assert.Empty(Directory.GetFiles(fixture.Dir, "*.partial.*"));
    }

    // ───────────────────────── 입력 검증 ─────────────────────────

    [Fact]
    public async Task Probe_Reads_Real_File()
    {
        TestTools.RequireFfmpeg();
        var info = await Runner(new RecordingRunner()).ProbeAsync(fixture.Source, Ct);
        Assert.Equal((W, H), (info.Width, info.Height));
        Assert.Equal("30000/1001", info.Fps.ToString());
        Assert.Equal("bt709", info.ColorSpace);
        Assert.Equal(["aac"], info.AudioCodecs);
        Assert.InRange(info.EstimateFrames(), fixture.Frames - 1, fixture.Frames + 1);
    }

    [Fact]
    public async Task Bad_Requests_Are_Rejected_With_Readable_Messages()
    {
        TestTools.RequireFfmpeg();
        var runner = Runner(new RecordingRunner());

        async Task<string> Fails(JobRequest request, AppSettings? settings = null)
        {
            var target = settings is null ? runner : new JobRunner(settings, new RecordingRunner(), NullJobLog.Instance);
            return (await Assert.ThrowsAsync<JobException>(() => target.RunAsync(request, null, Ct))).Message;
        }

        Assert.Contains("확장자", await Fails(Request("결과.webm")));
        Assert.Contains("입력 파일과 같습니다", await Fails(Request("x.mp4") with { OutputPath = fixture.Source }));
        Assert.Contains("박스", await Fails(Request("x.mp4", boxes: [])));
        Assert.Contains("입력 파일이 없습니다", await Fails(Request("x.mp4") with { InputPath = Path.Combine(fixture.Dir, "없는 파일.mp4") }));
        Assert.Contains("시작 지점", await Fails(Request("x.mp4", new JobOptions { StartSeconds = 100 })));
        Assert.Contains("프레임 안에", await Fails(Request("x.mp4", boxes: [new PixelRect(5000, 5000, 10, 10)])));
        Assert.Contains("파이썬", await Fails(Request("x.mp4"), new AppSettings { ProPainterDir = TestTools.MockProPainterDir }));
        Assert.Contains("ProPainter 폴더", await Fails(Request("x.mp4"), new AppSettings { Python = "python", ProPainterDir = fixture.Dir }));
        Assert.Contains("겹침", await Fails(Request("x.mp4", new JobOptions { ChunkLength = 10, Overlap = 8 })));

        string text = Path.Combine(fixture.Dir, "영상 아님.mp4");
        await File.WriteAllTextAsync(text, "이건 영상이 아니다", Ct);
        Assert.Contains("영상 정보를 읽지 못했습니다", await Fails(Request("x.mp4") with { InputPath = text }));
        Assert.False(File.Exists(Path.Combine(fixture.Dir, "x.mp4")));
    }

    [Fact]
    public async Task Work_Directory_From_Another_Job_Is_Refused()
    {
        TestTools.RequireFfmpegAndPython();
        string work = Path.Combine(fixture.Dir, "남의 작업 폴더");
        Directory.CreateDirectory(work);
        new JobManifest { JobKey = "다른 작업" }.Save(work);

        var ex = await Assert.ThrowsAsync<JobException>(() =>
            Runner(new RecordingRunner(Mode("identity"))).RunAsync(Request("y.mp4") with { WorkDirectory = work }, null, Ct));
        Assert.Contains("다른 작업의 기록", ex.Message);
    }

    [Fact]
    public void Temp_Space_Estimate_Is_Area_Times_Frames_Times_Four()
    {
        var regions = RegionPlanner.Plan([Box], W, H);
        Assert.Equal(336L * 192 * 180 * 4, JobRunner.EstimateTempBytes(regions, 180));
    }

    [Fact]
    public void Job_Key_Changes_With_Anything_That_Changes_Intermediate_Results()
    {
        var request = new JobRequest { InputPath = @"C:\없는\a.mp4", OutputPath = @"C:\없는\b.mp4", Boxes = [Box] };
        string key = JobManifest.ComputeKey(request);

        Assert.Equal(key, JobManifest.ComputeKey(request));
        Assert.Equal(key, JobManifest.ComputeKey(request with { OutputPath = @"C:\없는\c.mkv", Options = new JobOptions { Crf = 30 } })); // 최종 화질은 무관
        Assert.NotEqual(key, JobManifest.ComputeKey(request with { Boxes = [Box.Offset(1, 0)] }));
        Assert.NotEqual(key, JobManifest.ComputeKey(request with { Options = new JobOptions { ChunkLength = 80 } }));
        Assert.NotEqual(key, JobManifest.ComputeKey(request with { Options = new JobOptions { DurationSeconds = 5 } }));
        Assert.NotEqual(key, JobManifest.ComputeKey(request with { Options = new JobOptions { Region = new RegionOptions { Pad = 64 } } }));
        Assert.NotEqual(key, JobManifest.ComputeKey(request with { InputPath = @"C:\없는\other.mp4" }));
    }
}

public class SettingsTests
{
    [Fact]
    public void Round_Trips_Through_Json_File()
    {
        string dir = TestTools.NewTempDir("설정");
        try
        {
            string path = Path.Combine(dir, "하위 폴더", "settings.json");
            Assert.Equal("ffmpeg", SettingsStore.Load(path).Ffmpeg); // 없으면 기본값

            SettingsStore.Save(new AppSettings { Python = @"D:\ProPainter\venv\Scripts\python.exe", ProPainterDir = @"D:\ProPainter 폴더", Video2X = @"D:\v2x\video2x.exe" }, path);
            var loaded = SettingsStore.Load(path);

            Assert.Equal(@"D:\ProPainter\venv\Scripts\python.exe", loaded.Python);
            Assert.Equal(@"D:\ProPainter 폴더", loaded.ProPainterDir);
            Assert.Equal(@"D:\v2x\video2x.exe", loaded.Video2X);
            Assert.Equal("ffprobe", loaded.Ffprobe);
            Assert.Contains("\"proPainterDir\"", File.ReadAllText(path));

            File.WriteAllText(path, "{ 깨진 json");
            Assert.Equal("ffmpeg", SettingsStore.Load(path).Ffmpeg);
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Environment_Check_Reports_Each_Tool()
    {
        TestTools.RequireFfmpegAndPython();
        var ct = TestContext.Current.CancellationToken;

        var good = await new EnvironmentChecker(new AppSettings { Python = "python", ProPainterDir = TestTools.MockProPainterDir }, TestTools.Runner).RunAsync(Path.GetTempPath(), ct);
        Assert.Equal(CheckStatus.Ok, good.Single(i => i.Name == "ffmpeg").Status);
        Assert.StartsWith("ffmpeg version", good.Single(i => i.Name == "ffmpeg").Detail);
        Assert.Equal(CheckStatus.Ok, good.Single(i => i.Name == "ffprobe").Status);
        Assert.Equal(CheckStatus.Warning, good.Single(i => i.Name == "ProPainter").Status); // 가짜 폴더에는 가중치가 없다
        Assert.Contains("ProPainter.pth", good.Single(i => i.Name == "ProPainter").Detail);
        Assert.Equal(CheckStatus.Warning, good.Single(i => i.Name == "Video2X").Status);
        Assert.Contains(good, i => i.Name == "디스크 여유 공간");

        var bad = await new EnvironmentChecker(
            new AppSettings { Ffmpeg = "없는-ffmpeg.exe", Python = "python", ProPainterDir = Path.GetTempPath(), Video2X = @"C:\없음\video2x.exe" },
            TestTools.Runner).RunAsync(null, ct);
        Assert.Equal(CheckStatus.Error, bad.Single(i => i.Name == "ffmpeg").Status);
        Assert.Equal(CheckStatus.Error, bad.Single(i => i.Name == "ProPainter").Status);
        Assert.Equal(CheckStatus.Error, bad.Single(i => i.Name == "Video2X").Status);
        Assert.DoesNotContain(bad, i => i.Name == "디스크 여유 공간");
    }
}
