using System.Security.AccessControl;
using System.Security.Principal;

namespace NekoVup.Core.Runtime;

/// <summary>
/// 드라이브 바로 아래에 두는 폴더(<c>X:\NekoVup-runtime</c>, <c>X:\NekoVup-work</c>)를 써도 되는지 본다.
/// 드라이브 바로 아래에는 이 PC의 다른 사용자도 폴더를 만들 수 있다 — 누가 같은 이름의 폴더를 먼저 만들어 파이썬을 넣어 두면
/// 우리는 그것을 "이미 준비된 환경"으로 알고 실행하게 된다. 그래서 폴더를 만든 사람(소유자)을 보고, 다른 사용자의 것이면 쓰지 않는다.
/// </summary>
public static class FolderTrust
{
    /// <summary>Windows 구성 요소 설치 관리자(TrustedInstaller). 미리 정해진 이름이 없어 값으로 적는다.</summary>
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>이름 뒤에 붙이는 번호의 끝 ("NekoVup-runtime-9"까지).</summary>
    private const int LastNumber = 9;

    /// <summary>
    /// 써도 되는 폴더인지. 아직 없는 폴더(우리가 만들 것)와 권한이 없는 파일 시스템(FAT·exFAT, 네트워크 경로 — 가릴 방법이 없다)은 참.
    /// 소유자를 읽지 못하면(다른 사용자가 잠가 둔 폴더) 거짓.
    /// </summary>
    public static bool IsTrusted(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists || !HasPermissions(info.FullName)) return true;
            return info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner
                && IsTrustedOwner(owner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 믿는 소유자: 나, 관리자 그룹(관리자 권한으로 실행해 만든 폴더), 시스템. 이 PC에 더는 없는 계정도 받는다 —
    /// Windows를 다시 설치하면 전에 내가 만든 폴더의 소유자가 그렇게 보이는데, 그때마다 6 GB를 새로 받게 할 수는 없다.
    /// 지금 이 PC에 있는 다른 사용자의 것만 거른다.
    /// </summary>
    internal static bool IsTrustedOwner(SecurityIdentifier owner)
    {
        using var me = WindowsIdentity.GetCurrent();
        if (me.User is { } user && owner.Equals(user)) return true;
        if (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid)) return true;
        if (owner.Value.Equals(TrustedInstaller, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            owner.Translate(typeof(NTAccount));
            return false;
        }
        catch (IdentityNotMappedException)
        {
            return true;
        }
        catch (SystemException)
        {
            return false; // 계정을 알아보지 못했다 (도메인에 닿지 않는 등) — 모르면 쓰지 않는다
        }
    }

    /// <summary>
    /// 폴더를 나·관리자·시스템만 쓸 수 있게 한다. 드라이브 바로 아래의 폴더는 "이 PC의 모든 사용자가 수정"하는 권한을 물려받는다 —
    /// 그대로 두면 다른 사용자가 중간 프레임을 들여다보거나 바꿔 놓을 수 있다. 이미 따로 정해 둔 폴더는 건드리지 않고, 실패해도 그냥 넘어간다.
    /// </summary>
    public static void MakePrivate(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists || !HasPermissions(info.FullName)) return;
            if (info.GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected) return;
            using var me = WindowsIdentity.GetCurrent();
            if (me.User is not { } user) return;

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            SecurityIdentifier[] allowed = [user, new(WellKnownSidType.BuiltinAdministratorsSid, null), new(WellKnownSidType.LocalSystemSid, null)];
            foreach (var who in allowed)
            {
                security.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }
            info.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
        }
    }

    /// <summary>
    /// <paramref name="parent"/> 아래에서 쓸 폴더. 보통은 <paramref name="name"/> 그대로이고, 그 이름의 폴더가 다른 사용자의 것이면
    /// "이름-2", "이름-3"…에서 쓸 수 있는 첫 번째를 고른다.
    /// </summary>
    public static string Pick(string parent, string name) => Pick(parent, name, IsTrusted);

    internal static string Pick(string parent, string name, Func<string, bool> trusted)
    {
        for (int number = 1; number <= LastNumber; number++)
        {
            string candidate = Path.Combine(parent, Numbered(name, number));
            if (trusted(candidate)) return candidate;
        }
        return Path.Combine(parent, name);
    }

    /// <summary><paramref name="parent"/> 아래에 실제로 있는 "이름", "이름-2"… 폴더.</summary>
    public static IEnumerable<string> Existing(string parent, string name)
    {
        for (int number = 1; number <= LastNumber; number++)
        {
            string candidate = Path.Combine(parent, Numbered(name, number));
            if (Directory.Exists(candidate)) yield return candidate;
        }
    }

    /// <summary>폴더 이름이 <paramref name="name"/>이거나 거기에 번호가 붙은 것("이름-2" … "이름-9")인지.</summary>
    public static bool IsNameOrNumbered(string folderName, string name)
    {
        for (int number = 1; number <= LastNumber; number++)
            if (folderName.Equals(Numbered(name, number), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string Numbered(string name, int number) => number == 1 ? name : $"{name}-{number}";

    /// <summary>파일마다 소유자·권한을 적는 파일 시스템인지 (NTFS·ReFS).</summary>
    private static bool HasPermissions(string fullPath)
    {
        try
        {
            return Path.GetPathRoot(fullPath) is { Length: > 0 } drive && new DriveInfo(drive).DriveFormat is "NTFS" or "ReFS";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
