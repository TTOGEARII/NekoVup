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

/// <summary>미리보기로 보는 것.</summary>
public enum PreviewMode
{
    /// <summary>원본 (박스를 그리고 고친다)</summary>
    Original,

    /// <summary>원본·결과 나란히: 세로선 왼쪽은 원본, 오른쪽은 결과</summary>
    Compare,

    /// <summary>결과만</summary>
    Result,
}

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
        Queue.CollectionChanged += (_, _) => RefreshQueueTexts();
    }

    public AppSettings Settings { get; private set; }

    public MediaInfo? Info { get; private set; }

    public ObservableCollection<BoxViewModel> Boxes { get; } = [];

    /// <summary>올려 둔 영상들. 시작을 누르면 위에서부터 차례로 처리한다 (사용자 요청).</summary>
    public ObservableCollection<QueueItemViewModel> Queue { get; } = [];

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

    /// <summary>프레임 하나의 길이(초). 타임라인에서 한 프레임씩 옮길 때 쓴다.</summary>
    [ObservableProperty]
    public partial double FrameSeconds { get; set; }

    [ObservableProperty]
    public partial double PreviewSeconds { get; set; }

    [ObservableProperty]
    public partial string PreviewTimeText { get; set; } = "";

    /// <summary>미리보기로 무엇을 볼지: 원본, 원본·결과 나란히(비교), 결과. 비교·결과는 결과가 있을 때만 고를 수 있다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsResult))]
    public partial PreviewMode PreviewView { get; set; }

    /// <summary>미리보기가 결과 파일을 쓰는지 (비교·결과 보기이고 결과가 있을 때).</summary>
    public bool ShowsResult => PreviewView != PreviewMode.Original && ResultPath is not null;

    /// <summary>
    /// 현재 장면 빠른 미리보기로 처리하는 길이(초) — 재생 헤드 앞뒤로 반씩. 프레임마다 따로 처리하는 방식(delogo, Video2X)에만 쓴다.
    /// 앞뒤 프레임을 함께 보는 방식(ProPainter, 옛 비디오)은 실제 작업의 조각을 통째로 처리한다 (<see cref="ScenePlanner"/>).
    /// </summary>
    private const double SceneSeconds = 1.0;

    /// <summary>빠른 미리보기 폴더들의 위 폴더. 창(프로세스)마다 그 아래에 자기 폴더를 둔다.</summary>
    private static string PreviewRoot => Path.Combine(Path.GetTempPath(), "NekoVup-preview");

    /// <summary>
    /// 이 창의 빠른 미리보기 결과와 작업 폴더. 출력 폴더를 건드리지 않게 임시 폴더에 두고,
    /// 창을 둘 띄웠을 때 서로의 파일을 지우지 않게 프로세스마다 따로 쓴다.
    /// </summary>
    public static string PreviewFolder => Path.Combine(PreviewRoot, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

    /// <summary>지금 결과가 빠른 미리보기로 만든 임시 파일인지 (출력 파일이 아니다).</summary>
    public bool ResultIsPreview { get; private set; }

    /// <summary>
    /// 이 창의 빠른 미리보기 파일을 지운다. 아직 그림을 뽑느라 붙잡고 있어 못 지운 것은 다음에 지운다.
    /// 비정상 종료한 창이 남긴 폴더도 함께 치운다. <paramref name="everyone"/>이면 열려 있는 다른 창의 것까지 (프로그램 제거).
    /// </summary>
    public static void DeletePreviewFiles(bool everyone = false)
    {
        try
        {
            if (!Directory.Exists(PreviewRoot)) return;
            foreach (string folder in Directory.GetDirectories(PreviewRoot))
            {
                string name = Path.GetFileName(folder);
                bool mine = int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int id) && id == Environment.ProcessId;
                if (everyone || mine || !IsOpenWindow(name)) TryDeleteFolder(folder);
            }
            // 비었으면 위 폴더도 지운다 (다른 창의 폴더가 남아 있으면 그대로 둔다).
            if (!Directory.EnumerateFileSystemEntries(PreviewRoot).Any()) Directory.Delete(PreviewRoot);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>폴더 이름(프로세스 번호)의 주인이 지금 떠 있는 NekoVup 창인지. 번호는 다른 프로그램이 다시 쓸 수 있으므로 이름도 본다.</summary>
    private static bool IsOpenWindow(string folderName)
    {
        if (!int.TryParse(folderName, NumberStyles.None, CultureInfo.InvariantCulture, out int id)) return false;
        try
        {
            using var owner = Process.GetProcessById(id);
            using var self = Process.GetCurrentProcess();
            return owner.ProcessName.Equals(self.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false; // 그 번호의 프로세스가 없다 (또는 끝나는 중)
        }
    }

    /// <summary>실제 작업(시작 단추)이 처리할 프레임 구간. 조각은 이 구간의 처음부터 센다.</summary>
    private FrameSpan JobFrames(MediaInfo info)
    {
        double fps = info.Fps.Value;
        long total = info.EstimateFrames();
        if (!RangeOn) return new FrameSpan(0, total);
        var (start, end) = Range;
        long first = start > EdgeTolerance ? Math.Min(FrameGrid.FrameAt(start, fps), total - 1) : 0;
        long last = end < DurationSeconds - EdgeTolerance ? FrameGrid.FrameAt(end, fps) : total;
        return new FrameSpan(first, Math.Max(1, last - first));
    }

    /// <summary>재생 헤드가 가리키는 프레임 (원본 기준).</summary>
    private long PlayheadFrame(MediaInfo info) => FrameAt(info, PreviewSeconds);

    /// <summary>그 시각의 프레임 (영상의 마지막 프레임을 넘지 않게).</summary>
    private static long FrameAt(MediaInfo info, double seconds) =>
        Math.Min(FrameGrid.FrameAt(seconds, info.Fps.Value), Math.Max(0, info.EstimateFrames() - 1));

    // ───────── 옵션 ─────────

    /// <summary>타임라인에서 고른 구간만 처리할지 (끄면 영상 전체). 사용자 요청: 특정 부분만 잘라서 렌더링.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimelineRangeStart))]
    [NotifyPropertyChangedFor(nameof(TimelineRangeEnd))]
    [NotifyPropertyChangedFor(nameof(RangeEditable))]
    [NotifyPropertyChangedFor(nameof(RangeText))]
    public partial bool RangeOn { get; set; }

    /// <summary>구간의 시작(초, 원본 기준). 시각 칸·타임라인 손잡이·I 키가 바꾼다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimelineRangeStart))]
    [NotifyPropertyChangedFor(nameof(TimelineRangeEnd))]
    [NotifyPropertyChangedFor(nameof(RangeText))]
    [NotifyPropertyChangedFor(nameof(RangeStartText))]
    public partial double RangeStart { get; set; }

    /// <summary>구간의 끝(초, 원본 기준). 시각 칸·타임라인 손잡이·O 키가 바꾼다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimelineRangeStart))]
    [NotifyPropertyChangedFor(nameof(TimelineRangeEnd))]
    [NotifyPropertyChangedFor(nameof(RangeText))]
    [NotifyPropertyChangedFor(nameof(RangeEndText))]
    public partial double RangeEnd { get; set; }

    /// <summary>구간 시작 칸에 보이는 글자 — 분:초 (사용자 요청: 초로만 적혀 있으면 읽기 어렵다).</summary>
    public string RangeStartText => TimeText.Format(Clamp(RangeStart, 0, DurationSeconds, 0));

    public string RangeEndText => TimeText.Format(Clamp(RangeEnd, 0, DurationSeconds, DurationSeconds));

    /// <summary>시작 칸에 적은 시각("1:23.5", "1분 23초", 숫자만 적으면 초)을 구간에 넣는다. 읽을 수 없으면 그대로 둔다.</summary>
    public void EnterRangeStart(string text) => RangeStart = EnteredTime(text, RangeStart);

    public void EnterRangeEnd(string text) => RangeEnd = EnteredTime(text, RangeEnd);

    /// <summary>
    /// 칸에 적은 시각. 보이던 글자 그대로면 값을 건드리지 않는다 — 칸에는 0.01초까지만 보여, 다시 읽어 넣으면
    /// 영상 끝에 붙여 둔 구간이 "끝까지"가 아니게 깎인다.
    /// </summary>
    private double EnteredTime(string text, double current)
    {
        if (!TimeText.TryParse(text, out double seconds)) return current;
        seconds = Math.Min(seconds, DurationSeconds);
        if (double.IsFinite(current) && TimeText.Format(seconds) == TimeText.Format(current)) return current;
        return seconds >= DurationSeconds - EdgeTolerance ? DurationSeconds : seconds;
    }

    /// <summary>처리할 구간의 가장 짧은 길이(초).</summary>
    private const double MinRangeSeconds = 0.1;

    /// <summary>구간을 처음 켰을 때 잡는 길이(초) — 예전 "앞부분 10초 시험"과 같은 쓰임새.</summary>
    private const double DefaultRangeSeconds = 10;

    /// <summary>
    /// 실제로 처리할 구간(초). 숫자 칸을 비웠거나(NaN) 영상 밖·거꾸로 적었어도 타임라인·예상치·작업이 모두 이 값을 쓴다 —
    /// 그래야 보이는 것과 처리하는 것이 같다.
    /// </summary>
    private (double Start, double End) Range
    {
        get
        {
            double start = Clamp(RangeStart, 0, DurationSeconds, 0), end = Clamp(RangeEnd, 0, DurationSeconds, DurationSeconds);
            return start <= end ? (start, end) : (end, start);
        }
    }

    /// <summary>구간의 끝이 영상의 처음·끝과 이만큼(초) 안이면 처음부터·끝까지로 본다 (한 프레임보다 짧다).</summary>
    private const double EdgeTolerance = 0.005;

    /// <summary>작업에 넘길 구간. 처음부터면 시작이, 끝까지면 길이가 null이다 (둘 다 null이면 영상 전체).</summary>
    private (double? Start, double? Length) JobRange()
    {
        if (!RangeOn) return (null, null);
        var (start, end) = Range;
        // 끝까지 고른 것은 길이를 주지 않는다 — 영상 정보의 길이가 실제 마지막 프레임보다 조금 짧으면 끝 장면이 잘린다.
        return (start > EdgeTolerance ? start : null, end < DurationSeconds - EdgeTolerance ? end - start : null);
    }

    /// <summary>
    /// 구간을 고를 수 있는지. 영상 정보에 길이가 없는 파일(일부 스트림·깨진 파일)은 타임라인이 0.1초짜리로 그려져,
    /// 고른 구간과 실제로 처리되는 범위가 달라진다 — 그런 파일은 전체만 처리한다.
    /// </summary>
    public bool CanUseRange => HasVideo && Info is { DurationSeconds: > 0 };

    /// <summary>타임라인에 칠할 구간 (둘 다 0이면 영상 전체).</summary>
    public double TimelineRangeStart => RangeOn ? Range.Start : 0;

    public double TimelineRangeEnd => RangeOn ? Range.End : 0;

    /// <summary>타임라인의 구간 손잡이를 끌 수 있는지.</summary>
    public bool RangeEditable => RangeOn && !IsRunning;

    /// <summary>구간 칸 아래의 한 줄.</summary>
    public string RangeText
    {
        get
        {
            if (HasVideo && !CanUseRange) return "영상 길이를 알 수 없는 파일이라 구간을 고를 수 없습니다. 전체를 처리합니다.";
            if (!RangeOn) return "영상 전체를 처리합니다.";
            var (start, end) = Range;
            return string.Create(CultureInfo.InvariantCulture,
                $"{TimeText.Format(start)} ~ {TimeText.Format(end)} (길이 {TimeText.Length(end - start)})만 처리합니다. 칸에는 분:초로 적습니다 (예: 1:23.5 · 숫자만 적으면 초). 타임라인 구간 양 끝의 손잡이를 끌거나, 타임라인을 누른 뒤 I(시작)·O(끝) 키로도 정합니다.");
        }
    }

    /// <summary>재생 헤드 자리를 구간의 시작으로 (I 키, [현재 위치] 단추). 끝이 그보다 앞이면 끝을 10초 뒤로 민다. 구간을 꺼 두었으면 켠다.</summary>
    public void MarkRangeStart()
    {
        if (!CanUseRange || IsRunning) return;
        double start = Math.Clamp(PreviewSeconds, 0, DurationSeconds);
        double end = RangeOn && Range.End - start >= MinRangeSeconds ? Range.End : Math.Min(DurationSeconds, start + DefaultRangeSeconds);
        SetRange(start, end);
    }

    /// <summary>재생 헤드 자리를 구간의 끝으로 (O 키, [현재 위치] 단추). 시작이 그보다 뒤면 시작을 10초 앞으로 당긴다.</summary>
    public void MarkRangeEnd()
    {
        if (!CanUseRange || IsRunning) return;
        double end = Math.Clamp(PreviewSeconds, 0, DurationSeconds);
        double start = RangeOn && end - Range.Start >= MinRangeSeconds ? Range.Start : Math.Max(0, end - DefaultRangeSeconds);
        SetRange(start, end);
    }

    /// <summary>
    /// 구간을 정하고 켠다 (타임라인 손잡이를 끌 때도 부른다). 숫자 칸에 긴 소수가 보이지 않게 0.01초로 맞추되,
    /// 영상의 처음·끝에 붙인 것은 반올림하지 않는다 — 끝을 0.01초 앞으로 깎으면 "끝까지"가 아니게 된다.
    /// </summary>
    public void SetRange(double start, double end)
    {
        if (!CanUseRange || IsRunning) return;
        start = start <= EdgeTolerance ? 0 : Math.Round(start, 2);
        end = end >= DurationSeconds - EdgeTolerance ? DurationSeconds : Math.Round(end, 2);
        // 값을 먼저 넣고 켠다 — 켜는 순간 구간이 비어 있으면 기본 구간으로 덮어쓴다(OnRangeOnChanged).
        (RangeStart, RangeEnd) = (start, end);
        RangeOn = true;
    }

    /// <summary>구간을 처음 켰을 때: 지금 보고 있는 자리부터 10초 (영상 끝에 닿으면 앞으로 당긴다).</summary>
    private void PlaceDefaultRange()
    {
        double length = Math.Min(DefaultRangeSeconds, DurationSeconds);
        double start = Math.Clamp(PreviewSeconds, 0, Math.Max(0, DurationSeconds - length));
        (RangeStart, RangeEnd) = (Math.Round(start, 2), Math.Round(start + length, 2));
    }

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
        CleanMode.ProPainter => "로고·워터마크를 주변 화면으로 복원 · ProPainter",
        CleanMode.Delogo => "로고·워터마크를 빠르게 뭉개서 지움 · delogo",
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
    [NotifyPropertyChangedFor(nameof(RangeEditable))]
    [NotifyPropertyChangedFor(nameof(CanEditQueue))]
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
    [NotifyPropertyChangedFor(nameof(ShowsResult))]
    public partial bool HasResult { get; set; }

    public string? ResultPath { get; private set; }

    /// <summary>여러 영상을 차례로 처리할 때 지금 몇 번째인지 ("영상 2/5"). 하나만 처리할 때는 비어 있다.</summary>
    [ObservableProperty]
    public partial string BatchText { get; set; } = "";

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

    /// <summary>지금 화면에 열려 있는 영상 (목록의 하나).</summary>
    private QueueItemViewModel? _current;

    /// <summary>읽으려고 줄 세워 둔 파일들. 읽는 동안 또 끌어다 놓으면 뒤에 붙는다.</summary>
    private readonly System.Collections.Generic.Queue<string> _pendingPaths = new();

    /// <summary>다른 영상으로 바꾸는 중 — 값을 하나씩 옮기는 동안에는 따라 나오는 표시를 다시 계산하지 않는다 (끝에 한 번만).</summary>
    private bool _selecting;

    /// <summary>영상 정보를 읽어 목록에 넣는 중 (그동안은 시작하지 못한다).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanEditQueue))]
    public partial bool IsAdding { get; set; }

    /// <summary>목록을 고칠 수 있는지 (처리하는 동안과 영상 정보를 읽는 동안은 못 고친다).</summary>
    public bool CanEditQueue => !IsRunning && !IsAdding;

    /// <summary>영상 목록을 보일지 — 둘 이상 올렸을 때만. 하나만 다룰 때는 목록 없이 넓게 쓴다.</summary>
    public bool ShowQueue => Queue.Count > 1;

    public string QueueTitle => string.Create(CultureInfo.InvariantCulture, $"영상 목록 ({Queue.Count})");

    /// <summary>시작 단추의 글. 목록의 영상을 차례로 처리할 때는 몇 개인지 적는다.</summary>
    public string StartText => Queue.Count > 1 && Queue.Count(item => item.State != QueueState.Done) is > 0 and var count
        ? string.Create(CultureInfo.InvariantCulture, $"시작 ({count}개)")
        : "시작";

    /// <summary>
    /// 시작을 누르면 처리할 영상들. 목록에 아직 끝나지 않은 것(대기·실패·취소)이 있으면 그것들을 위에서부터 차례로,
    /// 모두 끝났거나 영상이 하나뿐이면 지금 보는 영상을 (다시) 처리한다.
    /// </summary>
    private List<QueueItemViewModel> StartTargets()
    {
        if (_current is null) return [];
        List<QueueItemViewModel> pending = Queue.Count > 1 ? [.. Queue.Where(item => item.State != QueueState.Done)] : [];
        return pending.Count > 0 ? pending : [_current];
    }

    private void RefreshQueueTexts()
    {
        OnPropertyChanged(nameof(ShowQueue));
        OnPropertyChanged(nameof(QueueTitle));
        OnPropertyChanged(nameof(StartText));
    }

    /// <summary>
    /// 영상을 목록에 올린다 (파일 고르기, 끌어다 놓기, 명령줄). 이미 올린 파일은 다시 올리지 않는다.
    /// 처음 올린 것(이미 있던 파일이면 그것)을 화면에 연다.
    /// </summary>
    public async Task AddVideosAsync(IReadOnlyList<string> paths)
    {
        if (IsRunning)
        {
            // 끌어다 놓기는 처리하는 동안에도 들어온다 — 아무 일도 없으면 놓친 줄 안다.
            if (paths.Count > 0) ShowInfo("처리하는 동안에는 영상을 더 올릴 수 없습니다. 끝난 뒤나 [중지]를 누른 뒤에 올리세요.");
            return;
        }
        foreach (string path in paths) _pendingPaths.Enqueue(path);
        // 읽는 동안 또 올리면 읽고 있는 쪽이 이어서 읽는다.
        if (IsAdding || _pendingPaths.Count == 0) return;
        ClearMessage();
        IsAdding = true;
        QueueItemViewModel? first = null;
        var problems = new List<string>();
        try
        {
            var prober = new JobRunner(Settings, _runner, NullJobLog.Instance);
            while (_pendingPaths.TryDequeue(out string? path))
            {
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
                    if (Queue.FirstOrDefault(item => FileLinker.SameFile(item.Path, path)) is { } existing)
                    {
                        first ??= existing;
                        continue;
                    }
                    StatusText = "영상 정보 읽는 중 · " + Path.GetFileName(path);
                    var info = await prober.ProbeAsync(path, CancellationToken.None);
                    var added = new QueueItemViewModel(info, DescribeInfo(info), DefaultOutput(info));
                    Queue.Add(added);
                    first ??= added;
                }
                catch (JobException ex)
                {
                    problems.Add(ex.Message);
                }
                catch (ToolFailedException ex)
                {
                    // ffprobe를 띄우지 못하면 나머지도 마찬가지다 — 같은 알림을 파일 수만큼 쌓지 않는다.
                    problems.Add("ffprobe를 실행하지 못했습니다. 환경 설정에서 FFmpeg 위치를 확인하세요.\n" + FirstLine(ex.Message));
                    _pendingPaths.Clear();
                }
            }
        }
        finally
        {
            _pendingPaths.Clear(); // 예상하지 못한 오류로 나왔을 때 남은 것이 다음에 올릴 때 딸려 오지 않게
            IsAdding = false;
            StatusText = "준비됨";
        }
        if (first is not null) Select(first);
        if (problems.Count > 0)
        {
            const int Shown = 3;
            string more = problems.Count > Shown ? $"\n… 외 {problems.Count - Shown}개" : "";
            ShowError(string.Join("\n", problems.Take(Shown)) + more);
        }
    }

    /// <summary>영상 이름 아래에 보이는 한 줄 (크기, 프레임 속도, 길이, 코덱).</summary>
    private static string DescribeInfo(MediaInfo info)
    {
        string aspect = info.SampleAspectRatio is { Num: var n, Den: var d } && n != d
            ? string.Create(CultureInfo.InvariantCulture, $" (화면비 {info.DisplayAspect:0.00}:1)")
            : "";
        string subtitles = info.SubtitleCodecs.Count > 0 ? $" · 자막 {info.SubtitleCodecs.Count}개" : "";
        string audio = info.HasAudio ? string.Join(", ", info.AudioCodecs) : "없음";
        string interlaced = info.IsInterlaced ? " · 인터레이스" : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"{info.Width}×{info.Height}{aspect} · {info.Fps.Value:0.###} fps · {FormatTime(info.DurationSeconds)} · 영상 {info.VideoCodec ?? "?"}{interlaced} · 오디오 {audio}{subtitles}");
    }

    /// <summary>처음 잡아 주는 출력 파일: 원본 옆의 "이름 [nekoVup].mp4". 목록의 다른 영상과 겹치면 번호를 붙인다.</summary>
    private string DefaultOutput(MediaInfo info)
    {
        // 자막이 있으면 자막을 담을 수 있는 mkv를 기본으로 한다.
        string extension = info.SubtitleCodecs.Count > 0 || Path.GetExtension(info.Path).Equals(".mkv", StringComparison.OrdinalIgnoreCase) ? ".mkv" : ".mp4";
        string output = Path.Combine(Path.GetDirectoryName(info.Path)!, Path.GetFileNameWithoutExtension(info.Path) + " [nekoVup]" + extension);
        return BatchCheck.MakeUnique(output, Queue.Select(item => ReferenceEquals(item, _current) ? OutputPath : item.OutputPath));
    }

    /// <summary>목록에서 고른 영상을 화면에 연다. 처리하는 동안에는 바꾸지 못한다 (처리 중인 영상을 보여 준다).</summary>
    public void SelectVideo(QueueItemViewModel item)
    {
        if (IsRunning && !ReferenceEquals(item, _current))
        {
            ShowInfo("처리하는 동안에는 다른 영상을 열 수 없습니다. 지금 처리하는 영상이 화면에 보입니다.");
            return;
        }
        if (!CanEditQueue || !Queue.Contains(item)) return;
        ClearMessage();
        Select(item);
        // 실패한 영상을 누르면 그 까닭을 다시 보여 준다 (여럿을 처리할 때는 영상마다 알림을 띄우지 않는다).
        if (item is { State: QueueState.Failed, Detail.Length: > 0 }) ShowError(item.Detail);
    }

    /// <summary>화면의 박스·구간·출력 파일을 지금 영상에 넣어 둔다 (다른 영상을 보다가 돌아오면 그대로 있게).</summary>
    private void SaveCurrent()
    {
        if (_current is not { } item) return;
        item.Boxes = [.. Boxes];
        (item.RangeOn, item.RangeStart, item.RangeEnd) = (RangeOn, RangeStart, RangeEnd);
        item.OutputPath = OutputPath;
        item.PreviewSeconds = PreviewSeconds;
        // 결과 파일은 처리가 끝날 때 넣어 둔다 (ExecuteAsync) — 여기서 화면의 것을 넣으면 빠른 미리보기(임시 파일)가 섞인다.
    }

    private void Select(QueueItemViewModel item)
    {
        if (ReferenceEquals(item, _current)) return;
        SaveCurrent();
        _selecting = true;
        try
        {
            if (_current is { } previous) previous.IsCurrent = false;
            // Clear()는 지운 박스를 알려 주지 않아 구독이 남는다 — 하나씩 뺀다.
            while (Boxes.Count > 0) Boxes.RemoveAt(Boxes.Count - 1);
            _current = item;
            item.IsCurrent = true;

            var info = item.Info;
            Info = info;
            InputPath = info.Path;
            FileName = item.Name;
            InfoText = item.InfoText;
            DurationSeconds = Math.Max(info.DurationSeconds, 0.1);
            FrameSeconds = 1 / info.Fps.Value;
            PreviewView = PreviewMode.Original;
            DeletePreviewFiles(); // 앞 영상의 빠른 미리보기는 더 쓸 데가 없다
            // 이 영상을 처리해 둔 결과가 남아 있으면 다시 볼 수 있다.
            ResultPath = item.ResultPath is { } result && File.Exists(result) ? result : null;
            _resultWindow = ResultPath is null ? null : item.ResultWindow;
            ResultIsPreview = false;
            HasResult = ResultPath is not null;
            // 구간은 영상마다 다르다. 값을 먼저 넣고 켠다 — 켜는 순간 구간이 비어 있으면 기본 구간으로 덮어쓴다.
            RangeOn = false;
            (RangeStart, RangeEnd) = (item.RangeStart, item.RangeEnd);
            RangeOn = item.RangeOn;
            foreach (var box in item.Boxes) Boxes.Add(box);
            OutputPath = item.OutputPath;
            HasVideo = true;
        }
        finally
        {
            _selecting = false;
        }
        // 영상마다 다르다 (길이를 모르는 파일은 구간을 못 고른다). 두 번째 영상부터는 HasVideo가 그대로라 직접 알린다.
        OnPropertyChanged(nameof(CanUseRange));
        OnPropertyChanged(nameof(RangeText));
        OnPropertyChanged(nameof(RangeStartText));
        OnPropertyChanged(nameof(RangeEndText));
        OnPropertyChanged(nameof(TimelineRangeStart));
        OnPropertyChanged(nameof(TimelineRangeEnd));
        if (!IsRunning) StatusText = "준비됨";
        PreviewSeconds = item.PreviewSeconds;
        // 시각이 앞 영상과 같으면 위 줄이 아무 일도 하지 않아 전체 길이 표시와 움직이는 박스의 자리가 옛 값으로 남는다.
        OnPreviewSecondsChanged(PreviewSeconds);
        UpdateDerived();
        VideoChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>열린 영상이 없는 처음 화면으로 돌아간다 (목록을 다 비웠을 때).</summary>
    private void CloseVideo()
    {
        _selecting = true;
        try
        {
            if (_current is { } previous) previous.IsCurrent = false;
            _current = null;
            while (Boxes.Count > 0) Boxes.RemoveAt(Boxes.Count - 1);
            PreviewView = PreviewMode.Original;
            DeletePreviewFiles();
            ResultPath = null;
            _resultWindow = null;
            ResultIsPreview = false;
            HasResult = false;
            RangeOn = false;
            (RangeStart, RangeEnd) = (0, 0);
            Info = null;
            (InputPath, FileName, InfoText, OutputPath) = ("", "", "", "");
            HasVideo = false;
        }
        finally
        {
            _selecting = false;
        }
        OnPropertyChanged(nameof(CanUseRange));
        OnPropertyChanged(nameof(RangeText));
        StatusText = "준비됨";
        UpdateDerived();
        VideoChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>영상을 목록에서 뺀다 (파일은 건드리지 않는다). 보고 있던 영상이면 옆의 영상을 연다.</summary>
    public void RemoveVideo(QueueItemViewModel item)
    {
        int index = Queue.IndexOf(item);
        if (IsRunning)
        {
            ShowInfo("처리하는 동안에는 목록에서 뺄 수 없습니다. 먼저 [중지]를 누르세요.");
            return;
        }
        if (!CanEditQueue || index < 0) return;
        ClearMessage();
        if (ReferenceEquals(item, _current))
        {
            if (Queue.Count == 1) CloseVideo();
            else Select(Queue[index + 1 < Queue.Count ? index + 1 : index - 1]);
        }
        Queue.RemoveAt(index);
    }

    /// <summary>목록을 모두 비우고 처음 화면으로 돌아간다.</summary>
    public void ClearQueue()
    {
        if (!CanEditQueue) return;
        ClearMessage();
        CloseVideo();
        while (Queue.Count > 0) Queue.RemoveAt(Queue.Count - 1);
    }

    /// <summary>끝난 영상도 모두 '대기'로 돌린다 — 옵션을 바꿔 전부 다시 처리하고 싶을 때.</summary>
    public void ResetQueue()
    {
        if (!CanEditQueue) return;
        foreach (var item in Queue) (item.State, item.Detail, item.Percent) = (QueueState.Waiting, "", 0);
        RefreshQueueTexts();
    }

    /// <summary>
    /// 지금 영상의 박스·구간·출력 파일을 고쳤다 — 이미 끝난 영상이면 다시 '대기'로 돌린다.
    /// 안 그러면 고치고 시작을 눌러도 이 영상은 건너뛴다.
    /// </summary>
    private void Touch()
    {
        if (_selecting || _following || IsRunning || _current is not { State: not QueueState.Waiting } item) return;
        (item.State, item.Detail, item.Percent) = (QueueState.Waiting, "", 0);
        RefreshQueueTexts();
    }

    partial void OnOutputPathChanged(string value) => Touch();

    /// <summary>
    /// 지금 영상의 박스를 목록의 다른 영상에도 넣는다 (그 영상에 있던 박스는 바꾼다). 같은 워터마크가 찍힌 영상 묶음을
    /// 한 번에 처리할 때 쓴다. 화면 크기가 다른 영상은 자리가 맞지 않으므로 건너뛴다.
    /// </summary>
    public void ApplyBoxesToOthers()
    {
        if (!CanEditQueue || _current is not { } source || Info is not { } info) return;
        if (Boxes.Count == 0)
        {
            ShowInfo("다른 영상에 넣을 박스가 없습니다. 먼저 이 영상에서 워터마크 위치를 정하세요.");
            return;
        }
        int applied = 0, skipped = 0;
        foreach (var item in Queue)
        {
            if (ReferenceEquals(item, source)) continue;
            if (item.Info.Width != info.Width || item.Info.Height != info.Height)
            {
                skipped++;
                continue;
            }
            long frame = FrameAt(item.Info, item.PreviewSeconds);
            item.Boxes = [.. Boxes.Select(box => CopyBox(box, info, item.Info, frame)).OfType<BoxViewModel>()];
            if (item.State != QueueState.Waiting) (item.State, item.Detail, item.Percent) = (QueueState.Waiting, "", 0);
            applied++;
        }
        RefreshQueueTexts();
        string skippedNote = skipped > 0 ? $" 화면 크기가 다른 {skipped}개는 건너뛰었습니다." : "";
        ShowInfo(applied > 0
            ? $"이 영상의 박스 {Boxes.Count}개를 다른 영상 {applied}개에 넣었습니다.{skippedNote}"
            : "화면 크기가 같은 다른 영상이 없어 넣지 못했습니다.");
    }

    /// <summary>박스를 다른 영상에 넣을 사본으로. 움직이는 박스는 시각으로 옮겨 그 영상의 프레임에 맞춘다.</summary>
    private static BoxViewModel? CopyBox(BoxViewModel box, MediaInfo from, MediaInfo to, long frame)
    {
        if (box.ToTrack(from.Fps.Value) is { } track) return TrackBox(track, to, frame);
        if (box.ToRect() is not { IsEmpty: false } rect) return null;
        var copy = new BoxViewModel(rect);
        copy.ShowAt(frame);
        return copy;
    }

    /// <summary>
    /// 명령줄로 받은 시작 옵션을 적용한다. 예: NekoVup.exe 영상.mp4 --box 1040,40,190,56 --mode delogo --upscale anime --scale 2 --test-seconds 10 --output 결과.mkv
    /// 영상을 여럿 적으면 모두 목록에 올리고, 박스·구간·출력 옵션은 첫 영상에 적용한다.
    /// </summary>
    public async Task ApplyStartupAsync(IReadOnlyList<string> arguments)
    {
        var files = new List<string>();
        var options = new List<(string Name, string Value)>();
        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < arguments.Count) options.Add((arguments[i], arguments[++i]));
            else if (File.Exists(arguments[i])) files.Add(arguments[i]);
        }
        if (files.Count == 0) return;

        await AddVideosAsync(files);
        if (!HasVideo) return;
        double? rangeStart = null, rangeLength = null;
        foreach (var (name, value) in options)
        {
            switch (name)
            {
                case "--box" when PixelRect.TryParse(value, out var box) && !box.IsEmpty:
                    AddBox(box);
                    break;
                // 움직이는 워터마크: "시각=x,y,w,h;시각=x,y,w,h". --move는 사이를 이어 가고, --jump는 그 시각에 한 번에 옮긴다.
                case "--move" or "--jump" when BoxTrack.TryParse(value, name == "--jump", out var track):
                    AddTrack(track);
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
                // 처리할 구간: --start 시작, --duration(= --test-seconds) 길이. 초("90")나 분:초("1:30"). 명령줄 판과 같은 이름이다.
                case "--start" when TimeText.TryParse(value, out double begin):
                    rangeStart = begin;
                    break;
                case "--duration" or "--test-seconds" when TimeText.TryParse(value, out double seconds) && seconds > 0:
                    rangeLength = seconds;
                    break;
                case "--output" when value.Trim().Length > 0:
                    // 경로로 읽을 수 없는 값은 버린다 — 예외가 나면 뒤에 적힌 옵션까지 적용되지 않는다.
                    try { OutputPath = Path.GetFullPath(value); }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
                    break;
            }
        }
        if (rangeStart is not null || rangeLength is not null)
        {
            double start = rangeStart ?? 0;
            SetRange(start, rangeLength is { } length ? start + length : DurationSeconds);
        }
    }

    /// <summary>미리보기용 프레임(PNG 바이트). <paramref name="result"/>면 결과 파일에서 같은 순간을 뽑는다. 실패하면 null.</summary>
    public async Task<byte[]?> GetPreviewAsync(double seconds, bool result, CancellationToken cancellationToken)
    {
        result &= ResultPath is not null;
        string? path = result ? ResultPath : Info?.Path;
        if (path is null) return null;
        if (Info is { DurationSeconds: > 0 } info)
        {
            // 마지막 프레임보다 뒤의 시각(타임라인의 맨 끝)에서는 그림이 나오지 않는다 — 마지막 프레임을 뽑는다.
            seconds = Math.Min(seconds, FrameGrid.PickSeconds(Math.Max(0, info.EstimateFrames() - 1), info.Fps.Value));
        }
        // 구간만 처리한 결과는 그 구간의 시작이 0초다 — 원본과 결과에서 같은 프레임을 뽑도록 프레임 번호로 옮긴다.
        if (_resultWindow is { } window)
        {
            long frame = FrameGrid.FrameAt(seconds, window.Fps);
            seconds = FrameGrid.PickSeconds(result ? window.View.Clamp(frame) - window.File.First : frame, window.Fps);
        }
        try
        {
            return await _runner.CaptureBytesAsync(new ToolInvocation(BundledTools.Resolve(Settings).Ffmpeg, FfmpegCommands.PreviewFrame(path, seconds)), cancellationToken);
        }
        catch (ToolFailedException)
        {
            return null;
        }
    }

    partial void OnPreviewSecondsChanged(double value)
    {
        PreviewTimeText = FormatTime(value) + " / " + FormatTime(DurationSeconds);
        FollowBoxes();
    }

    /// <summary>
    /// 결과 파일이 담은 원본 구간. <paramref name="File"/>은 파일에 든 프레임, <paramref name="View"/>는 그 가운데 볼 수 있는 부분
    /// (빠른 미리보기에서는 실제 렌더링과 같게 나오는 부분만 — <see cref="ScenePlan.Exact"/>).
    /// </summary>
    internal readonly record struct ResultWindow(FrameSpan File, FrameSpan View, double Fps)
    {
        public bool Contains(double seconds) => View.Contains(FrameGrid.FrameAt(seconds, Fps));
        public double Clamp(double seconds) => Contains(seconds) ? seconds : FrameGrid.TimeOf(View.Clamp(FrameGrid.FrameAt(seconds, Fps)), Fps);

        /// <summary>
        /// 실제로 만들어진 프레임 수에 맞춘다. 영상 정보의 길이로 어림한 수와 다를 수 있다 (끝까지 처리한 때) —
        /// 그대로 두면 파일에 없는 프레임을 보려다 빈 화면이 나온다.
        /// </summary>
        public ResultWindow Fit(long frames)
        {
            if (frames <= 0 || frames == File.Count) return this;
            var file = File with { Count = frames };
            long first = Math.Min(View.First, file.End - 1);
            long end = View.End >= File.End ? file.End : Math.Min(View.End, file.End);
            return this with { File = file, View = new FrameSpan(first, Math.Max(1, end - first)) };
        }
    }

    /// <summary>결과 파일이 담은 원본 구간. 영상 전체를 처리했으면 null.</summary>
    private ResultWindow? _resultWindow;

    /// <summary>결과를 보는 중이면 원본 시각을 결과가 담은 구간 안으로 당긴다.</summary>
    private double ClampToResult(double seconds) => ResultHas(seconds) ? seconds : _resultWindow!.Value.Clamp(seconds);

    /// <summary>
    /// 결과가 그 시각의 장면을 담고 있는지. 타임라인의 맨 끝(마지막 프레임보다 뒤)은 마지막 프레임으로 본다 — 재생 헤드가 가리키는
    /// 프레임과 같게. 안 그러면 끝까지 처리한 결과를 보다가 맨 끝으로 가면 원본으로 돌아간다.
    /// </summary>
    private bool ResultHas(double seconds)
    {
        if (_resultWindow is not { } window) return true;
        if (Info is { DurationSeconds: > 0 } info)
            seconds = Math.Min(seconds, FrameGrid.TimeOf(Math.Max(0, info.EstimateFrames() - 1), info.Fps.Value));
        return window.Contains(seconds);
    }

    /// <summary>
    /// 미리보기 시각을 옮긴다. 결과가 담지 않은 곳(구간 밖, 빠른 미리보기로 처리한 장면 밖)을 누르면 원본 보기로 돌아간다 —
    /// 결과에 붙잡아 두면 다른 장면을 보러 갈 수 없고, 그냥 두면 표시는 그 시각인데 화면은 결과의 첫·마지막 장면인 채로 남는다.
    /// </summary>
    public void SeekPreview(double seconds)
    {
        // 영상 밖의 시각(명령줄로 받은 움직이는 박스의 자리가 영상 끝보다 뒤일 수 있다)은 끝으로 당긴다.
        seconds = Math.Clamp(seconds, 0, Math.Max(0, DurationSeconds));
        if (ShowsResult && !ResultHas(seconds)) PreviewView = PreviewMode.Original;
        PreviewSeconds = seconds;
    }

    partial void OnPreviewViewChanged(PreviewMode value)
    {
        // 결과 구간 밖의 시각을 보고 있었으면 안으로 옮긴다 — 안 그러면 비교·결과로 바꿔도 결과 쪽 화면이 그대로다.
        if (ShowsResult) PreviewSeconds = ClampToResult(PreviewSeconds);
    }

    // ═════════════════════ 박스 ═════════════════════

    public BoxViewModel AddBox(PixelRect rect)
    {
        var box = new BoxViewModel(rect);
        if (Info is { } info) box.ShowAt(PlayheadFrame(info));
        Boxes.Add(box);
        return box;
    }

    /// <summary>움직이는 박스를 넣는다 (명령줄의 --move, --jump).</summary>
    private void AddTrack(BoxTrack track)
    {
        if (Info is { } info && TrackBox(track, info, PlayheadFrame(info)) is { } box) Boxes.Add(box);
    }

    /// <summary>시각으로 적힌 움직임을 그 영상의 박스로 만든다. 쓸 수 있는 자리가 하나도 없으면 null.</summary>
    private static BoxViewModel? TrackBox(BoxTrack track, MediaInfo info, long playhead)
    {
        try
        {
            // 숫자 칸은 화면 밖의 값을 담지 못한다 (0보다 작으면 0으로 고쳐 적는다) — 화면에 걸치는 동안의 자리를 화면 안으로 잘라서 넣는다.
            var screen = new PixelRect(0, 0, info.Width, info.Height);
            if (BoxPath.From(track, info.Fps.Value).ClampTo(screen)?.Flatten() is not { } path) return null;
            // 영상 끝보다 뒤에 적은 자리에는 재생 헤드가 닿지 못한다 (가 볼 수도, 지울 수도 없다) — 마지막 프레임에서의 자리로 바꿔 둔다.
            long end = info.DurationSeconds > 0 ? Math.Max(0, info.EstimateFrames() - 1) : long.MaxValue;
            var keys = path.Frames.Select(frame => Math.Min(frame, end)).Distinct().Select(frame => KeyValuePair.Create(frame, path.At(frame)));
            var box = new BoxViewModel();
            box.Load(BoxPath.FromFrames(keys, path.Jump), playhead);
            return box;
        }
        catch (ArgumentException)
        {
            return null; // 쓸 수 있는 자리가 하나도 없다
        }
    }

    /// <summary>움직이는 박스를 지금 보는 장면에서의 자리로 옮긴다.</summary>
    private void FollowBoxes()
    {
        if (Info is not { } info || Boxes.Count == 0) return;
        long frame = PlayheadFrame(info);
        // 자리만 따라 옮기는 것이라 처리 영역은 그대로다 — 화면만 다시 그린다.
        _following = true;
        foreach (var box in Boxes) box.ShowAt(frame);
        _following = false;
        if (Boxes.Any(b => b.Moving)) OverlayChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool _following;

    /// <summary>움직이는 박스에서, 지금 장면의 앞(-1)·뒤(+1)에 맞춰 둔 자리가 있는 시각. 없으면 null.</summary>
    public double? KeySeconds(BoxViewModel box, int direction) =>
        Info is { } info && box.NeighborKey(direction) is { } frame ? FrameGrid.TimeOf(frame, info.Fps.Value) : null;

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
        Touch();
        UpdateDerived();
    }

    private void OnBoxPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_following || e.PropertyName == nameof(BoxViewModel.KeyText)) return;
        // 박스를 끌면 X·Y·너비·높이가 차례로 바뀐다 — 다 넣은 뒤의 알림(PlacedProperty) 하나로 한 번만 계산한다.
        if (sender is BoxViewModel { Setting: true }) return;
        Touch();
        UpdateDerived();
    }

    partial void OnRemoveOnChanged(bool value) => UpdateDerived();
    partial void OnRemoveMethodIndexChanged(int value) => UpdateDerived();
    partial void OnUpscaleOnChanged(bool value) => UpdateDerived();
    partial void OnScaleIndexChanged(int value) => UpdateDerived();
    partial void OnPadChanged(double value) => UpdateDerived();
    partial void OnMaxAreaChanged(double value) => UpdateDerived();
    partial void OnRangeOnChanged(bool value)
    {
        // 처음 켜면(정해 둔 구간이 없으면) 지금 보고 있는 자리부터 10초를 잡는다. 정해 둔 구간이 있으면(짧아도) 그대로 쓴다.
        if (value && RangeStart == 0 && RangeEnd == 0) PlaceDefaultRange();
        Touch();
        UpdateDerived();
    }

    partial void OnRangeStartChanged(double value) => OnRangeEdited();
    partial void OnRangeEndChanged(double value) => OnRangeEdited();

    /// <summary>
    /// 구간을 고치기 시작하면 미리보기를 원본으로 돌린다 — 결과를 보는 중에는 재생 헤드가 결과 구간 안으로 당겨져,
    /// 손잡이를 끌어도 새로 자를 자리의 장면을 볼 수 없다.
    /// </summary>
    private void OnRangeEdited()
    {
        if (PreviewView != PreviewMode.Original) PreviewView = PreviewMode.Original;
        Touch();
        UpdateDerived();
    }

    /// <summary>박스·옵션에서 따라 나오는 표시(처리 영역, 경고, 결과 크기)를 다시 계산한다.</summary>
    private void UpdateDerived()
    {
        if (_selecting) return; // 다른 영상으로 바꾸는 중 — 다 옮긴 뒤에 한 번 계산한다
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

            var paths = Boxes.Select(b => b.ToPath()).OfType<BoxPath>().ToList();
            if (Mode == CleanMode.ProPainter && paths.Count > 0)
            {
                try
                {
                    // 움직이는 박스는 지나가는 자리를 모두 덮게 영역을 잡는다
                    var plan = MotionPlan.Create(paths, info.Width, info.Height, BuildRegionOptions());
                    var regions = plan.Regions;
                    RegionRects = [.. regions.Select(r => r.Rect)];
                    var (rangeStart, rangeLength) = JobRange();
                    long frames = info.EstimateFrames(rangeStart, rangeLength);
                    double gb = JobRunner.EstimateTempBytes(regions, frames) / 1024.0 / 1024.0 / 1024.0;
                    RegionText = string.Create(CultureInfo.InvariantCulture,
                        $"처리 영역 {string.Join(", ", regions.Select(r => $"{r.Rect.Width}×{r.Rect.Height}"))} (점선) · 임시 공간 약 {gb:0.0} GB");
                    var large = regions.Where(r => r.NeedsResize).ToList();
                    if (large.Count > 0)
                    {
                        HasRegionWarning = true;
                        bool roams = Enumerable.Range(0, regions.Count).Any(i => regions[i].NeedsResize && plan.Moves(i));
                        string advice = roams
                            ? "워터마크가 지나가는 자리가 넓어서 그렇습니다 — 넓게 돌아다니는 워터마크는 delogo 방식이 더 깔끔할 수 있습니다."
                            : "박스를 줄이거나 여백을 낮춰 보세요.";
                        RegionWarning = string.Create(CultureInfo.InvariantCulture,
                            $"처리 영역이 면적 상한({BuildRegionOptions().MaxArea:N0}px)을 넘어 {large.Min(r => r.ResizeRatio):0.##}배로 줄여서 복원합니다. 그 부분이 조금 흐려질 수 있습니다. {advice}");
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

    public bool CanStart => HasVideo && !IsRunning && !IsUpdating && !IsAdding;

    /// <summary>
    /// 화면의 설정으로 작업 요청을 만든다. <paramref name="scene"/>이면 재생 헤드 둘레만 임시 폴더로 처리하는 빠른 미리보기.
    /// <paramref name="window"/>는 결과 파일이 담을 원본 구간 (영상 전체면 null).
    /// </summary>
    private JobRequest? BuildRequest(bool scene, out ResultWindow? window)
    {
        window = null;
        if (Info is null) return null;
        var (mode, upscale) = (Mode, Upscale);
        var boxes = Boxes.Where(b => !b.HasTrack).Select(b => b.ToRect()).Where(r => !r.IsEmpty).ToList();
        var tracks = Boxes.Select(b => b.ToTrack(Info.Fps.Value)).OfType<BoxTrack>().ToList();

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
        if (mode != CleanMode.None && boxes.Count == 0 && tracks.Count == 0)
        {
            ShowError("워터마크 위치가 없습니다. 미리보기 위에서 끌어서 박스를 그리거나 워터마크 제거를 끄세요.");
            return null;
        }
        if (scene)
        {
            // 빠른 미리보기: 구간·출력 파일은 쓰지 않는다 (재생 헤드 둘레만 임시 폴더에 처리한다).
            if (!CanUseRange)
            {
                ShowError("영상 길이를 알 수 없는 파일이라 장면을 골라 미리 처리할 수 없습니다.");
                return null;
            }
        }
        else
        {
            if (RangeOn && !CanUseRange)
            {
                ShowError("영상 길이를 알 수 없는 파일이라 구간을 고를 수 없습니다. '일부 구간만 처리'를 끄고 전체를 처리하세요.");
                return null;
            }
            // 손잡이로 줄일 수 있는 가장 짧은 구간(0.1초)도 받는다 — 부동소수점 오차로 0.0999…가 되어도.
            if (RangeOn && Range.End - Range.Start < MinRangeSeconds - 1e-6)
            {
                ShowError("처리할 구간이 너무 짧습니다. 타임라인에서 구간의 시작과 끝을 다시 정하세요.");
                return null;
            }
            if (Unquote(OutputPath).Length == 0)
            {
                ShowError("출력 파일을 지정하세요.");
                return null;
            }
        }
        string? output = scene
            // 이름을 매번 다르게 한다 — 앞 미리보기 파일을 아직 그림을 뽑느라 붙잡고 있을 수 있다.
            ? Path.Combine(PreviewFolder, string.Create(CultureInfo.InvariantCulture, $"scene-{Environment.TickCount64}.mkv"))
            : ResolveOutputPath();
        if (output is null)
        {
            ShowError("출력 파일 경로가 올바르지 않습니다: " + Unquote(OutputPath));
            return null;
        }
        var options = new JobOptions
        {
            Mode = mode,
            Upscale = upscale,
            Scale = ScaleIndex + 2,
            Region = BuildRegionOptions(),
            ChunkLength = (int)Clamp(ChunkLength, 20, 1000, 100),
            Overlap = (int)Clamp(Overlap, 0, 200, 10),
            SubvideoLength = (int)Clamp(SubvideoLength, 10, 500, 40),
            Crf = (int)Clamp(Crf, 0, 40, 18),
            Sharpen = (int)Clamp(Sharpen, 0, FfmpegCommands.MaxSharpen, 0),
            Deinterlace = DeinterlaceMode,
        };

        // 처리할 구간은 프레임 번호로 정한다 — 초로만 넘기면 결과의 첫 프레임이 어느 것인지가 반올림에 달려,
        // 미리보기에서 원본과 결과가 한 프레임씩 어긋난다.
        double fps = Info.Fps.Value;
        var job = JobFrames(Info);
        bool toEnd = !RangeOn || Range.End >= DurationSeconds - EdgeTolerance;
        var frames = job;
        if (scene)
        {
            long playhead = PlayheadFrame(Info);
            int sceneFrames = Math.Max(1, (int)Math.Round(SceneSeconds * fps));
            // 길이를 모르는 영상은 프레임 수도 모른다 — 맨 앞의 한 장면만 처리한다 ("끝까지"로 넘기면 영상 전체를 처리하게 된다).
            bool known = Info.DurationSeconds > 0;
            if (!known) job = new FrameSpan(0, sceneFrames);
            // 재생 헤드가 고른 구간 밖이면 그 장면은 영상 전체를 처리할 때의 모습으로 보여 준다.
            else if (!job.Contains(playhead)) job = new FrameSpan(0, Info.EstimateFrames());
            ScenePlan plan;
            try
            {
                if (mode == CleanMode.ProPainter) JobRunner.ValidateProPainterOptions(options);
                plan = ScenePlanner.Plan(playhead, job, sceneFrames,
                    mode == CleanMode.ProPainter, upscale == UpscalePreset.OldVideo, options.ChunkLength, options.Overlap);
            }
            catch (JobException ex)
            {
                ShowError(ex.Message);
                return null;
            }
            // 장면이 작업의 끝에 닿았으면 길이를 주지 않는다 (아래와 같은 까닭).
            toEnd = known && job.End >= Info.EstimateFrames() && plan.File.End >= job.End;
            frames = plan.File;
            window = new ResultWindow(plan.File, plan.Exact, fps);
        }
        else if (RangeOn)
        {
            window = new ResultWindow(job, job, fps);
        }

        return new JobRequest
        {
            InputPath = Info.Path,
            OutputPath = output,
            // 빠른 미리보기의 중간 파일도 임시 폴더에 — 출력 폴더(.nekovup)나 OneDrive를 건드리지 않게
            WorkDirectory = scene ? Path.Combine(PreviewFolder, "work") : null,
            Boxes = mode == CleanMode.None ? [] : boxes,
            Tracks = mode == CleanMode.None ? [] : tracks,
            Options = options with
            {
                StartSeconds = FrameGrid.SeekSeconds(frames.First, fps),
                // 끝까지 고른 것은 길이를 주지 않는다 — 영상 정보의 길이가 실제 마지막 프레임보다 조금 짧으면 끝 장면이 잘린다.
                DurationSeconds = toEnd ? null : FrameGrid.LengthSeconds(frames.First, frames.Count, fps),
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

    /// <summary>
    /// 시작: 목록에 아직 끝나지 않은 영상이 있으면 위에서부터 차례로 처리하고(사용자 요청), 하나뿐이거나 모두 끝났으면 지금 영상을 처리한다.
    /// 영상마다 자기 박스·구간·출력 파일을 쓰고, 제거 방식·업스케일 같은 옵션은 함께 쓴다.
    /// 하나가 실패해도 다음 영상으로 넘어가고, 취소하면 남은 영상은 그대로 남는다 (다시 시작하면 이어서 한다).
    /// </summary>
    public async Task StartAsync()
    {
        if (!CanRun()) return;
        ClearMessage();
        var targets = StartTargets();
        if (targets.Count == 0) return;

        // 시작하기 전에 모든 영상의 설정을 먼저 본다 — 몇 시간 뒤 세 번째 영상에서 "워터마크 위치가 없습니다"로 멈추지 않게.
        // 화면의 값으로 요청을 만들므로 영상을 하나씩 열어 가며 만든다. 걸리는 것이 있으면 그 영상을 연 채로 멈춘다.
        var jobs = new List<(QueueItemViewModel Item, JobRequest Request, ResultWindow? Window)>();
        foreach (var item in targets)
        {
            Select(item);
            if (BuildRequest(scene: false, out var window) is not { } request)
            {
                if (targets.Count > 1) Message = $"[{item.Name}] {Message}";
                return;
            }
            jobs.Add((item, request, window));
        }
        if (Queue.Count > 1)
        {
            var entries = jobs.Select(job => new BatchEntry(job.Item.Name, job.Request.InputPath, job.Request.OutputPath)).ToList();
            if (BatchCheck.FindConflict(entries, Queue.Select(item => item.Path)) is { } conflict)
            {
                ShowError(conflict);
                return;
            }
        }

        bool batch = jobs.Count > 1;
        int done = 0, failed = 0;
        string? cancelNote = null;
        BeginRun();
        try
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                var (item, request, window) = jobs[i];
                Select(item); // 처리하는 영상을 화면에 보여 준다 (끝나면 그 결과를 바로 볼 수 있다)
                if (batch)
                {
                    BatchText = string.Create(CultureInfo.InvariantCulture, $"영상 {i + 1}/{jobs.Count}");
                    AppendLog(string.Create(CultureInfo.InvariantCulture, $"── {i + 1}/{jobs.Count} · {item.Name} ──"));
                }
                var end = await ExecuteAsync(request, window, scene: false, item, batch);
                if (end.Outcome == Outcome.Cancelled)
                {
                    cancelNote = end.Note;
                    break;
                }
                if (end.Outcome == Outcome.Done) done++;
                else failed++;
            }
            if (!batch) return;

            // 여럿을 처리한 때는 영상마다 알림을 띄우지 않았다 — 끝에 한 번 모아서 알린다.
            string failedNote = failed > 0 ? $", {failed}개 실패" : "";
            if (cancelNote is not null)
            {
                int left = jobs.Count - done - failed;
                StatusText = "중지됨";
                ShowInfo($"취소했습니다 ({done}개 완료{failedNote}, {left}개 남음). [시작]을 다시 누르면 남은 영상을 이어서 처리합니다."
                    + (cancelNote.Length > 0 ? "\n처리하던 영상: " + cancelNote : ""));
            }
            else if (failed > 0)
            {
                StatusText = $"{done}개 완료 · {failed}개 실패";
                ShowError($"영상 {jobs.Count}개 가운데 {done}개를 완료했고 {failed}개는 실패했습니다. 목록에서 실패한 영상을 누르면 까닭이 보입니다. 해결한 뒤 [시작]을 다시 누르면 실패한 영상만 다시 처리합니다.");
            }
            else
            {
                StatusText = $"{done}개 완료";
                ShowInfo($"영상 {done}개를 모두 완료했습니다. 목록에서 영상을 누르면 결과를 볼 수 있습니다.");
            }
        }
        finally
        {
            EndRun();
        }
    }

    /// <summary>
    /// 현재 장면 빠른 미리보기: 재생 헤드 둘레만 지금 설정으로 처리해 비교 보기로 보여 준다 (사용자 요청).
    /// 실제 작업과 같은 처리 과정·같은 조각(<see cref="ScenePlanner"/>)을 쓰므로 보이는 것이 렌더링 결과와 같다.
    /// ProPainter와 옛 비디오를 함께 켠 때만 옛 비디오의 조각 자리가 실제와 달라 조금 다를 수 있다. 출력 파일은 만들지 않는다.
    /// </summary>
    public async Task PreviewSceneAsync()
    {
        if (!CanRun()) return;
        ClearMessage();
        if (BuildRequest(scene: true, out var window) is not { } request) return;
        BeginRun();
        try
        {
            await ExecuteAsync(request, window, scene: true, item: null, batch: false);
        }
        finally
        {
            EndRun();
        }
    }

    private bool CanRun()
    {
        if (IsRunning || IsAdding) return false;
        if (IsUpdating)
        {
            ShowInfo("업데이트하는 중입니다. 프로그램이 다시 시작된 뒤에 시작하세요.");
            return false;
        }
        return true;
    }

    /// <summary>실행을 시작한다 (영상 하나든 여럿이든 한 번). 취소 단추는 이 실행 전체를 멈춘다.</summary>
    private void BeginRun()
    {
        Log.Clear();
        HasRun = true;
        BatchText = "";
        _jobDone = new TaskCompletionSource();
        _jobCancel = new CancellationTokenSource();
        IsRunning = true;
        OnPropertyChanged(nameof(CanStart));
    }

    private void EndRun()
    {
        _jobCancel?.Dispose();
        _jobCancel = null;
        BatchText = "";
        IsRunning = false;
        OnPropertyChanged(nameof(CanStart));
        RefreshQueueTexts();
        _jobDone?.TrySetResult();
        _jobDone = null;
    }

    private enum Outcome
    {
        Done,
        Cancelled,
        Failed,
    }

    /// <summary>작업 하나가 끝난 모습. <paramref name="Note"/>는 취소했을 때 다시 시작하면 무엇이 남아 있는지.</summary>
    private readonly record struct JobEnd(Outcome Outcome, string Note = "");

    /// <summary>처리하고 있는 목록의 영상 (빠른 미리보기면 null).</summary>
    private QueueItemViewModel? _runningItem;

    /// <summary>작업마다 하나씩 올린다 — 앞 작업이 보낸 진행 알림이 늦게 와도 다음 작업의 표시를 건드리지 않게.</summary>
    private int _runSerial;

    /// <summary>
    /// 작업 하나를 돌리고 화면에 결과를 적는다. <paramref name="batch"/>면 여럿 가운데 하나라 알림은 띄우지 않고
    /// 로그와 목록에만 적는다 (알림은 다 끝난 뒤 한 번).
    /// </summary>
    private async Task<JobEnd> ExecuteAsync(JobRequest request, ResultWindow? window, bool scene, QueueItemViewModel? item, bool batch)
    {
        HasResult = false;
        PreviewView = PreviewMode.Original;
        ResultPath = null;
        ResultIsPreview = false;
        _resultWindow = null;
        if (scene) DeletePreviewFiles();
        StageText = "준비 중";
        StatusText = "처리 중";
        DetailText = "";
        StageProgress = 0;
        TotalProgress = 0;
        TotalText = "0%";
        EtaText = "";
        _lastStage = null;
        _eta.Restart();
        int serial = ++_runSerial;
        _runningItem = item;
        if (item is not null)
        {
            (item.State, item.Detail, item.Percent) = (QueueState.Running, "", 0);
            // 앞서 만든 결과는 이제 덮어쓴다 — 실패하거나 취소하면 볼 결과가 없다.
            (item.ResultPath, item.ResultWindow) = (null, null);
        }

        var token = _jobCancel!.Token;
        var log = new DelegateJobLog((level, message) =>
        {
            if (level == LogLevel.Detail) return; // 도구 출력은 작업 폴더의 job.log에 남는다
            string prefix = level switch { LogLevel.Warning => "⚠ ", LogLevel.Error => "✖ ", _ => "" };
            _dispatcher.TryEnqueue(() => AppendLog(prefix + message));
        });
        var progress = new CallbackProgress(value => OnProgress(value, serial));
        var jobRunner = new JobRunner(Settings, _runner, log);

        try
        {
            var result = await Task.Run(() => jobRunner.RunAsync(request, progress, token), token);
            ResultPath = result.OutputPath;
            // 구간만 처리했으면 결과 파일의 첫 프레임이 원본의 구간 시작이다 (미리보기에서 원본 프레임과 맞춰 보여 준다).
            _resultWindow = window?.Fit(result.FrameCount);
            ResultIsPreview = scene;
            HasResult = true;
            StageText = scene ? "미리보기 완료" : "완료";
            DetailText = string.Create(CultureInfo.InvariantCulture, $"{result.FrameCount}프레임 · {EtaEstimator.Format(result.Elapsed)} 걸림");
            StageProgress = 100;
            TotalProgress = 100;
            TotalText = "100%";
            EtaText = "";
            StatusText = scene ? "미리보기 완료" : "완료";
            if (item is not null)
            {
                (item.State, item.Detail, item.Percent) = (QueueState.Done, $"{DetailText} → {result.OutputPath}", 100);
                // 다른 영상을 보다가 돌아와도 이 결과를 다시 볼 수 있게 넣어 둔다.
                (item.ResultPath, item.ResultWindow) = (result.OutputPath, _resultWindow);
            }
            if (scene)
            {
                // 차이가 한눈에 보이게 원본·결과 나란히 보기로 연다
                PreviewView = PreviewMode.Compare;
                string span = _resultWindow is { } shown
                    ? $" ({FormatTime(FrameGrid.TimeOf(shown.View.First, shown.Fps))} ~ {FormatTime(FrameGrid.TimeOf(shown.View.End, shown.Fps))})"
                    : "";
                ShowInfo($"이 장면{span}을 지금 설정으로 처리해 봤습니다 (출력 파일은 만들지 않았습니다). 미리보기의 세로선을 끌어 원본과 비교하세요.");
            }
            else if (batch)
            {
                AppendLog("✔ 완료: " + result.OutputPath);
            }
            else
            {
                ShowInfo("완료했습니다: " + result.OutputPath);
            }
            return new JobEnd(Outcome.Done);
        }
        catch (OperationCanceledException)
        {
            StageText = "취소됨";
            StatusText = "중지됨";
            EtaText = "";
            string note = scene ? "" : ResumeNote(request); // 빠른 미리보기는 이어서 하지 않는다 (매번 새로 처리한다)
            if (item is not null) (item.State, item.Detail) = (QueueState.Cancelled, note);
            if (!batch) ShowInfo(note.Length > 0 ? "취소했습니다. " + note : "취소했습니다.");
            return new JobEnd(Outcome.Cancelled, note);
        }
        catch (Exception ex) when (ex is JobException or ToolFailedException or IOException or UnauthorizedAccessException)
        {
            // 디스크가 꽉 찼거나 폴더에 쓸 수 없는 것(IOException 등)도 작업 실패로 보여 준다 — 안 그러면 화면이 "처리 중"에 멈춘 것처럼 남는다.
            AppendLog("✖ " + ex.Message);
            return Fail(FirstLine(ex.Message) + RetryHint(ex, scene ? "" : ResumeNote(request)));
        }
        catch (Exception ex)
        {
            // 예상하지 못한 오류도 화면을 "실패"로 돌려놓고 파일에 남긴다 — 그냥 두면 단계 표시가 "처리 중"에 멈춘 채 남는다.
            App.LogError(ex);
            AppendLog("✖ " + ex);
            return Fail("예기치 못한 오류: " + FirstLine(ex.Message) + RetryHint(ex, scene ? "" : ResumeNote(request)));
        }
        finally
        {
            _eta.Stop();
            _runningItem = null;
        }

        JobEnd Fail(string message)
        {
            StageText = "실패";
            StatusText = "실패 — 로그를 확인하세요";
            EtaText = "";
            if (item is not null) (item.State, item.Detail) = (QueueState.Failed, message);
            if (!batch) ShowError(message);
            return new JobEnd(Outcome.Failed);
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
    private void OnProgress(JobProgress value, int serial)
    {
        long now = Stopwatch.GetTimestamp();
        bool stageChanged = value.Stage != _lastStage;
        if (!stageChanged && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastProgressTicks), now) < TimeSpan.FromMilliseconds(100)) return;
        Interlocked.Exchange(ref _lastProgressTicks, now);
        _lastStage = value.Stage;

        _dispatcher.TryEnqueue(() =>
        {
            if (!IsRunning || serial != _runSerial) return;
            if (_runningItem is { } item) item.Percent = (int)Math.Clamp(value.OverallFraction * 100, 0, 100);
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
