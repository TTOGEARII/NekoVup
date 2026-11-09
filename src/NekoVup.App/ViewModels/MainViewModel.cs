using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using NekoVup.Core.Media;
using NekoVup.Core.Pipeline;
using NekoVup.Core.Processes;
using NekoVup.Core.Regions;
using NekoVup.Core.Settings;

namespace NekoVup.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 2000;

    private readonly DispatcherQueue _dispatcher;
    private readonly ToolRunner _runner = new();
    private readonly EtaEstimator _eta = new();
    private CancellationTokenSource? _jobCancel;
    private long _lastProgressTicks;
    private JobStage? _lastStage;

    public MainViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        Settings = SettingsStore.Load();
        IsDark = Settings.Theme == "Dark";
        LoadSettingsIntoEditor();
        Boxes.CollectionChanged += OnBoxesCollectionChanged;
    }

    public AppSettings Settings { get; private set; }

    public MediaInfo? Info { get; private set; }

    public ObservableCollection<BoxViewModel> Boxes { get; } = [];
    public ObservableCollection<string> Log { get; } = [];
    public ObservableCollection<CheckItemViewModel> CheckItems { get; } = [];

    /// <summary>미리보기에 점선으로 그릴 처리 영역 (영상 좌표).</summary>
    public IReadOnlyList<PixelRect> RegionRects { get; private set; } = [];

    /// <summary>박스나 처리 영역이 바뀌어 미리보기를 다시 그려야 할 때.</summary>
    public event EventHandler? OverlayChanged;

    /// <summary>다른 영상이 열렸을 때.</summary>
    public event EventHandler? VideoChanged;

    // ───────── 영상 ─────────

    [ObservableProperty]
    public partial string InputPath { get; set; } = "";

    [ObservableProperty]
    public partial string FileName { get; set; } = "";

    [ObservableProperty]
    public partial string InfoText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoVideo))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    public partial bool HasVideo { get; set; }

    public bool NoVideo => !HasVideo;

    [ObservableProperty]
    public partial double DurationSeconds { get; set; } = 1;

    [ObservableProperty]
    public partial double PreviewSeconds { get; set; }

    [ObservableProperty]
    public partial string PreviewTimeText { get; set; } = "";

    /// <summary>미리보기로 결과 파일을 볼지 (작업이 끝난 뒤에만 고를 수 있다).</summary>
    [ObservableProperty]
    public partial bool PreviewResult { get; set; }

    // ───────── 옵션 ─────────

    /// <summary>워터마크 제거 카드의 스위치.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsBoxes))]
    [NotifyPropertyChangedFor(nameof(RemoveHint))]
    public partial bool RemoveOn { get; set; } = true;

    /// <summary>제거 방식: 0 = ProPainter, 1 = delogo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoveHint))]
    public partial int RemoveMethodIndex { get; set; }

    public bool NeedsBoxes => RemoveOn;

    private CleanMode Mode => !RemoveOn ? CleanMode.None : RemoveMethodIndex == 1 ? CleanMode.Delogo : CleanMode.ProPainter;

    public string RemoveHint => Mode switch
    {
        CleanMode.ProPainter => "고정된 로고를 주변 화면으로 자연스럽게 복원 · ProPainter",
        CleanMode.Delogo => "고정된 로고를 빠르게 뭉개서 지움 · delogo",
        _ => "꺼 두면 화면은 그대로 두고 업스케일만 해요",
    };

    /// <summary>업스케일 카드의 스위치.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpscaleEnabled))]
    [NotifyPropertyChangedFor(nameof(UpscaleHint))]
    public partial bool UpscaleOn { get; set; }

    /// <summary>업스케일 방식: 0 = 애니(빠름), 1 = 애니(고품질), 2 = 실사.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpscaleHint))]
    public partial int UpscalePresetIndex { get; set; }

    public bool UpscaleEnabled => UpscaleOn;

    private UpscalePreset Upscale => !UpscaleOn
        ? UpscalePreset.None
        : UpscalePresetIndex switch { 1 => UpscalePreset.AnimeHq, 2 => UpscalePreset.Real, _ => UpscalePreset.Anime };

    public string UpscaleHint => Upscale switch
    {
        UpscalePreset.Anime => "애니메이션을 2~4배로 선명하게 · RealESRGAN",
        UpscalePreset.AnimeHq => "애니메이션을 2~4배로, 느리지만 더 곱게 · RealCUGAN",
        UpscalePreset.Real => "실사 영상을 2~4배로 선명하게 · RealESRGAN",
        _ => "화면을 2~4배로 키워 선명하게",
    };

    /// <summary>0 = 2배, 1 = 3배, 2 = 4배.</summary>
    [ObservableProperty]
    public partial int ScaleIndex { get; set; }

    [ObservableProperty]
    public partial string OutputSizeText { get; set; } = "";

    [ObservableProperty]
    public partial bool TestSection { get; set; }

    [ObservableProperty]
    public partial double TestSeconds { get; set; } = 10;

    [ObservableProperty]
    public partial double Pad { get; set; } = 96;

    [ObservableProperty]
    public partial double MaxArea { get; set; } = 300_000;

    [ObservableProperty]
    public partial double ChunkLength { get; set; } = 100;

    [ObservableProperty]
    public partial double Overlap { get; set; } = 10;

    [ObservableProperty]
    public partial double SubvideoLength { get; set; } = 40;

    [ObservableProperty]
    public partial double Crf { get; set; } = 18;

    [ObservableProperty]
    public partial string OutputPath { get; set; } = "";

    [ObservableProperty]
    public partial string RegionText { get; set; } = "";

    [ObservableProperty]
    public partial string RegionWarning { get; set; } = "";

    [ObservableProperty]
    public partial bool HasRegionWarning { get; set; }

    // ───────── 알림 ─────────

    [ObservableProperty]
    public partial string Message { get; set; } = "";

    [ObservableProperty]
    public partial bool HasMessage { get; set; }

    [ObservableProperty]
    public partial bool MessageIsError { get; set; }

    // ───────── 실행 상태 ─────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotRunning))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    public partial bool IsRunning { get; set; }

    public bool NotRunning => !IsRunning;

    /// <summary>한 번이라도 실행했는지 (진행 패널을 보여 줄지).</summary>
    [ObservableProperty]
    public partial bool HasRun { get; set; }

    [ObservableProperty]
    public partial string StageText { get; set; } = "";

    [ObservableProperty]
    public partial string DetailText { get; set; } = "";

    [ObservableProperty]
    public partial double StageProgress { get; set; }

    [ObservableProperty]
    public partial double TotalProgress { get; set; }

    [ObservableProperty]
    public partial string TotalText { get; set; } = "";

    [ObservableProperty]
    public partial string EtaText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasResult { get; set; }

    public string? ResultPath { get; private set; }

    /// <summary>아래 줄 오른쪽에 보여 주는 한 줄 상태.</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = "준비 완료 ♪";

    // ───────── 화면 테마 ─────────

    /// <summary>다크 테마인지. 바꾸면 설정 파일에 바로 적는다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeIndex))]
    [NotifyPropertyChangedFor(nameof(ThemeButtonText))]
    public partial bool IsDark { get; set; }

    /// <summary>아래 줄의 테마 버튼: 누르면 바뀔 쪽을 적어 둔다.</summary>
    public string ThemeButtonText => IsDark ? "☀ 화이트" : "🌙 다크";

    /// <summary>설정 창의 테마 고르기: 0 = 화이트, 1 = 다크.</summary>
    public int ThemeIndex
    {
        get => IsDark ? 1 : 0;
        set
        {
            if (value is 0 or 1) IsDark = value == 1;
        }
    }

    partial void OnIsDarkChanged(bool value)
    {
        string theme = value ? "Dark" : "Light";
        if (Settings.Theme == theme) return;
        Settings.Theme = theme;
        TrySaveSettings();
    }

    // ───────── 환경 설정 편집 칸 ─────────

    [ObservableProperty]
    public partial string EditFfmpeg { get; set; } = "";

    [ObservableProperty]
    public partial string EditFfprobe { get; set; } = "";

    [ObservableProperty]
    public partial string EditPython { get; set; } = "";

    [ObservableProperty]
    public partial string EditProPainterDir { get; set; } = "";

    [ObservableProperty]
    public partial string EditVideo2X { get; set; } = "";

    [ObservableProperty]
    public partial bool IsChecking { get; set; }

    // ═════════════════════ 영상 열기 ═════════════════════

    public async Task LoadVideoAsync(string path)
    {
        if (IsRunning) return;
        ClearMessage();
        try
        {
            var info = await new JobRunner(Settings, _runner, NullJobLog.Instance).ProbeAsync(path, CancellationToken.None);
            Info = info;
            InputPath = info.Path;
            FileName = Path.GetFileName(info.Path);
            DurationSeconds = Math.Max(info.DurationSeconds, 0.1);
            PreviewResult = false;
            HasResult = false;
            ResultPath = null;
            Boxes.Clear();

            string aspect = info.SampleAspectRatio is { Num: var n, Den: var d } && n != d
                ? string.Create(CultureInfo.InvariantCulture, $" (화면비 {info.DisplayAspect:0.00}:1)")
                : "";
            string subtitles = info.SubtitleCodecs.Count > 0 ? $" · 자막 {info.SubtitleCodecs.Count}개" : "";
            string audio = info.HasAudio ? string.Join(", ", info.AudioCodecs) : "없음";
            InfoText = string.Create(CultureInfo.InvariantCulture,
                $"{info.Width}×{info.Height}{aspect} · {info.Fps.Value:0.###} fps · {FormatTime(info.DurationSeconds)} · 영상 {info.VideoCodec ?? "?"} · 오디오 {audio}{subtitles}");

            // 자막이 있으면 자막을 담을 수 있는 mkv를 기본으로 한다.
            string extension = info.SubtitleCodecs.Count > 0 || Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase) ? ".mkv" : ".mp4";
            OutputPath = Path.Combine(Path.GetDirectoryName(info.Path)!, Path.GetFileNameWithoutExtension(info.Path) + " [nekoVup]" + extension);

            HasVideo = true;
            StatusText = "준비 완료 ♪";
            PreviewSeconds = Math.Min(DurationSeconds / 2, 60);
            UpdateDerived();
            VideoChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (JobException ex)
        {
            ShowError(ex.Message);
        }
        catch (ToolFailedException ex)
        {
            ShowError("ffprobe를 실행하지 못했습니다. 환경 설정에서 FFmpeg 위치를 확인하세요.\n" + FirstLine(ex.Message));
        }
    }

    /// <summary>
    /// 명령줄로 받은 시작 옵션을 적용한다. 예: NekoVup.exe 영상.mp4 --box 1040,40,190,56 --mode delogo --upscale anime --scale 2 --test-seconds 10 --output 결과.mkv
    /// </summary>
    public async Task ApplyStartupAsync(IReadOnlyList<string> arguments)
    {
        string? file = null;
        var options = new List<(string Name, string Value)>();
        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < arguments.Count) options.Add((arguments[i], arguments[++i]));
            else file ??= arguments[i];
        }
        if (file is null || !File.Exists(file)) return;

        await LoadVideoAsync(file);
        if (!HasVideo) return;
        foreach (var (name, value) in options)
        {
            switch (name)
            {
                case "--box" when PixelRect.TryParse(value, out var box) && !box.IsEmpty:
                    AddBox(box);
                    break;
                case "--mode":
                    (RemoveOn, RemoveMethodIndex) = value.ToLowerInvariant() switch { "delogo" => (true, 1), "none" => (false, RemoveMethodIndex), _ => (true, 0) };
                    break;
                case "--upscale":
                    (UpscaleOn, UpscalePresetIndex) = value.ToLowerInvariant() switch
                    {
                        "anime" => (true, 0),
                        "anime-hq" => (true, 1),
                        "real" => (true, 2),
                        _ => (false, UpscalePresetIndex),
                    };
                    break;
                case "--scale" when int.TryParse(value, out int scale) && scale is >= 2 and <= 4:
                    ScaleIndex = scale - 2;
                    break;
                case "--test-seconds" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0:
                    (TestSection, TestSeconds) = (true, seconds);
                    break;
                case "--output":
                    OutputPath = Path.GetFullPath(value);
                    break;
            }
        }
    }

    /// <summary>미리보기용 프레임(PNG 바이트). 실패하면 null.</summary>
    public async Task<byte[]?> GetPreviewAsync(double seconds, CancellationToken cancellationToken)
    {
        string? path = PreviewResult && ResultPath is not null ? ResultPath : Info?.Path;
        if (path is null) return null;
        try
        {
            return await _runner.CaptureBytesAsync(new ToolInvocation(Settings.Ffmpeg, FfmpegCommands.PreviewFrame(path, seconds)), cancellationToken);
        }
        catch (ToolFailedException)
        {
            return null;
        }
    }

    partial void OnPreviewSecondsChanged(double value) => PreviewTimeText = FormatTime(value) + " / " + FormatTime(DurationSeconds);

    // ═════════════════════ 박스 ═════════════════════

    public BoxViewModel AddBox(PixelRect rect)
    {
        var box = new BoxViewModel(rect);
        Boxes.Add(box);
        return box;
    }

    public void AddDefaultBox()
    {
        if (Info is null) return;
        int w = Math.Max(16, Info.Width / 6), h = Math.Max(16, Info.Height / 10);
        AddBox(new PixelRect(Info.Width - w - Info.Width / 20, Info.Height / 20, w, h));
    }

    private void OnBoxesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (BoxViewModel box in e.OldItems) box.PropertyChanged -= OnBoxPropertyChanged;
        if (e.NewItems is not null)
            foreach (BoxViewModel box in e.NewItems) box.PropertyChanged += OnBoxPropertyChanged;
        UpdateDerived();
    }

    private void OnBoxPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateDerived();

    partial void OnRemoveOnChanged(bool value) => UpdateDerived();
    partial void OnRemoveMethodIndexChanged(int value) => UpdateDerived();
    partial void OnUpscaleOnChanged(bool value) => UpdateDerived();
    partial void OnScaleIndexChanged(int value) => UpdateDerived();
    partial void OnPadChanged(double value) => UpdateDerived();
    partial void OnMaxAreaChanged(double value) => UpdateDerived();
    partial void OnTestSectionChanged(bool value) => UpdateDerived();
    partial void OnTestSecondsChanged(double value) => UpdateDerived();

    /// <summary>박스·옵션에서 따라 나오는 표시(처리 영역, 경고, 결과 크기)를 다시 계산한다.</summary>
    private void UpdateDerived()
    {
        var info = Info;
        RegionRects = [];
        RegionText = "";
        RegionWarning = "";
        HasRegionWarning = false;

        if (info is not null)
        {
            int scale = ScaleIndex + 2;
            OutputSizeText = UpscaleEnabled
                ? string.Create(CultureInfo.InvariantCulture, $"{info.Width}×{info.Height} → {info.Width * scale}×{info.Height * scale}")
                : "";

            var boxes = Boxes.Select(b => b.ToRect()).Where(r => !r.IsEmpty).ToList();
            if (Mode == CleanMode.ProPainter && boxes.Count > 0)
            {
                try
                {
                    var regions = RegionPlanner.Plan(boxes, info.Width, info.Height, BuildRegionOptions());
                    RegionRects = [.. regions.Select(r => r.Rect)];
                    long frames = info.EstimateFrames(null, TestSection ? TestSeconds : null);
                    double gb = JobRunner.EstimateTempBytes(regions, frames) / 1024.0 / 1024.0 / 1024.0;
                    RegionText = string.Create(CultureInfo.InvariantCulture,
                        $"처리 영역 {string.Join(", ", regions.Select(r => $"{r.Rect.Width}×{r.Rect.Height}"))} (점선) · 임시 공간 약 {gb:0.0} GB");
                    var large = regions.Where(r => r.NeedsResize).ToList();
                    if (large.Count > 0)
                    {
                        HasRegionWarning = true;
                        RegionWarning = string.Create(CultureInfo.InvariantCulture,
                            $"처리 영역이 면적 상한({MaxArea:N0}px)을 넘어 {large.Min(r => r.ResizeRatio):0.##}배로 줄여서 복원합니다. 그 부분이 조금 흐려질 수 있습니다. 박스를 줄이거나 여백을 낮춰 보세요.");
                    }
                }
                catch (ArgumentException ex)
                {
                    RegionText = ex.Message;
                }
            }
        }
        OverlayChanged?.Invoke(this, EventArgs.Empty);
    }

    private RegionOptions BuildRegionOptions() => new()
    {
        Pad = (int)Clamp(Pad, 0, 2000, 96),
        MaxArea = (long)Clamp(MaxArea, 10_000, 100_000_000, 300_000),
    };

    // ═════════════════════ 실행 ═════════════════════

    public bool CanStart => HasVideo && !IsRunning;

    private JobRequest? BuildRequest()
    {
        if (Info is null) return null;
        var (mode, upscale) = (Mode, Upscale);
        var boxes = Boxes.Select(b => b.ToRect()).Where(r => !r.IsEmpty).ToList();

        if (mode == CleanMode.None && upscale == UpscalePreset.None)
        {
            ShowError("할 일이 없어요. 위쪽 카드에서 워터마크 제거나 업스케일을 켜 주세요.");
            return null;
        }
        if (mode != CleanMode.None && boxes.Count == 0)
        {
            ShowError("워터마크 위치가 없어요. 미리보기 위에서 끌어서 박스를 그리거나, 워터마크 제거를 꺼 주세요.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            ShowError("출력 파일을 지정하세요.");
            return null;
        }

        return new JobRequest
        {
            InputPath = Info.Path,
            OutputPath = OutputPath.Trim(),
            Boxes = mode == CleanMode.None ? [] : boxes,
            Options = new JobOptions
            {
                Mode = mode,
                Upscale = upscale,
                Scale = ScaleIndex + 2,
                DurationSeconds = TestSection ? Clamp(TestSeconds, 1, 86_400, 10) : null,
                Region = BuildRegionOptions(),
                ChunkLength = (int)Clamp(ChunkLength, 20, 1000, 100),
                Overlap = (int)Clamp(Overlap, 0, 200, 10),
                SubvideoLength = (int)Clamp(SubvideoLength, 10, 500, 40),
                Crf = (int)Clamp(Crf, 0, 40, 18),
            },
        };
    }

    public async Task StartAsync()
    {
        if (IsRunning) return;
        ClearMessage();
        var request = BuildRequest();
        if (request is null) return;

        Log.Clear();
        HasRun = true;
        HasResult = false;
        PreviewResult = false;
        ResultPath = null;
        IsRunning = true;
        StageText = "준비 중";
        StatusText = "일하는 중…";
        DetailText = "";
        StageProgress = 0;
        TotalProgress = 0;
        TotalText = "0%";
        EtaText = "";
        _lastStage = null;
        _eta.Restart();
        OnPropertyChanged(nameof(CanStart));

        _jobCancel = new CancellationTokenSource();
        var token = _jobCancel.Token;
        var log = new DelegateJobLog((level, message) =>
        {
            if (level == LogLevel.Detail) return; // 도구 출력은 작업 폴더의 job.log에 남는다
            string prefix = level switch { LogLevel.Warning => "⚠ ", LogLevel.Error => "✖ ", _ => "" };
            _dispatcher.TryEnqueue(() => AppendLog(prefix + message));
        });
        var progress = new CallbackProgress(OnProgress);
        var jobRunner = new JobRunner(Settings, _runner, log);

        try
        {
            var result = await Task.Run(() => jobRunner.RunAsync(request, progress, token), token);
            ResultPath = result.OutputPath;
            HasResult = true;
            StageText = "완료";
            DetailText = string.Create(CultureInfo.InvariantCulture, $"{result.FrameCount}프레임 · {EtaEstimator.Format(result.Elapsed)} 걸림");
            StageProgress = 100;
            TotalProgress = 100;
            TotalText = "100%";
            EtaText = "";
            StatusText = "다 됐어요 ✿";
            ShowInfo("완료했습니다: " + result.OutputPath);
        }
        catch (OperationCanceledException)
        {
            StageText = "취소됨";
            StatusText = "멈췄어요";
            EtaText = "";
            ShowInfo("취소했습니다. 같은 설정으로 다시 시작하면 끝난 부분은 건너뛰고 이어서 합니다.");
        }
        catch (Exception ex) when (ex is JobException or ToolFailedException)
        {
            StageText = "실패";
            StatusText = "실패했어요 (로그를 확인해 주세요)";
            EtaText = "";
            AppendLog("✖ " + ex.Message);
            ShowError(FirstLine(ex.Message) + "\n같은 설정으로 다시 시작하면 끝난 부분은 건너뛰고 이어서 합니다.");
        }
        finally
        {
            _eta.Stop();
            _jobCancel.Dispose();
            _jobCancel = null;
            IsRunning = false;
            OnPropertyChanged(nameof(CanStart));
        }
    }

    public void Cancel()
    {
        if (_jobCancel is { IsCancellationRequested: false } cancel)
        {
            StageText = "취소하는 중…";
            StatusText = "멈추는 중…";
            cancel.Cancel();
        }
    }

    /// <summary>작업 스레드에서 불린다. 화면 갱신은 너무 잦지 않게(초당 10번 정도) 줄여서 넘긴다.</summary>
    private void OnProgress(JobProgress value)
    {
        long now = Stopwatch.GetTimestamp();
        bool stageChanged = value.Stage != _lastStage;
        if (!stageChanged && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastProgressTicks), now) < TimeSpan.FromMilliseconds(100)) return;
        Interlocked.Exchange(ref _lastProgressTicks, now);
        _lastStage = value.Stage;

        _dispatcher.TryEnqueue(() =>
        {
            if (!IsRunning) return;
            StageText = StageName(value.Stage);
            DetailText = value.Detail ?? "";
            StageProgress = value.StageFraction * 100;
            TotalProgress = value.OverallFraction * 100;
            TotalText = string.Create(CultureInfo.InvariantCulture, $"{value.OverallFraction * 100:0}%");
            EtaText = _eta.Update(value.OverallFraction);
            StatusText = StageText + " · " + TotalText;
        });
    }

    public static string StageName(JobStage stage) => stage switch
    {
        JobStage.Probe => "영상 정보 읽기",
        JobStage.Extract => "프레임 추출",
        JobStage.Inpaint => "워터마크 복원 (ProPainter)",
        JobStage.Merge => "조각 병합",
        JobStage.Composite => "합성·인코딩",
        JobStage.Delogo => "워터마크 제거 (delogo)",
        JobStage.Prepare => "업스케일 준비",
        JobStage.Upscale => "업스케일 (Video2X)",
        JobStage.Finalize => "오디오·자막 붙이기",
        _ => "마무리",
    };

    private void AppendLog(string line)
    {
        while (Log.Count >= MaxLogLines) Log.RemoveAt(0);
        Log.Add(DateTime.Now.ToString("HH:mm:ss ", CultureInfo.InvariantCulture) + line);
    }

    // ═════════════════════ 환경 설정 ═════════════════════

    public void LoadSettingsIntoEditor()
    {
        EditFfmpeg = Settings.Ffmpeg;
        EditFfprobe = Settings.Ffprobe;
        EditPython = Settings.Python;
        EditProPainterDir = Settings.ProPainterDir;
        EditVideo2X = Settings.Video2X;
    }

    private AppSettings SettingsFromEditor() => new()
    {
        Ffmpeg = string.IsNullOrWhiteSpace(EditFfmpeg) ? "ffmpeg" : EditFfmpeg.Trim(),
        Ffprobe = string.IsNullOrWhiteSpace(EditFfprobe) ? "ffprobe" : EditFfprobe.Trim(),
        Python = EditPython.Trim(),
        ProPainterDir = EditProPainterDir.Trim(),
        Video2X = EditVideo2X.Trim(),
        Theme = Settings.Theme,
    };

    public void SaveSettings()
    {
        Settings = SettingsFromEditor();
        TrySaveSettings();
    }

    private void TrySaveSettings()
    {
        try
        {
            SettingsStore.Save(Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("설정을 저장하지 못했습니다: " + ex.Message);
        }
    }

    // ═════════════════════ 버전·업데이트 ═════════════════════

    /// <summary>제목 줄과 정보 탭에 보여 주는 버전 (예: v1.0.0).</summary>
    public string VersionText { get; } = "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "?");

    /// <summary>새 버전이 있어 [업데이트] 버튼을 보여 줄지.</summary>
    [ObservableProperty]
    public partial bool UpdateAvailable { get; set; }

    [ObservableProperty]
    public partial string UpdateButtonText { get; set; } = "🎀 새 버전!";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotUpdating))]
    public partial bool IsUpdating { get; set; }

    public bool NotUpdating => !IsUpdating;

    /// <summary>정보 탭의 업데이트 상태 한 줄.</summary>
    [ObservableProperty]
    public partial string UpdateStatus { get; set; } = "";

    /// <summary>편집 칸에 적힌 값으로 점검한다 (저장 전에도 확인할 수 있게).</summary>
    public async Task CheckEnvironmentAsync()
    {
        if (IsChecking) return;
        IsChecking = true;
        CheckItems.Clear();
        try
        {
            string? folder = string.IsNullOrWhiteSpace(OutputPath) ? null : Path.GetDirectoryName(OutputPath);
            var items = await Task.Run(() => new EnvironmentChecker(SettingsFromEditor(), _runner).RunAsync(folder, CancellationToken.None));
            foreach (var item in items)
            {
                string glyph = item.Status switch { CheckStatus.Ok => "✔", CheckStatus.Warning => "⚠", _ => "✖" };
                CheckItems.Add(new CheckItemViewModel(glyph, item.Name, item.Detail));
            }
        }
        finally
        {
            IsChecking = false;
        }
    }

    // ═════════════════════ 도우미 ═════════════════════

    public void ShowError(string message) => (Message, MessageIsError, HasMessage) = (message, true, true);

    public void ShowInfo(string message) => (Message, MessageIsError, HasMessage) = (message, false, true);

    public void ClearMessage() => HasMessage = false;

    public static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}:{span.Seconds:00}.{span.Milliseconds / 100}");
    }

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? text;

    private sealed class CallbackProgress(Action<JobProgress> handler) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => handler(value);
    }
}
