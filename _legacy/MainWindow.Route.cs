using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
namespace SuiteRedes;

public class IssueVm { public string Icon { get; init; } = ""; public string Text { get; init; } = ""; public Brush Fg { get; init; } = Pal.Gray; public Brush Bg { get; init; } = Pal.GrayBg; }

sealed class PathInfo { public string Name = ""; public List<StatRow> Rows = new(); }
public partial class MainWindow
{
    StatRow? selRow; string selName = "", boundName = ""; List<StatRow> selPath = new();
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    static TimeSpan Phase(double period) => TimeSpan.FromSeconds(-(Clock.Elapsed.TotalSeconds % period));

    void CmbLive_Changed(object s, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != CmbLive) return;
        selRow = null; boundName = "";
        BindHops();
        RefreshMonitorUi();
    }

    // ---------- caminhos ----------
    PathInfo? PathFor(string name)
    {
        if (gwRowLive != null && name == gwRowLive.Title) return new PathInfo { Name = name, Rows = new List<StatRow> { gwRowLive } };
        var d = lastEvidence?.Dests.FirstOrDefault(x => x.Name == name);
        if (d == null) return null;
        if (!Traces.TryGetValue(name, out var rows) || rows.Count == 0) return new PathInfo { Name = name, Rows = new List<StatRow> { d.Row } };
        var list = rows.ToList(); if (!d.TraceReached) list.Add(d.Row);
        return new PathInfo { Name = name, Rows = list };
    }

    List<PathInfo> CurrentPaths()
    {
        var res = new List<PathInfo>();
        if (CmbLive.SelectedItem is string sel && PathFor(sel) is { } one) res.Add(one);
        return res;
    }

    void BindHops()
    {
        if (CmbLive.SelectedItem is not string n || PathFor(n) is not { } p) { boundName = ""; return; }
        selName = boundName = n; selPath = p.Rows;
        selRow = p.Rows[^1];
    }

    // ---------- avaliação de cada ponto ----------
    (Sev sev, bool silent, List<IssueVm> issues) Assess(List<StatRow> path, int i)
    {
        var r = path[i]; bool last = i == path.Count - 1; var iss = new List<IssueVm>(); Sev sev = Sev.Ok; bool silent = false;
        void Add(Sev s, string t)
        {
            iss.Add(new IssueVm
            {
                Icon = s switch { Sev.Error => "✖", Sev.Warn => "⚠", Sev.Info => "ℹ", _ => "✔" }, Text = t,
                Fg = s switch { Sev.Error => Pal.Err, Sev.Warn => Pal.Warn, Sev.Info => Pal.Info, _ => Pal.Ok },
                Bg = s switch { Sev.Error => Pal.ErrBg, Sev.Warn => Pal.WarnBg, Sev.Info => Pal.InfoBg, _ => Pal.OkBg }
            });
            if (s >= Sev.Warn && s > sev) sev = s;
        }
        if (r.Sent < 3) { silent = true; Add(Sev.Info, "Coletando amostras…"); return (sev, silent, iss); }
        if (r.Received == 0)
        {
            if (last) Add(Sev.Error, "O destino não responde ao teste (pode estar bloqueando ping).");
            else { silent = true; Add(Sev.Info, "Não responde ao teste — comum em roteadores e não indica problema por si só."); }
            return (sev, silent, iss);
        }
        var later = path.Skip(i + 1).Where(x => x.Sent >= 5).ToList();
        if (r.Sent >= 5 && r.Loss >= 5)
        {
            bool propagates = last || later.All(x => x.Loss >= 10);
            if (propagates) Add(r.Loss >= 20 ? Sev.Error : Sev.Warn, last ? $"Perda de {r.Loss:F0}% de pacotes até o destino." : $"Perda real de {r.Loss:F0}% começa neste ponto e continua até o destino.");
            else Add(Sev.Info, $"Perda de {r.Loss:F0}% aqui é o equipamento ignorando o teste (os pontos seguintes estão normais).");
        }
        if (r.Avg > 150) Add(r.Avg > 400 ? Sev.Error : Sev.Warn, $"Latência alta: {r.Avg:F0} ms em média.");
        var prev = path.Take(i).LastOrDefault(x => x.Received > 0);
        if (prev != null && r.Avg - prev.Avg > 80 && later.Where(x => x.Received > 0).All(x => x.Avg >= r.Avg * 0.7))
            Add(Sev.Warn, $"Salto de +{r.Avg - prev.Avg:F0} ms neste trecho (o atraso continua até o destino).");
        if (r.Jitter > 30) Add(Sev.Warn, $"Oscilação alta: {r.Jitter:F0} ms entre um teste e outro.");
        if (NetInfo.IsPrivate(r.Address) && i <= 2) Add(Sev.Info, "Equipamento da sua rede local (roteador/modem).");
        else if (NetInfo.IsCgnat(r.Address)) Add(Sev.Info, "CGNAT do provedor (IP compartilhado entre clientes).");
        if (iss.All(x => x.Icon == "ℹ")) iss.Insert(0, new IssueVm { Icon = "✔", Text = "Sem problemas neste ponto.", Fg = Pal.Ok, Bg = Pal.OkBg });
        return (sev, silent, iss);
    }
    static Brush SevBrush(Sev s, bool silent) => silent ? Pal.Gray : s switch { Sev.Error => Pal.Err, Sev.Warn => Pal.Warn, _ => Pal.Ok };

    // ---------- atualização periódica da página ----------
    void RefreshMonitorUi()
    {
        bool has = CmbLive.Items.Count > 0; MonSection.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has) { DrawRoutes(); return; }
        if (boundName != CmbLive.SelectedItem as string) BindHops();
        for (int i = 0; i < selPath.Count; i++)
        {
            var (sv, sil, _) = Assess(selPath, i); var rw = selPath[i];
            rw.SetHop(sil ? (rw.Received == 0 && rw.Sent >= 3 ? "Não responde (ok)" : "Coletando…") : sv == Sev.Error ? "Problema" : sv == Sev.Warn ? "Atenção" : "OK", SevBrush(sv, sil)); rw.Raise();
        }
        UpdateHeaderChip(); DrawAvail(); DrawChart(); DrawRoutes();
    }

    void UpdateHeaderChip()
    {
        Sev worst = Sev.Ok; bool any = false;
        foreach (var p in CurrentPaths()) for (int i = 0; i < p.Rows.Count; i++) { var (s, sil, _) = Assess(p.Rows, i); if (!sil) any = true; if (s > worst) worst = s; }
        string txt = !any ? "Aguardando" : worst == Sev.Error ? "Crítico" : worst == Sev.Warn ? "Atenção" : "Saudável";
        var br = !any ? Pal.Gray : SevBrush(worst, false);
        MonChip.Background = !any ? Pal.GrayBg : worst == Sev.Error ? Pal.ErrBg : worst == Sev.Warn ? Pal.WarnBg : Pal.OkBg;
        TxtMonChip.Foreground = br; TxtMonChip.Text = "●  " + txt;
    }

    // ---------- disponibilidade ----------
    void DrawAvail()
    {
        var c = AvailCanvas; c.Children.Clear(); double w = c.ActualWidth, h = c.ActualHeight; if (w < 40) return;
        if (CmbLive.SelectedItem is not string name || !SamplesBy.TryGetValue(name, out var smp)) return;
        int N = Compat.Clamp((int)(w / 9), 20, 160); double win = 15 * 60, bsec = win / N; var ok = new int[N]; var lost = new int[N]; var now = DateTime.Now; int tOk = 0, tLost = 0;
        for (int k = smp.Count - 1; k >= 0; k--)
        {
            double age = (now - smp[k].T).TotalSeconds; if (age >= win) break;
            int b = N - 1 - (int)(age / bsec); if (b < 0) break;
            if (smp[k].Rtt >= 0) { ok[b]++; tOk++; } else { lost[b]++; tLost++; }
        }
        double bw = w / N;
        for (int b = 0; b < N; b++)
        {
            var br = ok[b] + lost[b] == 0 ? Lat.Empty : lost[b] == 0 ? Pal.Ok : ok[b] == 0 ? Pal.Err : Pal.Warn;
            var r = new Rectangle { Width = Math.Max(1, bw - 1.5), Height = h, Fill = br, RadiusX = 2, RadiusY = 2 };
            Canvas.SetLeft(r, b * bw); c.Children.Add(r);
        }
        TxtAvailTitle.Text = tOk + tLost == 0 ? "Disponibilidade nos últimos 15 min" : $"Disponibilidade de {name} — {100.0 * tOk / (tOk + tLost):F1}% nos últimos 15 min  (verde normal • amarelo perdas • vermelho sem resposta)";
    }

    // ---------- gráfico grande (estilo PingPlotter) ----------
    StatRow? chartRow; double chartX0, chartPw, chartStep; int lastChartN = -1; StatRow? lastChartRow;

    void DrawChart()
    {
        var row = selPath.LastOrDefault() ?? selRow; if (row == null) return;
        var c = ChartLive; double w = c.ActualWidth, h = c.ActualHeight; if (w < 90 || h < 60) return;
        c.Children.Clear(); chartRow = row;
        const int N = 120; double x0 = 46, pw = w - x0 - 8, ph = h - 18, step = pw / N; chartX0 = x0; chartPw = pw; chartStep = step;
        var hist = row.History; int n = hist.Count, from = Math.Max(0, n - N);
        double max = Math.Max(50, hist.Skip(from).Where(v => v.HasValue).Select(v => (double)v!.Value).DefaultIfEmpty(0).Max() * 1.15);
        foreach (var f in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            double y = ph - f * ph;
            c.Children.Add(new Line { X1 = x0, X2 = w - 8, Y1 = y, Y2 = y, Stroke = (Brush)FindResource("Line"), StrokeThickness = 1, Opacity = .6, StrokeDashArray = { 3, 3 } });
            var lb = new TextBlock { Text = $"{max * f:F0} ms", Foreground = Pal.Gray, FontSize = 10.5 }; Canvas.SetLeft(lb, 2); Canvas.SetTop(lb, Compat.Clamp(y - 8, 0, ph - 12)); c.Children.Add(lb);
        }
        // limite de atenção (150 ms, mesmo critério do mapa de rotas)
        if (max > 150)
        {
            double ly = ph - 150 / max * ph;
            c.Children.Add(new Line { X1 = x0, X2 = w - 8, Y1 = ly, Y2 = ly, Stroke = Pal.Warn, StrokeThickness = 1, Opacity = .5, StrokeDashArray = { 6, 4 } });
            var wl = new TextBlock { Text = "limite 150 ms", Foreground = Pal.Warn, FontSize = 10, Opacity = .8 }; Canvas.SetLeft(wl, x0 + 4); Canvas.SetTop(wl, ly - 14); c.Children.Add(wl);
        }
        // área de dados: desliza suavemente a cada nova amostra
        var plot = new Canvas { Width = w, Height = ph, IsHitTestVisible = false }; var tt = new TranslateTransform(); plot.RenderTransform = tt; c.Children.Add(plot);
        double X(int k) => x0 + pw - (n - k) * step + step / 2; double Y(long v) => ph - Math.Min(v, max) / max * ph;
        var accent = new SolidColorBrush(Color.FromRgb(0x7C, 0xB4, 0xFF)); var fill = new LinearGradientBrush(Color.FromArgb(90, 0x7C, 0xB4, 0xFF), Color.FromArgb(0, 0x7C, 0xB4, 0xFF), 90);
        int runStart = -1;
        void Flush(int end)
        {
            if (runStart < 0) return;
            var line = new PathFigure { StartPoint = new Point(X(runStart), Y(hist[runStart]!.Value)) }; var area = new PathFigure { StartPoint = new Point(X(runStart), ph) };
            area.Segments.Add(new LineSegment(new Point(X(runStart), Y(hist[runStart]!.Value)), true));
            for (int k = runStart + 1; k <= end; k++) { var pt = new Point(X(k), Y(hist[k]!.Value)); line.Segments.Add(new LineSegment(pt, true)); area.Segments.Add(new LineSegment(pt, true)); }
            area.Segments.Add(new LineSegment(new Point(X(end), ph), true)); area.IsClosed = true;
            var ag = new PathGeometry(); ag.Figures.Add(area); plot.Children.Add(new System.Windows.Shapes.Path { Data = ag, Fill = fill });
            // linha colorida por faixa de latência (verde ≤80 ms, amarelo ≤150 ms, vermelho acima)
            if (runStart == end) { var d1 = new Ellipse { Width = 5, Height = 5, Fill = Lat.For(hist[end]!.Value) }; Canvas.SetLeft(d1, X(end) - 2.5); Canvas.SetTop(d1, Y(hist[end]!.Value) - 2.5); plot.Children.Add(d1); }
            for (int k = runStart + 1; k <= end; k++)
                plot.Children.Add(new Line { X1 = X(k - 1), Y1 = Y(hist[k - 1]!.Value), X2 = X(k), Y2 = Y(hist[k]!.Value), Stroke = Lat.For(Math.Max(hist[k - 1]!.Value, hist[k]!.Value)), StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            runStart = -1;
        }
        for (int k = from; k < n; k++)
        {
            if (hist[k].HasValue) { if (runStart < 0) runStart = k; }
            else
            {
                Flush(k - 1);
                var r = new Rectangle { Width = Math.Max(3, step * .8), Height = ph, Fill = Lat.LostSoft }; Canvas.SetLeft(r, X(k) - r.Width / 2); plot.Children.Add(r);
                var xm = new TextBlock { Text = "✕", Foreground = Pal.Err, FontSize = 11, FontWeight = FontWeights.Bold }; Canvas.SetLeft(xm, X(k) - 4); Canvas.SetTop(xm, 1); plot.Children.Add(xm);
            }
        }
        Flush(n - 1);
        if (n > 0 && hist[n - 1] is long lastV)
        {
            double lx = X(n - 1), ly = Y(lastV); var col = Lat.For(lastV);
            var dot = new Ellipse { Width = 9, Height = 9, Fill = col, Stroke = Brushes.White, StrokeThickness = 1.5 }; Canvas.SetLeft(dot, lx - 4.5); Canvas.SetTop(dot, ly - 4.5);
            var ring = new Ellipse { Width = 9, Height = 9, Stroke = col, StrokeThickness = 2, RenderTransformOrigin = new Point(.5, .5), RenderTransform = new ScaleTransform(1, 1) }; Canvas.SetLeft(ring, lx - 4.5); Canvas.SetTop(ring, ly - 4.5);
            var dur = TimeSpan.FromSeconds(1.2); var rep = System.Windows.Media.Animation.RepeatBehavior.Forever;
            ring.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 3, dur) { RepeatBehavior = rep });
            ring.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 3, dur) { RepeatBehavior = rep });
            ring.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(.9, 0, dur) { RepeatBehavior = rep });
            plot.Children.Add(ring); plot.Children.Add(dot);
        }
        if (ReferenceEquals(lastChartRow, row) && n > lastChartN && lastChartN >= 0)
            tt.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(step * (n - lastChartN), 0, TimeSpan.FromMilliseconds(900)) { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
        lastChartRow = row; lastChartN = n;
        if (row.Received > 0)
        {
            double y = ph - Math.Min(row.Avg, max) / max * ph;
            c.Children.Add(new Line { X1 = x0, X2 = w - 8, Y1 = y, Y2 = y, Stroke = Brushes.White, Opacity = .55, StrokeThickness = 1, StrokeDashArray = { 5, 3 } });
            var al = new TextBlock { Text = $"média {row.Avg:F0} ms", Foreground = Brushes.White, FontSize = 10.5, Opacity = .85 }; Canvas.SetRight(al, 10); Canvas.SetTop(al, Compat.Clamp(y - 15, 0, ph - 14)); c.Children.Add(al);
        }
        var old = new TextBlock { Text = "← mais antigo", Foreground = Pal.Gray, FontSize = 10.5 }; Canvas.SetLeft(old, x0); Canvas.SetTop(old, h - 15); c.Children.Add(old);
        var now = new TextBlock { Text = "agora →", Foreground = Pal.Gray, FontSize = 10.5 }; Canvas.SetRight(now, 8); Canvas.SetTop(now, h - 15); c.Children.Add(now);
        int idx = selPath.IndexOf(row); string title = gwRowLive != null && selName == gwRowLive.Title ? "Roteador (gateway)" : idx >= 0 && idx == selPath.Count - 1 ? selName : $"Salto {row.HopLabel}";
        // horários das perdas no destino e perda real em saltos intermediários (a mesma que o mapa de rotas mostra)
        var lostAt = Enumerable.Range(0, Math.Min(n, row.Times.Count)).Where(k => hist[k] == null).Select(k => row.Times[k]).ToList();
        string lossTxt = lostAt.Count == 0 ? "" : $"\nPacotes perdidos: {lostAt.Count} — últimos às {string.Join(", ", lostAt.Skip(Math.Max(0, lostAt.Count - 5)).Select(t => t.ToString("HH:mm:ss")))}";
        var hopLoss = selPath.Take(Math.Max(0, selPath.Count - 1)).Select((r, i) => (r, i)).Where(x => x.r.Sent >= 5 && x.r.Loss >= 5 && selPath.Skip(x.i + 1).All(y => y.Loss >= 5)).Select(x => $"salto {x.r.HopLabel} ({x.r.Address}) {x.r.Loss:F0}%").FirstOrDefault();
        if (hopLoss != null) lossTxt += $"\nPerda começa no {hopLoss} e segue até o destino.";
        else
        {
            var only = selPath.Take(Math.Max(0, selPath.Count - 1)).Where(r => r.Sent >= 5 && r.Loss >= 5).Select(r => $"{r.HopLabel} ({r.Loss:F0}%)").ToList();
            if (only.Count > 0) lossTxt += $"\nSaltos {string.Join(", ", only)} mostram perda no mapa só porque o equipamento limita respostas ao teste; o destino {(row.Loss < 1 ? "não perdeu pacotes" : $"perdeu {row.LossTxt}")}, então não é perda real no caminho.";
        }
        TxtChartInfo.Text = $"{title} ({row.Address})  •  último {row.LastTxt}  •  média {row.AvgTxt}  •  perda {row.LossTxt}  •  oscilação {row.JitTxt}{lossTxt}";
    }

    void ChartLive_MouseMove(object s, MouseEventArgs e)
    {
        if (chartRow == null || chartStep <= 0) return;
        double x = e.GetPosition(ChartLive).X; var hist = chartRow.History; int n = hist.Count; if (n == 0) return;
        int k = n - 1 - (int)Math.Floor((chartX0 + chartPw - x) / chartStep);
        if (k < 0 || k >= n) { TxtChartHover.Text = ""; return; }
        string when = k < chartRow.Times.Count ? chartRow.Times[k].ToString("HH:mm:ss") : "";
        TxtChartHover.Text = (hist[k] is long v ? $"{v} ms" : "pacote perdido") + $"  às {when}" + (n - 1 - k == 0 ? "  (agora)" : "");
    }
    void ChartLive_MouseLeave(object s, MouseEventArgs e) => TxtChartHover.Text = "";
    void ChartLive_SizeChanged(object s, SizeChangedEventArgs e) { lastChartN = -1; DrawChart(); DrawAvail(); }
}
