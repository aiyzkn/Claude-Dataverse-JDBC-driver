using MarkMpn.Sql4Cds.Engine;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace Gateway.Dataverse;

/// <summary>
/// 確立済み ServiceClient を保持し、SQL 実行用の Sql4CdsConnection と
/// 並列実行用のクローンを払い出す(製品版。PoC の DataverseConnectionFactory を再設計)。
/// </summary>
public sealed class DataverseConnection : IDisposable
{
    public DataverseConnection(ServiceClient client) => Client = client;

    /// <summary>バルク/EM メッセージ発行用の主接続。</summary>
    public ServiceClient Client { get; }

    /// <summary>
    /// 読み取りに TDS エンドポイントを使うか(F-15/D-16)。
    /// null=Sql4Cds 自動判定(TDS 有効かつ表現可能なら TDS、不可なら FetchXML)。
    /// false=常に FetchXML(TDS 無効環境での機能継続)。true=TDS 強制。
    /// </summary>
    public bool? UseTdsEndpoint { get; set; }

    /// <summary>
    /// スケールアウト用の追加アプリケーションユーザー(SP)接続(4.2.4)。null/空なら単一 SP。
    /// 各 SP は Dataverse のサービス保護制限(throttle)予算を個別に持つため、書き込みバッチを
    /// SP 間で分散すると集約スループットが向上する。主接続の追加ピアとしてここに保持する。
    /// </summary>
    public IReadOnlyList<DataverseConnection>? ScaleOutPeers { get; set; }

    /// <summary>書き込み分散に用いる接続プール = [主接続, ...ScaleOutPeers]。常に 1 件以上。</summary>
    public IReadOnlyList<DataverseConnection> ScaleOutPool
        => ScaleOutPeers is { Count: > 0 } peers ? new[] { this }.Concat(peers).ToList() : new[] { this };

    /// <summary>並列実行用のクローン(単一インスタンスは同期 Execute が直列化するため。OBS-7)。</summary>
    public ServiceClient Clone() => Client.Clone();

    /// <summary>SQL(SELECT/DML)実行用の Sql4Cds 接続。呼び出しごとに生成する。</summary>
    public Sql4CdsConnection CreateSqlConnection()
    {
        var con = new Sql4CdsConnection(Client);
        if (UseTdsEndpoint.HasValue) con.UseTDSEndpoint = UseTdsEndpoint.Value;
        return con;
    }

    public void Dispose()
    {
        Client.Dispose();
        if (ScaleOutPeers is not null)
            foreach (var peer in ScaleOutPeers) peer.Dispose();
    }
}
