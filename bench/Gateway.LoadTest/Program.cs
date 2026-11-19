using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Client;
using Gateway.Flight.Protocol.Sql;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Net.Client;
using NBomber.CSharp;

// Dataverse GW 負荷試験(3.3.1)。稼働中ゲートウェイに Flight SQL の SELECT を
// 並列負荷でかけ、スループット/レイテンシを NBomber で計測・レポートする。
//
// 実行前提: ゲートウェイが起動していること(接続先 Dataverse が必要)。CI では実行しない
// (資格情報が無く、Dataverse のサービス保護で throttle 律速のため。CI は build+単体テストで回帰を担保)。
//
// 設定(環境変数):
//   LOAD_GW        接続先        (既定 http://localhost:32010)
//   LOAD_SQL       実行 SQL      (既定 SELECT TOP 10 accountid, name FROM account)
//   LOAD_RATE      毎秒リクエスト (既定 5)
//   LOAD_DURATION  継続秒        (既定 20)

// h2c(平文 HTTP/2)でゲートウェイへ接続するために必要
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var address = Environment.GetEnvironmentVariable("LOAD_GW") ?? "http://localhost:32010";
var sql = Environment.GetEnvironmentVariable("LOAD_SQL") ?? "SELECT TOP 10 accountid, name FROM account";
var rate = int.TryParse(Environment.GetEnvironmentVariable("LOAD_RATE"), out var r) ? r : 5;
var durationSec = int.TryParse(Environment.GetEnvironmentVariable("LOAD_DURATION"), out var d) ? d : 20;

Console.WriteLine($"LoadTest: {address} / rate={rate}rps / {durationSec}s / SQL={sql}");

using var channel = GrpcChannel.ForAddress(address);
var client = new FlightClient(channel);

// SELECT を 1 往復実行し、取得行数を返す(GetInfo → 各エンドポイントの DoGet を消費)
async Task<long> RunSelectAsync()
{
    var command = Any.Pack(new CommandStatementQuery { Query = sql }).ToByteArray();
    var descriptor = FlightDescriptor.CreateCommandDescriptor(command);
    var info = await client.GetInfo(descriptor);
    long rows = 0;
    foreach (var ep in info.Endpoints)
    {
        var stream = client.GetStream(ep.Ticket);
        while (await stream.ResponseStream.MoveNext(CancellationToken.None))
            rows += stream.ResponseStream.Current.Length;
    }
    return rows;
}

var scenario = Scenario.Create("flight_select", async _ =>
    {
        try
        {
            var rows = await RunSelectAsync();
            return Response.Ok(sizeBytes: (int)Math.Min(int.MaxValue, rows));
        }
        catch (Exception ex)
        {
            return Response.Fail(message: ex.Message);
        }
    })
    .WithoutWarmUp()
    .WithLoadSimulations(Simulation.Inject(rate: rate, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(durationSec)));

NBomberRunner
    .RegisterScenarios(scenario)
    .WithReportFileName($"loadtest_flight_select")
    .Run();
