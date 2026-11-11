using System.Security.AccessControl;
using System.Security.Principal;
using NekoVup.Core.Runtime;

namespace NekoVup.Tests;

/// <summary>드라이브 바로 아래에 두는 폴더를 써도 되는지: 만든 사람(소유자)이 나·관리자·시스템인 폴더만 쓴다.</summary>
public class FolderTrustTests
{
    private static string NewFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nekovup-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void A_Folder_I_Made_And_A_Missing_Folder_Are_Trusted()
    {
        string dir = NewFolder();
        try
        {
            Assert.True(FolderTrust.IsTrusted(dir));
            Assert.True(FolderTrust.IsTrusted(Path.Combine(dir, "아직 없는 폴더")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Only_Me_Administrators_And_System_Are_Trusted_Owners()
    {
        using var me = WindowsIdentity.GetCurrent();
        Assert.True(FolderTrust.IsTrustedOwner(me.User!));
        Assert.True(FolderTrust.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
        Assert.True(FolderTrust.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));

        // 이 PC에 있는 다른 계정·그룹
        Assert.False(FolderTrust.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.WorldSid, null)));
        Assert.False(FolderTrust.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
        Assert.False(FolderTrust.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)));
    }

    [Fact]
    public void An_Account_That_No_Longer_Exists_Is_Trusted()
    {
        // Windows를 다시 설치하면 전에 내가 만든 폴더의 소유자가 이렇게 보인다 (이 PC에 없는 계정).
        var gone = new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-1001");
        Assert.True(FolderTrust.IsTrustedOwner(gone));
    }

    [Fact]
    public void MakePrivate_Leaves_Only_Me_Administrators_And_System()
    {
        string dir = NewFolder();
        try
        {
            File.WriteAllText(Path.Combine(dir, "전에 있던 파일.txt"), "x");
            FolderTrust.MakePrivate(dir);

            var security = new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access);
            if (!security.AreAccessRulesProtected) return; // 권한이 없는 파일 시스템(FAT 등)의 임시 폴더 — 볼 것이 없다

            using var me = WindowsIdentity.GetCurrent();
            string[] allowed =
            [
                me.User!.Value,
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            ];
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            Assert.NotEmpty(rules);
            Assert.All(rules, rule => Assert.Contains(rule.IdentityReference.Value, allowed));

            // 나는 그대로 읽고 쓸 수 있다 (전에 있던 파일도, 새 파일도).
            Assert.Equal("x", File.ReadAllText(Path.Combine(dir, "전에 있던 파일.txt")));
            Directory.CreateDirectory(Path.Combine(dir, "작업"));
            File.WriteAllText(Path.Combine(dir, "작업", "새 파일.txt"), "y");
            Assert.True(FolderTrust.IsTrusted(dir));

            FolderTrust.MakePrivate(dir); // 두 번 불러도 그대로
            Assert.True(new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Pick_Numbers_The_Name_When_The_Folder_Is_Someone_Elses()
    {
        string drive = @"D:\";
        Assert.Equal(@"D:\NekoVup-work", FolderTrust.Pick(drive, "NekoVup-work", _ => true));
        Assert.Equal(@"D:\NekoVup-work-2", FolderTrust.Pick(drive, "NekoVup-work", path => !path.EndsWith("NekoVup-work", StringComparison.Ordinal)));
        Assert.Equal(@"D:\NekoVup-work-3", FolderTrust.Pick(drive, "NekoVup-work", path => path.EndsWith("-3", StringComparison.Ordinal)));
        // 전부 남의 것이면 원래 이름을 돌려준다 — 쓰려는 쪽에서 이유를 알린다.
        Assert.Equal(@"D:\NekoVup-work", FolderTrust.Pick(drive, "NekoVup-work", _ => false));
    }

    [Theory]
    [InlineData("NekoVup-runtime", true)]
    [InlineData("nekovup-RUNTIME", true)]
    [InlineData("NekoVup-runtime-2", true)]
    [InlineData("NekoVup-runtime-9", true)]
    [InlineData("NekoVup-runtime-1", false)]
    [InlineData("NekoVup-runtime-10", false)]
    [InlineData("NekoVup-runtime-", false)]
    [InlineData("NekoVup-runtime2", false)]
    [InlineData("내 NekoVup-runtime", false)]
    public void IsNameOrNumbered_Accepts_The_Name_And_Its_Numbered_Forms(string folderName, bool expected) =>
        Assert.Equal(expected, FolderTrust.IsNameOrNumbered(folderName, "NekoVup-runtime"));

    [Fact]
    public void Existing_Lists_Only_Folders_That_Are_There()
    {
        string dir = NewFolder();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "NekoVup-runtime-2"));
            Directory.CreateDirectory(Path.Combine(dir, "NekoVup-runtime-10")); // 우리가 붙이는 번호가 아니다
            Assert.Equal([Path.Combine(dir, "NekoVup-runtime-2")], FolderTrust.Existing(dir, "NekoVup-runtime"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
