using System.Security.Cryptography;
using System.Text;
using Gateway.Dataverse;

namespace Gateway.Host.Lifecycle;

/// <summary>
/// プロセス間認証トークン(要件定義書 §6: gRPC は localhost バインド＋プロセス間認証トークン)。
/// 起動時にランダムトークンを生成し、ユーザースコープのファイルへ書き出す。
/// クライアント(将来の JDBC/ODBC ラッパー)はこのファイルを読み、リクエストヘッダーで提示する。
/// 現状は localhost バインドが主防御で、トークン強制は Gateway:RequireToken で切り替える。
/// </summary>
public sealed class GatewayToken
{
    public const string HeaderName = "x-gateway-token";

    public string Value { get; }
    public string FilePath { get; }

    private GatewayToken(string value, string filePath)
    {
        Value = value;
        FilePath = filePath;
    }

    public static GatewayToken CreateAndPersist()
    {
        var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataverseGW");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "gateway.token");
        File.WriteAllText(path, value);
        FileProtection.RestrictToCurrentUser(path); // L-3: 継承 ACL に頼らず現ユーザーのみへ明示制限
        return new GatewayToken(value, path);
    }

    /// <summary>
    /// 提示トークンを定数時間で照合する(L-3: 非定数時間比較によるタイミング差の回避)。
    /// 長さ不一致は即 false(トークン長は固定のため長さの漏洩は許容)。
    /// </summary>
    public bool Matches(string? provided)
    {
        if (string.IsNullOrEmpty(provided)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Value), Encoding.UTF8.GetBytes(provided));
    }
}
