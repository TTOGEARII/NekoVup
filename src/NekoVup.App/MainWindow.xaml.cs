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
        if (_closing || !ViewModel.IsRunning) return;

        _closing = true;
        e.Handled = true;
        AppWindow.Hide();
        try
        {
            ViewModel.Cancel();
            await ViewModel.WaitForJobAsync(TimeSpan.FromSeconds(5));
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
        var info = ViewModel.Info!;
        // 앞 영상에서 뽑고 있던 그림이 뒤늦게 도착해 새 영상 위에 그려지지 않게 한다.
        _previewCancel?.Cancel();
        Editor.SetImage(null);
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
            case nameof(MainViewModel.IsRunning):
                Editor.IsEditable = !ViewModel.IsRunning;
                Editor.Redraw();
                break;
            case nameof(MainViewModel.IsDark):
                ApplyTheme();
                break;
        }
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

    private void OnPreviewSourceToggled(object sender, RoutedEventArgs e) => RequestPreview();

    private void RequestPreview()
    {
        if (!ViewModel.HasVideo) return;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private async Task LoadPreviewAsync()
    {
        _previewCancel?.Cancel();
        var cancel = _previewCancel = new CancellationTokenSource();
        PreviewBusy.IsActive = true;
        try
        {
            byte[]? png = await ViewModel.GetPreviewAsync(ViewModel.PreviewSeconds, cancel.Token);
            if (cancel.IsCancellationRequested || png is null || png.Length == 0) return;

            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
            }
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (!cancel.IsCancellationRequested) Editor.SetImage(bitmap);
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
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await ViewModel.LoadVideoAsync(file.Path);
    });

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (ViewModel.IsRunning || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "이 영상 열기";
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
                if (items.OfType<StorageFile>().FirstOrDefault() is { } file) await ViewModel.LoadVideoAsync(file.Path);
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    // ───────── 박스 ─────────

    private void OnAddBoxClick(object sender, RoutedEventArgs e) => ViewModel.AddDefaultBox();

    private void OnRemoveBoxClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BoxViewModel box) ViewModel.Boxes.Remove(box);
    }

    // ───────── 실행 ─────────

    private void OnStartClick(object sender, RoutedEventArgs e) => Guard(ViewModel.StartAsync);

    private void OnCancelClick(object sender, RoutedEventArgs e) => ViewModel.Cancel();

    /// <summary>결과 파일이 있으면 탐색기에서 그 파일을 선택한 채로, 없으면 출력 폴더를 연다.</summary>
    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ResultPath is { } result && File.Exists(result))
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
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
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
