using System.Security.AccessControl;
using System.Security.Principal;

namespace Gateway.Dataverse;

/// <summary>
/// レポート等の機微ファイルのアクセス制御(M-2/L-3)。現ユーザーのみへの権限制限と、
/// 保持期間を過ぎた古いファイルの後始末を提供する。
/// </summary>
public static class FileProtection
{
    /// <summary>ファイルのアクセス権を現ユーザーのみに制限する(Windows=ACL / 非 Windows=0600)。</summary>
    public static void RestrictToCurrentUser(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User!;
            var security = new FileSecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); // 継承を切る
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 0600
        }
    }

    /// <summary>
    /// directory 内の searchPattern に一致するファイルのうち、最終更新が retentionDays を超えた
    /// ものを削除する(M-2: 無期限残置の防止)。retentionDays が 0 以下なら何もしない。
    /// nowUtc はテスト用の注入点(既定は現在時刻)。削除失敗は無視する(後始末はベストエフォート)。
    /// </summary>
    public static int PruneOld(string directory, string searchPattern, int retentionDays, DateTime nowUtc)
    {
        if (retentionDays <= 0 || !Directory.Exists(directory)) return 0;
        var cutoff = nowUtc.AddDays(-retentionDays);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory, searchPattern))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) { File.Delete(file); removed++; }
            }
            catch { /* ロック中/権限等はスキップ(ベストエフォート) */ }
        }
        return removed;
    }
}
