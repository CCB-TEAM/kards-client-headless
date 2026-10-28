using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HeadlessKards;

public sealed record BotOptions(string Server = "http://127.0.0.1:1145", string Username = "headless_lcorp",
    string BattleCode = "LCORP", int? DeckId = null, bool FirstTurnDraws = true,
    int PollMilliseconds = 500, int ActionMilliseconds = 900, int TimeoutSeconds = 1800,
    string OutputDirectory = "artifacts/live",string? DeckCode=null,string MainFaction="Germany",string AllyFaction="Soviet");

// This is a regular player, not the server's built-in pw opponent.
// Send once, then consume the server echo. Never retry an ambiguous POST.
public sealed class FyNetwork(Engine engine, BotOptions options, HttpClient http)
{
    public const string TestDeckCode = "%%14|bdbdbdbdbdbdbdbdbdbdbdbdbcbcbcbcbcbcbcbcbcbcbcbcbhbhbhbhbhbhbhbhbhbh9p9p9p9p9p;;;~;;;|3v8v";
    public int PlayerId { get; private set; }
    private int cursor, sendId;
    private readonly List<string> packets = [];
    private readonly Dictionary<int,string> seen = [];
    private readonly JsonSerializerOptions wire = new(Json.Options) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip };
    private string directory = "";
    private void Log(string message)
    {
        var line=$"[{DateTimeOffset.Now:HH:mm:ss}] {message}";
        Console.WriteLine(line);
        if(directory.Length!=0) File.AppendAllText(Path.Combine(directory,"bot.log"),line+Environment.NewLine);
    }
    private async Task<string> Request(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if(body is not null) req.Content = new StringContent(Json.Write(body),Encoding.UTF8,"application/json");
        using var response = await http.SendAsync(req,ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if(!response.IsSuccessStatusCode) throw new RuleException($"fyserver {method} {path}: HTTP {(int)response.StatusCode}");
        return text;
    }
    private async Task<JsonElement> Call(HttpMethod method,string path,object? body,CancellationToken ct)
    { using var doc=JsonDocument.Parse(await Request(method,path,body,ct)); return doc.RootElement.Clone(); }
    public async Task Run(CancellationToken ct)
    {
        var uri = new Uri(options.Server.TrimEnd('/')+"/");
        if(!uri.IsLoopback || uri.Scheme!="http") throw new RuleException("This test bot requires a loopback http server");
        if(options.PollMilliseconds<100 || options.ActionMilliseconds<0 || options.TimeoutSeconds<1 || string.IsNullOrWhiteSpace(options.Username)
            || string.IsNullOrWhiteSpace(options.BattleCode)) throw new RuleException("Invalid bot settings");
        http.BaseAddress=uri; http.Timeout=TimeSpan.FromSeconds(20);
        directory=Path.Combine(Path.GetFullPath(options.OutputDirectory),DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);
        var session=await Call(HttpMethod.Post,"session",new { provider="email",provider_details=new {payment_provider=""},client_type="headless",
            build="subset-v0.2-rng1",platform_type="Windows",app_guid="headless-kards",version="1",platform_info="headless",platform_version="1",
            account_linking="",language="en",automatic_account_creation=true,username=options.Username,password="" },ct);
        PlayerId=session.GetProperty("player_id").GetInt32();
        http.DefaultRequestHeaders.Authorization=new("JWT",FyCodec.Encode(options.Username,0));
        int deckId;
        var deckCode=options.DeckCode??TestDeckCode;
        if(options.DeckId is int configured) deckId=configured;
        else
        {
            // Reuse the bot's own named deck instead of creating one each run.
            var headers=session.GetProperty("decks").GetProperty("headers");
            var existing=headers.EnumerateArray().FirstOrDefault(d=>d.GetProperty("name").GetString()=="Headless test" && d.GetProperty("deck_code").GetString()==deckCode);
            if(existing.ValueKind==JsonValueKind.Object) deckId=existing.GetProperty("id").GetInt32();
            else deckId=(await Call(HttpMethod.Post,$"players/{PlayerId}/decks",new {name="Headless test",main_faction=options.MainFaction,ally_faction=options.AllyFaction,deck_code=deckCode},ct)).GetProperty("id").GetInt32();
        }
        Log($"已登录机器人 {PlayerId}；对战码 {options.BattleCode}；日志 {directory}");
        bool queued=false;
        try
        {
            await Request(HttpMethod.Post,"lobbyplayers",new {player_id=PlayerId,deck_id=deckId,extra_data="battle_code:"+options.BattleCode},ct); queued=true;
            Log("等待同一对战码的玩家，请使用说明中的测试卡组。");
            JsonElement info;
            while(true)
            {
                info=await Call(HttpMethod.Get,"matches/v2",null,ct);
                if(info.ValueKind==JsonValueKind.Object) break;
                await Task.Delay(options.PollMilliseconds,ct);
            }
            queued=false;
            await Play(info,ct);
        }
        finally
        {
            if(queued)
            {
                using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await Request(HttpMethod.Delete,$"lobbyplayers/leave?playerId={PlayerId}",null,cleanup.Token); } catch(Exception e) when(e is HttpRequestException or OperationCanceledException or RuleException) { Log("退出队列未确认"); }
            }
        }
    }
    private async Task Play(JsonElement info,CancellationToken ct)
    {
        var root=JsonNode.Parse(info.GetRawText())!.AsObject();
        if(root["local_subactions"]?.GetValue<bool>()!=true) throw new RuleException("Require local_subactions=true");
        var data=root["match_and_starting_data"]!.AsObject();
        var start=data["starting_data"]!.AsObject();
        int left=start["player_id_left"]!.GetValue<int>(),right=start["player_id_right"]!.GetValue<int>();
        var side=PlayerId==left?Side.Left:PlayerId==right?Side.Right:throw new RuleException("Bot not in match");
        int opponent=side==Side.Left?right:left;
        int id=data["match"]!["match_id"]!.GetValue<int>();
        // The native client stamps every submission with the match id as send_action_id.
        sendId=id;
        string match=$"matches/v2/{id}", actions=match+"/actions";
        await File.WriteAllTextAsync(Path.Combine(directory,"starting-info.json"),info.GetRawText(),ct);
        // Preflight both decks before announcing ready or playing anything.
        var unsupported=new HashSet<string>();
        foreach(var name in new[] {"location_card_left","location_card_right","starting_hand_left","starting_hand_right","deck_left","deck_right"})
        {
            var node=start[name]!;
            foreach(var card in node is JsonArray array ? array.ToArray() : new JsonNode?[] { node })
            {
                string key=card!["name"]!.GetValue<string>();
                if(!engine.Catalog.TryGetValue(key,out var definition) || !FyProtocol.HasNativeCode(definition)) unsupported.Add(key);
            }
        }
        if(unsupported.Count!=0) throw new RuleException("Both-deck preflight failed; unsupported cards: "+string.Join(", ",unsupported.Order()));
        _=new FyProtocol(engine,new(info,true,options.FirstTurnDraws));
        await Request(HttpMethod.Put,match+"/",new {action="lvl-loaded"},ct);
        string mine=side==Side.Left?"left":"right";
        var discards=ChooseMulligan(start["starting_hand_"+mine]!.AsArray());
        Log($"换牌：{(discards.Length==0?"保留全部手牌":"丢弃卡牌ID "+string.Join(",",discards))}");
        var own=await Call(HttpMethod.Post,match+"/mulligan",new {discarded_card_ids=discards},ct);
        ApplyMulligan(start,mine,own);
        string other=side==Side.Left?"right":"left";
        while(true)
        {
            var result=await Call(HttpMethod.Get,match+"/mulligan/"+other,null,ct);
            if(result.ValueKind==JsonValueKind.Object) { ApplyMulligan(start,other,result); break; }
            await Task.Delay(options.PollMilliseconds,ct);
        }
        var bootstrap=new FyBootstrap(JsonSerializer.SerializeToElement(root),true,options.FirstTurnDraws);
        await File.WriteAllTextAsync(Path.Combine(directory,"bootstrap.json"),Json.Write(bootstrap),ct);
        var protocol=new FyProtocol(engine,bootstrap);
        // Native matches open directly with the first player's XActionStartOfTurn as action 1;
        // the client does not know a separate start-of-game marker.
        FyAction? pending=null;
        DateTimeOffset pendingSince=default;
        Log($"对局 {id}，机器人为 {side}，调度完成。");
        try
        {
            while(true)
            {
                var poll=await Call(HttpMethod.Put,actions,new {min_action_id=cursor,opponent_id=opponent,time_since_opponent_ping=0},ct);
                if(poll.TryGetProperty("actions",out var batch))
                foreach(var packet in batch.EnumerateArray())
                {
                    string encoded=packet.GetString()!;
                    var decoded=FyCodec.Decode(encoded);
                    var action=JsonSerializer.Deserialize<FyAction>(decoded.Text,wire) ?? throw new RuleException("Invalid action");
                    if(action.SendActionId!=decoded.SendActionId) throw new RuleException("Packet salt/id mismatch");
                    string canonical=Json.Write(action);
                    if(action.ActionId<=cursor)
                    { if(!seen.TryGetValue(action.ActionId,out var old)||old!=canonical) throw new RuleException("Conflicting server echo"); continue; }
                    if(action.ActionId!=cursor+1) throw new RuleException("Server action gap");
                    if(action.PlayerId!=left && action.PlayerId!=right) throw new RuleException("Unknown action player");
                    await File.AppendAllTextAsync(Path.Combine(directory,"actions.jsonl"),canonical+Environment.NewLine,ct);
                    if(action.ActionType=="ActionEndMatch")
                    {
                        Log("服务器已结束对局："+action.ActionData.GetValueOrDefault("winner_side"));
                        await Request(HttpMethod.Get,match+"/post",null,ct); return;
                    }
                    else
                    {
                        if(pending is not null && action.PlayerId==PlayerId && !SameSubmission(pending,action)) throw new RuleException("Own action echo differs from submitted action");
                        protocol.Accept(action); packets.Add(encoded);
                        if(pending is not null && action.PlayerId==PlayerId) pending=null;
                        Log($"动作 {action.ActionId}: {action.ActionType}；回合 {protocol.State.Turn}");
                    }
                    cursor=action.ActionId; seen.Add(cursor,canonical);
                }
                if(poll.TryGetProperty("match",out var status) && status.TryGetProperty("status",out var state) && state.GetString()=="finished")
                { Log("服务器状态 finished"); await Request(HttpMethod.Get,match+"/post",null,ct); return; }
                if(pending is not null)
                {
                    if(DateTimeOffset.UtcNow-pendingSince>TimeSpan.FromSeconds(20)) throw new RuleException("Action echo timed out; stopped without retrying ambiguous POST");
                }
                else if(protocol.State.Outcome is not null)
                {
                    // The ordinary client can finish from either participant's report.
                    await Send(actions,new FyAction(0,"XActionEndMatch",PlayerId,new(),[],protocol.State.Turn,sendId,
                        Value:new() { ["winner_side"]=protocol.State.Outcome,["result"]="Victory_DestroyHQ" }),ct);
                    Log("已提交胜负，等待服务器确认。");
                    // Block another submission until a finished poll or terminal action.
                    pending=new FyAction(0,"XActionEndMatch",PlayerId,new(),[],0); pendingSince=DateTimeOffset.UtcNow;
                }
                else if(protocol.State.Active==side)
                {
                    await Task.Delay(options.ActionMilliseconds,ct);
                    pending=protocol.Think(sendId) ?? throw new RuleException("No bot action");
                    pendingSince=DateTimeOffset.UtcNow;
                    await Send(actions,pending,ct);
                }
                await Task.Delay(options.PollMilliseconds,ct);
            }
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory,"checkpoint.json"),Json.Write(protocol.Checkpoint()));
            await File.WriteAllTextAsync(Path.Combine(directory,"wire-replay.json"),Json.Write(new WireReplay(bootstrap,packets,protocol.Checkpoint().StateHash,ProtocolTest.RandomProfile(protocol.State))));
        }
    }
    private Task<string> Send(string actions,FyAction action,CancellationToken ct) => Request(HttpMethod.Post,actions,new {a=FyCodec.Encode(Json.Write(action),action.SendActionId)},ct);
    private static bool SameSubmission(FyAction sent,FyAction echoed) => sent.SendActionId==echoed.SendActionId && sent.ActionType==echoed.ActionType
        && sent.PlayerId==echoed.PlayerId && sent.Action==echoed.Action && sent.SubActions.Length==echoed.SubActions.Length
        && sent.ActionData.Count==echoed.ActionData.Count && sent.ActionData.All(p=>echoed.ActionData.GetValueOrDefault(p.Key)==p.Value);
    // Aggro opening: throw back anything that cannot be played by turn 3.
    // Always keep at least two cards so a bad draw cannot empty the hand.
    private int[] ChooseMulligan(JsonArray hand)
    {
        var scored=hand.Select(c=>(Id:c!["card_id"]!.GetValue<int>(),Cost:engine.Catalog.TryGetValue(c["name"]!.GetValue<string>(),out var d)?d.Cost:99))
            .OrderByDescending(c=>c.Cost).ToList();
        return scored.Where(c=>c.Cost>=4).Take(Math.Max(0,scored.Count-2)).Select(c=>c.Id).ToArray();
    }
    private static void ApplyMulligan(JsonObject start,string side,JsonElement result)
    {
        start["deck_"+side]=JsonNode.Parse(result.GetProperty("deck").GetRawText());
        var hand=start["starting_hand_"+side]!.AsArray();
        foreach(var replacement in result.GetProperty("replacement_cards").EnumerateArray())
        {
            int position=replacement.GetProperty("location_number").GetInt32();
            int at=Enumerable.Range(0,hand.Count).Single(i=>hand[i]!["location_number"]!.GetValue<int>()==position);
            hand[at]=JsonNode.Parse(replacement.GetRawText());
        }
    }
}
