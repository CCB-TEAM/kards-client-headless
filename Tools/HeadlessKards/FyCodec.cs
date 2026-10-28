using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HeadlessKards;

// Wire algorithm checked against CCB-TEAM/fyserver CodecService.cs, commit
// 4dd0a0891f56b54e3bd783ee8f22d9acd757ef12. This is obfuscation, not authentication.
public static class FyCodec
{
    private static readonly int[] Lengths = [
        47,53,73,55,61,103,47,103,33,45,73,37,97,71,39,71,31,61,83,101,
        53,97,79,75,37,31,33,69,43,63,39,43,79,55,49,73,83,67,59,69,
        103,39,47,37,41,71,89,55,49,45,33,45,69,49,43,53,59,31,59,101,
        61,41,79,75,83,89,75,67,41,89,63,101,67,63,97];
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static string Encode(string text, int sendActionId)
    {
        var data = Utf8.GetBytes(text);
        if (data.Length > 999999 || sendActionId is < 0 or > 0xFFFFFF) throw new RuleException("Codec payload/id out of range");
        int index = RandomNumberGenerator.GetInt32(Lengths.Length);
        var key = new byte[Lengths[index]];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        byte[] id = [(byte)((sendActionId >> 16) ^ key[0]), (byte)((sendActionId >> 8) ^ key[1]), (byte)(sendActionId ^ key[2])];
        for (int i = 0; i < data.Length; i++) data[i] ^= key[i % key.Length];
        return index.ToString("D2", CultureInfo.InvariantCulture) + data.Length.ToString("D6", CultureInfo.InvariantCulture)
            + Convert.ToBase64String(id) + Encoding.ASCII.GetString(key) + Convert.ToBase64String(data);
    }
    public static (string Text, int SendActionId) Decode(string encoded)
    {
        if (encoded.Length < 12 || encoded.Length > 1_333_448 || !encoded.AsSpan(0, 8).ToString().All(char.IsAsciiDigit)) throw new RuleException("Invalid codec header");
        int index = int.Parse(encoded.AsSpan(0, 2), CultureInfo.InvariantCulture);
        int length = int.Parse(encoded.AsSpan(2, 6), CultureInfo.InvariantCulture);
        if (index >= Lengths.Length) throw new RuleException("Invalid codec table index");
        int keyLength = Lengths[index];
        if (encoded.Length != 12 + keyLength + 4 * ((length + 2) / 3)) throw new RuleException("Codec length mismatch");
        var keyText = encoded.Substring(12, keyLength);
        if (keyText.Any(c => !Alphabet.Contains(c))) throw new RuleException("Invalid codec key");
        try
        {
            var id = Convert.FromBase64String(encoded.Substring(8, 4));
            var cipher = Convert.FromBase64String(encoded[(12 + keyLength)..]);
            if (id.Length != 3 || cipher.Length != length) throw new RuleException("Codec decoded length mismatch");
            var key = Encoding.ASCII.GetBytes(keyText);
            for (int i = 0; i < cipher.Length; i++) cipher[i] ^= key[i % keyLength];
            return (Utf8.GetString(cipher), ((id[0] ^ key[0]) << 16) | ((id[1] ^ key[1]) << 8) | (id[2] ^ key[2]));
        }
        catch (Exception e) when (e is FormatException or DecoderFallbackException) { throw new RuleException("Invalid codec payload"); }
    }
}
