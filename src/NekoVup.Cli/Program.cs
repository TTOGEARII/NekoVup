using System.Globalization;
using System.Text;
using NekoVup.Core.Chunks;
using NekoVup.Core.Media;
using NekoVup.Core.Pipeline;
using NekoVup.Core.Processes;
using NekoVup.Core.Regions;
using NekoVup.Core.Runtime;
using NekoVup.Core.Settings;

namespace NekoVup.Cli;

internal static class Program
{
    private const int ExitOk = 0, ExitFailed = 1, ExitUsage = 2, ExitCancelled = 130;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        // "nekovup run --help"처럼 명령 뒤에 적어도 도움말을 보여 준다 (그냥 두면 "--help 뒤에 값이 필요합니다"가 된다).
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help" || args.Skip(1).Any(a => a is "-h" or "--help"))
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? ExitUsage : ExitOk;
        }

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        try
        {
            var options = CommandLine.Parse(args.AsSpan(1));
            return args[0].ToLowerInvariant() switch
            {
                "probe" => await ProbeAsync(options, cancel.Token),
                "plan" => await PlanAsync(options, cancel.Token),
                "run" => await RunAsync(options, cancel.Token),
                "check" => await CheckAsync(options, cancel.Token),
                "setup" => await SetupAsync(options, cancel.Token),
                "settings" => Settings(options),
                _ => throw new UsageException($"알 수 없는 명령: {args[0]}"),
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine("사용법 오류: " + ex.Message);
            Console.Error.WriteLine("도움말: nekovup --help");
            return ExitUsage;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("취소되었습니다.");
            return ExitCancelled;
        }
        catch (Exception ex) when (ex is JobException or ToolFailedException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 디스크가 꽉 찼거나 폴더에 쓸 수 없을 때, 값이 말이 안 될 때(빈 경로, 쓸 수 없는 글자가 든 경로, --overlap이 --chunk의 절반 초과)도 오류 추적 대신 한 줄로 알린다.
            Console.Error.WriteLine("오류: " + ex.Message);
            return ExitFailed;
        }
        catch (Exception ex)
        {
            // 예상하지 못한 오류도 오류 추적을 쏟아내며 죽지 않게 한 줄로 알린다 (추적은 --verbose일 때만).
            Console.Error.WriteLine($"오류: 예기치 못한 오류입니다 ({ex.GetType().Name}): {ex.Message}");
            if (args.Contains("--verbose")) Console.Error.WriteLine(ex);
            return ExitFailed;
        }
    }

    private static async Task<int> ProbeAsync(CommandLine cl, CancellationToken ct)
    {
        var settings = LoadSettings(cl);
        string input = cl.RequireInput();
        cl.EnsureAllUsed(); // 오타는 영상을 읽기 전에 알린다
        var info = await new JobRunner(settings, new ToolRunner(), NullJobLog.Instance).ProbeAsync(input, ct);
        Console.WriteLine($"파일      : {info.Path}");
        Console.WriteLine($"크기      : {info.Width}x{info.Height}" + (info.Rotation != 0 ? $" (저장 {info.CodedWidth}x{info.CodedHeight}, 회전 {info.Rotation}°)" : ""));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"프레임레이트: {info.Fps} ({info.Fps.Value:0.###} fps)"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"길이      : {info.DurationSeconds:0.###}초 (약 {info.EstimateFrames()}프레임)"));
        Console.WriteLine($"영상 코덱 : {info.VideoCodec} / {info.PixelFormat}");
        Console.WriteLine($"색 정보   : 공간 {info.ColorSpace ?? "표기 없음"}, 범위 {info.ColorRange ?? "표기 없음"}");
        Console.WriteLine($"오디오    : {(info.HasAudio ? string.Join(", ", info.AudioCodecs) : "없음")}");
        return ExitOk;
    }

    private static async Task<int> PlanAsync(CommandLine cl, CancellationToken ct)
    {
        var settings = LoadSettings(cl);
        var request = BuildRequest(cl, requireOutput: false);
        cl.EnsureAllUsed();
        var info = await new JobRunner(settings, new ToolRunner(), NullJobLog.Instance).ProbeAsync(request.InputPath, ct);
        var o = request.Options;
        // 화면 밖이라 빠지는 박스는 run과 같이 알린다 (영역이 왜 그렇게 잡혔는지 알 수 있게). delogo로 지울 때도 알린다.
        JobRunner.DescribeBoxes(request, info, new DelegateJobLog((level, message) =>
        {
            if (level >= LogLevel.Warning) Console.Error.WriteLine("경고: " + message);
        }));
        if (o.Mode != CleanMode.ProPainter)
        {
            // 영역 계획은 ProPainter에만 있다 — 박스 없이 업스케일만 고르면 박스가 없다는 엉뚱한 오류가 났다.
            Console.WriteLine($"영상 {info.Width}x{info.Height}, {info.Fps} fps — plan은 ProPainter로 지울 때의 처리 영역·조각만 보여 줍니다 (지금은 --mode {o.Mode.ToString().ToLowerInvariant()}).");
            return ExitOk;
        }
        JobRunner.ValidateProPainterOptions(o);

        MotionPlan plan;
        try
        {
            plan = MotionPlan.Create(request.Paths(info.Fps.Value), info.Width, info.Height, o.Region);
        }
        catch (ArgumentException ex)
        {
            throw new JobException(ex.Message, ex);
        }
        var regions = plan.Regions;

        long frames = info.EstimateFrames(o.StartSeconds, o.DurationSeconds);
        var chunks = ChunkPlanner.Plan((int)frames, o.ChunkLength, o.Overlap);
        Console.WriteLine($"영상 {info.Width}x{info.Height}, {info.Fps} fps, 처리 구간 약 {frames}프레임");
        for (int i = 0; i < regions.Count; i++)
        {
            var r = regions[i];
            string resize = r.NeedsResize
                ? string.Create(CultureInfo.InvariantCulture, $" → 상한 {o.Region.MaxArea:N0}px 초과, {r.ResizeRatio:0.####}배로 줄여 처리")
                : "";
            // 움직이는 박스는 지나가는 자리마다 하나씩 들어 있어서 그린 박스 수와 다르다.
            string boxes = plan.Moves(i) ? $"워터마크가 움직이는 영역, 지나가는 자리 {r.Boxes.Count}곳" : $"박스 {r.Boxes.Count}개";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"영역 {i + 1}: x={r.Rect.X} y={r.Rect.Y} {r.Rect.Width}x{r.Rect.Height} (면적 {r.Rect.Area:N0}px, {boxes}){resize}"));
        }
        Console.WriteLine($"조각 {chunks.Count}개: " + string.Join(" ", chunks.Take(8).Select(c => $"[{c.Start},{c.End})")) + (chunks.Count > 8 ? " …" : ""));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"임시 공간 약 {JobRunner.EstimateTempBytes(regions, frames) / 1024.0 / 1024.0 / 1024.0:0.00} GB 필요"));
        return ExitOk;
    }

    private static async Task<int> RunAsync(CommandLine cl, CancellationToken ct)
    {
        var settings = LoadSettings(cl);
        bool verbose = cl.Flag("--verbose");
        var request = BuildRequest(cl, requireOutput: true);
        cl.EnsureAllUsed();
        // 끝나지 못한 작업의 중간 파일 폴더를 적어 둔다 (제거할 때 지우려고). 설정 파일을 따로 준 실행(시험용)은 사용자 폴더에 적지 않는다.
        if (cl.Value("--settings") is null) WorkFolders.RegistryPath = WorkFolders.DefaultRegistryPath;

        var gate = new Lock();
        var log = new DelegateJobLog((level, message) =>
        {
            if (level == LogLevel.Detail && !verbose) return;
            lock (gate)
            {
                var writer = level >= LogLevel.Warning ? Console.Error : Console.Out;
                writer.WriteLine(level switch
                {
                    LogLevel.Detail => "    " + message,
                    LogLevel.Warning => "경고: " + message,
                    LogLevel.Error => "오류: " + message,
                    _ => message,
                });
            }
        });

        var progress = new ConsoleProgress(gate);
        var result = await new JobRunner(settings, new ToolRunner(), log).RunAsync(request, progress, ct);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"끝: {result.FrameCount}프레임, {result.Elapsed.TotalSeconds:0.0}초 걸림 → {result.OutputPath}"));
        if (result.WorkDirectory is { } work) Console.WriteLine($"중간 파일: {work}");
        return ExitOk;
    }

    private static async Task<int> CheckAsync(CommandLine cl, CancellationToken ct)
    {
        var settings = LoadSettings(cl);
        string? work = cl.Value("--work");
        cl.EnsureAllUsed();
        var items = await new EnvironmentChecker(settings, new ToolRunner()).RunAsync(work ?? Environment.CurrentDirectory, ct);
        foreach (var item in items)
        {
            string mark = item.Status switch { CheckStatus.Ok => "[ 정상 ]", CheckStatus.Warning => "[ 주의 ]", _ => "[ 실패 ]" };
            Console.WriteLine($"{mark} {item.Name}: {item.Detail}");
        }
        return items.Any(i => i.Status == CheckStatus.Error) ? ExitFailed : ExitOk;
    }

    /// <summary>ProPainter 파이썬 환경을 내려받아 준비하고 설정에 적는다 (창 화면 판은 처음 실행 때 자동으로 한다).</summary>
    private static async Task<int> SetupAsync(CommandLine cl, CancellationToken ct)
    {
        string? settingsPath = cl.Value("--settings");
        bool force = cl.Flag("--force");
        bool verbose = cl.Flag("--verbose");
        var settings = ReadSettings(settingsPath);
        // 상대 경로를 그대로 설정에 적으면 다른 폴더에서 실행할 때 못 찾는다.
        string root = Path.GetFullPath(cl.Value("--dir") ?? ProPainterSetup.ResolveRoot(settings));
        cl.EnsureAllUsed();

        if (!force && ProPainterSetup.IsInstalled(root))
        {
            Console.WriteLine($"이미 준비되어 있습니다: {root} (다시 하려면 --force)");
        }
        else
        {
            var gate = new Lock();
            var log = new DelegateJobLog((level, message) =>
            {
                if (level == LogLevel.Detail && !verbose) return;
                lock (gate) Console.WriteLine(level >= LogLevel.Warning ? "경고: " + message : message);
            });
            string? last = null;
            var progress = new Progress<SetupProgress>(p =>
            {
                string line = string.Create(CultureInfo.InvariantCulture, $"[{p.Fraction * 100,3:0}%] {p.Step}{(p.Detail is null ? "" : " — " + p.Detail)}");
                if (line == last) return;
                last = line;
                lock (gate) Console.WriteLine(line);
            });
            using var http = new HttpClient();
            await new ProPainterSetup(new ToolRunner(), http, log).InstallAsync(root, progress, ct);
        }

        ProPainterSetup.Apply(settings, root);
        SettingsStore.Save(settings, settingsPath);
        Console.WriteLine($"설정에 적었습니다: python = {settings.Python}, propainter = {settings.ProPainterDir}");
        return ExitOk;
    }

    private static int Settings(CommandLine cl)
    {
        string? path = cl.Value("--settings");
        var settings = ReadSettings(path);
        bool changed = ApplyToolOverrides(cl, settings);
        cl.EnsureAllUsed();
        if (changed) SettingsStore.Save(settings, path);
        // 내장 도구가 쓰이는 칸은 실제로 쓰는 경로를 함께 보여 준다.
        var effective = BundledTools.Resolve(settings);
        Console.WriteLine($"설정 파일: {path ?? SettingsStore.DefaultPath}{(changed ? " (저장됨)" : "")}");
        Console.WriteLine($"  ffmpeg     = {Show(settings.Ffmpeg, effective.Ffmpeg)}");
        Console.WriteLine($"  ffprobe    = {Show(settings.Ffprobe, effective.Ffprobe)}");
        Console.WriteLine($"  python     = {settings.Python}");
        Console.WriteLine($"  propainter = {settings.ProPainterDir}");
        Console.WriteLine($"  video2x    = {Show(settings.Video2X, effective.Video2X)}");
        return ExitOk;

        static string Show(string configured, string effective) =>
            configured == effective ? configured : $"{configured}  → 내장 도구 {effective}";
    }

    private static AppSettings LoadSettings(CommandLine cl)
    {
        var settings = ReadSettings(cl.Value("--settings"));
        ApplyToolOverrides(cl, settings);
        return settings;
    }

    /// <summary>설정 파일을 읽는다. 읽지 못했으면 기본값으로 하되 알린다 (조용히 넘어가면 적어 둔 경로가 왜 안 먹는지 모른다).</summary>
    private static AppSettings ReadSettings(string? path)
    {
        var settings = SettingsStore.Load(path, out string? error);
        if (error is not null) Console.Error.WriteLine("경고: 설정 파일을 읽지 못해 기본값을 씁니다: " + error);
        return settings;
    }

    private static bool ApplyToolOverrides(CommandLine cl, AppSettings settings)
    {
        bool changed = false;
        if (cl.Value("--ffmpeg") is { } ffmpeg) (settings.Ffmpeg, changed) = (ffmpeg, true);
        if (cl.Value("--ffprobe") is { } ffprobe) (settings.Ffprobe, changed) = (ffprobe, true);
        if (cl.Value("--python") is { } python) (settings.Python, changed) = (python, true);
        if (cl.Value("--propainter") is { } dir) (settings.ProPainterDir, changed) = (Path.GetFullPath(dir), true);
        if (cl.Value("--video2x") is { } video2x) (settings.Video2X, changed) = (video2x, true);
        return changed;
    }

    private static JobRequest BuildRequest(CommandLine cl, bool requireOutput)
    {
        string input = cl.RequireInput();
        // 둘 다 읽어 둔다 (하나만 읽으면 같이 적었을 때 다른 하나가 "알 수 없는 옵션"으로 걸린다).
        string? shortOutput = cl.Value("-o"), longOutput = cl.Value("--output");
        string? output = shortOutput ?? longOutput;
        if (requireOutput && output is null) throw new UsageException("출력 파일을 -o 로 지정하세요.");

        var boxes = new List<PixelRect>();
        foreach (var text in cl.Values("--box"))
        {
            if (!PixelRect.TryParse(text, out var box) || box.IsEmpty)
                throw new UsageException($"--box 형식은 x,y,w,h 입니다: '{text}'");
            boxes.Add(box);
        }
        var tracks = new List<BoxTrack>();
        foreach (string name in (string[])["--move", "--jump"])
        {
            foreach (var text in cl.Values(name))
            {
                if (!BoxTrack.TryParse(text, name == "--jump", out var track))
                    throw new UsageException($"{name} 형식은 \"시각=x,y,w,h;시각=x,y,w,h\" 입니다 (시각은 분:초나 초): '{text}'");
                tracks.Add(track);
            }
        }
        bool noPositions = boxes.Count == 0 && tracks.Count == 0;

        var upscale = (cl.Value("--upscale") ?? "none").ToLowerInvariant() switch
        {
            "none" => UpscalePreset.None,
            "anime" => UpscalePreset.Anime,
            "anime-hq" => UpscalePreset.AnimeHq,
            "real" => UpscalePreset.Real,
            "old" or "old-video" => UpscalePreset.OldVideo,
            var other => throw new UsageException($"--upscale 은 none, anime, anime-hq, real, old 중 하나입니다: '{other}'"),
        };
        var deinterlace = (cl.Value("--deinterlace") ?? "auto").ToLowerInvariant() switch
        {
            "auto" => Deinterlace.Auto,
            "on" or "always" => Deinterlace.Always,
            "off" or "never" => Deinterlace.Never,
            var other => throw new UsageException($"--deinterlace 는 auto, on, off 중 하나입니다: '{other}'"),
        };
        // 박스 없이 업스케일만 고르면 워터마크 제거는 하지 않는다.
        string modeText = cl.Value("--mode") ?? (noPositions && upscale != UpscalePreset.None ? "none" : "propainter");
        var mode = modeText.ToLowerInvariant() switch
        {
            "propainter" => CleanMode.ProPainter,
            "delogo" => CleanMode.Delogo,
            "none" => CleanMode.None,
            var other => throw new UsageException($"--mode 는 propainter, delogo, none 중 하나입니다: '{other}'"),
        };
        if (noPositions && mode != CleanMode.None)
            throw new UsageException("워터마크 위치를 --box x,y,w,h 로 하나 이상 지정하세요 (움직이는 워터마크는 --move, --jump). 업스케일만 하려면 --upscale 만 적으세요.");

        var defaults = new JobOptions();
        // 둘 다 읽어 둔다 (하나만 읽으면 같이 적었을 때 다른 하나가 "알 수 없는 옵션"으로 걸린다). 길이 0은 "전체"로 처리되므로 받지 않는다.
        double? duration = cl.Time("--duration", positive: true), testSeconds = cl.Time("--test-seconds", positive: true);
        var options = new JobOptions
        {
            Mode = mode,
            Upscale = upscale,
            Scale = cl.Int("--scale") ?? defaults.Scale,
            GpuIndex = cl.Int("--gpu") ?? defaults.GpuIndex,
            StartSeconds = cl.Time("--start"),
            DurationSeconds = duration ?? testSeconds,
            Region = new RegionOptions
            {
                Pad = cl.Int("--pad") ?? defaults.Region.Pad,
                MaxArea = cl.Int("--max-area") ?? defaults.Region.MaxArea,
            },
            ChunkLength = cl.Int("--chunk") ?? defaults.ChunkLength,
            Overlap = cl.Int("--overlap") ?? defaults.Overlap,
            SubvideoLength = cl.Int("--subvideo") ?? defaults.SubvideoLength,
            Fp16 = !cl.Flag("--no-fp16"),
            Crf = cl.Int("--crf") ?? defaults.Crf,
            Preset = cl.Value("--preset") ?? defaults.Preset,
            Sharpen = Math.Clamp(cl.Int("--sharpen") ?? defaults.Sharpen, 0, FfmpegCommands.MaxSharpen),
            Deinterlace = deinterlace,
        };

        return new JobRequest
        {
            InputPath = input,
            OutputPath = output ?? Path.ChangeExtension(input, ".out.mp4"),
            Boxes = boxes,
            Tracks = tracks,
            Options = options,
            WorkDirectory = cl.Value("--work"),
            KeepWorkDirectory = cl.Flag("--keep-work"),
            SkipDiskSpaceCheck = cl.Flag("--skip-disk-check"),
        };
    }

    private const string Usage = """
        nekovup — 영상 워터마크 제거 + 업스케일 (명령줄 판)

        명령:
          nekovup probe <입력>                       영상 정보 보기
          nekovup plan  <입력> --box x,y,w,h …       처리 영역·조각·필요 공간 미리 보기
          nekovup run   <입력> -o <출력> --box x,y,w,h …   워터마크 제거 (+ 업스케일)
          nekovup run   <입력> -o <출력> --upscale anime    업스케일만
          nekovup check                              외부 도구 설치 상태 점검
          nekovup setup [--dir <폴더>] [--force]     ProPainter 파이썬 환경 내려받아 준비 (약 3 GB, 설정에 자동 반영)
          nekovup settings [도구 경로 옵션]           설정 보기 / 저장

        run 옵션:
          --box x,y,w,h        워터마크 위치 (여러 번 지정 가능, 화면에 보이는 영상 기준 픽셀)
          --move "시각=x,y,w,h;시각=x,y,w,h;…"   움직이는 워터마크 — 적어 둔 시각 사이를 고르게 이어 간다
          --jump "시각=x,y,w,h;시각=x,y,w,h;…"   움직이는 워터마크 — 다음 시각까지 그 자리에 머물다 한 번에 옮겨 간다
                               시각은 분:초(1:23.5)나 초(83.5), 영상 처음부터 잰다. 여러 번 지정 가능
          --mode propainter|delogo|none   워터마크 제거 방식 (기본 propainter, delogo는 빠른 모드, none은 제거 안 함)
          --upscale none|anime|anime-hq|real|old   업스케일 방식 (기본 none)
                               anime: 애니메이션·빠름, anime-hq: 애니메이션·고품질, real: 실사,
                               old: 옛 비디오(캠코더·VHS) — RealBasicVSR, ProPainter 파이썬 환경 필요, 느림
          --scale 2|3|4        업스케일 배율 (기본 2)
          --gpu N              업스케일에 쓸 GPU 번호 (기본 0, nekovup check 로 확인)
          --sharpen N          업스케일 뒤 선명도 0~100 (기본 0 = 안 함, 30~50 권장)
          --deinterlace auto|on|off   인터레이스(빗살) 해제 (기본 auto = 파일 표기를 따름)
          --test-seconds N     앞 N초만 처리 (= --duration N)
          --start S --duration D     S부터 D 동안만 처리 (초 90 이나 분:초 1:30)
          --pad N              박스 둘레 여백 (기본 96)
          --max-area N         처리 영역 면적 상한 (기본 300000)
          --chunk N --overlap N --subvideo N   조각 길이 100 / 겹침 10 / ProPainter subvideo 40
          --no-fp16            반정밀도 끄기 (VRAM을 더 쓴다)
          --crf N --preset P   최종 화질 (기본 18, veryfast)
          --work <폴더>        중간 파일을 둘 폴더. 그 안에 작업마다 폴더를 따로 만든다 (기본: 출력 폴더\.nekovup)
          --keep-work          끝나도 중간 파일을 남김
          --skip-disk-check    임시 공간 검사 생략
          --verbose            외부 도구 출력까지 모두 표시

        도구 경로 (설정 파일 값을 이번 실행에만 덮어씀, settings 명령에서는 저장):
          --ffmpeg <경로> --ffprobe <경로> --python <경로> --propainter <폴더> --video2x <경로>
          --settings <파일>    설정 파일 위치 (기본 %AppData%\NekoVup\settings.json)
        """;
}

