using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
namespace SuiteRedes;

public record Sample(DateTime T, string Target, long Rtt); // Rtt = -1 → pacote perdido
public record EventRow(string Time, string Target, string Kind, string Detail, DateTime T);

/// <summary>
/// Monitoramento contínuo em THREADS SEPARADAS: a rede (ping/MTR) e a análise rodam no pool de threads;
/// os resultados entram numa fila e a interface os aplica em lote a cada 200 ms (sem travar a tela).
/// </summary>
public partial class MainWindow
{
    readonly ObservableCollection<EventRow> Events = new();
    readonly Dictionary<string, List<Sample>> SamplesBy = new();
    CancellationTokenSource? monCts; bool monPaused; DateTime monStart; TimeSpan monAccum; int monSamples;
    StatRow? gwRowLive; readonly DispatcherTimer monTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer drainTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
    readonly ConcurrentQueue<Action> uiQ = new(); readonly HashSet<StatRow> dirty = new();
    volatile bool paused;
    bool MonRunning => monCts != null && !monCts.IsCancellationRequested;

    void InitMonitor()
    {
        GridEvents.ItemsSource = Events;
        monTimer.Tick += (_, _) => { UpdateMonStatus(); RefreshMonitorUi(); };
        monTimer.Start();
        drainTimer.Tick += (_, _) => Drain();
        drainTimer.Start();
    }

    /// <summary>Aplica na thread de UI, em lote, tudo o que as threads de rede produziram.</summary>
    void Drain()
    {
        int n = 0;
        while (n++ < 2000 && uiQ.TryDequeue(out var a)) { try { a(); } catch { } }
        if (dirty.Count > 0) { foreach (var r in dirty) r.Raise(); dirty.Clear(); }
    }

    void AddEvent(string target, string kind, string detail)
    {
        Events.Insert(0, new EventRow(DateTime.Now.ToString("HH:mm:ss"), target, kind, detail, DateTime.Now));
    }
    void PostEvent(string target, string kind, string detail) => uiQ.Enqueue(() => AddEvent(target, kind, detail));

    void RecordSample(string target, long rtt)
    {
        if (!SamplesBy.TryGetValue(target, out var l)) SamplesBy[target] = l = new();
        l.Add(new Sample(DateTime.Now, target, rtt)); monSamples++;
        if (l.Count > 7200) l.RemoveRange(0, 1200);
    }

    void StartMonitor()
    {
        if (lastEvidence == null || MonRunning) return;
        var ev = lastEvidence; monCts = new(); var ct = monCts.Token; monPaused = paused = false; monStart = DateTime.Now;
        BtnMonPause.IsEnabled = BtnMonStop.IsEnabled = true; BtnMonPause.Content = "Pausar";
        var names = new List<string>();
        var pingTargets = new List<(string name, StatRow row, IPAddress ip)>();
        if (gwRowLive != null && IPAddress.TryParse(gwRowLive.Address, out var gip)) pingTargets.Add((gwRowLive.Title, gwRowLive, gip));
        foreach (var d in ev.Dests.Where(d => d.Resolved && d.Row.Received > 0 && IPAddress.TryParse(d.Ip, out _))) pingTargets.Add((d.Name, d.Row, IPAddress.Parse(d.Ip)));
        foreach (var t in pingTargets) { names.Add(t.name); var (n, r, ip) = t; _ = Task.Run(() => MonitorPingLoop(n, r, ip, ct)); }
        foreach (var d in ev.Dests.Where(d => Traces.ContainsKey(d.Name) && d.Ip != "")) { var ip = IPAddress.Parse(d.Ip); var rows = Traces[d.Name]; _ = Task.Run(() => MonitorTraceLoop(ip, rows, ct)); }
        _ = Task.Run(() => MonitorAnalyzeLoop(ev, ct));
        boundName = ""; CmbLive.ItemsSource = names; if (names.Count > 0) CmbLive.SelectedIndex = Math.Min(1, names.Count - 1);
        MonSection.Visibility = names.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AddEvent("Monitoramento", "Iniciado", $"acompanhando {pingTargets.Count} alvo(s) e {Traces.Count} rota(s) em tempo real (threads separadas)");
    }

