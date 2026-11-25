using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
namespace SuiteRedes;

/// <summary>
/// Três visuais:
/// 1) Topologia ao vivo (esquerda→direita, faixas por rede, pacotes animados cuja velocidade reflete a latência acrescentada no trecho,
///    pacotes "caindo" onde há perda real, anel de % de respostas em cada ponto).
/// 2) Onde o tempo é gasto: latência do destino dividida entre as redes (base = mediana de cada ponto).
/// 3) Radar dos últimos 2 min (varredura) + anel de disponibilidade do ponto selecionado.
/// </summary>
public partial class MainWindow
{
    // ---------- zonas (redes) ----------
    string GroupName(StatRow h)
    {
        if (h.Address is "*" or "?") return "";
        if (h.Host.EndsWith(".ix.br", StringComparison.OrdinalIgnoreCase)) return "IX.br " + (h.Host.Split('.').Reverse().Skip(2).FirstOrDefault() ?? "").ToUpperInvariant();
        var o = OrgOf(h); if (o == "") return "";
        if (SameProvider(h)) return o + " (seu provedor)";
        return o;
    }
    /// <summary>Chave da rede de um salto: o número do AS quando conhecido (identificador real), senão o tipo/nome.</summary>
    string ZoneKey(StatRow h)
    {
        if (h.Address is "*" or "?") return "";
        if (NetInfo.IsPrivate(h.Address)) return h.Index == 1 ? "home" : "priv";
        if (NetInfo.IsCgnat(h.Address)) return "cgnat";
        if (h.Host.EndsWith(".ix.br", StringComparison.OrdinalIgnoreCase)) return "ix:" + GroupName(h);
        var d = AsDigits(h.Asn); if (d != "") return "as:" + d;
        var g = GroupName(h); return g == "" ? "unk" : "org:" + g;
    }
    List<(string name, int a, int b)> Zones(RouteVm r)
    {
        // agrupa saltos consecutivos da mesma rede; saltos mudos ficam na rede anterior (não acrescentam medição)
        var z = new List<(string, int, int)>(); string cur = ""; int start = 0; int n = r.Hops.Count;
        string NameOf(int a2, int b2)
        {
            for (int k = a2; k <= b2; k++) { var g = GroupName(r.Hops[k]); if (g != "" && !g.StartsWith("AS")) return g; }
            for (int k = a2; k <= b2; k++) { var g = GroupName(r.Hops[k]); if (g != "") return g; }
            return Enumerable.Range(a2, b2 - a2 + 1).Any(k => r.Hops[k].Address is not ("*" or "?")) ? "Rede não identificada" : "Sem resposta";
        }
        for (int i = 0; i < n; i++)
        {
            var key = ZoneKey(r.Hops[i]); if (key == "") key = cur == "" ? "silent" : cur;
            if (i == 0) { cur = key; continue; }
            if (key != cur) { z.Add((NameOf(start, i - 1), start, i - 1)); cur = key; start = i; }
        }
        if (n > 0) z.Add((NameOf(start, n - 1), start, n - 1));
        // redes vizinhas com o mesmo nome (mesma organização com mais de um AS) viram uma só
        for (int q = z.Count - 1; q > 0; q--)
            if (z[q].Item1 == z[q - 1].Item1) { z[q - 1] = (z[q - 1].Item1, z[q - 1].Item2, z[q].Item3); z.RemoveAt(q); }
        return z;
    }
    static double Mid(StatRow h) => h.Received > 0 ? Median(h) : -1;

