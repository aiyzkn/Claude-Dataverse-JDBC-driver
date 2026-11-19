using System.Data.Common;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Gateway.Flight;

/// <summary>
/// DbDataReader → Arrow RecordBatch 変換。
/// 現状: decimal(Money 含む)は double、Guid/EntityReference/Choice は文字列。
/// ロスレス型マッピング(F-13)は 2.1.4 で拡張する。
/// </summary>
public static class ArrowConvert
{
    public static Schema SchemaFromReader(DbDataReader reader)
    {
        var builder = new Schema.Builder();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            // 別名なし集計(例: SELECT COUNT(*))は列名が null/空になりうる(特に FetchXML 経路)。
            // Arrow の Field は null 名で例外になるため合成名を割り当てる(3.2.1 macOS/FetchXML 対策)。
            var name = reader.GetName(i);
            if (string.IsNullOrEmpty(name)) name = $"Column{i + 1}";
            builder.Field(new Field(name, MapType(reader.GetFieldType(i)), nullable: true));
        }
        return builder.Build();
    }

    public static Schema RowsAffectedSchema()
        => new Schema.Builder().Field(new Field("rows_affected", Int64Type.Default, nullable: false)).Build();

    public static RecordBatch RowsAffectedBatch(long rowsAffected)
    {
        var array = new Int64Array.Builder().Append(rowsAffected).Build();
        return new RecordBatch(RowsAffectedSchema(), new IArrowArray[] { array }, 1);
    }

    private static IArrowType MapType(Type? t)
    {
        if (t == typeof(string)) return StringType.Default;
        if (t == typeof(bool)) return BooleanType.Default;
        if (t == typeof(short) || t == typeof(byte) || t == typeof(sbyte)) return Int16Type.Default;
        if (t == typeof(int)) return Int32Type.Default;
        if (t == typeof(long)) return Int64Type.Default;
        if (t == typeof(float)) return FloatType.Default;
        if (t == typeof(double) || t == typeof(decimal)) return DoubleType.Default;
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return new TimestampType(TimeUnit.Microsecond, "UTC");
        if (t == typeof(byte[])) return BinaryType.Default;
        return StringType.Default;
    }

    public static IEnumerable<RecordBatch> ReadBatches(DbDataReader reader, Schema schema, int batchSize)
    {
        var anyRows = false;
        while (true)
        {
            var writers = schema.FieldsList.Select(f => MakeColumnWriter(f.DataType)).ToArray();
            var rows = 0;
            while (rows < batchSize && reader.Read())
            {
                for (var i = 0; i < writers.Length; i++)
                {
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    writers[i].Append(value);
                }
                rows++;
            }

            if (rows == 0)
            {
                if (!anyRows)
                    yield return new RecordBatch(schema, writers.Select(w => w.Build()).ToArray(), 0);
                yield break;
            }

            anyRows = true;
            yield return new RecordBatch(schema, writers.Select(w => w.Build()).ToArray(), rows);
            if (rows < batchSize) yield break;
        }
    }

    private sealed class ColumnWriter
    {
        public required Action<object?> Append { get; init; }
        public required Func<IArrowArray> Build { get; init; }
    }

    private static ColumnWriter MakeColumnWriter(IArrowType type)
    {
        switch (type.TypeId)
        {
            case ArrowTypeId.Boolean:
            {
                var b = new BooleanArray.Builder();
                return new ColumnWriter { Append = v => { if (v is null) b.AppendNull(); else b.Append(Convert.ToBoolean(v)); }, Build = () => b.Build() };
            }
            case ArrowTypeId.Int16:
            {
                var b = new Int16Array.Builder();
                return new ColumnWriter { Append = v => { if (v is null) b.AppendNull(); else b.Append(Convert.ToInt16(v)); }, Build = () => b.Build() };
            }
            case ArrowTypeId.Int32:
            {
                var b = new Int32Array.Builder();
                return new ColumnWriter { Append = v => { if (v is null) b.AppendNull(); else b.Append(Convert.ToInt32(v)); }, Build = () => b.Build() };
            }
            case ArrowTypeId.Int64:
            {
                var b = new Int64Array.Builder();
                return new ColumnWriter { Append = v => { if (v is null) b.AppendNull(); else b.Append(Convert.ToInt64(v)); }, Build = () => b.Build() };
            }
            case ArrowTypeId.Float:
            {
                var b = new FloatArray.Builder();
                return new ColumnWriter { Append = v => { if (v is null) b.AppendNull(); else b.Append(Convert.ToSingle(v)); }, Build = () => b.Build() };
            }
            case ArrowTypeId.Double:
            {
                var b = new DoubleArray.Builder();
                return new ColumnWriter { Append = v => { if (v is null) b.AppendNull(); else b.Append(Convert.ToDouble(v)); }, Build = () => b.Build() };
            }
            case ArrowTypeId.Timestamp:
            {
                var b = new TimestampArray.Builder((TimestampType)type);
                return new ColumnWriter
                {
                    Append = v =>
                    {
                        if (v is null) { b.AppendNull(); return; }
                        var dto = v switch
                        {
                            DateTimeOffset o => o,
                            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
                            _ => new DateTimeOffset(Convert.ToDateTime(v), TimeSpan.Zero),
                        };
                        b.Append(dto);
                    },
                    Build = () => b.Build(),
                };
            }
            case ArrowTypeId.Binary:
            {
                var b = new BinaryArray.Builder();
                return new ColumnWriter { Append = v => { if (v is null) b.AppendNull(); else b.Append((ReadOnlySpan<byte>)(byte[])v); }, Build = () => b.Build() };
            }
            default:
            {
                var b = new StringArray.Builder();
                return new ColumnWriter { Append = v => { if (v is null) b.AppendNull(); else b.Append(ToStringValue(v)); }, Build = () => b.Build() };
            }
        }
    }

    private static string ToStringValue(object value) => value switch
    {
        Guid g => g.ToString(),
        _ => value.ToString() ?? string.Empty,
    };
}
