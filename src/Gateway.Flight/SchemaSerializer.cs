using Apache.Arrow;
using Apache.Arrow.Ipc;
using Google.Protobuf;

namespace Gateway.Flight;

/// <summary>
/// Arrow Schema を Flight SQL の parameter_schema / dataset_schema 用 IPC バイト列へ変換する。
/// Java の MessageSerializer.deserializeSchema は先頭 1 メッセージを読むため、
/// ArrowStreamWriter が書くスキーマ＋EOS で相互運用できる。
/// </summary>
public static class SchemaSerializer
{
    public static ByteString Serialize(Schema schema)
    {
        using var ms = new MemoryStream();
        using (var writer = new ArrowStreamWriter(ms, schema, leaveOpen: true))
        {
            writer.WriteStart();
            writer.WriteEnd();
        }
        return ByteString.CopyFrom(ms.ToArray());
    }
}
