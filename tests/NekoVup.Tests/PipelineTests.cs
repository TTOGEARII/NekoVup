using NekoVup.Core.Chunks;
using NekoVup.Core.Pipeline;
using NekoVup.Core.Processes;
using NekoVup.Core.Regions;
using NekoVup.Core.Runtime;
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
    public IReadOnlyList<ToolInvocation> RealBasicVsrCalls => [.. Invocations.Where(i => i.Arguments.Count > 0 && i.Arguments[0].EndsWith("realbasicvsr.py", StringComparison.Ordinal))];
    public IReadOnlyList<ToolInvocation> ExtractCalls => [.. Invocations.Where(i => i.Arguments.Contains("-fps_mode"))];

    public Task<ToolResult> RunAsync(ToolInvocation invocation, Action<string>? onOutput, Action<string>? onError, CancellationToken cancellationToken)
    {
        lock (_invocations) _invocations.Add(invocation);
        // 진짜 RealBasicVSR 스크립트는 torch가 있어야 돈다 — 테스트에서는 같은 인자를 받는 가짜 스크립트로 바꿔 돌린다.
        if (invocation.Arguments.Count > 0 && invocation.Arguments[0].EndsWith("realbasicvsr.py", StringComparison.Ordinal))
            invocation = invocation with { Arguments = [Path.Combine(TestTools.MockProPainterDir, "realbasicvsr_mock.py"), .. invocation.Arguments.Skip(1)] };
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

/// <summary>도구를 끝까지 돌린 뒤, 지정한 파일을 쓴 명령이면 실패한 것으로 알리는 실행기.</summary>
internal sealed class FailAfterWriting(string path) : IToolRunner
{
    public async Task<ToolResult> RunAsync(ToolInvocation invocation, Action<string>? onOutput, Action<string>? onError, CancellationToken cancellationToken)
    {
        var result = await TestTools.Runner.RunAsync(invocation, onOutput, onError, cancellationToken);
        if (invocation.Arguments.Contains(path)) throw new IOException("쓰다가 실패했습니다");
        return result;
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
    public async Task Result_Is_Kept_Under_Another_Name_When_The_Output_File_Is_In_Use()
    {
        TestTools.RequireFfmpeg();
        var request = Request("열어 둔 결과.mp4", new JobOptions { Mode = CleanMode.Delogo, DurationSeconds = 1 });
        File.WriteAllText(request.OutputPath, "앞서 만든 결과");
        string second = Path.Combine(fixture.Dir, "열어 둔 결과 (2).mp4");

        JobResult result;
        // 재생기가 앞서 만든 결과를 열어 두고 있다: 덮어쓸 수 없다
        using (new FileStream(request.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = await Runner(new RecordingRunner()).RunAsync(request, null, Ct);

        // 다 만든 결과를 버리지 않고 옆에 둔다
        Assert.Equal(second, result.OutputPath);
        Assert.True(new FileInfo(second).Length > 1000);
        Assert.Equal("앞서 만든 결과", File.ReadAllText(request.OutputPath));
        Assert.Empty(Directory.GetFiles(fixture.Dir, "*.partial.*"));
    }

    [Fact]
    public async Task Box_Outside_The_Frame_Is_Skipped_With_A_Warning()
    {
        TestTools.RequireFfmpeg();
        var log = new List<string>();
        var request = Request("화면 밖 박스.mp4", new JobOptions { Mode = CleanMode.Delogo, DurationSeconds = 1 }, [Box, new PixelRect(5000, 5000, 10, 10)]);

        await Runner(new RecordingRunner(), log).RunAsync(request, null, Ct);

        // 남은 박스로 작업은 끝난다. 빠진 박스를 알려야 워터마크가 왜 남았는지 안다.
        Assert.Single(log, line => line.StartsWith("Warning", StringComparison.Ordinal) && line.Contains("(5000,5000) 10x10") && line.Contains("화면(1280x720) 밖"));
        Assert.Equal(30, await TestTools.CountFramesAsync(request.OutputPath, Ct));
    }

    [Fact]
    public async Task Finished_Result_Left_Under_The_Partial_Name_Survives_A_Run_That_Fails_Before_Rewriting_It()
    {
        TestTools.RequireFfmpegAndPython();
        // 앞선 실행이 완성본을 제자리에 옮기지 못하면 "이름.partial.확장자"로 남겨 둔다.
        var request = Request("남은 결과.mp4") with { WorkDirectory = Path.Combine(fixture.Dir, "남은 결과 작업 폴더") };
        string partial = Path.Combine(fixture.Dir, "남은 결과.partial.mp4");
        File.WriteAllText(partial, "앞서 끝낸 결과");
        try
        {
            string work = JobRunner.JobWorkDirectory(request, JobManifest.ComputeKey(request));
            Directory.CreateDirectory(work);
            new JobManifest { JobKey = "다른 작업" }.Save(work);

            // 이번 실행은 그 파일에 손대기 전에 실패한다 → 지우면 안 된다
            await Assert.ThrowsAsync<JobException>(() => Runner(new RecordingRunner(Mode("identity"))).RunAsync(request, null, Ct));
            Assert.Equal("앞서 끝낸 결과", File.ReadAllText(partial));
        }
        finally
        {
            File.Delete(partial); // 다른 테스트가 "남은 .partial 파일이 없다"를 확인한다
        }
    }

    [Fact]
    public async Task Partial_File_This_Run_Rewrote_Is_Removed_When_The_Run_Fails()
    {
        TestTools.RequireFfmpeg();
        var request = Request("다시 쓰다 실패.mp4", new JobOptions { Mode = CleanMode.Delogo, DurationSeconds = 1 });
        string partial = Path.Combine(fixture.Dir, "다시 쓰다 실패.partial.mp4");
        File.WriteAllText(partial, "앞서 끝낸 결과");
        try
        {
            // 이번 실행이 같은 이름에 새로 쓴 뒤 실패했다 → 남은 것은 쓰다 만 파일이므로 지운다
            await Assert.ThrowsAsync<IOException>(() => Runner(new FailAfterWriting(partial)).RunAsync(request, null, Ct));
            Assert.False(File.Exists(partial));
            Assert.False(File.Exists(request.OutputPath));
        }
        finally
        {
            File.Delete(partial);
        }
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
        // 지정한 폴더 안에 작업별 폴더가 생긴다. 그 폴더에 원래 있던 파일은 끝까지 그대로여야 한다.
        string root = Path.Combine(fixture.Dir, "재개 작업 폴더");
        Directory.CreateDirectory(root);
        string mine = Path.Combine(root, "내 파일.txt");
        await File.WriteAllTextAsync(mine, "지우면 안 됨", Ct);
        var request = Request("재개.mp4", options) with { WorkDirectory = root };
        string work = JobRunner.JobWorkDirectory(request, JobManifest.ComputeKey(request));
        Assert.Equal(root, Path.GetDirectoryName(work));

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
        Assert.Equal("지우면 안 됨", await File.ReadAllTextAsync(mine, Ct));
    }

    [Fact]
    public async Task Custom_Work_Folder_Is_Removed_After_Success_Only_When_Empty()
    {
        TestTools.RequireFfmpegAndPython();
        string root = Path.Combine(fixture.Dir, "빈 작업 폴더");
        var request = Request("빈 작업 폴더.mp4", new JobOptions { DurationSeconds = 1 }) with { WorkDirectory = root };

        var result = await Runner(new RecordingRunner(Mode("identity"))).RunAsync(request, null, Ct);

        Assert.Null(result.WorkDirectory);
        Assert.False(Directory.Exists(root)); // 우리가 만든 폴더이고 비었으므로 지운다

        // 큰 드라이브로 돌려 둔 정션은 비어도 남긴다 — 지우면 다음 작업은 원래 드라이브에 쓰게 된다
        string target = Path.Combine(fixture.Dir, "큰 드라이브 대신");
        string junction = Path.Combine(fixture.Dir, "작업 정션");
        Directory.CreateDirectory(target);
        TestTools.MakeJunction(junction, target);
        try
        {
            await Runner(new RecordingRunner(Mode("identity"))).RunAsync(request with { WorkDirectory = junction }, null, Ct);
            Assert.True(Directory.Exists(junction));
            Assert.Empty(Directory.GetFileSystemEntries(target)); // 작업 폴더는 지웠다
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction); // 정션만 지운다
            TestTools.DeleteDir(target);
        }
    }

    [Fact]
    public void Job_Work_Folder_Name_Is_Safe_For_Long_And_Emoji_Names()
    {
        var request = new JobRequest { InputPath = @"C:\영상\" + new string('가', 39) + "😀나머지.mp4", OutputPath = @"D:\결과\out.mp4", Boxes = [Box] };
        string work = JobRunner.JobWorkDirectory(request, "0123456789abcdef");

        Assert.Equal(@"D:\결과\.nekovup", Path.GetDirectoryName(work));
        // 40번째 자리에 두 칸짜리 글자(이모지)가 걸리면 그 앞에서 자른다 — 반쪽만 남으면 폴더를 만들 수 없다
        Assert.Equal(new string('가', 39) + "-0123456789", Path.GetFileName(work));
        Assert.Equal(@"E:\작업\a-0123456789", JobRunner.JobWorkDirectory(request with { InputPath = @"C:\a.mp4", WorkDirectory = @"E:\작업" }, "0123456789abcdef"));
    }

    [Fact]
    public void Work_Folder_Stays_Out_Of_OneDrive()
    {
        // 출력 폴더가 OneDrive 안이면 중간 PNG 수십 GB가 동기화된다 — 밖에 둔다. --work로 직접 정한 곳은 그대로 쓴다.
        string[] sync = [@"C:\Users\나\OneDrive"];
        string fallback = @"D:\NekoVup-work";
        string? asked = null;
        string Fallback(string name) { asked = name; return fallback; }
        var request = new JobRequest { InputPath = @"C:\a.mp4", OutputPath = @"C:\Users\나\OneDrive\바탕 화면\a [nekoVup].mkv", Boxes = [Box] };

        Assert.Equal(@"D:\NekoVup-work\a-0123456789", JobRunner.JobWorkDirectory(request, "0123456789abcdef", sync, Fallback));
        Assert.Equal("a-0123456789", asked); // 이어 하기: 이 작업의 폴더가 있는 드라이브를 찾게 이름을 넘긴다
        Assert.Equal(@"C:\Users\나\OneDrive2\.nekovup\a-0123456789",
            JobRunner.JobWorkDirectory(request with { OutputPath = @"C:\Users\나\OneDrive2\a.mkv" }, "0123456789abcdef", sync, Fallback));
        Assert.Equal(@"E:\작업\a-0123456789",
            JobRunner.JobWorkDirectory(request with { WorkDirectory = @"E:\작업" }, "0123456789abcdef", sync, Fallback));

        Assert.True(SyncFolders.Contains(@"C:\Users\나\OneDrive", sync));
        Assert.True(SyncFolders.Contains(@"c:\users\나\onedrive\바탕 화면\영상", sync));
        Assert.False(SyncFolders.Contains(@"C:\Users\나\OneDrive2\영상", sync));
        Assert.False(SyncFolders.Contains(@"D:\영상", sync));
        Assert.False(SyncFolders.Contains("상대 경로", sync));
    }

    [Theory]
    [InlineData("yuv420p", false)]
    [InlineData("yuvj420p", false)]
    [InlineData("nv12", false)]
    [InlineData("yuv420p10le", true)]
    [InlineData("yuv422p12le", true)]
    [InlineData("p010le", true)]
    [InlineData(null, false)]
    public void High_Bit_Depth_Is_Detected_From_Pixel_Format(string? pixelFormat, bool expected) =>
        Assert.Equal(expected, JobRunner.IsHighBitDepth(pixelFormat));

    [Fact]
    public async Task Corrupt_Inpainted_Frame_Is_Reported_As_A_Job_Error()
    {
        // 병렬 병합 중에 난 오류는 한 묶음(AggregateException)으로 온다 — 화면이 다룰 수 있는 오류로 풀려 나와야 한다.
        string root = Path.Combine(fixture.Dir, "깨진 프레임"), frames = Path.Combine(root, "in", "c0000", "frames");
        Directory.CreateDirectory(frames);
        for (int i = 0; i < 4; i++) await File.WriteAllTextAsync(Path.Combine(frames, ChunkMerger.ChunkFrameName(i)), "PNG가 아니다", Ct);
        var chunks = ChunkPlanner.Plan(4, 100, 10);

        var ex = Assert.Throws<JobException>(() => ChunkMerger.Merge(Path.Combine(root, "in"), chunks, Path.Combine(root, "out"), 16, 16, new byte[256], null, Ct));
        Assert.Contains("복원된 프레임", ex.Message);

        // 파일이 아예 없으면 파일 오류 그대로 (이것도 화면이 다루는 종류다)
        foreach (var file in Directory.GetFiles(frames)) File.Delete(file);
        Assert.ThrowsAny<IOException>(() => ChunkMerger.Merge(Path.Combine(root, "in"), chunks, Path.Combine(root, "out"), 16, 16, new byte[256], null, Ct));
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
        // 같은 파일을 다르게 적어도 알아본다 (정션을 거친 경로) — 글자만 비교하면 결과가 원본을 덮어쓴다
        string links = TestTools.NewTempDir("정션");
        string junction = Path.Combine(links, "원본 폴더");
        try
        {
            TestTools.MakeJunction(junction, Path.GetDirectoryName(fixture.Source)!);
            Assert.Contains("입력 파일과 같습니다", await Fails(Request("x.mp4") with { OutputPath = Path.Combine(junction, Path.GetFileName(fixture.Source)) }));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction); // 정션만 지운다 (가리키는 폴더는 그대로)
            TestTools.DeleteDir(links);
        }
        // 입력 이름이 "출력.partial.확장자"와 같으면 처리 중에 입력을 덮어쓰게 된다
        string partialNamed = Path.Combine(fixture.Dir, "덮어쓰기.partial.mp4");
        File.Copy(fixture.Source, partialNamed, overwrite: true);
        try
        {
            Assert.Contains("임시 파일 이름", await Fails(Request("덮어쓰기.mp4") with { InputPath = partialNamed }));
            Assert.Equal(new FileInfo(fixture.Source).Length, new FileInfo(partialNamed).Length);
        }
        finally
        {
            File.Delete(partialNamed); // 다른 테스트가 "남은 .partial 파일이 없다"를 확인한다
        }
        Assert.Contains("박스", await Fails(Request("x.mp4", boxes: [])));
        Assert.Contains("입력 파일이 없습니다", await Fails(Request("x.mp4") with { InputPath = Path.Combine(fixture.Dir, "없는 파일.mp4") }));
        Assert.Contains("시작 지점", await Fails(Request("x.mp4", new JobOptions { StartSeconds = 100 })));
        // 숫자 옵션은 도구를 돌리기 전에 걸러낸다 (길이 0은 ffmpeg에서 "제한 없음"이 되어 영상 전체를 처리하게 된다)
        foreach (double bad in new[] { 0, -1, double.NaN, double.PositiveInfinity })
            Assert.Contains("처리할 길이", await Fails(Request("x.mp4", new JobOptions { DurationSeconds = bad })));
        foreach (double bad in new[] { -0.5, double.NaN, double.NegativeInfinity })
            Assert.Contains("시작 지점은 0 이상", await Fails(Request("x.mp4", new JobOptions { StartSeconds = bad })));
        foreach (int bad in new[] { -1, 52 })
            Assert.Contains("화질(CRF)", await Fails(Request("x.mp4", new JobOptions { Crf = bad })));
        Assert.Contains("인코딩 속도(preset)", await Fails(Request("x.mp4", new JobOptions { Preset = "빠르게" })));
        Assert.Contains("프레임 안에", await Fails(Request("x.mp4", boxes: [new PixelRect(5000, 5000, 10, 10)])));
        Assert.Contains("파이썬", await Fails(Request("x.mp4"), new AppSettings { ProPainterDir = TestTools.MockProPainterDir }));
        Assert.Contains("ProPainter 폴더", await Fails(Request("x.mp4"), new AppSettings { Python = "python", ProPainterDir = fixture.Dir }));
        Assert.Contains("겹침", await Fails(Request("x.mp4", new JobOptions { ChunkLength = 10, Overlap = 8 })));
        // ProPainter 숫자 옵션도 추출 전에, 한국어로 알린다 (.NET 기본 메시지는 영어)
        Assert.Contains("subvideo", await Fails(Request("x.mp4", new JobOptions { SubvideoLength = 0 })));
        Assert.Contains("여백(pad)", await Fails(Request("x.mp4", new JobOptions { Region = new RegionOptions { Pad = -1 } })));
        Assert.Contains("최대 넓이", await Fails(Request("x.mp4", new JobOptions { Region = new RegionOptions { MaxArea = 0 } })));
        Assert.Contains("조각 길이(chunk)", await Fails(Request("x.mp4", new JobOptions { ChunkLength = 1 })));
        Assert.Contains("조각 겹침(overlap)", await Fails(Request("x.mp4", new JobOptions { Overlap = -1 })));
        // Windows가 받지 않는 이름은 복원을 다 끝낸 뒤 마지막 인코딩에서야 실패한다 — 시작 전에 거른다
        foreach (string bad in new[] { "제목?.mp4", "a*b.mkv", "t|1.mov", "a<b>.mp4", "a\"b.mp4", "스트림.mp4:숨김.mp4" })
            Assert.Contains("쓸 수 없는 글자", await Fails(Request(bad)));
        Assert.Contains("쓸 수 없는 글자", await Fails(Request("x.mp4") with { OutputPath = Path.Combine(fixture.Dir, "폴더?", "x.mp4") }));
        // 장치 이름은 확장자를 붙여도 장치로 열린다 — "확장자가 틀렸다"가 아니라 이름이 문제라고 알린다
        foreach (string bad in new[] { "CON.mp4", "nul.mkv", "Com1.mov", "lpt9 .mp4", "aux", "CONOUT$.mp4", "COM¹.mkv" })
            Assert.Contains("장치 이름", await Fails(Request(bad)));
        foreach (string fine in new[] { @"C:\x\CONSOLE.mp4", @"C:\x\con2.mkv", @"C:\COM1 폴더\x.mp4", @"\\서버\공유\NUL 아님.mp4" })
            Assert.False(JobRunner.IsReservedName(fine), fine);
        // 드라이브·공유 경로 앞부분의 글자(: \\?\)나 한글·공백·괄호·%·&·'는 걸지 않는다
        foreach (string fine in new[] { @"C:\영상 (1)\50% & 'x'.mp4", @"\\서버\공유\a b.mkv", @"\\?\C:\긴 경로\x.mp4", @"\\?\UNC\서버\공유\x.mov" })
            Assert.False(JobRunner.HasUnusableName(fine), fine);

        string text = Path.Combine(fixture.Dir, "영상 아님.mp4");
        await File.WriteAllTextAsync(text, "이건 영상이 아니다", Ct);
        Assert.Contains("영상 정보를 읽지 못했습니다", await Fails(Request("x.mp4") with { InputPath = text }));
        Assert.False(File.Exists(Path.Combine(fixture.Dir, "x.mp4")));
    }

    [Fact]
    public async Task Output_Folder_That_Refuses_Files_Is_Caught_Before_Any_Work()
    {
        TestTools.RequireFfmpeg();
        // 일반 권한으로 본 C:\ 바로 아래처럼 폴더는 만들어도 파일은 못 만드는 곳. 예전에는 작업 폴더를 만들어 다 처리한 뒤 마지막 인코딩에서야 실패했다.
        string dir = TestTools.NewTempDir("파일 금지");
        try
        {
            var deny = await TestTools.Runner.RunAsync(new ToolInvocation("icacls", [dir, "/deny", "*S-1-1-0:(WD)"]), null, null, Ct);
            Assert.Equal(0, deny.ExitCode);
            var runner = new RecordingRunner();

            var error = await Assert.ThrowsAsync<JobException>(() =>
                Runner(runner).RunAsync(Request("x.mp4") with { OutputPath = Path.Combine(dir, "결과.mp4") }, null, Ct));

            Assert.Contains("출력 폴더에 파일을 만들 수 없습니다", error.Message);
            Assert.DoesNotContain("관리자 권한", error.Message); // 드라이브 바로 아래가 아니다
            Assert.All(runner.Invocations, call => Assert.Contains("ffprobe", call.FileName, StringComparison.OrdinalIgnoreCase));
            Assert.Empty(Directory.GetFileSystemEntries(dir)); // .nekovup 작업 폴더도, 확인용 파일도 남지 않는다

            // 없는 드라이브: 권한이 아니라 드라이브를 찾을 수 없다고 알린다
            string missing = Enumerable.Range('D', 23).Select(c => $"{(char)c}:\\").First(d => !Directory.Exists(d));
            var gone = await Assert.ThrowsAsync<JobException>(() =>
                Runner(new RecordingRunner()).RunAsync(Request("x.mp4") with { OutputPath = Path.Combine(missing, "영상", "결과.mp4") }, null, Ct));
            Assert.Contains("드라이브나 위쪽 폴더를 찾을 수 없습니다", gone.Message);
        }
        finally
        {
            await TestTools.Runner.RunAsync(new ToolInvocation("icacls", [dir, "/remove:d", "*S-1-1-0"]), null, null, Ct);
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Work_Directory_From_Another_Job_Is_Refused()
    {
        TestTools.RequireFfmpegAndPython();
        var request = Request("y.mp4") with { WorkDirectory = Path.Combine(fixture.Dir, "남의 작업 폴더") };
        string work = JobRunner.JobWorkDirectory(request, JobManifest.ComputeKey(request));
        Directory.CreateDirectory(work);
        new JobManifest { JobKey = "다른 작업" }.Save(work);

        var ex = await Assert.ThrowsAsync<JobException>(() => Runner(new RecordingRunner(Mode("identity"))).RunAsync(request, null, Ct));
        Assert.Contains("다른 작업의 기록", ex.Message);
        Assert.NotNull(JobManifest.TryLoad(work)); // 남의 기록은 건드리지 않는다
    }

    [Fact]
    public void Temp_Space_Estimate_Is_Area_Times_Frames_Times_Four()
    {
        var regions = RegionPlanner.Plan([Box], W, H);
        Assert.Equal(336L * 192 * 180 * 4, JobRunner.EstimateTempBytes(regions, 180));
    }

    [Fact]
    public void ProPainter_Scratch_Folder_Is_Separate_For_Each_Work_Directory()
    {
        // ProPainter 폴더 안의 임시 자리를 작업 폴더마다 따로 쓴다 — 두 작업을 동시에 돌려도 서로의 조각을 지우지 않는다.
        string name = JobRunner.ScratchName(@"D:\작업\.nekovup\영상-0123456789");

        Assert.Equal(12, name.Length);
        Assert.Equal(name, JobRunner.ScratchName(@"d:\작업\.NEKOVUP\영상-0123456789"));              // 같은 폴더를 대소문자만 다르게
        Assert.Equal(name, JobRunner.ScratchName(@"D:\작업\.nekovup\다른 폴더\..\영상-0123456789"));
        Assert.NotEqual(name, JobRunner.ScratchName(@"D:\작업\.nekovup\영상-abcdef0123"));
        Assert.NotEqual(name, JobRunner.ScratchName(@"E:\작업\.nekovup\영상-0123456789"));
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

            // 손으로 고치다 null을 적어 둔 칸은 기본값으로 메운다 (그대로 두면 쓰는 곳마다 오류가 난다)
            File.WriteAllText(path, """{ "ffmpeg": null, "ffprobe": null, "python": null, "proPainterDir": null, "video2X": null, "theme": null }""");
            loaded = SettingsStore.Load(path);
            Assert.Equal(("ffmpeg", "ffprobe", "", "", "", "Dark"), (loaded.Ffmpeg, loaded.Ffprobe, loaded.Python, loaded.ProPainterDir, loaded.Video2X, loaded.Theme));
            File.WriteAllText(path, "null");
            Assert.Equal("ffmpeg", SettingsStore.Load(path).Ffmpeg);

            // 읽지 못한 파일은 까닭을 알리고 옆에 남겨 둔다 — 기본값으로 다시 저장되면 적어 둔 경로가 아무 말 없이 사라진다
            string broken = """{ "python": "D:\ProPainter\venv\Scripts\python.exe" }"""; // 역슬래시를 하나만 적었다
            File.WriteAllText(path, broken);
            var defaults = SettingsStore.Load(path, out string? error);
            Assert.Equal("", defaults.Python);
            Assert.NotNull(error);
            Assert.Contains(".bad", error);
            Assert.Equal(broken, File.ReadAllText(path + ".bad"));
            Assert.Equal(broken, File.ReadAllText(path)); // 읽기만 해서는 원래 파일을 건드리지 않는다

            // 손으로 고치며 흔히 넣는 주석·끝 쉼표는 받아 준다. 없는 파일은 오류가 아니다.
            File.WriteAllText(path, "{\n  // 내 파이썬\n  \"python\": \"D:\\\\py\\\\python.exe\",\n}");
            Assert.Equal(@"D:\py\python.exe", SettingsStore.Load(path, out error).Python);
            Assert.Null(error);
            Assert.Equal("ffmpeg", SettingsStore.Load(Path.Combine(dir, "없는 설정.json"), out error).Ffmpeg);
            Assert.Null(error);
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public void Save_Leaves_No_Temp_File_Behind()
    {
        string dir = TestTools.NewTempDir("설정 임시");
        try
        {
            string path = Path.Combine(dir, "settings.json");
            SettingsStore.Save(new AppSettings(), path);
            SettingsStore.Save(new AppSettings { Theme = "Light" }, path);
            Assert.Equal([path], Directory.GetFileSystemEntries(dir));
            Assert.Equal("Light", SettingsStore.Load(path).Theme);

            // 저장하지 못했을 때도 쓰다 만 파일을 남기지 않는다 (여기서는 그 이름이 폴더라서 옮기지 못한다).
            string blocked = Path.Combine(dir, "막힌 이름.json");
            Directory.CreateDirectory(blocked);
            Assert.ThrowsAny<Exception>(() => SettingsStore.Save(new AppSettings(), blocked));
            Assert.Equal([path, blocked], Directory.GetFileSystemEntries(dir).Order(StringComparer.Ordinal));
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public void Bundled_Tools_Fill_Empty_Default_And_Missing_Paths_Only()
    {
        string dir = TestTools.NewTempDir("내장 도구");
        try
        {
            string bundled = Path.Combine(dir, "tools", "ffmpeg", "ffmpeg.exe");
            string mine = Path.Combine(dir, "내 ffmpeg.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
            File.WriteAllBytes(bundled, []);
            File.WriteAllBytes(mine, []);

            Assert.Equal(bundled, BundledTools.Pick("", "ffmpeg", bundled));                        // 비어 있음
            Assert.Equal(bundled, BundledTools.Pick("ffmpeg", "ffmpeg", bundled));                  // 기본 이름 (PATH 의존)
            Assert.Equal(bundled, BundledTools.Pick(" FFMPEG ", "ffmpeg", bundled));
            Assert.Equal(bundled, BundledTools.Pick(@"D:\사라진\ffmpeg.exe", "ffmpeg", bundled));   // 적힌 파일이 없음
            Assert.Equal(mine, BundledTools.Pick(mine, "ffmpeg", bundled));                         // 직접 고른 파일은 그대로
            Assert.Equal("ffmpeg7", BundledTools.Pick("ffmpeg7", "ffmpeg", bundled));               // 다른 이름(PATH)도 그대로
            Assert.Equal(@"D:\사라진\ffmpeg.exe", BundledTools.Pick(@"D:\사라진\ffmpeg.exe", "ffmpeg", null)); // 내장 도구가 없으면 손대지 않는다

            var resolved = BundledTools.Resolve(new AppSettings { Video2X = "", Python = "py" }, bundled, null, null);
            Assert.Equal(bundled, resolved.Ffmpeg);
            Assert.Equal("ffprobe", resolved.Ffprobe);
            Assert.Equal("", resolved.Video2X);
            Assert.Equal("py", resolved.Python);
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

    [Fact]
    public async Task Environment_Check_Gives_Up_On_A_Tool_That_Never_Exits()
    {
        // 경로 칸에 창이 뜨는 프로그램을 고르면 끝나지 않는다 — 점검이 멈춰 있으면 ProPainter 준비 버튼도 계속 거절된다.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var items = await new EnvironmentChecker(TestTools.MockSettings(), new HangingRunner(), TimeSpan.FromMilliseconds(300))
            .RunAsync(null, TestContext.Current.CancellationToken);

        foreach (string name in new[] { "ffmpeg", "ffprobe", "Python + PyTorch", "Video2X" })
        {
            var item = items.Single(i => i.Name == name);
            Assert.Equal(CheckStatus.Error, item.Status);
            Assert.Contains("끝나지 않아", item.Detail);
        }
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"{clock.Elapsed.TotalSeconds:0.0}초");

        // 사용자가 취소한 것은 "응답 없음"으로 바꾸지 않고 그대로 알린다
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new EnvironmentChecker(TestTools.MockSettings(), new HangingRunner(), TimeSpan.FromMinutes(5)).RunAsync(null, cancelled.Token));
    }

    /// <summary>무엇을 실행하든 취소될 때까지 끝나지 않는 도구.</summary>
    private sealed class HangingRunner : IToolRunner
    {
        public async Task<ToolResult> RunAsync(ToolInvocation invocation, Action<string>? onOutput, Action<string>? onError, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new ToolResult(0, "");
        }
    }
}
