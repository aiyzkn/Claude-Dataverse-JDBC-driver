using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Gateway.Dataverse;

/// <summary>
/// MSAL トークンキャッシュを OS ネイティブ保護で永続化する
/// (Windows: DPAPI / macOS: Keychain / Linux: libsecret)。要件定義書 §6 セキュリティ。
/// </summary>
internal static class TokenCachePersistence
{
    public static void Enable(ITokenCache cache, string directory, string cacheName)
    {
        Directory.CreateDirectory(directory);
        // OS ネイティブ保護でキャッシュを永続化する。ヘルパーが実行 OS に応じて選択:
        //   Windows=DPAPI(既定) / macOS=Keychain(WithMacKeyChain) / Linux=libsecret(WithLinuxKeyring)。
        // 各設定は該当 OS でのみ効果を持ち、他 OS では無視される(3.2.1 macOS 対応)。
        var props = new StorageCreationPropertiesBuilder($"{cacheName}.bin", directory)
            .WithMacKeyChain(serviceName: "DataverseGW", accountName: cacheName)
            .WithLinuxKeyring(
                schemaName: "dataversegw.tokencache",
                collection: MsalCacheHelper.LinuxKeyRingDefaultCollection,
                secretLabel: "Dataverse GW token cache",
                attribute1: new KeyValuePair<string, string>("Product", "DataverseGW"),
                attribute2: new KeyValuePair<string, string>("Component", "TokenCache"))
            .Build();

        // 同期的に helper を用意してキャッシュに登録
        var helper = MsalCacheHelper.CreateAsync(props).GetAwaiter().GetResult();
        helper.RegisterCache(cache);
    }
}
