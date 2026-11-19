using Gateway.Core;
using Gateway.Dataverse;
using Xunit;

namespace Gateway.Tests;

/// <summary>secret を持つ record の ToString() 秘匿(M-5)。</summary>
public class SecretRedactionTests
{
    [Fact]
    public void ConnParams_ToString_はClientSecretを含まない()
    {
        var p = new DataverseContextRegistry.ConnParams(
            "https://x.crm.dynamics.com", AuthKind.ClientSecret, "tid", "cid", "SUPER_SECRET_VALUE", null);
        var s = p.ToString();
        Assert.DoesNotContain("SUPER_SECRET_VALUE", s);
        Assert.Contains("***", s);
        Assert.Contains("cid", s); // 非秘密の識別情報は残す(調査可能性)
    }

    [Fact]
    public void ConnParams_ToString_secretなしは_none表示()
    {
        var p = new DataverseContextRegistry.ConnParams(
            "https://x.crm.dynamics.com", AuthKind.Certificate, "tid", "cid", null, "THUMB");
        Assert.Contains("(none)", p.ToString());
    }

    [Fact]
    public void PrincipalConfig_ToString_はClientSecretを含まない()
    {
        var p = new ScaleOutPool.PrincipalConfig(AuthKind.ClientSecret, "tid", "cid", "SECRET_XYZ", null);
        var s = p.ToString();
        Assert.DoesNotContain("SECRET_XYZ", s);
        Assert.Contains("***", s);
    }

    [Fact]
    public void ConnParams_補間で文字列化してもsecretが載らない()
    {
        var p = new DataverseContextRegistry.ConnParams(
            "u", AuthKind.ClientSecret, "t", "c", "LEAK_ME", null);
        var interpolated = $"context={p}"; // 誤って record 全体を文字列化するケース
        Assert.DoesNotContain("LEAK_ME", interpolated);
    }
}
