using System.Globalization;
using Microsoft.UI.Xaml;

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

    /// <summary>예기치 못한 오류는 창을 닫지 않고 알림 줄에 보여 주고, 파일에도 남긴다.</summary>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            string folder = NekoVup.Core.Settings.SettingsStore.DataDirectory;
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "crash.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ", CultureInfo.InvariantCulture) + e.Exception + Environment.NewLine + Environment.NewLine);
        }
        catch (IOException)
        {
        }

        if (_window is not null)
        {
            _window.ViewModel.ShowError("예기치 못한 오류: " + e.Message);
            e.Handled = true;
        }
    }
}
