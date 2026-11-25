using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
namespace SuiteRedes;

/// <summary>
/// Rotas ao vivo: um trilho por destino. Visual minimalista — caminho normal em cinza; só o que é problema ganha cor,
/// com marcação do salto onde o problema começa. Destinos com problema sobem para o topo.
/// </summary>
public partial class MainWindow
{
    const double RowH = 62, LabelW = 250, StatW = 140;

    void RoutesHost_SizeChanged(object s, SizeChangedEventArgs e) => DrawRoutes();

    List<string> RouteNames() => lastEvidence?.Dests.Where(d => d.Resolved && d.Ip != "").Select(d => d.Name).Take(MaxDests).ToList() ?? new();

    /// <summary>Pior estado do caminho e índice do primeiro salto onde o problema começa (-1 se nenhum).</summary>
    (Sev worst, bool any, int first) PathState(List<StatRow> path)
    {
        Sev worst = Sev.Ok; bool any = false; int first = -1;
        for (int i = 0; i < path.Count; i++)
        {
            var (sv, sil, _) = Assess(path, i);
            if (!sil) any = true;
            if (!sil && sv >= Sev.Warn && first < 0) first = i;
            if (sv > worst) worst = sv;
        }
        return (worst, any, first);
    }

    void DrawRoutes()
    {
        var cv = RoutesCanvas; cv.Children.Clear();
        var names = RouteNames(); if (names.Count == 0) { RoutesSection.Visibility = Visibility.Collapsed; return; }
        if (RoutesSection.Visibility != Visibility.Visible) { RoutesSection.Visibility = Visibility.Visible; Dispatcher.BeginInvoke(DrawRoutes, System.Windows.Threading.DispatcherPriority.Loaded); return; }
        double W = RoutesHost.ActualWidth; if (W < 480) return;
        var T1 = (Brush)FindResource("T1"); var T3 = (Brush)FindResource("T3"); var LineB = (Brush)FindResource("Line"); var Card = (Brush)FindResource("BgCard");

        var items = names.Select((n, k) => { var p = PathFor(n)?.Rows ?? new List<StatRow>(); return (name: n, k, path: p, st: PathState(p)); })
            .OrderByDescending(x => x.st.any ? (int)x.st.worst : -1).ThenBy(x => x.k).ToList();
        int bad = items.Count(x => x.st.any && x.st.worst >= Sev.Warn);
        bool anyErr = items.Any(x => x.st.any && x.st.worst == Sev.Error);
        TxtRoutesSummary.Text = bad == 0 ? $"{items.Count} destinos • nenhum problema detectado" : $"{bad} de {items.Count} destinos com problema (listados primeiro)";
        TxtRoutesSummary.Foreground = bad == 0 ? Pal.Ok : anyErr ? Pal.Err : Pal.Warn;

        cv.Width = W; cv.Height = items.Count * RowH;
        for (int ri = 0; ri < items.Count; ri++)
        {
            var (name, _, path, (worst, any, first)) = items[ri];
            double y0 = ri * RowH, cy = y0 + RowH / 2 - 2;
            bool selected = CmbLive.SelectedItem as string == name;
            var stBrush = !any ? Pal.Gray : SevBrush(worst, false);

            // faixa clicável (seleciona o destino no gráfico de latência)
            var band = new Rectangle { Width = W, Height = RowH - 4, RadiusX = 6, RadiusY = 6, Fill = selected ? (Brush)FindResource("BgCard2") : Brushes.Transparent, Cursor = Cursors.Hand, Tag = name };
            band.MouseLeftButtonDown += (s, e) => { if (((FrameworkElement)s).Tag is string n && CmbLive.Items.Contains(n)) CmbLive.SelectedItem = n; };
            Canvas.SetTop(band, y0); cv.Children.Add(band);
            if (any && worst >= Sev.Warn)
            {
                var bar = new Rectangle { Width = 3, Height = RowH - 18, RadiusX = 1.5, RadiusY = 1.5, Fill = stBrush, IsHitTestVisible = false };
                Canvas.SetLeft(bar, 2); Canvas.SetTop(bar, y0 + 7); cv.Children.Add(bar);
            }

            // rótulo: nome amigável + host
            var label = DestLabel(name);
            var st = new StackPanel { Width = LabelW - 24, IsHitTestVisible = false };
            st.Children.Add(new TextBlock { Text = label != "" ? label : name, FontWeight = FontWeights.SemiBold, FontSize = 13.5, Foreground = T1, TextTrimming = TextTrimming.CharacterEllipsis });
            st.Children.Add(new TextBlock { Text = label != "" ? $"{name}  •  {path.Count} saltos" : $"{path.Count} saltos", FontSize = 11, Foreground = T3, TextTrimming = TextTrimming.CharacterEllipsis });
            Canvas.SetLeft(st, 14); Canvas.SetTop(st, y0 + 11); cv.Children.Add(st);

            // resultado no destino: latência + perda
            var dest = path.LastOrDefault(); bool collecting = dest == null || dest.Sent < 3;
            bool lossy = dest != null && dest.Sent >= 5 && dest.Loss >= 1;
            var rs = new StackPanel { Width = StatW - 12, IsHitTestVisible = false };
            rs.Children.Add(new TextBlock { Text = collecting ? "…" : dest!.Received > 0 ? $"{dest.Avg:F0} ms" : "sem resposta", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = stBrush, TextAlignment = TextAlignment.Right });
            rs.Children.Add(new TextBlock { Text = collecting ? "coletando" : lossy ? $"perda {dest!.Loss:F0}%" : dest!.Received > 0 ? $"sem perda • osc. {dest.Jitter:F0} ms" : "", FontSize = 11, Foreground = lossy ? Pal.Err : T3, TextAlignment = TextAlignment.Right });
            Canvas.SetLeft(rs, W - StatW); Canvas.SetTop(rs, y0 + 11); cv.Children.Add(rs);
            if (path.Count == 0) continue;

            // trilho
            double x0 = LabelW + 6, x1 = W - StatW - 22; int n = path.Count; double step = (x1 - x0) / n;
            double X(int i) => x0 + (i + 1) * step;
            cv.Children.Add(new Line { X1 = x0, X2 = X(n - 1), Y1 = cy, Y2 = cy, Stroke = LineB, StrokeThickness = 2, IsHitTestVisible = false });
            if (first >= 0)
                cv.Children.Add(new Line { X1 = first == 0 ? x0 : X(first - 1), X2 = X(n - 1), Y1 = cy, Y2 = cy, Stroke = stBrush, StrokeThickness = 3, Opacity = .6, IsHitTestVisible = false });
            var pc = new Ellipse { Width = 8, Height = 8, Fill = Pal.Info, ToolTip = "Este computador (origem)" };
            Canvas.SetLeft(pc, x0 - 4); Canvas.SetTop(pc, cy - 4); cv.Children.Add(pc);

            // operadora por trecho (saltos consecutivos da mesma organização)
            string Org(StatRow r) => NetInfo.IsPrivate(r.Address) ? "rede local" : (r.Org ?? "").Trim();
            for (int i = 0, gs = 0; i <= n; i++)
            {
                if (i < n && (i == gs || Org(path[i]) == "" || Org(path[i]) == Org(path[gs]))) continue;
                var o = Org(path[gs]); double gx0 = X(gs) - step / 2 + 2, gw = (i - gs) * step - 4;
                if (o != "" && gw >= 44)
                {
                    var ot = new TextBlock { Text = o, FontSize = 10, Foreground = T3, Width = gw, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, IsHitTestVisible = false, ToolTip = o };
                    Canvas.SetLeft(ot, gx0); Canvas.SetTop(ot, cy + 9); cv.Children.Add(ot);
                    if (gs > 0) cv.Children.Add(new Line { X1 = gx0 - 2, X2 = gx0 - 2, Y1 = cy + 9, Y2 = cy + 21, Stroke = LineB, StrokeThickness = 1, IsHitTestVisible = false });
                }
                gs = i;
            }

            // saltos: normais pequenos e cinza; com problema maiores e coloridos
            for (int i = 0; i < n; i++)
            {
                var r = path[i]; var (sv, sil, iss) = Assess(path, i); bool last = i == n - 1; bool prob = !sil && sv >= Sev.Warn;
                double d = prob ? 14 : last ? 12 : sil ? 7 : 8;
                Shape mark = last ? new Rectangle { RadiusX = 3, RadiusY = 3 } : new Ellipse();
                mark.Width = mark.Height = d; mark.HorizontalAlignment = HorizontalAlignment.Center; mark.VerticalAlignment = VerticalAlignment.Center;
                if (sil && !last) { mark.Stroke = Pal.Gray; mark.StrokeThickness = 1.2; mark.Fill = Card; }
                else mark.Fill = prob || last ? SevBrush(sv, sil) : T3;
                var hit = new Grid { Width = Math.Max(18, d + 6), Height = 26, Background = Brushes.Transparent, ToolTip = HopTip(r, i, last, name, iss), Cursor = Cursors.Help };
                ToolTipService.SetShowDuration(hit, 60000); ToolTipService.SetInitialShowDelay(hit, 120);
                hit.Children.Add(mark);
                Canvas.SetLeft(hit, X(i) - hit.Width / 2); Canvas.SetTop(hit, cy - 13); cv.Children.Add(hit);

                if (i == first)
                {
                    var prev = path.Take(i).LastOrDefault(x => x.Received > 0);
                    string why = r.Received == 0 ? "sem resposta"
                        : r.Sent >= 5 && r.Loss >= 5 ? $"perda {r.Loss:F0}%"
                        : prev != null && r.Avg - prev.Avg > 80 ? $"+{r.Avg - prev.Avg:F0} ms"
                        : r.Jitter > 30 ? $"oscilação {r.Jitter:F0} ms" : $"{r.Avg:F0} ms";
                    var at = new TextBlock { Text = $"▼ {(last ? "no destino" : "começa no salto " + r.Index)}: {why}", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = SevBrush(sv, false), IsHitTestVisible = false };
                    at.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Canvas.SetLeft(at, Compat.Clamp(X(i) - at.DesiredSize.Width / 2, x0 - 4, Math.Max(x0 - 4, x1 + 16 - at.DesiredSize.Width)));
                    Canvas.SetTop(at, cy - 26); cv.Children.Add(at);
                }
            }
            if (ri < items.Count - 1) cv.Children.Add(new Line { X1 = 12, X2 = W - 12, Y1 = y0 + RowH - 2, Y2 = y0 + RowH - 2, Stroke = LineB, StrokeThickness = 1, Opacity = .5, IsHitTestVisible = false });
        }
    }

