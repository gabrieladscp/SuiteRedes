using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
namespace SuiteRedes;

/// <summary>Resultado consolidado de um destino testado (ping + TCP + HTTPS + rota).</summary>
public class DestResult
{
    public string Name = ""; public string Ip = ""; public StatRow Row = new();
    public bool Resolved = true; public string Tcp = ""; public bool? HttpOk; public long HttpMs;
    public bool TraceDone, TraceReached, RouteLoop; public int FirstLossHop; public string FirstLossAddr = "", FirstLossOrg = ""; public double FirstLossPct; public bool FirstLossLocal;
    public bool Reachable => Row.Received > 0 || Tcp == "Aberta";
    public bool IsIp => IPAddress.TryParse(Name, out _);
}

/// <summary>Todos os fatos coletados pelo teste único. O motor de diagnóstico raciocina sobre isto.</summary>
public class Evidence
{
    // máquina local
    public bool HasPrimary, IsWifi, IsEthernet, Apipa, NoGateway, ProxyActive, VpnActive;
    public long LinkMbps; public double AdapterErrPct, TcpRetransPct; public int? WifiSignal; public string AdapterName = "";
    // roteador
    public bool GwTested; public int GwSent, GwRecv; public double GwLoss, GwAvg, GwJitter;
    // destinos
    public List<DestResult> Dests = new();
    // DNS
    public bool DnsTested, ResolverOk; public int SysDnsCount; public bool SysDnsOk, PubDnsOk, SysNx; public double SysDnsAvg = -1, PubDnsAvg = -1; public string DnsDomain = "";
    // HTTP
    public int HttpTotal, HttpOk; public bool TlsErrors; public double HttpTtfbAvg;
    // extras
    public int? Mtu; public double? SpeedMbps, IdleAvg, LoadedAvg, LoadedLoss;
    public PublicInfo Pub = new(); public bool Cgnat, DoubleNat; public NatVerdict? Nat; public string WifiBand = "", WifiRadio = ""; public double? WifiRate;
    public string Isp => Pub.IspName != "" ? Pub.IspName : "seu provedor";

    public IEnumerable<DestResult> Inet => Dests.Where(d => d.Row.Sent > 0 || d.Tcp != "");
    public int PingOk => Inet.Count(d => d.Row.Received > 0);
    public int TcpTried => Inet.Count(d => d.Tcp != "");
    public int TcpOk => Inet.Count(d => d.Tcp == "Aberta");
    public bool InetOk => Inet.Any(d => d.Reachable);
    public double InetLoss => Inet.Where(d => d.Row.Received > 0).Select(d => d.Row.Loss).DefaultIfEmpty(0).Average();
    public double InetAvg => Inet.Where(d => d.Row.Received > 0).Select(d => d.Row.Avg).DefaultIfEmpty(0).Average();
    public double InetJitter => Inet.Where(d => d.Row.Received > 0).Select(d => d.Row.Jitter).DefaultIfEmpty(0).Average();
}

public class Hypothesis
{
    public string Id { get; init; } = ""; public string Title { get; init; } = ""; public string Who { get; init; } = "";
    public int Confidence { get; set; } public Sev Severity { get; init; } public string Summary { get; init; } = "";
    public List<string> Why { get; } = new(); public List<string> Steps { get; init; } = new();
    public string ConfText => $"{Confidence}% de certeza";
    public string WhoText => "Responsável: " + Who;
    public Brush Color => Severity switch { Sev.Ok => Pal.Ok, Sev.Info => Pal.Info, Sev.Warn => Pal.Warn, _ => Pal.Err };
    public Brush Tint => Severity switch { Sev.Ok => Pal.OkBg, Sev.Info => Pal.InfoBg, Sev.Warn => Pal.WarnBg, _ => Pal.ErrBg };
    public string Icon => Severity switch { Sev.Ok => "✅", Sev.Info => "ℹ️", Sev.Warn => "⚠️", _ => "❌" };
    public string WhyText => string.Join("\n", Why.Select(w => "• " + w));
    public string StepsText => Steps.Count == 0 ? "Nenhuma ação necessária." : string.Join("\n", Steps.Select((s, i) => $"{i + 1}. {s}"));
}

public enum StageState { Ok, Warn, Error, Unknown }
public class Stage
{
    public string Icon { get; init; } = ""; public string Name { get; init; } = ""; public string Detail { get; set; } = ""; public StageState State { get; set; }
    public Brush Edge => State switch { StageState.Ok => Pal.Ok, StageState.Warn => Pal.Warn, StageState.Error => Pal.Err, _ => Pal.Gray };
    public Brush Fill => State switch { StageState.Ok => Pal.OkBg, StageState.Warn => Pal.WarnBg, StageState.Error => Pal.ErrBg, _ => Pal.GrayBg };
    public string StateText => State switch { StageState.Ok => "● Normal", StageState.Warn => "● Atenção", StageState.Error => "● Problema", _ => "● Não avaliado" };
}

