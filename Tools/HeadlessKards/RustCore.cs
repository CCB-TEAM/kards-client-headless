using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace HeadlessKards;

public enum RulesBackend {Managed,Rust}
public sealed class RustCore(IReadOnlyCollection<Definition> cards,string catalogHash)
{
    public static string Fingerprint(IReadOnlyCollection<Definition> cards)=>Send(new {op="catalog",cards}).GetProperty("hash").GetString()!;
    public T Call<T>(string op,State? state=null,CreateRequest? setup=null,Command? command=null,int? revision=null,bool defer=false,bool draw=false,int? insertion=null)
    {
        var result=Send(new {op,cards,catalog_hash=catalogHash,state,setup,command,expected_revision=revision,defer_next_start=defer,draw,insertion});
        return Json.Read<T>(result.GetRawText());
    }
    public Command? Think(State state)
    {
        var result=Send(new {op="think",cards,catalog_hash=catalogHash,state});
        return result.ValueKind==JsonValueKind.Null ? null : Json.Read<Command>(result.GetRawText());
    }
    public static JsonElement ValidatePack(JsonElement pack)=>Send(new {op="validate_pack",pack});
    public static JsonElement Send<T>(T request)
    {
        IntPtr pointer=IntPtr.Zero;
        try
        {
            if(Abi()!=1) throw new RuleException("Rust core ABI mismatch");
            byte[] json=Encoding.UTF8.GetBytes(Json.Write(request));
            pointer=Request(json,(nuint)json.Length);
            if(pointer==IntPtr.Zero) throw new RuleException("Rust core rejected request buffer");
            string reply=Marshal.PtrToStringUTF8(pointer) ?? throw new RuleException("Empty Rust response");
            using var doc=JsonDocument.Parse(reply);
            if(!doc.RootElement.GetProperty("ok").GetBoolean()) throw new RuleException(doc.RootElement.GetProperty("error").GetString()??"Rust core error");
            return doc.RootElement.GetProperty("result").Clone();
        }
        catch(Exception e) when(e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { throw new RuleException("Rust rules backend unavailable; run Tools/HeadlessCore/Build.ps1 and keep headless_core.dll beside HeadlessKards.dll"); }
        finally {if(pointer!=IntPtr.Zero) Free(pointer);}
    }
    [DllImport("headless_core",EntryPoint="hk_core_abi",CallingConvention=CallingConvention.Cdecl)] private static extern uint Abi();
    [DllImport("headless_core",EntryPoint="hk_core_request",CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr Request(byte[] data,nuint length);
    [DllImport("headless_core",EntryPoint="hk_core_free",CallingConvention=CallingConvention.Cdecl)] private static extern void Free(IntPtr pointer);
}
