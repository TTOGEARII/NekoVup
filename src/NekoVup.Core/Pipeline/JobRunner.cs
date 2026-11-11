using System.Diagnostics;
using System.Globalization;
using NekoVup.Core.Chunks;
using NekoVup.Core.Imaging;
using NekoVup.Core.Media;
using NekoVup.Core.Processes;
using NekoVup.Core.Regions;
using NekoVup.Core.Runtime;
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
public sealed class JobRunner(AppSettings configured, IToolRunner runner, IJobLog log)
{
    /// <summary>내장 도구가 있으면 그쪽으로 바꾼 설정 (<see cref="BundledTools"/>).</summary>
    private readonly AppSettings settings = BundledTools.Resolve(configured);

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

    /// <summary>
    /// 화면 밖이라 처리하지 않는 박스를 알리고, 움직이는 박스는 어떻게 따라가는지 적는다. 다른 박스가 남아 있으면 작업은 그대로
    /// 끝나므로, 빠진 것을 알려야 워터마크가 왜 남았는지 안다.
    /// </summary>
    public static void DescribeBoxes(JobRequest request, MediaInfo info, IJobLog log)
    {
        if (request.Options.Mode == CleanMode.None) return;
        // delogo는 가장자리 1픽셀을 쓰지 못한다 (DelogoPlanner와 같은 기준).
        var frame = request.Options.Mode == CleanMode.Delogo ? new PixelRect(1, 1, info.Width - 2, info.Height - 2) : new PixelRect(0, 0, info.Width, info.Height);
        string screen = $"화면({info.Width}x{info.Height})";
        foreach (var box in request.Boxes)
            if (box.ClampTo(frame).IsEmpty)
                log.Warning($"박스 ({box.X},{box.Y}) {box.Width}x{box.Height}는 {screen} 밖이라 처리하지 않습니다.");
        foreach (var track in request.Tracks)
        {
            // 경로에 쓰는 자리만 시각 순서로 (같은 프레임에 둘을 적었으면 뒤의 것 하나 — BoxPath.From과 같다).
            var keys = track.Keys
                .Where(BoxPath.IsUsable)
                .GroupBy(k => FrameGrid.FrameAt(k.Seconds, info.Fps.Value))
                .OrderBy(g => g.Key)
                .Select(g => g.Last())
                .ToList();
            if (keys.Count == 0)
            {
                log.Warning("움직이는 박스에 쓸 수 있는 위치가 없어 처리하지 않습니다.");
            }
            else if (track.Jump)
            {
                // 건너뛰는 박스가 화면 밖에 적어 둔 자리는 쓰지 않는다 (앞 자리에 머문다).
                foreach (var key in keys.Where(k => k.Rect.ClampTo(frame).IsEmpty))
                    log.Warning($"움직이는 박스의 {TimeText.Format(key.Seconds)} 위치 ({key.Rect.X},{key.Rect.Y}) {key.Rect.Width}x{key.Rect.Height}는 {screen} 밖이라 쓰지 않습니다.");
                keys.RemoveAll(k => k.Rect.ClampTo(frame).IsEmpty);
            }
            else if (BoxPath.From(track, info.Fps.Value).ClampTo(frame) is null)
            {
                // 직선으로 가는 박스는 밖에 적어 둔 자리도 가는 방향으로 쓴다. 가는 길이 화면에 한 번도 걸치지 않을 때만 뺀다.
                log.Warning($"움직이는 박스가 {screen}에 한 번도 걸치지 않아 처리하지 않습니다.");
                keys.Clear();
            }
            if (keys.Count == 0) continue;
            string times = string.Join(", ", keys.Take(8).Select(k => TimeText.Format(k.Seconds))) + (keys.Count > 8 ? ", …" : "");
            string how = keys.Count == 1 ? "한 자리에 고정" : track.Jump ? "자리를 건너뛰며 옮김" : "사이를 직선으로 이어 감";
            log.Info($"움직이는 박스: 위치 {keys.Count}곳 ({times}), {how}");
        }
    }

