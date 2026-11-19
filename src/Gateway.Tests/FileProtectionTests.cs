using Gateway.Dataverse;
using Xunit;

namespace Gateway.Tests;

/// <summary>レポートの保持期間掃除(M-2)とアクセス制限(M-2/L-3)。</summary>
public class FileProtectionTests
{
    [Fact]
    public void PruneOld_保持期間を超えた失敗行CSVのみ削除()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dvgw_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var now = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);
            var old = Path.Combine(dir, "failed_old.csv");
            var fresh = Path.Combine(dir, "failed_fresh.csv");
            var other = Path.Combine(dir, "audit_20260701.csv"); // 対象外パターン
            File.WriteAllText(old, "x");
            File.WriteAllText(fresh, "x");
            File.WriteAllText(other, "x");
            File.SetLastWriteTimeUtc(old, now.AddDays(-40));
            File.SetLastWriteTimeUtc(fresh, now.AddDays(-5));
            File.SetLastWriteTimeUtc(other, now.AddDays(-40));

            var removed = FileProtection.PruneOld(dir, "failed_*.csv", retentionDays: 30, nowUtc: now);

            Assert.Equal(1, removed);
            Assert.False(File.Exists(old));   // 期限超過 → 削除
            Assert.True(File.Exists(fresh));  // 期限内 → 残す
            Assert.True(File.Exists(other));  // パターン外 → 対象外
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PruneOld_保持日数0なら何もしない()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dvgw_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "failed_x.csv");
            File.WriteAllText(f, "x");
            File.SetLastWriteTimeUtc(f, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var removed = FileProtection.PruneOld(dir, "failed_*.csv", retentionDays: 0, nowUtc: DateTime.UtcNow);
            Assert.Equal(0, removed);
            Assert.True(File.Exists(f));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void RestrictToCurrentUser_例外なく適用でき読み書きが維持される()
    {
        var f = Path.Combine(Path.GetTempPath(), "dvgw_perm_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(f, "secret");
        try
        {
            FileProtection.RestrictToCurrentUser(f); // 現ユーザーは以後も読める
            Assert.Equal("secret", File.ReadAllText(f));
        }
        finally { File.Delete(f); }
    }
}
