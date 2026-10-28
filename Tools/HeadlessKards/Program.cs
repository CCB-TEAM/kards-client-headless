using System.Collections.Concurrent;
using System.Text.Json;
using HeadlessKards;

try
{
    var backend=Environment.GetEnvironmentVariable("HEADLESS_RNG_BACKEND") switch
    {
        null or "managed"=>RandomBackend.Managed,"rust"=>RandomBackend.Rust,
        _=>throw new RuleException("HEADLESS_RNG_BACKEND must be managed or rust")
    };
    var rulesBackend=Environment.GetEnvironmentVariable("HEADLESS_RULES_BACKEND") switch
    {
        null or "rust"=>RulesBackend.Rust,"managed"=>RulesBackend.Managed,
        _=>throw new RuleException("HEADLESS_RULES_BACKEND must be rust or managed")
    };
    var definitions=Json.Read<Definition[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cards.json")));
    if(Environment.GetEnvironmentVariable("HEADLESS_CARD_PACK") is string packPath)
    {
        using var packDoc=JsonDocument.Parse(File.ReadAllText(packPath));
        var validated=RustCore.ValidatePack(packDoc.RootElement);
        definitions=Json.Read<Definition[]>(validated.GetProperty("pack").GetProperty("cards").GetRawText());
    }
    var engine = new Engine(definitions,backend,rulesBackend);
    switch (args.FirstOrDefault() ?? "help")
    {
        case "pack-validate":
            using(var packFile=JsonDocument.Parse(File.ReadAllText(args[1])))
            {
                var result=RustCore.ValidatePack(packFile.RootElement);
                if(args.Length>2) File.WriteAllText(args[2],result.GetProperty("pack").GetRawText()+Environment.NewLine);
                Console.WriteLine(Json.Write(new {hash=result.GetProperty("hash").GetString(),cards=result.GetProperty("pack").GetProperty("cards").GetArrayLength()}));
            }
            break;
        case "script-test":
            ScriptTest.Run(args[1],args[2]);
            break;
        case "aggro-test":
            AggroTest.Run(args[1],args.Length>2?int.Parse(args[2]):25);
            break;
        case "rules-parity-test":
            RulesParityTest.Run(definitions);
            break;
        case "bot":
            var botOptions=args.Length>1 ? Json.Read<BotOptions>(File.ReadAllText(args[1])) : new BotOptions();
            using(var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(botOptions.TimeoutSeconds)))
            using(var client=new HttpClient(new HttpClientHandler { AllowAutoRedirect=false }))
            {
                ConsoleCancelEventHandler cancel=(_,e)=> { e.Cancel=true; cancellation.Cancel(); };
                Console.CancelKeyPress+=cancel;
                try { await new FyNetwork(engine,botOptions,client).Run(cancellation.Token); }
                catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { Console.WriteLine("机器人已停止（取消或超时）。"); }
                finally { Console.CancelKeyPress-=cancel; }
            }
            break;
        case "selftest":
            SelfTest.Run(engine);
            break;
        case "client-rng-test":
            ClientRandomTest.Run(args[1]);
            break;
        case "protocol-test":
            ProtocolTest.Run(engine);
            break;
        case "rng-trace":
            var rngTrace=RandomTrace.Run(Json.Read<RandomTracePlan>(File.ReadAllText(args[1])),backend);
            if(args.Length>2) File.WriteAllText(args[2],Json.Write(rngTrace));
            else Console.WriteLine(Json.Write(rngTrace));
            break;
        case "wire-selfplay":
            var wireReplay = ProtocolTest.SelfPlay(engine,args.Length > 1 ? int.Parse(args[1]) : 42);
            var checkpoint = ProtocolTest.Replay(engine,wireReplay);
            if (args.Length > 2) File.WriteAllText(args[2],Json.Write(wireReplay));
            Console.WriteLine(Json.Write(new { packets=wireReplay.Packets.Count, checkpoint.State.Outcome, checkpoint.StateHash }));
            break;
        case "wire-replay":
            var wireResult=ProtocolTest.Replay(engine,Json.Read<WireReplay>(File.ReadAllText(args[1])));
            Console.WriteLine(Json.Write(new { verified=true, wireResult.LastActionId, wireResult.State.Outcome, wireResult.StateHash }));
            break;
        case "wire-fixture":
            var fixture = Json.Write(ProtocolFixtures.FromDemo(engine,args.Length > 2 ? int.Parse(args[2]) : 42));
            if (args.Length > 1) File.WriteAllText(args[1],fixture);
            else Console.WriteLine(fixture);
            break;
        case "compare-snapshot":
            var trace=Json.Read<WireReplay>(File.ReadAllText(args[1]));
            var expected=ProtocolTest.Replay(engine,trace);
            using (var snapshot=JsonDocument.Parse(File.ReadAllText(args[2])))
            {
                var comparison=NativeSnapshot.Compare(engine,expected,snapshot.RootElement);
                Console.WriteLine(Json.Write(comparison));
                if (!comparison.Equal) Environment.ExitCode=2;
            }
            break;
        case "selfplay":
            var seed = args.Length > 1 ? int.Parse(args[1]) : 1;
            var replay = engine.SelfPlay(new(seed), 4000);
            var final = engine.Verify(replay);
            if (args.Length > 2) File.WriteAllText(args[2], Json.Write(replay));
            Console.WriteLine(Json.Write(new { seed, actions = replay.Actions.Count, final.Turn, final.Outcome, replay.FinalHash }));
            break;
        case "selfplay-config":
            var configuredSetup=Json.Read<CreateRequest>(File.ReadAllText(args[1]));
            var configuredReplay=engine.SelfPlay(configuredSetup,4000);
            var configuredFinal=engine.Verify(configuredReplay);
            if(args.Length>2) File.WriteAllText(args[2],Json.Write(configuredReplay));
            Console.WriteLine(Json.Write(new {configuredSetup.Seed,actions=configuredReplay.Actions.Count,configuredFinal.Turn,configuredFinal.Outcome,configuredReplay.FinalHash,rules_backend=engine.RulesBackend.ToString()}));
            break;
        case "replay":
            var verified = engine.Verify(Json.Read<Replay>(File.ReadAllText(args[1])));
            Console.WriteLine(Json.Write(new { verified = true, verified.Revision, verified.Turn, verified.Outcome, hash = Engine.StateHash(verified) }));
            break;
        case "serve":
            var port = args.Length > 1 ? int.Parse(args[1]) : 1146;
            if (port is < 1024 or > 65535) throw new RuleException("Use an unprivileged local port");
            var builder = WebApplication.CreateBuilder([]);
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
            builder.Services.ConfigureHttpJsonOptions(o =>
            {
                o.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
                o.SerializerOptions.UnmappedMemberHandling = Json.Options.UnmappedMemberHandling;
                foreach (var converter in Json.Options.Converters) o.SerializerOptions.Converters.Add(converter);
            });
            var app = builder.Build();
            var sessions = new ConcurrentDictionary<Guid, Session>();
            var native = new ConcurrentDictionary<Guid, NativeSession>();
            app.Use(async (context, next) =>
            {
                try { await next(context); }
                catch (RuleException e) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
                catch (JsonException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "Invalid JSON" }); }
            });
            app.MapGet("/health", () => new { status = "ok", rules = Engine.RulesVersion, engine.CatalogHash, rules_backend=engine.RulesBackend.ToString(),rng_profile=LogicRandom.Profile,rng_backend=engine.RulesBackend==RulesBackend.Rust ? "Rust core" : backend.ToString(),native_client_protocol = false, native_packet_adapter = true });
            app.MapGet("/v1/cards", () => engine.Catalog.Values);
            app.MapPost("/v1/matches", (CreateRequest setup) =>
            {
                if (sessions.Count >= 128) return Results.Problem("Delete unused test sessions", statusCode: 429);
                var session = new Session(setup, engine.New(setup));
                var id = Guid.NewGuid();
                sessions[id] = session;
                return Results.Ok(new { id, state = session.State });
            });
            app.MapGet("/v1/matches/{id:guid}", (Guid id) =>
            {
                if (!sessions.TryGetValue(id, out var session)) return Results.NotFound();
                lock (session.Gate) return Results.Text(Json.Write(session.State), "application/json");
            });
            app.MapDelete("/v1/matches/{id:guid}", (Guid id) => sessions.TryRemove(id, out _) ? Results.Ok() : Results.NotFound());
            app.MapGet("/v1/matches/{id:guid}/legal", (Guid id) =>
            {
                if (!sessions.TryGetValue(id, out var session)) return Results.NotFound();
                lock (session.Gate) return Results.Ok(new { session.State.Revision, actions = engine.Legal(session.State) });
            });
            app.MapGet("/v1/matches/{id:guid}/think", (Guid id) =>
            {
                if (!sessions.TryGetValue(id, out var session)) return Results.NotFound();
                lock (session.Gate) return Results.Ok(new { session.State.Revision, action = engine.Think(session.State) });
            });
            app.MapPost("/v1/matches/{id:guid}/actions", (Guid id, ApplyRequest request) =>
            {
                if (!sessions.TryGetValue(id, out var session)) return Results.NotFound();
                lock (session.Gate)
                {
                    if (request.Command is null) throw new RuleException("Command is required");
                    session.State = engine.Apply(session.State, request.Command, request.ExpectedRevision);
                    var hash = Engine.StateHash(session.State);
                    session.Actions.Add(new(session.State.Revision, request.Command, hash));
                    return Results.Ok(new { state = session.State, hash });
                }
            });
            app.MapGet("/v1/matches/{id:guid}/replay", (Guid id) =>
            {
                if (!sessions.TryGetValue(id, out var session)) return Results.NotFound();
                lock (session.Gate) return Results.Text(Json.Write(new Replay(session.Setup, engine.CatalogHash, [.. session.Actions], Engine.StateHash(session.State),LogicRandom.Profile)), "application/json");
            });
            app.MapPost("/v1/native/matches", (FyBootstrap bootstrap) =>
            {
                if (native.Count >= 128) return Results.Problem("Delete unused protocol sessions", statusCode:429);
                FyProtocol protocol;
                try { protocol = new FyProtocol(engine,bootstrap); }
                catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or ArgumentException)
                { throw new RuleException("Invalid native starting_data"); }
                var id = Guid.NewGuid();
                native[id] = new NativeSession(bootstrap,protocol);
                return Results.Ok(new { id, checkpoint=protocol.Checkpoint() });
            });
            app.MapGet("/v1/native/matches/{id:guid}", (Guid id) =>
            {
                if (!native.TryGetValue(id,out var session)) return Results.NotFound();
                lock (session.Gate) return Results.Ok(session.Protocol.Checkpoint());
            });
            app.MapGet("/v1/native/matches/{id:guid}/think", (Guid id) =>
            {
                if (!native.TryGetValue(id,out var session)) return Results.NotFound();
                lock (session.Gate)
                {
                    if (session.Diverged) throw new RuleException("Snapshot diverged; start a fresh protocol session");
                    var action = session.Protocol.Think(session.Protocol.LastActionId+1);
                    return Results.Ok(new { action, a=action is null ? null : FyCodec.Encode(Json.Write(action),action.SendActionId) });
                }
            });
            app.MapPost("/v1/native/matches/{id:guid}/actions", (Guid id, PacketRequest request) =>
            {
                if (!native.TryGetValue(id,out var session)) return Results.NotFound();
                if (string.IsNullOrEmpty(request.A)) throw new RuleException("Packet a is required");
                lock (session.Gate)
                {
                    if (session.Diverged) throw new RuleException("Snapshot diverged; start a fresh protocol session");
                    var prior = session.Protocol.LastActionId;
                    var result = session.Protocol.AcceptPacket(request.A);
                    if (result.LastActionId != prior) session.Packets.Add(request.A);
                    return Results.Ok(result);
                }
            });
            app.MapPost("/v1/native/matches/{id:guid}/compare", (Guid id, JsonElement snapshot) =>
            {
                if (!native.TryGetValue(id,out var session)) return Results.NotFound();
                lock (session.Gate)
                {
                    SnapshotReport comparison;
                    try { comparison=NativeSnapshot.Compare(engine,session.Protocol.Checkpoint(),snapshot); }
                    catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException)
                    { throw new RuleException("Invalid native snapshot wrapper"); }
                    if (!comparison.Equal) session.Diverged=true;
                    return Results.Ok(comparison);
                }
            });
            app.MapGet("/v1/native/matches/{id:guid}/replay", (Guid id) =>
            {
                if (!native.TryGetValue(id,out var session)) return Results.NotFound();
                lock (session.Gate) return Results.Text(Json.Write(new WireReplay(session.Bootstrap,[.. session.Packets],session.Protocol.Checkpoint().StateHash,LogicRandom.Profile)),"application/json");
            });
            app.MapDelete("/v1/native/matches/{id:guid}", (Guid id) => native.TryRemove(id,out _) ? Results.Ok() : Results.NotFound());
            await app.RunAsync();
            break;
        default:
            Console.WriteLine("HeadlessKards subset prototype\n  selftest\n  protocol-test\n  rng-trace <plan.json> [trace.json]\n  selfplay [seed] [replay.json]\n  replay <replay.json>\n  wire-selfplay [seed] [wire-replay.json]\n  wire-replay <wire-replay.json>\n  wire-fixture [bootstrap.json] [seed]\n  compare-snapshot <wire-replay.json> <snapshot.json>\n  serve [port=1146]\nHEADLESS_RNG_BACKEND=managed|rust; internal HTTP API, live client integration unverified.");
            break;
    }
}
catch (Exception e) when (e is RuleException or JsonException or FormatException or IOException or IndexOutOfRangeException or OverflowException or KeyNotFoundException or HttpRequestException or TaskCanceledException)
{
    Console.Error.WriteLine(Json.Write(new { error = e.Message }));
    Environment.ExitCode = 1;
}

sealed record ApplyRequest(int ExpectedRevision, Command? Command);
sealed record PacketRequest(string A);
sealed class NativeSession(FyBootstrap bootstrap, FyProtocol protocol)
{
    public object Gate { get; } = new();
    public FyBootstrap Bootstrap { get; } = bootstrap;
    public FyProtocol Protocol { get; } = protocol;
    public List<string> Packets { get; } = [];
    public bool Diverged { get; set; }
}
sealed class Session(CreateRequest setup, State state)
{
    public object Gate { get; } = new();
    public CreateRequest Setup { get; } = setup;
    public State State { get; set; } = state;
    public List<Transition> Actions { get; } = [];
}
