using System.Globalization;
using System.Text;
namespace SuiteRedes.Json;

public enum JsonValueKind { Undefined, Object, Array, String, Number, True, False, Null }

/// <summary>Leitor JSON mínimo e sem dependências (substitui System.Text.Json no .NET Framework).</summary>
public readonly struct JsonElement
{
    readonly object? v; readonly bool has;
    internal JsonElement(object? value) { v = value; has = true; }
    public JsonValueKind ValueKind => !has ? JsonValueKind.Undefined : v switch
    {
        null => JsonValueKind.Null, Dictionary<string, JsonElement> => JsonValueKind.Object, List<JsonElement> => JsonValueKind.Array,
        string => JsonValueKind.String, bool b => b ? JsonValueKind.True : JsonValueKind.False, _ => JsonValueKind.Number
    };
    public bool TryGetProperty(string name, out JsonElement value)
    {
        if (v is Dictionary<string, JsonElement> d && d.TryGetValue(name, out value)) return true;
        value = default; return false;
    }
    public JsonElement GetProperty(string name) => TryGetProperty(name, out var x) ? x : throw new KeyNotFoundException(name);
    public IEnumerable<JsonElement> EnumerateArray() => v as List<JsonElement> ?? throw new InvalidOperationException("não é array");
    public IEnumerable<JsonProperty> EnumerateObject() => (v as Dictionary<string, JsonElement> ?? throw new InvalidOperationException("não é objeto")).Select(kv => new JsonProperty(kv.Key, kv.Value));
    public int GetArrayLength() => (v as List<JsonElement>)?.Count ?? throw new InvalidOperationException("não é array");
    public JsonElement this[int i] => ((List<JsonElement>)v!)[i];
    public string? GetString() => v as string;
    public bool TryGetInt32(out int n)
    {
        n = 0; if (v is double d && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue) { n = (int)d; return true; }
        return false;
    }
    public int GetInt32() => TryGetInt32(out var n) ? n : throw new FormatException("não é inteiro");
    public JsonElement Clone() => this;
    public override string ToString() => v switch
    {
        null => "", string s => s, bool b => b ? "True" : "False", double d => d.ToString(CultureInfo.InvariantCulture), _ => Writer.Write(v)
    };
}

public readonly struct JsonProperty { public string Name { get; } public JsonElement Value { get; } public JsonProperty(string n, JsonElement v) { Name = n; Value = v; } }

public sealed class JsonDocument : IDisposable
{
    public JsonElement RootElement { get; }
    JsonDocument(JsonElement r) { RootElement = r; }
    public static JsonDocument Parse(string json) { int i = 0; var r = Reader.Value(json, ref i); return new JsonDocument(r); }
    public void Dispose() { }
}

static class Reader
{
    static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
    public static JsonElement Value(string s, ref int i)
    {
        Ws(s, ref i); if (i >= s.Length) throw new FormatException("JSON incompleto");
        char c = s[i];
        if (c == '{')
        {
            var d = new Dictionary<string, JsonElement>(); i++; Ws(s, ref i);
            if (s[i] == '}') { i++; return new JsonElement(d); }
            while (true) { Ws(s, ref i); var k = Str(s, ref i); Ws(s, ref i); i++; /* : */ d[k] = Value(s, ref i); Ws(s, ref i); if (s[i] == ',') { i++; continue; } i++; break; }
            return new JsonElement(d);
        }
        if (c == '[')
        {
            var l = new List<JsonElement>(); i++; Ws(s, ref i);
            if (s[i] == ']') { i++; return new JsonElement(l); }
            while (true) { l.Add(Value(s, ref i)); Ws(s, ref i); if (s[i] == ',') { i++; continue; } i++; break; }
            return new JsonElement(l);
        }
        if (c == '"') return new JsonElement(Str(s, ref i));
        if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return new JsonElement(true); }
        if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return new JsonElement(false); }
        if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return new JsonElement(null); }
        int st = i; while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
        if (i == st) throw new FormatException($"JSON inválido na posição {i}");
        return new JsonElement(double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture));
    }
    static string Str(string s, ref int i)
    {
        var sb = new StringBuilder(); i++;
        while (s[i] != '"')
        {
            char c = s[i++];
            if (c != '\\') { sb.Append(c); continue; }
            char e = s[i++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break; case 't': sb.Append('\t'); break; case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break; case 'f': sb.Append('\f'); break;
                case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                default: sb.Append(e); break;
            }
        }
        i++; return sb.ToString();
    }
}

static class Writer
{
    public static string Write(object? o) { var sb = new StringBuilder(); W(sb, o); return sb.ToString(); }
    static void W(StringBuilder sb, object? o)
    {
        switch (o)
        {
            case null: sb.Append("null"); break;
            case JsonElement je: sb.Append(je.ValueKind == JsonValueKind.String ? Q(je.GetString()!) : je.ToString()); break;
            case string s: sb.Append(Q(s)); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case DateTime dt: sb.Append(Q(dt.ToString("o", CultureInfo.InvariantCulture))); break;
            case double or float or decimal or int or long or short or byte: sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture)); break;
            case Dictionary<string, JsonElement> d: sb.Append('{').Append(string.Join(",", d.Select(kv => Q(kv.Key) + ":" + Write(kv.Value)))).Append('}'); break;
            case System.Collections.IEnumerable e: sb.Append('['); bool first = true; foreach (var x in e) { if (!first) sb.Append(','); first = false; W(sb, x); } sb.Append(']'); break;
            default:
                sb.Append('{'); bool f = true;
                foreach (var p in o.GetType().GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract"))
                { if (!f) sb.Append(','); f = false; sb.Append(Q(p.Name)).Append(':'); W(sb, p.GetValue(o)); }
                sb.Append('}'); break;
        }
    }
    static string Q(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
            switch (c) { case '"': sb.Append("\\\""); break; case '\\': sb.Append("\\\\"); break; case '\n': sb.Append("\\n"); break; case '\r': sb.Append("\\r"); break; case '\t': sb.Append("\\t"); break; default: if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break; }
        return sb.Append('"').ToString();
    }
}

/// <summary>Serialização simples: objetos por propriedades públicas, listas e textos.</summary>
public static class JsonSerializer
{
    public static string Serialize<T>(T value) => Writer.Write(value);
}
