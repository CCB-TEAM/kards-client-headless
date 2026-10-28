using HeadlessKards;
using fyserver.Services;
using System.Text.Json;
using fyserver.Models;

var codec = new CodecService();
int checks = 0;
for (int i=0;i<1000;i++)
{
    int id = i % 3 == 0 ? 0xFFFFFF : i;
    var message = Json.Write(new { action_type="PC", send_action_id=id, message="异想体 / PE-BOX", padding=new string('x',i%250) });
    var fromUpstream = codec.Encode(message,id);
    var decoded = FyCodec.Decode(fromUpstream);
    if (decoded.Text != message || decoded.SendActionId != id) throw new Exception("Decode upstream mismatch");
    var local = FyCodec.Encode(message,id);
    var text = codec.Decode(local,out var upstreamId);
    if (text != message || upstreamId != id) throw new Exception("Upstream decode mismatch");
    checks += 2;
}
var engine = new Engine(Json.Read<Definition[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"cards.json"))));
var replay = ProtocolTest.SelfPlay(engine,42);
var session = new FyProtocol(engine,replay.Bootstrap);
foreach (var packet in replay.Packets)
{
    var plaintext = codec.Decode(packet,out var id);
    var serverAction = JsonSerializer.Deserialize<MatchAction>(plaintext,fyserver.Serialization.FyJsonContext.Default.MatchAction)
        ?? throw new Exception("Cannot deserialize real server DTO");
    if (serverAction.SendActionId != id) throw new Exception("Real server model id mismatch");
    var serverPacket = codec.Encode(serverAction);
    session.AcceptPacket(serverPacket);
    checks++;
}
if (session.Checkpoint().StateHash!=replay.FinalHash) throw new Exception("Real DTO/codec replay diverged");
Console.WriteLine(Json.Write(new { passed=checks, upstream="CCB-TEAM/fyserver@4dd0a0891f56b54e3bd783ee8f22d9acd757ef12", codec_source_linked=true, actual_match_dto_linked=true, native_client_tested=false }));
