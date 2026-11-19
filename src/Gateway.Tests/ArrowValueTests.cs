using Apache.Arrow;
using Gateway.Dataverse;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xunit;

namespace Gateway.Tests;

/// <summary>ArrowValue.ToAttributeValue の型ロスレス変換仕様(2.1.4/F-13)。</summary>
public class ArrowValueTests
{
    private static StringArray Str(string s) => new StringArray.Builder().Append(s).Build();
    private static Int64Array I64(long v) => new Int64Array.Builder().Append(v).Build();
    private static Int32Array I32(int v) => new Int32Array.Builder().Append(v).Build();
    private static DoubleArray Dbl(double v) => new DoubleArray.Builder().Append(v).Build();

    private static AttributeInfo Info(AttributeTypeCode tc, string? target = null, DateTimeBehavior? dtb = null)
        => new("col", tc, target is null ? System.Array.Empty<string>() : new[] { target }, dtb);

    private static AttributeInfo PolyInfo(AttributeTypeCode tc, params string[] targets)
        => new("col", tc, targets, null);

    [Fact]
    public void Lookup_becomes_EntityReference()
    {
        var g = Guid.NewGuid();
        var v = ArrowValue.ToAttributeValue(Str(g.ToString()), 0, Info(AttributeTypeCode.Lookup, "account"));
        var er = Assert.IsType<EntityReference>(v);
        Assert.Equal("account", er.LogicalName);
        Assert.Equal(g, er.Id);
    }

    [Fact]
    public void Polymorphic_lookup_requires_type_hint()
    {
        var g = Guid.NewGuid();
        // 複数ターゲットで型ヒント無し(GUID のみ)は解決不能 → 例外
        Assert.Throws<InvalidOperationException>(() =>
            ArrowValue.ToAttributeValue(Str(g.ToString()), 0, PolyInfo(AttributeTypeCode.Owner, "systemuser", "team")));
    }

    [Fact]
    public void Polymorphic_lookup_resolved_via_entity_guid_hint()
    {
        var g = Guid.NewGuid();
        var v = ArrowValue.ToAttributeValue(Str($"systemuser:{g}"), 0, PolyInfo(AttributeTypeCode.Owner, "systemuser", "team"));
        var er = Assert.IsType<EntityReference>(v);
        Assert.Equal("systemuser", er.LogicalName);
        Assert.Equal(g, er.Id);
    }

    [Fact]
    public void Type_hint_outside_targets_is_rejected()
    {
        var g = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() =>
            ArrowValue.ToAttributeValue(Str($"contact:{g}"), 0, PolyInfo(AttributeTypeCode.Owner, "systemuser", "team")));
    }

    [Fact]
    public void Alternate_key_lookup_single_target()
    {
        // keyName=keyValue 形式(単一ターゲット)→ 代替キー参照(F-06)
        var v = ArrowValue.ToAttributeValue(Str("poc_externalid=EXT-001"), 0, Info(AttributeTypeCode.Lookup, "poc_widget"));
        var er = Assert.IsType<EntityReference>(v);
        Assert.Equal("poc_widget", er.LogicalName);
        Assert.Equal(Guid.Empty, er.Id);
        Assert.True(er.KeyAttributes.ContainsKey("poc_externalid"));
        Assert.Equal("EXT-001", er.KeyAttributes["poc_externalid"]);
    }

    [Fact]
    public void Alternate_key_lookup_with_entity_hint()
    {
        var v = ArrowValue.ToAttributeValue(Str("account:accountnumber=ACC-9"), 0,
            PolyInfo(AttributeTypeCode.Customer, "account", "contact"));
        var er = Assert.IsType<EntityReference>(v);
        Assert.Equal("account", er.LogicalName);
        Assert.Equal("ACC-9", er.KeyAttributes["accountnumber"]);
    }

    [Fact]
    public void PartyList_becomes_activityparty_collection()
    {
        var g1 = Guid.NewGuid(); var g2 = Guid.NewGuid();
        var v = ArrowValue.ToAttributeValue(Str($"systemuser:{g1},contact:{g2}"), 0, Info(AttributeTypeCode.PartyList));
        var ec = Assert.IsType<Microsoft.Xrm.Sdk.EntityCollection>(v);
        Assert.Equal(2, ec.Entities.Count);
        Assert.Equal("activityparty", ec.Entities[0].LogicalName);
        var p0 = (EntityReference)ec.Entities[0]["partyid"];
        Assert.Equal("systemuser", p0.LogicalName);
        Assert.Equal(g1, p0.Id);
    }

    [Fact]
    public void Picklist_becomes_OptionSetValue()
    {
        var v = ArrowValue.ToAttributeValue(I32(3), 0, Info(AttributeTypeCode.Picklist));
        Assert.Equal(3, Assert.IsType<OptionSetValue>(v).Value);
    }

    [Fact]
    public void Money_becomes_Money_preserving_decimals()
    {
        var v = ArrowValue.ToAttributeValue(Dbl(1234.56), 0, Info(AttributeTypeCode.Money));
        Assert.Equal(1234.56m, Assert.IsType<Money>(v).Value);
    }

    [Fact]
    public void DateTime_userlocal_is_marked_utc()
    {
        var dtArr = new TimestampArray.Builder()
            .Append(new DateTimeOffset(2022, 3, 4, 5, 6, 7, TimeSpan.Zero)).Build();
        var v = ArrowValue.ToAttributeValue(dtArr, 0, Info(AttributeTypeCode.DateTime, dtb: DateTimeBehavior.UserLocal));
        var dt = Assert.IsType<DateTime>(v);
        Assert.Equal(DateTimeKind.Utc, dt.Kind);
    }

    [Fact]
    public void DateTime_dateonly_drops_time()
    {
        var dtArr = new TimestampArray.Builder()
            .Append(new DateTimeOffset(2022, 3, 4, 5, 6, 7, TimeSpan.Zero)).Build();
        var v = ArrowValue.ToAttributeValue(dtArr, 0, Info(AttributeTypeCode.DateTime, dtb: DateTimeBehavior.DateOnly));
        var dt = Assert.IsType<DateTime>(v);
        Assert.Equal(new DateTime(2022, 3, 4), dt);
        Assert.Equal(0, dt.TimeOfDay.Ticks);
    }

    [Fact]
    public void BigInt_from_int64_stays_long()
    {
        var v = ArrowValue.ToAttributeValue(I64(9_000_000_000L), 0, Info(AttributeTypeCode.BigInt));
        Assert.Equal(9_000_000_000L, Assert.IsType<long>(v));
    }

    [Fact]
    public void Integer_coerced_to_int32()
    {
        var v = ArrowValue.ToAttributeValue(I64(42), 0, Info(AttributeTypeCode.Integer));
        Assert.Equal(42, Assert.IsType<int>(v));
    }

    [Fact]
    public void String_passthrough()
    {
        var v = ArrowValue.ToAttributeValue(Str("hello"), 0, Info(AttributeTypeCode.String));
        Assert.Equal("hello", Assert.IsType<string>(v));
    }

    [Fact]
    public void Null_returns_null_regardless_of_type()
    {
        var arr = new StringArray.Builder().AppendNull().Build();
        Assert.Null(ArrowValue.ToAttributeValue(arr, 0, Info(AttributeTypeCode.Lookup, "account")));
    }

    [Fact]
    public void No_metadata_returns_raw_clr_value()
    {
        var v = ArrowValue.ToAttributeValue(I32(7), 0, info: null);
        Assert.Equal(7, v);
    }
}
