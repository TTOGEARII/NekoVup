using System.Globalization;
using Microsoft.UI.Xaml;
using NekoVup.Core.Settings;

namespace NekoVup.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        // 실행 파일에 영상을 끌어다 놓거나 "연결 프로그램"으로 열었을 때, 또는 옵션을 붙여 실행했을 때
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Length > 1) _window.OpenOnStartup(arguments[1..]);
    }

    /// <summary>오류를 %AppData%\NekoVup\crash.log에 남긴다.</summary>
    public static void LogError(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            File.AppendAllText(Path.Combine(SettingsStore.DataDirectory, "crash.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ", CultureInfo.InvariantCulture) + exception + Environment.NewLine + Environment.NewLine);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>예기치 못한 오류는 창을 닫지 않고 알림 줄에 보여 주고, 파일에도 남긴다.</summary>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        if (_window is not null)
        {
            _window.ViewModel.ShowError("예기치 못한 오류: " + e.Message);
            e.Handled = true;
        }
    }
}
