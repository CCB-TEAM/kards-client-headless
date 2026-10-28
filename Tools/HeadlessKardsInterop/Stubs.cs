using System.Text.Json.Serialization;

// Supply only a source-generated serializer context for the real upstream
// MatchAction model; codec and DTO source files are compiled unchanged.
namespace fyserver.Serialization
{
    [JsonSourceGenerationOptions(PropertyNamingPolicy=JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(fyserver.Models.MatchAction))]
    [JsonSerializable(typeof(System.Text.Json.JsonElement))]
    internal partial class FyJsonContext : JsonSerializerContext;
    [JsonSerializable(typeof(string))]
    internal partial class ConfigJsonContext : JsonSerializerContext;
}
