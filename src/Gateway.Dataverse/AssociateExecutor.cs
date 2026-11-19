using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Apache.Arrow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace Gateway.Dataverse;

/// <summary>
/// N:N 関連付け(D-13/F-07)。2 つの構文を同一の Associate/Disassociate に変換する:
///   ① INSERT INTO &lt;relationship&gt; (record1id, record2id) VALUES (?, ?)  — DBeaver 転送向け
///   ② ASSOCIATE / DISASSOCIATE &lt;relationship&gt; (?, ?)                     — 明示操作向け
/// リレーションシップ名からエンティティ両端を RetrieveRelationship で解決・キャッシュする。
/// </summary>
public sealed class AssociateExecutor
{
    private static readonly Regex NnInsertPattern = new(
        @"^\s*INSERT\s+INTO\s+(?<rel>\w+)\s*\(\s*(?<cols>[^)]*?)\s*\)\s*VALUES\s*\(\s*(?<vals>[^)]*?)\s*\)\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AssocPattern = new(
        @"^\s*(?<verb>ASSOCIATE|DISASSOCIATE)\s+(?<rel>\w+)\s*\(\s*(?<vals>[^)]*?)\s*\)\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public enum AssocVerb { Associate, Disassociate }

    public sealed record AssociateCommand(AssocVerb Verb, string Relationship);

    // ExecuteMultiple の 1 要求あたり上限
    private const int EmChunkSize = 1000;

    private readonly DataverseConnection _connection;
    private readonly ILogger<AssociateExecutor> _log;
    private readonly ProductionGuard _guard;
    private readonly int _dop;
    private readonly ConcurrentDictionary<string, ManyToManyRelationshipMetadata> _relCache = new(StringComparer.OrdinalIgnoreCase);

    public AssociateExecutor(DataverseConnection connection, IConfiguration config, ProductionGuard guard, ILogger<AssociateExecutor> log)
    {
        _connection = connection;
        _log = log;
        _guard = guard;
        // INSERT 経路と同様に dop 追従で並列化(OBS-PERF-2)。明示設定が無ければ推奨並列度。
        _dop = config.GetValue<int?>("Gateway:Dop") ?? Math.Max(1, connection.Client.RecommendedDegreesOfParallelism);
    }

    /// <summary>N:N 構文(INSERT 交差テーブル / ASSOCIATE / DISASSOCIATE)を解析。非該当なら null。</summary>
    public static AssociateCommand? TryParse(string sql)
    {
        var mi = NnInsertPattern.Match(sql);
        if (mi.Success)
        {
            var cols = mi.Groups["cols"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => c.Trim('[', ']', '"', '`', ' ').ToLowerInvariant()).ToList();
            var vals = mi.Groups["vals"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            // 交差テーブルの規約: 列が record1id, record2id かつ全て '?'
            if (cols.Count == 2 && cols.Contains("record1id") && cols.Contains("record2id")
                && vals.Count == 2 && vals.All(v => v == "?"))
                return new AssociateCommand(AssocVerb.Associate, mi.Groups["rel"].Value);
            return null; // 通常の INSERT は PreparedInsertExecutor が扱う
        }

        var ma = AssocPattern.Match(sql);
        if (ma.Success)
        {
            var vals = ma.Groups["vals"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (vals.Count != 2 || vals.Any(v => v != "?")) return null;
            var verb = ma.Groups["verb"].Value.ToUpperInvariant() == "DISASSOCIATE" ? AssocVerb.Disassociate : AssocVerb.Associate;
            return new AssociateCommand(verb, ma.Groups["rel"].Value);
        }
        return null;
    }

    /// <summary>関連付けの列(record1id/record2id または ? , ?)を返す。parameter_schema 用。</summary>
    public static IReadOnlyList<string> ParameterColumns => new[] { "record1id", "record2id" };

    public async Task<int> ExecuteBatchAsync(AssociateCommand cmd, RecordBatch batch, CancellationToken ct, bool allowProduction = false)
    {
        var rowCount = batch.Length;
        if (rowCount == 0) return 0;

        _guard.EnsureWriteAllowed(cmd.Verb.ToString(), cmd.Relationship, allowProduction); // 本番環境ガード(3.1.2/4.2.2b)
        var rel = ResolveRelationship(cmd.Relationship);
        var relationship = new Relationship(rel.SchemaName);

        // 全リクエストを構築(GUID 変換は Arrow から即時に行う)
        var all = new List<OrganizationRequest>(rowCount);
        for (var row = 0; row < rowCount; row++)
        {
            var id1 = ToGuid(ArrowValue.Get(batch.Column(0), row));
            var id2 = ToGuid(ArrowValue.Get(batch.Column(1), row));
            var target = new EntityReference(rel.Entity1LogicalName, id1);
            var related = new EntityReferenceCollection { new(rel.Entity2LogicalName, id2) };
            all.Add(cmd.Verb == AssocVerb.Associate
                ? new AssociateRequest { Target = target, Relationship = relationship, RelatedEntities = related }
                : new DisassociateRequest { Target = target, Relationship = relationship, RelatedEntities = related });
        }

        // ExecuteMultiple 上限(1000)ごとに分割
        var chunks = new List<List<OrganizationRequest>>();
        for (var i = 0; i < all.Count; i += EmChunkSize)
            chunks.Add(all.GetRange(i, Math.Min(EmChunkSize, all.Count - i)));

        // 1 チャンク = ExecuteMultiple 1 回。単一 ServiceClient は Execute を直列化するため
        // dop 並列時は Clone() したクライアントで実行する(OBS-PERF-2 / OBS-7)。
        void RunChunk(ServiceClient client, int index)
        {
            var requests = new OrganizationRequestCollection();
            foreach (var r in chunks[index]) requests.Add(r);
            var em = new ExecuteMultipleRequest
            {
                Requests = requests,
                Settings = new ExecuteMultipleSettings { ContinueOnError = false, ReturnResponses = false },
            };
            var resp = (ExecuteMultipleResponse)Resilience.Execute(() => client.Execute(em), _log);
            if (resp.IsFaulted)
            {
                var first = resp.Responses.FirstOrDefault(r => r.Fault is not null);
                var globalIndex = (long)index * EmChunkSize + (first?.RequestIndex ?? 0);
                throw new InvalidOperationException($"{cmd.Verb} 内でエラー(行 #{globalIndex}): {first?.Fault?.Message}");
            }
        }

        var workers = Math.Min(_dop, chunks.Count);
        if (workers <= 1)
        {
            for (var i = 0; i < chunks.Count; i++) RunChunk(_connection.Client, i);
        }
        else
        {
            var clients = new ServiceClient[workers];
            for (var i = 0; i < workers; i++) clients[i] = _connection.Clone();
            try
            {
                var tasks = new Task[workers];
                for (var w = 0; w < workers; w++)
                {
                    var worker = w;
                    tasks[worker] = Task.Run(() =>
                    {
                        for (var i = worker; i < chunks.Count; i += workers) RunChunk(clients[worker], i);
                    }, ct);
                }
                await Task.WhenAll(tasks);
            }
            finally
            {
                foreach (var c in clients) c.Dispose();
            }
        }

        _log.LogInformation("{Verb} {Rel}: {Count} 件 ({Chunks} チャンク × dop {Dop})",
            cmd.Verb, rel.SchemaName, rowCount, chunks.Count, workers);
        return rowCount;
    }

    private ManyToManyRelationshipMetadata ResolveRelationship(string name)
        => _relCache.GetOrAdd(name, n =>
        {
            var resp = (RetrieveRelationshipResponse)_connection.Client.Execute(new RetrieveRelationshipRequest { Name = n });
            if (resp.RelationshipMetadata is not ManyToManyRelationshipMetadata mm)
                throw new InvalidOperationException($"'{n}' は N:N リレーションシップではありません。");
            return mm;
        });

    private static Guid ToGuid(object? v) => v switch
    {
        Guid g => g,
        string s => Guid.Parse(s),
        _ => throw new InvalidOperationException($"GUID に変換できない値: {v}"),
    };
}
