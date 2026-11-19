# syntax=docker/dockerfile:1
# Dataverse GW — Linux コンテナ (4.2.5)
#
# ビルド(コンテキスト = リポジトリルート):
#   docker build -f dist/Dockerfile -t dataverse-gw:1.0.0 .
# 実行(認証情報は環境変数。コミット/共有厳禁。永続データは /data ボリューム):
#   docker run --rm -p 32010:32010 \
#     -e DATAVERSE_URL=https://<org>.crm7.dynamics.com/ \
#     -e DATAVERSE_AUTH=ClientSecret \
#     -e DATAVERSE_TENANT_ID=<tenant> -e DATAVERSE_CLIENT_ID=<client> -e DATAVERSE_CLIENT_SECRET=<secret> \
#     -v dvgw-data:/data \
#     dataverse-gw:1.0.0
#
# 注意(Linux コンテナの制約): 認証は ClientSecret / Certificate のみ(対話型/デバイスコードは不可)。
# TLS は Gateway__Tls__CertPath(PFX ファイル)で指定する(Thumbprint=Windows 証明書ストアは Linux 非対応)。

# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# csproj を先にコピーして restore をレイヤーキャッシュ
COPY src/Gateway.Core/Gateway.Core.csproj             src/Gateway.Core/
COPY src/Gateway.Checkpoint/Gateway.Checkpoint.csproj src/Gateway.Checkpoint/
COPY src/Gateway.Dataverse/Gateway.Dataverse.csproj   src/Gateway.Dataverse/
COPY src/Gateway.Flight/Gateway.Flight.csproj         src/Gateway.Flight/
COPY src/Gateway.Host/Gateway.Host.csproj             src/Gateway.Host/
RUN dotnet restore src/Gateway.Host/Gateway.Host.csproj
# ソースをコピーして publish(フレームワーク依存。ランタイムは aspnet イメージが提供)
COPY src/ src/
RUN dotnet publish src/Gateway.Host/Gateway.Host.csproj -c Release -o /app --no-restore

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# 永続データ(チェックポイント F-11 / 監査 / ドライラン / 失敗行レポート / プロセストークン)は
# .NET の LocalApplicationData 配下に集約される。非 root ユーザーでは $HOME からの解決が不確実で
# 空に落ちる(→ CWD 相対で /app と衝突)ため、.NET が最優先で参照する XDG_DATA_HOME を明示する。
# これで LocalApplicationData=/data となり、/data/DataverseGW 配下へ集約される。
ENV HOME=/data
ENV XDG_DATA_HOME=/data
RUN mkdir -p /data && chown $APP_UID /data
VOLUME /data

# 既定でネットワーク公開(localhost バインドはコンテナ外から届かないため any=0.0.0.0)。
# 本番はネットワーク公開に TLS 必須: Gateway__Tls__CertPath(＋CertPassword)を指定すること。
ENV Gateway__Bind=any
ENV Gateway__Port=32010
EXPOSE 32010

# 非 root 実行(.NET 8 イメージ提供の app ユーザー)
USER $APP_UID
ENTRYPOINT ["dotnet", "DataverseGW.dll"]