public class Report
{
    public List<Hypothesis> Hyps = new(); public List<Stage> Stages = new(); public string Narrative = "";
    public Hypothesis Top => Hyps[0];
}

/// <summary>Motor de diagnóstico embutido: sistema especialista local (regras + correlação + eliminação de causas).</summary>
public static class Diagnosis
{
    // ---------- coleta local ----------
    public static void GatherLocal(Evidence e, string wifiText)
    {
        var (nic, gw) = Diagnostics.Primary();
        var ups = Diagnostics.Nics().Where(n => n.OperationalStatus == OperationalStatus.Up && !Diagnostics.IsVirtual(n)).ToList();
        e.HasPrimary = nic != null;
        e.NoGateway = nic == null && ups.Count > 0;
        e.Apipa = (nic != null ? new[] { nic } : ups.ToArray()).Any(n => n.GetIPProperties().UnicastAddresses.Any(u => u.Address.ToString().StartsWith("169.254.")));
        var n0 = nic ?? ups.FirstOrDefault();
        if (n0 != null)
        {
            e.AdapterName = n0.Name; e.IsWifi = n0.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            e.IsEthernet = n0.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx;
            e.LinkMbps = n0.Speed / 1_000_000;
            try { var st = n0.GetIPv4Statistics(); long pk = st.UnicastPacketsReceived + st.UnicastPacketsSent; e.AdapterErrPct = pk > 1000 ? 100.0 * (st.IncomingPacketsWithErrors + st.OutgoingPacketsWithErrors + st.IncomingPacketsDiscarded + st.OutgoingPacketsDiscarded) / pk : 0; } catch { }
        }
        var m = Regex.Match(wifiText, @"(Signal|Sinal)\s*:\s*(\d+)\s*%", RegexOptions.IgnoreCase);
        if (e.IsWifi && m.Success) e.WifiSignal = int.Parse(m.Groups[2].Value);
        try { var t = IPGlobalProperties.GetIPGlobalProperties().GetTcpIPv4Statistics(); e.TcpRetransPct = t.SegmentsSent > 5000 ? 100.0 * t.SegmentsResent / t.SegmentsSent : 0; } catch { }
        try { var p = System.Net.WebRequest.GetSystemWebProxy().GetProxy(new Uri("http://www.example.com")); e.ProxyActive = p != null && p.Host != "www.example.com"; } catch { }
        e.VpnActive = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.OperationalStatus == OperationalStatus.Up &&
            (n.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel && !n.Description.Contains("Teredo") && !n.Description.Contains("isatap", StringComparison.OrdinalIgnoreCase) && !n.Description.Contains("6to4") ||
             new[] { "vpn", "wireguard", "openvpn", "nord", "tap-windows", "fortinet", "anyconnect" }.Any(k => n.Description.Contains(k, StringComparison.OrdinalIgnoreCase))));
    }

    static bool IsPrivate(string a)
    {
        if (!IPAddress.TryParse(a, out var ip)) return false; var b = ip.GetAddressBytes(); if (b.Length != 4) return false;
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    public static void FillTrace(DestResult d, List<StatRow> rows, bool reached)
    {
        d.TraceDone = true; d.TraceReached = reached;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Sent < 5 || r.Loss < 20) continue;
            if (rows.Skip(i + 1).All(x => x.Loss >= 10) && (i < rows.Count - 1 || reached))
            { d.FirstLossHop = r.Index; d.FirstLossAddr = r.Address; d.FirstLossOrg = r.Org; d.FirstLossPct = r.Loss; d.FirstLossLocal = IsPrivate(r.Address) && r.Index <= 2; break; }
        }
        d.RouteLoop = rows.Where(r => r.Address is not ("*" or "?" or "")).GroupBy(r => r.Address).Any(g => g.Count() > 1 && g.Last().Index - g.First().Index > 1);
    }

    // ---------- motor ----------
    public static Report Analyze(Evidence e)
    {
        var hyps = new List<Hypothesis>();
        Hypothesis H(string id, string title, string who, int conf, Sev sev, string summary, string[] steps, params string[] why)
        {
            var h = new Hypothesis { Id = id, Title = title, Who = who, Confidence = Compat.Clamp(conf, 5, 98), Severity = sev, Summary = summary, Steps = steps.ToList() };
            h.Why.AddRange(why.Where(w => w != "")); hyps.Add(h); return h;
        }
        bool inetOk = e.InetOk, inetTested = e.Inet.Any();
        bool gwSilent = e.GwTested && e.GwRecv == 0 && inetOk;
        bool gwBad = e.GwTested && ((e.GwRecv == 0 && !inetOk) || (e.GwRecv > 0 && e.GwLoss >= 5 && e.GwSent - e.GwRecv >= 3));
        bool gwDeg = e.GwTested && e.GwRecv > 0 && !gwBad && ((e.GwLoss >= 3 && e.GwSent - e.GwRecv >= 3) || e.GwAvg > 25 || e.GwJitter > 30);
        bool local = gwBad || gwDeg;
        string gwTxt = e.GwTested ? (e.GwRecv == 0 ? "O roteador não respondeu a nenhum teste." : $"O roteador respondeu {e.GwRecv} de {e.GwSent} testes (perda {e.GwLoss:F0}%, média {e.GwAvg:F0} ms, oscilação {e.GwJitter:F0} ms).") : "";
        string inetTxt = inetTested ? $"Até os destinos externos: perda média {e.InetLoss:F1}%, tempo médio {e.InetAvg:F0} ms, oscilação {e.InetJitter:F0} ms." : "";

        if (!e.HasPrimary && !e.NoGateway && !e.Apipa)
            H("sem-rede", "O computador não está conectado a nenhuma rede", "Seu computador / cabo / Wi-Fi", 97, Sev.Error,
              "Nenhum adaptador de rede ativo foi encontrado. Sem conexão física ou Wi-Fi não há como acessar nada.",
              new[] { "Se usa cabo: confira se está firme nas duas pontas e se as luzes da porta acendem.", "Se usa Wi-Fi: ligue o Wi-Fi, desligue o modo avião e conecte à sua rede.", "Reinicie o computador e o roteador.", "Teste outro cabo ou outra porta do roteador." },
              "Nenhum adaptador físico está ativo (conectado).");
        else if (e.Apipa)
            H("dhcp", "O roteador não está entregando um endereço IP ao computador", "Seu roteador / Wi-Fi", 95, Sev.Error,
              "O Windows recebeu um endereço provisório (169.254.x.x), o que significa que o servidor DHCP do roteador não respondeu. Sem IP válido, não há internet.",
              new[] { "Reinicie o roteador (desligue da tomada por 30 segundos).", "No Windows abra o Prompt de Comando e execute: ipconfig /release e depois ipconfig /renew.", "Esqueça a rede Wi-Fi e conecte de novo.", "Verifique se o DHCP está ligado nas configurações do roteador." },
              "Endereço IP provisório (169.254.x.x) no adaptador " + e.AdapterName + ".");
        else if (e.NoGateway)
            H("sem-gateway", "Conectado à rede, mas sem caminho para fora (sem gateway)", "Seu roteador / configuração de IP", 93, Sev.Error,
              "O adaptador está ativo, porém não tem um roteador padrão configurado; por isso nada fora da rede local é acessível.",
              new[] { "Configure o IP como automático (DHCP) nas propriedades do adaptador.", "Se usa IP fixo, confira o campo Gateway padrão.", "Reinicie o roteador." }, "Adaptador ativo sem gateway padrão.");

        bool noLocalConn = hyps.Count > 0;

        if (!noLocalConn)
        {
            // Wi-Fi
            if (e.IsWifi && e.WifiSignal is int sg && sg < 55)
            {
                int c = 40 + (55 - sg) / 2 + (local ? 30 : 0) + (e.InetLoss > 2 ? 5 : 0);
                H("wifi-fraco", "Sinal de Wi-Fi fraco", "Seu Wi-Fi (posição do computador/roteador)", c, local ? Sev.Error : Sev.Warn,
                  "O computador está longe do roteador ou há obstáculos no caminho. Sinal fraco causa perda de pacotes, quedas e lentidão.",
                  new[] { "Aproxime-se do roteador ou tire obstáculos (paredes, espelhos, eletrodomésticos).", "Prefira a rede de 5 GHz se o roteador for dual band e estiver perto.", "Se possível, conecte por cabo de rede para confirmar que o problema some.", "Considere um repetidor ou sistema mesh." },
                  $"Sinal do Wi-Fi em {sg}% (o ideal é acima de 70%).", gwTxt);
            }
            else if (e.IsWifi && local)
            {
                H("wifi-interf", "Wi-Fi congestionado ou com interferência", "Seu Wi-Fi / roteador", 58 + (e.GwJitter > 30 ? 15 : 0) + (e.GwLoss >= 5 ? 10 : 0), Sev.Warn,
                  "O sinal está bom, mas a comunicação com o roteador está instável. Costuma ser interferência de redes vizinhas, canal lotado ou roteador sobrecarregado.",
                  new[] { "Troque o canal do Wi-Fi no roteador (use 1, 6 ou 11 em 2,4 GHz) ou use 5 GHz.", "Afaste o roteador de micro-ondas, telefones sem fio e caixas de som Bluetooth.", "Desconecte aparelhos que estejam baixando muito (TVs, celulares, consoles).", "Teste por cabo para confirmar se o Wi-Fi é a causa." },
                  e.WifiSignal is int s2 ? $"Sinal bom ({s2}%), mas o roteador responde mal." : "Roteador responde mal pelo Wi-Fi.", gwTxt, e.WifiBand.Contains("2,4") || e.WifiBand.Contains("2.4") ? "Você está na banda de 2,4 GHz, a mais sujeita a interferência." : "", e.WifiRate is double wr && wr < 100 ? $"Taxa de conexão Wi-Fi baixa: {wr:F0} Mbps." : "");
            }
            // Cabo / placa
            if (e.IsEthernet && (e.LinkMbps is > 0 and <= 100 || e.AdapterErrPct >= 1))
            {
                int c = (e.LinkMbps is > 0 and <= 100 ? 45 : 0) + (e.AdapterErrPct >= 1 ? 45 : 0) + (local ? 15 : 0);
                H("cabo", "Cabo de rede ou placa de rede com defeito", "Seu cabo / placa de rede", c, c >= 70 ? Sev.Error : Sev.Warn,
                  "O link cabeado negociou velocidade baixa e/ou está gerando pacotes com erro. Isso é típico de cabo danificado, conector ruim ou porta defeituosa.",
                  new[] { "Troque o cabo por outro (Cat5e ou Cat6) e teste.", "Teste outra porta do roteador/switch.", "Atualize o driver da placa de rede.", "Se persistir, a placa de rede pode estar com defeito." },
                  e.LinkMbps is > 0 and <= 100 ? $"Cabo negociou apenas {e.LinkMbps} Mbps (esperado 1000 Mbps)." : "", e.AdapterErrPct >= 1 ? $"{e.AdapterErrPct:F2}% dos pacotes do adaptador tiveram erro/descarte." : "");
            }
            // Roteador
            if (local && !(e.IsWifi && e.WifiSignal is < 55) && !hyps.Any(h => h.Id is "wifi-interf" or "cabo"))
                H("roteador", "Roteador sobrecarregado ou com defeito", "Seu roteador", 62 + (e.GwLoss >= 10 ? 15 : 0), gwBad ? Sev.Error : Sev.Warn,
                  "A conexão do computador até o roteador já apresenta perda ou lentidão, então o problema está dentro da sua casa/empresa, antes de chegar ao provedor.",
                  new[] { "Reinicie o roteador (30 segundos desligado).", "Desconecte aparelhos desnecessários.", "Verifique superaquecimento e atualize o firmware.", "Se for antigo, considere trocar." }, gwTxt);
            // Provedor
            if (!local && inetTested && !inetOk)
                H("provedor-off", "Sem acesso à internet: problema no modem ou no provedor", $"Seu provedor ({e.Isp})", 92, Sev.Error,
                  "O roteador está bem, mas nenhum destino da internet respondeu (nem ping, nem conexão TCP). O corte está entre o seu modem e a internet.",
                  new[] { "Reinicie o modem/ONU (30 segundos desligado) e aguarde 3 minutos.", "Observe as luzes: LOS/PON/Internet vermelha ou apagada indica falha na linha.", "Confira se não há aviso de manutenção/queda na região ou fatura em atraso.", "Ligue para o provedor e informe: 'o roteador responde, mas não há saída para a internet'." },
                  gwTxt, $"Nenhum dos {e.Inet.Count()} destino(s) respondeu.");
            if (!local && inetOk && e.InetLoss >= 3)
            {
                var worst = e.Dests.FirstOrDefault(d => d.FirstLossHop > 0);
                bool near = worst?.FirstLossLocal == true;
                H(near ? "modem" : "provedor-perda", near ? "Perda de pacotes no seu modem/roteador" : "Perda de pacotes no provedor ou na internet", near ? "Seu modem / roteador" : $"Seu provedor ({e.Isp})",
                  55 + (e.InetLoss >= 10 ? 20 : 0) + (worst != null ? 15 : 0), e.InetLoss >= 10 ? Sev.Error : Sev.Warn,
                  "A rede local está saudável, mas pacotes se perdem no caminho para a internet. Isso causa travadas em chamadas, jogos e vídeos.",
                  near ? new[] { "Reinicie o modem e o roteador.", "Se houver modem e roteador separados, teste ligar o PC direto no modem.", "Verifique o cabo entre modem e roteador." }
                       : new[] { "Reinicie o modem e teste novamente em outro horário (pode ser congestionamento).", "Salve o relatório e envie ao suporte do provedor, mostrando o salto onde a perda começa.", "Se a perda for constante, peça visita técnica (sinal/cabeamento)." },
                  inetTxt, worst != null ? $"Perda começa no salto {worst.FirstLossHop} ({worst.FirstLossAddr}{(worst.FirstLossOrg != "" ? " — " + worst.FirstLossOrg : "")}) rumo a {worst.Name} e continua até o destino." : "");
            }
            if (!local && inetOk && e.InetAvg > 100 && e.InetLoss < 3)
                H("provedor-lento", "Tempo de resposta alto até a internet", $"Seu provedor ({e.Isp})", 60, Sev.Warn,
                  "A rede local está boa, mas os destinos levam muito tempo para responder (esperado abaixo de 50 ms para servidores próximos).",
                  new[] { "Teste em outro horário (noite costuma congestionar).", "Verifique se há downloads ou streaming no momento.", "Se persistir, reclame ao provedor com o relatório." }, inetTxt);
            // Destinos específicos (comparação entre vários destinos)
            var good = e.Inet.Where(d => d.Reachable && d.Row.Loss < 10).ToList(); // 1 perda isolada em 15 testes não caracteriza falha
            if (!local && good.Count >= 1)
                foreach (var d in e.Inet.Where(d => !good.Contains(d)))
                {
                    if (!d.Resolved) { H("nome-" + d.Name, $"O nome “{d.Name}” não foi encontrado", "O próprio endereço digitado ou o DNS", 80, Sev.Warn, "O nome não pôde ser traduzido em IP, enquanto outros destinos funcionam.", new[] { "Confira se digitou o endereço corretamente.", "Teste trocar o DNS (veja recomendação de DNS, se houver)." }, $"Falha ao resolver {d.Name}."); continue; }
                    var nome = d.Name;
                    H("destino-" + nome, $"Problema específico com {nome}", $"O servidor {nome} ou a rota até ele", !d.Reachable || d.Row.Loss >= 30 ? 80 : 60, d.Reachable ? Sev.Warn : Sev.Error,
                      "Os outros destinos funcionam bem, então sua internet está OK. A falha ocorre só nesse destino: servidor fora do ar, bloqueio de ping, ou problema na rota até ele.",
                      new[] { "Teste acessar o destino de outra rede (ex.: 4G do celular) para confirmar.", "Se for um serviço seu/da empresa, avise o responsável.", d.FirstLossHop > 0 ? $"A perda começa no salto {d.FirstLossHop} ({d.FirstLossAddr}) — informe isso ao responsável pela rede." : "Veja a rota na seção de detalhes técnicos." },
                      $"Perda {d.Row.Loss:F0}%, " + (d.Reachable ? "responde parcialmente" : "não respondeu") + $"; {good.Count} de {e.Inet.Count()} destinos estão normais.");
                }
            foreach (var d in e.Dests.Where(d => d.RouteLoop))
                H("loop-" + d.Name, $"Loop de roteamento no caminho até {d.Name}", "Seu provedor / operadora", 85, Sev.Error, "Os pacotes passam repetidamente pelos mesmos roteadores sem chegar ao destino.", new[] { "Reporte à operadora com o relatório da rota." }, "O mesmo equipamento aparece em saltos não consecutivos.");
        }

        // DNS (só faz sentido se a internet por IP funciona)
        if (!noLocalConn && inetOk && e.DnsTested)
        {
            bool sysFail = !e.SysDnsOk && (e.SysDnsCount > 0 || !e.ResolverOk);
            if (sysFail && e.PubDnsOk)
                H("dns-falha", "O DNS do seu provedor/roteador está falhando", "Seu provedor / roteador (DNS)", 93, Sev.Error,
                  "A internet funciona por número IP, mas o servidor DNS configurado não traduz nomes de sites. Por isso os sites não abrem, embora o Wi-Fi mostre conectado.",
                  new[] { "Troque o DNS do computador para 1.1.1.1 e 8.8.8.8 (Configurações > Rede > Propriedades do adaptador > DNS).", "Ou troque o DNS nas configurações do roteador.", "No Prompt de Comando, execute: ipconfig /flushdns" },
                  "Servidores DNS do sistema não responderam.", "DNS públicos (1.1.1.1 / 8.8.8.8) responderam normalmente.");
            else if (sysFail && !e.PubDnsOk)
                H("dns-bloq", "Consultas DNS bloqueadas (porta 53)", "Firewall / provedor / rede da empresa", 80, Sev.Error,
                  "Nem os DNS públicos responderam, apesar de haver internet por IP. Firewall, antivírus ou a rede estão bloqueando consultas DNS.",
                  new[] { "Desative temporariamente o firewall/antivírus para testar.", "Se estiver em rede corporativa, fale com o TI.", "Teste DNS sobre HTTPS no navegador." }, "Nenhum servidor DNS respondeu.");
            else if (e.SysDnsOk && e.SysDnsAvg > 120 && e.PubDnsAvg >= 0 && e.PubDnsAvg < e.SysDnsAvg * 0.6)
                H("dns-lento", "DNS lento deixando os sites demorados para abrir", "Seu provedor (DNS)", 75, Sev.Warn,
                  "O DNS configurado leva bastante tempo para responder, o que atrasa o início de cada site.",
                  new[] { "Troque o DNS para 1.1.1.1 / 8.8.8.8 ou 9.9.9.9." }, $"DNS atual responde em {e.SysDnsAvg:F0} ms; DNS público em {e.PubDnsAvg:F0} ms.");
            if (e.SysNx && e.PubDnsOk)
                H("dns-falso", "O DNS está retornando respostas incorretas", "Seu provedor / roteador (DNS)", 80, Sev.Warn,
                  "O DNS atual diz que um site conhecido não existe, enquanto DNS públicos o encontram. Pode ser filtro, falha ou redirecionamento indevido.", new[] { "Troque o DNS para 1.1.1.1 / 8.8.8.8.", "Verifique malware que altere o DNS." }, $"{e.DnsDomain}: NXDOMAIN no DNS do sistema.");
        }

        // HTTPS
        if (!noLocalConn && inetOk && e.HttpTotal > 0 && e.HttpOk == 0 && !hyps.Any(h => h.Id.StartsWith("dns-")))
        {
            if (e.TlsErrors)
                H("tls", "Erro de certificado de segurança ao abrir sites", "Seu computador (data/hora) ou antivírus", 85, Sev.Error,
                  "A conexão é estabelecida, mas o certificado dos sites é recusado. Causas comuns: data/hora do PC errada ou antivírus/proxy interceptando a conexão.",
                  new[] { "Corrija a data, hora e fuso do Windows e sincronize.", "Desative a verificação HTTPS do antivírus para testar.", "Remova proxy desconhecido." }, "Falhas de TLS/certificado em todos os sites testados.");
            else
                H("http-bloq", "Sites não abrem, apesar de a conexão estar boa", "Seu computador (proxy/firewall/antivírus)", 80 + (e.ProxyActive ? 8 : 0), Sev.Error,
                  "Ping e DNS funcionam, mas as páginas web não carregam. Costuma ser proxy configurado, firewall ou antivírus bloqueando navegação.",
                  new[] { "Desative proxy em Configurações > Rede > Proxy.", "Desative temporariamente antivírus/firewall para testar.", "Teste outro navegador." },
                  e.ProxyActive ? "Há um proxy configurado no Windows." : "", $"0 de {e.HttpTotal} sites carregaram.");
        }

        // Qualidade (bufferbloat / velocidade / MTU / retransmissão)
        if (!noLocalConn)
        {
            if (e.IdleAvg is double ia && e.LoadedAvg is double la && la - ia >= 100)
                H("bufferbloat", "A internet “trava” quando alguém usa muita banda (bufferbloat)", "Seu roteador (configuração de fila)", 85, la - ia >= 250 ? Sev.Error : Sev.Warn,
                  "Quando a conexão é usada (downloads, vídeos), o tempo de resposta explode. Isso causa lag em jogos e cortes em chamadas, mesmo com velocidade boa.",
                  new[] { "Ative QoS/SQM (Smart Queue Management / fq_codel / CAKE) no roteador.", "Limite a banda do roteador a ~90% da velocidade contratada.", "Priorize chamadas e jogos nas configurações de QoS." },
                  $"Tempo parado: {ia:F0} ms → durante download: {la:F0} ms (+{la - ia:F0} ms).");
            if (e.SpeedMbps is double sp && sp < 10 && inetOk)
                H("velocidade", "Velocidade de download baixa", e.IsWifi && e.WifiSignal is < 60 ? "Seu Wi-Fi" : "Seu provedor / plano", sp < 3 ? 80 : 60, sp < 3 ? Sev.Error : Sev.Warn,
                  "O download medido está bem abaixo do que a maioria dos planos entrega.",
                  new[] { "Teste por cabo para eliminar o Wi-Fi.", "Feche aparelhos que estejam baixando.", "Compare com o plano contratado; se estiver bem abaixo, acione o provedor." }, $"Download medido: {sp:F1} Mbps.");
            if (e.Mtu is int mt && mt < 1400)
                H("mtu", "Tamanho de pacote (MTU) incorreto", "Seu modem/roteador/VPN", 70, Sev.Warn, "Pacotes grandes não passam sem serem quebrados, causando lentidão e sites que abrem pela metade.", new[] { "Defina MTU 1500 (ou 1492 em PPPoE) no roteador.", "Desative a VPN para testar." }, $"MTU do caminho: {mt}.");
            if (e.TcpRetransPct >= 3 && !hyps.Any(h => h.Severity >= Sev.Warn && h.Id is "wifi-fraco" or "wifi-interf" or "cabo" or "roteador" or "provedor-perda" or "modem"))
                H("retrans", "O Windows detectou perdas intermitentes de pacotes", "Rede em geral", 50, Sev.Warn, "As retransmissões TCP estão altas, indicando perda de dados em algum momento, mesmo que o teste atual não tenha pego.", new[] { "Repita a verificação por mais tempo, em horários de problema.", "Teste por cabo." }, $"{e.TcpRetransPct:F1}% dos segmentos TCP foram reenviados desde que o PC ligou.");
            if (e.Inet.Any() && e.PingOk == 0 && e.TcpOk > 0)
                H("icmp", "Ping está bloqueado, mas a internet funciona", "Provedor / firewall", 90, Sev.Info, "Seu provedor ou firewall filtra ICMP (ping). Medições de perda por ping podem não ser confiáveis, mas as conexões reais funcionam.", Array.Empty<string>(), "Ping falhou em todos os destinos; TCP funcionou.");
        }
        if (!noLocalConn && e.Cgnat && e.Nat != null)
            H("cgnat", e.Nat.Confirmed ? "Seu provedor usa CGNAT (IP público compartilhado)" : "Provável CGNAT do provedor (não confirmado)", $"Seu provedor ({e.Isp})", e.Nat.Confirmed ? 90 : 55, Sev.Info,
              e.Nat.Summary + " Não causa lentidão, mas impede abrir portas, acessar o PC de fora e hospedar servidores/jogos.",
              new[] { "Se precisar de acesso remoto/servidor, peça ao provedor um IP público (fixo ou dinâmico real).", "Alternativa: use VPN ou túnel (ex.: Tailscale, Cloudflare Tunnel)." }, string.Join(" ", e.Nat.Evidence));
        if (!noLocalConn && e.DoubleNat && !e.Cgnat && e.Nat != null)
            H("nat-duplo", e.Nat.Confirmed ? "NAT duplo (dois equipamentos fazendo NAT)" : "Possível NAT adicional (não confirmado)", "Seu modem / roteador", e.Nat.Confirmed ? 85 : 55, Sev.Info,
              e.Nat.Summary + " Pode atrapalhar jogos online, VoIP e redirecionamento de portas.",
              new[] { "Coloque o modem do provedor em modo bridge (ponte) e deixe só o seu roteador fazendo NAT.", "Ou configure o roteador secundário como ponto de acesso (sem NAT/DHCP)." }, string.Join(" ", e.Nat.Evidence));
        if (e.VpnActive && hyps.Any(h => h.Severity >= Sev.Warn))
            H("vpn", "Há uma VPN ativa que pode estar influenciando", "Sua VPN", 45, Sev.Info, "VPNs adicionam latência e podem causar perdas ou bloqueios.", new[] { "Desconecte a VPN e repita a verificação." }, "Adaptador de VPN/túnel ativo.");

        if (!hyps.Any(h => h.Severity >= Sev.Warn))
        {
            var ok = H("ok", "Sua conexão está saudável", "Nenhum — tudo certo", 90, Sev.Ok,
                "Todos os testes foram concluídos sem sinais de problema nas partes avaliadas.", Array.Empty<string>(),
                gwTxt, inetTxt, e.SpeedMbps is double s ? $"Velocidade de download: {s:F1} Mbps." : "");
            hyps.Remove(ok); hyps.Insert(0, ok);
        }
        else hyps.Sort((a, b) => a.Severity != b.Severity ? b.Severity.CompareTo(a.Severity) : b.Confidence.CompareTo(a.Confidence));

        return new Report { Hyps = hyps, Stages = BuildStages(e, local, gwSilent, inetOk), Narrative = BuildNarrative(e, hyps[0], inetOk) };
    }

    static List<Stage> BuildStages(Evidence e, bool local, bool gwSilent, bool inetOk)
    {
        var st = new List<Stage>();
        bool noConn = !e.HasPrimary;
        var pc = new Stage { Icon = "💻", Name = "Seu computador", State = StageState.Ok, Detail = "Placa de rede e IP normais" };
        if (noConn || e.Apipa) { pc.State = StageState.Error; pc.Detail = e.Apipa ? "Sem IP válido (DHCP)" : e.NoGateway ? "Sem gateway" : "Sem conexão"; }
        else if (e.AdapterErrPct >= 1 || e.TcpRetransPct >= 3) { pc.State = StageState.Warn; pc.Detail = e.AdapterErrPct >= 1 ? "Pacotes com erro" : "Muitas retransmissões"; }
        st.Add(pc);
        var link = new Stage { Icon = e.IsWifi ? "📶" : "🔌", Name = e.IsWifi ? "Wi-Fi" : "Cabo de rede", State = StageState.Unknown, Detail = "Sem dados" };
        if (!noConn)
        {
            if (e.IsWifi && e.WifiSignal is int sg) { link.State = sg < 40 ? StageState.Error : sg < 65 ? StageState.Warn : StageState.Ok; link.Detail = $"Sinal {sg}%"; }
            else if (e.IsEthernet && e.LinkMbps > 0) { link.State = e.LinkMbps < 100 ? StageState.Error : e.LinkMbps == 100 ? StageState.Warn : StageState.Ok; link.Detail = $"{e.LinkMbps} Mbps"; }
        }
        st.Add(link);
        var rt = new Stage { Icon = "📡", Name = "Roteador", State = StageState.Unknown, Detail = "Não avaliado" };
        if (e.GwTested)
        {
            if (gwSilent) { rt.State = StageState.Unknown; rt.Detail = "Não responde a ping (normal)"; }
            else if (e.GwRecv == 0) { rt.State = StageState.Error; rt.Detail = "Não responde"; }
            else if (e.GwLoss >= 5) { rt.State = StageState.Error; rt.Detail = $"Perda {e.GwLoss:F0}%"; }
            else if (local) { rt.State = StageState.Warn; rt.Detail = $"{e.GwAvg:F0} ms, oscila {e.GwJitter:F0} ms"; }
            else { rt.State = StageState.Ok; rt.Detail = $"{e.GwAvg:F0} ms, sem perda"; }
        }
        st.Add(rt);
        var pv = new Stage { Icon = "🏢", Name = "Provedor", State = StageState.Unknown, Detail = "Não avaliado" };
        if (e.Inet.Any() && !noConn)
        {
            if (!inetOk) { pv.State = local ? StageState.Unknown : StageState.Error; pv.Detail = local ? "Corrija a rede local antes" : "Sem saída para a internet"; }
            else if (e.InetLoss >= 3) { pv.State = e.InetLoss >= 10 ? StageState.Error : StageState.Warn; pv.Detail = $"Perda {e.InetLoss:F1}%"; }
            else if (e.InetAvg > 100) { pv.State = StageState.Warn; pv.Detail = $"Lento: {e.InetAvg:F0} ms"; }
            else { pv.State = StageState.Ok; pv.Detail = $"{e.InetAvg:F0} ms, perda {e.InetLoss:F1}%"; }
        }
        st.Add(pv);
        var dn = new Stage { Icon = "🔎", Name = "DNS", State = StageState.Unknown, Detail = "Não avaliado" };
        if (e.DnsTested && inetOk)
        {
            bool fail = !e.SysDnsOk && (e.SysDnsCount > 0 || !e.ResolverOk);
            if (fail) { dn.State = StageState.Error; dn.Detail = "Servidor não responde"; }
            else if (e.SysDnsAvg > 120) { dn.State = StageState.Warn; dn.Detail = $"Lento: {e.SysDnsAvg:F0} ms"; }
            else { dn.State = StageState.Ok; dn.Detail = e.SysDnsAvg >= 0 ? $"{e.SysDnsAvg:F0} ms" : "Resolvendo nomes"; }
        }
        st.Add(dn);
        var web = new Stage { Icon = "🌍", Name = "Sites e destinos", State = StageState.Unknown, Detail = "Não avaliado" };
        if (e.Inet.Any() && inetOk)
        {
            int bad = e.Inet.Count(d => !d.Reachable || d.Row.Loss >= 10);
            if (e.HttpTotal > 0 && e.HttpOk == 0) { web.State = StageState.Error; web.Detail = "Páginas não abrem"; }
            else if (bad > 0) { web.State = StageState.Warn; web.Detail = $"{bad} de {e.Inet.Count()} com problema"; }
            else { web.State = StageState.Ok; web.Detail = $"{e.Inet.Count()} destinos normais"; }
        }
        st.Add(web);
        var q = new Stage { Icon = "⚡", Name = "Velocidade", State = StageState.Unknown, Detail = "Não avaliado" };
        if (e.SpeedMbps is double sp)
        {
            bool bb = e.IdleAvg is double ia && e.LoadedAvg is double la && la - ia >= 100;
            q.State = sp < 3 ? StageState.Error : sp < 10 || bb ? StageState.Warn : StageState.Ok;
            q.Detail = $"{sp:F0} Mbps" + (bb ? ", lag sob carga" : "");
        }
        st.Add(q);
        return st;
    }

    static string BuildNarrative(Evidence e, Hypothesis top, bool inetOk)
    {
        var sb = new StringBuilder();
        if (e.HasPrimary)
            sb.Append(e.IsWifi ? $"Seu computador está no Wi-Fi{(e.WifiSignal is int s ? $" (sinal {s}%)" : "")}. " : $"Seu computador está ligado por cabo{(e.LinkMbps > 0 ? $" a {e.LinkMbps} Mbps" : "")}. ");
        else sb.Append("Seu computador não está conectado a uma rede utilizável. ");
        if (e.GwTested)
            sb.Append(e.GwRecv == 0 ? "O roteador não respondeu ao ping. " : $"O roteador respondeu com {e.GwLoss:F0}% de perda e {e.GwAvg:F0} ms. ");
        if (e.Pub.Ok)
            sb.Append($"Seu IP público é {e.Pub.Ip}{(e.Pub.IspName != "" ? $" (provedor {e.Pub.IspName}" + (e.Pub.Asn != "" ? $", {e.Pub.Asn}" : "") + ")" : "")}{(e.Pub.Place != "" ? $", localização aproximada: {e.Pub.Place}" : "")}. ");
        if (e.Inet.Any())
            sb.Append(inetOk ? $"Foram testados {e.Inet.Count()} destino(s): {e.PingOk} respondem a ping, perda média {e.InetLoss:F1}% e tempo médio {e.InetAvg:F0} ms. " : "Nenhum destino da internet respondeu. ");
        if (e.DnsTested) sb.Append(e.SysDnsOk || e.ResolverOk ? "O DNS está traduzindo nomes. " : "O DNS não está traduzindo nomes. ");
        if (e.HttpTotal > 0) sb.Append($"{e.HttpOk} de {e.HttpTotal} sites abriram. ");
        if (e.SpeedMbps is double sp) sb.Append($"A velocidade de download medida foi {sp:F1} Mbps. ");
        sb.Append("\n\nConclusão: ").Append(top.Severity == Sev.Ok ? "não encontrei problemas." : $"o problema mais provável é “{top.Title.ToLowerInvariant()}” ({top.Confidence}% de certeza). {top.Summary}");
        return sb.ToString();
    }
}
