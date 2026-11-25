using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
namespace SuiteRedes;

/// <summary>Resultado de uma verificação da rede local. Só existe se foi medido; sempre traz a evidência.</summary>
public sealed class Check
{
    public string Id = "", Area = "", Title = "", Detail = "", Evidence = "", Fix = "";
    public Sev Sev; public DateTime At = DateTime.Now;
}

/// <summary>
/// Diagnóstico global da infraestrutura local, em threads separadas da análise de rotas:
/// • thread 1: ping contínuo ao roteador (1/s) — separa problema da sua rede x problema da internet;
/// • thread 2: a cada 60 s verifica adaptador/Wi-Fi, configuração IP, DNS (falha, lentidão, sequestro de NXDOMAIN),
///   portal cativo/interceptação HTTP, HTTPS, MTU, IPv6 e proxy.
/// Regra: nenhuma conclusão sem medição; cada item mostra o que foi medido.
/// </summary>
public partial class MainWindow
{
    readonly Dictionary<string, Check> checks = new();
    readonly StatRow gwRow = new() { Title = "Roteador" };
    IPAddress? gwIp; bool healthMode; DateTime? lastHealthRun;
    bool wifi; int? mtu;

    void SetCheck(string id, string area, Sev sev, string title, string detail, string evidence, string fix = "")
    {
        int g0 = gen;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (g0 != gen) return;
            bool changed = !checks.TryGetValue(id, out var old) || old.Sev != sev;
            checks[id] = new Check { Id = id, Area = area, Sev = sev, Title = title, Detail = detail, Evidence = evidence, Fix = fix };
            if (changed && old != null && Routes.Count > 0 && (sev >= Sev.Warn || old.Sev >= Sev.Warn))
                Events.Add(new EventRec { T = DateTime.Now, Route = HealthRoute, Sev = sev >= Sev.Warn ? sev : Sev.Ok, Text = $"{area}: {title}" });
        }));
    }
    void DropCheck(string id) => Dispatcher.BeginInvoke(new Action(() => checks.Remove(id)));
    readonly RouteVm HealthRoute = new() { Host = "rede-local", Label = "Rede local" };

    void StartHealth(CancellationToken ct)
    {
        checks.Clear(); gwRow.Reset(); mtu = null;
        _ = Task.Run(() => GatewayLoop(ct));
        _ = Task.Run(() => HealthLoop(ct));
    }

    // ---------- thread 1: roteador ----------
    async Task GatewayLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (_, gw) = Diagnostics.Primary(); gwIp = gw;
                if (gw == null) { await Task.Delay(2000, ct); continue; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var p = await Net.ProbeAsync(gw, 1000, 128, 32, false, ct);
                await Dispatcher.BeginInvoke(new Action(() => { gwRow.Address = gw.ToString(); gwRow.Record(p.Status == IPStatus.Success ? p.Rtt : null, p.Status.ToString(), false); }));
                await Task.Delay(Math.Max(0, 1000 - (int)sw.ElapsedMilliseconds), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>Avalia a qualidade até o roteador (janela dos últimos 60 s). Roda na UI a cada atualização.</summary>
    void EvalGateway()
    {
        if (gwIp == null || gwRow.Sent < 10) return;
        double loss = RecentLoss(gwRow, 60), avg = RecentAvg(gwRow, 60);
        var tl = Tail(gwRow, 60); var diffs = new List<double>();
        for (int q = 1; q < tl.Count; q++) if (tl[q] is long b1 && tl[q - 1] is long a1) diffs.Add(Math.Abs(b1 - a1));
        double jit = diffs.Count == 0 ? 0 : diffs.Average();
        string ev = $"Roteador {gwIp}: perda {loss:F0}%, média {(avg < 0 ? "—" : avg.ToString("F0") + " ms")}, oscilação {jit:F0} ms (últimos 60 s, {gwRow.Sent} testes no total).";
        bool anyRouteOk = Routes.Any(r => r.Dest != null && r.Dest.Received > 0);
        bool routesLossy = Routes.Any(r => r.Dest != null && r.Dest.Sent >= 10 && RecentLoss(r.Dest, 60) >= 2);
        if ((gwRow.Received == 0 && gwRow.Sent >= 10) || (loss >= 99 && anyRouteOk && !routesLossy))
        {
            if (anyRouteOk) SetCheck("gw", "Rede local", Sev.Info, "O roteador não responde a ping", "O roteador não responde ao ping, mas os destinos na internet respondem: o caminho pelo roteador funciona. A qualidade só até o roteador não pôde ser medida.", ev);
            else SetCheck("gw", "Rede local", Sev.Error, "O roteador não responde", "Nem o roteador nem a internet respondem: a falha está entre este computador e o roteador.", ev, "Verifique o cabo ou a conexão Wi-Fi; reinicie o roteador.");
            return;
        }
        double lim = wifi ? 30 : 10;
        if (loss >= 10) SetCheck("gw", "Rede local", Sev.Error, $"Perda de {loss:F0}% dentro da sua rede", routesLossy ? "Os pacotes se perdem entre este computador e o roteador. Os destinos também apresentam perda nos últimos 60 s." : "Os pacotes de teste se perdem entre este computador e o roteador. Os destinos não apresentam perda no mesmo período.", ev, wifi ? "Aproxime-se do roteador, troque para 5 GHz ou teste com cabo." : "Troque o cabo de rede e a porta do roteador.");
        else if (loss >= 2) SetCheck("gw", "Rede local", Sev.Warn, $"Perda de {loss:F0}% até o roteador", routesLossy ? "Pacotes se perdem entre este computador e o roteador. Os destinos também apresentam perda nos últimos 60 s." : "Pacotes de teste se perdem entre este computador e o roteador. Os destinos não apresentam perda no mesmo período.", ev, wifi ? "Verifique o sinal do Wi-Fi e interferências (micro-ondas, paredes, outras redes no mesmo canal)." : "Verifique o cabo e a porta do roteador.");
        else if (avg > lim) SetCheck("gw", "Rede local", Sev.Warn, $"Roteador lento para responder ({avg:F0} ms)", $"Média de {avg:F0} ms só até o roteador, acima do limite de {lim:F0} ms usado para rede {(wifi ? "Wi-Fi" : "cabeada")}.", ev, "Reinicie o roteador; se usar Wi-Fi, teste perto dele ou com cabo.");
        else if (jit > 15) SetCheck("gw", "Rede local", Sev.Warn, $"Oscilação de {jit:F0} ms até o roteador", $"A latência até o roteador varia em média {jit:F0} ms de um teste para o outro.", ev, "Teste com cabo para confirmar; troque o canal do Wi-Fi.");
        else SetCheck("gw", "Rede local", Sev.Ok, "Conexão com o roteador estável", "Sem perda e com resposta rápida dentro da sua rede.", ev);
    }

    // ---------- thread 2: verificações periódicas ----------
    async Task HealthLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await RunChecks(ct);
                Dispatcher.Invoke(() => lastHealthRun = DateTime.Now);
                await Task.Delay(60000, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log(ex); }
    }

    async Task RunChecks(CancellationToken ct)
    {
        var (nic, gw) = Diagnostics.Primary();
        // adaptador e configuração IP
        if (nic == null)
        {
            SetCheck("nic", "Adaptador", Sev.Error, "Nenhuma conexão de rede ativa", "Não há adaptador com endereço e roteador configurados.", "Nenhum adaptador ativo com gateway IPv4.", "Conecte o cabo ou o Wi-Fi.");
            return;
        }
        var ipv4 = nic.GetIPProperties().UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
        wifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
        long mbps = nic.Speed / 1_000_000;
        string nicEv = $"{nic.Description} — {(wifi ? "Wi-Fi" : "cabo")}, IP {ipv4}, roteador {gw}, velocidade do link {mbps} Mbps.";
        if (ipv4 != null && ipv4.ToString().StartsWith("169.254."))
            SetCheck("ip", "Configuração IP", Sev.Error, "O computador não recebeu endereço do roteador (DHCP)", "O IP 169.254.x.x é atribuído pelo próprio Windows quando o roteador não responde ao pedido de endereço.", nicEv, "Reinicie o roteador; no Prompt: ipconfig /release e ipconfig /renew.");
        else DropCheck("ip");
        var others = Diagnostics.Nics().Where(n => n.Id != nic.Id && n.OperationalStatus == OperationalStatus.Up && Diagnostics.GatewaysV4(n).Any()).ToList();
        if (others.Count > 0)
            SetCheck("multi", "Configuração IP", Sev.Info, $"Mais de uma conexão ativa com saída para a internet", "Há mais de um adaptador ativo com rota padrão para a internet.", "Também ativas: " + string.Join("; ", others.Select(o => o.Name + " (" + o.Description + ")")), "Desative a conexão que não estiver usando.");
        else DropCheck("multi");

        if (wifi)
        {
            var d = NetInfo.WifiDetails(Diagnostics.WifiInfo());
            var raw = Diagnostics.WifiInfo(); int sig = -1;
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"Signal : (\d+)%"); if (m.Success) sig = int.Parse(m.Groups[1].Value);
            string band = d.TryGetValue("band", out var b) ? b : "", rx = d.TryGetValue("rx", out var r) ? r : "", ch = d.TryGetValue("channel", out var c) ? c : "";
            string ev = $"Rede “{(d.TryGetValue("ssid", out var s) ? s : "?")}”, sinal {(sig >= 0 ? sig + "%" : "?")}, banda {band}, canal {ch}, taxa {rx} Mbps, padrão {(d.TryGetValue("radio", out var rd) ? rd : "?")}.";
            if (sig >= 0 && sig < 40) SetCheck("wifi", "Wi-Fi", Sev.Warn, $"Sinal do Wi-Fi fraco ({sig}%)", "Intensidade do sinal informada pelo Windows abaixo de 40%.", ev, "Aproxime-se do roteador, remova obstáculos ou use um repetidor/mesh.");
            else if (double.TryParse(rx, out var rxv) && rxv > 0 && rxv < 50) SetCheck("wifi", "Wi-Fi", Sev.Warn, $"Wi-Fi conectado a apenas {rxv:F0} Mbps", "A taxa negociada limita a velocidade máxima deste computador.", ev, band.StartsWith("2") ? "Use a rede 5 GHz se o roteador tiver." : "Aproxime-se do roteador.");
            else if (band.StartsWith("2")) SetCheck("wifi", "Wi-Fi", Sev.Info, "Wi-Fi na banda de 2,4 GHz", "Conectado na banda de 2,4 GHz.", ev, "Se o roteador oferecer 5 GHz, prefira essa rede.");
            else SetCheck("wifi", "Wi-Fi", Sev.Ok, "Wi-Fi com bom sinal", "Sinal e taxa de conexão adequados.", ev);
            DropCheck("link");
        }
        else
        {
            DropCheck("wifi");
            if (mbps > 0 && mbps < 100) SetCheck("link", "Adaptador", Sev.Warn, $"Cabo de rede negociando a {mbps} Mbps", $"A placa de rede negociou {mbps} Mbps com o roteador: este é o limite máximo de velocidade deste computador, independente do plano contratado.", nicEv, "Troque o cabo (Cat5e ou superior) e teste outra porta do roteador.");
            else SetCheck("link", "Adaptador", Sev.Ok, $"Conexão cabeada a {mbps} Mbps", "Link físico negociado normalmente.", nicEv);
        }

        // DNS
        await CheckDns(ct);
        // portal cativo / interceptação HTTP e HTTPS
        await CheckWeb(ct);
        // MTU (uma vez por teste)
        if (mtu == null)
        {
            mtu = await FindMtu(IPAddress.Parse("1.1.1.1"), ct);
            if (mtu is int mv)
            {
                string ev = $"Maior pacote que passa sem fragmentar até 1.1.1.1: {mv} bytes.";
                if (mv >= 1500) SetCheck("mtu", "MTU", Sev.Ok, "Tamanho de pacote (MTU) padrão: 1500", "Pacotes de tamanho máximo passam normalmente.", ev);
                else if (mv >= 1480) SetCheck("mtu", "MTU", Sev.Info, $"MTU de {mv} bytes", $"Pacotes de até {mv} bytes passam sem fragmentar ({1500 - mv} bytes abaixo do padrão 1500).", ev);
                else if (mv >= 1400) SetCheck("mtu", "MTU", Sev.Info, $"MTU reduzido ({mv} bytes)", "Pacotes maiores que esse tamanho não passam sem fragmentar.", ev);
                else SetCheck("mtu", "MTU", Sev.Warn, $"MTU muito baixo ({mv} bytes)", "Pacotes maiores que esse tamanho não passam sem fragmentar até a internet.", ev, "Verifique o MTU do roteador (PPPoE: 1492) e a VPN.");
            }
        }
        // IPv6
        bool hasV6 = nic.GetIPProperties().UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal && !u.Address.IsIPv6Teredo && !u.Address.IsIPv6SiteLocal && !u.Address.ToString().StartsWith("fd") && !u.Address.ToString().StartsWith("fc"));
        if (hasV6)
        {
            var t = await Tcp(IPAddress.Parse("2606:4700:4700::1111"), 443, 3000);
            if (t == null) SetCheck("v6", "IPv6", Sev.Warn, "IPv6 configurado, mas sem acesso à internet", "O adaptador tem endereço IPv6 global, mas a conexão IPv6 de teste não completou.", "Endereço IPv6 global presente; conexão a [2606:4700:4700::1111]:443 falhou.", "Corrija o IPv6 no roteador ou desative IPv6 no adaptador.");
            else SetCheck("v6", "IPv6", Sev.Ok, "IPv6 funcionando", "Conexões IPv6 saem normalmente.", $"Conexão IPv6 à Cloudflare em {t} ms.");
        }
        else SetCheck("v6", "IPv6", Sev.Info, "Sem IPv6", "Esta conexão usa só IPv4. Não é problema.", "Nenhum endereço IPv6 global no adaptador.");
        // proxy
        try
        {
            // proxy pela API de rede do .NET (sem ler o registro do Windows)
            var probe = new Uri("http://www.msftconnecttest.com/");
            var px = System.Net.WebRequest.GetSystemWebProxy().GetProxy(probe);
            if (px != null && px != probe) SetCheck("proxy", "Proxy", Sev.Info, "Proxy configurado no Windows", "O Windows está configurado para enviar o tráfego web por um proxy.", "Servidor: " + px.Authority, "Se não reconhece este proxy, desative em Configurações > Rede > Proxy.");
            else DropCheck("proxy");
        }
        catch { }
    }

    async Task CheckDns(CancellationToken ct)
    {
        var (pnic, _) = Diagnostics.Primary();
        var sys = pnic == null ? Diagnostics.SystemDns() : pnic.GetIPProperties().DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).Distinct().ToList(); if (sys.Count == 0) { SetCheck("dns", "DNS", Sev.Error, "Nenhum servidor DNS configurado", "Sem DNS o computador não converte nomes de sites em endereços.", "Lista de DNS do adaptador vazia.", "Configure DNS automático (DHCP) ou 1.1.1.1 / 8.8.8.8."); return; }
        var names = new[] { "www.google.com", "registro.br", "www.microsoft.com" };
        async Task<(int ok, double avg, int nx)> Q(string server)
        {
            int ok = 0, nx = 0; var ms = new List<double>();
            foreach (var n in names) { var r = await DnsClient.QueryAsync(server, n, "A", 2000, ct); if (r.Ok) { ok++; ms.Add(r.Ms); } else if (r.Status == "NXDOMAIN") nx++; }
            return (ok, ms.Count == 0 ? -1 : ms.Average(), nx);
        }
        var sysRes = new List<(string s, int ok, double avg, int nx)>();
        foreach (var s in sys.Take(3)) { var (ok, avg, nx) = await Q(s); sysRes.Add((s, ok, avg, nx)); }
        var (pok, pavg, _) = await Q("1.1.1.1");
        string ev = string.Join("; ", sysRes.Select(x => $"{x.s}: {x.ok}/{names.Length} respostas{(x.avg >= 0 ? $", {x.avg:F0} ms" : "")}")) + $". Referência 1.1.1.1: {pok}/{names.Length}{(pavg >= 0 ? $", {pavg:F0} ms" : "")}.";
        int good = sysRes.Count(x => x.ok >= names.Length - 1);   // 1 timeout isolado em 3 não caracteriza falha
        if (sysRes.All(x => x.ok == 0))
        {
            if (pok > 0) SetCheck("dns", "DNS", Sev.Error, "O DNS configurado não responde", "Os servidores DNS configurados não responderam a nenhuma consulta; o DNS público 1.1.1.1 respondeu.", ev, "Troque o DNS para 1.1.1.1 e 8.8.8.8 (no adaptador ou no roteador).");
            else SetCheck("dns", "DNS", Sev.Error, "Nenhum DNS responde", "Nem os servidores DNS configurados nem 1.1.1.1 responderam a consultas na porta 53.", ev, "Verifique firewall/antivírus e a conexão.");
        }
        else if (good < sysRes.Count) SetCheck("dns", "DNS", Sev.Warn, "Um dos servidores DNS falha", "Pelo menos um servidor DNS configurado não respondeu a todas as consultas.", ev, "Remova o servidor que falha ou use 1.1.1.1 / 8.8.8.8.");
        else
        {
            double sAvg = sysRes.Where(x => x.avg >= 0).Select(x => x.avg).DefaultIfEmpty(-1).Min();
            if (sAvg > 150 && pavg >= 0 && sAvg > pavg * 2) SetCheck("dns", "DNS", Sev.Warn, $"DNS lento ({sAvg:F0} ms)", $"O DNS configurado levou {sAvg:F0} ms por consulta; 1.1.1.1 levou {pavg:F0} ms.", ev, "Troque o DNS para 1.1.1.1 / 8.8.8.8.");
            else SetCheck("dns", "DNS", Sev.Ok, $"DNS respondendo ({sAvg:F0} ms)", "Os servidores DNS configurados respondem normalmente.", ev);
        }
        // sequestro de NXDOMAIN: nome inexistente deve dar NXDOMAIN; se o DNS do sistema devolve endereço, ele está redirecionando
        // nome fixo e legível em domínio reservado (RFC 6761): nada de nomes aleatórios, que imitam DGA de botnet
        const string fake = "suiteredes-teste-nxdomain.invalid";
        var sr = await DnsClient.QueryAsync(sys[0], fake, "A", 2500, ct); var pr = await DnsClient.QueryAsync("1.1.1.1", fake, "A", 2500, ct);
        if (sr.Ok && sr.Answers.Count > 0 && pr.Status == "NXDOMAIN")
            SetCheck("nx", "DNS", Sev.Warn, "O DNS redireciona endereços inexistentes", "Consultado um nome que não existe: o DNS configurado devolveu um endereço, enquanto 1.1.1.1 respondeu corretamente que o nome não existe.", $"{fake} → {string.Join(", ", sr.Answers.Select(a => a.Data))} pelo DNS {sys[0]}; 1.1.1.1 responde NXDOMAIN.", "Use 1.1.1.1 / 8.8.8.8 e verifique o DNS configurado no roteador.");
        else DropCheck("nx");
    }

    async Task CheckWeb(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(6) };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var resp = await http.GetAsync("http://www.msftconnecttest.com/connecttest.txt", ct); var body = await resp.Content.ReadAsStringAsync();
            string ev = $"GET http://www.msftconnecttest.com/connecttest.txt → HTTP {(int)resp.StatusCode}, {sw.ElapsedMilliseconds} ms, conteúdo {(body.Length > 40 ? body.Substring(0, 40) + "…" : body)}";
            if (body.Trim() == "Microsoft Connect Test") SetCheck("portal", "Internet", Sev.Ok, "Acesso à internet sem interceptação", "O teste de conectividade do Windows retornou exatamente o esperado.", ev);
            else SetCheck("portal", "Internet", Sev.Warn, "Página de login ou interceptação detectada", "O endereço de teste da Microsoft não devolveu o conteúdo esperado: a resposta foi substituída no caminho.", ev, "Abra o navegador e faça login na rede, se for o caso.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { SetCheck("portal", "Internet", Sev.Warn, "Teste de conectividade do Windows falhou", "Não foi possível acessar o endereço de teste da Microsoft.", ex.Message); }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var r = await http.GetAsync("https://www.google.com/generate_204", ct);
            SetCheck("https", "Internet", Sev.Ok, $"HTTPS funcionando ({sw.ElapsedMilliseconds} ms)", "Conexão segura com certificado válido.", $"GET https://www.google.com/generate_204 → HTTP {(int)r.StatusCode} em {sw.ElapsedMilliseconds} ms (inclui DNS, conexão e TLS).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message; bool cert = msg.IndexOf("certific", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("SSL", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("TLS", StringComparison.OrdinalIgnoreCase) >= 0;
            SetCheck("https", "Internet", Sev.Error, cert ? "Erro de certificado em conexões seguras" : "Conexões HTTPS falham", cert ? $"O certificado apresentado não foi aceito. Relógio do Windows: {DateTime.Now:dd/MM/yyyy HH:mm}." : "Não foi possível abrir uma conexão segura com www.google.com.", msg, cert ? "Confira data e hora do Windows; desative a inspeção HTTPS do antivírus para testar." : "");
        }
    }

    static async Task<int?> FindMtu(IPAddress ip, CancellationToken ct)
    {
        if ((await Net.ProbeAsync(ip, 2000, 128, 32, true, ct)).Status != IPStatus.Success) return null;
        int lo = 32, hi = 1472, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2; bool ok = false;
            for (int t = 0; t < 3 && !ok; t++) ok = (await Net.ProbeAsync(ip, 2000, 128, mid, true, ct)).Status == IPStatus.Success;
            if (ok) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return best < 0 ? null : best + 28;
    }

    static async Task<long?> Tcp(IPAddress ip, int port, int timeout)
    {
        try
        {
            using var c = new TcpClient(ip.AddressFamily); var sw = System.Diagnostics.Stopwatch.StartNew(); var t = c.ConnectAsync(ip, port); _ = t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return await Task.WhenAny(t, Task.Delay(timeout)) == t && !t.IsFaulted && c.Connected ? sw.ElapsedMilliseconds : null;
        }
        catch { return null; }
    }

    /// <summary>Indícios de NAT a partir das rotas: CGNAT (100.64/10) e endereços privados após o roteador.</summary>
    void EvalNat()
    {
        var hops = Routes.Where(r => r.Phase == "Ao vivo").SelectMany(r => r.Hops.Take(4)).Where(h => h.Index >= 2 && h.Index <= 4 && h.Address is not ("*" or "?")).ToList();
        if (hops.Count == 0) return;
        var cg = hops.FirstOrDefault(h => NetInfo.IsCgnat(h.Address));
        bool pubPriv = pubInfo?.Ok == true && (NetInfo.IsPrivate(pubInfo.Ip) || NetInfo.IsCgnat(pubInfo.Ip));
        if (cg != null) SetCheck("nat", "NAT", Sev.Info, "Endereço da faixa CGNAT na rota", "A rota passa por um endereço da faixa 100.64.0.0/10, reservada pela RFC 6598 para CGNAT (endereços compartilhados entre clientes do provedor).", $"Salto {cg.Index} da rota é {cg.Address} (faixa 100.64.0.0/10).");
        else
        {
            var p2 = hops.Where(h => h.Index == 2 && NetInfo.IsPrivate(h.Address)).Select(h => h.Address).Distinct().ToList();
            if (p2.Count > 0) SetCheck("nat", "NAT", Sev.Info, "Rede privada logo após o seu roteador", "O segundo ponto da rota usa endereço privado: o tráfego passa por outra rede interna antes de chegar à internet.", $"Salto 2 = {string.Join(", ", p2)} (faixa privada RFC 1918).");
            else DropCheck("nat");
        }
    }

    // ---------- tela ----------
    void Health_Click(object s, MouseButtonEventArgs e) { healthMode = true; LstRoutes.SelectedItem = null; DrawSelected(); }

    void UpdateHealthEntry()
    {
        var list = checks.Values.ToList(); int bad = list.Count(c => c.Sev >= Sev.Warn); bool err = list.Any(c => c.Sev == Sev.Error);
        HealthDot.Fill = list.Count == 0 ? Pal.Gray : err ? Pal.Err : bad > 0 ? Pal.Warn : Pal.Ok;
        TxtHealthSub.Text = list.Count == 0 ? (Running ? "Analisando…" : "Inicie o teste para analisar") : bad == 0 ? $"{list.Count} verificações, nenhum problema" : $"{bad} problema(s) em {list.Count} verificações";
        TxtHealthSub.Foreground = bad == 0 ? Res("T3") : err ? Pal.Err : Pal.Warn;
        HealthEntry.Background = healthMode ? Res("BgCard2") : Brushes.Transparent;
        HealthEntry.BorderBrush = healthMode ? Res("Accent") : Brushes.Transparent;
    }

    void DrawHealth()
    {
        TxtDest.Text = "Diagnóstico da rede local";
        TxtDestSub.Text = $"Roda em paralelo às rotas. Ping ao roteador a cada segundo; demais verificações a cada 60 s{(lastHealthRun is DateTime t ? $" (última às {t:HH:mm:ss})" : "")}.";
        var list = checks.Values.OrderByDescending(c => c.Sev == Sev.Error ? 3 : c.Sev == Sev.Warn ? 2 : c.Sev == Sev.Info ? 0 : 1).ThenBy(c => c.Area).ToList();
        var worst = list.Select(c => c.Sev).DefaultIfEmpty(Sev.Ok).Max(); int bad = list.Count(c => c.Sev >= Sev.Warn);
        bool waiting = list.Count == 0;
        TxtVerdict.Text = waiting ? "Analisando a rede local…" : bad == 0 ? "Nenhum problema encontrado na sua rede local, DNS ou configuração." : $"{bad} problema(s) encontrado(s). " + (checks.TryGetValue("gw", out var g) && g.Sev >= Sev.Warn ? "Há problema dentro da sua rede local: ele afeta todos os destinos." : "Veja abaixo o que foi medido e como resolver.");
        VerdictIcon.Background = waiting ? Pal.Gray : SevBrush(bad == 0 ? Sev.Ok : worst); TxtVerdictIcon.Text = waiting ? "…" : bad == 0 ? "✓" : worst == Sev.Error ? "✕" : "!";
        VerdictBox.Background = waiting ? Res("BgCard") : BgFor(bad == 0 ? Sev.Ok : worst);
        PnlStats.Children.Clear();
        bool gok = gwRow.Received > 0;
        Tile(PnlStats, gok ? $"{RecentAvg(gwRow, 60):F0}" : "—", "ms", "até o roteador", null, 30);
        Tile(PnlStats, gwRow.Sent > 0 ? $"{RecentLoss(gwRow, 60):F0}" : "—", "%", "perda local (60 s)", gwRow.Sent >= 10 && RecentLoss(gwRow, 60) >= 2 ? Pal.Err : null, 30);

        var hl = HealthList; hl.Children.Clear();
        foreach (var c in list)
        {
            var col = c.Sev switch { Sev.Error => Pal.Err, Sev.Warn => Pal.Warn, Sev.Info => Pal.Gray, _ => Pal.Ok };
            var bd = new Border { Background = Res("BgCard"), CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(0, 0, 0, 10), BorderBrush = c.Sev >= Sev.Warn ? col : Brushes.Transparent, BorderThickness = new Thickness(c.Sev >= Sev.Warn ? 3 : 0, 0, 0, 0) };
            var sp = new StackPanel();
            var head = new DockPanel();
            var area = new TextBlock { Text = c.Area, Foreground = Res("T3"), FontSize = 12.5 }; DockPanel.SetDock(area, Dock.Right); head.Children.Add(area);
            var icon = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = col, Margin = new Thickness(0, 0, 10, 0), Child = new TextBlock { Text = c.Sev switch { Sev.Error => "✕", Sev.Warn => "!", Sev.Info => "i", _ => "✓" }, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Res("BgApp"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            head.Children.Add(icon);
            head.Children.Add(new TextBlock { Text = c.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Res("T1"), TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(head);
            sp.Children.Add(new TextBlock { Text = c.Detail, Foreground = Res("T2"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30, 6, 0, 0) });
            if (c.Fix != "" && c.Sev >= Sev.Warn) sp.Children.Add(new TextBlock { Text = "Como resolver: " + c.Fix, Foreground = Res("T1"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30, 6, 0, 0) });
            sp.Children.Add(new TextBlock { Text = "Medido: " + c.Evidence, Foreground = Res("T3"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30, 6, 0, 0) });
            bd.Child = sp; hl.Children.Add(bd);
        }
        hl.Children.Add(new TextBlock { Text = "Não testado: bufferbloat (atraso sob carga). Medi-lo exige saturar a conexão com um teste de velocidade, o que distorceria as rotas em andamento.", Foreground = Res("T3"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 4, 0, 0) });
    }
}
