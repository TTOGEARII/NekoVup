using System.Globalization;

namespace NekoVup.Core.Runtime;

/// <summary>감시 스크립트가 기다리는 시간(초)과 실행을 시도하는 횟수. 기본값이 실제로 쓰는 값이고, 테스트에서만 줄여 쓴다.</summary>
public sealed record WatchdogTiming(int ApplyTimeoutSeconds = 180, int SettleSeconds = 4, int FirstWaitSeconds = 10, int MaxLaunches = 6);

/// <summary>
/// 업데이트를 적용한 뒤 프로그램이 자동으로 다시 뜨지 않는 환경(백신이 새 실행 파일을 잠시 막는 경우)을 위한 감시 스크립트.
/// 바깥(cmd)에서 지켜보다가 새 버전이 적용됐는데 프로그램이 떠 있지 않으면 대신 실행한다.
/// </summary>
public static class RestartWatchdog
{
    /// <summary>
    /// 감시 스크립트가 경로(설치 폴더의 버전 파일, 띄울 실행 파일)를 받는 환경 변수. 경로를 스크립트에 글자로 적지 않는다 —
    /// cmd는 배치 파일을 콘솔 코드 페이지로 읽는데, 영문 Windows에서는 chcp 65001이 듣지 않아 한글이 든 경로가 깨졌다(CI에서 실제로 실패).
    /// 환경 변수는 유니코드 그대로 넘어가고, 펼칠 때 값 속의 %나 괄호를 다시 해석하지도 않는다.
    /// </summary>
    public const string VersionFileVariable = "NEKOVUP_VERSION_FILE", TargetVariable = "NEKOVUP_TARGET";

    /// <summary>
    /// 설치 폴더의 버전 파일(sq.version)에서 "이 버전이 적용됐다"를 알아보는 글자.
    /// 닫는 태그까지 맞춰 본다 — 앞부분만 보면 1.0.2-beta가 깔린 상태에서 1.0.2를 이미 적용된 것으로 잘못 안다.
    /// </summary>
    public static string VersionMarker(string version)
    {
        // 스크립트 안에 그대로 넣으므로 버전에 쓰이는 글자(숫자·영문·점·빼기·더하기)만 받는다.
        if (version.Length == 0 || !version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+'))
            throw new ArgumentException($"버전 표기가 아닙니다: {version}", nameof(version));
        return $"<version>{version}</version>";
    }

    /// <summary>
    /// 감시 스크립트(배치 파일) 내용.
    /// <list type="number">
    /// <item>버전 파일에 새 버전이 적힐 때까지 기다린다. 끝내 적히지 않으면 아무것도 하지 않는다 —
    /// 아직 적용하는 중에 프로그램을 띄우면 바꾸고 있는 파일을 붙잡아 적용을 망친다.</item>
    /// <item>프로그램이 떠 있으면 끝낸다. 아니면 실행하고, 기다렸다가 다시 본다. 기다리는 시간을 매번 두 배로 늘리고 횟수를 제한한다 —
    /// 백신이 풀어 줄 때까지 몇 분은 버티되, 새 버전이 뜨자마자 죽는 경우에 끝없이 다시 띄우지는 않는다.</item>
    /// </list>
    /// </summary>
    /// <param name="targetVersion">적용될 버전.</param>
    /// <param name="imageName">떠 있는지 볼 프로세스 이름 (NekoVup.exe).</param>
    /// <param name="launchCommand">프로그램을 실행하는 배치 명령 한 줄 (<see cref="LaunchCommand"/>).</param>
    /// <param name="timing">기다리는 시간과 횟수. 없으면 기본값.</param>
    /// <remarks>버전 파일 경로는 <see cref="VersionFileVariable"/> 환경 변수로 받는다. 스크립트 글자는 ASCII뿐이다.</remarks>
    public static string BuildScript(string targetVersion, string imageName, string launchCommand, WatchdogTiming? timing = null)
    {
        timing ??= new WatchdogTiming();
        // timeout은 콘솔이 없으면 실패할 수 있어 ping으로 기다린다 (-n N 은 약 N-1초).
        // PATH에 다른 find가 앞서 있는 환경이 있어 시스템 도구는 절대 경로로 부른다.
        // 되풀이는 for가 아니라 goto로 돈다 — for 안에서는 펼친 값 속의 %i 같은 글자가 for 변수로 바뀐다.
        // 버전 파일은 cmd 내장 type으로 읽어 findstr에 넘긴다. findstr.exe는 파일을 시스템 ANSI 코드 페이지로 열어서,
        // 그 코드 페이지에 없는 글자가 든 경로(영문 Windows의 한글 사용자 이름 등)는 찾지 못한다(실측: 이 PC에서 태국어·이모지 폴더 실패).
        return string.Create(CultureInfo.InvariantCulture, $"""
            @echo off
            setlocal EnableExtensions DisableDelayedExpansion
            set "sys=%SystemRoot%\System32"
            set waited=0
            :poll
            type "%{VersionFileVariable}%" 2>nul | "%sys%\findstr.exe" /c:"{VersionMarker(targetVersion)}" >nul && goto applied
            set /a waited+=1
            if %waited% geq {timing.ApplyTimeoutSeconds} goto end
            "%sys%\ping.exe" -n 2 127.0.0.1 >nul
            goto poll
            :applied
            "%sys%\ping.exe" -n {timing.SettleSeconds + 1} 127.0.0.1 >nul
            set tries=0
            set wait={timing.FirstWaitSeconds}
            :launch
            "%sys%\tasklist.exe" /fi "imagename eq {imageName}" | "%sys%\find.exe" /i "{imageName}" >nul && goto end
            {launchCommand}
            set /a tries+=1
            if %tries% geq {timing.MaxLaunches} goto end
            set /a pings=wait+1
            "%sys%\ping.exe" -n %pings% 127.0.0.1 >nul
            set /a wait*=2
            goto launch
            :end
            """).ReplaceLineEndings("\r\n"); // 줄 끝이 LF뿐인 배치 파일은 goto가 표지를 못 찾을 때가 있다
    }

    /// <summary>
    /// 실행 파일(<see cref="TargetVariable"/> 환경 변수)을 띄우는 배치 명령. start로 띄워야 프로그램이 끝날 때까지 스크립트가 붙잡혀 있지 않는다.
    /// </summary>
    public static string LaunchCommand { get; } = $"start \"\" \"%{TargetVariable}%\"";
}
