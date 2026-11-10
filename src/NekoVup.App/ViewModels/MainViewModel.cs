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
using NekoVup.Core.Runtime;
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
        Settings = SettingsStore.Load(null, out _settingsLoadError);
        IsDark = Settings.Theme != "Light";
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

    /// <summary>타임라인에 칠할 처리 구간 길이(초). 0이면 전체.</summary>
    public double RangeSeconds => TestSection ? TestLength : 0;

    /// <summary>
    /// 시험 구간으로 실제로 처리할 길이(초). 숫자 칸을 비우면(NaN) 기본값 10초 —
    /// 타임라인·예상치·작업이 모두 이 값을 써야 보이는 것과 처리하는 것이 같다.
    /// </summary>
    private double TestLength => Clamp(TestSeconds, 1, 86_400, 10);

    /// <summary>워터마크 제거 단계의 스위치.</summary>
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
        CleanMode.ProPainter => "고정 로고를 주변 화면으로 복원 · ProPainter",
        CleanMode.Delogo => "고정 로고를 빠르게 뭉개서 지움 · delogo",
        _ => "꺼 두면 화면은 그대로 두고 업스케일만 합니다",
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
        : UpscalePresetIndex switch { 1 => UpscalePreset.AnimeHq, 2 => UpscalePreset.Real, 3 => UpscalePreset.OldVideo, _ => UpscalePreset.Anime };

    public string UpscaleHint => Upscale switch
    {
        UpscalePreset.Anime => "애니메이션 2~4배 · RealESRGAN",
        UpscalePreset.AnimeHq => "애니메이션 2~4배, 고품질 · RealCUGAN",
        UpscalePreset.Real => "실사 2~4배 · RealESRGAN",
        UpscalePreset.OldVideo => "옛 비디오 2~4배 · RealBasicVSR (느림)",
        _ => "해상도를 2~4배로 키웁니다",
    };

    /// <summary>인터레이스 해제: 0 = 자동(파일 표기), 1 = 항상, 2 = 안 함.</summary>
    [ObservableProperty]
    public partial int DeinterlaceIndex { get; set; }

    private Deinterlace DeinterlaceMode => DeinterlaceIndex switch { 1 => Deinterlace.Always, 2 => Deinterlace.Never, _ => Deinterlace.Auto };

    /// <summary>0 = 2배, 1 = 3배, 2 = 4배.</summary>
    [ObservableProperty]
    public partial int ScaleIndex { get; set; }

    [ObservableProperty]
    public partial string OutputSizeText { get; set; } = "";

    /// <summary>업스케일 뒤 선명도 0~100 (0 = 안 함). 모델이 경계를 부드럽게 만든 것을 다시 세운다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SharpenText))]
    public partial double Sharpen { get; set; }

    public string SharpenText => Sharpen <= 0 ? "끔" : ((int)Sharpen).ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeSeconds))]
    public partial bool TestSection { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeSeconds))]
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
    public partial string StatusText { get; set; } = "준비됨";

    // ───────── 화면 테마 ─────────

    /// <summary>다크 테마인지. 바꾸면 설정 파일에 바로 적는다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeIndex))]
    [NotifyPropertyChangedFor(nameof(ThemeButtonText))]
    public partial bool IsDark { get; set; }

    /// <summary>아래 줄의 테마 버튼: 누르면 바뀔 쪽을 적어 둔다.</summary>
    public string ThemeButtonText => IsDark ? "라이트 테마" : "다크 테마";

    /// <summary>설정 창의 테마 고르기: 0 = 라이트, 1 = 다크.</summary>
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

    private int _loadSerial;

    public async Task LoadVideoAsync(string path)
    {
        if (IsRunning) return;
        ClearMessage();
        // 읽는 동안 다른 영상을 또 열거나 작업을 시작할 수 있다 — 마지막으로 연 것만 반영한다.
        int serial = ++_loadSerial;
        try
        {
            // 명령줄로 받은 상대 경로도 전체 경로로 바꿔 둔다 (출력 경로를 입력 폴더 기준으로 풀기 때문).
            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new JobException($"입력 파일 경로가 올바르지 않습니다: {path}", ex);
            }
            var info = await new JobRunner(Settings, _runner, NullJobLog.Instance).ProbeAsync(path, CancellationToken.None);
            if (serial != _loadSerial || IsRunning) return;
            Info = info;
            InputPath = info.Path;
            FileName = Path.GetFileName(info.Path);
            DurationSeconds = Math.Max(info.DurationSeconds, 0.1);
            PreviewResult = false;
            HasResult = false;
            ResultPath = null;
            _resultSeconds = null;
            Boxes.Clear();

            string aspect = info.SampleAspectRatio is { Num: var n, Den: var d } && n != d
                ? string.Create(CultureInfo.InvariantCulture, $" (화면비 {info.DisplayAspect:0.00}:1)")
                : "";
            string subtitles = info.SubtitleCodecs.Count > 0 ? $" · 자막 {info.SubtitleCodecs.Count}개" : "";
            string audio = info.HasAudio ? string.Join(", ", info.AudioCodecs) : "없음";
            string interlaced = info.IsInterlaced ? " · 인터레이스" : "";
            InfoText = string.Create(CultureInfo.InvariantCulture,
                $"{info.Width}×{info.Height}{aspect} · {info.Fps.Value:0.###} fps · {FormatTime(info.DurationSeconds)} · 영상 {info.VideoCodec ?? "?"}{interlaced} · 오디오 {audio}{subtitles}");

            // 자막이 있으면 자막을 담을 수 있는 mkv를 기본으로 한다.
            string extension = info.SubtitleCodecs.Count > 0 || Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase) ? ".mkv" : ".mp4";
            OutputPath = Path.Combine(Path.GetDirectoryName(info.Path)!, Path.GetFileNameWithoutExtension(info.Path) + " [nekoVup]" + extension);

            HasVideo = true;
            StatusText = "준비됨";
            PreviewSeconds = Math.Min(DurationSeconds / 2, 60);
            // 시각이 앞 영상과 같으면 위 줄이 아무 일도 하지 않아 전체 길이 표시가 옛 값으로 남는다.
            OnPreviewSecondsChanged(PreviewSeconds);
            UpdateDerived();
            VideoChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (JobException ex)
        {
            if (serial == _loadSerial) ShowError(ex.Message);
        }
        catch (ToolFailedException ex)
        {
            if (serial == _loadSerial) ShowError("ffprobe를 실행하지 못했습니다. 환경 설정에서 FFmpeg 위치를 확인하세요.\n" + FirstLine(ex.Message));
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
                        "old" => (true, 3),
                        _ => (false, UpscalePresetIndex),
                    };
                    break;
                case "--scale" when int.TryParse(value, out int scale) && scale is >= 2 and <= 4:
                    ScaleIndex = scale - 2;
                    break;
                case "--test-seconds" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && double.IsFinite(seconds) && seconds > 0:
                    (TestSection, TestSeconds) = (true, seconds);
                    break;
                case "--output" when value.Trim().Length > 0:
                    // 경로로 읽을 수 없는 값은 버린다 — 예외가 나면 뒤에 적힌 옵션까지 적용되지 않는다.
                    try { OutputPath = Path.GetFullPath(value); }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
                    break;
            }
        }
    }

    /// <summary>미리보기용 프레임(PNG 바이트). 실패하면 null.</summary>
    public async Task<byte[]?> GetPreviewAsync(double seconds, CancellationToken cancellationToken)
    {
        bool result = PreviewResult && ResultPath is not null;
        string? path = result ? ResultPath : Info?.Path;
        if (path is null) return null;
        // 앞부분만 처리한 결과는 원본보다 짧다. 끝을 넘긴 시각을 달라고 하면 그림이 나오지 않아 원본 화면이 그대로 남는다.
        if (result && _resultSeconds is { } length) seconds = Math.Min(seconds, LastPreviewable(length));
        try
        {
            return await _runner.CaptureBytesAsync(new ToolInvocation(BundledTools.Resolve(Settings).Ffmpeg, FfmpegCommands.PreviewFrame(path, seconds)), cancellationToken);
        }
        catch (ToolFailedException)
        {
            return null;
        }
    }

    partial void OnPreviewSecondsChanged(double value) => PreviewTimeText = FormatTime(value) + " / " + FormatTime(DurationSeconds);

    /// <summary>결과 파일의 길이(초). 앞부분만 처리했을 때만 값이 있다 (전체를 처리했으면 원본과 같다).</summary>
    private double? _resultSeconds;

    /// <summary>길이가 이만큼인 파일에서 그림을 뽑을 수 있는 마지막 시각 (끝에서 조금 앞).</summary>
    private static double LastPreviewable(double length) => Math.Max(0, length - 0.5);

    /// <summary>
    /// 미리보기 시각을 옮긴다. 앞부분만 처리한 결과를 보는 중이면 결과의 끝을 넘기지 않는다 —
    /// 넘기면 표시는 그 시각인데 화면은 결과의 마지막 장면인 채로 남는다.
    /// </summary>
    public void SeekPreview(double seconds)
    {
        if (PreviewResult && ResultPath is not null && _resultSeconds is { } length) seconds = Math.Min(seconds, LastPreviewable(length));
        PreviewSeconds = seconds;
    }

    partial void OnPreviewResultChanged(bool value)
    {
        // 결과가 끝난 뒤의 시각을 보고 있었으면 결과의 끝으로 옮긴다 — 안 그러면 "결과"로 바꿔도 화면이 그대로다.
        if (value && _resultSeconds is { } length && PreviewSeconds > LastPreviewable(length)) PreviewSeconds = LastPreviewable(length);
    }

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
                    long frames = info.EstimateFrames(null, TestSection ? TestLength : null);
                    double gb = JobRunner.EstimateTempBytes(regions, frames) / 1024.0 / 1024.0 / 1024.0;
                    RegionText = string.Create(CultureInfo.InvariantCulture,
                        $"처리 영역 {string.Join(", ", regions.Select(r => $"{r.Rect.Width}×{r.Rect.Height}"))} (점선) · 임시 공간 약 {gb:0.0} GB");
                    var large = regions.Where(r => r.NeedsResize).ToList();
                    if (large.Count > 0)
                    {
                        HasRegionWarning = true;
                        RegionWarning = string.Create(CultureInfo.InvariantCulture,
                            $"처리 영역이 면적 상한({BuildRegionOptions().MaxArea:N0}px)을 넘어 {large.Min(r => r.ResizeRatio):0.##}배로 줄여서 복원합니다. 그 부분이 조금 흐려질 수 있습니다. 박스를 줄이거나 여백을 낮춰 보세요.");
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

    public bool CanStart => HasVideo && !IsRunning && !IsUpdating;

    private JobRequest? BuildRequest()
    {
        if (Info is null) return null;
        var (mode, upscale) = (Mode, Upscale);
        var boxes = Boxes.Select(b => b.ToRect()).Where(r => !r.IsEmpty).ToList();

        if (mode == CleanMode.None && upscale == UpscalePreset.None)
        {
            ShowError("할 일이 없습니다. 위쪽 단계에서 워터마크 제거나 업스케일을 켜세요.");
            return null;
        }
        bool needsPython = mode == CleanMode.ProPainter || upscale == UpscalePreset.OldVideo;
        if (needsPython && SetupRunning)
        {
            ShowError("ProPainter 파이썬 환경을 아직 준비하는 중입니다. 끝날 때까지 기다리거나, 그동안 delogo 방식이나 다른 업스케일 방식을 쓰세요.");
            return null;
        }
        if (needsPython && ProPainterSetup.MissingDrive(Settings) is { } drive)
        {
            // 여기서 "자동 준비를 누르라"고 하면 다른 드라이브에 3 GB를 또 받는다.
            ShowError($"ProPainter 파이썬 환경이 있는 드라이브({drive})를 찾을 수 없습니다. 외장 드라이브라면 연결한 뒤 다시 시작하세요. 다른 곳에 새로 준비하려면 설정 → 도구에서 [ProPainter 자동 준비]를 누르세요.");
            return null;
        }
        if (needsPython && !ProPainterSetup.IsConfigured(Settings))
        {
            ShowError("파이썬 환경(ProPainter)이 준비되지 않았습니다. 옛 비디오 방식과 ProPainter에 필요합니다. 설정 → 도구에서 [ProPainter 자동 준비]를 누르세요.");
            return null;
        }
        if (mode != CleanMode.None && boxes.Count == 0)
        {
            ShowError("워터마크 위치가 없습니다. 미리보기 위에서 끌어서 박스를 그리거나 워터마크 제거를 끄세요.");
            return null;
        }
        if (Unquote(OutputPath).Length == 0)
        {
            ShowError("출력 파일을 지정하세요.");
            return null;
        }
        if (ResolveOutputPath() is not { } output)
        {
            ShowError("출력 파일 경로가 올바르지 않습니다: " + Unquote(OutputPath));
            return null;
        }

        return new JobRequest
        {
            InputPath = Info.Path,
            OutputPath = output,
            Boxes = mode == CleanMode.None ? [] : boxes,
            Options = new JobOptions
            {
                Mode = mode,
                Upscale = upscale,
                Scale = ScaleIndex + 2,
                DurationSeconds = TestSection ? TestLength : null,
                Region = BuildRegionOptions(),
                ChunkLength = (int)Clamp(ChunkLength, 20, 1000, 100),
                Overlap = (int)Clamp(Overlap, 0, 200, 10),
                SubvideoLength = (int)Clamp(SubvideoLength, 10, 500, 40),
                Crf = (int)Clamp(Crf, 0, 40, 18),
                Sharpen = (int)Clamp(Sharpen, 0, FfmpegCommands.MaxSharpen, 0),
                Deinterlace = DeinterlaceMode,
            },
        };
    }

    /// <summary>
    /// 출력 칸에 적힌 것을 전체 경로로 푼다. 탐색기에서 따옴표째 붙여 넣은 경로도, 이름만 적은 것도 받는다.
    /// 비어 있거나 경로로 읽을 수 없으면 null.
    /// </summary>
    public string? ResolveOutputPath()
    {
        string output = Unquote(OutputPath);
        if (output.Length == 0) return null;
        try
        {
            // 이름만 적었으면 원본 옆에 둔다. 그냥 두면 프로그램 폴더 기준이 되는데, 그 폴더는 업데이트 때 통째로 바뀐다.
            if (Info is { } info) return Path.GetFullPath(output, Path.GetDirectoryName(info.Path)!);
            return Path.IsPathFullyQualified(output) ? Path.GetFullPath(output) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// 실패 알림 뒤에 붙일 안내. 작업이 낸 오류(JobException)는 메시지에 할 일이 이미 적혀 있다 (설정을 고치라는 것 등).
    /// 그 밖의 실패는 그대로 다시 하면 또 실패하기도 하므로(폴더 권한, GPU 메모리 부족, 도구를 못 띄움) 원인부터 보라고 하고,
    /// 다시 시작하면 무엇이 남아 있는지(<see cref="ResumeNote"/>)만 알린다.
    /// </summary>
    private static string RetryHint(Exception ex, string resumeNote) => ex switch
    {
        JobException => "",
        // 출력 폴더는 시작할 때 확인했다 — 메시지의 경로(작업 폴더·설정 폴더 등)를 보라고 한다
        UnauthorizedAccessException => "\n위 경로에 쓸 수 없습니다. 쓸 수 있는 곳으로 출력을 옮기거나 그 폴더의 권한을 확인하세요.",
        _ => "\n로그에서 원인을 확인하고 해결하세요." + (resumeNote.Length > 0 ? " " + resumeNote : ""),
    };

    /// <summary>
    /// 멈춘 작업을 같은 설정으로 다시 시작하면 무엇을 건너뛰는지. ProPainter 단계는 조각마다 남지만, 업스케일은 Video2X·RealBasicVSR가
    /// 한 번에 끝까지 돌리는 단계라 중간부터 이어 하지 못한다 (업스케일이 끝났으면 마무리만 다시 한다). delogo만 하는 작업은 남는 것이 없다.
    /// </summary>
    private string ResumeNote(JobRequest request)
    {
        bool proPainter = request.Options.Mode == CleanMode.ProPainter, upscale = request.Options.Upscale != UpscalePreset.None;
        if (upscale && _lastStage == JobStage.Finalize) return "같은 설정으로 다시 시작하면 업스케일까지 끝난 결과를 그대로 써서 마무리만 다시 합니다.";
        if (proPainter && upscale && _lastStage == JobStage.Upscale) return "같은 설정으로 다시 시작하면 워터마크 복원 결과는 그대로 쓰고, 업스케일은 처음부터 다시 합니다.";
        if (proPainter) return "같은 설정으로 다시 시작하면 끝난 부분은 건너뛰고 이어서 합니다.";
        if (upscale) return "업스케일은 중간부터 이어 하지 못해, 다시 시작하면 처음부터 합니다.";
        return "";
    }

    private TaskCompletionSource? _jobDone;

    /// <summary>돌고 있는 작업이 끝나기를(취소 뒤 정리까지) 기다린다. 시간이 지나면 그냥 돌아온다.</summary>
    public Task WaitForJobAsync(TimeSpan timeout) =>
        _jobDone is { } done ? Task.WhenAny(done.Task, Task.Delay(timeout)) : Task.CompletedTask;

    public async Task StartAsync()
    {
        if (IsRunning) return;
        if (IsUpdating)
        {
            ShowInfo("업데이트하는 중입니다. 프로그램이 다시 시작된 뒤에 시작하세요.");
            return;
        }
        ClearMessage();
        var request = BuildRequest();
        if (request is null) return;

        Log.Clear();
        HasRun = true;
        HasResult = false;
        PreviewResult = false;
        ResultPath = null;
        _resultSeconds = null;
        _jobDone = new TaskCompletionSource();
        IsRunning = true;
        StageText = "준비 중";
        StatusText = "처리 중";
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
            _resultSeconds = request.Options.DurationSeconds is { } seconds && seconds < DurationSeconds ? seconds : null;
            HasResult = true;
            StageText = "완료";
            DetailText = string.Create(CultureInfo.InvariantCulture, $"{result.FrameCount}프레임 · {EtaEstimator.Format(result.Elapsed)} 걸림");
            StageProgress = 100;
            TotalProgress = 100;
            TotalText = "100%";
            EtaText = "";
            StatusText = "완료";
            ShowInfo("완료했습니다: " + result.OutputPath);
        }
        catch (OperationCanceledException)
        {
            StageText = "취소됨";
            StatusText = "중지됨";
            EtaText = "";
            string note = ResumeNote(request);
            ShowInfo(note.Length > 0 ? "취소했습니다. " + note : "취소했습니다.");
        }
        catch (Exception ex) when (ex is JobException or ToolFailedException or IOException or UnauthorizedAccessException)
        {
            // 디스크가 꽉 찼거나 폴더에 쓸 수 없는 것(IOException 등)도 작업 실패로 보여 준다 — 안 그러면 화면이 "처리 중"에 멈춘 것처럼 남는다.
            StageText = "실패";
            StatusText = "실패 — 로그를 확인하세요";
            EtaText = "";
            AppendLog("✖ " + ex.Message);
            ShowError(FirstLine(ex.Message) + RetryHint(ex, ResumeNote(request)));
        }
        catch (Exception ex)
        {
            // 예상하지 못한 오류도 화면을 "실패"로 돌려놓고 파일에 남긴다 — 그냥 두면 단계 표시가 "처리 중"에 멈춘 채 남는다.
            App.LogError(ex);
            StageText = "실패";
            StatusText = "실패 — 로그를 확인하세요";
            EtaText = "";
            AppendLog("✖ " + ex);
            ShowError("예기치 못한 오류: " + FirstLine(ex.Message) + RetryHint(ex, ResumeNote(request)));
        }
        finally
        {
            _eta.Stop();
            _jobCancel.Dispose();
            _jobCancel = null;
            IsRunning = false;
            OnPropertyChanged(nameof(CanStart));
            _jobDone.TrySetResult();
            _jobDone = null;
        }
    }

    public void Cancel()
    {
        if (_jobCancel is { IsCancellationRequested: false } cancel)
        {
            StageText = "취소하는 중…";
            StatusText = "중지하는 중…";
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
        JobStage.Upscale => "업스케일",
        JobStage.Finalize => "오디오·자막 붙이기",
        _ => "마무리",
    };

    private void AppendLog(string line)
    {
        while (Log.Count >= MaxLogLines) Log.RemoveAt(0);
        Log.Add(DateTime.Now.ToString("HH:mm:ss ", CultureInfo.InvariantCulture) + line);
    }

    // ═════════════════════ 환경 설정 ═════════════════════

    /// <summary>시작할 때 설정 파일을 읽지 못했으면 그 까닭 (기본값으로 시작했다).</summary>
    private readonly string? _settingsLoadError;

    /// <summary>준비가 이미 끝난 환경을 처음부터 다시 받으려고 [ProPainter 자동 준비]를 한 번 눌러 둔 상태 (설정 창을 열 때마다 풀린다).</summary>
    private bool _resetupArmed;
    private long _resetupArmedAt;

    /// <summary>
    /// 되돌릴 수 없는 일(환경 다시 받기, 프로그램 제거)을 두 번 눌러 확인할 때, 첫 클릭 뒤 이 시간 안의 클릭은 확인으로 치지 않는다 —
    /// 두 번 누르기(더블클릭)의 두 번째 클릭이 그대로 확인이 되면 안 된다.
    /// </summary>
    public const long ConfirmDelayMilliseconds = 1000;

    public void LoadSettingsIntoEditor()
    {
        if (_resetupArmed)
        {
            // 확인 대기를 풀었으니 "한 번 더 누르세요" 안내도 지운다
            _resetupArmed = false;
            SetupStatus = "";
        }
        // 내장 도구를 쓰는 칸은 비워 보여 준다 (자리 표시 글로 내장 도구라고 알린다). 직접 적은 경로만 보인다.
        EditFfmpeg = BundledTools.HasFfmpeg && Settings.Ffmpeg == "ffmpeg" ? "" : Settings.Ffmpeg;
        EditFfprobe = BundledTools.HasFfmpeg && Settings.Ffprobe == "ffprobe" ? "" : Settings.Ffprobe;
        EditPython = Settings.Python;
        EditProPainterDir = Settings.ProPainterDir;
        EditVideo2X = Settings.Video2X;
    }

    /// <summary>도구 탭 머리글. 설치 파일로 깔았으면 FFmpeg·Video2X가 들어 있어 ProPainter만 따로 준비하면 된다.</summary>
    public string ToolsHint => BundledTools.HasFfmpeg && BundledTools.HasVideo2X
        ? "FFmpeg와 Video2X는 프로그램에 들어 있어 비워 두면 됩니다. 다른 판을 쓰려면 경로를 적으세요. ProPainter는 처음 실행 때 자동으로 준비됩니다(아래)."
        : "NekoVup은 아래 도구들을 불러서 씁니다. 도구는 따로 설치해야 하며 여기에 위치를 적어 주세요. FFmpeg는 PATH에 있으면 이름만 적어도 됩니다.";

    public string FfmpegPlaceholder => BundledTools.Ffmpeg is { } path ? "내장: " + path : "ffmpeg";
    public string FfprobePlaceholder => BundledTools.Ffprobe is { } path ? "내장: " + path : "ffprobe";
    public string Video2XPlaceholder => BundledTools.Video2X is { } path ? "내장: " + path : "";

    private AppSettings SettingsFromEditor() => new()
    {
        Ffmpeg = Unquote(EditFfmpeg) is { Length: > 0 } ffmpeg ? ffmpeg : "ffmpeg",
        Ffprobe = Unquote(EditFfprobe) is { Length: > 0 } ffprobe ? ffprobe : "ffprobe",
        Python = Unquote(EditPython),
        ProPainterDir = Unquote(EditProPainterDir),
        Video2X = Unquote(EditVideo2X),
        Theme = Settings.Theme,
    };

    /// <summary>탐색기의 "경로로 복사"는 경로를 큰따옴표로 감싼다. 그대로 붙여 넣어도 되게 따옴표와 빈칸을 뗀다.</summary>
    private static string Unquote(string text) => text.Trim().Trim('"').Trim();

    public void SaveSettings()
    {
        Settings = SettingsFromEditor();
        TrySaveSettings();
    }

    private bool TrySaveSettings()
    {
        try
        {
            SettingsStore.Save(Settings);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("설정을 저장하지 못했습니다: " + ex.Message);
            return false;
        }
    }

    // ═════════════════════ ProPainter 자동 준비 ═════════════════════

    private CancellationTokenSource? _setupCancel;

    /// <summary>ProPainter 파이썬 환경을 내려받는 중인지 (화면 위쪽에 진행 줄을 보인다).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotSettingUp))]
    public partial bool SetupRunning { get; set; }

    public bool NotSettingUp => !SetupRunning;

    [ObservableProperty]
    public partial string SetupText { get; set; } = "";

    [ObservableProperty]
    public partial double SetupFraction { get; set; }

    /// <summary>설정 → 도구 탭의 상태 한 줄.</summary>
    [ObservableProperty]
    public partial string SetupStatus { get; set; } = "";

    /// <summary>
    /// 처음 실행 때: 설정에 쓸 수 있는 ProPainter가 없으면 자동으로 준비한다 (사용자 요청 "설치 파일 하나만 설치하면 바로 쓸 수 있게").
    /// 이미 준비된 폴더가 있으면 설정만 맞춘다. 사용자가 직접 깔아 적어 둔 ProPainter가 있으면 손대지 않는다.
    /// </summary>
    public async Task EnsureProPainterAsync()
    {
        if (_settingsLoadError is { } error)
        {
            // 설정 파일을 읽지 못해 기본값으로 시작했다. 여기서 자동으로 준비·저장하면 사용자가 적어 둔 경로가 덮어써진다.
            ShowError("설정 파일을 읽지 못해 기본값으로 시작했습니다: " + error);
            SetupStatus = "설정 파일을 읽지 못해 자동 준비를 건너뛰었습니다. 필요하면 [ProPainter 자동 준비]를 누르세요.";
            return;
        }
        if (ProPainterSetup.RootOf(Settings) is { } ours)
        {
            // 설정이 우리가 준비한 환경을 가리킨다. 스크립트 파일만 있다고 넘어가면 안 된다 — 다시 준비하다 끊겼거나(파이썬을 지운 뒤)
            // 업데이트로 환경의 판이 올라갔으면 여기서 이어서 준비해야 한다. 안 그러면 작업마다 파이썬 오류로 실패한다.
            if (ProPainterSetup.IsInstalled(ours)) return;
            if (!Directory.Exists(Path.GetPathRoot(ours)))
            {
                // 환경을 둔 드라이브가 지금 없다(뽑아 둔 외장 드라이브) — 다른 드라이브에 6 GB를 또 받지 않고 알리기만 한다.
                SetupStatus = $"ProPainter 환경이 있던 드라이브({Path.GetPathRoot(ours)})를 찾을 수 없습니다. 드라이브를 연결하거나 [ProPainter 자동 준비]를 누르세요.";
                return;
            }
            await SetupProPainterAsync(ProPainterSetup.ResolveRoot(Settings));
            return;
        }
        if (ProPainterSetup.IsConfigured(Settings)) return;
        if (ProPainterSetup.IsUserConfigured(Settings))
        {
            // 사용자가 직접 적은 경로가 지금 안 맞는 것(뽑아 둔 드라이브 등) — 덮어쓰지 않고 알리기만 한다.
            SetupStatus = "설정의 ProPainter 경로를 찾을 수 없습니다. 경로를 고치거나 [ProPainter 자동 준비]를 누르세요.";
            return;
        }
        string root = ProPainterSetup.ResolveRoot(Settings);
        if (ProPainterSetup.IsInstalled(root))
        {
            ApplyRuntime(root);
            return;
        }
        await SetupProPainterAsync(root);
    }

    /// <summary>준비된 환경을 설정에 적는다. 설정 파일에 저장하지 못했으면 false (그 오류는 이미 알림 줄에 떠 있다).</summary>
    private bool ApplyRuntime(string root)
    {
        ProPainterSetup.Apply(Settings, root);
        bool saved = TrySaveSettings();
        // 설정 창이 열려 있어도 다른 칸의 편집 내용은 건드리지 않게 두 칸만 바꾼다.
        EditPython = Settings.Python;
        EditProPainterDir = Settings.ProPainterDir;
        return saved;
    }

    /// <summary>ProPainter 환경을 내려받아 준비하고 설정에 적는다. 이미 돌고 있으면 아무것도 하지 않는다.</summary>
    public async Task SetupProPainterAsync(string? root = null)
    {
        if (SetupRunning) return;
        // 설정 창에서 눌렀을 때는 알림 줄이 창 뒤에 가려진다 — 거절한 까닭을 버튼 옆 상태 줄에도 적는다.
        if (IsRunning || IsChecking)
        {
            string busy = IsRunning ? "작업이" : "점검이";
            SetupStatus = busy + " 끝난 뒤에 다시 누르세요.";
            ShowError(busy + " 끝난 뒤에 ProPainter를 준비하세요 (준비 중에는 쓰던 파이썬을 지웁니다).");
            return;
        }
        if (IsUpdating)
        {
            // 준비는 저절로 이어지지 않는다 (업데이트가 실패할 수도 있다) — 다시 누르라고만 알린다.
            SetupStatus = "업데이트하는 중입니다. 업데이트가 끝난 뒤에 다시 누르세요.";
            return;
        }
        root ??= ProPainterSetup.ResolveRoot(Settings);
        // 이미 준비돼 있으면(다른 창이 준비를 마친 경우 등) 그대로 쓴다 — 다시 준비하면 지금 환경을 지우고 3 GB를 새로 받는다.
        // 정말 다시 받으려면 잠시 뒤 한 번 더 누른다. 바로 이어진 클릭(두 번 누르기의 두 번째)은 확인으로 치지 않는다.
        if (ProPainterSetup.IsInstalled(root) && (!_resetupArmed || Environment.TickCount64 - _resetupArmedAt < ConfirmDelayMilliseconds))
        {
            if (!_resetupArmed)
            {
                ApplyRuntime(root);
                (_resetupArmed, _resetupArmedAt) = (true, Environment.TickCount64);
            }
            SetupStatus = "이미 준비되어 있어 그대로 씁니다: " + root + " — 처음부터 다시 받으려면(지금 환경을 지웁니다) 잠시 뒤 한 번 더 누르세요.";
            return;
        }
        _resetupArmed = false;
        _setupCancel = new CancellationTokenSource();
        SetupRunning = true;
        SetupFraction = 0;
        SetupText = "ProPainter 준비 중 — 약 3 GB를 내려받습니다. 그동안 업스케일은 쓸 수 있습니다.";
        SetupStatus = "준비 중…";
        var progress = new Progress<SetupProgress>(p =>
        {
            SetupFraction = p.Fraction * 100;
            SetupText = string.Create(CultureInfo.InvariantCulture,
                $"ProPainter 준비 중 {p.Fraction * 100:0}% — {p.Step}{(string.IsNullOrEmpty(p.Detail) ? "" : " · " + p.Detail)}");
            SetupStatus = SetupText;
        });
        try
        {
            using var http = new HttpClient();
            var log = new DelegateJobLog((level, message) => { if (level >= LogLevel.Warning) _dispatcher.TryEnqueue(() => AppendLog(message)); });
            await Task.Run(() => new ProPainterSetup(_runner, http, log).InstallAsync(root, progress, _setupCancel.Token));
            SetupStatus = "준비 끝: " + root;
            // 설정을 저장하지 못했으면 그 오류를 덮지 않는다 (다음 실행 때 이 환경을 모르고 다시 준비할 수 있다는 것을 알아야 한다).
            if (ApplyRuntime(root)) ShowInfo("ProPainter 준비가 끝났습니다. 워터마크 복원을 쓸 수 있습니다.");
        }
        catch (OperationCanceledException) when (_setupCancel.IsCancellationRequested)
        {
            SetupStatus = "준비를 중지했습니다. 설정 → 도구에서 다시 시작할 수 있습니다.";
            ShowInfo("ProPainter 준비를 중지했습니다. 설정 → 도구에서 다시 시작할 수 있습니다.");
        }
        catch (Exception ex)
        {
            // 네트워크 시간 초과(OperationCanceledException), 손상된 압축(InvalidDataException), 권한(UnauthorizedAccessException) 등 무엇이든 여기서 알린다.
            // 도구가 실패하면 메시지에 출력 꼬리가 여러 줄 붙는다 — 화면에는 첫 줄만, 전부는 로그 파일에.
            string reason = ex is OperationCanceledException ? "내려받기 응답이 없습니다 (인터넷 연결 확인)" : FirstLine(ex.Message);
            App.LogError(ex);
            SetupStatus = "실패: " + reason;
            ShowError("ProPainter 준비에 실패했습니다: " + reason + " — 설정 → 도구에서 다시 시도할 수 있습니다.");
        }
        finally
        {
            SetupRunning = false;
            _setupCancel.Dispose();
            _setupCancel = null;
        }
    }

    public void CancelSetup() => _setupCancel?.Cancel();

    // ═════════════════════ 버전·업데이트 ═════════════════════

    /// <summary>제목 줄과 정보 탭에 보여 주는 버전 (예: v1.0.0).</summary>
    public string VersionText { get; } = "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "?");

    /// <summary>새 버전이 있어 [업데이트] 버튼을 보여 줄지.</summary>
    [ObservableProperty]
    public partial bool UpdateAvailable { get; set; }

    [ObservableProperty]
    public partial string UpdateButtonText { get; set; } = "업데이트";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotUpdating))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    public partial bool IsUpdating { get; set; }

    public bool NotUpdating => !IsUpdating;

    /// <summary>정보 탭의 업데이트 상태 한 줄.</summary>
    [ObservableProperty]
    public partial string UpdateStatus { get; set; } = "";

    /// <summary>편집 칸에 적힌 값으로 점검한다 (저장 전에도 확인할 수 있게).</summary>
    public async Task CheckEnvironmentAsync()
    {
        if (IsChecking) return;
        if (SetupRunning)
        {
            // 준비하는 동안에는 파이썬 환경을 지우고 새로 푸는 중이라, 점검하면 멀쩡한 것도 실패로 나오고 쓰는 파일을 붙잡아 준비를 방해한다.
            CheckItems.Clear();
            CheckItems.Add(new CheckItemViewModel("⚠", "점검", "ProPainter 준비가 끝난 뒤에 점검하세요."));
            return;
        }
        IsChecking = true;
        CheckItems.Clear();
        try
        {
            string? folder = ResolveOutputPath() is { } output ? Path.GetDirectoryName(output) : null;
            // 출력 폴더가 OneDrive 안이면 작업 폴더는 밖(X:\NekoVup-work)에 생긴다 — 그 드라이브의 공간을 본다
            if (folder is not null && SyncFolders.Contains(folder)) folder = WorkFolders.FallbackRoot();
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
