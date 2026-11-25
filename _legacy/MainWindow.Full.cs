using System.Net;
using System.Windows;
namespace SuiteRedes;

public partial class MainWindow
{
    string lastSig = ""; int hypColsMax = 1;
    static double ParseMs(string s) => double.TryParse(s.Replace(" ms", "").Trim(), out var v) ? v : -1;

    async Task TestDestAsync(DestResult d, CancellationToken ct)
    {
        var row = d.Row;
        var ip = await Net.ResolveAsync(d.Name, ct);
        if (ip == null) { d.Resolved = false; row.SetStatus("Nome não encontrado", Pal.Err); row.Extra = "O DNS não conseguiu traduzir este nome"; row.Raise(); return; }
        d.Ip = ip.ToString(); row.Address = d.Ip;
        await PingLoopAsync(row, ip, 15, 200, 2000, 32, false, ct);
        (row.Asn, row.Org) = await NetInfo.AsnAsync(d.Ip, ct); row.Raise();
        var (st, ms) = await TcpProbeAsync(ip, 443, 2500, ct);
        string port = "443";
        if (st != "Aberta") { var (st2, ms2) = await TcpProbeAsync(ip, 80, 2500, ct); if (st2 == "Aberta") { st = st2; ms = ms2; port = "80"; } }
        d.Tcp = st; row.Extra = st == "Aberta" ? $"Porta {port} aberta ({ms} ms)" : $"Porta 443/80: {st}";
        if (row.Received == 0 && st == "Aberta") row.SetStatus("Ping bloqueado (TCP ok)", Pal.Info);
        row.Raise();
    }

