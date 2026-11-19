using System.ServiceModel;
using Microsoft.Xrm.Sdk;

namespace Gateway.Dataverse;

/// <summary>
/// 失敗行レポート用の安全なエラー要約(M-2)。既定では Dataverse エラーコード/例外型のみを返し、
/// PII を含み得るメッセージ全文は出力しない。原因列は FailureReport 側で別列に保持される。
/// </summary>
public static class ErrorSummary
{
    /// <param name="includeMessage">true のときのみメッセージ全文を付加する(オプトイン。既定 false)。</param>
    public static string Safe(Exception ex, bool includeMessage)
    {
        var code = ex is FaultException<OrganizationServiceFault> { Detail: not null } fe
            ? $"0x{fe.Detail.ErrorCode:X8}"
            : ex.GetType().Name;
        // メッセージ全文は PII(違反フィールドの実値: メール・氏名・ID 等)を含み得るため既定では出さない。
        return includeMessage ? $"{code}: {ex.Message.ReplaceLineEndings(" ")}" : code;
    }
}
