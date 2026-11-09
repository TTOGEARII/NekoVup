using System.Diagnostics;
using System.Globalization;
using NekoVup.Core.Chunks;
using NekoVup.Core.Imaging;
using NekoVup.Core.Media;
using NekoVup.Core.Processes;
using NekoVup.Core.Regions;
using NekoVup.Core.Settings;

namespace NekoVup.Core.Pipeline;

/// <summary>
/// 작업 하나를 처음부터 끝까지 진행한다.
/// <code>
/// 워터마크 제거:  ProPainter(추출 → 복원 → 병합 → 합성)  |  delogo  |  없음
/// 업스케일:      (중간 영상) → Video2X → 오디오·자막 붙이기
/// </code>
/// 인스턴스 하나로 동시에 여러 작업을 돌리지 않는다.
/// </summary>
public sealed class JobRunner(AppSettings settings, IToolRunner runner, IJobLog log)
{
    public const string ProPainterScript = "inference_propainter.py";

    /// <summary>ProPainter 폴더 안에 잠깐 만드는 임시 폴더 이름.</summary>
    public const string ProPainterScratch = ".nekovup-tmp";

    /// <summary>ProPainter 프로세스가 통째로 죽었을 때 조각 하나를 몇 번까지 돌려 보는지.</summary>
    public const int ProPainterAttempts = 3;

    /// <summary>Windows에서 프로세스가 예외로 죽으면 종료 코드가 0xC0000005 같은 NTSTATUS 오류 값이 된다.</summary>
    public static bool IsNativeCrash(int exitCode) => unchecked((uint)exitCode) >= 0xC0000000;

    private IJobLog _log = log;
    private ProgressPlan _progress = new(null, []);

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new JobException($"입력 파일이 없습니다: {path}");
        string json;
        try
        {
            json = await runner.RunCaptureAsync(new ToolInvocation(settings.Ffprobe, [.. FfprobeParser.Arguments, path]), cancellationToken).ConfigureAwait(false);
        }
        catch (ToolFailedException ex)
        {
            throw new JobException("영상 정보를 읽지 못했습니다. " + ex.Message, ex);
        }