    async void BtnFull_Click(object s, RoutedEventArgs e)
    {
        var targets = Net.Targets(DestsText).Take(MaxDests).ToList();
        if (targets.Count == 0) { Error("Informe ao menos um destino (ex.: google.com)."); return; }
        StopMonitor(false); RoutesCanvas.Children.Clear(); RoutesSection.Visibility = Visibility.Collapsed; Events.Clear(); SamplesBy.Clear(); monSamples = 0; monAccum = TimeSpan.Zero; gwRowLive = null; lastReport = null;
        BtnMonPause.IsEnabled = false; BtnMonStop.IsEnabled = false; TxtMonStatus.Text = "Monitoramento começa automaticamente após a verificação.";
        fullCts = new(); var ct = fullCts.Token;
        BtnFull.IsEnabled = false; BtnFullStop.IsEnabled = true; PnlDests.IsEnabled = false; BtnExport.IsEnabled = false;
        DestRows.Clear(); DnsRows.Clear(); HttpRows.Clear(); Traces.Clear();
        CmbLive.ItemsSource = null; boundName = ""; selRow = null; selPath = new(); MonSection.Visibility = Visibility.Collapsed;
        lastSig = ""; PbFull.Value = 0; ItemsStages.ItemsSource = null; ItemsHyps.ItemsSource = null; TxtHypsTitle.Visibility = Visibility.Collapsed; TxtStagesTitle.Visibility = Visibility.Collapsed;
        HeadDetail.Visibility = Visibility.Collapsed; TxtHistory.Text = "";
        TxtHeadLabel.Text = "Diagnóstico"; TxtHeadIcon.Text = "⏳"; TxtHeadTitle.Text = "Verificando... aguarde"; TxtHeadTitle.Foreground = (System.Windows.Media.Brush)FindResource("T1"); TxtHeadWho.Text = ""; TxtHeadSummary.Text = "Não feche o programa. Os testes rodam em sequência e levam cerca de 1 a 2 minutos.";
        HeadBorder.BorderBrush = Pal.Gray; HeadBorder.Background = (System.Windows.Media.Brush)FindResource("BgCard"); PbHeadConf.Visibility = Visibility.Collapsed; TxtHeadConf.Text = "";
        void Step(int pct, string msg) { PbFull.Value = pct; TxtFullStatus.Text = msg; }
        var ev = new Evidence();
        try
        {
            // ===== Fase 1: tudo em paralelo (cada teste em sua própria tarefa) =====
            // As continuações voltam para a thread da interface, então listas e linhas da tela são atualizadas sem conflito.
            var (_, gw) = Diagnostics.Primary();
            int doneN = 0, totalN = 0; var running = new List<string>();
            void Upd() { PbFull.Value = 4 + 76.0 * doneN / Math.Max(1, totalN); TxtFullStatus.Text = running.Count == 0 ? "Finalizando..." : $"Em andamento ({doneN}/{totalN} concluídos): {string.Join(", ", running.Take(5))}{(running.Count > 5 ? "..." : "")}"; }
            async Task Job(string name, Func<Task> work) { totalN++; running.Add(name); Upd(); try { await work(); } finally { running.Remove(name); doneN++; Upd(); } }

            var pubTask = NetInfo.GetPublicAsync(ct);
            string wifi = "";
            foreach (var t in targets) { var d = new DestResult { Name = t, Row = new StatRow { Title = t } }; ev.Dests.Add(d); DestRows.Add(d.Row); }
            StatRow? g = null; if (gw != null) { g = new StatRow { Title = "Roteador (gateway)" }; DestRows.Insert(0, g); gwRowLive = g; }
            var sysDns = Diagnostics.SystemDns();
            var domain = ev.Dests.FirstOrDefault(d => !d.IsIp)?.Name ?? "www.google.com";
            var sites = ev.Dests.Where(d => !d.IsIp).Select(d => d.Name).Take(4).ToList();
            foreach (var def in new[] { "www.google.com", "www.cloudflare.com" }) if (sites.Count < 2 && !sites.Contains(def)) sites.Add(def);
            HttpRow[] http = Array.Empty<HttpRow>(); List<DnsRow> dns = new(); string? upnpWan = null;

            var jobs = new List<Task>
            {
                Job("computador", async () => { wifi = await Task.Run(Diagnostics.WifiInfo); await Task.Run(() => Diagnosis.GatherLocal(ev, wifi)); }),
                Job("sites (HTTPS)", async () => { http = await Task.WhenAll(sites.Select(u => HttpTestAsync("https://" + u, ct))); }),
                Job("DNS", async () =>
                {
                    dns = await DnsTestAsync(domain, "A", sysDns.Concat(new[] { "1.1.1.1", "8.8.8.8" }).Distinct().ToList(), 4, 2000, ct);
                    try { await Compat.GetHostAddressesAsync("www.microsoft.com", ct); ev.ResolverOk = true; } catch (OperationCanceledException) { throw; } catch { ev.ResolverOk = false; }
                }),
                Job("IP público", async () => { ev.Pub = await pubTask; }),
                Job("NAT (roteador)", async () =>
                {
                    var (natNic, _) = Diagnostics.Primary();
                    var localV4 = natNic?.GetIPProperties().UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address;
                    if (localV4 != null) upnpWan = await Nat.UpnpWanIpAsync(localV4, gw, ct);
                }),
                Job("MTU", async () => { ev.Mtu = await FindMtuAsync(IPAddress.Parse("8.8.8.8"), _ => { }, ct); }),
            };
            if (g != null) jobs.Add(Job("roteador", async () =>
            {
                await PingLoopAsync(g, gw!, 20, 150, 1500, 32, false, ct);
                ev.GwTested = true; ev.GwSent = g.Sent; ev.GwRecv = g.Received; ev.GwLoss = g.Loss; ev.GwAvg = g.Avg; ev.GwJitter = g.Jitter;
                g.Extra = "Equipamento local"; g.Raise();
            }));
            // cada destino: nome → ping → conexão e, assim que tiver o IP, a rota (sem esperar os outros destinos)
            foreach (var d in ev.Dests) jobs.Add(Job(d.Name, async () =>
            {
                await TestDestAsync(d, ct);
                if (!d.Resolved || d.Ip == "") return;
                var rows = new List<StatRow>();
                bool reached = await TraceAsync(IPAddress.Parse(d.Ip), 25, 6, 1200, true, rows, ct);
                await NetInfo.EnrichHopsAsync(rows, ct);
                Traces[d.Name] = rows; Diagnosis.FillTrace(d, rows, reached);
            }));
            await Task.WhenAll(jobs);

            // consolida resultados de HTTPS e DNS
            foreach (var h in http) HttpRows.Add(h);
            ev.HttpTotal = http.Length; ev.HttpOk = http.Count(h => h.Status.StartsWith("OK"));
            ev.TlsErrors = http.Any(h => new[] { "SSL", "certific", "authentication", "autentica", "TLS", "trust" }.Any(k => h.Status.Contains(k, StringComparison.OrdinalIgnoreCase)));
            ev.HttpTtfbAvg = http.Where(h => h.Status.StartsWith("OK")).Select(h => (double)h.TtfbMs).DefaultIfEmpty(0).Average();
            foreach (var d in ev.Dests) { var m = http.FirstOrDefault(h => h.Url.EndsWith("//" + d.Name)); if (m != null) { d.HttpOk = m.Status.StartsWith("OK"); d.HttpMs = m.TotalMs; d.Row.Extra += d.HttpOk == true ? $" • HTTPS ok ({m.TotalMs} ms)" : $" • HTTPS falhou: {m.Status}"; d.Row.Raise(); } }
            foreach (var d in dns) { if (sysDns.Contains(d.Server)) d.Provider = "Seu DNS" + (d.Provider != "" && d.Provider != "Sistema" ? " (" + d.Provider + ")" : ""); DnsRows.Add(d); }
            var sysRows = dns.Where(d => sysDns.Contains(d.Server)).ToList(); var pubRows = dns.Where(d => !sysDns.Contains(d.Server)).ToList();
            ev.DnsTested = true; ev.DnsDomain = domain; ev.SysDnsCount = sysDns.Count;
            ev.SysDnsOk = sysRows.Any(d => d.Status is "OK" or "INSTÁVEL"); ev.PubDnsOk = pubRows.Any(d => d.Status == "OK");
            ev.SysNx = sysRows.Any(d => d.Status == "NXDOMAIN");
            var sa = sysRows.Where(d => d.Status == "OK").Select(d => ParseMs(d.AvgTxt)).Where(v => v >= 0).ToList(); ev.SysDnsAvg = sa.Count > 0 ? sa.Average() : -1;
            var pa = pubRows.Where(d => d.Status == "OK").Select(d => ParseMs(d.AvgTxt)).Where(v => v >= 0).ToList(); ev.PubDnsAvg = pa.Count > 0 ? pa.Average() : -1;
            if (sysDns.Count == 0) ev.SysDnsOk = ev.ResolverOk;
            ev.Nat = Nat.Analyze(gw?.ToString(), Traces.Values.Select(t => (IReadOnlyList<StatRow>)t).ToList(), ev.Pub.SeenIps.Count > 0 ? ev.Pub.SeenIps : (ev.Pub.Ok ? new List<string> { ev.Pub.Ip } : new()), upnpWan, ev.VpnActive);
            ev.Cgnat = ev.Nat.Kind == NatKind.Cgnat; ev.DoubleNat = ev.Nat.Kind == NatKind.DoubleNat; foreach (var kv in NetInfo.WifiDetails(wifi)) { if (kv.Key == "band") ev.WifiBand = kv.Value; if (kv.Key == "radio") ev.WifiRadio = kv.Value; if (kv.Key == "rx" && double.TryParse(kv.Value, out var rx)) ev.WifiRate = rx; }

            // ===== Fase 2: velocidade isolada (saturar o link junto com os outros testes falsearia latência e perda) =====
            Step(82, "Medindo velocidade e latência sob carga (isolado para não interferir nas outras medições)...");
            var idle = new StatRow { Title = "idle" };
            await PingLoopAsync(idle, IPAddress.Parse("1.1.1.1"), 8, 120, 1500, 32, false, ct);
            var sp = await SpeedTestAsync(8, m => TxtFullStatus.Text = m, ct);
            if (sp != null)
            {
                ev.SpeedMbps = sp.Mbps; if (idle.Received > 2) { ev.IdleAvg = idle.Avg; ev.LoadedAvg = sp.LoadedAvg; ev.LoadedLoss = sp.LoadedLoss; }
            }

            // Análise inteligente
            Step(98, "Analisando os resultados...");
            var report = await Task.Run(() => Diagnosis.Analyze(ev));
            lastReport = report; lastEvidence = ev;
            BuildInfo(ev, wifi, gw); Render(report, ev);
            PbFull.Value = 100; TxtFullStatus.Text = "Verificação concluída."; HeadBorder.BringIntoView();
            BtnExport.IsEnabled = true; StartMonitor();
        }
        catch (OperationCanceledException) { TxtFullStatus.Text = "Verificação cancelada."; TxtHeadIcon.Text = "🩺"; TxtHeadTitle.Text = "Verificação cancelada"; TxtHeadSummary.Text = "Clique em “Verificar minha internet” para tentar de novo."; }
        catch (Exception ex) { App.Log(ex); TxtHeadTitle.Text = "Erro inesperado durante a verificação"; TxtHeadSummary.Text = ex.Message + " (detalhes em " + App.LogPath + ")"; }
        finally { BtnFull.IsEnabled = true; BtnFullStop.IsEnabled = false; PnlDests.IsEnabled = true; }
    }

