using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SuiteRedes.Json;
using System.Text.RegularExpressions;
namespace SuiteRedes;

public class InfoItem { public string Label { get; init; } = ""; public string Value { get; init; } = ""; public string Hint { get; init; } = ""; }

public class PublicInfo
{
    public bool Ok; public string Ip = "", Ipv6 = "", Asn = "", Org = "", Isp = "", City = "", Region = "", Country = "", Rdns = "", Colo = "", Timezone = "";
    public List<string> SeenIps = new();
    public bool Ipv6Works => Ipv6 != "";
    public string Place => string.Join(", ", new[] { City, Region, Country }.Where(x => x != ""));
    public string IspName => Isp != "" ? Isp : Org;
}

/// <summary>Consultas de informação de rede: IP público, ASN/provedor, geolocalização aproximada e operadoras da rota.</summary>
public static class NetInfo
{
    static readonly Dictionary<string, (string asn, string org)> cache = new();
    static readonly Dictionary<string, string> asnNames = new();

    static HttpClient NewHttp(int seconds = 6) { var h = new HttpClient { Timeout = TimeSpan.FromSeconds(seconds) }; h.DefaultRequestHeaders.UserAgent.ParseAdd("SuiteRedes/2.0 (+https://www.skytins.com.br)"); return h; }
    static string S(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

    public static async Task<PublicInfo> GetPublicAsync(CancellationToken ct)
    {
        var p = new PublicInfo();
        // IP público por UMA consulta à Cloudflare (1.1.1.1). Não usamos serviços "qual é meu IP"/geolocalização
        // (ipify, icanhazip, ipwho.is...): consultar vários deles é o padrão de reconhecimento de trojans e
        // dispara a detecção comportamental dos antivírus (ex.: Kaspersky PDM:Trojan.Win32.Bazon).
        try
        {
            using var http = NewHttp(5);
            var t = await http.GetStringAsync("https://1.1.1.1/cdn-cgi/trace", ct);
            foreach (var l in t.Split((char)10))
            {
                if (l.StartsWith("colo=")) p.Colo = l[5..].Trim();
                if (l.StartsWith("loc=")) p.Country = l[4..].Trim();
                if (l.StartsWith("ip=")) { p.Ip = l[3..].Trim(); p.Ok = IPAddress.TryParse(p.Ip, out _); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        if (p.Ok) p.SeenIps.Add(p.Ip);
        // ASN pelo DNS da Team Cymru (se a API não trouxe)
        if (p.Ip != "" && p.Asn == "") { var (a, o) = await AsnAsync(p.Ip, ct); p.Asn = a; if (p.Org == "") p.Org = o; }
        if (p.Ip != "" && IPAddress.TryParse(p.Ip, out var ip)) p.Rdns = await Net.ReverseAsync(ip, 2500) ?? "";
        // IPv6
        try
        {
            if (NetworkInterface.GetAllNetworkInterfaces().Any(n => n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal && !u.Address.IsIPv6Teredo && !IPAddress.IsLoopback(u.Address))))
            {
                using var http = NewHttp(4);
                var t = await http.GetStringAsync("https://[2606:4700:4700::1111]/cdn-cgi/trace", ct);
                foreach (var l in t.Split('\n')) if (l.StartsWith("ip=")) p.Ipv6 = l[3..].Trim();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        return p;
    }

    static bool IsLocalRange(byte[] b) => b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || b[0] == 127 || (b[0] == 169 && b[1] == 254);
    public static bool IsCgnat(string ip) => IPAddress.TryParse(ip, out var a) && a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes() is var b && b[0] == 100 && b[1] is >= 64 and <= 127;
    public static bool IsPrivate(string ip) => IPAddress.TryParse(ip, out var a) && a.AddressFamily == AddressFamily.InterNetwork && IsLocalRange(a.GetAddressBytes());

    // ASN/operadora via HTTPS (RIPEstat). Antes era por consultas DNS TXT próprias (UDP/53 direto a 1.1.1.1/8.8.8.8 com o IP
    // codificado no nome): esse padrão é idêntico a túnel DNS de malware e dispara a detecção comportamental dos antivírus.
    static readonly SemaphoreSlim ripeGate = new(4);
    static async Task<JsonElement?> RipeAsync(string call, string resource, CancellationToken ct)
    {
        await ripeGate.WaitAsync(ct);
        try
        {
            using var http = NewHttp(8);
            var t = await http.GetStringAsync($"https://stat.ripe.net/data/{call}/data.json?resource={Uri.EscapeDataString(resource)}&sourceapp=SuiteRedes", ct);
            using var d = JsonDocument.Parse(t);
            return d.RootElement.TryGetProperty("data", out var x) ? x.Clone() : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
        finally { ripeGate.Release(); }
    }

    /// <summary>ASN e nome da operadora de um IP IPv4.</summary>
    public static async Task<(string asn, string org)> AsnAsync(string ip, CancellationToken ct)
    {
        if (!IPAddress.TryParse(ip, out var a) || a.AddressFamily != AddressFamily.InterNetwork) return ("", "");
        var b = a.GetAddressBytes();
        if (IsCgnat(ip)) return ("", "CGNAT do provedor");
        if (IsLocalRange(b)) return ("", "Rede local");
        lock (cache) if (cache.TryGetValue(ip, out var c)) return c;
        var res = ("", "");
        try
        {
            var d = await RipeAsync("network-info", ip, ct);
            string asn = "";
            if (d is { } x && x.TryGetProperty("asns", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray()) { asn = e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString(); if (asn != "") break; }
            if (asn != "" && int.TryParse(asn, out var n)) res = ("AS" + asn, await AsnNameAsync(n, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        if (res.Item1 != "") lock (cache) cache[ip] = res;
        return res;
    }

    public static async Task<string> AsnNameAsync(int asn, CancellationToken ct)
    {
        string key = asn.ToString(); string? name;
        lock (asnNames) asnNames.TryGetValue(key, out name);
        if (name != null) return name;
        try
        {
            var d = await RipeAsync("as-overview", "AS" + key, ct);
            name = d is { } x ? S(x, "holder") : "";
            name = Regex.Replace(Regex.Replace(name, @"^AS\d+\s*[-–]?\s*", ""), @",\s*[A-Z]{2}$", "").Trim();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { name = ""; }
        if (name != "") lock (asnNames) asnNames[key] = name;
        return name;
    }

    /// <summary>Preenche ASN/operadora de cada salto da rota.</summary>
    public static async Task EnrichHopsAsync(IEnumerable<StatRow> rows, CancellationToken ct)
    {
        await Task.WhenAll(rows.Where(r => r.Address is not ("*" or "?" or "")).Select(async r =>
        {
            var (asn, org) = await AsnAsync(r.Address, ct);
            r.Asn = asn; r.Org = org; r.Raise();
        }));
        double? prev = null;
        foreach (var r in rows)
        {
            if (r.Received > 0) { r.Delta = prev == null ? "—" : $"{(r.Avg - prev.Value >= 0 ? "+" : "")}{r.Avg - prev.Value:F0} ms"; prev = r.Avg; } else r.Delta = "—";
            r.Raise();
        }
    }

    /// <summary>Resumo "AS1 (Nome) → AS2 (Nome) → ..." sem repetições consecutivas.</summary>
    public static string AsPath(IEnumerable<StatRow> rows)
    {
        var parts = new List<string>(); string last = "";
        foreach (var r in rows)
        {
            string k = r.Asn != "" ? $"{r.Asn} ({r.Org})" : r.Org is "Rede local" or "CGNAT do provedor" ? r.Org : "";
            if (k != "" && k != last) { parts.Add(k); last = k; }
        }
        return string.Join(" → ", parts);
    }

    public static Dictionary<string, string> WifiDetails(string netsh)
    {
        var d = new Dictionary<string, string>();
        foreach (var line in netsh.Split('\n'))
        {
            int i = line.IndexOf(" : "); if (i < 0) i = line.IndexOf(": "); if (i < 0) continue;
            string k = line[..i].Trim(), v = line[(i + 2)..].Trim().TrimStart(':').Trim();
            string? key = k switch
            {
                "SSID" => "ssid", "Radio type" or "Tipo de rádio" or "Tipo de radio" => "radio", "Channel" or "Canal" => "channel", "Band" or "Banda" => "band",
                "Receive rate (Mbps)" or "Velocidade de recepção (Mbps)" or "Taxa de recepção (Mbps)" => "rx", "Transmit rate (Mbps)" or "Velocidade de transmissão (Mbps)" or "Taxa de transmissão (Mbps)" => "tx",
                "Authentication" or "Autenticação" => "auth", _ => null
            };
            if (key != null && !d.ContainsKey(key)) d[key] = v;
        }
        return d;
    }
}