internal sealed class UsageException(string message) : Exception(message);

/// <summary>단계가 바뀌거나 1% 이상 나아갔을 때만 한 줄씩 찍는다 (파일로 돌려도 읽기 좋게).</summary>
internal sealed class ConsoleProgress(Lock gate) : IProgress<JobProgress>
{
    private JobStage? _stage;
    private int _percent = -1;
    private string? _detail;

    public void Report(JobProgress value)
    {
        int percent = (int)(value.OverallFraction * 100);
        lock (gate)
        {
            // 세부 문구(처리한 프레임 수 등)는 매 프레임 바뀌므로, 퍼센트나 단계가 바뀔 때만 찍는다. 다만 조각이 넘어가는 것은 알린다.
            bool chunkChanged = value.Stage == JobStage.Inpaint && value.Detail != _detail;
            if (value.Stage == _stage && percent == _percent && !chunkChanged) return;
            (_stage, _percent, _detail) = (value.Stage, percent, value.Detail);
            string name = value.Stage switch
            {
                JobStage.Probe => "영상 정보 읽기",
                JobStage.Extract => "프레임 추출",
                JobStage.Inpaint => "복원 (ProPainter)",
                JobStage.Merge => "조각 병합",
                JobStage.Composite => "합성·인코딩",
                JobStage.Delogo => "delogo 인코딩",
                JobStage.Prepare => "업스케일 준비",
                JobStage.Upscale => "업스케일 (Video2X)",
                JobStage.Finalize => "오디오·자막 붙이기",
                _ => "마무리",
            };
            Console.WriteLine($"[{percent,3}%] {name}{(value.Detail is null ? "" : " — " + value.Detail)}");
        }
    }
}