    void Render(Report r, Evidence ev, bool save = true)
    {
        var t = r.Top;
        TxtHeadIcon.Text = t.Icon; TxtHeadLabel.Text = t.Severity == Sev.Ok ? "Resultado" : "Causa mais provável";
        TxtHeadTitle.Text = t.Title; TxtHeadTitle.Foreground = t.Color;
        TxtHeadWho.Text = t.Severity == Sev.Ok ? "" : t.WhoText; TxtHeadSummary.Text = t.Summary;
        HeadBorder.BorderBrush = t.Color; HeadBorder.Background = t.Tint;
        PbHeadConf.Value = t.Confidence; PbHeadConf.Visibility = Visibility.Visible; TxtHeadConf.Text = t.ConfText;
        string sig = string.Join("|", r.Stages.Select(x => x.Name + x.State + x.Detail)) + "#" + string.Join("|", r.Hyps.Select(x => x.Id + x.Confidence + x.Why.Count));
        if (sig == lastSig) return;
        lastSig = sig;
        ItemsStages.ItemsSource = r.Stages; TxtStagesTitle.Visibility = Visibility.Visible;
        TxtHeadSteps.Text = t.StepsText; TxtHeadWhy.Text = t.Why.Count > 0 ? t.WhyText : "Nenhum problema encontrado nos testes."; HeadDetail.Visibility = Visibility.Visible;
        if (save) _ = Task.Run(() => SaveAndCompare(r, ev)).ContinueWith(t => Dispatcher.BeginInvoke(() => { lastHistory = t.Result; TxtHistory.Text = lastHistory; }));
        var others = r.Hyps.Skip(1).ToList();
        ItemsHyps.ItemsSource = others; TxtHypsTitle.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed; TxtHypsTitle.Header = $"Outras causas possíveis ({others.Count})"; hypColsMax = Math.Max(1, others.Count); Window_SizeChanged(this, null!);
    }
}

