using System.Net;
using System.Net.Sockets;
using System.Text;
using SuiteRedes.Json;
namespace SuiteRedes;

public class IpIntelReport
{
    public string Ip = "", Prefix = "", Error = "";
    public List<int> OriginAsns = new();
    public List<InfoItem> Registry = new(), Routing = new(), Upstreams = new(), Downstreams = new();
    public List<string> Prefixes = new(); public int PrefixTotal;
    public string WhoisText = "";
    public List<string> Notes = new();
    public Dictionary<int, string> Names = new();
}

/// <summary>Consulta de IP público: registro (RDAP), roteamento BGP, vizinhos, prefixos e whois. Fontes: RDAP dos registros regionais e RIPEstat (dados do RIPE RIS).</summary>
public static class IpIntel
{
    static readonly HttpClient Http = Make();
    static HttpClient Make() { var h = new HttpClient { Timeout = TimeSpan.FromSeconds(25) }; h.DefaultRequestHeaders.UserAgent.ParseAdd("SuiteRedes/2.0 (+https://www.skytins.com.br)"); return h; }
    const string Stat = "https://stat.ripe.net/data/";

    static async Task<JsonElement?> Json(string url, CancellationToken ct)
    {
        try { using var r = await Http.GetAsync(url, ct); if (!r.IsSuccessStatusCode) return null; return JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.Clone(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }
    static JsonElement? Data(JsonElement? e) => e is { } r && r.TryGetProperty("data", out var d) ? d : null;
    static string CleanName(string n) => System.Text.RegularExpressions.Regex.Replace(n.Trim(), @"^AS\d+\s*[-–]?\s*", "");
    static string Str(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
    static void Add(List<InfoItem> l, string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) l.Add(new InfoItem { Label = k, Value = v! }); }

    public static bool IsPublicIp(string s, out IPAddress ip)
    {
        ip = IPAddress.None;
        if (!IPAddress.TryParse(s, out var a)) return false; ip = a;
        if (a.AddressFamily == AddressFamily.InterNetwork) { var b = a.GetAddressBytes(); return !(NetInfo.IsPrivate(s) || NetInfo.IsCgnat(s) || b[0] == 0 || b[0] >= 224 || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) || (b[0] == 198 && b[1] is 18 or 19) || (b[0] == 198 && b[1] == 51 && b[2] == 100) || (b[0] == 203 && b[1] == 0 && b[2] == 113)); }
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return !(IPAddress.IsLoopback(a) || a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || a.GetAddressBytes()[0] is 0xfc or 0xfd || a.Equals(IPAddress.IPv6Any));
        return false;
    }

    public static async Task<IpIntelReport> QueryAsync(string input, CancellationToken ct)
    {
        var rep = new IpIntelReport { Ip = input.Trim() };
        if (!IsPublicIp(rep.Ip, out var ipa)) { rep.Error = "Informe um IP público válido. Endereços privados, CGNAT (100.64.0.0/10) e reservados não têm registro público."; return rep; }
        rep.Ip = ipa.ToString();
        var rdapT = Json("https://rdap.org/ip/" + rep.Ip, ct);
        var povT = Json($"{Stat}prefix-overview/data.json?resource={rep.Ip}&sourceapp=SuiteRedes", ct);
        var whoT = Json($"{Stat}whois/data.json?resource={rep.Ip}&sourceapp=SuiteRedes", ct);
        var cymT = NetInfo.AsnAsync(rep.Ip, ct);
        await Task.WhenAll(rdapT, povT, whoT, cymT);

        // ---- Roteamento: prefixo e AS de origem ----
        var pov = Data(await povT); bool announced = false; var holders = new Dictionary<int, string>();
        if (pov is { } pd)
        {
            rep.Prefix = Str(pd, "resource"); announced = pd.TryGetProperty("announced", out var an) && an.ValueKind == JsonValueKind.True;
            if (pd.TryGetProperty("asns", out var asns)) foreach (var a in asns.EnumerateArray()) if (a.TryGetProperty("asn", out var n) && n.TryGetInt32(out var num)) { rep.OriginAsns.Add(num); holders[num] = CleanName(Str(a, "holder")); }
        }
        else rep.Notes.Add("RIPEstat indisponível: dados de roteamento BGP não puderam ser obtidos.");
        var cym = await cymT;
        Add(rep.Routing, "Prefixo anunciado", rep.Prefix);
        if (rep.OriginAsns.Count > 0) Add(rep.Routing, rep.OriginAsns.Count > 1 ? "AS de origem (vários)" : "AS de origem", string.Join(" • ", rep.OriginAsns.Select(n => $"AS{n} {holders[n]}".Trim())));
        else if (pov != null) rep.Notes.Add("Este IP não está anunciado na tabela BGP vista pelo RIPE RIS neste momento.");
        if (cym.asn != "")
        {
            bool agree = rep.OriginAsns.Contains(int.TryParse(cym.asn[2..], out var cn) ? cn : -1);
            Add(rep.Routing, "Conferência (Team Cymru)", $"{cym.asn} {CleanName(cym.org)} — {(agree || rep.OriginAsns.Count == 0 ? "confere" : "DIVERGE do RIPEstat")}");
            if (!agree && rep.OriginAsns.Count > 0) rep.Notes.Add($"As fontes divergem quanto ao AS de origem ({cym.asn} no Team Cymru). Pode ser mudança de roteamento recente.");
        }

        // ---- Registro (RDAP) ----
        var rd = await rdapT;
        if (rd is { } r && r.TryGetProperty("handle", out _))
        {
            Add(rep.Registry, "Nome da rede", Str(r, "name")); Add(rep.Registry, "Handle", Str(r, "handle"));
            Add(rep.Registry, "Faixa", $"{Str(r, "startAddress")} – {Str(r, "endAddress")}");
            if (r.TryGetProperty("cidr0_cidrs", out var cs)) Add(rep.Registry, "CIDR", string.Join(", ", cs.EnumerateArray().Select(c => $"{Str(c, "v4prefix")}{Str(c, "v6prefix")}/{Str(c, "length")}")));
            Add(rep.Registry, "Tipo", Str(r, "type")); Add(rep.Registry, "País", Str(r, "country"));
            if (r.TryGetProperty("status", out var st)) Add(rep.Registry, "Status", string.Join(", ", st.EnumerateArray().Select(x => x.GetString())));
            Add(rep.Registry, "Bloco pai", Str(r, "parentHandle"));
            if (r.TryGetProperty("events", out var evs)) foreach (var e in evs.EnumerateArray()) { var act = Str(e, "eventAction"); var d = Str(e, "eventDate"); if (d.Length >= 10) Add(rep.Registry, act switch { "registration" => "Registrado em", "last changed" => "Última alteração", _ => act }, d[..10]); }
            if (r.TryGetProperty("entities", out var ents)) foreach (var line in Contacts(ents)) rep.Registry.Add(line);
            Add(rep.Registry, "Registro (RIR)", Str(r, "port43"));
        }
        else rep.Notes.Add("RDAP não respondeu: use o whois abaixo (texto do próprio registro).");

        // ---- Whois bruto ----
        if (Data(await whoT) is { } wd)
        {
            var sb = new StringBuilder();
            foreach (var key in new[] { "records", "irr_records" })
                if (wd.TryGetProperty(key, out var recs))
                    foreach (var rec in recs.EnumerateArray()) { foreach (var kv in rec.EnumerateArray()) { var k = Str(kv, "key"); var v = Str(kv, "value"); if (k != "") sb.AppendLine($"{k}: {v}"); } sb.AppendLine(); }
            rep.WhoisText = sb.ToString().TrimEnd();
        }

        // ---- Por AS de origem: prefixos, vizinhos, RPKI, caminhos ----
        if (rep.OriginAsns.Count > 0)
        {
            int asn = rep.OriginAsns[0]; string res = "AS" + asn;
            var pfxT = Json($"{Stat}announced-prefixes/data.json?resource={res}&sourceapp=SuiteRedes", ct);
            var neiT = Json($"{Stat}asn-neighbours/data.json?resource={res}&sourceapp=SuiteRedes", ct);
            var rpkiT = rep.Prefix != "" ? Json($"{Stat}rpki-validation/data.json?resource={res}&prefix={rep.Prefix}&sourceapp=SuiteRedes", ct) : Task.FromResult<JsonElement?>(null);
            await Task.WhenAll(pfxT, neiT, rpkiT);

            if (Data(await rpkiT) is { } rk) { var s = Str(rk, "status"); Add(rep.Routing, "Validação RPKI (origem × prefixo)", s switch { "valid" => "Válida", "invalid" => "INVÁLIDA", "unknown" => "Sem ROA (não validada)", _ => s }); }
            if (Data(await pfxT) is { } pf && pf.TryGetProperty("prefixes", out var ps))
            {
                var all = ps.EnumerateArray().Select(p => Str(p, "prefix")).Where(p => p != "").ToList();
                rep.PrefixTotal = all.Count; int v4 = all.Count(p => !p.Contains(':'));
                Add(rep.Routing, $"Prefixos anunciados por AS{asn}", $"{all.Count} ({v4} IPv4, {all.Count - v4} IPv6)");
                rep.Prefixes = all.OrderBy(p => p.Contains(':') ? 1 : 0).ThenBy(p => p.Contains(':') ? 0 : (int)(BitConverter.ToUInt32(IPAddress.Parse(p.Split('/')[0]).GetAddressBytes().Reverse().ToArray(), 0) >> 8)).ThenBy(p => p, StringComparer.Ordinal).Take(120).ToList();
            }
            var asSet = new HashSet<int>();
            List<(int asn, int power, int peers)> left = new(), right = new(); int lc = 0, rc = 0, uc = 0;
            if (Data(await neiT) is { } nd)
            {
                if (nd.TryGetProperty("neighbour_counts", out var nc)) { int.TryParse(Str(nc, "left"), out lc); int.TryParse(Str(nc, "right"), out rc); int.TryParse(Str(nc, "uncertain"), out uc); }
                if (nd.TryGetProperty("neighbours", out var ns)) foreach (var n in ns.EnumerateArray())
                {
                    if (!n.TryGetProperty("asn", out var na) || !na.TryGetInt32(out var nn)) continue; int.TryParse(Str(n, "power"), out var pw); int.TryParse(Str(n, "v4_peers"), out var vp);
                    var t = Str(n, "type"); if (t == "left") left.Add((nn, pw, vp)); else if (t == "right") right.Add((nn, pw, vp));
                }
            }
            // nomes
            var need = new HashSet<int>(rep.OriginAsns); foreach (var x in left.OrderByDescending(x => x.power).Take(15)) need.Add(x.asn); foreach (var x in right.OrderByDescending(x => x.power).Take(15)) need.Add(x.asn);
            foreach (var kv in holders) rep.Names[kv.Key] = kv.Value;
            var sem = new SemaphoreSlim(6);
            await Task.WhenAll(need.Where(a => !rep.Names.ContainsKey(a)).Select(async a => { await sem.WaitAsync(ct); try { var n = await NetInfo.AsnNameAsync(a, ct); lock (rep.Names) rep.Names[a] = CleanName(n); } finally { sem.Release(); } }));
            string Nm(int a) => rep.Names.TryGetValue(a, out var n) && n != "" ? n : "(nome não obtido)";
            foreach (var x in left.OrderByDescending(x => x.power).Take(8)) rep.Upstreams.Add(new InfoItem { Label = "AS" + x.asn, Value = $"{Nm(x.asn)} — visto por {x.peers} coletores" });
            foreach (var x in right.OrderByDescending(x => x.power).Take(15)) rep.Downstreams.Add(new InfoItem { Label = "AS" + x.asn, Value = $"{Nm(x.asn)} — visto por {x.peers} coletores" });
            Add(rep.Routing, "Vizinhos BGP observados", $"{lc} a montante (provedores/peers de trânsito) • {rc} a jusante (clientes) • {uc} incertos");
            rep.Notes.Add("Vizinhos e caminhos vêm de coletores públicos do RIPE RIS. Não é a lista completa de peerings do AS e não distingue trânsito pago de peering gratuito.");
        }
        return rep;
    }

    static IEnumerable<InfoItem> Contacts(JsonElement ents)
    {
        foreach (var e in ents.EnumerateArray())
        {
            string roles = e.TryGetProperty("roles", out var rs) ? string.Join("/", rs.EnumerateArray().Select(x => x.GetString() switch { "registrant" => "Titular", "abuse" => "Abuso", "technical" => "Técnico", "administrative" => "Administrativo", "noc" => "NOC", var o => o })) : "";
            string fn = "", mail = "";
            if (e.TryGetProperty("vcardArray", out var vc) && vc.GetArrayLength() > 1)
                foreach (var f in vc[1].EnumerateArray()) { var name = f[0].GetString(); var val = f.GetArrayLength() > 3 && f[3].ValueKind == JsonValueKind.String ? f[3].GetString() : ""; if (name == "fn") fn = val ?? ""; if (name == "email") mail = val ?? ""; }
            if (roles != "" && (fn != "" || mail != "")) yield return new InfoItem { Label = roles, Value = string.Join(" — ", new[] { fn, mail }.Where(x => x != "")) };
            if (e.TryGetProperty("entities", out var sub)) foreach (var s in Contacts(sub)) yield return s;
        }
    }
}
