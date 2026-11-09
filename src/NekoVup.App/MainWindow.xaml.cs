using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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

    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = new MainViewModel(DispatcherQueue.GetForCurrentThread());
        InitializeComponent();

        AppWindow.Resize(new SizeInt32(1360, 900));
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
        _previewTimer.Tick += async (_, _) => await LoadPreviewAsync();

        ViewModel.VideoChanged += OnVideoChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.Log.CollectionChanged += OnLogChanged;
        Closed += (_, _) => ViewModel.Cancel();

        _ = CheckUpdateAsync(quiet: true);
    }

    /// <summary>명령줄 인자(열 파일과 옵션)를 적용한다.</summary>
    public async void OpenOnStartup(IReadOnlyList<string> arguments) => await ViewModel.ApplyStartupAsync(arguments);

    // ───────── 뷰모델 → 화면 ─────────

    private void OnVideoChanged(object? sender, EventArgs e)
    {
        var info = ViewModel.Info!;
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

    /// <summary>화이트/다크를 창 전체에 적용한다. 창 단추(최소화·닫기)는 테마 브러시를 따르지 않아 색을 직접 맞춘다.</summary>
    private void ApplyTheme()
    {
        bool dark = ViewModel.IsDark;
        var theme = dark ? ElementTheme.Dark : ElementTheme.Light;
        Root.RequestedTheme = theme;
        SettingsDialog.RequestedTheme = theme;

        Color ink = dark ? Color.FromArgb(255, 0xF5, 0xF5, 0xF5) : Color.FromArgb(255, 0x11, 0x11, 0x11);
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

    private void OnTimelineChanged(object sender, RangeBaseValueChangedEventArgs e) => RequestPreview();

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

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(AppWindow.Id);
        foreach (string extension in VideoExtensions) picker.FileTypeFilter.Add(extension);
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await ViewModel.LoadVideoAsync(file.Path);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (ViewModel.IsRunning || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "이 영상 열기";
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (ViewModel.IsRunning || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.OfType<StorageFile>().FirstOrDefault() is { } file) await ViewModel.LoadVideoAsync(file.Path);
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ───────── 박스 ─────────

    private void OnAddBoxClick(object sender, RoutedEventArgs e) => ViewModel.AddDefaultBox();

    private void OnRemoveBoxClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BoxViewModel box) ViewModel.Boxes.Remove(box);
    }

    // ───────── 실행 ─────────

    private async void OnStartClick(object sender, RoutedEventArgs e) => await ViewModel.StartAsync();

    private void OnCancelClick(object sender, RoutedEventArgs e) => ViewModel.Cancel();

    /// <summary>결과 파일이 있으면 탐색기에서 그 파일을 선택한 채로, 없으면 출력 폴더를 연다.</summary>
    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ResultPath is { } result && File.Exists(result))
        {
            OpenExplorer("/select," + result);
            return;
        }
        string? folder = string.IsNullOrWhiteSpace(ViewModel.OutputPath) ? null : Path.GetDirectoryName(ViewModel.OutputPath.Trim());
        if (folder is not null && Directory.Exists(folder)) OpenExplorer(folder);
        else ViewModel.ShowInfo("아직 결과가 없어요. 영상을 열고 [시작]을 눌러 주세요.");
    }

    private static void OpenExplorer(string argument)
    {
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        info.ArgumentList.Add(argument);
        Process.Start(info)?.Dispose();
    }

    private async void OnBrowseOutputClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker(AppWindow.Id);
        picker.FileTypeChoices.Add("MKV (자막 유지)", [".mkv"]);
        picker.FileTypeChoices.Add("MP4", [".mp4"]);
        if (!string.IsNullOrWhiteSpace(ViewModel.OutputPath))
        {
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(ViewModel.OutputPath);
            picker.DefaultFileExtension = Path.GetExtension(ViewModel.OutputPath) is { Length: > 0 } extension ? extension : ".mkv";
        }
        var file = await picker.PickSaveFileAsync();
        if (file is not null) ViewModel.OutputPath = file.Path;
    }

    // ───────── 설정 ─────────

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        ViewModel.LoadSettingsIntoEditor();
        ViewModel.CheckItems.Clear();
        _uninstallArmed = false;
        UninstallButton.Content = "프로그램 제거";
        UninstallButton.Visibility = _update?.IsInstalled == true ? Visibility.Visible : Visibility.Collapsed;
        if (await SettingsDialog.ShowAsync() == ContentDialogResult.Primary) ViewModel.SaveSettings();
    }

    /// <summary>서류철 탭을 누르면 그 탭의 내용만 보여 준다.</summary>
    private void OnSettingsTabChecked(object sender, RoutedEventArgs e)
    {
        if (PanelTools is null || PanelLook is null || PanelAbout is null) return; // 화면을 만드는 중
        PanelTools.Visibility = ReferenceEquals(sender, TabTools) ? Visibility.Visible : Visibility.Collapsed;
        PanelLook.Visibility = ReferenceEquals(sender, TabLook) ? Visibility.Visible : Visibility.Collapsed;
        PanelAbout.Visibility = ReferenceEquals(sender, TabAbout) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnCheckClick(object sender, RoutedEventArgs e) => await ViewModel.CheckEnvironmentAsync();

    private async void OnBrowsePythonClick(object sender, RoutedEventArgs e)
    {
        if (await PickFileAsync(".exe") is { } path) ViewModel.EditPython = path;
    }

    private async void OnBrowseVideo2XClick(object sender, RoutedEventArgs e)
    {
        if (await PickFileAsync(".exe") is { } path) ViewModel.EditVideo2X = path;
    }

    private async void OnBrowseProPainterClick(object sender, RoutedEventArgs e)
    {
        var folder = await new FolderPicker(AppWindow.Id).PickSingleFolderAsync();
        if (folder is not null) ViewModel.EditProPainterDir = folder.Path;
    }

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
            UninstallButton.Content = "정말 제거할까요? 한 번 더 누르세요";
            return;
        }
        ViewModel.Cancel();
        UpdateService.LaunchUninstaller();
        Close();
    }

    // ───────── 업데이트 ─────────

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e) => await CheckUpdateAsync(quiet: false);

    /// <summary>새 버전이 있으면 아래 줄에 업데이트 버튼을 띄운다. 시작할 때는 조용히(실패해도 알리지 않고) 확인한다.</summary>
    private async Task CheckUpdateAsync(bool quiet)
    {
        try
        {
            _update ??= new UpdateService();
            if (!_update.IsInstalled)
            {
                ViewModel.UpdateStatus = "설치 파일(Setup)로 설치했을 때만 자동으로 업데이트돼요.";
                return;
            }

            ViewModel.UpdateStatus = "확인하는 중…";
            _pendingUpdate = await _update.CheckAsync();
            if (_pendingUpdate is null)
            {
                ViewModel.UpdateAvailable = false;
                ViewModel.UpdateStatus = "최신 버전이에요.";
                return;
            }

            string version = _pendingUpdate.TargetFullRelease.Version.ToString();
            ViewModel.UpdateButtonText = $"🎀 v{version} 업데이트!";
            ViewModel.UpdateAvailable = true;
            ViewModel.UpdateStatus = $"새 버전 v{version}이 나왔어요. 아래 줄의 업데이트 버튼을 눌러 주세요.";
        }
        catch (Exception ex)
        {
            // 오프라인이거나 GitHub가 응답하지 않을 때. 시작할 때의 확인은 조용히 넘어간다.
            ViewModel.UpdateStatus = quiet ? "" : "확인하지 못했어요: " + ex.Message;
        }
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_update is null || _pendingUpdate is null) return;
        if (ViewModel.IsRunning)
        {
            ViewModel.ShowInfo("작업이 끝난 뒤에 업데이트해 주세요.");
            return;
        }

        ViewModel.IsUpdating = true;
        try
        {
            var dispatcher = DispatcherQueue;
            await _update.DownloadAndApplyAsync(_pendingUpdate,
                percent => dispatcher.TryEnqueue(() => ViewModel.StatusText = $"업데이트 내려받는 중 {percent}%"));
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = "업데이트 실패";
            ViewModel.ShowError("업데이트하지 못했어요: " + ex.Message);
            ViewModel.IsUpdating = false;
        }
    }
}
