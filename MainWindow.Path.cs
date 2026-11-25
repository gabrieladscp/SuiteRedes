using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
namespace SuiteRedes;

/// <summary>
/// Painel da rota. Uma pergunta por área: topo = "está bom?"; esquerda = "por onde passa?" (caminho agrupado por rede);
/// direita = "o que acontece neste ponto?" (detalhe do ponto clicado, gráfico e ocorrências).
/// </summary>
public partial class MainWindow
{
    StatRow? selHop;            // ponto clicado (null = destino)
    StatRow? PointRow => Sel == null ? null : selHop != null && Sel.Hops.Contains(selHop) ? selHop : Sel.Dest;

    Brush Res(string k) => (Brush)FindResource(k);
    static Brush BgFor(Sev s) => s switch { Sev.Error => Pal.ErrBg, Sev.Warn => Pal.WarnBg, _ => Pal.OkBg };

    void DrawSelected()
    {
        var r = Sel; bool has = r != null;
        PnlHealthBody.Visibility = healthMode ? Visibility.Visible : Visibility.Collapsed;
        if (healthMode) { PnlEmpty.Visibility = PnlBody.Visibility = Visibility.Collapsed; PnlHead.Visibility = Visibility.Visible; DrawHealth(); return; }
        PnlEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        PnlHead.Visibility = PnlBody.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (r == null) return;
        TxtDest.Text = r.Title;
        string ipTxt = r.Ip != null && r.Ip.ToString() != r.Host ? r.Ip.ToString() : "";
        TxtDestSub.Text = (r.Label != "" ? r.Host + (ipTxt != "" ? $"  ({ipTxt})" : "") : ipTxt) + (r.Tcp ? "   ·   destino bloqueia ping, medido por conexão TCP" : "");
        var (verdict, vb) = Verdict(r);
        TxtVerdict.Text = verdict;
        bool waiting = !r.Failed && (!r.Any || r.Phase != "Ao vivo");
        VerdictIcon.Background = waiting ? Pal.Gray : vb; TxtVerdictIcon.Text = waiting ? "…" : r.Failed || r.Worst == Sev.Error && r.First >= 0 ? "✕" : r.First >= 0 ? "!" : "✓";
        VerdictBox.Background = r.Failed ? Pal.ErrBg : !r.Any || r.Phase != "Ao vivo" ? Res("BgCard") : BgFor(r.First >= 0 ? r.Worst : Sev.Ok);

        var d = r.Dest; PnlStats.Children.Clear(); bool okd = d != null && d.Received > 0;
        Tile(PnlStats, okd ? $"{Median(d!):F0}" : "—", "ms", "latência (mediana)", null, 30);
        Tile(PnlStats, d != null && d.Sent > 0 ? $"{d.Loss:F1}" : "—", "%", "perda", d != null && d.Sent >= 5 && d.Loss >= LossWarn ? Pal.Err : null, 30);
        Tile(PnlStats, okd && d!.Received > 1 ? $"{d.Jitter:F0}" : "—", "ms", "oscilação", okd && d!.Received >= 10 && d.Jitter > 30 ? Pal.Warn : null, 30);
        BuildPath(r); ShowPoint(r);
    }

    void Tile(Panel host, string value, string unit, string label, Brush? color, double size)
    {
        var sp = new StackPanel { Margin = host is UniformGrid ? new Thickness(0, 0, 12, 0) : new Thickness(32, 0, 0, 0) };
        var line = new TextBlock { FontFamily = (FontFamily)FindResource("Num"), Foreground = color ?? Res("T1") };
        line.Inlines.Add(new System.Windows.Documents.Run(value) { FontSize = size });
        if (value != "—" && unit != "") line.Inlines.Add(new System.Windows.Documents.Run(" " + unit) { FontSize = size * .5, Foreground = Res("T3") });
        sp.Children.Add(line); sp.Children.Add(new TextBlock { Text = label, Foreground = Res("T3"), FontSize = 12.5 });
        host.Children.Add(sp);
    }

}
