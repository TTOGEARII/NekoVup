using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.JobObjects;

namespace NekoVup.Core.Processes;

/// <summary>
/// "핸들이 닫히면 소속 프로세스를 모두 종료"로 설정한 Windows Job Object. 도구 실행 한 번마다 하나씩 만든다.
/// <list type="bullet">
/// <item>취소: <see cref="Terminate"/> 한 번으로 도구와 그 자손 프로세스가 한꺼번에 종료된다.
/// (부모가 먼저 죽으면 손자를 놓치는 <see cref="Process.Kill(bool)"/>의 빈틈을 메운다 — 파이썬 실행기가 진짜 파이썬을 따로 띄우는 환경에서 실제로 생긴다.)</item>
/// <item>이 프로그램이 비정상 종료: 운영체제가 핸들을 닫으면서 ffmpeg·python 등이 함께 정리된다.</item>
/// </list>
/// </summary>
internal sealed class ChildProcessJob : IDisposable
{
    private readonly SafeFileHandle _handle;

    private ChildProcessJob(SafeFileHandle handle) => _handle = handle;

    public static unsafe ChildProcessJob? TryCreate()
    {
        var handle = PInvoke.CreateJobObject(null, (string?)null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!PInvoke.SetInformationJobObject((HANDLE)handle.DangerousGetHandle(), JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &info, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
        {
            handle.Dispose();
            return null;
        }

        return new ChildProcessJob(handle);
    }

    /// <summary>프로세스를 Job에 넣는다. 이후 그 프로세스가 만드는 자식도 자동으로 소속된다.</summary>
    /// <returns>넣었으면 true. 실패해도 도구 실행 자체는 계속할 수 있다.</returns>
    public bool Add(Process process)
    {
        try
        {
            return PInvoke.AssignProcessToJobObject(_handle, process.SafeHandle);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Job에 속한 모든 프로세스를 즉시 종료한다.</summary>
    public void Terminate()
    {
        if (!_handle.IsClosed) PInvoke.TerminateJobObject(_handle, 1);
    }

    public void Dispose() => _handle.Dispose();
}
