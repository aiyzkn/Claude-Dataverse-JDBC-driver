using Apache.Arrow;
using Apache.Arrow.Types;

namespace Gateway.Flight;

/// <summary>
/// Flight SQL メタデータコマンド(GetCatalogs/GetDbSchemas/GetTableTypes/GetTables)の
/// 結果スキーマとバッチを組み立てる(最小実装)。DBeaver ナビゲーター構築用。
/// 全エンティティ列挙・フィルタ・カタログ階層の本実装は F-13(2.1.4 以降)。
/// </summary>
public static class MetadataResults
{
    public const string SchemaName = "dbo";

    public static readonly Schema CatalogsSchema = new Schema.Builder()
        .Field(new Field("catalog_name", StringType.Default, true)).Build();

    public static readonly Schema DbSchemasSchema = new Schema.Builder()
        .Field(new Field("catalog_name", StringType.Default, true))
        .Field(new Field("db_schema_name", StringType.Default, false)).Build();

    public static readonly Schema TableTypesSchema = new Schema.Builder()
        .Field(new Field("table_type", StringType.Default, false)).Build();

    public static Schema TablesSchema(bool includeSchema)
    {
        var b = new Schema.Builder()
            .Field(new Field("catalog_name", StringType.Default, true))
            .Field(new Field("db_schema_name", StringType.Default, true))
            .Field(new Field("table_name", StringType.Default, false))
            .Field(new Field("table_type", StringType.Default, false));
        if (includeSchema)
            b.Field(new Field("table_schema", BinaryType.Default, false));
        return b.Build();
    }

    public static RecordBatch Catalogs()
        => new(CatalogsSchema, new IArrowArray[] { new StringArray.Builder().Build() }, 0);

    public static RecordBatch DbSchemas()
    {
        var catalog = new StringArray.Builder().AppendNull().Build();
        var schema = new StringArray.Builder().Append(SchemaName).Build();
        return new RecordBatch(DbSchemasSchema, new IArrowArray[] { catalog, schema }, 1);
    }

    public static RecordBatch TableTypes()
    {
        var t = new StringArray.Builder().Append("TABLE").Build();
        return new RecordBatch(TableTypesSchema, new IArrowArray[] { t }, 1);
    }

    public sealed record TableRow(string Name, string Type, byte[]? SchemaBytes);

    public static RecordBatch Tables(IReadOnlyList<TableRow> rows, bool includeSchema)
    {
        var catalog = new StringArray.Builder();
        var dbSchema = new StringArray.Builder();
        var name = new StringArray.Builder();
        var type = new StringArray.Builder();
        var tableSchema = new BinaryArray.Builder();

        foreach (var r in rows)
        {
            catalog.AppendNull();
            dbSchema.Append(SchemaName);
            name.Append(r.Name);
            type.Append(r.Type);
            if (includeSchema)
            {
                if (r.SchemaBytes is null) tableSchema.AppendNull();
                else tableSchema.Append((ReadOnlySpan<byte>)r.SchemaBytes);
            }
        }

        var arrays = includeSchema
            ? new IArrowArray[] { catalog.Build(), dbSchema.Build(), name.Build(), type.Build(), tableSchema.Build() }
            : new IArrowArray[] { catalog.Build(), dbSchema.Build(), name.Build(), type.Build() };
        return new RecordBatch(TablesSchema(includeSchema), arrays, rows.Count);
    }
}
