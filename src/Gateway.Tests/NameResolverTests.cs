using Gateway.Dataverse;
using Xunit;

namespace Gateway.Tests;

/// <summary>名前→ID 自動解決(3.1.4): 辞書構築・曖昧検出・オプトイン。</summary>
public class NameResolverTests
{
    private static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    [Fact]
    public void BuildMap_unique_names()
    {
        var map = NameResolver.BuildMap(new[] { (G(1), (string?)"Contoso"), (G(2), "Fabrikam") });
        Assert.False(map["Contoso"].Ambiguous);
        Assert.Equal(G(1), map["Contoso"].Id);
        Assert.Equal(G(2), map["Fabrikam"].Id);
    }

    [Fact]
    public void BuildMap_duplicate_name_is_ambiguous()
    {
        var map = NameResolver.BuildMap(new[] { (G(1), (string?)"Acme"), (G(2), "Acme"), (G(3), "Acme") });
        Assert.True(map["Acme"].Ambiguous);
        Assert.Equal(3, map["Acme"].Count);
    }

    [Fact]
    public void BuildMap_skips_null_or_blank_names()
    {
        var map = NameResolver.BuildMap(new[] { (G(1), (string?)null), (G(2), "  "), (G(3), "Ok") });
        Assert.Single(map);
        Assert.True(map.ContainsKey("Ok"));
    }

    [Fact]
    public void BuildMap_lookup_is_case_insensitive()
    {
        var map = NameResolver.BuildMap(new[] { (G(1), (string?)"Contoso") });
        Assert.True(map.ContainsKey("contoso"));
        Assert.True(map.ContainsKey("CONTOSO"));
    }

    [Fact]
    public void Resolve_names_hint_is_parsed()
    {
        var o = PreparedInsertExecutor.ParseHints("INSERT /*+ RESOLVE_NAMES */ INTO t (a) VALUES (?)");
        Assert.True(o.ResolveNames);
        Assert.False(PreparedInsertExecutor.ParseHints("INSERT INTO t (a) VALUES (?)").ResolveNames);
    }
}
