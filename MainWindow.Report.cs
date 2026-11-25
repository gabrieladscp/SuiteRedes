using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
namespace SuiteRedes;

/// <summary>Uma ocorrência detectada em tempo real (fica no histórico e vai para o relatório).</summary>
public sealed class EventRec
{
    public DateTime T { get; init; } public RouteVm Route { get; init; } = null!; public Sev Sev { get; init; } public string Text { get; init; } = "";
    public string TimeTxt => T.ToString("HH:mm:ss");
    public Brush Brush => Sev switch { Sev.Error => Pal.Err, Sev.Warn => Pal.Warn, Sev.Info => Pal.Info, _ => Pal.Ok };
    public string SevTxt => Sev switch { Sev.Error => "Crítico", Sev.Warn => "Atenção", Sev.Info => "Informação", _ => "Normalizado" };
}

public partial class MainWindow
{
    readonly List<EventRec> Events = new();
    sealed class Det { public int Fail; public bool Down, LossOn, SpikeOn; public DateTime DownSince; public int LastFirst = -1, Streak; }
    readonly Dictionary<RouteVm, Det> det = new();
    readonly Dictionary<StatRow, (string ip, int n)> pendingChange = new();
    Det D(RouteVm r) { if (!det.TryGetValue(r, out var d)) det[r] = d = new Det(); return d; }
    void Ev(RouteVm r, Sev s, string t) { Events.Add(new EventRec { T = DateTime.Now, Route = r, Sev = s, Text = t }); if (Events.Count > 2000) Events.RemoveRange(0, 500); }

