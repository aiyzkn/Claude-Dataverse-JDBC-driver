using System.ServiceModel;
using Gateway.Dataverse;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Gateway.Tests;

/// <summary>失敗行レポートのエラー要約マスキング(M-2: PII 平文残置の防止)。</summary>
public class ErrorSummaryTests
{
    private const string Pii = "email=taro.yamada@example.com";

    [Fact]
    public void 既定はメッセージ全文を出さない()
    {
        var ex = new InvalidOperationException($"重複です {Pii}");
        var summary = ErrorSummary.Safe(ex, includeMessage: false);
        Assert.DoesNotContain(Pii, summary);
        Assert.Equal("InvalidOperationException", summary); // 例外型のみ
    }

    [Fact]
    public void オプトイン時はメッセージ全文を含む()
    {
        var ex = new InvalidOperationException($"重複です {Pii}");
        var summary = ErrorSummary.Safe(ex, includeMessage: true);
        Assert.Contains(Pii, summary);
    }

    [Fact]
    public void Dataverseフォルトは既定でエラーコードのみ_PIIを出さない()
    {
        var fault = new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040237), Message = $"duplicate {Pii}" };
        var ex = new FaultException<OrganizationServiceFault>(fault, "duplicate " + Pii);
        var summary = ErrorSummary.Safe(ex, includeMessage: false);
        Assert.StartsWith("0x", summary);
        Assert.DoesNotContain(Pii, summary);
        Assert.DoesNotContain("duplicate", summary);
    }

    [Fact]
    public void 改行はスペースに畳まれCSV行を壊さない()
    {
        var ex = new InvalidOperationException("line1\nline2\r\nline3");
        var summary = ErrorSummary.Safe(ex, includeMessage: true);
        Assert.DoesNotContain('\n', summary);
        Assert.DoesNotContain('\r', summary);
    }
}
