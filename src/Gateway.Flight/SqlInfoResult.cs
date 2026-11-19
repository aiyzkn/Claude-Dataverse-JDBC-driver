using Apache.Arrow;
using Apache.Arrow.Types;

namespace Gateway.Flight;

/// <summary>
/// Flight SQL の CommandGetSqlInfo 応答(最小実装)。JDBC ドライバーは接続時に
/// GetSqlInfo を呼び、エラーだと接続が中断するため、仕様どおりのスキーマで 0 行を返す。
/// 完全な SqlInfo 応答は Phase 1 後続(F-13)。
/// </summary>
public static class SqlInfoResult
{
    public static Schema Schema { get; } = BuildSchema();

    private static Schema BuildSchema()
    {
        var valueUnion = new UnionType(
            new[]
            {
                new Field("string_value", StringType.Default, true),
                new Field("bool_value", BooleanType.Default, true),
                new Field("bigint_value", Int64Type.Default, true),
                new Field("int32_bitmask", Int32Type.Default, true),
                new Field("string_list", new ListType(new Field("item", StringType.Default, true)), true),
                new Field("int32_to_int32_list_map",
                    new ListType(new Field("entries",
                        new StructType(new[]
                        {
                            new Field("key", Int32Type.Default, false),
                            new Field("value", new ListType(new Field("item", Int32Type.Default, true)), true),
                        }), false)), true),
            },
            new[] { 0, 1, 2, 3, 4, 5 },
            UnionMode.Dense);

        return new Schema.Builder()
            .Field(new Field("info_name", UInt32Type.Default, false))
            .Field(new Field("value", valueUnion, true))
            .Build();
    }

    public static RecordBatch Empty()
    {
        var infoName = new UInt32Array.Builder().Build();
        var valueField = (Field)Schema.GetFieldByName("value");
        var unionType = (UnionType)valueField.DataType;
        var children = unionType.Fields.Select(BuildEmpty).ToArray();
        var unionData = new ArrayData(
            unionType, length: 0, nullCount: 0, offset: 0,
            buffers: new[] { ArrowBuffer.Empty, ArrowBuffer.Empty },
            children: children.Select(c => c.Data).ToArray());
        var valueArray = new DenseUnionArray(unionData);
        return new RecordBatch(Schema, new IArrowArray[] { infoName, valueArray }, 0);
    }

    private static IArrowArray BuildEmpty(Field field) => field.DataType.TypeId switch
    {
        ArrowTypeId.String => new StringArray.Builder().Build(),
        ArrowTypeId.Boolean => new BooleanArray.Builder().Build(),
        ArrowTypeId.Int64 => new Int64Array.Builder().Build(),
        ArrowTypeId.Int32 => new Int32Array.Builder().Build(),
        ArrowTypeId.List => EmptyList((ListType)field.DataType),
        _ => throw new NotSupportedException($"SqlInfo union member not handled: {field.DataType.TypeId}"),
    };

    private static IArrowArray EmptyList(ListType type)
    {
        var valueChild = BuildEmptyByType(type.ValueDataType);
        var offsets = new ArrowBuffer.Builder<int>().Append(0).Build();
        var data = new ArrayData(
            type, length: 0, nullCount: 0, offset: 0,
            buffers: new[] { ArrowBuffer.Empty, offsets },
            children: new[] { valueChild.Data });
        return new ListArray(data);
    }

    private static IArrowArray BuildEmptyByType(IArrowType type) => type.TypeId switch
    {
        ArrowTypeId.String => new StringArray.Builder().Build(),
        ArrowTypeId.Int32 => new Int32Array.Builder().Build(),
        ArrowTypeId.List => EmptyList((ListType)type),
        ArrowTypeId.Struct => EmptyStruct((StructType)type),
        _ => throw new NotSupportedException($"SqlInfo nested type not handled: {type.TypeId}"),
    };

    private static IArrowArray EmptyStruct(StructType type)
    {
        var children = type.Fields.Select(f => BuildEmptyByType(f.DataType)).ToArray();
        var data = new ArrayData(
            type, length: 0, nullCount: 0, offset: 0,
            buffers: new[] { ArrowBuffer.Empty },
            children: children.Select(c => c.Data).ToArray());
        return new StructArray(data);
    }
}