    static List<long?> Tail(StatRow h, int n) => h.History.Skip(Math.Max(0, h.History.Count - n)).ToList();
    static double RecentLoss(StatRow h, int n) { var t = Tail(h, n); return t.Count == 0 ? 0 : 100.0 * t.Count(x => x == null) / t.Count; }
    static double RecentAvg(StatRow h, int n) { var t = Tail(h, n).Where(x => x.HasValue).Select(x => (double)x!.Value).ToList(); return t.Count == 0 ? -1 : t.Average(); }
    /// <summary>Mediana das últimas medições guardadas (até 300 ≈ 5 min). Com quantidade par, média dos dois centrais.</summary>
    static double Median(StatRow h)
    {
        var (at, val) = h.MedianCache; if (at == h.Sent) return val;
        var v = h.History.Where(x => x.HasValue).Select(x => (double)x!.Value).OrderBy(x => x).ToList();
        double m = v.Count == 0 ? -1 : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
        h.MedianCache = (h.Sent, m); return m;
    }
    /// <summary>O mesmo provedor do cliente: mesmo ASN, ou nome da organização com palavra significativa em comum (provedores com vários ASNs).</summary>
    bool SameProvider(StatRow h)
    {
        if (SameAs(h.Asn, pubInfo?.Asn)) return true;
        var mine = Words((pubInfo?.Org ?? "") + " " + (pubInfo?.Isp ?? "")); if (mine.Count == 0 || h.Org == "") return false;
        return Words(h.Org).Overlaps(mine);
    }
    static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase) { "TELECOM", "TELECOMUNICACOES", "TELECOMUNICAÇÕES", "LTDA", "LTDA.", "SERVICOS", "SERVIÇOS", "COMUNICACOES", "COMUNICAÇÕES", "INTERNET", "NETWORK", "NETWORKS", "BRASIL", "BRAZIL", "PROVEDOR", "TECNOLOGIA", "SOLUCOES", "SOLUÇÕES", "INC.", "INC", "S.A.", "EIRELI", "MULTIMIDIA", "MULTIMÍDIA", "COMERCIO", "COMÉRCIO" };
    static HashSet<string> Words(string s) => new(System.Text.RegularExpressions.Regex.Split(s.ToUpperInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length >= 4 && !Generic.Contains(w) && !w.StartsWith("AS") || (w.Length >= 4 && w.StartsWith("AS") && !w.Skip(2).All(char.IsDigit) && !Generic.Contains(w))), StringComparer.OrdinalIgnoreCase);
    static string HopName(StatRow h) { var o = OrgOf(h); return $"salto {h.Index} ({h.Address}{(o != "" ? ", " + o : "")})"; }

    /// <summary>Roda a cada rodada de medição: quedas, perda recente, picos de latência (comparados com o normal da própria rota).</summary>
    void Detect(RouteVm r)
    {
        var d = r.Dest; if (d == null || d.History.Count == 0) return; var s = D(r); var hops = r.Hops;
        bool lost = d.History[d.History.Count - 1] == null;
        if (lost)
        {
            if (++s.Fail == 3 && !s.Down)
            {
                s.Down = true; s.DownSince = DateTime.Now.AddSeconds(-3);
                var lastOk = hops.Take(hops.Count - 1).LastOrDefault(h => Tail(h, 5).Any(x => x != null));
                Ev(r, Sev.Error, "Queda: o destino parou de responder (3 testes seguidos)." + (lastOk != null ? $" Último ponto que respondeu nos últimos 5 testes: {HopName(lastOk)}." : " Nenhum ponto do caminho respondeu nos últimos 5 testes."));
            }
            return;
        }
        if (s.Down) { Ev(r, Sev.Ok, $"Voltou a responder depois de {(DateTime.Now - s.DownSince).TotalSeconds:F0} s fora do ar."); s.Down = false; }
        s.Fail = 0;

        // perda nos últimos 30 s (separada da média da sessão, para ver o que está acontecendo agora)
        if (d.Sent >= 20)
        {
            double l30 = RecentLoss(d, 30);
            if (!s.LossOn && l30 >= 10)
            {
                s.LossOn = true;
                int at = -1; for (int i = 0; i < hops.Count; i++) { if (Tail(hops[i], 30).All(x => x == null)) continue; if (RecentLoss(hops[i], 30) >= 8 && hops.Skip(i + 1).All(x => RecentLoss(x, 30) >= 8)) { at = i; break; } }
                Ev(r, l30 >= 25 ? Sev.Error : Sev.Warn, $"Perda de {l30:F0}% nos últimos 30 testes." + (at >= 0 && at < hops.Count - 1 ? $" Começa no {HopName(hops[at])} e segue até o destino." : ""));
            }
            else if (s.LossOn && l30 < 3) { s.LossOn = false; Ev(r, Sev.Ok, "Perda normalizada (abaixo de 3% nos últimos 30 testes)."); }
        }
        // pico de latência: média dos últimos 5 s comparada com a mediana da própria rota
        if (d.Received >= 30)
        {
            double med = Median(d), rec = RecentAvg(d, 5);
            if (!s.SpikeOn && rec > med * 1.8 && rec - med > 40)
            {
                s.SpikeOn = true;
                var at = hops.FirstOrDefault(h => h.Received >= 10 && RecentAvg(h, 5) - Median(h) > 40 && hops.Skip(hops.IndexOf(h) + 1).Where(x => x.Received >= 10).All(x => RecentAvg(x, 5) - Median(x) > 30));
                Ev(r, Sev.Warn, $"Latência subiu para {rec:F0} ms (o normal desta rota é {med:F0} ms)." + (at != null && at != d ? $" O aumento começa no {HopName(at)}." : ""));
            }
            else if (s.SpikeOn && rec < med * 1.3) { s.SpikeOn = false; Ev(r, Sev.Ok, $"Latência voltou ao normal ({rec:F0} ms)."); }
        }
    }

    /// <summary>Um salto passou a responder por outro roteador: só registra depois de 3 confirmações seguidas.</summary>
    void RouteChange(RouteVm r, StatRow h, string ip)
    {
        var c = pendingChange.TryGetValue(h, out var p) && p.ip == ip ? (ip, p.n + 1) : (ip, 1);
        pendingChange[h] = c; if (c.Item2 < 3) return;
        pendingChange.Remove(h); var old = h.Address; h.Address = ip; h.Host = ""; h.HostChecked = false; h.Org = ""; h.Asn = ""; h.Reset();
        Ev(r, Sev.Info, $"Rota mudou no salto {h.Index}: {old} → {ip} (confirmado em 3 testes seguidos). As medições deste salto recomeçam do zero.");
        _ = NetInfo.EnrichHopsAsync(new[] { h }, CancellationToken.None);
        if (System.Net.IPAddress.TryParse(ip, out var a)) _ = Reverse(h, a);
    }

    void TrackProblem(RouteVm r)
    {
        if (r.Phase != "Ao vivo") return; var s = D(r);
        // histerese: só registra quando o estado se mantém por 5 s (evita alertas que piscam)
        bool now = r.First >= 0, was = s.LastFirst >= 0;
        if (now && was && r.First != s.LastFirst) { if (++s.Streak >= 5) { s.Streak = 0; s.LastFirst = r.First; Ev(r, r.Worst, $"O início do problema mudou para {(r.First == r.Hops.Count - 1 ? "o destino" : "o " + HopName(r.Hops[r.First]))}: {r.Why}."); } return; }
        if (now == was) { s.Streak = 0; return; }
        if (++s.Streak < 5) return;
        s.Streak = 0; s.LastFirst = r.First;
        if (now) Ev(r, r.Worst, $"Problema identificado {(r.First == r.Hops.Count - 1 ? "no destino" : "a partir do " + HopName(r.Hops[r.First]))}: {r.Why}.");
        else Ev(r, Sev.Ok, "A rota voltou a ficar saudável.");
    }

    void ShowEvents(RouteVm r)
    {
        var l = Events.Where(e => e.Route == r).ToList(); l.Reverse();
        if (LstEvents.Items.Count != l.Count || !ReferenceEquals(LstEvents.Tag, r)) { LstEvents.ItemsSource = l; LstEvents.Tag = r; }
        TxtNoEv.Visibility = l.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtEvCount.Text = l.Count == 0 ? "" : $"({l.Count})";
    }

    // ---------- exportação ----------
    void BtnExport_Click(object s, RoutedEventArgs e)
    {
        if (Routes.Count == 0) return;
        var dlg = new SaveFileDialog { Filter = "Relatório para leitura (*.html)|*.html|Amostras brutas para planilha (*.csv)|*.csv", FileName = $"relatorio-rotas-{DateTime.Now:yyyyMMdd-HHmm}" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            bool csv = dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(dlg.FileName, csv ? BuildCsv() : BuildHtml(), new UTF8Encoding(true));
            if (!csv && MessageBox.Show("Relatório salvo. Abrir agora?", "Suíte de Redes", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
        catch (Exception ex) { App.Log(ex); MessageBox.Show("Não foi possível salvar: " + ex.Message, "Suíte de Redes"); }
    }

    string BuildCsv()
    {
        var sb = new StringBuilder("data_hora;destino;salto;ip;latencia_ms;perdido\r\n");
        foreach (var r in Routes.OrderBy(x => x.Order))
            foreach (var h in r.Hops)
                for (int k = 0; k < h.History.Count && k < h.Times.Count; k++)
                    sb.Append($"{h.Times[k]:yyyy-MM-dd HH:mm:ss};{r.Host};{(h == r.Dest ? "destino" : h.Index.ToString())};{h.Address};{h.History[k]?.ToString() ?? ""};{(h.History[k] == null ? 1 : 0)}\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Relatório objetivo, sem gráficos: conclusão, problemas (onde, medido, como resolver), tabela por destino,
    /// rede local, detalhe salto a salto só das rotas com problema e as ocorrências. Dados brutos ficam no CSV.
    /// </summary>
    string BuildHtml()
    {
        string E(string? x) => System.Net.WebUtility.HtmlEncode(x ?? "");
        string F(double v) => v.ToString("F0", CultureInfo.InvariantCulture);
        string P(double v) => v.ToString("F1", CultureInfo.GetCultureInfo("pt-BR")) + "%";
        string Cls(Sev s) => s switch { Sev.Error => "err", Sev.Warn => "warn", _ => "ok" };
        var end = ended ?? DateTime.Now; var dur = end - started;
        var list = Routes.OrderBy(x => x.Order).ToList();
        int noData = list.Count(r => !r.Failed && !r.Any);
        var badRoutes = list.Where(r => r.Failed || (r.Any && r.First >= 0)).ToList();
        var badChecks = checks.Values.Where(c => c.Sev >= Sev.Warn).OrderByDescending(c => c.Sev).ToList();
        int problems = badRoutes.Count + badChecks.Count;
        bool anyErr = badRoutes.Any(r => r.Failed || r.Worst == Sev.Error) || badChecks.Any(c => c.Sev == Sev.Error);

        var sb = new StringBuilder();
        sb.Append(@"<!doctype html><html lang=""pt-BR""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""><title>Relatório de rede</title><style>
body{margin:0;background:#fff;color:#1a1a1a;font:14px/1.5 'Segoe UI',system-ui,sans-serif}main{max-width:900px;margin:0 auto;padding:28px 20px 48px}
h1{font-size:22px;margin:0 0 2px}h2{font-size:16px;margin:26px 0 8px;padding-bottom:4px;border-bottom:1px solid #ddd}
.mut{color:#666}table{width:100%;border-collapse:collapse;font-size:13.5px}th{text-align:left;font-weight:600;color:#555;padding:6px 8px;border-bottom:2px solid #ddd}
td{padding:6px 8px;border-bottom:1px solid #eee;vertical-align:top}td.n,th.n{text-align:right;white-space:nowrap}
.res{padding:12px 14px;border-radius:6px;font-weight:600;margin:16px 0}.res.ok{background:#e8f5ee}.res.warn{background:#fdf3dc}.res.err{background:#fde8e7}
.ok{color:#1e7d4f}.warn{color:#9a6700}.err{color:#c62828}ul{margin:4px 0;padding-left:20px}li{margin:6px 0}
@media print{main{padding:0}}
</style></head><body><main>");
        sb.Append($"<h1>Relatório de rede</h1><div class=mut>{started:dd/MM/yyyy HH:mm} a {end:HH:mm} · {dur:hh\\:mm\\:ss} de medição</div>");

        // identificação da conexão
        if (pubInfo is { Ok: true } pi)
        {
            var parts = new List<string> { $"IP público <b>{E(pi.Ip)}</b>" };
            if (LocalIp() is string lip) parts.Add($"IP privado <b>{E(lip)}</b>");
            if (!string.IsNullOrEmpty(pi.IspName)) parts.Add($"Provedor <b>{E(pi.IspName)}</b>{(!string.IsNullOrEmpty(pi.Asn) ? " (" + E(pi.Asn) + ")" : "")}");
            if (!string.IsNullOrEmpty(pi.Place)) parts.Add($"Local aproximado <b>{E(pi.Place)}</b>");
            sb.Append($"<div style=\"margin-top:8px\">{string.Join(" · ", parts)}</div>");
        }

        // conclusão
        string resTxt = problems == 0 && (noData > 0 || checks.Count == 0)
            ? "Nenhum problema encontrado até agora, mas a análise está incompleta" + (noData > 0 ? $": {noData} destino(s) sem dados suficientes" : "") + (checks.Count == 0 ? (noData > 0 ? "; " : ": ") + "rede local não verificada" : "") + "."
            : problems == 0 ? $"Nenhum problema encontrado. Rede local e os {list.Count} destinos funcionaram normalmente."
            : $"{problems} problema(s) encontrado(s)" + (checks.TryGetValue("gw", out var g) && g.Sev >= Sev.Warn ? ", incluindo na rede local (afeta todos os destinos)." : ".");
        sb.Append($"<div class=\"res {(problems == 0 ? "ok" : anyErr ? "err" : "warn")}\">{E(resTxt)}</div>");

        // problemas e como resolver
        if (problems > 0)
        {
            sb.Append("<h2>Problemas e como resolver</h2><ul>");
            foreach (var c in badChecks)
                sb.Append($"<li><b class={Cls(c.Sev)}>{E(c.Area)}: {E(c.Title)}</b><br>{E(c.Detail)}<br><span class=mut>Medido: {E(c.Evidence)}</span>{(c.Fix != "" ? $"<br><b>Como resolver:</b> {E(c.Fix)}" : "")}</li>");
            foreach (var r in badRoutes)
            {
                var (v, _) = Verdict(r); var sev = r.Failed ? Sev.Error : r.Worst; string where = "";
                if (!r.Failed && r.First >= 0 && r.First < r.Hops.Count)
                {
                    var h = r.Hops[r.First];
                    where = $"Onde começa: {(r.First == r.Hops.Count - 1 ? "no destino" : "salto " + h.Index)} — {h.Address}{(h.Host != "" ? " (" + h.Host + ")" : "")}{(OrgOf(h) != "" ? ", " + OrgOf(h) : "")}. Latência {F(Median(h))} ms, perda {P(h.Loss)} em {h.Sent} testes.";
                }
                sb.Append($"<li><b class={Cls(sev)}>{E(r.Title)}</b> <span class=mut>({E(r.Host)})</span><br>{E(v)}{(where != "" ? $"<br><span class=mut>{E(where)}</span>" : "")}</li>");
            }
            sb.Append("</ul>");
        }

        // destinos
        sb.Append("<h2>Destinos</h2><table><tr><th>Destino</th><th class=n>Latência</th><th class=n>Mín.–máx.</th><th class=n>Perda</th><th class=n>Oscilação</th><th>Situação</th></tr>");
        foreach (var r in list)
        {
            var d = r.Dest; bool bad = r.Failed || (r.Any && r.First >= 0);
            string sit = r.Failed ? "Falhou" : !r.Any ? "Sem dados" : bad ? (r.Worst == Sev.Error ? "Problema" : "Atenção") : "Normal";
            string sc = r.Failed ? "err" : !r.Any ? "mut" : bad ? Cls(r.Worst) : "ok";
            bool lossBad = d != null && d.Sent >= 5 && d.Loss >= LossWarn;
            sb.Append($"<tr><td><b>{E(r.Title)}</b><br><span class=mut>{E(r.Host)}{(r.Ip != null && r.Ip.ToString() != r.Host ? " · " + r.Ip : "")}</span></td>"
                + $"<td class=n>{(d?.Received > 0 ? F(Median(d)) + " ms" : "—")}</td>"
                + $"<td class=n>{(d?.Received > 0 ? F(d.Min) + "–" + F(d.Max) + " ms" : "—")}</td>"
                + $"<td class=\"n{(lossBad ? " err" : "")}\">{(d?.Sent > 0 ? P(d.Loss) : "—")}</td>"
                + $"<td class=n>{(d?.Received > 1 ? F(d.Jitter) + " ms" : "—")}</td>"
                + $"<td class={sc}><b>{sit}</b>{(r.Tcp ? "<br><span class=mut>medido por TCP</span>" : "")}</td></tr>");
        }
        sb.Append("</table><div class=mut style=\"font-size:12.5px;margin-top:6px\">Latência: mediana dos últimos ~5 min. Mín.–máx. e perda: sessão inteira. Perda a partir de 3% é considerada problema.</div>");

        // rede local
        var okChecks = checks.Values.Where(c => c.Sev < Sev.Warn).Select(c => c.Title).ToList();
        if (checks.Count > 0)
            sb.Append($"<h2>Rede local</h2><div>{(badChecks.Count == 0 ? "<b class=ok>Sem problemas.</b>" : $"<b class=warn>{badChecks.Count} problema(s), descritos acima.</b>")}</div>{(okChecks.Count > 0 ? $"<div class=mut>OK: {E(string.Join("; ", okChecks))}.</div>" : "")}");

        // salto a salto apenas das rotas com problema
        foreach (var r in badRoutes.Where(x => x.Hops.Count > 0))
        {
            sb.Append($"<h2>Rota até {E(r.Title)}</h2><table><tr><th class=n>Salto</th><th>IP</th><th>Nome / rede</th><th class=n>Latência</th><th class=n>Perda</th></tr>");
            for (int i = 0; i < r.Hops.Count; i++)
            {
                var h = r.Hops[i]; var (sv, sil, _) = Assess(r.Hops, i); bool prob = !sil && sv >= Sev.Warn; bool last = i == r.Hops.Count - 1;
                if (h.Address is "*" or "?") { sb.Append($"<tr><td class=n>{h.Index}</td><td colspan=4 class=mut>não responde a testes (normal em muitos roteadores)</td></tr>"); continue; }
                var name = string.Join(" · ", new[] { h.Host, OrgOf(h) }.Where(x => !string.IsNullOrEmpty(x)));
                sb.Append($"<tr{(prob ? $" class={Cls(sv)}" : "")}><td class=n>{(last ? "destino" : h.Index.ToString())}</td><td>{(prob ? "<b>" + E(h.Address) + "</b>" : E(h.Address))}</td><td>{E(name)}</td><td class=n>{(h.Received > 0 ? F(Median(h)) + " ms" : "—")}</td><td class=n>{(h.Sent > 0 ? P(h.Loss) : "—")}</td></tr>");
            }
            sb.Append("</table>");
        }

        // ocorrências (só alertas; as 30 mais recentes)
        var evs = Events.Where(e => e.Sev >= Sev.Warn).ToList();
        if (evs.Count > 0)
        {
            sb.Append("<h2>Ocorrências</h2><table><tr><th>Hora</th><th>Destino</th><th>O que aconteceu</th></tr>");
            foreach (var ev in evs.Skip(Math.Max(0, evs.Count - 30))) sb.Append($"<tr><td class=n>{ev.T:HH:mm:ss}</td><td>{E(ev.Route.Title)}</td><td class={Cls(ev.Sev)}>{E(ev.Text)}</td></tr>");
            sb.Append("</table>");
            if (evs.Count > 30) sb.Append($"<div class=mut>Mostrando as 30 mais recentes de {evs.Count}.</div>");
        }
        sb.Append("<p class=mut style=\"margin-top:28px;font-size:12.5px\">Rotas sem problema não são detalhadas. As medições completas, segundo a segundo, estão na exportação CSV. Gerado pela Suíte de Redes.</p></main></body></html>");
        return sb.ToString();
    }
}
