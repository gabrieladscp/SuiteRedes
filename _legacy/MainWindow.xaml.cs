using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using SuiteRedes.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
namespace SuiteRedes;

public record HistEntry(DateTime Time, string Title, string Sev, double GwLoss, double InetLoss, double InetAvg, double Speed);

public partial class MainWindow : Window
{
    // Destinos: um por linha, "host = rótulo" (rótulo opcional). Padrão cobre Brasil (várias regiões), anycast global e exterior.
    const int MaxDests = 16;
    static readonly (string Host, string Label)[] DefaultDests =
    {
        ("rds.skytins.com.br", "Servidor da empresa"),
        ("200.160.4.6", "NIC.br · São Paulo (sem CDN)"),
        ("a.st1.ntp.br", "NTP.br · São Paulo"),
        ("www.ufrj.br", "UFRJ · Rio de Janeiro"),
        ("www.ufrgs.br", "UFRGS · Porto Alegre"),
        ("www.ufpe.br", "UFPE · Recife"),
        ("8.8.8.8", "Google DNS (anycast)"),
        ("1.1.1.1", "Cloudflare DNS (anycast)"),
        ("www.arin.net", "ARIN · EUA"),
        ("90.130.70.73", "Tele2 · Suécia"),
        ("ping.online.net", "Scaleway · França"),
        ("www.u-tokyo.ac.jp", "Univ. de Tóquio · Japão"),
    };
    readonly Dictionary<string, string> DestLabels = new(StringComparer.OrdinalIgnoreCase);
    static bool DestOk(string t) => t.Length is > 0 and <= 253 && t.All(c => (c < 128 && char.IsLetterOrDigit(c)) || c == '.' || c == '-');
    List<(string Host, string Label)> ParseDests(string text)
    {
        var res = new List<(string, string)>();
        foreach (var raw in text.Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim(); if (line.Length == 0 || line.StartsWith("#")) continue;
            int eq = line.IndexOf('='); var host = (eq >= 0 ? line[..eq] : line).Trim(); var label = eq >= 0 ? line[(eq + 1)..].Trim() : "";
            if (DestOk(host) && !res.Any(x => x.Item1.Equals(host, StringComparison.OrdinalIgnoreCase))) res.Add((host, label));
        }
        return res.Take(MaxDests).ToList();
    }
    string DestsText
    {
        get
        {
            var l = ParseDests(TxtDests.Text); DestLabels.Clear();
            foreach (var (h, lb) in l) if (lb != "") DestLabels[h] = lb;
            return string.Join(((char)10).ToString(), l.Select(x => x.Host));
        }
    }
    string DestLabel(string host) => DestLabels.TryGetValue(host, out var l) ? l : "";
    static string Fmt(IEnumerable<(string Host, string Label)> l) => string.Join(Environment.NewLine, l.Select(x => x.Label == "" ? x.Host : $"{x.Host} = {x.Label}"));
    void InitDestBoxes() { }
    static string CfgPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuiteRedes", "destinos.txt");
    void LoadDests()
    {
        string v = Fmt(DefaultDests);
        try { if (File.Exists(CfgPath) && ParseDests(File.ReadAllText(CfgPath)).Count > 0) v = File.ReadAllText(CfgPath); } catch { }
        TxtDests.Text = v; _ = DestsText;
    }
    void PopCfg_Closed(object? s, EventArgs e)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(CfgPath)!); File.WriteAllText(CfgPath, Fmt(ParseDests(TxtDests.Text))); } catch (Exception ex) { App.Log(ex); }
    }
    void BtnCfgReset_Click(object s, RoutedEventArgs e) => TxtDests.Text = Fmt(DefaultDests);
    readonly ObservableCollection<StatRow> DestRows = new();
    readonly ObservableCollection<DnsRow> DnsRows = new();
    readonly ObservableCollection<HttpRow> HttpRows = new();
    readonly Dictionary<string, List<StatRow>> Traces = new();
    CancellationTokenSource? fullCts;
    public static readonly DependencyProperty HypColsProperty = DependencyProperty.Register(nameof(HypCols), typeof(int), typeof(MainWindow), new PropertyMetadata(1));
    public int HypCols { get => (int)GetValue(HypColsProperty); set => SetValue(HypColsProperty, value); }
    public static readonly DependencyProperty StageColsProperty = DependencyProperty.Register(nameof(StageCols), typeof(int), typeof(MainWindow), new PropertyMetadata(4));
    public int StageCols { get => (int)GetValue(StageColsProperty); set => SetValue(StageColsProperty, value); }
    public static readonly DependencyProperty InfoColsProperty = DependencyProperty.Register(nameof(InfoCols), typeof(int), typeof(MainWindow), new PropertyMetadata(3));
    public int InfoCols { get => (int)GetValue(InfoColsProperty); set => SetValue(InfoColsProperty, value); }
    void Window_SizeChanged(object s, SizeChangedEventArgs e)
    {
        double avail = ActualWidth - 214 - 56;
        HypCols = Compat.Clamp((int)(avail / 520), 1, Math.Max(1, hypColsMax));
        StageCols = Compat.Clamp((int)(avail / 170), 2, 7); InfoCols = Compat.Clamp((int)(avail / 250), 2, 5);
    }
    Report? lastReport; Evidence? lastEvidence; List<InfoItem> infoLocal = new(), infoNet = new(), infoRoute = new(); string lastHistory = "";

    public MainWindow()
    {
        InitializeComponent();
        GridDests.ItemsSource = DestRows; GridDns.ItemsSource = DnsRows;
        GridHttp.ItemsSource = HttpRows;
        InitMonitor(); MaybeSelfTest();
        InitDestBoxes(); LoadDests();
        PreviewKeyDown += (_, k) => { if (k.Key == System.Windows.Input.Key.F5 && BtnFull.IsEnabled && !(System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox)) { BtnFull_Click(this, new RoutedEventArgs()); k.Handled = true; } else if (k.Key == System.Windows.Input.Key.Escape && BtnFullStop.IsEnabled) { BtnFullStop_Click(this, new RoutedEventArgs()); k.Handled = true; } };
        Closing += (_, _) => monCts?.Cancel();
    }

    // ---------- utilitários ----------
    static void Error(string m) => MessageBox.Show(m, "Suíte de Redes", MessageBoxButton.OK, MessageBoxImage.Warning);
    void BtnFullStop_Click(object s, RoutedEventArgs e) => fullCts?.Cancel();

    // ---------- histórico (comparação com a verificação anterior) ----------
    static double Num(JsonElement e, string n) => e.TryGetProperty(n, out var v) && double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
    static string HistPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SuiteRedes", "history.json");
    string SaveAndCompare(Report r, Evidence ev)
    {
        try
        {
            var list = new List<HistEntry>();
            if (File.Exists(HistPath)) list = JsonDocument.Parse(File.ReadAllText(HistPath)).RootElement.EnumerateArray().Select(e => new HistEntry(DateTime.Parse(e.GetProperty("Time").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind), e.GetProperty("Title").GetString() ?? "", e.GetProperty("Sev").GetString() ?? "", Num(e, "GwLoss"), Num(e, "InetLoss"), Num(e, "InetAvg"), Num(e, "Speed"))).ToList();
            var prev = list.LastOrDefault();
            list.Add(new HistEntry(DateTime.Now, r.Top.Title, r.Top.Severity.ToString(), ev.GwLoss, ev.InetLoss, ev.InetAvg, ev.SpeedMbps ?? 0));
            Directory.CreateDirectory(Path.GetDirectoryName(HistPath)!);
            File.WriteAllText(HistPath, JsonSerializer.Serialize(list.TakeLast(30)));
            if (prev == null) return "Primeira verificação registrada. Nas próximas vezes o programa compara com esta.";
            var sb = new StringBuilder($"Comparação com a verificação anterior ({prev.Time:dd/MM HH:mm}): ");
            sb.Append(prev.Title == r.Top.Title ? "diagnóstico igual. " : $"antes: “{prev.Title}”. ");
            sb.Append($"Perda até a internet {prev.InetLoss:F1}% → {ev.InetLoss:F1}%; tempo médio {prev.InetAvg:F0} → {ev.InetAvg:F0} ms");
            if (prev.Speed > 0 && ev.SpeedMbps is double sp) sb.Append($"; velocidade {prev.Speed:F0} → {sp:F0} Mbps");
            return sb.Append('.').ToString();
        }
        catch { return ""; }
    }

    // ---------- relatório ----------
    async void BtnExport_Click(object s, RoutedEventArgs e)
    {
        if (lastReport == null) return;
        var dlg = new SaveFileDialog { Filter = "Relatório HTML (*.html)|*.html|Texto (*.txt)|*.txt|Amostras brutas (*.csv)|*.csv", FileName = $"relatorio-rede-{DateTime.Now:yyyyMMdd-HHmm}" };
        if (dlg.ShowDialog() != true) return;
        var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
        bool anon = ext == ".csv" || MessageBox.Show("Ocultar dados que identificam você no relatório?\n\nNome do computador, IP público, DNS reverso, endereço MAC, IPv6, rede Wi-Fi e cidade serão mascarados. Recomendado ao compartilhar o arquivo (LGPD).\n\nSim = anonimizar (recomendado)   Não = incluir os dados completos",
            "Suíte de Redes - privacidade", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.No;
        var redact = anon ? Redactions() : new List<(string, string)>();
        if (ext == ".html") { var data = BuildReportData(); data.Anonymized = anon; data.Machine = anon ? "(oculto)" : Environment.MachineName; var html = Redact(await Task.Run(() => HtmlReport.Build(data)), redact, true); File.WriteAllText(dlg.FileName, html, new UTF8Encoding(true)); if (MessageBox.Show("Relatório salvo. Abrir agora?", "Suíte de Redes", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true }); return; }
        if (ext == ".csv") { File.WriteAllText(dlg.FileName, HtmlReport.Csv(SamplesBy), new UTF8Encoding(true)); return; }
        var sb = new StringBuilder();
        sb.AppendLine($"RELATÓRIO DE REDE — {DateTime.Now:dd/MM/yyyy HH:mm:ss} — {(anon ? "computador oculto (relatório anonimizado - LGPD)" : Environment.MachineName)}");
        sb.AppendLine(new string('=', 80)); sb.AppendLine("RESUMO"); sb.AppendLine(lastReport.Narrative);
        sb.AppendLine(); sb.AppendLine("## MAPA DA CONEXÃO");
        foreach (var st in lastReport.Stages) sb.AppendLine($"  {st.Name,-18} {st.StateText,-16} {st.Detail}");
        sb.AppendLine(); sb.AppendLine("## DIAGNÓSTICO (causas, da mais provável para a menos)");
        foreach (var h in lastReport.Hyps)
        {
            sb.AppendLine($"[{h.Severity}] {h.Title} — {h.ConfText} — {h.WhoText}"); sb.AppendLine("  " + h.Summary);
            if (h.Why.Count > 0) sb.AppendLine("  Evidências:\n" + h.WhyText.Replace("\n", "\n  "));
            if (h.Steps.Count > 0) sb.AppendLine("  Como resolver:\n  " + h.StepsText.Replace("\n", "\n  "));
            sb.AppendLine();
        }
        sb.AppendLine("## DESTINOS");
        sb.AppendLine($"{"Destino",-26}{"IP",-17}{"Env",5}{"Perda",8}{"Média",10}{"Jitter",9}  Status / serviço");
        foreach (var r in DestRows) sb.AppendLine($"{r.Title,-26}{r.Address,-17}{r.Sent,5}{r.LossTxt,8}{r.AvgTxt,10}{r.JitTxt,9}  {r.Status} {r.Extra}");
        foreach (var kv in Traces)
        {
            sb.AppendLine(); sb.AppendLine($"## ROTA até {kv.Key}");
            foreach (var r in kv.Value) sb.AppendLine($"{r.Index,3} {r.Address,-16}{r.Host,-36}{r.LossTxt,8}{r.AvgTxt,10}  {r.Status}");
        }
        if (DnsRows.Count > 0) { sb.AppendLine(); sb.AppendLine("## DNS"); foreach (var d in DnsRows) sb.AppendLine($"{d.Server,-18}{d.Provider,-12}{d.Success,-6}{d.AvgTxt,-10}{d.Status,-10}{d.Answers}"); }
        if (HttpRows.Count > 0) { sb.AppendLine(); sb.AppendLine("## SITES"); foreach (var h in HttpRows) sb.AppendLine($"{h.Url,-38}{h.Status,-16}DNS {h.DnsMs} ms  1ª resp. {h.TtfbMs} ms  total {h.TotalMs} ms"); }
        File.WriteAllText(dlg.FileName, Redact(sb.ToString(), redact, false), new UTF8Encoding(true));
       
    }
}