        try
        {
            return FfprobeParser.Parse(json, path);
        }
        catch (InvalidDataException ex)
        {
            throw new JobException(ex.Message, ex);
        }
    }

    public async Task<JobResult> RunAsync(JobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        string input = Path.GetFullPath(request.InputPath);
        string output = Path.GetFullPath(request.OutputPath);
        request = request with { InputPath = input, OutputPath = output };
        var options = request.Options;
        bool upscale = options.Upscale != UpscalePreset.None;

        if (!FfmpegCommands.IsSupportedOutput(output))
            throw new JobException("출력 파일 확장자는 .mp4, .mkv, .mov, .m4v 중 하나여야 합니다.");
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new JobException("출력 파일이 입력 파일과 같습니다. 다른 이름을 지정하세요.");
        if (options.Mode == CleanMode.None && !upscale)
            throw new JobException("할 일이 없습니다. 워터마크 제거 방식이나 업스케일 중 하나는 골라야 합니다.");
        if (options.Mode != CleanMode.None && request.Boxes.Count == 0)
            throw new JobException("워터마크 박스를 하나 이상 지정하세요.");

        UpscalePlan? plan = null;
        if (upscale)
        {
            try { plan = Video2X.Plan(options.Upscale, options.Scale); }
            catch (ArgumentException ex) { throw new JobException(ex.Message, ex); }
            if (string.IsNullOrWhiteSpace(settings.Video2X) || !File.Exists(settings.Video2X))
                throw new JobException($"Video2X 실행 파일을 찾을 수 없습니다: {settings.Video2X}");
        }

        _progress = new ProgressPlan(progress, StagePlan(options.Mode, plan));
        _progress.Report(JobStage.Probe, 0);
        var info = await ProbeAsync(input, cancellationToken).ConfigureAwait(false);
        if (options.StartSeconds is { } start && info.DurationSeconds > 0 && start >= info.DurationSeconds)
            throw new JobException("시작 지점이 영상 길이보다 뒤에 있습니다.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        _log.Info(string.Create(CultureInfo.InvariantCulture,
            $"입력: {info.Width}x{info.Height}, {info.Fps} fps, {info.DurationSeconds:0.##}초, 오디오 {(info.HasAudio ? string.Join("/", info.AudioCodecs) : "없음")}"));
        if (info.SubtitleCodecs.Count > 0 && !FfmpegCommands.KeepsSubtitles(output))
            _log.Warning("원본에 자막이 있지만 이 출력 형식에는 담지 않습니다. 자막을 유지하려면 출력을 .mkv로 지정하세요.");

        string partial = PartialPath(output);
        try
        {
            int frames;
            string? workDirectory = null;
            if (options.Mode == CleanMode.Delogo && !upscale)
                frames = await RunDelogoAsync(request, info, partial, new EncodeOptions(options.Crf, options.Preset), JobStage.Delogo, cancellationToken).ConfigureAwait(false);
            else
                (frames, workDirectory) = await RunInWorkDirectoryAsync(request, info, plan, partial, cancellationToken).ConfigureAwait(false);

            File.Move(partial, output, overwrite: true);
            _progress.Report(JobStage.Finished, 1);
            _log.Info($"완료: {output}");
            return new JobResult(output, frames, clock.Elapsed, workDirectory);
        }
        catch (OperationCanceledException)
        {
            _log.Warning("작업이 취소되었습니다.");
            throw;
        }
        finally
        {
            TryDeleteFile(partial);
        }
    }

    // ───────────────────────── 작업 폴더를 쓰는 경로 (ProPainter, 업스케일) ─────────────────────────

    private async Task<(int Frames, string? WorkDirectory)> RunInWorkDirectoryAsync(JobRequest request, MediaInfo info, UpscalePlan? plan, string partial, CancellationToken cancellationToken)
    {
        var options = request.Options;
        IReadOnlyList<ProcessRegion> regions = [];
        if (options.Mode == CleanMode.ProPainter)
        {
            ValidateProPainter(options);
            try
            {
                regions = RegionPlanner.Plan(request.Boxes, info.Width, info.Height, options.Region);
            }
            catch (ArgumentException ex)
            {
                throw new JobException(ex.Message, ex);
            }
        }

        string key = JobManifest.ComputeKey(request);
        string work = Path.GetFullPath(request.WorkDirectory ?? DefaultWorkDirectory(request.OutputPath, request.InputPath, key));
        Directory.CreateDirectory(work);

        int frames;
        var outerLog = _log;
        var fileLog = new FileJobLog(Path.Combine(work, "job.log"));
        _log = new CompositeJobLog(outerLog, fileLog);
        try
        {
            var manifest = JobManifest.TryLoad(work);
            if (manifest is not null && manifest.JobKey != key)
                throw new JobException($"작업 폴더에 다른 작업의 기록이 남아 있습니다. 비어 있는 폴더를 지정하세요: {work}");
            if (manifest is not null) _log.Info("이전 작업 기록을 찾았습니다. 끝난 단계는 건너뜁니다.");
            manifest ??= new JobManifest { JobKey = key, InputPath = request.InputPath, Fps = info.Fps.ToString() };
            manifest.Save(work);

            if (plan is null)
            {
                // 업스케일이 없으면 합성 결과가 곧 최종 파일이다.
                frames = await CleanWithProPainterAsync(request, info, regions, work, manifest, partial, new EncodeOptions(options.Crf, options.Preset), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                string cleaned = Path.Combine(work, "cleaned.mkv");
                frames = await MakeCleanedAsync(request, info, regions, work, manifest, cleaned, cancellationToken).ConfigureAwait(false);
                string upscaled = Path.Combine(work, "upscaled.mkv");
                await UpscaleAsync(options, plan, manifest, work, cleaned, upscaled, frames, cancellationToken).ConfigureAwait(false);
                await FinalizeAsync(request, info, plan, upscaled, partial, frames, cancellationToken).ConfigureAwait(false);
            }

            if (request.KeepWorkDirectory) return (frames, work);
        }
        finally
        {
            _log = outerLog;
            fileLog.Dispose();
        }

        // 성공했을 때만 여기로 온다: 중간 파일 정리.
        TryDeleteDirectory(work);
        if (request.WorkDirectory is null) TryDeleteEmptyDirectory(Path.GetDirectoryName(work)!);
        return (frames, null);
    }

    /// <summary>업스케일러에 넣을 중간 영상(영상만, B프레임 없음)을 만든다. 이미 완성돼 있으면 건너뛴다.</summary>
    private async Task<int> MakeCleanedAsync(JobRequest request, MediaInfo info, IReadOnlyList<ProcessRegion> regions, string work, JobManifest manifest, string cleaned, CancellationToken cancellationToken)
    {
        var options = request.Options;
        if (manifest.Cleaned && manifest.CleanedFrames > 0 && File.Exists(cleaned))
        {
            _log.Info("업스케일용 중간 영상: 이미 끝나 있어 건너뜁니다.");
            return manifest.CleanedFrames;
        }

        manifest.Cleaned = false;
        manifest.UpscaledKey = "";
        manifest.Save(work);

        string temp = PartialPath(cleaned);
        var encode = EncodeOptions.ForIntermediate(options.Preset);
        switch (options.Mode)
        {
            case CleanMode.ProPainter:
                await CleanWithProPainterAsync(request, info, regions, work, manifest, temp, encode, cancellationToken).ConfigureAwait(false);
                break;
            case CleanMode.Delogo:
                await RunDelogoAsync(request, info, temp, encode, JobStage.Delogo, cancellationToken).ConfigureAwait(false);
                break;
            default:
                long total = info.EstimateFrames(options.StartSeconds, options.DurationSeconds);
                var args = FfmpegCommands.Prepare(info, options.StartSeconds, options.DurationSeconds, encode, temp);
                await RunFfmpegAsync(args, frame => _progress.Report(JobStage.Prepare, Fraction(frame, total)), cancellationToken).ConfigureAwait(false);
                break;
        }

        int frames = await CountFramesAsync(temp, cancellationToken).ConfigureAwait(false);
        if (frames <= 0) throw new JobException("처리할 프레임이 없습니다. 시작 지점·길이를 확인하세요.");
        File.Move(temp, cleaned, overwrite: true);
        manifest.Cleaned = true;
        manifest.CleanedFrames = frames;
        manifest.Save(work);
        return frames;
    }

    // ───────────────────────── delogo ─────────────────────────

    private async Task<int> RunDelogoAsync(JobRequest request, MediaInfo info, string target, EncodeOptions encode, JobStage stage, CancellationToken cancellationToken)
    {
        var options = request.Options;
        IReadOnlyList<PixelRect> boxes;
        try
        {
            boxes = DelogoPlanner.Plan(request.Boxes, info.Width, info.Height);
        }
        catch (ArgumentException ex)
        {
            throw new JobException(ex.Message, ex);
        }

        long total = info.EstimateFrames(options.StartSeconds, options.DurationSeconds);
        long last = 0;
        var args = FfmpegCommands.Delogo(info, boxes, options.StartSeconds, options.DurationSeconds, encode, target);
        await RunFfmpegAsync(args, frame =>
        {
            last = frame;
            _progress.Report(stage, Fraction(frame, total));
        }, cancellationToken).ConfigureAwait(false);
        return (int)last;
    }

    // ───────────────────────── ProPainter ─────────────────────────

    private void ValidateProPainter(JobOptions options)
    {
        if (string.IsNullOrWhiteSpace(settings.Python))
            throw new JobException("ProPainter용 파이썬 경로가 설정되지 않았습니다.");
        if (string.IsNullOrWhiteSpace(settings.ProPainterDir) || !File.Exists(Path.Combine(settings.ProPainterDir, ProPainterScript)))
            throw new JobException($"ProPainter 폴더가 올바르지 않습니다 ({ProPainterScript}가 없음): {settings.ProPainterDir}");
        try
        {
            ChunkPlanner.Plan(1, options.ChunkLength, options.Overlap); // 옵션 검증
        }
        catch (ArgumentException ex)
        {
            throw new JobException(ex.Message, ex);
        }
    }

    /// <summary>영역 추출 → ProPainter → 병합 → 원본 위에 합성해서 <paramref name="target"/>에 인코딩한다.</summary>
    private async Task<int> CleanWithProPainterAsync(JobRequest request, MediaInfo info, IReadOnlyList<ProcessRegion> regions, string work, JobManifest manifest, string target, EncodeOptions encode, CancellationToken cancellationToken)
    {
        var options = request.Options;
        manifest.Regions = [.. regions.Select(r => r.Rect.ToString())];
        manifest.Save(work);
        for (int i = 0; i < regions.Count; i++)
        {
            var r = regions[i];
            string resize = r.NeedsResize
                ? string.Create(CultureInfo.InvariantCulture, $", 면적이 커서 {r.ResizeRatio:0.####}배로 줄여 처리")
                : "";
            _log.Info($"처리 영역 {i + 1}: 위치 ({r.Rect.X},{r.Rect.Y}) 크기 {r.Rect.Width}x{r.Rect.Height}{resize}");
        }

        long estimatedFrames = info.EstimateFrames(options.StartSeconds, options.DurationSeconds);
        if (!request.SkipDiskSpaceCheck && !manifest.Extracted)
            CheckDiskSpace(work, regions, estimatedFrames);

        // 1) 영역 프레임 추출
        int frameCount = await ExtractAsync(request, info, regions, work, manifest, estimatedFrames, cancellationToken).ConfigureAwait(false);
        var chunks = ChunkPlanner.Plan(frameCount, options.ChunkLength, options.Overlap);
        _log.Info($"프레임 {frameCount}장, 조각 {chunks.Count}개 (조각 길이 {options.ChunkLength}, 겹침 {options.Overlap})");

        // 2) ProPainter (영역 × 조각)
        string scratch = Path.Combine(settings.ProPainterDir, ProPainterScratch, manifest.JobKey[..10]);
        try
        {
            int totalChunks = regions.Count * chunks.Count, doneChunks = 0;
            for (int i = 0; i < regions.Count; i++)
            {
                if (IsMerged(manifest, work, i, frameCount))
                {
                    doneChunks += chunks.Count;
                    continue;
                }

                foreach (var chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string detail = $"영역 {i + 1}/{regions.Count}, 조각 {chunk.Index + 1}/{chunks.Count}";
                    _progress.Report(JobStage.Inpaint, (double)doneChunks / totalChunks, detail);
                    await InpaintChunkAsync(work, scratch, i, regions[i], chunk, options, cancellationToken).ConfigureAwait(false);
                    doneChunks++;
                    _progress.Report(JobStage.Inpaint, (double)doneChunks / totalChunks, detail);
                }
            }
        }
        finally
        {
            TryDeleteDirectory(scratch);
            TryDeleteEmptyDirectory(Path.GetDirectoryName(scratch)!);
        }

        // 3) 조각 병합 + 알파
        for (int i = 0; i < regions.Count; i++)
        {
            if (IsMerged(manifest, work, i, frameCount)) continue;
            var region = regions[i];
            int index = i;
            var alpha = MaskBuilder.BuildAlpha(region, options.MaskDilation, region.ResizeRatio);
            var mergeProgress = new SynchronousProgress<int>(done =>
                _progress.Report(JobStage.Merge, (index + (double)done / frameCount) / regions.Count));
            await Task.Run(() => ChunkMerger.Merge(InpaintDir(work, index), chunks, OutDir(work, index),
                region.Rect.Width, region.Rect.Height, alpha, mergeProgress, cancellationToken), cancellationToken).ConfigureAwait(false);

            if (CountPng(OutDir(work, i)) != frameCount)
                throw new JobException("병합된 프레임 수가 맞지 않습니다. 작업 폴더를 지우고 다시 시도하세요.");
            manifest.MergedRegions.Add(i);
            manifest.Save(work);
            // 병합이 끝난 영역의 원본·복원 프레임은 더 필요 없다.
            TryDeleteDirectory(SrcDir(work, i));
            TryDeleteDirectory(InpaintDir(work, i));
        }

        // 4) 원본 위에 얹어 인코딩
        var overlays = regions.Select((r, i) => (r.Rect, OutDir(work, i))).ToList();
        var args = FfmpegCommands.Composite(info, overlays, options.StartSeconds, options.DurationSeconds, encode, target);
        await RunFfmpegAsync(args, frame => _progress.Report(JobStage.Composite, Fraction(frame, frameCount)), cancellationToken).ConfigureAwait(false);
        return frameCount;
    }

    private async Task<int> ExtractAsync(JobRequest request, MediaInfo info, IReadOnlyList<ProcessRegion> regions, string work, JobManifest manifest, long estimatedFrames, CancellationToken cancellationToken)
    {
        bool reusable = manifest.Extracted && manifest.FrameCount > 0 &&
            Enumerable.Range(0, regions.Count).All(i => IsMerged(manifest, work, i, manifest.FrameCount) || CountPng(SrcDir(work, i)) == manifest.FrameCount);
        if (reusable)
        {
            _log.Info("프레임 추출: 이미 끝나 있어 건너뜁니다.");
            _progress.Report(JobStage.Extract, 1);
            return manifest.FrameCount;
        }

        // 추출을 다시 하면 그 뒤 단계 결과도 모두 무효다.
        manifest.Extracted = false;
        manifest.FrameCount = 0;
        manifest.MergedRegions.Clear();
        manifest.Save(work);
        for (int i = 0; i < regions.Count; i++)
        {
            TryDeleteDirectory(RegionDir(work, i));
            Directory.CreateDirectory(SrcDir(work, i));
        }

        var targets = regions.Select((r, i) => (r.Rect, SrcDir(work, i))).ToList();
        var args = FfmpegCommands.ExtractRegions(info, targets, request.Options.StartSeconds, request.Options.DurationSeconds);
        await RunFfmpegAsync(args, frame => _progress.Report(JobStage.Extract, Fraction(frame, estimatedFrames)), cancellationToken).ConfigureAwait(false);

        int count = CountPng(SrcDir(work, 0));
        if (count == 0) throw new JobException("프레임을 하나도 추출하지 못했습니다. 시작 지점·길이를 확인하세요.");
        for (int i = 1; i < regions.Count; i++)
            if (CountPng(SrcDir(work, i)) != count) throw new JobException("영역마다 추출된 프레임 수가 다릅니다.");

        manifest.Extracted = true;
        manifest.FrameCount = count;
        manifest.Save(work);
        return count;
    }

    /// <summary>
    /// 조각 하나를 ProPainter로 복원한다.
    /// <para>
    /// ProPainter는 프레임을 OpenCV로 읽고 쓰는데, Windows의 OpenCV는 한글 등 영문이 아닌 글자가 든 경로를 열지 못한다(실측: "바탕 화면" 아래 폴더에서 실패).
    /// 그래서 입력·마스크·출력은 ProPainter 폴더 안의 임시 폴더에 두고 **상대 경로**(영문만)로 넘긴 뒤, 끝나면 결과를 작업 폴더로 옮긴다.
    /// 작업 디렉터리는 어차피 ProPainter 폴더여야 한다(가중치를 상대 경로 weights/ 에서 찾는다).
    /// </para>
    /// </summary>
    private async Task InpaintChunkAsync(string work, string scratch, int regionIndex, ProcessRegion region, ChunkRange chunk, JobOptions options, CancellationToken cancellationToken)
    {
        string name = ChunkMerger.ChunkName(chunk);
        string framesDir = ChunkMerger.ChunkFramesDir(InpaintDir(work, regionIndex), chunk);
        if (CountPng(framesDir) == chunk.Length)
        {
            _log.Info($"영역 {regionIndex + 1} 조각 {name}: 결과가 이미 있어 건너뜁니다.");
            return;
        }

        string regionScratch = Path.Combine(scratch, "r" + regionIndex.ToString(CultureInfo.InvariantCulture));
        string inputDir = Path.Combine(regionScratch, "in", name);
        string outputRoot = Path.Combine(regionScratch, "out");
        string maskPath = Path.Combine(regionScratch, "mask.png");
        TryDeleteDirectory(inputDir);
        TryDeleteDirectory(Path.Combine(outputRoot, name));
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputRoot);
        PngCodec.Write(maskPath, MaskBuilder.BuildMask(region), region.Rect.Width, region.Rect.Height, 1);

        // 추출해 둔 프레임을 하드링크로 모은다 (다른 드라이브면 복사).
        string src = SrcDir(work, regionIndex);
        for (int frame = chunk.Start; frame < chunk.End; frame++)
        {
            string file = ChunkMerger.SequenceFrameName(frame);
            FileLinker.LinkOrCopy(Path.Combine(src, file), Path.Combine(inputDir, file));
        }

        string Relative(string path) => Path.GetRelativePath(settings.ProPainterDir, path);
        var args = new List<string>
        {
            ProPainterScript, "-i", Relative(inputDir), "-m", Relative(maskPath), "-o", Relative(outputRoot),
            "--subvideo_length", options.SubvideoLength.ToString(CultureInfo.InvariantCulture),
            "--mask_dilation", options.MaskDilation.ToString(CultureInfo.InvariantCulture),
            "--save_frames",
        };
        if (options.Fp16) args.Add("--fp16");
        if (region.NeedsResize) args.AddRange(["--resize_ratio", region.ResizeRatio.ToString("0.####", CultureInfo.InvariantCulture)]);

        var invocation = new ToolInvocation(settings.Python, args)
        {
            WorkingDirectory = settings.ProPainterDir,
            Environment = new Dictionary<string, string>
            {
                ["PYTHONUTF8"] = "1",
                ["PYTHONIOENCODING"] = "utf-8",
                ["PYTHONUNBUFFERED"] = "1",
            },
        };
        _log.Detail("> " + invocation.DisplayText);
        var clock = Stopwatch.StartNew();
        for (int attempt = 1; ; attempt++)
        {
            var result = await runner.RunAsync(invocation, line => _log.Detail(line), line => _log.Detail(line), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0) break;

            // 파이썬 예외(메모리 부족 등)는 코드 1로 끝나고 다시 해도 똑같다. 반면 GPU 라이브러리 안에서 프로세스가 통째로 죽는 경우는
            // 같은 입력으로도 간헐적으로 생기므로(실측: 같은 조각 7번 중 1번) 몇 번 다시 시도한다.
            if (!IsNativeCrash(result.ExitCode) || attempt >= ProPainterAttempts)
                throw new ToolFailedException(
                    $"ProPainter가 오류 코드 {result.ExitCode}로 끝났습니다.{Environment.NewLine}{result.ErrorTail}", result.ExitCode);
            _log.Warning($"ProPainter가 비정상 종료했습니다 (코드 0x{result.ExitCode:X8}). 다시 시도합니다 ({attempt + 1}/{ProPainterAttempts}).");
            TryDeleteDirectory(Path.Combine(outputRoot, name));
        }

        string produced = Path.Combine(outputRoot, name, "frames");
        int count = CountPng(produced);
        if (count != chunk.Length)
            throw new ToolFailedException($"ProPainter 결과 프레임 수가 다릅니다 (기대 {chunk.Length}, 실제 {count}).");

        // 결과를 작업 폴더로 옮긴다. 다 옮긴 뒤에야 "frames" 이름이 되므로, 그 폴더가 있으면 완성본이다.
        string staging = framesDir + ".tmp";
        TryDeleteDirectory(staging);
        TryDeleteDirectory(framesDir);
        Directory.CreateDirectory(staging);
        foreach (var file in Directory.EnumerateFiles(produced, "*.png"))
            MoveFile(file, Path.Combine(staging, Path.GetFileName(file)));
        Directory.Move(staging, framesDir);

        TryDeleteDirectory(inputDir);
        TryDeleteDirectory(Path.Combine(outputRoot, name));
        _log.Info(string.Create(CultureInfo.InvariantCulture, $"영역 {regionIndex + 1} 조각 {name}: {chunk.Length}장 복원 ({clock.Elapsed.TotalSeconds:0.0}초)"));
    }

    // ───────────────────────── 업스케일 ─────────────────────────

    private async Task UpscaleAsync(JobOptions options, UpscalePlan plan, JobManifest manifest, string work, string cleaned, string upscaled, int frames, CancellationToken cancellationToken)
    {
        // 뒤에 줄이는 단계가 있으면 여기 결과는 중간물이므로 화질을 높게 잡는다.
        var encode = plan.NeedsDownscale ? new EncodeOptions(EncodeOptions.IntermediateCrf, options.Preset) : new EncodeOptions(options.Crf, options.Preset);
        string upscaleKey = string.Create(CultureInfo.InvariantCulture, $"{plan.Processor}|{plan.Model}|{plan.ModelScale}|{encode.Crf}|{encode.Preset}");
        if (manifest.UpscaledKey == upscaleKey && File.Exists(upscaled))
        {
            _log.Info("업스케일: 이미 끝나 있어 건너뜁니다.");
            _progress.Report(JobStage.Upscale, 1);
            return;
        }

        manifest.UpscaledKey = "";
        manifest.Save(work);
        string temp = PartialPath(upscaled);
        TryDeleteFile(temp);

        var invocation = new ToolInvocation(settings.Video2X, Video2X.Arguments(plan, cleaned, temp, options.GpuIndex, encode));
        _log.Info($"업스케일: {plan.Model} {plan.ModelScale}배, {frames}장");
        _log.Detail("> " + invocation.DisplayText);
        _progress.Report(JobStage.Upscale, 0);

        bool finished = false;
        var clock = Stopwatch.StartNew();
        var result = await runner.RunAsync(invocation, line =>
        {
            if (Video2X.TryParseProgress(line, out long frame))
            {
                _progress.Report(JobStage.Upscale, Fraction(frame, frames),
                    frame > 0 ? string.Create(CultureInfo.InvariantCulture, $"{frame}/{frames}장, 초당 {frame / Math.Max(clock.Elapsed.TotalSeconds, 0.001):0.0}장") : null);
                return;
            }
            if (line.Contains(Video2X.DoneMarker, StringComparison.Ordinal)) finished = true;
            if (!string.IsNullOrWhiteSpace(line)) _log.Detail(Video2X.StripAnsi(line));
        }, line => _log.Detail(line), cancellationToken).ConfigureAwait(false);

        // Video2X 6.4.0은 정상적으로 끝낸 뒤에도 종료 과정에서 죽어 0이 아닌 코드를 내는 일이 있다(실측).
        // 그래서 종료 코드가 아니라 "완료 문구 + 결과 프레임 수"로 성공을 판단한다.
        if (result.ExitCode != 0 && !finished)
            throw new ToolFailedException($"Video2X가 오류 코드 {result.ExitCode}로 끝났습니다.{Environment.NewLine}{result.ErrorTail}", result.ExitCode);
        if (result.ExitCode != 0)
            _log.Detail($"Video2X가 작업을 마친 뒤 종료 코드 {result.ExitCode}를 냈습니다. 결과 파일을 검사합니다.");

        int produced = File.Exists(temp) ? await CountFramesAsync(temp, cancellationToken).ConfigureAwait(false) : 0;
        if (produced != frames)
            throw new ToolFailedException($"업스케일 결과 프레임 수가 다릅니다 (기대 {frames}, 실제 {produced}).");

        File.Move(temp, upscaled, overwrite: true);
        manifest.UpscaledKey = upscaleKey;
        manifest.Save(work);
        _log.Info(string.Create(CultureInfo.InvariantCulture, $"업스케일 끝: {frames}장, {clock.Elapsed.TotalSeconds:0}초 (초당 {frames / Math.Max(clock.Elapsed.TotalSeconds, 0.001):0.0}장)"));
    }

    private async Task FinalizeAsync(JobRequest request, MediaInfo info, UpscalePlan plan, string upscaled, string partial, int frames, CancellationToken cancellationToken)
    {
        var options = request.Options;
        (int Width, int Height)? scaleTo = plan.NeedsDownscale ? (info.Width * plan.FinalScale, info.Height * plan.FinalScale) : null;
        int outputHeight = info.Height * plan.FinalScale;
        var args = FfmpegCommands.Finalize(info, upscaled, scaleTo, outputHeight, options.StartSeconds, options.DurationSeconds,
            new EncodeOptions(options.Crf, options.Preset), partial);
        await RunFfmpegAsync(args, frame => _progress.Report(JobStage.Finalize, Fraction(frame, frames)), cancellationToken).ConfigureAwait(false);
    }

    // ───────────────────────── 공통 ─────────────────────────

    private async Task RunFfmpegAsync(List<string> args, Action<long> onFrame, CancellationToken cancellationToken)
    {
        var parser = new FfmpegProgressParser();
        var invocation = new ToolInvocation(settings.Ffmpeg, args);
        _log.Detail("> " + invocation.DisplayText);
        await runner.RunCheckedAsync(invocation,
            line => { if (parser.Feed(line, out var p)) onFrame(p.Frame); },
            line => _log.Detail(line),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> CountFramesAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            string text = await runner.RunCaptureAsync(new ToolInvocation(settings.Ffprobe, FfmpegCommands.CountFrames(path)), cancellationToken).ConfigureAwait(false);
            return int.TryParse(text.Trim().TrimEnd(','), NumberStyles.Integer, CultureInfo.InvariantCulture, out int frames) ? frames : 0;
        }
        catch (ToolFailedException)
        {
            return 0;
        }
    }

    private void CheckDiskSpace(string work, IReadOnlyList<ProcessRegion> regions, long frames)
    {
        long needed = EstimateTempBytes(regions, frames);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(work)!);
            long free = drive.AvailableFreeSpace;
            _log.Info(string.Create(CultureInfo.InvariantCulture, $"임시 공간: 약 {Gb(needed):0.0} GB 필요, {drive.Name} 남은 공간 {Gb(free):0.0} GB"));
            if (free < needed)
                throw new JobException(string.Create(CultureInfo.InvariantCulture,
                    $"임시 공간이 부족합니다. 약 {Gb(needed):0.0} GB가 필요한데 {drive.Name}에 {Gb(free):0.0} GB만 남았습니다. 작업 폴더를 다른 드라이브로 지정하거나 구간을 줄이세요."));
        }
        catch (ArgumentException)
        {
            _log.Warning("작업 폴더 드라이브의 남은 공간을 확인할 수 없습니다 (네트워크 경로 등).");
        }

        static double Gb(long bytes) => bytes / 1024.0 / 1024.0 / 1024.0;
    }

    /// <summary>중간 파일에 필요한 공간 어림: 영역 면적 × 프레임 수 × 4바이트.</summary>
    public static long EstimateTempBytes(IReadOnlyList<ProcessRegion> regions, long frames) =>
        regions.Sum(r => r.Rect.Area) * frames * 4;

    public static string DefaultWorkDirectory(string outputPath, string inputPath, string jobKey)
    {
        string stem = Path.GetFileNameWithoutExtension(inputPath);
        if (stem.Length > 40) stem = stem[..40];
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, ".nekovup", $"{stem.Trim()}-{jobKey[..10]}");
    }

    /// <summary>작업 종류에 따른 단계 목록과, 전체 진행률에서 각 단계가 차지하는 비중(대략의 소요 시간 비).</summary>
    private static List<(JobStage Stage, double Weight)> StagePlan(CleanMode mode, UpscalePlan? plan)
    {
        var stages = new List<(JobStage, double)>();
        switch (mode)
        {
            case CleanMode.ProPainter:
                stages.AddRange([(JobStage.Extract, 5), (JobStage.Inpaint, 75), (JobStage.Merge, 5), (JobStage.Composite, 15)]);
                break;
            case CleanMode.Delogo:
                stages.Add((JobStage.Delogo, 15));
                break;
            default:
                stages.Add((JobStage.Prepare, 10));
                break;
        }
        if (plan is not null)
        {
            // 배율이 클수록 오래 걸린다 (출력 픽셀 수에 비례).
            stages.Add((JobStage.Upscale, 60.0 * plan.ModelScale * plan.ModelScale / 4));
            stages.Add((JobStage.Finalize, plan.NeedsDownscale ? 25 : 2));
        }
        return stages;
    }

    private static bool IsMerged(JobManifest manifest, string work, int region, int frameCount) =>
        manifest.MergedRegions.Contains(region) && CountPng(OutDir(work, region)) == frameCount;

    private static string RegionDir(string work, int index) => Path.Combine(work, "r" + index.ToString(CultureInfo.InvariantCulture));
    private static string SrcDir(string work, int index) => Path.Combine(RegionDir(work, index), "src");
    private static string InpaintDir(string work, int index) => Path.Combine(RegionDir(work, index), "pp");
    private static string OutDir(string work, int index) => Path.Combine(RegionDir(work, index), "out");

    /// <summary>완성 전에는 다른 이름으로 쓰고 끝나면 바꾼다. 확장자는 ffmpeg가 형식을 알 수 있게 유지한다.</summary>
    private static string PartialPath(string output) =>
        Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + ".partial" + Path.GetExtension(output));

    private static int CountPng(string directory) =>
        Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.png").Count() : 0;

    private static double Fraction(long done, long total) => total <= 0 ? 0 : Math.Clamp((double)done / total, 0, 1);

    private static void MoveFile(string source, string target)
    {
        try
        {
            File.Move(source, target, overwrite: true);
        }
        catch (IOException)
        {
            // 다른 드라이브 등 이름 바꾸기로 옮길 수 없는 경우
            File.Copy(source, target, overwrite: true);
            File.Delete(source);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>단계별 진행률을 전체 진행률로 바꿔 전달한다.</summary>
    private sealed class ProgressPlan(IProgress<JobProgress>? sink, List<(JobStage Stage, double Weight)> stages)
    {
        private readonly double _total = Math.Max(stages.Sum(s => s.Weight), 1e-9);

        public void Report(JobStage stage, double fraction, string? detail = null)
        {
            if (sink is null) return;
            fraction = Math.Clamp(fraction, 0, 1);
            double overall;
            if (stage == JobStage.Finished) overall = 1;
            else if (stage == JobStage.Probe) overall = 0;
            else
            {
                double before = 0, weight = 0;
                foreach (var (s, w) in stages)
                {
                    if (s == stage) { weight = w; break; }
                    before += w;
                }
                overall = (before + weight * fraction) / _total;
            }
            sink.Report(new JobProgress(stage, fraction, Math.Clamp(overall, 0, 1), detail));
        }
    }

    /// <summary>호출한 스레드에서 바로 콜백을 부르는 IProgress (기본 <see cref="Progress{T}"/>는 동기화 컨텍스트로 넘겨 순서가 뒤섞일 수 있다).</summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
