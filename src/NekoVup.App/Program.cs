using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NekoVup.Core.Runtime;
using Velopack;

namespace NekoVup.App;

public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    private static void Main()
    {
        // 설치·업데이트·제거 때 설치 프로그램이 앱을 잠깐 실행해 뒷정리를 시킨다. 창을 띄우기 전에 가장 먼저 처리해야 한다.
        // 제거 때는 자동으로 준비한 ProPainter 환경과 설정까지 같이 지운다 (사용자 요청). 기록은 %Temp%\NekoVup-uninstall.log에 남긴다.
        VelopackApp.Build()
            // 내려받아 두고 적용하지 않은 업데이트(다른 창이 열려 있어 거절했거나 적용이 실패한 것)를 시작할 때 저절로 적용하지 않는다.
            // 적용은 설치 폴더에서 도는 NekoVup을 모두 강제로 끝내므로, 다른 창에서 돌던 작업까지 죽는다. 업데이트 단추로만 적용한다
            // (단추는 다른 창·작업을 확인하고, 받아 둔 파일을 다시 쓴다).
            .SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(version =>
            {
                UninstallLog($"제거 시작 (v{version})");
                try
                {
                    UninstallCleanup.Run(UninstallLog);
                }
                catch (Exception ex)
                {
                    // 뒷정리가 실패해도 제거는 계속돼야 한다. 무엇이 남았는지 알 수 있게 적어 둔다.
                    UninstallLog($"정리 실패: {ex.GetType().Name}: {ex.Message}");
                }
                UpdateService.DeleteTempFiles();
            })
            .Run();

        // 끝나지 못한 작업의 중간 파일 폴더를 적어 둔다 (제거할 때 지우려고).
        WorkFolders.RegistryPath = WorkFolders.DefaultRegistryPath;

        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }

    private static void UninstallLog(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "NekoVup-uninstall.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ", System.Globalization.CultureInfo.InvariantCulture) + message + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