    public async Task<JobResult> RunAsync(JobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        // 전체 경로로 바꾸면 CON.mp4 같은 이름은 장치 경로(\\.\CON)가 되어 "확장자가 틀렸다"는 엉뚱한 오류가 난다. 먼저 거른다.
        if (IsReservedName(request.OutputPath))
            throw new JobException($"Windows가 장치 이름으로 쓰는 이름(CON, PRN, AUX, NUL, COM1~9, LPT1~9)은 파일 이름으로 쓸 수 없습니다: {Path.GetFileName(request.OutputPath)}");
        string input = Path.GetFullPath(request.InputPath);
        string output = Path.GetFullPath(request.OutputPath);
        request = request with { InputPath = input, OutputPath = output };
        var options = request.Options;
        bool upscale = options.Upscale != UpscalePreset.None;

        if (!FfmpegCommands.IsSupportedOutput(output))
            throw new JobException("출력 파일 확장자는 .mp4, .mkv, .mov, .m4v 중 하나여야 합니다.");
        // Windows가 받지 않는 글자가 이름에 있으면 마지막 인코딩에서야 실패한다 (복원을 다 끝낸 뒤). 시작하기 전에 알린다.
        if (HasUnusableName(output))
            throw new JobException($"출력 경로에 파일 이름으로 쓸 수 없는 글자(: * ? \" < > | 등)가 있습니다: {output}");
        // 글자로만 비교하면 같은 파일을 다르게 적은 경우(드라이브 문자와 네트워크 경로, 정션 등)를 놓쳐 결과가 원본을 덮어쓴다.
        if (FileLinker.SameFile(input, output))
            throw new JobException("출력 파일이 입력 파일과 같습니다. 다른 이름을 지정하세요.");
        // 완성 전에는 "이름.partial.확장자"로 쓴다. 입력이 바로 그 이름이면 입력을 덮어쓰게 된다.
        if (FileLinker.SameFile(input, PartialPath(output)))
            throw new JobException("입력 파일 이름이 이 출력의 임시 파일 이름(.partial)과 같습니다. 출력 이름을 다르게 지정하세요.");
        if (options.Mode == CleanMode.None && !upscale)
            throw new JobException("할 일이 없습니다. 워터마크 제거 방식이나 업스케일 중 하나는 골라야 합니다.");
        if (options.Mode != CleanMode.None && request.Boxes.Count == 0 && request.Tracks.Count == 0)
            throw new JobException("워터마크 박스를 하나 이상 지정하세요.");
        // 길이 0은 ffmpeg에 "제한 없음"으로 넘어가 영상 전체를 처리하게 된다 — 앞부분만 시험하려던 것이 몇 시간짜리 작업이 된다.
        if (options.DurationSeconds is { } length && !(double.IsFinite(length) && length > 0))
            throw new JobException("처리할 길이는 0보다 큰 숫자여야 합니다.");
        if (options.StartSeconds is { } begin && !(double.IsFinite(begin) && begin >= 0))
            throw new JobException("시작 지점은 0 이상의 숫자여야 합니다.");
        // 화질·속도 값이 틀리면 마지막 인코딩에서야 실패한다 (복원을 다 끝낸 뒤). 시작하기 전에 알린다.
        if (options.Crf is < 0 or > FfmpegCommands.MaxCrf)
            throw new JobException($"화질(CRF)은 0~{FfmpegCommands.MaxCrf} 사이여야 합니다: {options.Crf}");
        if (!FfmpegCommands.Presets.Contains(options.Preset))
            throw new JobException($"인코딩 속도(preset)는 {string.Join(", ", FfmpegCommands.Presets)} 중 하나여야 합니다: {options.Preset}");

        UpscalePlan? plan = null;
        if (upscale)
        {
            try { plan = Video2X.Plan(options.Upscale, options.Scale); }
            catch (ArgumentException ex) { throw new JobException(ex.Message, ex); }
            if (plan.UsesPython)
            {
                if (string.IsNullOrWhiteSpace(settings.Python))
                    throw new JobException("옛 비디오 방식(RealBasicVSR)은 ProPainter용 파이썬 환경이 필요합니다. 설정 → 도구에서 [ProPainter 자동 준비]를 누르세요.");
                if (!File.Exists(RealBasicVsr.ScriptPath))
                    throw new JobException($"추론 스크립트가 없습니다: {RealBasicVsr.ScriptPath}");
            }
            else if (string.IsNullOrWhiteSpace(settings.Video2X) || !File.Exists(settings.Video2X))
                throw new JobException($"Video2X 실행 파일을 찾을 수 없습니다: {settings.Video2X}");
        }
        bool usesPython = options.Mode == CleanMode.ProPainter || plan is { UsesPython: true };
        // 파이썬·ProPainter를 둔 드라이브가 빠져 있으면 연결하라고 알린다 — "다시 준비하라"고 하면 다른 드라이브에 3 GB를 또 받는다.
        // (명령줄 setup --dir로 이름을 바꾼 환경은 드라이브가 없으면 우리 것인지조차 알 수 없어 HoldRuntime이 거르지 못한다.)
        if (usesPython && ProPainterSetup.MissingDrive(settings) is { } missingDrive)
            throw new JobException($"ProPainter 파이썬 환경이 있는 드라이브({missingDrive})를 찾을 수 없습니다. 외장 드라이브라면 연결한 뒤 다시 시작하세요.");
        // 우리가 준비한 파이썬 환경을 쓰는 작업은 끝날 때까지 "쓰는 중"으로 잡아 둔다 (다른 창에서 다시 준비해 지우지 못하게).
        using var runtime = usesPython ? HoldRuntime() : null;

        _progress = new ProgressPlan(progress, StagePlan(options.Mode, plan, plan is not null && ReencodesAtFinalize(plan, options)));
        _progress.Report(JobStage.Probe, 0);
        // 설정에 적힌 도구 대신 내장 도구를 쓰게 됐으면(적힌 파일이 없을 때) 조용히 바꾸지 말고 알린다.
        if (configured.Ffmpeg != settings.Ffmpeg && configured.Ffmpeg != "ffmpeg") _log.Warning($"설정의 ffmpeg({configured.Ffmpeg})를 찾을 수 없어 내장 도구를 씁니다.");
        if (upscale && configured.Video2X != settings.Video2X && configured.Video2X.Length > 0) _log.Warning($"설정의 Video2X({configured.Video2X})를 찾을 수 없어 내장 도구를 씁니다.");
        var info = await ProbeAsync(input, cancellationToken).ConfigureAwait(false);
        if (options.StartSeconds is { } start && info.DurationSeconds > 0 && start >= info.DurationSeconds)
            throw new JobException("시작 지점이 영상 길이보다 뒤에 있습니다.");
        // 결과는 yuv420p(색 정보를 2×2로 묶는 형식)로 인코딩하므로 홀수 크기는 담지 못한다. 몇 시간 처리한 뒤 마지막 인코딩에서 실패하기 전에 알린다.
        if (info.Width % 2 != 0 || info.Height % 2 != 0)
            throw new JobException($"가로·세로 크기가 홀수인 영상({info.Width}x{info.Height})은 처리할 수 없습니다. 먼저 짝수 크기로 바꿔 주세요.");
        DescribeBoxes(request, info, _log);
        EnsureWritableFolder(Path.GetDirectoryName(output)!);

        _log.Info(string.Create(CultureInfo.InvariantCulture,
            $"입력: {info.Width}x{info.Height}, {info.Fps} fps, {info.DurationSeconds:0.##}초, 오디오 {(info.HasAudio ? string.Join("/", info.AudioCodecs) : "없음")}"));
        if (FfmpegCommands.ConvertedAudio(info, output) is { Count: > 0 } converted)
            _log.Info($"이 출력 형식({Path.GetExtension(output)})에 그대로 담을 수 없는 오디오는 바꿔 담습니다: {string.Join(", ", converted)}");
        if (options.ShouldDeinterlace(info))
            _log.Info(info.IsInterlaced ? $"인터레이스 영상({info.FieldOrder}) — 빗살을 풀고 처리합니다." : "인터레이스 해제를 항상 켜서 처리합니다.");
        else if (info.IsInterlaced)
            _log.Warning($"인터레이스 영상({info.FieldOrder})인데 해제를 껐습니다. 빗살 무늬가 그대로 커집니다.");
        if (info.SubtitleCodecs.Count > 0 && !FfmpegCommands.KeepsSubtitles(output))
            _log.Warning("원본에 자막이 있지만 이 출력 형식에는 담지 않습니다. 자막을 유지하려면 출력을 .mkv로 지정하세요.");
        else if (FfmpegCommands.DroppedSubtitleCodecs(info) is { Count: > 0 } dropped)
            _log.Warning($"MKV에 옮겨 담을 수 없는 형식의 자막 {dropped.Count}개({string.Join(", ", dropped.Distinct())})는 빠집니다.");
        if (IsHighBitDepth(info.PixelFormat))
            _log.Warning($"원본은 10비트 이상 영상({info.PixelFormat})입니다. 결과는 8비트로 저장됩니다.");

        string partial = PartialPath(output);
        // 앞선 실행이 완성본을 제자리에 옮기지 못하고 이 이름으로 남겨 뒀을 수 있다 (PlaceOutput).
        // 이번 실행이 그 파일에 손대기 전에 실패하거나 취소되면 지우지 않는다.
        var leftover = FileStamp(partial);
        bool finished = false;
        try
        {
            int frames;
            string? workDirectory = null;
            if (options.Mode == CleanMode.Delogo && !upscale)
                frames = await RunDelogoAsync(request, info, partial, new EncodeOptions(options.Crf, options.Preset), JobStage.Delogo, cancellationToken).ConfigureAwait(false);
            else
                (frames, workDirectory) = await RunInWorkDirectoryAsync(request, info, plan, partial, cancellationToken).ConfigureAwait(false);

            // 여기부터 partial은 완성본이다 (중간 파일은 이미 지웠다). 제자리에 놓지 못해도 지우면 안 된다.
            finished = true;
            output = PlaceOutput(partial, output);
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
            if (!finished && !(leftover is { } before && before == FileStamp(partial))) TryDeleteFile(partial);
        }
    }

