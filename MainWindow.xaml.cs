using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
namespace SuiteRedes;

/// <summary>Um destino monitorado: rota descoberta + medição contínua de todos os saltos.</summary>
public sealed class RouteVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Host = "", Label = "";
    public IPAddress? Ip; public bool Reached, Failed, Tcp;
    /// <summary>Trava da rota: a gravação das medições (UI) e a análise paralela (pool) nunca se cruzam.</summary>
    public readonly object Sync = new();
    /// <summary>Resultado da última análise feita em segundo plano (null = ainda não analisada).</summary>
    public Tuple<Sev, bool, int, string>? Analysis;
    public string Phase = "Na fila";
    public readonly List<StatRow> Hops = new();   // inclui o destino como último item
    public int Order;
    public Sev Worst = Sev.Ok; public bool Any; public int First = -1; public string Why = "";
    public string Title => Label != "" ? Label : Host;
    public string Sub { get; set; } = "";
    public Brush SubBrush { get; set; } = Pal.Gray;
    public string Ms { get; set; } = "";
    public Brush StateBrush { get; set; } = Pal.Gray;
    public Brush MsBrush { get; set; } = Pal.Gray;
    public PointCollection Spark { get; set; } = new();
    public StatRow? Dest => Hops.Count > 0 ? Hops[Hops.Count - 1] : null;
    public void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public partial class MainWindow : Window
{
    // ---------- destinos ----------
    const int MaxDests = 16;
    static readonly (string Host, string Label)[] DefaultDests =
    {
        ("rds.skytins.com.br", "Skytins Datacenter"),
        ("a.dns.br", "DNS do .br (NIC.br)"),
        ("a.st1.ntp.br", "NTP.br (NIC.br)"),
        ("ix.br", "IX.br (NIC.br)"),
        ("a.root-servers.net", "DNS raiz A"),
        ("l.root-servers.net", "DNS raiz L"),
        ("1.1.1.1", "Cloudflare"),
        ("8.8.8.8", "Google"),
        ("9.9.9.9", "Quad9"),
    };
    static bool HostOk(string t) => t.Length is > 0 and <= 253 && t.All(c => (c < 128 && char.IsLetterOrDigit(c)) || c == '.' || c == '-');
    static List<(string Host, string Label)> ParseDests(string text)
    {
        var res = new List<(string, string)>();
        foreach (var raw in text.Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim(); if (line.Length == 0 || line.StartsWith("#")) continue;
            int eq = line.IndexOf('='); var host = (eq >= 0 ? line.Substring(0, eq) : line).Trim(); var label = eq >= 0 ? line.Substring(eq + 1).Trim() : "";
            if (HostOk(host) && !res.Any(x => x.Item1.Equals(host, StringComparison.OrdinalIgnoreCase))) res.Add((host, label));
        }
        return res.Take(MaxDests).ToList();
    }
    static string Fmt(IEnumerable<(string Host, string Label)> l) => string.Join(Environment.NewLine, l.Select(x => x.Label == "" ? x.Host : $"{x.Host} = {x.Label}"));
    static string CfgPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuiteRedes", "destinos-v2.txt");
    void LoadDests()
    {
        string v = Fmt(DefaultDests);
        try { if (File.Exists(CfgPath)) { var t = File.ReadAllText(CfgPath); if (ParseDests(t).Count > 0) v = t; } } catch { }
        TxtDests.Text = v;
    }
    void PopCfg_Closed(object? s, EventArgs e)
    {
        try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CfgPath)!); File.WriteAllText(CfgPath, Fmt(ParseDests(TxtDests.Text))); } catch (Exception ex) { App.Log(ex); }
    }
    void BtnCfgReset_Click(object s, RoutedEventArgs e) => TxtDests.Text = Fmt(DefaultDests);

    // ---------- estado ----------
    readonly ObservableCollection<RouteVm> Routes = new();
    CancellationTokenSource? cts; DateTime started; DateTime? ended; int gen;
    /// <summary>Trava única dos dados medidos: um mesmo ponto (StatRow) pode pertencer a várias rotas.</summary>
    static readonly object DataSync = new();
    /// <summary>
    /// Pontos compartilhados: o mesmo IP na mesma posição (TTL) em várias rotas é medido UMA vez por rodada, pela rota "dona".
    /// Sem isso, o roteador recebia um teste por destino por segundo e, por limitar respostas ICMP (RFC 1812),
    /// deixava de responder a alguns — gerando "perda" falsa e números diferentes para o mesmo ponto em cada destino.
    /// </summary>
    readonly Dictionary<string, StatRow> sharedHops = new();
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };
    bool Running => cts != null && !cts.IsCancellationRequested;
    RouteVm? Sel => LstRoutes.SelectedItem as RouteVm;
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    static TimeSpan PhaseOf(double period) => TimeSpan.FromSeconds(-(Clock.Elapsed.TotalSeconds % period));

    public MainWindow()
    {
        InitializeComponent();
        LstRoutes.ItemsSource = Routes; LoadDests();
        tick.Tick += (_, _) => Refresh(); tick.Start();
        PreviewKeyDown += (_, k) => { if (k.Key == Key.F5 && Keyboard.FocusedElement is not TextBox) { BtnRun_Click(this, new RoutedEventArgs()); k.Handled = true; } };
        Closing += (_, _) => cts?.Cancel();
        SizeChanged += (_, _) => Refresh();
#if DEBUG
        // captura para verificação (somente build Debug): --shot arquivo.png [segundos]
        // Fica fora do Release: captura de tela + gravação silenciosa de arquivos + autoencerramento é um padrão
        // que a heurística dos antivírus associa a spyware.
        var a = Environment.GetCommandLineArgs(); int si = Array.IndexOf(a, "--shot");
        int zi = Array.IndexOf(a, "--size"); if (zi >= 0 && zi + 1 < a.Length && a[zi + 1].Split('x') is { Length: 2 } wh) { WindowStartupLocation = WindowStartupLocation.Manual; Left = 0; Top = 0; Width = double.Parse(wh[0]); Height = double.Parse(wh[1]); }
        if (si >= 0 && si + 1 < a.Length)
            Loaded += async (_, _) =>
            {
                BtnRun_Click(this, new RoutedEventArgs());
                await Task.Delay(1000 * (si + 2 < a.Length && int.TryParse(a[si + 2], out var sec) ? sec : 40));
                if (a.Contains("--health")) { healthMode = true; LstRoutes.SelectedItem = null; }
                Refresh(); UpdateLayout(); await Task.Delay(300);
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(this);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = File.Create(a[si + 1])) enc.Save(fs);
                File.WriteAllText(a[si + 1] + ".html", BuildHtml(), new System.Text.UTF8Encoding(true)); File.WriteAllText(a[si + 1] + ".csv", BuildCsv());
                bmp.Clear(); GC.Collect(); GC.WaitForPendingFinalizers();
                // só no modo de captura: encerra direto, sem a destruição nativa do WPF (evita falha conhecida do RenderTargetBitmap no .NET Framework)
                Environment.Exit(0);
            };
