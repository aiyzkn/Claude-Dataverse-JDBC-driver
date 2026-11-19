using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Query;

namespace Gateway.Dataverse;

/// <summary>
/// 名前→ID 自動解決(3.1.4 / R-5 対策)。対象エンティティのプライマリ名→ID を事前辞書化し、
/// Lookup 値がプレーンな名前で来た場合に ID へ解決する。既定 OFF(オプトイン必須)。
/// **曖昧一致(同名複数)は行エラー**、未一致も行エラー(誤参照を防ぐ=R-5)。
/// 辞書はエンティティ単位でキャッシュ(初回アクセスで全件取得)。
/// </summary>
public sealed class NameResolver
{
    /// <summary>名前1件の解決結果。Ambiguous なら同名が複数存在。</summary>
    public sealed record Entry(Guid Id, int Count)
    {
        public bool Ambiguous => Count > 1;
    }

    /// <summary>辞書のキャッシュ(構築時刻付き。TTL 失効判定用。4.1.3)。</summary>
    private sealed record Cached(DateTime BuiltUtc, IReadOnlyDictionary<string, Entry> Dict);

    private readonly DataverseConnection _connection;
    private readonly EntityMetadataCache _metadata;
    private readonly ILogger<NameResolver> _log;
    private readonly TimeSpan _ttl; // TimeSpan.Zero = 失効なし(プロセス生存中保持)
    private readonly ConcurrentDictionary<string, Lazy<Cached>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public NameResolver(DataverseConnection connection, EntityMetadataCache metadata, IConfiguration config, ILogger<NameResolver> log)
    {
        _connection = connection;
        _metadata = metadata;
        _log = log;
        // 事前辞書の TTL(分)。既定 0=失効なし。>0 で長時間セッションの陳腐化(改名/削除)に対応(4.1.3)。
        var mins = config.GetValue<double?>("Gateway:NameResolve:CacheTtlMinutes") ?? 0;
        _ttl = mins > 0 ? TimeSpan.FromMinutes(mins) : TimeSpan.Zero;
    }

    /// <summary>エンティティの名前辞書を取得(TTL 失効時は再構築)。</summary>
    private IReadOnlyDictionary<string, Entry> GetDict(string entity)
    {
        while (true)
        {
            var lazy = _cache.GetOrAdd(entity,
                e => new Lazy<Cached>(() => new Cached(DateTime.UtcNow, Build(e)), LazyThreadSafetyMode.ExecutionAndPublication));
            var cached = lazy.Value;
            if (_ttl == TimeSpan.Zero || DateTime.UtcNow - cached.BuiltUtc <= _ttl)
                return cached.Dict;
            // TTL 失効 → この lazy のみ除去して再構築(他スレッドが差し替えた新しい物は消さない)
            _cache.TryRemove(new KeyValuePair<string, Lazy<Cached>>(entity, lazy));
            _log.LogInformation("名前→ID辞書 {Entity} を TTL 失効で再構築", entity);
        }
    }

    /// <summary>名前辞書を明示的に無効化する(次アクセスで再構築)。null で全エンティティ。</summary>
    public void Invalidate(string? entity = null)
    {
        if (entity is null) { _cache.Clear(); _log.LogInformation("名前→ID辞書を全無効化"); }
        else { _cache.TryRemove(entity, out _); _log.LogInformation("名前→ID辞書 {Entity} を無効化", entity); }
    }

    /// <summary>名前を対象エンティティの ID に解決する。未一致・曖昧は例外(行エラーとして扱われる)。</summary>
    public Guid Resolve(string entity, string name)
    {
        var dict = GetDict(entity);

        var key = name.Trim();
        if (dict.TryGetValue(key, out var entry))
        {
            if (entry.Ambiguous)
                throw new InvalidOperationException(
                    $"名前→ID解決: 名前 '{name}' は {entity} 内で {entry.Count} 件に一致し曖昧です。GUID か代替キーで一意に指定してください。");
            return entry.Id;
        }

        // キャッシュミス: 事前辞書化の後に作成された可能性がある。対象名で直接照会して鮮度を確保する
        // (辞書は一括処理の性能用に維持しつつ、ミス時のみ 1 回のクエリで最新を確認)。
        var fresh = ResolveDirect(entity, key);
        if (fresh is null)
            throw new InvalidOperationException(
                $"名前→ID解決: 名前 '{name}' に一致する {entity} が見つかりません。GUID か代替キーで指定してください。");
        if (fresh.Value.Ambiguous)
            throw new InvalidOperationException(
                $"名前→ID解決: 名前 '{name}' は {entity} 内で複数に一致し曖昧です。GUID か代替キーで一意に指定してください。");
        return fresh.Value.Id;
    }

    /// <summary>対象名を直接照会する(辞書ミス時のフォールバック)。TopCount=2 で曖昧を検出。</summary>
    private (Guid Id, bool Ambiguous)? ResolveDirect(string entity, string name)
    {
        var (idAttr, nameAttr) = _metadata.PrimaryAttributes(entity);
        if (nameAttr is null) return null;
        var q = new QueryExpression(entity)
        {
            ColumnSet = new ColumnSet(idAttr),
            TopCount = 2,
            Criteria = new FilterExpression { Conditions = { new ConditionExpression(nameAttr, ConditionOperator.Equal, name) } },
        };
        var rows = _connection.Client.RetrieveMultiple(q).Entities;
        if (rows.Count == 0) return null;
        return (rows[0].Id, rows.Count > 1);
    }

    private IReadOnlyDictionary<string, Entry> Build(string entity)
    {
        var (idAttr, nameAttr) = _metadata.PrimaryAttributes(entity);
        if (nameAttr is null)
            throw new InvalidOperationException(
                $"名前→ID解決: {entity} にはプライマリ名属性がありません。GUID か代替キーで指定してください。");

        var rows = new List<(Guid Id, string? Name)>();
        var query = new QueryExpression(entity)
        {
            ColumnSet = new ColumnSet(idAttr, nameAttr),
            PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 },
        };
        while (true)
        {
            var page = _connection.Client.RetrieveMultiple(query);
            foreach (var e in page.Entities)
                rows.Add((e.Id, e.GetAttributeValue<string>(nameAttr)));
            if (!page.MoreRecords) break;
            query.PageInfo.PageNumber++;
            query.PageInfo.PagingCookie = page.PagingCookie;
        }

        var map = BuildMap(rows);
        var ambiguous = map.Values.Count(v => v.Ambiguous);
        _log.LogInformation("名前→ID辞書: {Entity} 名前 {Count} 種(曖昧 {Amb} 種)を事前辞書化(属性 {Attr})",
            entity, map.Count, ambiguous, nameAttr);
        return map;
    }

    /// <summary>(id, name) 群から 名前→Entry 辞書を構築する(純粋関数。曖昧=同名複数を Count で表現)。</summary>
    public static IReadOnlyDictionary<string, Entry> BuildMap(IEnumerable<(Guid Id, string? Name)> rows)
    {
        var map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in rows)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var key = name.Trim();
            map[key] = map.TryGetValue(key, out var cur)
                ? cur with { Count = cur.Count + 1 } // 同名 → 曖昧(Id は先勝ちだが Ambiguous 判定で弾く)
                : new Entry(id, 1);
        }
        return map;
    }
}