public partial class MainWindow
{
    void BuildInfo(Evidence ev, string wifiText, IPAddress? gw)
    {
        var loc = new List<InfoItem>(); var net = new List<InfoItem>(); var rt = new List<InfoItem>();
        void A(List<InfoItem> l, string label, string? v) { if (!string.IsNullOrWhiteSpace(v)) l.Add(new InfoItem { Label = label, Value = v! }); }
        var (nic, _) = Diagnostics.Primary(); nic ??= Diagnostics.Nics().FirstOrDefault(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up);
        var wd = NetInfo.WifiDetails(wifiText);
        if (nic != null)
        {
            var ip = nic.GetIPProperties(); var v4 = ip.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            A(loc, "Conexão", ev.IsWifi ? $"Wi-Fi{(wd.TryGetValue("ssid", out var ss) ? " “" + ss + "”" : "")}" : $"Cabo — {nic.Name}");
            if (ev.IsWifi)
            {
                A(loc, "Wi-Fi: padrão / banda", string.Join(" • ", new[] { wd.GetValueOrDefault("radio"), wd.GetValueOrDefault("band") }.Where(x => !string.IsNullOrEmpty(x))));
                A(loc, "Wi-Fi: canal / sinal", string.Join(" • ", new[] { wd.TryGetValue("channel", out var ch) ? "canal " + ch : null, ev.WifiSignal is int s ? $"sinal {s}%" : null }.Where(x => x != null)!));
                A(loc, "Wi-Fi: taxa de conexão", wd.ContainsKey("rx") ? $"↓ {wd["rx"]} Mbps / ↑ {wd.GetValueOrDefault("tx", "?")} Mbps" : null);
                A(loc, "Wi-Fi: segurança", wd.GetValueOrDefault("auth"));
            }
            else A(loc, "Velocidade do link", ev.LinkMbps > 0 ? $"{ev.LinkMbps} Mbps" : null);
            A(loc, "IP local", v4 != null ? $"{v4.Address} / {v4.IPv4Mask}" : null);
            A(loc, "IPv6 local", ip.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal)?.Address.ToString());
            A(loc, "Roteador (gateway)", gw?.ToString());
            A(loc, "Servidor DHCP", ip.DhcpServerAddresses.FirstOrDefault()?.ToString());
            A(loc, "DNS configurado", string.Join(", ", ip.DnsAddresses.Select(x => x.ToString())));
            A(loc, "Endereço MAC", string.Join("-", nic.GetPhysicalAddress().GetAddressBytes().Select(x => x.ToString("X2"))));
            try { A(loc, "MTU local", ip.GetIPv4Properties().Mtu.ToString()); } catch { }
        }
        var p = ev.Pub;
        A(net, "IP público", p.Ok ? p.Ip : "Não foi possível obter (sem acesso ao serviço)");
        A(net, "DNS reverso do IP", p.Rdns);
        A(net, "Provedor", string.Join(" • ", new[] { p.Asn, p.IspName }.Where(x => x != "").Distinct()));
        A(net, "Localização (estimada)", p.Place);
        A(net, "IPv6 na internet", p.Ipv6Works ? "Funciona — " + p.Ipv6 : "Não disponível");
        A(net, "Data center Cloudflare mais próximo", p.Colo);
        if (p.SeenIps.Distinct().Count() > 1) A(net, "IPs públicos vistos", string.Join(", ", p.SeenIps.Distinct()));
        if (ev.Nat != null)
        {
            A(net, "NAT / CGNAT", ev.Nat.Short);
            A(net, "NAT: evidências", string.Join(" ", ev.Nat.Evidence));
            A(net, "NAT: limitações", string.Join(" ", ev.Nat.Limits));
        }
        Dispatcher.BeginInvoke(() => { if (p.Ok) { TxtLookupIp.Text = p.Ip; TxtLookupIp.Tag = "auto"; BtnLookup_Click(this, new RoutedEventArgs()); } });
        var main = ev.Dests.FirstOrDefault(d => Traces.ContainsKey(d.Name));
        if (main != null)
        {
            var rows = Traces[main.Name]; var last = rows.LastOrDefault(r => r.Received > 0);
            A(rt, "Rota analisada", $"{main.Name} ({main.Ip})");
            A(rt, "Saltos até o destino", $"{rows.Count}{(main.TraceReached ? "" : " (destino não alcançado)")}");
            A(rt, "Latência até o destino", last != null ? $"{last.Avg:F0} ms (oscilação {last.Jitter:F0} ms)" : null);
            A(rt, "Equipamentos com IP privado", rows.Count(r => NetInfo.IsPrivate(r.Address)).ToString());
            A(rt, "Operadoras no caminho", NetInfo.AsPath(rows));
            A(rt, "Perda no trajeto", main.FirstLossHop > 0 ? $"começa no salto {main.FirstLossHop} ({main.FirstLossAddr}{(main.FirstLossOrg != "" ? " — " + main.FirstLossOrg : "")})" : "Nenhuma perda real detectada");
            var dest = ev.Dests.Where(d => Traces.ContainsKey(d.Name)).Select(d => d.Row).FirstOrDefault(r => r.Asn != "");
        }
        infoLocal = loc; infoNet = net; infoRoute = rt;
        ItemsInfoNet.ItemsSource = net.Where(i => !i.Label.StartsWith("NAT: ")).ToList(); ItemsInfoLocal.ItemsSource = loc; TxtNatDetail.Text = string.Join("\n", net.Where(i => i.Label.StartsWith("NAT: ")).Select(i => "• " + i.Value)); ExpDet.Visibility = Visibility.Visible;
    }
}