public partial class MainWindow
{
    // ---------- LGPD: dados que identificam a pessoa/equipamento e suas máscaras ----------
    static string MaskIp(string ip) => System.Net.IPAddress.TryParse(ip, out var a)
        ? a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? string.Join(".", ip.Split('.').Take(2)) + ".x.x" : string.Join(":", ip.Split(':').Take(2)) + ":…(oculto)"
        : "(oculto)";
    List<(string from, string to)> Redactions()
    {
        var l = new List<(string, string)>();
        void Add(string? v, string m) { if (!string.IsNullOrWhiteSpace(v) && v.Length >= 3) l.Add((v.Trim(), m)); }
        Add(Environment.MachineName, "(computador oculto)"); Add(Environment.UserName, "(usuário oculto)");
        var p = lastEvidence?.Pub;
        if (p != null)
        {
            foreach (var ip in p.SeenIps.Append(p.Ip).Distinct()) Add(ip, MaskIp(ip));
            Add(p.Rdns, "(oculto)"); Add(p.Ipv6, MaskIp(p.Ipv6));
            if (p.City != "") { Add(p.Place, string.Join(", ", new[] { p.Region, p.Country }.Where(x => x != ""))); }
        }
        foreach (var i in infoLocal)
        {
            if (i.Label == "Endereço MAC") Add(i.Value, string.Join("-", i.Value.Split('-').Take(3)) + "-xx-xx-xx");
            if (i.Label == "IPv6 local") Add(i.Value, MaskIp(i.Value));
            if (i.Label == "Conexão" && i.Value.IndexOf('“') is int q and >= 0 && i.Value.IndexOf('”', q + 1) is int q2 and > 0) Add(i.Value[(q + 1)..q2], "(rede oculta)");
        }
        return l.OrderByDescending(x => x.Item1.Length).ToList();
    }
    static string Redact(string text, List<(string from, string to)> r, bool html)
    {
        foreach (var (f, t) in r)
        {
            text = text.Replace(f, t, StringComparison.OrdinalIgnoreCase);
            if (html) { var ef = System.Net.WebUtility.HtmlEncode(f); if (ef != f) text = text.Replace(ef, System.Net.WebUtility.HtmlEncode(t), StringComparison.OrdinalIgnoreCase); }
        }
        return text;
    }

    ReportData BuildReportData()
    {
        var el = monAccum + (MonRunning && !monPaused ? DateTime.Now - monStart : TimeSpan.Zero);
        return new ReportData
        {
            Rep = lastReport!, Ev = lastEvidence!, Narrative = lastReport!.Narrative, History = lastHistory,
            Dests = DestRows.ToList(), Traces = Traces.ToDictionary(k => k.Key, v => v.Value), Dns = DnsRows.ToList(), Http = HttpRows.ToList(),
            Events = Events.ToList(), Samples = SamplesBy.ToDictionary(k => k.Key, v => v.Value.ToList()),
            Local = infoLocal, Net = infoNet, Route = infoRoute, Monitored = el, SampleCount = monSamples
        };
    }
}