    static object HopTip(StatRow r, int i, bool last, string dest, List<IssueVm> iss)
    {
        var sp = new StackPanel { MaxWidth = 360 };
        sp.Children.Add(new TextBlock { Text = last ? $"Destino: {dest}" : $"Salto {r.Index}", FontWeight = FontWeights.Bold, FontSize = 13 });
        void L(string k, string v) { if (!string.IsNullOrWhiteSpace(v) && v != "—") sp.Children.Add(new TextBlock { Text = $"{k}: {v}", TextWrapping = TextWrapping.Wrap }); }
        L("IP", r.Address is "*" or "?" ? "sem resposta" : r.Address); L("Nome", r.Host); L("Operadora", (r.Asn + " " + r.Org).Trim());
        if (r.Received > 0) { L("Latência", $"média {r.Avg:F0} ms  (mín. {r.Min:F0} / máx. {r.Max:F0})"); L("Oscilação", r.JitTxt); }
        L("Perda", r.Sent > 0 ? $"{r.LossTxt} de {r.Sent} testes" : "");
        foreach (var x in iss.Take(3)) sp.Children.Add(new TextBlock { Text = $"{x.Icon} {x.Text}", TextWrapping = TextWrapping.Wrap, Foreground = x.Fg, Margin = new Thickness(0, 4, 0, 0) });
        return sp;
    }
}