/// <summary>아주 단순한 인자 해석기: 첫 위치 인자 = 입력, 나머지는 "--이름 값" 또는 "--플래그".</summary>
internal sealed class CommandLine
{
    private static readonly HashSet<string> Flags = ["--verbose", "--keep-work", "--skip-disk-check", "--no-fp16", "--force"];

    private readonly List<(string Name, string? Value)> _options = [];
    private readonly HashSet<int> _used = [];
    private string? _input;

    public static CommandLine Parse(ReadOnlySpan<string> args)
    {
        var cl = new CommandLine();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith('-') && arg.Length > 1 && !char.IsDigit(arg[1]))
            {
                if (Flags.Contains(arg)) cl._options.Add((arg, null));
                else if (i + 1 < args.Length) cl._options.Add((arg, args[++i]));
                else throw new UsageException($"{arg} 뒤에 값이 필요합니다.");
            }
            else if (cl._input is null) cl._input = arg;
            else throw new UsageException($"알 수 없는 인자: {arg}");
        }
        return cl;
    }

    public string RequireInput() => _input ?? throw new UsageException("입력 파일을 지정하세요.");

    public bool Flag(string name) => Find(name).Any();

    public string? Value(string name) => Find(name).LastOrDefault();

    public IEnumerable<string> Values(string name) => Find(name).Select(v => v!).ToList();

    public int? Int(string name) =>
        Value(name) is not { } text ? null
        : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v
        : throw new UsageException($"{name} 에는 정수를 적으세요: '{text}'");

    /// <summary>0 이상의 숫자. <paramref name="positive"/>면 0도 받지 않는다.</summary>
    public double? Double(string name, bool positive = false) =>
        Value(name) is not { } text ? null
        : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) && (positive ? v > 0 : v >= 0) ? v
        : throw new UsageException($"{name} 에는 {(positive ? "0보다 큰" : "0 이상의")} 숫자를 적으세요: '{text}'");

    /// <summary>시각이나 길이. 초("90")나 분:초("1:30")로 적는다. <paramref name="positive"/>면 0도 받지 않는다.</summary>
    public double? Time(string name, bool positive = false) =>
        Value(name) is not { } text ? null
        : TimeText.TryParse(text, out double v) && (!positive || v > 0) ? v
        : throw new UsageException($"{name} 에는 {(positive ? "0보다 긴 " : "")}시간을 초(90)나 분:초(1:30)로 적으세요: '{text}'");

    /// <summary>해석되지 않은 옵션이 남아 있으면 오타일 가능성이 높으므로 알려 준다.</summary>
    public void EnsureAllUsed()
    {
        for (int i = 0; i < _options.Count; i++)
            if (!_used.Contains(i)) throw new UsageException($"알 수 없는 옵션: {_options[i].Name}");
    }

    private List<string?> Find(string name)
    {
        var found = new List<string?>();
        for (int i = 0; i < _options.Count; i++)
        {
            if (_options[i].Name != name) continue;
            _used.Add(i);
            found.Add(_options[i].Value ?? "");
        }
        return found;
    }
}