    // ---- roda no pool de threads (nunca na UI) ----
    async Task MonitorPingLoop(string name, StatRow row, IPAddress ip, CancellationToken ct)
    {
        int consec = 0, n = 0; bool down = false; double sum = 0; DateTime lastSpikeT = DateTime.MinValue;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (paused) { await Task.Delay(300, ct); continue; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = await Net.ProbeAsync(ip, 2000, 128, 32, false, ct);
                long? rtt = r.Status == IPStatus.Success ? r.Rtt : null; string st = r.Status.ToString();
                uiQ.Enqueue(() => { row.Record(rtt, st, false); dirty.Add(row); RecordSample(name, rtt ?? -1); });
                if (rtt == null) { if (++consec == 3 && !down) { down = true; PostEvent(name, "⛔ Queda", "3 pacotes seguidos sem resposta"); } }
                else
                {
                    if (down) { down = false; PostEvent(name, "✅ Recuperado", $"voltou a responder ({rtt} ms)"); }
                    consec = 0; n++; sum += rtt.Value; double avg = sum / n;
                    if (n > 20 && rtt > Math.Max(100, avg * 3) && (DateTime.Now - lastSpikeT).TotalSeconds > 30) { lastSpikeT = DateTime.Now; PostEvent(name, "⚡ Pico de latência", $"{rtt} ms (média {avg:F0} ms)"); }
                }
                await Task.Delay(Math.Max(0, 1000 - (int)sw.ElapsedMilliseconds), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task MonitorTraceLoop(IPAddress ip, List<StatRow> rows, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (paused) { await Task.Delay(300, ct); continue; }
                var snap = rows.ToList();
                var res = await Task.WhenAll(snap.Select(async row =>
                {
                    var p = await Net.ProbeAsync(ip, 1500, row.Index, 32, false, ct);
                    return (row, p, ok: Net.IsHopReply(p.Status));
                }));
                uiQ.Enqueue(() =>
                {
                    foreach (var (row, p, ok) in res)
                    {
                        if (ok && row.Address == "*" && p.From != null) row.Address = p.From.ToString();
                        row.Record(ok ? p.Rtt : null, ok ? null : p.Status.ToString(), false); dirty.Add(row);
                    }
                });
                await Task.Delay(1200, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Reavalia o diagnóstico a cada 15 s numa thread própria e só envia o resultado final para a UI.</summary>
    async Task MonitorAnalyzeLoop(Evidence ev, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(15000, ct);
                if (paused) continue;
                var gw = gwRowLive;
                if (gw != null) { ev.GwSent = gw.Sent; ev.GwRecv = gw.Received; ev.GwLoss = gw.Loss; ev.GwAvg = gw.Avg; ev.GwJitter = gw.Jitter; }
                foreach (var d in ev.Dests) { if (Traces.TryGetValue(d.Name, out var rows)) Diagnosis.FillTrace(d, rows.ToList(), d.TraceReached); }
                var rep = Diagnosis.Analyze(ev);
                uiQ.Enqueue(() =>
                {
                    var prev = lastReport?.Top;
                    if (prev != null && rep.Top.Id != prev.Id) AddEvent("Diagnóstico", "🔄 Mudou", $"de “{prev.Title}” para “{rep.Top.Title}” ({rep.Top.Confidence}%)");
                    lastReport = rep; Render(rep, ev, false);
                });
            }
        }
        catch (OperationCanceledException) { }
    }

    void UpdateMonStatus()
    {
        if (lastEvidence == null) return;
        var el = monAccum + (MonRunning && !monPaused ? DateTime.Now - monStart : TimeSpan.Zero);
        TxtMonStatus.Text = (MonRunning ? (monPaused ? "⏸ Pausado" : "🟢 Monitorando ao vivo") : "⚪ Monitoramento parado") + $" • {el:hh\\:mm\\:ss} • {monSamples} amostras • {Events.Count} evento(s)";
    }

    void BtnMonPause_Click(object s, RoutedEventArgs e)
    {
        if (!MonRunning) { StartMonitor(); return; }
        if (monPaused) { monPaused = paused = false; monStart = DateTime.Now; BtnMonPause.Content = "Pausar"; AddEvent("Monitoramento", "Retomado", ""); }
        else { monPaused = paused = true; monAccum += DateTime.Now - monStart; BtnMonPause.Content = "▶  Retomar"; AddEvent("Monitoramento", "Pausado", ""); }
    }

    void BtnMonStop_Click(object s, RoutedEventArgs e) => StopMonitor(true);

    void StopMonitor(bool log)
    {
        if (!MonRunning) return;
        if (!monPaused) monAccum += DateTime.Now - monStart;
        monCts!.Cancel(); monPaused = paused = false; BtnMonPause.Content = "▶  Reiniciar"; BtnMonPause.IsEnabled = true; BtnMonStop.IsEnabled = false;
        if (log) AddEvent("Monitoramento", "Parado", $"{monSamples} amostras registradas");
        UpdateMonStatus();
    }

}

public partial class MainWindow
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint code);
    /// <summary>Modo de autoteste (linha de comando: --selftest PASTA): roda tudo, monitora 30 s, grava relatórios e sai.</summary>
    void MaybeSelfTest()
    {
        var a = Environment.GetCommandLineArgs(); int i = Array.IndexOf(a, "--selftest");
        if (i < 0 || i + 1 >= a.Length) return;
        var dir = a[i + 1]; Directory.CreateDirectory(dir);
        Loaded += async (_, _) =>
        {
            var swSt = System.Diagnostics.Stopwatch.StartNew();
            BtnFull_Click(this, new RoutedEventArgs());
            while (lastReport == null || !MonRunning) await Task.Delay(200);
            File.WriteAllText(System.IO.Path.Combine(dir, "tempo.txt"), $"{swSt.Elapsed.TotalSeconds:F1}");
            await Task.Delay(30000);
            var data = BuildReportData(); data.Machine = Environment.MachineName;
            File.WriteAllText(System.IO.Path.Combine(dir, "relatorio-completo.html"), await Task.Run(() => HtmlReport.Build(data)), new System.Text.UTF8Encoding(true));
            var dataA = BuildReportData(); dataA.Anonymized = true; dataA.Machine = "(oculto)";
            File.WriteAllText(System.IO.Path.Combine(dir, "relatorio-anonimo.html"), Redact(await Task.Run(() => HtmlReport.Build(dataA)), Redactions(), true), new System.Text.UTF8Encoding(true));
            File.WriteAllText(System.IO.Path.Combine(dir, "amostras.csv"), HtmlReport.Csv(SamplesBy), new System.Text.UTF8Encoding(true));
            await Task.Delay(1500); while (!BtnLookup.IsEnabled) await Task.Delay(300);
            File.WriteAllText(System.IO.Path.Combine(dir, "nat.txt"), (lastEvidence?.Nat?.Short ?? "?") + "\n" + TxtLookupStatus.Text);
            async Task Shot(string tag, Action prep)
            {
                prep(); await Task.Delay(1300); UpdateLayout(); RefreshMonitorUi(); await Task.Delay(300);
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(this); var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = File.Create(System.IO.Path.Combine(dir, $"janela-{tag}.png"))) enc.Save(fs);
                bmp.Clear(); GC.Collect(); GC.WaitForPendingFinalizers(); // .NET Framework: libera recursos nativos do WPF antes de sair
            }
            await Shot("1-topo", () => MainScroll.ScrollToTop());
            await Shot("2-rotas", () => RoutesSection.BringIntoView());
            await Shot("3-latencia", () => MonSection.BringIntoView());
            await Shot("0-config", () => { MainScroll.ScrollToTop(); BtnCfg.IsChecked = true; });
            BtnCfg.IsChecked = false;
            await Shot("4-detalhes", () => { ExpDet.IsExpanded = true; ExpConn.IsExpanded = true; ExpDet.BringIntoView(); });
            // Só no autoteste: o WPF do .NET Framework às vezes corrompe o heap ao destruir as capturas (RenderTargetBitmap)
            // no encerramento. Tudo já foi gravado em disco, então encerra direto sem a destruição nativa do WPF.
            TerminateProcess(System.Diagnostics.Process.GetCurrentProcess().Handle, 0);
        };
    }
}