    // ---------- 1) topologia ----------
    void BuildPath(RouteVm r)
    {
        BuildLegend(r);
        var c = TopoCanvas; ClearAnimated(c); int n = r.Hops.Count;
        if (n == 0) { c.Width = 300; c.Children.Add(new TextBlock { Text = r.Phase + "…", Foreground = Res("T3"), Margin = new Thickness(20) }); return; }
        double W = Math.Max(400, TopoScroll.ActualWidth), H = c.Height = Math.Max(200, TopoScroll.ActualHeight - (TopoScroll.ComputedHorizontalScrollBarVisibility == Visibility.Visible ? 14 : 0)), x0 = 64, cy = Math.Round(Math.Max(84, H < 280 ? H * 0.40 : H * 0.42));   // abaixo dos rótulos das faixas (até 2 linhas)
        // a última faixa vai até meio espaçamento além do último ponto: reservar esse meio espaço + margem
        double sx = Math.Max(118, (W - x0 - 24) / (n + 0.5)); c.Width = Math.Max(W, x0 + (n + 0.5) * sx + 16);
        double X(int i) => x0 + (i + 1) * sx;
        var acc = ((SolidColorBrush)Res("Accent")).Color;

        // faixas por rede
        var zones = Zones(r); int zi = 0; double labBottom = 0; var bands = new List<Rectangle>();
        var zoneOf = new int[n]; for (int q = 0; q < zones.Count; q++) for (int k = zones[q].a; k <= zones[q].b; k++) zoneOf[k] = q;
        foreach (var (name, a, b) in zones)
        {
            double zx = X(a) - sx / 2 + 2, zw = (b - a + 1) * sx - 4;
            if (a == 0) { double left = x0 - 56; zw += zx - left; zx = left; }
            int fk0 = Enumerable.Range(a, b - a + 1).Where(k => r.Hops[k].Address is not ("*" or "?")).DefaultIfEmpty(a).First();
            var zc = ZoneColor(zi);
            var band = new Rectangle { Width = zw, Height = H - 16, RadiusX = 8, RadiusY = 8, Fill = new SolidColorBrush(Color.FromArgb(20, zc.R, zc.G, zc.B)), Stroke = new SolidColorBrush(Color.FromArgb(110, zc.R, zc.G, zc.B)), StrokeThickness = 1 };
            Canvas.SetLeft(band, zx); Canvas.SetTop(band, 8); c.Children.Add(band); bands.Add(band);
            int fk = Enumerable.Range(a, b - a + 1).Where(k => r.Hops[k].Address is not ("*" or "?")).DefaultIfEmpty(a).First();
            var zk = KindOf(r.Hops[fk], fk, fk == n - 1);
            var lb = new DockPanel { Width = zw - 16, ToolTip = name, LastChildFill = true };
            lb.Children.Add(NodeIcon(zk, 13, new SolidColorBrush(zc)));
            lb.Children.Add(new TextBlock { Text = name, Foreground = new SolidColorBrush(zc), FontWeight = FontWeights.SemiBold, FontSize = 12.5, Margin = new Thickness(6, 0, 0, 0), TextWrapping = TextWrapping.Wrap });
            Canvas.SetLeft(lb, zx + 8); Canvas.SetTop(lb, 16); c.Children.Add(lb);
            lb.Measure(new Size(Math.Max(10, zw - 16), double.PositiveInfinity)); labBottom = Math.Max(labBottom, 16 + lb.DesiredSize.Height);
            zi++;
        }
        // pontos sempre abaixo do maior nome de rede (o nome nunca é cortado: ocupa as linhas que precisar)
        cy = Math.Max(cy, Math.Round(labBottom + 40));
        double contentBottom = 0;
        // origem
        var you = new Ellipse { Width = 34, Height = 34, Fill = Res("T1") }; Canvas.SetLeft(you, x0 - 17); Canvas.SetTop(you, cy - 17); c.Children.Add(you);
        var yi = NodeIcon(NodeKind.You, 16, Res("BgApp")); yi.Width = 34; yi.TextAlignment = TextAlignment.Center; Canvas.SetLeft(yi, x0 - 17); Canvas.SetTop(yi, cy - 8); c.Children.Add(yi);
        var who = new StackPanel { Width = 108 };
        TextBlock Line2(string a, string b) { var t = new TextBlock { TextAlignment = TextAlignment.Center, FontSize = 11.5 }; t.Inlines.Add(new System.Windows.Documents.Run(a + " ") { Foreground = Res("T3"), FontSize = 11 }); t.Inlines.Add(new System.Windows.Documents.Run(b) { Foreground = Res("T1") }); return t; }
        who.Children.Add(new TextBlock { Text = "você", Foreground = Res("T3"), FontSize = 11.5, TextAlignment = TextAlignment.Center });
        if (LocalIp() is string lip) who.Children.Add(Line2("privado", lip));
        if (pubInfo?.Ok == true) who.Children.Add(Line2("público", pubInfo.Ip));
        Canvas.SetLeft(who, x0 - 54); Canvas.SetTop(who, cy + 25); c.Children.Add(who);

        // ligações + pacotes
        int lastResp = r.Hops.FindLastIndex(h => h.Received > 0);
        double prevMs = 0;
        for (int i = 0; i < n; i++)
        {
            var h = r.Hops[i]; double xa = i == 0 ? x0 : X(i - 1), xb = X(i);
            bool inProb = r.First >= 0 && i >= r.First;
            var stroke = inProb ? SevBrush(r.Worst) : Res("Line");
            c.Children.Add(new Line { X1 = xa, X2 = xb, Y1 = cy, Y2 = cy, Stroke = stroke, StrokeThickness = inProb ? 3 : 2, Opacity = inProb ? .7 : 1 });
            double ms = Mid(h); double delta = ms >= 0 ? Math.Max(0, ms - prevMs) : 0; if (ms >= 0) prevMs = ms;
            if (Running && i <= lastResp)
            {
                // duração arredondada para manter a animação contínua entre atualizações
                double dur = Math.Round(Compat.Clamp(0.6 + delta / 25.0, 0.6, 4.0) * 4) / 4;
                var pk = new Ellipse { Width = 6, Height = 6, Fill = inProb ? SevBrush(r.Worst) : Res("T1"), IsHitTestVisible = false };
                Canvas.SetTop(pk, cy - 3); c.Children.Add(pk);
                pk.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(xa - 3, xb - 3, TimeSpan.FromSeconds(dur)) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = PhaseOf(dur) });
            }
        }
        // pontos: ícone pelo tipo de rede, IP e latência sempre visíveis
        for (int i = 0; i < n; i++)
        {
            var h = r.Hops[i]; bool last = i == n - 1; var (sv, sil, why) = Assess(r.Hops, i); bool prob = !sil && sv >= Sev.Warn;
            double x = X(i), rad = last ? 17 : sil ? 6 : 15;
            var col = prob ? SevBrush(sv) : last && !sil ? Pal.Ok : sil ? Pal.Gray : Res("T2");
            if (!sil && h.Sent > 0)
            {
                double frac = h.Received / (double)h.Sent; bool real = prob || last;
                c.Children.Add(Arc(x, cy, rad + 5, 0, 1, Res("BgCard2"), 3));
                c.Children.Add(Arc(x, cy, rad + 5, 0, frac, real ? col : Res("T3"), 3));
                if (frac < 0.999) c.Children.Add(Arc(x, cy, rad + 5, frac, 1, real ? Pal.Err : Pal.Gray, 3));
            }
            var kind = KindOf(h, i, last); var zcol = ZoneColor(zoneOf[i]); Brush kb = new SolidColorBrush(zcol); Color? kt = zcol;
            Shape node = last ? new Rectangle { RadiusX = 7, RadiusY = 7 } : new Ellipse();
            node.Width = node.Height = rad * 2;
            node.Fill = sil ? Res("BgCard") : prob || last ? BgFor(prob ? sv : Sev.Ok) : kt is Color tc ? new SolidColorBrush(Color.FromArgb(46, tc.R, tc.G, tc.B)) : Res("BgCard2");
            node.Stroke = sil ? Pal.Gray : prob || last ? col : kb; node.StrokeThickness = sil ? 1.5 : 2;
            if (kind == NodeKind.Cgnat && !sil) node.StrokeDashArray = new DoubleCollection { 2, 1.5 };
            Canvas.SetLeft(node, x - rad); Canvas.SetTop(node, cy - rad); c.Children.Add(node);
            if (!sil)
            {
                var ic = NodeIcon(kind, 15, prob || last ? col : kb);
                ic.Width = rad * 2; ic.TextAlignment = TextAlignment.Center; Canvas.SetLeft(ic, x - rad); Canvas.SetTop(ic, cy - 8); c.Children.Add(ic);
            }
            if (Running && prob && h.Sent >= 5 && h.Loss >= 3)
            {
                double period = Compat.Clamp(3.0 - h.Loss / 20.0, 0.8, 3.0);
                var drop = new Ellipse { Width = 6, Height = 6, Fill = Pal.Err, IsHitTestVisible = false }; Canvas.SetLeft(drop, x - 3); c.Children.Add(drop);
                drop.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(cy + rad, cy + 80, TimeSpan.FromSeconds(period)) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = PhaseOf(period), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
                drop.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(period)) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = PhaseOf(period) });
            }
            // rótulos: salto, latência, IP, perda
            var lab = new StackPanel { Width = sx - 6 };
            lab.Children.Add(new TextBlock { Text = last ? "destino" : $"salto {h.Index}", Foreground = Res("T3"), FontSize = 11.5, TextAlignment = TextAlignment.Center });
            if (!sil)
            {
                var ms = new TextBlock { FontFamily = (FontFamily)FindResource("Num"), Foreground = prob ? col : Res("T1"), TextAlignment = TextAlignment.Center };
                ms.Inlines.Add(new System.Windows.Documents.Run(h.Received > 0 ? $"{Mid(h):F0}" : "—") { FontSize = 16 });
                if (h.Received > 0) ms.Inlines.Add(new System.Windows.Documents.Run(" ms") { FontSize = 11.5, Foreground = Res("T3") });
                lab.Children.Add(ms);
                lab.Children.Add(new TextBlock { Text = h.Address is "*" or "?" ? "sem resposta" : h.Address, Foreground = Res("T2"), FontSize = 11.5, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
                string dns = h.Host != "" ? h.Host : h.HostChecked ? "sem nome DNS" : "resolvendo nome…";
                lab.Children.Add(new TextBlock { Text = h.Host != "" ? h.Host.Replace(".", ".\u200B") : dns, Foreground = Res("T3"), FontSize = 11, FontStyle = h.Host == "" ? FontStyles.Italic : FontStyles.Normal, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, ToolTip = h.Host != "" ? h.Host : null });
                if (h.Sent >= 5 && h.Loss >= 1) lab.Children.Add(new TextBlock { Text = $"perda {h.Loss:F0}%", Foreground = prob || last ? Pal.Err : Res("T3"), FontSize = 11.5, TextAlignment = TextAlignment.Center });
            }
            else if (h.Sent >= 3)
            {
                lab.Children.Add(new TextBlock { Text = "não responde", Foreground = Res("T3"), FontSize = 11.5, TextAlignment = TextAlignment.Center });
                lab.Children.Add(new TextBlock { Text = "IP e nome indisponíveis", Foreground = Res("T3"), FontSize = 11, FontStyle = FontStyles.Italic, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, ToolTip = "Este roteador descarta os pacotes de teste sem responder. Sem resposta não há IP de origem e, portanto, nem nome DNS. Os pontos seguintes respondem, então o tráfego passa normalmente por ele." });
            }
            Canvas.SetLeft(lab, x - (sx - 6) / 2); Canvas.SetTop(lab, cy + rad + 8); c.Children.Add(lab);
            lab.Measure(new Size(sx - 6, double.PositiveInfinity)); contentBottom = Math.Max(contentBottom, cy + rad + 8 + lab.DesiredSize.Height);
            if (prob && i == r.First)
            {
                var at = new Border { Background = BgFor(sv), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Child = new TextBlock { Text = "▼ " + why, Foreground = Res("T1"), FontSize = 12.5 } };
                at.Measure(new Size(400, 40)); Canvas.SetLeft(at, Math.Max(4, Math.Min(c.Width - at.DesiredSize.Width - 4, x - at.DesiredSize.Width / 2))); Canvas.SetTop(at, cy - rad - 34); c.Children.Add(at);
            }
        }
        // área cresce para caber tudo (rolagem vertical em vez de cortar texto)
        double need = Math.Max(H, Math.Max(contentBottom, cy + 120) + 18);
        if (need > c.Height) { c.Height = need; foreach (var bd in bands) bd.Height = need - 16; }
    }

    void TopoScroll_SizeChanged(object s, SizeChangedEventArgs e) { if (Sel != null && !healthMode) BuildPath(Sel); }

    /// <summary>Para todas as animações dos filhos e limpa o painel. Animações "Forever" em elementos descartados
    /// continuariam consumindo CPU e memória indefinidamente.</summary>
    static void ClearAnimated(Panel p)
    {
        foreach (UIElement el in p.Children) StopAnim(el);
        p.Children.Clear();
    }
    static void StopAnim(UIElement el)
    {
        el.BeginAnimation(Canvas.LeftProperty, null); el.BeginAnimation(Canvas.TopProperty, null); el.BeginAnimation(UIElement.OpacityProperty, null);
        if (el is Shape sh) sh.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        switch (el.RenderTransform)
        {
            case RotateTransform rt when !rt.IsFrozen: rt.BeginAnimation(RotateTransform.AngleProperty, null); break;
            case ScaleTransform st when !st.IsFrozen: st.BeginAnimation(ScaleTransform.ScaleXProperty, null); st.BeginAnimation(ScaleTransform.ScaleYProperty, null); break;
            case TranslateTransform tt when !tt.IsFrozen: tt.BeginAnimation(TranslateTransform.XProperty, null); break;
        }
        if (el is Panel pp) foreach (UIElement ch in pp.Children) StopAnim(ch);
    }

    static string? cachedLocalIp; static DateTime localIpAt;
    static string? LocalIp()
    {
        if ((DateTime.Now - localIpAt).TotalSeconds < 30) return cachedLocalIp;
        localIpAt = DateTime.Now;
        var (nic, _) = Diagnostics.Primary();
        cachedLocalIp = nic?.GetIPProperties().UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString();
        return cachedLocalIp;
    }

    // ---------- ícones (Segoe Fluent Icons / MDL2 Assets, nativas do Windows) ----------
    enum NodeKind { You, Home, Private, Cgnat, Provider, Ix, Transit, Dest }
    /// <summary>Cor por tipo. Só "seu provedor" (violeta) e "IX.br" (ciano) têm matiz próprio — par validado para
    /// visão normal e daltonismo; os demais tipos são neutros e se distinguem pelo ícone. Verde/amarelo/vermelho = estado.</summary>
    // Validado (vizinhos no caminho, visão normal e daltonismo): CGNAT magenta → provedor violeta → IX.br ciano.
    static readonly Color[] ZoneCycle = { Color.FromRgb(0x39, 0x87, 0xE5), Color.FromRgb(0xD9, 0x59, 0x26), Color.FromRgb(0x90, 0x85, 0xE9), Color.FromRgb(0x19, 0x9E, 0x70) };
    /// <summary>Cor da n-ésima rede do caminho. Ciclo validado (vizinhas distinguíveis em visão normal e daltonismo).</summary>
    static Color ZoneColor(int i) => ZoneCycle[((i % ZoneCycle.Length) + ZoneCycle.Length) % ZoneCycle.Length];
    static Color? KindTint(NodeKind k) => k switch
    {
        NodeKind.Cgnat => Color.FromRgb(0xD5, 0x51, 0x81), NodeKind.Provider => Color.FromRgb(0x90, 0x85, 0xE9), NodeKind.Ix => Color.FromRgb(0x5C, 0xC8, 0xE6), _ => null
    };
    Brush KindBrush(NodeKind k) => KindTint(k) is Color c ? new SolidColorBrush(c) : k switch { NodeKind.Home or NodeKind.Transit or NodeKind.You => Res("T1"), NodeKind.Dest => Pal.Ok, _ => Res("T2") };
    static string KindLabel(NodeKind k) => k switch
    {
        NodeKind.You => "você", NodeKind.Home => "seu roteador", NodeKind.Private => "rede privada", NodeKind.Cgnat => "CGNAT (IP compartilhado)",
        NodeKind.Provider => "seu provedor", NodeKind.Ix => "IX.br (troca de tráfego)", NodeKind.Transit => "outra operadora", _ => "destino"
    };
    void BuildLegend(RouteVm r)
    {
        var p = TopoLegend; p.Children.Clear();
        var kinds = new List<NodeKind> { NodeKind.You };
        for (int i = 0; i < r.Hops.Count; i++) { var h = r.Hops[i]; if (h.Address is "*" or "?") continue; var k = KindOf(h, i, i == r.Hops.Count - 1); if (!kinds.Contains(k)) kinds.Add(k); }
        void Chip(UIElement mark, string text)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 4) };
            sp.Children.Add(mark); sp.Children.Add(new TextBlock { Text = text, Foreground = Res("T2"), FontSize = 12.5, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            p.Children.Add(sp);
        }
        foreach (var k in kinds.OrderBy(k => (int)k))
        {
            var g = new Grid { Width = 20, Height = 20 };
            Shape s = k == NodeKind.Dest ? new Rectangle { RadiusX = 5, RadiusY = 5 } : new Ellipse();
            Brush kb = k == NodeKind.Dest ? Pal.Ok : Res("T1"); Color? kt = null;
            s.Stroke = k == NodeKind.You ? Res("T1") : kb; s.StrokeThickness = 1.5; s.Fill = k == NodeKind.You ? Res("T1") : kt is Color tc ? new SolidColorBrush(Color.FromArgb(46, tc.R, tc.G, tc.B)) : k == NodeKind.Dest ? Pal.OkBg : Res("BgCard2");
            if (k == NodeKind.Cgnat) s.StrokeDashArray = new DoubleCollection { 2, 1.5 };
            g.Children.Add(s);
            var ic = NodeIcon(k, 10.5, k == NodeKind.You ? Res("BgApp") : kb); ic.HorizontalAlignment = HorizontalAlignment.Center; ic.VerticalAlignment = VerticalAlignment.Center; g.Children.Add(ic);
            Chip(g, KindLabel(k));
        }
        if (r.Hops.Any(h => h.Address is "*" or "?")) { var e = new Ellipse { Width = 10, Height = 10, Stroke = Pal.Gray, StrokeThickness = 1.5, Margin = new Thickness(5, 0, 5, 0) }; Chip(e, "não responde ao teste"); }
        var ring = new Grid { Width = 20, Height = 20 }; ring.Children.Add(Arc(10, 10, 8, 0, 0.8, Res("T3"), 2.5)); ring.Children.Add(Arc(10, 10, 8, 0.8, 1, Pal.Err, 2.5)); Chip(ring, "anel = % de respostas (vermelho = perda real)");
        { var sw = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var zc in ZoneCycle.Take(3)) sw.Children.Add(new Rectangle { Width = 8, Height = 12, RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 0, 2, 0), Fill = new SolidColorBrush(zc) }); Chip(sw, "cor = rede do trecho (a mesma na barra de tempo)"); }
    }

    static string Glyph(NodeKind k) => k switch
    {
        NodeKind.You => "", NodeKind.Home => "", NodeKind.Private => "", NodeKind.Cgnat => "",
        NodeKind.Provider => "", NodeKind.Ix => "", NodeKind.Transit => "", _ => ""
    };
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    TextBlock NodeIcon(NodeKind k, double size, Brush fg) => new() { Text = Glyph(k), FontFamily = IconFont, FontSize = size, Foreground = fg, IsHitTestVisible = false };
    NodeKind KindOf(StatRow h, int i, bool last)
    {
        if (last) return NodeKind.Dest;
        if (h.Host.EndsWith(".ix.br", StringComparison.OrdinalIgnoreCase)) return NodeKind.Ix;
        if (NetInfo.IsCgnat(h.Address)) return NodeKind.Cgnat;
        if (NetInfo.IsPrivate(h.Address)) return h.Index == 1 ? NodeKind.Home : NodeKind.Private;
        if (SameProvider(h)) return NodeKind.Provider;
        return NodeKind.Transit;
    }

    /// <summary>Arco circular de f0 a f1 (fração da volta, começando no topo, sentido horário).</summary>
    static Shape Arc(double cx, double cy, double r, double f0, double f1, Brush stroke, double th)
    {
        if (f1 - f0 >= 0.9999) return new Ellipse { Width = r * 2, Height = r * 2, Stroke = stroke, StrokeThickness = th, Margin = new Thickness(cx - r, cy - r, 0, 0), IsHitTestVisible = false };
        Point P(double f) => new(cx + r * Math.Sin(f * 2 * Math.PI), cy - r * Math.Cos(f * 2 * Math.PI));
        var fig = new PathFigure { StartPoint = P(f0) };
        fig.Segments.Add(new ArcSegment(P(f1), new Size(r, r), 0, f1 - f0 > 0.5, SweepDirection.Clockwise, true));
        var g = new PathGeometry(); g.Figures.Add(fig);
        return new System.Windows.Shapes.Path { Data = g, Stroke = stroke, StrokeThickness = th, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
    }

    // ---------- destino: tempo gasto, radar e ocorrências ----------
    void ShowPoint(RouteVm r)
    {
        var d = r.Dest; if (d == null) return; var (sv, sil, _) = Assess(r.Hops, r.Hops.Count - 1);
        DrawSpend(r); DrawRadar(d, true); ShowEvents(r);
    }

    // ---------- 2) onde o tempo é gasto ----------
    void DrawSpend(RouteVm r)
    {
        var c = SpendCanvas; c.Children.Clear(); double w = c.ActualWidth; if (w < 100) return;
        var d = r.Dest; if (d == null || d.Received == 0) { c.Children.Add(new TextBlock { Text = "Aguardando respostas do destino.", Foreground = Res("T3") }); return; }
        double total = Median(d); var acc = ((SolidColorBrush)Res("Accent")).Color;
        var parts = new List<(string name, double ms, Brush fill)>(); double prev = 0; int nH = r.Hops.Count;
        int zIdx = -1;
        foreach (var (name, a, b) in Zones(r))
        {
            zIdx++;
            var lastOk = Enumerable.Range(a, b - a + 1).Reverse().Select(k => r.Hops[k]).FirstOrDefault(x => x.Received > 0);
            if (lastOk == null) continue;
            double m = Math.Min(Median(lastOk), total), add = Math.Max(0, m - prev); prev = Math.Max(prev, m);
            int fk = Enumerable.Range(a, b - a + 1).Where(k => r.Hops[k].Address is not ("*" or "?")).DefaultIfEmpty(a).First();
            var kind = KindOf(r.Hops[fk], fk, fk == nH - 1);
            Brush fill = new SolidColorBrush(ZoneColor(zIdx));
            parts.Add((name, add, fill));
        }
        double sum = Math.Max(1, parts.Sum(p => p.ms)); double x = 0, bh = 14;
        var legend = new WrapPanel { Width = w };
        for (int k = 0; k < parts.Count; k++)
        {
            var (name, ms, fill) = parts[k];
            double pw = w * ms / sum;
            if (pw >= 0.5) { var seg = new Rectangle { Width = Math.Max(2, pw - 2), Height = bh, RadiusX = 3, RadiusY = 3, Fill = fill }; Canvas.SetLeft(seg, x); c.Children.Add(seg); x += pw; }
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 4) };
            item.Children.Add(new Rectangle { Width = 9, Height = 9, RadiusX = 2, RadiusY = 2, Fill = fill, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            var t = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxWidth = Math.Max(60, w - 24) };
            t.Inlines.Add(new System.Windows.Documents.Run(name + " ") { Foreground = Res("T2") });
            t.Inlines.Add(new System.Windows.Documents.Run($"+{ms:F0} ms") { FontFamily = (FontFamily)FindResource("Num"), FontSize = 13, Foreground = Res("T1") });
            item.Children.Add(t); legend.Children.Add(item);
        }
        Canvas.SetTop(legend, bh + 10); c.Children.Add(legend);
        legend.Measure(new Size(w, double.PositiveInfinity)); c.Height = bh + 10 + legend.DesiredSize.Height + 4;   // altura acompanha o conteúdo: nada é cortado
    }

    // ---------- 3a) radar dos últimos 2 min ----------
    // Anel de tempo: a medição mais recente fica sempre no topo ("agora"); as anteriores seguem no sentido anti-horário
    // (−30 s à esquerda, −1 min embaixo, −1,5 min à direita). Comprimento do traço = quanto acima do mínimo medido.
    // O anel gira suavemente uma posição a cada nova medição. Sem ponteiro: nada cruza o número central.
    const int RadarN = 120;
    void DrawRadar(StatRow h, bool lossIsReal)
    {
        var c = RadarCanvas; ClearAnimated(c); double w = c.ActualWidth, ht = c.ActualHeight; if (w < 80 || ht < 80) return;
        // reserva o espaço dos rótulos de tempo FORA do círculo: 18 px em cima/embaixo, 30 px nas laterais
        const double PadV = 18, PadH = 30;
        double Rl = Math.Min((w - 2 * PadH) / 2, (ht - 2 * PadV) / 2);
        bool small = Rl < 34;                                     // sem espaço para rótulos: círculo ocupa tudo, sem rótulos
        double R = small ? Math.Min(w, ht) / 2 - 3 : Rl, cx = w / 2, cy = ht / 2, r0 = R * 0.30, gap = small ? 2 : 4;
        if (R < 10) return;
        var acc = ((SolidColorBrush)Res("Accent")).Color;
        var hist = h.History; int n = hist.Count, from = Math.Max(0, n - RadarN);
        var vals = hist.Skip(from).Where(v => v.HasValue).Select(v => (double)v!.Value).OrderBy(v => v).ToList();
        // normal da rota = mediana; escala = 0 até um valor redondo acima do percentil 95 (um pico isolado não achata o resto)
        double med = vals.Count > 0 ? (vals.Count % 2 == 1 ? vals[vals.Count / 2] : (vals[vals.Count / 2 - 1] + vals[vals.Count / 2]) / 2) : 0;
        double p95 = vals.Count > 0 ? vals[(int)((vals.Count - 1) * 0.95)] : 0;
        double scale = Nice(Math.Max(10, p95 * 1.15));
        double r1 = r0 + gap;
        double RadOf(double ms) => r1 + (R - r1) * Compat.Clamp(ms / scale, 0, 1);
        // anéis de referência em 1/3, 2/3 e 3/3 da escala
        for (int q = 1; q <= 3; q++)
        {
            double rr = RadOf(scale * q / 3);
            var e = new Ellipse { Width = rr * 2, Height = rr * 2, Stroke = Res("Line"), StrokeThickness = 1, Opacity = q == 3 ? .9 : .45 }; Canvas.SetLeft(e, cx - rr); Canvas.SetTop(e, cy - rr); c.Children.Add(e);
        }
        // anel do "normal" (mediana) tracejado
        if (med > 0) { double rm = RadOf(med); var en = new Ellipse { Width = rm * 2, Height = rm * 2, Stroke = Res("T3"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 3 }, Opacity = .8 }; Canvas.SetLeft(en, cx - rm); Canvas.SetTop(en, cy - rm); c.Children.Add(en); }
        // marcas de tempo
        void TimeLbl(string t, double deg, bool strong)
        {
            var l = new TextBlock { Text = t, Foreground = strong ? Res("T2") : Res("T3"), FontSize = 11, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal };
            l.Measure(new Size(100, 30)); double lw = l.DesiredSize.Width, lh = l.DesiredSize.Height;
            // posição explícita por quadrante: nunca sobrepõe o círculo nem sai da área reservada
            var (x, y) = deg switch
            {
                0 => (cx - lw / 2, cy - R - lh - 2),
                180 => (cx - lw / 2, cy + R + 2),
                -90 => (cx - R - lw - 5, cy - lh / 2),
                _ => (cx + R + 5, cy - lh / 2)
            };
            Canvas.SetLeft(l, x); Canvas.SetTop(l, y); c.Children.Add(l);
        }
        if (!small) { TimeLbl("agora", 0, true); TimeLbl("−30", -90, false); TimeLbl("−60", 180, false); TimeLbl("−90", 90, false); }
        // traços (num grupo que gira uma posição por segundo)
        double step = 360.0 / RadarN;
        var ring = new Canvas { Width = w, Height = ht, IsHitTestVisible = false };
        for (int k = from; k < n; k++)
        {
            int age = n - 1 - k; double a = -age * step * Math.PI / 180;
            double fade = 1 - age / (double)RadarN * 0.65;
            double len; Brush b;
            if (hist[k] is long v)
            {
                len = Compat.Clamp(v / scale, 0.03, 1);
                bool spike = med > 0 && v > med * 2 && v - med > 20;
                var bc = spike ? ((SolidColorBrush)Pal.Warn).Color : acc;
                b = new SolidColorBrush(Color.FromArgb((byte)(255 * (spike ? 1 : fade)), bc.R, bc.G, bc.B));
            }
            else { len = 1; b = lossIsReal ? Pal.Err : Pal.Gray; }
            double r2 = r1 + (R - r1) * len;
            ring.Children.Add(new Line { X1 = cx + r1 * Math.Sin(a), Y1 = cy - r1 * Math.Cos(a), X2 = cx + r2 * Math.Sin(a), Y2 = cy - r2 * Math.Cos(a), Stroke = b, StrokeThickness = Math.Max(1.6, Math.Min(3, 2 * Math.PI * r1 / RadarN * 0.7)), StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = hist[k] == null && !lossIsReal ? .5 : 1 });
        }
        var rot = new RotateTransform(0, cx, cy); ring.RenderTransform = rot; c.Children.Add(ring);
        if (Running && n > 0) rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(step, 0, TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        // marcador fixo de "agora" no topo
        var mk = new Ellipse { Width = 6, Height = 6, Fill = Res("T1") }; Canvas.SetLeft(mk, cx - 3); Canvas.SetTop(mk, cy - R - 3); c.Children.Add(mk);
        // disco central por cima de tudo: valor atual sempre legível
        var disk = new Ellipse { Width = r0 * 2, Height = r0 * 2, Fill = Res("BgCard2") }; Canvas.SetLeft(disk, cx - r0); Canvas.SetTop(disk, cy - r0); c.Children.Add(disk);
        var now = hist.Count > 0 ? hist[hist.Count - 1] : null;
        var ct = new TextBlock { TextAlignment = TextAlignment.Center, Width = r0 * 2 };
        ct.Inlines.Add(new System.Windows.Documents.Run(now is long nv ? nv.ToString() : h.Sent > 0 ? "perdido" : "…") { FontFamily = (FontFamily)FindResource("Num"), FontSize = now is long ? Math.Min(24, r0 * 0.55) : 13, Foreground = now == null && h.Sent > 0 ? (lossIsReal ? Pal.Err : Pal.Gray) : Res("T1") });
        if (now is long) { ct.Inlines.Add(new System.Windows.Documents.LineBreak()); ct.Inlines.Add(new System.Windows.Documents.Run(r0 >= 34 ? "ms agora" : "ms") { FontSize = r0 >= 34 ? 10 : 9, Foreground = Res("T3") }); }
        ct.Measure(new Size(200, 100)); Canvas.SetLeft(ct, cx - r0); Canvas.SetTop(ct, cy - ct.DesiredSize.Height / 2); c.Children.Add(ct);
        TxtRadarLegend.Text = vals.Count == 0 ? "" : $"anéis {scale / 3:F0} · {scale * 2 / 3:F0} · {scale:F0} ms · tracejado = normal ({med:F0} ms)";
        TxtRadarLegend.ToolTip = $"Cada traço é uma medição; a mais recente fica no topo e as anteriores seguem no sentido anti-horário. O comprimento é a latência em ms (borda = {scale:F0} ms ou mais). Tracejado = mediana da rota ({med:F0} ms). Amarelo = pico (mais que o dobro do normal). {(lossIsReal ? "Vermelho" : "Cinza")} = sem resposta.";
    }

}
