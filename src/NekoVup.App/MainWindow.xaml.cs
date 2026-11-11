using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using NekoVup.App.ViewModels;
using NekoVup.Core.Settings;
using Velopack;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;

namespace NekoVup.App;

public sealed partial class MainWindow : Window
{
    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".mov", ".m4v", ".avi", ".webm", ".ts", ".m2ts", ".wmv", ".flv"];

    private readonly DispatcherQueueTimer _previewTimer;
    private CancellationTokenSource? _previewCancel;
    private UpdateService? _update;
    private UpdateInfo? _pendingUpdate;
    private bool _uninstallArmed;
    private long _uninstallArmedAt;

    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = new MainViewModel(DispatcherQueue.GetForCurrentThread());
        InitializeComponent();

        ResizeToFit(1360, 900);
        string icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        ApplyTheme();

        Editor.Attach(ViewModel);
        UpdateEditorState();

        // 타임라인을 끄는 동안 프레임을 매번 뽑지 않도록 잠깐 멈췄을 때만 불러온다.
        _previewTimer = DispatcherQueue.CreateTimer();
        _previewTimer.Interval = TimeSpan.FromMilliseconds(140);
        _previewTimer.IsRepeating = false;
        _previewTimer.Tick += (_, _) => Guard(LoadPreviewAsync);

        ViewModel.VideoChanged += OnVideoChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.Log.CollectionChanged += OnLogChanged;
        Closed += OnClosed;
        // 창이 뜰 때 첫 컨트롤(스위치)에 키보드 포커스 테두리가 그려지지 않게, 눈에 띄지 않는 곳에 포커스를 둔다.
        Root.Loaded += (_, _) =>
        {
            SettingsButton.Focus(FocusState.Pointer); // Pointer: 포커스 테두리를 그리지 않는다 (Programmatic은 그린다)
            // 처음 실행: ProPainter 환경이 없으면 알아서 내려받아 준비한다 (설정에 건너뛰기 옵션은 없다 — 사용자 요청).
            Guard(ViewModel.EnsureProPainterAsync);
        };

        _ = CheckUpdateAsync(quiet: true);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    /// <summary>
    /// 창 크기를 정한다. 크기는 100% 배율 기준이라 화면 배율만큼 키우고, 화면(작업 영역)보다 크면 줄인다 —
    /// 작은 화면에서 창 아래쪽(시작 단추)이 화면 밖으로 나가지 않게.
    /// </summary>
    private void ResizeToFit(int width, int height)
    {
        uint dpi = GetDpiForWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id));
        double scale = dpi is >= 96 and <= 960 ? dpi / 96.0 : 1;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var size = new SizeInt32(Math.Min((int)(width * scale), area.Width), Math.Min((int)(height * scale), area.Height));
        AppWindow.Resize(size);

        var position = AppWindow.Position;
        bool inside = position.X >= area.X && position.Y >= area.Y
            && position.X + size.Width <= area.X + area.Width && position.Y + size.Height <= area.Y + area.Height;
        if (!inside) AppWindow.Move(new PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
    }

    private bool _closing;

    /// <summary>
    /// 창을 닫을 때. 작업 중이면 창만 먼저 감추고, 취소한 작업이 뒷정리(쓰다 만 출력 파일 지우기)를 마칠 때까지 잠깐 기다렸다가 끝낸다 —
    /// 바로 끝내면 출력 폴더에 "이름.partial.mkv"가 남는다.
    /// </summary>
    private async void OnClosed(object sender, WindowEventArgs e)
    {
        ViewModel.CancelSetup();
        // 빠른 미리보기 파일(임시 폴더)은 창을 닫을 때 지운다. 작업 중이면 그 작업이 끝난 뒤에.
        if (!ViewModel.IsRunning) MainViewModel.DeletePreviewFiles();
        if (_closing || !ViewModel.IsRunning) return;

        _closing = true;
        e.Handled = true;
        AppWindow.Hide();
        try
        {
            ViewModel.Cancel();
            await ViewModel.WaitForJobAsync(TimeSpan.FromSeconds(5));
            MainViewModel.DeletePreviewFiles();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        finally
        {
            Close();
        }
    }

    private void OnSetupProPainterClick(object sender, RoutedEventArgs e) => Guard(() => ViewModel.SetupProPainterAsync());

    private void OnCancelSetupClick(object sender, RoutedEventArgs e) => ViewModel.CancelSetup();

    /// <summary>명령줄 인자(열 파일과 옵션)를 적용한다.</summary>
    public void OpenOnStartup(IReadOnlyList<string> arguments) => Guard(() => ViewModel.ApplyStartupAsync(arguments));

    // ───────── 뷰모델 → 화면 ─────────

    private void OnVideoChanged(object? sender, EventArgs e)
    {
        // 앞 영상에서 뽑고 있던 그림이 뒤늦게 도착해 새 영상 위에 그려지지 않게 한다.
        _previewCancel?.Cancel();
        _previewTimer.Stop();
        Editor.SetImage(null);
        Editor.SetCompareImage(null);
        if (ViewModel.Info is not { } info) return; // 목록을 다 비웠다
        Editor.SetVideo(info.Width, info.Height, info.DisplayAspect);
        RequestPreview();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.MessageIsError) or nameof(MainViewModel.HasMessage):
                MessageBar.Severity = ViewModel.MessageIsError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
                break;
            case nameof(MainViewModel.IsRunning) or nameof(MainViewModel.RemoveOn):
                UpdateEditorState();
                break;
            case nameof(MainViewModel.PreviewView) or nameof(MainViewModel.ShowsResult):
                // 뷰모델이 보기를 바꿨으면(미리보기 처리가 끝나 비교로, 새 작업·새 영상이라 원본으로) 선택 단추도 맞춘다
                (ViewModel.PreviewView switch { PreviewMode.Compare => ViewCompare, PreviewMode.Result => ViewResult, _ => ViewOriginal }).IsChecked = true;
                UpdateEditorState();
                RequestPreview();
                break;
            case nameof(MainViewModel.IsDark):
                ApplyTheme();
                break;
        }
    }

    /// <summary>
    /// 미리보기의 워터마크 박스는 워터마크 제거를 켰을 때만 보이고 그릴 수 있다 (사용자 요청) — 업스케일만 할 때 박스를 그려도 쓰이지 않아 헷갈린다.
    /// 작업 중에도 막는다. 결과·비교를 보는 동안에는 박스가 결과를 가리지 않게 감추고, 비교 보기에서는 끌면 세로선이 움직인다.
    /// </summary>
    private void UpdateEditorState()
    {
        bool original = !ViewModel.ShowsResult;
        Editor.CompareMode = ViewModel.ShowsResult && ViewModel.PreviewView == PreviewMode.Compare;
        Editor.ShowBoxes = ViewModel.RemoveOn && original;
        Editor.IsEditable = ViewModel.RemoveOn && original && !ViewModel.IsRunning;
        Editor.Redraw();
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && ViewModel.Log.Count > 0)
            LogList.ScrollIntoView(ViewModel.Log[^1]);
    }

    // ───────── 테마 ─────────

    private void OnThemeClick(object sender, RoutedEventArgs e) => ViewModel.IsDark = !ViewModel.IsDark;

    /// <summary>다크/라이트를 창 전체에 적용한다. 창 단추(최소화·닫기)는 테마 브러시를 따르지 않아 색을 직접 맞춘다.</summary>
    private void ApplyTheme()
    {
        bool dark = ViewModel.IsDark;
        var theme = dark ? ElementTheme.Dark : ElementTheme.Light;
        Root.RequestedTheme = theme;
        SettingsDialog.RequestedTheme = theme;

        Color ink = dark ? Color.FromArgb(255, 0xE8, 0xE8, 0xEA) : Color.FromArgb(255, 0x1B, 0x1C, 0x1F);
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = ink;
        titleBar.ButtonHoverForegroundColor = ink;
        titleBar.ButtonPressedForegroundColor = ink;
        titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 0x80, 0x80, 0x80);
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x28, ink.R, ink.G, ink.B);
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x48, ink.R, ink.G, ink.B);
    }

    // ───────── 미리보기 ─────────

    /// <summary>타임라인을 눌러 시각을 옮겼을 때.</summary>
    private void OnTimelineSeek(object? sender, EventArgs e)
    {
        ViewModel.SeekPreview(Timeline.Position);
        // 결과의 끝을 넘겨 눌렀으면 시각이 당겨진다. 당겨진 값이 앞서와 같으면 바인딩이 다시 알려 주지 않으므로 표시를 직접 맞춘다.
        Timeline.Position = ViewModel.PreviewSeconds;
        RequestPreview();
    }

    /// <summary>타임라인 머리줄의 [원본 | 비교 | 결과]를 골랐을 때.</summary>
    private void OnPreviewViewChecked(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return; // 화면을 만드는 중 (IsChecked="True"가 먼저 불린다)
        ViewModel.PreviewView = ReferenceEquals(sender, ViewCompare) ? PreviewMode.Compare
            : ReferenceEquals(sender, ViewResult) ? PreviewMode.Result
            : PreviewMode.Original;
    }

    private void OnScenePreviewClick(object sender, RoutedEventArgs e) => Guard(ViewModel.PreviewSceneAsync);

    /// <summary>타임라인의 구간 손잡이를 끌었을 때.</summary>
    private void OnTimelineRangeChanged(object? sender, EventArgs e) => ViewModel.SetRange(Timeline.RangeStart, Timeline.RangeEnd);

    /// <summary>타임라인에서 I(시작)·O(끝) 키를 눌렀을 때.</summary>
    private void OnTimelineRangeMark(object? sender, bool start)
    {
        if (start) ViewModel.MarkRangeStart();
        else ViewModel.MarkRangeEnd();
    }

    private void OnRangeStartHereClick(object sender, RoutedEventArgs e) => ViewModel.MarkRangeStart();

    private void OnRangeEndHereClick(object sender, RoutedEventArgs e) => ViewModel.MarkRangeEnd();

    private void OnRangeTimeLostFocus(object sender, RoutedEventArgs e) => CommitRangeTime((TextBox)sender);

    private void OnRangeTimeKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        CommitRangeTime((TextBox)sender);
        e.Handled = true;
    }

    /// <summary>구간 칸에 적은 시각(분:초)을 넣는다. 읽을 수 없는 글자나 영상 밖의 시각은 실제 값으로 되돌려 보여 준다.</summary>
    private void CommitRangeTime(TextBox box)
    {
        bool start = ReferenceEquals(box, RangeStartBox);
        if (start) ViewModel.EnterRangeStart(box.Text);
        else ViewModel.EnterRangeEnd(box.Text);
        box.Text = start ? ViewModel.RangeStartText : ViewModel.RangeEndText;
        box.SelectionStart = box.Text.Length;
    }

    private void RequestPreview()
    {
        if (!ViewModel.HasVideo) return;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private static async Task<BitmapImage> ToBitmapAsync(byte[] png)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
        }
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private async Task LoadPreviewAsync()
    {
        _previewCancel?.Cancel();
        var cancel = _previewCancel = new CancellationTokenSource();
        PreviewBusy.IsActive = true;
        try
        {
            // 비교 보기면 왼쪽(원본)과 오른쪽(결과)을 둘 다 뽑는다. 결과 보기면 결과만, 그 밖에는 원본만.
            bool compare = Editor.CompareMode;
            // 시각은 한 번만 읽는다 — 첫 그림을 뽑는 사이에 재생 헤드가 움직이면 양쪽이 서로 다른 장면이 된다.
            double seconds = ViewModel.PreviewSeconds;
            // 비교 보기의 두 그림은 서로 다른 파일에서 뽑으므로 함께 뽑는다 (차례로 뽑으면 장면을 옮길 때마다 두 배로 기다린다).
            var first = ViewModel.GetPreviewAsync(seconds, ViewModel.ShowsResult && !compare, cancel.Token);
            var second = compare ? ViewModel.GetPreviewAsync(seconds, true, cancel.Token) : Task.FromResult<byte[]?>(null);
            await Task.WhenAll(first, second);
            byte[]? png = await first;
            byte[]? after = await second;
            if (cancel.IsCancellationRequested) return;
            if (png is null || png.Length == 0)
            {
                // 결과에서 그 장면을 못 뽑았으면 비운다 — 앞에 보던 그림(원본일 수도 있다)이 결과처럼 남으면 안 된다.
                if (ViewModel.ShowsResult && !compare) Editor.SetImage(null);
                return;
            }

            var bitmap = await ToBitmapAsync(png);
            var afterBitmap = after is { Length: > 0 } ? await ToBitmapAsync(after) : null;
            if (cancel.IsCancellationRequested) return;
            Editor.SetImage(bitmap);
            Editor.SetCompareImage(afterBitmap);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_previewCancel, cancel)) PreviewBusy.IsActive = false;
        }
    }

    // ───────── 파일 열기 / 끌어다 놓기 ─────────

    private void OnOpenClick(object sender, RoutedEventArgs e) => Guard(async () =>
    {
        var picker = new FileOpenPicker(AppWindow.Id);
        foreach (string extension in VideoExtensions) picker.FileTypeFilter.Add(extension);
        // 여러 개를 한 번에 고를 수 있다 — 고른 영상은 목록에 올라가 차례로 처리된다.
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0) await ViewModel.AddVideosAsync([.. files.Select(file => file.Path)]);
    });

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (ViewModel.IsRunning || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = ViewModel.HasVideo ? "목록에 추가" : "영상 열기";
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (ViewModel.IsRunning || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        Guard(async () =>
        {
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                List<string> paths = [.. items.OfType<StorageFile>().Select(file => file.Path).Where(path => path.Length > 0)];
                // 여럿을 한꺼번에 놓으면 영상만 올린다 (같이 딸려 온 자막·그림마다 "읽지 못했습니다"를 띄우지 않게).
                // 하나만 놓았으면 확장자가 낯설어도 열어 본다.
                if (paths.Count > 1)
                    paths = [.. paths.Where(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))];
                if (paths.Count > 0) await ViewModel.AddVideosAsync(paths);
                else ViewModel.ShowInfo("올릴 영상 파일이 없습니다. 영상 파일을 끌어다 놓으세요 (폴더는 올리지 못합니다).");
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    // ───────── 영상 목록 ─────────

    private void OnQueueItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QueueItemViewModel item) ViewModel.SelectVideo(item);
    }

    private void OnQueueRemoveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QueueItemViewModel item) ViewModel.RemoveVideo(item);
    }

    private void OnQueueClearClick(object sender, RoutedEventArgs e) => ViewModel.ClearQueue();

    private void OnQueueResetClick(object sender, RoutedEventArgs e) => ViewModel.ResetQueue();

    private void OnApplyBoxesClick(object sender, RoutedEventArgs e) => ViewModel.ApplyBoxesToOthers();

    // ───────── 박스 ─────────

    private void OnAddBoxClick(object sender, RoutedEventArgs e) => ViewModel.AddDefaultBox();

    private void OnRemoveBoxClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BoxViewModel box) ViewModel.Boxes.Remove(box);
    }

    /// <summary>
    /// 숫자 칸은 적은 값을 포커스를 잃은 뒤에야 반영한다 (LostFocus는 늦게 온다). 값을 적다가 타임라인을 누르면 장면이 먼저 옮겨져서,
    /// 움직이는 박스는 적던 값이 옮겨 간 장면의 자리로 적힌다 — 포커스가 떠나기 직전(이쪽은 바로 온다)에 반영해 둔다.
    /// </summary>
    private void OnBoxNumberLosingFocus(UIElement sender, Microsoft.UI.Xaml.Input.LosingFocusEventArgs e)
    {
        if (sender is not NumberBox box || FindInput(box) is not { } input) return;
        string text = input.Text.Trim();
        // 비운 칸은 숫자 칸이 하던 대로 둔다.
        if (text.Length == 0 || box.NumberFormatter is not Windows.Globalization.NumberFormatting.INumberParser parser) return;
        if (parser.ParseDouble(text) is { } value && value != box.Value) box.Value = value;
    }

    private static TextBox? FindInput(DependencyObject parent)
    {
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox input) return input;
            if (FindInput(child) is { } found) return found;
        }
        return null;
    }

    private void OnPreviousKeyClick(object sender, RoutedEventArgs e) => GoToKey(sender, -1);

    private void OnNextKeyClick(object sender, RoutedEventArgs e) => GoToKey(sender, +1);

    /// <summary>움직이는 박스에서, 앞·뒤에 맞춰 둔 자리가 있는 장면으로 간다.</summary>
    private void GoToKey(object sender, int direction)
    {
        if ((sender as FrameworkElement)?.Tag is not BoxViewModel box || ViewModel.KeySeconds(box, direction) is not { } seconds) return;
        ViewModel.SeekPreview(seconds);
        Timeline.Position = ViewModel.PreviewSeconds;
        RequestPreview();
    }

    private void OnRemoveKeyClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is BoxViewModel box) box.RemoveKeyHere();
    }

    // ───────── 실행 ─────────

    private void OnStartClick(object sender, RoutedEventArgs e) => Guard(ViewModel.StartAsync);

    private void OnCancelClick(object sender, RoutedEventArgs e) => ViewModel.Cancel();

    /// <summary>결과 파일이 있으면 탐색기에서 그 파일을 선택한 채로, 없으면 출력 폴더를 연다.</summary>
    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        // 빠른 미리보기의 결과는 임시 파일이다 — 그 대신 출력 폴더를 연다
        if (ViewModel.ResultPath is { } result && File.Exists(result) && !ViewModel.ResultIsPreview)
        {
            OpenExplorer("/select," + result);
            return;
        }
        string? folder = ViewModel.ResolveOutputPath() is { } output ? Path.GetDirectoryName(output) : null;
        if (folder is not null && Directory.Exists(folder)) OpenExplorer(folder);
        else if (ViewModel.Info is null) ViewModel.ShowInfo("아직 결과가 없습니다. 영상을 열고 [시작]을 누르세요.");
        else if (folder is null) ViewModel.ShowInfo("출력 파일을 지정하세요.");
        else if (Path.GetPathRoot(folder) is { Length: > 0 } drive && !Directory.Exists(drive))
            ViewModel.ShowInfo($"출력 폴더의 드라이브({drive})를 찾을 수 없습니다: {folder}");
        else ViewModel.ShowInfo("출력 폴더가 아직 없습니다. [시작]을 누르면 만들어집니다: " + folder);
    }

    private static void OpenExplorer(string argument)
    {
        // 이름만 적으면 현재 폴더의 explorer.exe가 먼저 잡힐 수 있다 — Windows 폴더의 것을 전체 경로로 띄운다.
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var info = new ProcessStartInfo(explorer) { UseShellExecute = false };
        info.ArgumentList.Add(argument);
        Process.Start(info)?.Dispose();
    }

    private void OnBrowseOutputClick(object sender, RoutedEventArgs e) => Guard(async () =>
    {
        var picker = new FileSavePicker(AppWindow.Id);
        // 형식 목록은 List<string>을 직접 만들어 넘긴다. 컬렉션 식([".mkv"])으로 쓰면 컴파일러가 만든 형식이 넘어가는데,
        // 그 형식은 Windows 쪽 인터페이스로 바꿔 줄 수 없어 InvalidCastException이 난다(이 줄에서 프로그램이 꺼졌었다).
        picker.FileTypeChoices.Add("MKV (자막 유지)", new List<string> { ".mkv" });
        picker.FileTypeChoices.Add("MP4", new List<string> { ".mp4" });
        if (ViewModel.ResolveOutputPath() is { } current)
        {
            // 지금 적혀 있는 이름·형식·폴더에서 시작한다
            string extension = Path.GetExtension(current) is { Length: > 0 } found ? found.ToLowerInvariant() : ".mkv";
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(current);
            picker.DefaultFileExtension = extension;
            picker.InitialFileTypeIndex = extension == ".mp4" ? 1 : 0;
            if (Path.GetDirectoryName(current) is { Length: > 0 } folder && Directory.Exists(folder)) picker.SuggestedFolder = folder;
        }
        var file = await picker.PickSaveFileAsync();
        if (file is not null) ViewModel.OutputPath = file.Path;
    });

    /// <summary>
    /// 단추 처리를 감싼다. 기다리는 도중(await 뒤)에 난 오류는 프로그램 전체의 오류 처리기까지 가지 못하고
    /// 프로그램을 그대로 꺼뜨리므로, 여기서 잡아 알림 줄에 보여 주고 파일에 남긴다.
    /// </summary>
    private async void Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ViewModel.ShowError("예기치 못한 오류: " + ex.Message);
        }
    }

    // ───────── 설정 ─────────

    private void OnSettingsClick(object sender, RoutedEventArgs e) => Guard(async () =>
    {
        ViewModel.LoadSettingsIntoEditor();
        ViewModel.CheckItems.Clear();
        _uninstallArmed = false;
        UninstallButton.Content = "프로그램 제거";
        UninstallButton.Visibility = _update?.IsInstalled == true ? Visibility.Visible : Visibility.Collapsed;
        if (await SettingsDialog.ShowAsync() == ContentDialogResult.Primary) ViewModel.SaveSettings();
    });

    /// <summary>탭을 누르면 그 탭의 내용만 보여 준다.</summary>
    private void OnSettingsTabChecked(object sender, RoutedEventArgs e)
    {
        if (PanelTools is null || PanelLook is null || PanelAbout is null) return; // 화면을 만드는 중
        PanelTools.Visibility = ReferenceEquals(sender, TabTools) ? Visibility.Visible : Visibility.Collapsed;
        PanelLook.Visibility = ReferenceEquals(sender, TabLook) ? Visibility.Visible : Visibility.Collapsed;
        PanelAbout.Visibility = ReferenceEquals(sender, TabAbout) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCheckClick(object sender, RoutedEventArgs e) => Guard(ViewModel.CheckEnvironmentAsync);

    private void OnBrowsePythonClick(object sender, RoutedEventArgs e) => Guard(async () =>
    {
        if (await PickFileAsync(".exe") is { } path) ViewModel.EditPython = path;
    });

    private void OnBrowseVideo2XClick(object sender, RoutedEventArgs e) => Guard(async () =>
    {
        if (await PickFileAsync(".exe") is { } path) ViewModel.EditVideo2X = path;
    });

    private void OnBrowseProPainterClick(object sender, RoutedEventArgs e) => Guard(async () =>
    {
        var folder = await new FolderPicker(AppWindow.Id).PickSingleFolderAsync();
        if (folder is not null) ViewModel.EditProPainterDir = folder.Path;
    });

    private async Task<string?> PickFileAsync(string extension)
    {
        var picker = new FileOpenPicker(AppWindow.Id);
        picker.FileTypeFilter.Add(extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private void OnOpenRepoClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(UpdateService.RepoUrl) { UseShellExecute = true })?.Dispose();

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        OpenExplorer(SettingsStore.DataDirectory);
    }

    /// <summary>실수로 누르지 않게 두 번 눌러야 제거 프로그램이 뜬다.</summary>
    private void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        if (!_uninstallArmed)
        {
            _uninstallArmed = true;
            _uninstallArmedAt = Environment.TickCount64;
            UninstallButton.Content = "정말 제거할까요? 잠시 뒤 한 번 더 누르세요";
            return;
        }
        // 두 번 누르기(더블클릭)의 두 번째 클릭은 확인으로 치지 않는다 — 실수로 바로 제거 프로그램이 뜨지 않게.
        if (Environment.TickCount64 - _uninstallArmedAt < MainViewModel.ConfirmDelayMilliseconds) return;
        _uninstallArmed = false;
        UninstallButton.IsEnabled = false; // 기다리는 동안 또 눌러 제거 프로그램이 두 번 뜨지 않게
        UninstallButton.Content = "제거 프로그램을 띄우는 중…";
        Guard(async () =>
        {
            try
            {
                // 돌던 작업은 먼저 멈추고 뒷정리(쓰다 만 출력 파일 지우기)를 기다린다 — 제거 프로그램이 이 프로그램을 바로 끝내기 때문이다.
                ViewModel.Cancel();
                ViewModel.CancelSetup();
                await ViewModel.WaitForJobAsync(TimeSpan.FromSeconds(5));
                UpdateService.LaunchUninstaller();
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
            {
                // 제거 프로그램(Update.exe)이 없거나 막혀 있다 — 프로그램을 끄지 않고 알린다.
                UninstallButton.Content = "프로그램 제거";
                SettingsDialog.Hide();
                ViewModel.ShowError("제거 프로그램을 띄우지 못했습니다: " + ex.Message + "\nWindows 설정 → 앱에서 제거하세요.");
                return;
            }
            finally
            {
                UninstallButton.IsEnabled = true;
            }
            Close();
        });
    }

    // ───────── 업데이트 ─────────

    private void OnCheckUpdateClick(object sender, RoutedEventArgs e) => Guard(() => CheckUpdateAsync(quiet: false));

    /// <summary>새 버전이 있으면 아래 줄에 업데이트 버튼을 띄운다. 시작할 때는 조용히(실패해도 알리지 않고) 확인한다.</summary>
    private async Task CheckUpdateAsync(bool quiet)
    {
        try
        {
            _update ??= new UpdateService();
            if (!_update.IsInstalled)
            {
                ViewModel.UpdateStatus = "설치 파일(Setup)로 설치했을 때만 자동으로 업데이트됩니다.";
                return;
            }

            ViewModel.UpdateStatus = "확인하는 중…";
            _pendingUpdate = await _update.CheckAsync();
            if (_pendingUpdate is null)
            {
                ViewModel.UpdateAvailable = false;
                ViewModel.UpdateStatus = "최신 버전입니다.";
                return;
            }

            string version = _pendingUpdate.TargetFullRelease.Version.ToString();
            ViewModel.UpdateButtonText = $"v{version} 업데이트";
            ViewModel.UpdateAvailable = true;
            ViewModel.UpdateStatus = $"새 버전 v{version}이 있습니다. 아래 줄의 업데이트 단추를 누르세요.";
        }
        catch (Exception ex)
        {
            // 오프라인이거나 GitHub가 응답하지 않을 때. 시작할 때의 확인은 조용히 넘어간다.
            ViewModel.UpdateStatus = quiet ? "" : "확인하지 못했습니다: " + ex.Message;
        }
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_update is null || _pendingUpdate is null) return;
        if (ViewModel.IsRunning)
        {
            ViewModel.ShowInfo("작업이 끝난 뒤에 업데이트하세요.");
            return;
        }
        if (ViewModel.SetupRunning)
        {
            // 업데이트하면 프로그램이 다시 시작되면서 내려받던 것이 끊긴다.
            ViewModel.ShowInfo("ProPainter 준비가 끝난 뒤에 업데이트하세요.");
            return;
        }
        if (ViewModel.IsUpdating) return;
        if (UpdateService.OtherInstancesRunning())
        {
            ViewModel.ShowInfo("다른 NekoVup 창을 모두 닫은 뒤에 업데이트하세요. 업데이트하면 다른 창은 돌던 작업과 함께 강제로 닫힙니다.");
            return;
        }

        // 여기부터는 작업·ProPainter 준비를 새로 시작할 수 없다 (IsUpdating을 보고 막는다) — 내려받는 사이 시작한 작업이 적용 때 죽지 않게.
        ViewModel.IsUpdating = true;
        try
        {
            var dispatcher = DispatcherQueue;
            await _update.DownloadAndApplyAsync(_pendingUpdate,
                percent => dispatcher.TryEnqueue(() => ViewModel.StatusText = $"업데이트 내려받는 중 {percent}%"));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ViewModel.StatusText = "업데이트 실패";
            ViewModel.ShowError("업데이트하지 못했습니다: " + ex.Message);
            ViewModel.IsUpdating = false;
        }
    }
}
