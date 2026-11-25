using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
namespace SuiteRedes;

public enum NatKind { None, Cgnat, DoubleNat, Unknown }

/// <summary>Resultado da análise de NAT. Sempre acompanhado das evidências e das limitações do método.</summary>
public class NatVerdict
{
    public NatKind Kind; public bool Confirmed; public string Title = "", Summary = "";
    public List<string> Evidence = new(), Limits = new();
    public string Short => Kind switch
    {
        NatKind.Cgnat => Confirmed ? "CGNAT confirmado" : "CGNAT provável (indício, não confirmado)",
        NatKind.DoubleNat => Confirmed ? "NAT duplo confirmado" : "NAT adicional possível (IPs privados após o roteador; não confirmado)",
        NatKind.None => Confirmed ? "Sem CGNAT: IP público direto no roteador (confirmado)" : "CGNAT não detectado (não é prova de ausência)",
        _ => "Inconclusivo"
    };
}

public static class Nat
{
    // ---- UPnP: pergunta ao roteador qual é o IP da porta WAN dele (é a prova mais direta) ----
    public static async Task<string?> UpnpWanIpAsync(IPAddress local, IPAddress? gw, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(local, 0));
            var msg = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");
            await udp.SendAsync(msg, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), ct);
            var locs = new List<string>(); var end = DateTime.UtcNow.AddSeconds(2.5);
            while (DateTime.UtcNow < end)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(end - DateTime.UtcNow);
                try
                {
                    var r = await udp.ReceiveAsync(cts.Token);
                    var m = Regex.Match(Encoding.ASCII.GetString(r.Buffer), @"(?im)^location:\s*(\S+)");
                    if (m.Success && !locs.Contains(m.Groups[1].Value)) locs.Add(m.Groups[1].Value);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
            }
            if (gw != null) locs = locs.OrderBy(l => Uri.TryCreate(l, UriKind.Absolute, out var u) && u.Host == gw.ToString() ? 0 : 1).ToList();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            foreach (var l in locs.Take(3))
            {
                try
                {
                    var xml = XDocument.Parse(await http.GetStringAsync(l, ct));
                    var baseUri = new Uri(xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value is { Length: > 0 } ub ? ub : l);
                    foreach (var svc in xml.Descendants().Where(e => e.Name.LocalName == "service"))
                    {
                        string type = svc.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value ?? "", ctl = svc.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value ?? "";
                        if (ctl == "" || !(type.Contains("WANIPConnection") || type.Contains("WANPPPConnection"))) continue;
                        var body = $"<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:GetExternalIPAddress xmlns:u=\"{type}\"/></s:Body></s:Envelope>";
                        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, ctl)) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };
                        req.Headers.Add("SOAPACTION", $"\"{type}#GetExternalIPAddress\"");
                        using var resp = await http.SendAsync(req, ct);
                        var m = Regex.Match(await resp.Content.ReadAsStringAsync(ct), @"<NewExternalIPAddress>\s*([^<\s]+)\s*<");
                        if (m.Success && IPAddress.TryParse(m.Groups[1].Value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && !ip.Equals(IPAddress.Any)) return ip.ToString();
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        return null;
    }

    // ---- análise: combina todas as evidências em um veredito estável ----
    public static NatVerdict Analyze(string? gw, IEnumerable<IReadOnlyList<StatRow>> traces, IReadOnlyList<string> publicIps, string? upnpWan, bool vpn)
    {
        var v = new NatVerdict(); var ev = v.Evidence; var lim = v.Limits;
        string pub = publicIps.GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
        bool ipVaries = publicIps.Distinct().Count() > 1;

        // 1) saltos da rota antes do primeiro salto com IP público
        int used = 0; var cgTr = new HashSet<int>(); var pvTr = new HashSet<int>(); var cgIps = new SortedSet<string>(); var pvIps = new SortedSet<string>(); int t = 0;
        foreach (var tr in traces)
        {
            t++; bool sawPublic = false; var cg = new List<string>(); var pv = new List<string>();
            foreach (var r in tr)
            {
                var a = r.Address; if (a is "*" or "?" or "") continue;
                if (a == gw) continue;
                if (NetInfo.IsCgnat(a)) cg.Add(a);
                else if (NetInfo.IsPrivate(a)) pv.Add(a);
                else { sawPublic = true; break; }
            }
            if (!sawPublic) continue;
            used++;
            if (cg.Count > 0) { cgTr.Add(t); foreach (var x in cg) cgIps.Add(x); }
            if (pv.Count > 0) { pvTr.Add(t); foreach (var x in pv) pvIps.Add(x); }
        }
        if (used > 0) ev.Add($"Rotas analisadas: {used}. Saltos em 100.64.0.0/10 antes do 1º IP público: {(cgTr.Count > 0 ? $"{cgTr.Count} rota(s) ({string.Join(", ", cgIps)})" : "nenhum")}. Saltos privados extras além do roteador: {(pvTr.Count > 0 ? $"{pvTr.Count} rota(s) ({string.Join(", ", pvIps)})" : "nenhum")}.");
        else lim.Add("Nenhuma rota respondeu o suficiente para avaliar os primeiros saltos.");

        // 2) IP público observado
        if (pub != "") ev.Add(ipVaries ? $"IP público variou entre serviços consultados ao mesmo tempo: {string.Join(", ", publicIps.Distinct())}." : $"IP público observado: {pub} (igual em {publicIps.Count} serviço(s)).");
        else lim.Add("Não foi possível descobrir o IP público.");

        // 3) IP da porta WAN informado pelo roteador (UPnP)
        bool upCg = false, upPriv = false, upSame = false, upDiff = false;
        if (upnpWan != null)
        {
            ev.Add($"O roteador informou por UPnP que o IP da sua porta WAN é {upnpWan}.");
            upCg = NetInfo.IsCgnat(upnpWan); upPriv = NetInfo.IsPrivate(upnpWan);
            upSame = pub != "" && upnpWan == pub; upDiff = pub != "" && !upSame && !upCg && !upPriv;
        }
        else lim.Add("O roteador não respondeu ao UPnP (desativado ou não suportado), então o IP da porta WAN dele não pôde ser lido.");
        if (vpn) lim.Add("Há VPN/túnel ativo: endereços 100.x e saltos privados podem vir da VPN, não do provedor.");
        lim.Add("Equipamentos do provedor podem não responder ao traceroute (ICMP filtrado); a ausência de saltos suspeitos não prova que não há CGNAT.");

        bool cgStrong = cgTr.Count >= 2 && !vpn;
        if (upCg)
        { v.Kind = NatKind.Cgnat; v.Confirmed = true; v.Title = "CGNAT confirmado"; v.Summary = $"A porta WAN do seu roteador recebeu {upnpWan}, endereço da faixa 100.64.0.0/10 reservada a CGNAT (RFC 6598). O provedor divide um IP público entre clientes."; }
        else if (cgTr.Count > 0)
        { v.Kind = NatKind.Cgnat; v.Confirmed = cgStrong; v.Title = cgStrong ? "CGNAT confirmado" : "CGNAT provável"; v.Summary = $"A rota passa por {string.Join(", ", cgIps)} (faixa 100.64.0.0/10, RFC 6598) antes de chegar a um IP público. Esta faixa é reservada ao CGNAT dos provedores.{(cgStrong ? "" : " Visto em poucas rotas: trate como indício.")}"; }
        else if (upPriv)
        { v.Kind = NatKind.DoubleNat; v.Confirmed = true; v.Title = "NAT duplo confirmado"; v.Summary = $"A porta WAN do roteador recebeu um IP privado ({upnpWan}), ou seja, existe outro equipamento fazendo NAT antes dele (outro roteador seu ou CGNAT do provedor em faixa privada)."; }
        else if (upDiff)
        { v.Kind = NatKind.Cgnat; v.Confirmed = false; v.Title = "NAT do provedor provável"; v.Summary = $"O roteador diz ter o IP {upnpWan}, mas a internet enxerga {pub}. Algum equipamento a montante troca o endereço (CGNAT com IPs públicos ou proxy transparente)."; }
        else if (pvTr.Count >= 2)
        { v.Kind = NatKind.DoubleNat; v.Confirmed = false; v.Title = "NAT adicional possível"; v.Summary = $"Há equipamento(s) com IP privado ({string.Join(", ", pvIps)}) depois do roteador e antes da internet. Pode ser um segundo roteador seu ou o CGNAT do provedor usando faixa privada."; }
        else if (ipVaries && !upSame)
        { v.Kind = NatKind.Cgnat; v.Confirmed = false; v.Title = "Indício de NAT com pool de IPs"; v.Summary = "Serviços diferentes viram IPs públicos diferentes para o mesmo computador. É comum em CGNAT com vários IPs de saída, mas também ocorre com balanceamento de duas conexões (multi-WAN) ou VPN."; }
        else if (upSame && !ipVaries)
        { v.Kind = NatKind.None; v.Confirmed = true; v.Title = "Sem CGNAT"; v.Summary = $"O IP da porta WAN do roteador ({upnpWan}) é o mesmo que a internet enxerga. Não há NAT adicional entre o roteador e a internet."; }
        else if (pvTr.Count == 1)
        { v.Kind = NatKind.Unknown; v.Title = "Inconclusivo"; v.Summary = $"Um salto privado extra apareceu em apenas uma rota ({string.Join(", ", pvIps)}). Não é suficiente para afirmar NAT duplo."; }
        else
        { v.Kind = NatKind.None; v.Confirmed = false; v.Title = "CGNAT não detectado"; v.Summary = "Nenhum indício de CGNAT ou NAT duplo foi encontrado nos testes feitos, mas sem a leitura do IP WAN do roteador não é possível garantir."; }
        return v;
    }
}