#endif
    }

    // ---------- iniciar / parar ----------
    void BtnRun_Click(object s, RoutedEventArgs e)
    {
        if (Running) { cts!.Cancel(); ended = DateTime.Now; BtnRun.Content = "Iniciar teste"; Refresh(); return; }
        var list = ParseDests(TxtDests.Text);
        if (list.Count == 0) { MessageBox.Show("Adicione ao menos um destino em “Destinos”.", "Suíte de Redes"); return; }
        cts = new(); var ct = cts.Token; started = DateTime.Now; ended = null; gen++; pubInfo = null; det.Clear(); pendingChange.Clear(); lock (DataSync) sharedHops.Clear(); BtnRun.Content = "Parar teste";
        Routes.Clear(); Events.Clear(); BtnExport.IsEnabled = true;
        _ = LoadPublic(ct); StartHealth(ct); _ = Task.Run(() => AnalyzerLoop(ct));
        int k = 0;
        foreach (var (h, l) in list) { var r = new RouteVm { Host = h, Label = l, Order = k++ }; Routes.Add(r); _ = Task.Run(() => RunRoute(r, ct)); }
        var av = Environment.GetCommandLineArgs(); int ph = Array.IndexOf(av, "--pickhost");
        LstRoutes.SelectedIndex = av.Contains("--pick6") ? 6 : 0;
        if (ph >= 0 && ph + 1 < av.Length && Routes.FirstOrDefault(x => x.Host.Contains(av[ph + 1])) is RouteVm pr) LstRoutes.SelectedItem = pr;
        Refresh();
    }

    /// <summary>Resolve, descobre os saltos (TTL crescente) e mede todos continuamente, 1 rodada por segundo.</summary>
    async Task RunRoute(RouteVm r, CancellationToken ct)
    {
        // Roda no pool de threads (Task.Run). Toda alteração de estado visível vai para a UI em UM lote por rodada.
        int myGen = Dispatcher.Invoke(() => gen);
        Task Ui(Action a) => Dispatcher.InvokeAsync(() => { if (gen == myGen) a(); }, DispatcherPriority.Background).Task;
        try
        {
            await Ui(() => r.Phase = "Resolvendo nome");
            var ipAddr = IPAddress.TryParse(r.Host, out var ip) ? ip : await Net.ResolveAsync(r.Host, ct).ConfigureAwait(false);
            if (ipAddr == null) { await Ui(() => { r.Failed = true; r.Phase = "Nome não encontrado (DNS)"; }); return; }
            await Ui(() => { r.Ip = ipAddr; r.Phase = "Descobrindo a rota"; });
            var found = new List<StatRow>(); int fails = 0; bool reached = false;
            for (int ttl = 1; ttl <= 30; ttl++)
            {
                ProbeResult pr = new(IPStatus.Unknown, 0, null);
                for (int a = 0; a < 2; a++) { pr = await Net.ProbeAsync(ipAddr, 1200, ttl, 32, false, ct).ConfigureAwait(false); if (Net.IsHopReply(pr.Status)) break; }
                var row = new StatRow { Index = ttl, Title = ttl.ToString() };
                if (Net.IsHopReply(pr.Status)) { row.Address = pr.From?.ToString() ?? "?"; fails = 0; if (pr.From != null) _ = Reverse(row, pr.From); }
                else { row.Address = "*"; fails++; }
                found.Add(row);
                if (pr.Status == IPStatus.Success) { reached = true; break; }
                if (fails >= 5) { found.RemoveRange(found.Count - fails, fails); break; }
            }
            if (!reached) { var dr = new StatRow { Index = 0, Title = r.Host, Address = ipAddr.ToString() }; found.Add(dr); _ = Reverse(dr, ipAddr); }
            // reaproveita pontos já medidos por outra rota (mesmo IP e mesma posição); o destino nunca é compartilhado
            var owned = new HashSet<StatRow>();
            lock (DataSync)
            {
                for (int k = 0; k < found.Count; k++)
                {
                    var hp = found[k]; bool isLast = k == found.Count - 1; string key = $"{hp.Index}|{hp.Address}";
                    if (!isLast && hp.Address is not ("*" or "?") && sharedHops.TryGetValue(key, out var sh)) { found[k] = sh; continue; }
                    owned.Add(hp);
                    if (!isLast && hp.Address is not ("*" or "?")) sharedHops[key] = hp;
                }
            }
            await Ui(() => { r.Hops.AddRange(found); r.Reached = reached; r.Phase = "Ao vivo"; });
            _ = NetInfo.EnrichHopsAsync(found.Where(h => h.Address is not ("*" or "?")).ToList(), ct);
            var hops = found.Where(owned.Contains).ToArray(); var dst = found[found.Count - 1]; bool tcp = false;   // só mede os pontos que são desta rota
            while (!ct.IsCancellationRequested)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var res = await Task.WhenAll(hops.Select(async h => (h, p: await Net.ProbeAsync(ipAddr, 1500, h == dst ? 128 : h.Index, 32, false, ct).ConfigureAwait(false)))).ConfigureAwait(false);
                // destino que bloqueia ping: passa a medir pelo tempo de conexão TCP (porta 443, ou 80)
                bool switchTcp = false; long? tcpMs = null;
                if (!tcp && dst.Sent >= 4 && dst.Received == 0 && await TcpMs(ipAddr, ct).ConfigureAwait(false) is long) { tcp = switchTcp = true; }
                if (tcp) tcpMs = await TcpMs(ipAddr, ct).ConfigureAwait(false);
                await Ui(() => { lock (DataSync) {
                    if (switchTcp) { r.Tcp = true; dst.Reset(); }
                    foreach (var (h, p) in res)
                    {
                        if (tcp && h == dst) continue;
                        bool isDst = h == dst;
                        // destino: só vale resposta do próprio destino (TTL expirado = roteador antes dele, não o destino)
                        bool ok = isDst ? p.Status == IPStatus.Success : Net.IsHopReply(p.Status);
                        if (!isDst && p.Status == IPStatus.Success && h.Index > 0) continue; // rota encurtou: este TTL já chega ao destino
                        if (ok && h.Address == "*" && p.From != null) { h.Address = p.From.ToString(); var from = p.From; _ = Task.Run(() => Reverse(h, from)); _ = NetInfo.EnrichHopsAsync(new[] { h }, ct); }
                        if (!isDst && ok && p.From != null && h.Address is not ("*" or "?") && p.From.ToString() != h.Address && h.Index > 0) RouteChange(r, h, p.From.ToString());
                        else if (ok) pendingChange.Remove(h);
                        h.Record(ok ? p.Rtt : null, ok ? null : p.Status.ToString(), false);
                    }
                    if (tcp) dst.Record(tcpMs, tcpMs == null ? "TCP" : null, false);
                    Detect(r);
                } }).ConfigureAwait(false);
                await Task.Delay(Math.Max(0, 1000 - (int)sw.ElapsedMilliseconds), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log(ex); _ = Ui(() => { r.Failed = true; r.Phase = "Erro: " + ex.Message; }); }
    }
    static async Task<long?> TcpMs(IPAddress ip, CancellationToken ct)
    {
        // 443 e 80 em paralelo, limite total de 900 ms (mantém a rodada em ~1 s)
        async Task<long?> One(int port)
        {
            using var c = new System.Net.Sockets.TcpClient(ip.AddressFamily);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var t = c.ConnectAsync(ip, port); _ = t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return await Task.WhenAny(t, Task.Delay(900, ct)) == t && !t.IsFaulted && c.Connected ? sw.ElapsedMilliseconds : null;
        }
        var rs = await Task.WhenAll(One(443), One(80));
        return rs.Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty(-1).Min() is long m && m >= 0 ? m : null;
    }
    static async Task Reverse(StatRow row, IPAddress ip) { try { var h = await Net.ReverseAsync(ip); if (h != null) row.Host = h; } finally { row.HostChecked = true; } }

    // ---------- avaliação de cada salto ----------
    static (Sev sev, bool silent, string why) Assess(List<StatRow> path, int i)
    {
        var r = path[i]; bool last = i == path.Count - 1;
        if (r.Sent < 3) return (Sev.Ok, true, "coletando amostras");
        if (r.Received == 0) return last ? (Sev.Error, false, "o destino não responde") : (Sev.Ok, true, "não responde ao teste; os pontos seguintes respondem");
        var later = path.Skip(i + 1).Where(x => x.Sent >= 5).ToList();
        Sev sev = Sev.Ok; string why = "", note = "";
        void Set(Sev s, string w) { if (s > sev) { sev = s; why = w; } }
        if (r.Sent >= 5 && r.Loss >= LossWarn && (last || later.All(x => x.Loss >= LossWarn)))
        {
            double dLoss = last ? r.Loss : path[path.Count - 1].Loss;   // severidade = perda que chega ao destino
            Set(dLoss >= 15 ? Sev.Error : Sev.Warn, last ? $"perda de {r.Loss:F0}%" : $"perda começa aqui ({r.Loss:F0}% neste ponto) e chega a {dLoss:F0}% no destino");
        }
        var prev = path.Take(i).LastOrDefault(x => x.Received > 0);
        if (prev != null && r.Avg - prev.Avg > 60 && later.Where(x => x.Received > 0).All(x => x.Avg >= r.Avg * 0.7))
            note = $"+{r.Avg - prev.Avg:F0} ms neste trecho, mantidos até o destino";
        if (last && r.Avg > 300) Set(Sev.Warn, $"latência alta ({r.Avg:F0} ms)");
        if (r.Received >= 10 && r.Jitter > 30 && (last || later.Where(x => x.Received > 0).All(x => x.Jitter > 20))) Set(Sev.Warn, $"oscilação de {r.Jitter:F0} ms");
        return (sev, false, sev == Sev.Ok ? note : why);
    }
    /// <summary>Limite único de perda (tela, relatório e cores): a partir de 3% em 5+ testes.</summary>
    public const double LossWarn = 3;
    static Brush SevBrush(Sev s) => s switch { Sev.Error => Pal.Err, Sev.Warn => Pal.Warn, _ => Pal.Ok };
    static string OrgOf(StatRow r) => NetInfo.IsPrivate(r.Address) ? (r.Index == 1 ? "Sua rede" : "Rede privada") : NetInfo.IsCgnat(r.Address) ? "Faixa CGNAT"
        : r.Host.EndsWith(".ix.br", StringComparison.OrdinalIgnoreCase) ? "IX.br " + (r.Host.Split('.').Reverse().Skip(2).FirstOrDefault() ?? "").ToUpperInvariant()
        : r.Org.Trim() == "" && r.Asn != "" ? r.Asn : System.Text.RegularExpressions.Regex.Replace(r.Org.Trim(), @"^AS\d+\s*[-–]\s*", "");

    /// <summary>Cópia profunda dos saltos (chamar sob DataSync): a análise paralela trabalha sem tocar nos dados vivos.</summary>
    static RouteVm Snapshot(RouteVm r)
    {
        var c = new RouteVm { Host = r.Host, Phase = r.Phase };
        foreach (var h in r.Hops) c.Hops.Add(h.Clone());
        return c;
    }

    /// <summary>Avalia todos os saltos de uma rota. Puro (só lê dados): roda em qualquer thread, sob a trava da rota.</summary>
    static (Sev worst, bool any, int first, string why) Analyze(RouteVm r)
    {
        Sev worst = Sev.Ok; bool any = false; int first = -1; string w = "";
        var hops = r.Hops;
        for (int i = 0; i < hops.Count; i++)
        {
            var (s, sil, why) = Assess(hops, i);
            if (!sil) any = true;
            if (!sil && s >= Sev.Warn && first < 0) { first = i; w = why; }
            if (s > worst) worst = s;
        }
        return (worst, any, first, w);
    }

    /// <summary>
    /// Analisador em segundo plano: a cada segundo avalia TODAS as rotas em paralelo, usando todos os núcleos da CPU
    /// (Parallel.ForEach). A thread da interface só aplica o resultado pronto.
    /// </summary>
    async Task AnalyzerLoop(CancellationToken ct)
    {
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct };
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var list = await Dispatcher.InvokeAsync(() => Routes.Where(x => x.Phase == "Ao vivo").ToList());
                // cópia dos dados sob a trava (rápido), análise pesada em paralelo fora dela
                var snaps = new List<(RouteVm r, RouteVm copy)>();
                lock (DataSync) foreach (var r in list) snaps.Add((r, Snapshot(r)));
                Parallel.ForEach(snaps, opts, x => { var a = Analyze(x.copy); x.r.Analysis = Tuple.Create(a.worst, a.any, a.first, a.why); });
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log(ex); }
    }

    void Evaluate(RouteVm r)
    {
        var an = r.Analysis is { } t ? (t.Item1, t.Item2, t.Item3, t.Item4) : Analyze(r);
        (r.Worst, r.Any, r.First, r.Why) = an;
        var d = r.Dest;
        r.StateBrush = r.Failed ? Pal.Err : !r.Any ? Pal.Gray : r.Worst >= Sev.Warn ? SevBrush(r.Worst) : Pal.Ok;
        r.MsBrush = r.Failed || (r.Any && r.Worst >= Sev.Warn) ? r.StateBrush : r.Any ? (Brush)FindResource("T1") : Pal.Gray;
        r.Ms = r.Failed ? "—" : d == null || d.Received == 0 ? (d != null && d.Sent >= 3 ? "sem resp." : "…") : $"{Median(d):F0} ms";
        if (r.Failed || r.Phase != "Ao vivo") { r.Sub = r.Phase; r.SubBrush = r.Failed ? Pal.Err : Pal.Gray; }
        else if (r.First >= 0) { var h = r.Hops[r.First]; r.Sub = (r.First == r.Hops.Count - 1 ? "No destino: " : $"Salto {h.Index}: ") + r.Why; r.SubBrush = SevBrush(r.Worst); }
        else { r.Sub = d != null && d.Sent >= 3 ? $"{r.Hops.Count} saltos, perda {d.Loss:F0}%" : "Coletando amostras"; r.SubBrush = (Brush)FindResource("T3"); }
        TrackProblem(r); r.Raise();
    }

    // ---------- atualização (1x por segundo) ----------
    void Refresh()
    {
        foreach (var r in Routes) Evaluate(r);
        if (Running) { EvalGateway(); EvalNat(); }
        UpdateHealthEntry();
        // problemas primeiro; dentro do mesmo nível, ordem original
        int Rank(RouteVm r) => r.Failed ? 0 : r.Any && r.Worst == Sev.Error ? 1 : r.Any && r.Worst == Sev.Warn ? 2 : r.Any ? 4 : 3;
        var sorted = Routes.OrderBy(Rank).ThenBy(r => r.Order).ToList();
        var keep = Sel;
        for (int i = 0; i < sorted.Count; i++) { int cur = Routes.IndexOf(sorted[i]); if (cur != i) Routes.Move(cur, i); }
        if (keep != null && !ReferenceEquals(Sel, keep)) LstRoutes.SelectedItem = keep;

        int bad = Routes.Count(r => r.Failed || (r.Any && r.Worst >= Sev.Warn)), live = Routes.Count(r => r.Phase == "Ao vivo");
        TxtSummary.Text = Routes.Count == 0 ? "Nenhum teste em andamento." : bad == 0 ? $"{Routes.Count} destinos, nenhum com problema." : $"{bad} de {Routes.Count} destinos com problema.";
        TxtSummary.Foreground = Routes.Count == 0 || bad == 0 ? (Brush)FindResource("T3") : Routes.Any(r => r.Failed || r.Worst == Sev.Error) ? Pal.Err : Pal.Warn;
        if (Running) { TxtStatus.Text = $"Ao vivo há {DateTime.Now - started:hh\\:mm\\:ss}  ({live} de {Routes.Count} rotas traçadas)"; LiveDot.Fill = Pal.Ok; }
        else { TxtStatus.Text = Routes.Count == 0 ? "Parado. Clique em “Iniciar teste” para traçar e acompanhar as rotas." : "Teste parado. Os números mostram o que foi medido."; LiveDot.Fill = (Brush)FindResource("T3"); }
        DrawSelected();
    }

    void LstRoutes_SelectionChanged(object s, SelectionChangedEventArgs e) { selHop = null; if (Sel != null) healthMode = false; DrawSelected(); }
    
    // (painel da rota: MainWindow.Path.cs)
    (string, Brush) Verdict(RouteVm r)
    {
        if (r.Failed) return ($"Não foi possível testar: {r.Phase.ToLowerInvariant()}.", Pal.Err);
        if (r.Phase != "Ao vivo") return (r.Phase + "…", (Brush)FindResource("T2"));
        if (!r.Any) return ("Coletando amostras…", (Brush)FindResource("T2"));
        if (r.First < 0) return ("Rota saudável. Nenhum salto com perda ou atraso que chegue ao destino.", Pal.Ok);
        var h = r.Hops[r.First]; var org = OrgOf(h);
        var pub0 = r.Hops.FirstOrDefault(x => x.Received > 0 && !NetInfo.IsPrivate(x.Address) && x.Org != "");
        string where = NetInfo.IsPrivate(h.Address) && h.Index == 1 ? "na sua rede local (entre este computador e o roteador)"
            : NetInfo.IsPrivate(h.Address) ? $"em endereço privado ({h.Address})"
            : NetInfo.IsCgnat(h.Address) ? $"em endereço CGNAT ({h.Address})"
            : h.Host.EndsWith(".ix.br", StringComparison.OrdinalIgnoreCase) ? $"no ponto de troca de tráfego {org}"
            : h.Asn == "" ? (org != "" ? $"em {org}" : "")
            : SameProvider(h) ? $"na rede do seu provedor ({org})"
            : $"em {org}, rede diferente da do seu provedor";
        string at = r.First == r.Hops.Count - 1 ? "Problema no destino" : $"Problema começa no salto {h.Index}";
        int same = h.Index > 0 && h.Address is not ("*" or "?") ? Routes.Count(o => o != r && o.First >= 0 && o.First < o.Hops.Count && o.Hops[o.First].Address == h.Address) : 0;
        string common = same > 0 ? $" O mesmo ponto é a origem do problema em mais {same} rota(s)." : "";
        return ($"{at}{(where != "" ? ", " + where : "")}: {r.Why}.{common}", SevBrush(r.Worst));
    }

}