    /// <summary>
    /// 설정의 파이썬이 우리가 준비한 환경이면 그 환경을 "쓰는 중"으로 잡는다. 준비가 끝나 있지 않으면(다시 준비하다 중지·실패해
    /// 파이썬이 지워진 채) 영상 전체를 추출한 뒤 첫 조각에서 파이썬 오류로 실패하기 전에 알린다. 사용자가 직접 깐 파이썬이면 아무것도 하지 않는다.
    /// </summary>
    private FileStream? HoldRuntime()
    {
        if (ProPainterSetup.RootOf(settings) is not { } root) return null;
        // 준비는 끝났는데 드라이브를 뽑아 둔 것일 수 있다 — "다시 준비하라"고 하면 다른 드라이브에 3 GB를 또 받는다.
        if (!Directory.Exists(root))
            throw new JobException($"ProPainter 파이썬 환경 폴더를 찾을 수 없습니다: {root} — 외장 드라이브에 있다면 연결한 뒤 다시 시작하세요. 지웠다면 설정 → 도구에서 [ProPainter 자동 준비]를 누르세요 (명령줄은 setup).");
        FileStream? hold = null;
        try
        {
            hold = ProPainterSetup.HoldForUse(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 폴더가 없거나(뽑아 둔 드라이브) 쓸 수 없다 — 바로 아래 확인에서 걸러진다
        }
        if (ProPainterSetup.IsInstalled(root)) return hold;
        hold?.Dispose();
        // 앱은 알림 줄에 첫 줄만 보여 주므로 할 일까지 한 줄에 적는다.
        throw new JobException($"ProPainter 파이썬 환경 준비가 끝나지 않았습니다 (중지했거나 실패): {root} — 설정 → 도구에서 [ProPainter 자동 준비]를 다시 누르세요 (명령줄은 setup). ProPainter와 옛 비디오 방식에 필요합니다.");
    }

    /// <summary>
    /// 출력 폴더를 만들고 파일을 쓸 수 있는지 미리 확인한다. 폴더는 만들어지지만 파일은 못 만드는 곳(일반 권한으로 본 C:\ 바로 아래,
    /// 읽기 전용 공유 폴더 등)이면 작업 폴더까지 만들어 몇 시간 처리한 뒤 마지막 인코딩에서야 실패한다.
    /// </summary>
    private static void EnsureWritableFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            // 이름이 겹치지 않게 임의로 짓고, 닫히면 저절로 지워지게 연다 (남은 .partial 파일 보호와 엉키지 않는다).
            using var probe = new FileStream(Path.Combine(folder, $".nekovup-probe-{Guid.NewGuid():N}.tmp"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            bool driveRoot = Path.GetPathRoot(folder) is { } root
                && Path.TrimEndingDirectorySeparator(root).Equals(Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase);
            string hint = ex switch
            {
                DirectoryNotFoundException => "드라이브나 위쪽 폴더를 찾을 수 없습니다. 드라이브가 연결돼 있는지 확인하거나 다른 출력 폴더를 고르세요.",
                UnauthorizedAccessException when driveRoot => "다른 출력 폴더를 고르세요. 드라이브 바로 아래(C:\\ 등)는 관리자 권한이 있어야 쓸 수 있습니다.",
                _ => "다른 출력 폴더를 고르세요.",
            };
            throw new JobException($"출력 폴더에 파일을 만들 수 없습니다: {folder} ({ex.Message}) {hint}", ex);
        }
    }

    /// <summary>파일이 그대로인지 알아보기 위한 표시 (마지막으로 쓴 때와 크기). 없으면 null.</summary>
    private static (DateTime Written, long Length)? FileStamp(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? (file.LastWriteTimeUtc, file.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 완성본을 출력 이름으로 옮긴다. 그 이름의 파일을 다른 프로그램이 붙잡고 있으면(앞서 만든 결과를 재생기로 열어 둔 채 다시 돌린 경우)
    /// 덮어쓸 수 없다 — 몇 시간짜리 결과를 버리지 않고 옆에 다른 이름("이름 (2).mkv")으로 둔다.
    /// </summary>
    private string PlaceOutput(string partial, string output)
    {
        try
        {
            File.Move(partial, output, overwrite: true);
            return output;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string directory = Path.GetDirectoryName(output)!, stem = Path.GetFileNameWithoutExtension(output), extension = Path.GetExtension(output);
            for (int n = 2; n <= 20; n++)
            {
                string other = Path.Combine(directory, $"{stem} ({n}){extension}");
                if (File.Exists(other)) continue;
                try
                {
                    File.Move(partial, other);
                    _log.Warning($"출력 파일을 덮어쓰지 못해 ({ex.Message}) 다른 이름으로 저장했습니다: {other} — 다른 프로그램이 열어 두었거나 읽기 전용인지 확인하세요.");
                    return other;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw new JobException($"결과를 출력 이름으로 옮기지 못했습니다 ({ex.Message}). 완성된 파일은 여기 있습니다: {partial}", ex);
        }
    }

    // ───────────────────────── 작업 폴더를 쓰는 경로 (ProPainter, 업스케일) ─────────────────────────

    private async Task<(int Frames, string? WorkDirectory)> RunInWorkDirectoryAsync(JobRequest request, MediaInfo info, UpscalePlan? plan, string partial, CancellationToken cancellationToken)
    {
        var options = request.Options;
        var regions = MotionPlan.None;
        if (options.Mode == CleanMode.ProPainter)
        {
            ValidateProPainter(options);
            try
            {
                regions = MotionPlan.Create(request.Paths(info.Fps.Value), info.Width, info.Height, options.Region);
            }
            catch (ArgumentException ex)
            {
                throw new JobException(ex.Message, ex);
            }
            // 영역은 8의 배수 크기로만 잡을 수 있어, 그런 크기가 아닌 영상에서 화면 한쪽 끝에서 끝까지 걸친 박스는 가장자리가 영역 밖에 남는다.
            foreach (var region in regions.Regions)
            foreach (var box in region.Boxes)
                if (box.X < region.Rect.X || box.Y < region.Rect.Y || box.Right > region.Rect.Right || box.Bottom > region.Rect.Bottom)
                    _log.Warning($"박스 ({box.X},{box.Y}) {box.Width}x{box.Height}의 가장자리 몇 픽셀은 처리 영역({region.Rect.Width}x{region.Rect.Height}) 밖이라 복원되지 않습니다. 박스를 조금 줄이면 사라집니다.");
        }

        string key = JobManifest.ComputeKey(request);
        string work = JobWorkDirectory(request, key);
        string workRoot = Path.GetDirectoryName(work)!;
        Directory.CreateDirectory(work);
        // 작업이 끝나지 못하면 이 폴더가 남는다. 프로그램을 제거할 때 지울 수 있게 어디에 만들었는지 적어 둔다.
        // 사용자가 직접 정한 폴더는 적지 않는다 — 제거할 때 통째로 지우면 안 되는 폴더다.
        if (string.IsNullOrEmpty(request.WorkDirectory))
        {
            WorkFolders.Remember(workRoot);
            if (Path.GetFileName(workRoot).Equals(WorkFolders.FallbackFolderName, StringComparison.OrdinalIgnoreCase))
                _log.Info($"출력 폴더가 OneDrive 안이라, 중간 파일은 동기화되지 않게 {workRoot}에 둡니다.");
        }

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
                // 중간 파일(cleaned.mkv)은 원본의 색 범위를 그대로 지닌다 (AddEncode의 -color_range).
                bool fullRange = FfmpegCommands.SwsRange(info.ColorRange) == "pc";
                await UpscaleAsync(options, plan, manifest, work, cleaned, upscaled, frames, fullRange, cancellationToken).ConfigureAwait(false);
                await FinalizeAsync(request, info, plan, upscaled, partial, frames, cancellationToken).ConfigureAwait(false);
            }

            if (request.KeepWorkDirectory) return (frames, work);
        }
        finally
        {
            _log = outerLog;
            fileLog.Dispose();
        }

        // 성공했을 때만 여기로 온다: 중간 파일 정리. 이 작업의 폴더만 지우고, 그 위 폴더는 비었을 때만 지운다
        // (사용자가 지정한 폴더에 다른 파일이 있으면 그대로 둔다).
        TryDeleteDirectory(work);
        // 다른 드라이브로 돌려 둔 정션·링크(--work)는 비어도 남긴다 — 지우면 다음 작업은 원래 드라이브에 쓰게 된다.
        if (!IsLink(workRoot)) TryDeleteEmptyDirectory(workRoot);
        return (frames, null);
    }

    /// <summary>정션·심볼릭 링크인지. 알 수 없으면 링크로 본다 (지우지 않는 쪽).</summary>
    private static bool IsLink(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>업스케일러에 넣을 중간 영상(영상만, B프레임 없음)을 만든다. 이미 완성돼 있으면 건너뛴다.</summary>
    private async Task<int> MakeCleanedAsync(JobRequest request, MediaInfo info, MotionPlan regions, string work, JobManifest manifest, string cleaned, CancellationToken cancellationToken)
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
                var args = FfmpegCommands.Prepare(info, options.StartSeconds, options.DurationSeconds, encode, temp, options.ShouldDeinterlace(info));
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
        IReadOnlyList<BoxPath> paths;
        try
        {
            paths = DelogoPlanner.Plan(request.Paths(info.Fps.Value), info.Width, info.Height);
        }
        catch (ArgumentException ex)
        {
            throw new JobException(ex.Message, ex);
        }

        long total = info.EstimateFrames(options.StartSeconds, options.DurationSeconds);
        long last = 0;
        var args = FfmpegCommands.Delogo(info, paths, options.StartSeconds, options.DurationSeconds, encode, target, options.ShouldDeinterlace(info));
        // 움직이는 박스는 위치를 많이 적을수록 필터가 길어진다. 명령줄 길이 한도(약 3만 자)를 넘지 않게 길면 파일로 넘긴다.
        string? filterFile = null;
        int vf = args.IndexOf("-vf");
        if (vf >= 0 && args[vf + 1].Length > MaxInlineFilter)
        {
            filterFile = target + ".filter.txt";
            File.WriteAllText(filterFile, args[vf + 1], new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            (args[vf], args[vf + 1]) = ("-/vf", filterFile);
        }
        try
        {
            await RunFfmpegAsync(args, frame =>
            {
                last = frame;
                _progress.Report(stage, Fraction(frame, total));
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (filterFile is not null) TryDeleteFile(filterFile);
        }
        return (int)last;
    }

    /// <summary>이보다 긴 필터는 명령줄에 적지 않고 파일로 넘긴다 (ffmpeg <c>-/vf 파일</c>).</summary>
    private const int MaxInlineFilter = 2000;

    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
         .. "123456789¹²³".SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })], StringComparer.OrdinalIgnoreCase);

    /// <summary>파일 이름이 Windows의 장치 이름(확장자를 붙여도 장치로 열린다)인지.</summary>
    internal static bool IsReservedName(string path)
    {
        string name = Path.GetFileName(path);
        int dot = name.IndexOf('.');
        return ReservedNames.Contains((dot < 0 ? name : name[..dot]).TrimEnd(' '));
    }

    /// <summary>드라이브 표기 뒤의 폴더·파일 이름 가운데 Windows가 받지 않는 글자가 든 것이 있는지.</summary>
    internal static bool HasUnusableName(string fullPath)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string rest = fullPath[(Path.GetPathRoot(fullPath)?.Length ?? 0)..];
        return rest.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(name => name.IndexOfAny(invalid) >= 0);
    }

    // ───────────────────────── ProPainter ─────────────────────────

    private void ValidateProPainter(JobOptions options)
    {
        if (string.IsNullOrWhiteSpace(settings.Python))
            throw new JobException("ProPainter용 파이썬 경로가 설정되지 않았습니다.");
        if (string.IsNullOrWhiteSpace(settings.ProPainterDir) || !File.Exists(Path.Combine(settings.ProPainterDir, ProPainterScript)))
            throw new JobException($"ProPainter 폴더가 올바르지 않습니다 ({ProPainterScript}가 없음): {settings.ProPainterDir}");
        ValidateProPainterOptions(options);
    }

    /// <summary>
    /// ProPainter 숫자 옵션을 확인한다. 아래 계획 함수들의 .NET 기본 메시지는 영어라 그대로 보이면 안 된다 — 명령줄 plan도 이것으로 확인한다.
    /// </summary>
    public static void ValidateProPainterOptions(JobOptions options)
    {
        if (options.SubvideoLength < 1)
            throw new JobException($"ProPainter가 한 번에 볼 프레임 수(subvideo)는 1 이상이어야 합니다: {options.SubvideoLength}");
        if (options.Region.Pad < 0)
            throw new JobException($"박스 둘레 여백(pad)은 0 이상이어야 합니다: {options.Region.Pad}");
        if (options.Region.MaxArea < 64)
            throw new JobException($"처리 영역의 최대 넓이(max-area)는 64 이상이어야 합니다: {options.Region.MaxArea}");
        if (options.ChunkLength < 2)
            throw new JobException($"조각 길이(chunk)는 2 이상이어야 합니다: {options.ChunkLength}");
        if (options.Overlap < 0)
            throw new JobException($"조각 겹침(overlap)은 0 이상이어야 합니다: {options.Overlap}");
        try
        {
            ChunkPlanner.Plan(1, options.ChunkLength, options.Overlap); // 겹침이 조각의 절반을 넘는지
        }
        catch (ArgumentException ex)
        {
            throw new JobException(ex.Message, ex);
        }
    }

    /// <summary>영역 추출 → ProPainter → 병합 → 원본 위에 합성해서 <paramref name="target"/>에 인코딩한다.</summary>
    private async Task<int> CleanWithProPainterAsync(JobRequest request, MediaInfo info, MotionPlan motion, string work, JobManifest manifest, string target, EncodeOptions encode, CancellationToken cancellationToken)
    {
        var options = request.Options;
        var regions = motion.Regions;
        manifest.Regions = [.. regions.Select(r => r.Rect.ToString())];
        manifest.Save(work);
        for (int i = 0; i < regions.Count; i++)
        {
            var r = regions[i];
            string resize = r.NeedsResize
                ? string.Create(CultureInfo.InvariantCulture, $", 면적이 커서 {r.ResizeRatio:0.####}배로 줄여 처리")
                : "";
            _log.Info($"처리 영역 {i + 1}: 위치 ({r.Rect.X},{r.Rect.Y}) 크기 {r.Rect.Width}x{r.Rect.Height}{resize}{(motion.Moves(i) ? ", 움직이는 워터마크" : "")}");
            // 영역은 워터마크가 지나가는 자리를 모두 덮는다. 넓게 돌아다니면 영역이 커져 줄여서 처리하게 되고, 그만큼 흐려진다.
            if (motion.Moves(i) && r.NeedsResize)
                _log.Warning("워터마크가 넓은 범위를 움직여 처리 영역이 큽니다. 느리고 복원한 자리가 흐릿할 수 있습니다. 결과가 아쉬우면 '빠르게 (delogo)' 방식을 써 보세요.");
        }
        // 조각 안의 프레임 번호(0부터) → 원본 기준 프레임 번호. 움직이는 박스의 자리는 원본 기준으로 적혀 있다.
        long SourceFrame(int frame) => FrameGrid.SourceFrame(options.StartSeconds, frame, info.Fps.Value);

        long estimatedFrames = info.EstimateFrames(options.StartSeconds, options.DurationSeconds);
        if (!request.SkipDiskSpaceCheck && !manifest.Extracted)
            CheckDiskSpace(work, regions, estimatedFrames);

        // 1) 영역 프레임 추출
        int frameCount = await ExtractAsync(request, info, regions, work, manifest, estimatedFrames, cancellationToken).ConfigureAwait(false);
        var chunks = ChunkPlanner.Plan(frameCount, options.ChunkLength, options.Overlap);
        _log.Info($"프레임 {frameCount}장, 조각 {chunks.Count}개 (조각 길이 {options.ChunkLength}, 겹침 {options.Overlap})");

        // 2) ProPainter (영역 × 조각)
        string scratch = Path.Combine(settings.ProPainterDir, ProPainterScratch, ScratchName(work));
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

                int region = i, skipped = 0;
                Func<int, IReadOnlyList<PixelRect>>? maskAt = motion.Moves(i) ? frame => motion.MaskBoxes(region, SourceFrame(frame)) : null;
                foreach (var chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string detail = $"영역 {i + 1}/{regions.Count}, 조각 {chunk.Index + 1}/{chunks.Count}";
                    _progress.Report(JobStage.Inpaint, (double)doneChunks / totalChunks, detail);
                    // 이 조각 내내 워터마크가 다른 데 가 있으면 복원할 것이 없다 (병합 때 투명한 프레임을 쓴다).
                    if (maskAt is not null && !Enumerable.Range(chunk.Start, chunk.Length).Any(frame => maskAt(frame).Count > 0))
                        skipped++;
                    else
                        await InpaintChunkAsync(work, scratch, i, regions[i], chunk, maskAt, options, cancellationToken).ConfigureAwait(false);
                    doneChunks++;
                    _progress.Report(JobStage.Inpaint, (double)doneChunks / totalChunks, detail);
                }
                if (skipped > 0) _log.Info($"영역 {i + 1}: 워터마크가 지나가지 않는 조각 {skipped}개는 건너뛰었습니다.");
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
            Func<int, byte[]?> alphaAt;
            if (motion.Moves(i))
            {
                var moving = new MovingAlpha(region.Rect.Width, region.Rect.Height, options.MaskDilation, region.ResizeRatio);
                alphaAt = frame => moving.For(motion.MaskBoxes(index, SourceFrame(frame)));
            }
            else
            {
                var alpha = MaskBuilder.BuildAlpha(region, options.MaskDilation, region.ResizeRatio);
                alphaAt = _ => alpha;
            }
            var mergeProgress = new SynchronousProgress<int>(done =>
                _progress.Report(JobStage.Merge, (index + (double)done / frameCount) / regions.Count));
            try
            {
                await Task.Run(() => ChunkMerger.Merge(InpaintDir(work, index), chunks, OutDir(work, index),
                    region.Rect.Width, region.Rect.Height, alphaAt, mergeProgress, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (JobException ex)
            {
                // "작업 폴더를 지우라"는 안내에 어느 폴더인지 붙인다 (앱에서는 작업 폴더가 보이지 않는다).
                throw new JobException($"{ex.Message} (작업 폴더: {work})", ex);
            }

            if (CountPng(OutDir(work, i)) != frameCount)
                throw new JobException($"병합된 프레임 수가 맞지 않습니다. 작업 폴더를 지우고 다시 시도하세요: {work}");
            manifest.MergedRegions.Add(i);
            manifest.Save(work);
            // 병합이 끝난 영역의 원본·복원 프레임은 더 필요 없다.
            TryDeleteDirectory(SrcDir(work, i));
            TryDeleteDirectory(InpaintDir(work, i));
        }

        // 4) 원본 위에 얹어 인코딩
        var overlays = regions.Select((r, i) => (r.Rect, OutDir(work, i))).ToList();
        var args = FfmpegCommands.Composite(info, overlays, options.StartSeconds, options.DurationSeconds, encode, target, options.ShouldDeinterlace(info));
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
        var args = FfmpegCommands.ExtractRegions(info, targets, request.Options.StartSeconds, request.Options.DurationSeconds, request.Options.ShouldDeinterlace(info));
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
    /// <param name="maskAt">움직이는 워터마크면 프레임(구간 안 번호)마다 칠할 사각형. 고정이면 null — 마스크 한 장을 모든 프레임에 쓴다.</param>
    private async Task InpaintChunkAsync(string work, string scratch, int regionIndex, ProcessRegion region, ChunkRange chunk, Func<int, IReadOnlyList<PixelRect>>? maskAt, JobOptions options, CancellationToken cancellationToken)
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
        // 움직이는 워터마크는 프레임마다 마스크가 달라, 프레임과 같은 이름의 마스크를 폴더에 담아 넘긴다 (ProPainter가 이름순으로 짝짓는다).
        string maskPath = maskAt is null ? Path.Combine(regionScratch, "mask.png") : Path.Combine(regionScratch, "mask", name);
        TryDeleteDirectory(inputDir);
        TryDeleteDirectory(Path.Combine(outputRoot, name));
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputRoot);
        if (maskAt is null)
        {
            PngCodec.Write(maskPath, MaskBuilder.BuildMask(region), region.Rect.Width, region.Rect.Height, 1);
        }
        else
        {
            TryDeleteDirectory(maskPath);
            Directory.CreateDirectory(maskPath);
        }

        // 추출해 둔 프레임을 하드링크로 모은다 (다른 드라이브면 복사).
        string src = SrcDir(work, regionIndex);
        for (int frame = chunk.Start; frame < chunk.End; frame++)
        {
            string file = ChunkMerger.SequenceFrameName(frame);
            FileLinker.LinkOrCopy(Path.Combine(src, file), Path.Combine(inputDir, file));
            if (maskAt is not null)
                PngCodec.Write(Path.Combine(maskPath, file), MaskBuilder.BuildMask(region.Rect.Width, region.Rect.Height, maskAt(frame)), region.Rect.Width, region.Rect.Height, 1);
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
        if (maskAt is not null) TryDeleteDirectory(maskPath);
        _log.Info(string.Create(CultureInfo.InvariantCulture, $"영역 {regionIndex + 1} 조각 {name}: {chunk.Length}장 복원 ({clock.Elapsed.TotalSeconds:0.0}초)"));
    }

    // ───────────────────────── 업스케일 ─────────────────────────

    private async Task UpscaleAsync(JobOptions options, UpscalePlan plan, JobManifest manifest, string work, string cleaned, string upscaled, int frames, bool fullRange, CancellationToken cancellationToken)
    {
        // 뒤에 줄이거나 선명도를 거는 단계가 있으면 여기 결과는 중간물이므로 화질을 높게 잡는다.
        var encode = ReencodesAtFinalize(plan, options) ? new EncodeOptions(EncodeOptions.IntermediateCrf, options.Preset) : new EncodeOptions(options.Crf, options.Preset);
        // 화소 형식도 넣는다 — 범위를 잘못 다루던 때 만든 결과를 이어 하기에서 그대로 쓰지 않게.
        string upscaleKey = string.Create(CultureInfo.InvariantCulture, $"{plan.Processor}|{plan.Model}|{plan.ModelScale}|{encode.Crf}|{encode.Preset}|{Video2X.PixelFormat(fullRange)}");
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

        _log.Info($"업스케일: {plan.Model} {plan.ModelScale}배, {frames}장");
        _progress.Report(JobStage.Upscale, 0);
        var clock = Stopwatch.StartNew();
        if (plan.UsesPython)
            await RunRealBasicVsrAsync(options, cleaned, temp, frames, encode, clock, cancellationToken).ConfigureAwait(false);
        else
            await RunVideo2XAsync(plan, options, cleaned, temp, frames, encode, fullRange, clock, cancellationToken).ConfigureAwait(false);

        int produced = File.Exists(temp) ? await CountFramesAsync(temp, cancellationToken).ConfigureAwait(false) : 0;
        if (produced != frames)
            throw new ToolFailedException($"업스케일 결과 프레임 수가 다릅니다 (기대 {frames}, 실제 {produced}).");

        File.Move(temp, upscaled, overwrite: true);
        manifest.UpscaledKey = upscaleKey;
        manifest.Save(work);
        _log.Info(string.Create(CultureInfo.InvariantCulture, $"업스케일 끝: {frames}장, {clock.Elapsed.TotalSeconds:0}초 (초당 {frames / Math.Max(clock.Elapsed.TotalSeconds, 0.001):0.0}장)"));
    }

    private async Task RunVideo2XAsync(UpscalePlan plan, JobOptions options, string cleaned, string temp, int frames, EncodeOptions encode, bool fullRange, Stopwatch clock, CancellationToken cancellationToken)
    {
        var invocation = new ToolInvocation(settings.Video2X, Video2X.Arguments(plan, cleaned, temp, options.GpuIndex, encode, fullRange));
        _log.Detail("> " + invocation.DisplayText);

        bool finished = false;
        int[] pixelFormatWarnings = [0]; // 출력·오류 두 줄기가 따로 부르므로 Interlocked로 센다
        // 전체 범위(0~255) 영상이면 Video2X 안의 swscaler가 프레임마다 "deprecated pixel format used" 경고를 낸다 —
        // 두 시간짜리 영상이면 job.log가 수십 MB가 되므로 처음 한 번만 남긴다.
        void Detail(string line)
        {
            if (line.Contains("deprecated pixel format used", StringComparison.Ordinal) && Interlocked.Increment(ref pixelFormatWarnings[0]) > 1) return;
            _log.Detail(line);
        }
        var result = await runner.RunAsync(invocation, line =>
        {
            if (Video2X.TryParseProgress(line, out long frame))
            {
                ReportUpscaleFrame(frame, frames, clock);
                return;
            }
            if (line.Contains(Video2X.DoneMarker, StringComparison.Ordinal)) finished = true;
            if (!string.IsNullOrWhiteSpace(line)) Detail(Video2X.StripAnsi(line));
        }, Detail, cancellationToken).ConfigureAwait(false);

        // Video2X 6.4.0은 정상적으로 끝낸 뒤에도 종료 과정에서 죽어 0이 아닌 코드를 내는 일이 있다(실측).
        // 그래서 종료 코드가 아니라 "완료 문구 + 결과 프레임 수"로 성공을 판단한다.
        if (result.ExitCode != 0 && !finished)
            throw new ToolFailedException($"Video2X가 오류 코드 {result.ExitCode}로 끝났습니다.{Environment.NewLine}{result.ErrorTail}", result.ExitCode);
        if (result.ExitCode != 0)
            _log.Detail($"Video2X가 작업을 마친 뒤 종료 코드 {result.ExitCode}를 냈습니다. 결과 파일을 검사합니다.");
    }

    private static readonly HttpClient Http = new();

    /// <summary>RealBasicVSR: 모델 가중치를 확인(없으면 내려받기)하고 파이썬 스크립트로 돌린다. 입력 크기는 중간 영상(cleaned)의 크기 = 원본 크기.</summary>
    private async Task RunRealBasicVsrAsync(JobOptions options, string cleaned, string temp, int frames, EncodeOptions encode, Stopwatch clock, CancellationToken cancellationToken)
    {
        await RealBasicVsr.EnsureWeightsAsync(Http, (done, total) =>
            _progress.Report(JobStage.Upscale, 0, total > 0
                ? string.Create(CultureInfo.InvariantCulture, $"모델 내려받는 중 {done / 1048576.0:0}/{total / 1048576.0:0} MB")
                : "모델 내려받는 중"), _log.Info, cancellationToken).ConfigureAwait(false);

        var cleanedInfo = await ProbeAsync(cleaned, cancellationToken).ConfigureAwait(false);
        var invocation = new ToolInvocation(settings.Python,
            RealBasicVsr.Arguments(settings.Ffmpeg, cleaned, temp, cleanedInfo.Width, cleanedInfo.Height, frames, cleanedInfo.Fps, encode,
                cleanedInfo.ColorSpace, cleanedInfo.ColorRange))
        {
            Environment = Runtime.ProPainterSetup.PythonEnvironment,
        };
        _log.Detail("> " + invocation.DisplayText);
        var result = await runner.RunAsync(invocation, line =>
        {
            if (RealBasicVsr.TryParseProgress(line, out long frame)) { ReportUpscaleFrame(frame, frames, clock); return; }
            if (line.StartsWith("warning:", StringComparison.Ordinal)) _log.Warning(line["warning:".Length..].Trim());
            else if (!string.IsNullOrWhiteSpace(line)) _log.Detail(line);
        }, line => _log.Detail(line), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ToolFailedException($"RealBasicVSR가 오류 코드 {result.ExitCode}로 끝났습니다.{Environment.NewLine}{result.ErrorTail}", result.ExitCode);
    }

    private void ReportUpscaleFrame(long frame, int frames, Stopwatch clock) =>
        _progress.Report(JobStage.Upscale, Fraction(frame, frames),
            frame > 0 ? string.Create(CultureInfo.InvariantCulture, $"{frame}/{frames}장, 초당 {frame / Math.Max(clock.Elapsed.TotalSeconds, 0.001):0.0}장") : null);

    private async Task FinalizeAsync(JobRequest request, MediaInfo info, UpscalePlan plan, string upscaled, string partial, int frames, CancellationToken cancellationToken)
    {
        var options = request.Options;
        (int Width, int Height)? scaleTo = plan.NeedsDownscale ? (info.Width * plan.FinalScale, info.Height * plan.FinalScale) : null;
        int outputHeight = info.Height * plan.FinalScale;
        if (options.Sharpen > 0) _log.Info($"선명도 {options.Sharpen} 적용 ({FfmpegCommands.SharpenFilter(options.Sharpen)})");
        var args = FfmpegCommands.Finalize(info, upscaled, scaleTo, outputHeight, options.StartSeconds, options.DurationSeconds,
            new EncodeOptions(options.Crf, options.Preset), partial, options.Sharpen);
        await RunFfmpegAsync(args, frame => _progress.Report(JobStage.Finalize, Fraction(frame, frames)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>마지막 단계에서 영상을 다시 인코딩하는지 (줄이기 또는 선명도). 아니면 업스케일 결과를 그대로 옮긴다.</summary>
    private static bool ReencodesAtFinalize(UpscalePlan plan, JobOptions options) => plan.NeedsDownscale || options.Sharpen > 0;

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
                    $"임시 공간이 부족합니다. 약 {Gb(needed):0.0} GB가 필요한데 {drive.Name}에 {Gb(free):0.0} GB만 남았습니다. 공간을 비우거나, 출력 파일을 여유가 있는 드라이브에 지정하거나(작업 폴더는 출력 폴더 안에 생긴다, 명령줄은 --work), 처리 구간을 줄이세요."));
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

    /// <summary>
    /// 이 작업의 중간 파일 폴더: &lt;뿌리&gt;\&lt;입력 이름&gt;-&lt;작업 키&gt;. 뿌리는 기본이 출력 폴더의 <c>.nekovup</c>이고,
    /// 사용자가 폴더를 지정했으면(<see cref="JobRequest.WorkDirectory"/>) 그 폴더다. 지정한 폴더를 통째로 쓰지 않고 그 안에 작업별 폴더를 만드는 것은,
    /// 작업이 끝나면 중간 폴더를 지우기 때문이다 — 이미 다른 파일이 있는 폴더를 지정해도 그 파일은 건드리지 않는다.
    /// </summary>
    public static string JobWorkDirectory(JobRequest request, string jobKey) =>
        JobWorkDirectory(request, jobKey, SyncFolders.Roots, WorkFolders.FallbackRoot);

    /// <summary>
    /// 작업 폴더: 지정한 곳(--work), 아니면 출력 폴더 옆 <c>.nekovup</c>. 출력 폴더가 OneDrive 안이면 중간 PNG 수십 GB가
    /// 동기화되지 않게 밖(<paramref name="fallbackRoot"/>, 보통 <c>X:\NekoVup-work</c>)에 둔다 (사용자 결정, 2026-11-10).
    /// </summary>
    internal static string JobWorkDirectory(JobRequest request, string jobKey, IReadOnlyList<string> syncRoots, Func<string, string> fallbackRoot)
    {
        string stem = Path.GetFileNameWithoutExtension(request.InputPath);
        // 긴 이름은 자른다. 글자 하나가 두 칸을 쓰는 경우(이모지 등) 그 가운데를 자르면 만들 수 없는 폴더 이름이 된다.
        if (stem.Length > 40) stem = stem[..(char.IsHighSurrogate(stem[39]) ? 39 : 40)];
        string name = $"{stem.Trim()}-{jobKey[..10]}";
        string outputFolder = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!;
        string root = request.WorkDirectory is { Length: > 0 } custom ? Path.GetFullPath(custom)
            : SyncFolders.Contains(outputFolder, syncRoots) ? fallbackRoot(name)
            : Path.Combine(outputFolder, WorkFolders.FolderName);
        return Path.Combine(root, name);
    }

    /// <summary>
    /// ProPainter 폴더 안 임시 폴더의 이름 (영문·숫자만). 작업 폴더마다 다르다 — 작업 키만으로 지으면,
    /// 같은 입력·옵션을 다른 출력 폴더로 동시에 돌릴 때 두 작업이 서로의 파일을 지운다.
    /// </summary>
    public static string ScratchName(string workDirectory) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(workDirectory).ToUpperInvariant())))[..12];

    /// <summary>10비트 이상 픽셀 형식인지 (yuv420p10le, p010le 등).</summary>
    public static bool IsHighBitDepth(string? pixelFormat) =>
        pixelFormat is not null && (System.Text.RegularExpressions.Regex.IsMatch(pixelFormat, @"p(9|1[0246])(le|be)?$") || pixelFormat.StartsWith("p01", StringComparison.Ordinal));

    /// <summary>작업 종류에 따른 단계 목록과, 전체 진행률에서 각 단계가 차지하는 비중(대략의 소요 시간 비).</summary>
    private static List<(JobStage Stage, double Weight)> StagePlan(CleanMode mode, UpscalePlan? plan, bool reencodesAtFinalize)
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
            stages.Add((JobStage.Finalize, reencodesAtFinalize ? 25 : 2));
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
