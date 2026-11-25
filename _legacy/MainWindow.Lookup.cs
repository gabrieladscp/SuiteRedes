using System.Windows;
using System.Windows.Controls;
namespace SuiteRedes;

public partial class MainWindow
{
    CancellationTokenSource? lookupCts; 

    void TxtLookupIp_Changed(object s, TextChangedEventArgs e)
    {
        var t = TxtLookupIp; var clean = new string(t.Text.Where(c => Uri.IsHexDigit(c) || c is '.' or ':').ToArray());
        if (clean != t.Text) { int i = t.CaretIndex; t.Text = clean; t.CaretIndex = Math.Min(i, clean.Length); }
        if (t.IsKeyboardFocusWithin) t.Tag = "manual";
    }
    void TxtLookupIp_KeyDown(object s, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Enter) BtnLookup_Click(s, e); }

    async void BtnLookup_Click(object s, RoutedEventArgs e)
    {
        lookupCts?.Cancel(); lookupCts = new(); var ct = lookupCts.Token;
        BtnLookup.IsEnabled = false; TxtLookupStatus.Text = "Consultando registros públicos (RDAP, RIPEstat, Team Cymru)...";
        try
        {
            var r = await IpIntel.QueryAsync(TxtLookupIp.Text, ct);
            if (r.Error != "") { TxtLookupStatus.Text = r.Error; PnlLookup.Visibility = Visibility.Collapsed; return; }
            ItemsLkRouting.ItemsSource = r.Routing; ItemsLkReg.ItemsSource = r.Registry; ItemsLkUp.ItemsSource = r.Upstreams; ItemsLkDown.ItemsSource = r.Downstreams;
            ExpLkPref.Header = $"Prefixos anunciados ({r.PrefixTotal}{(r.PrefixTotal > r.Prefixes.Count ? $"; mostrando {r.Prefixes.Count}" : "")})";
            TxtLkPrefixes.Text = string.Join("   ", r.Prefixes); TxtLkWhois.Text = r.WhoisText == "" ? "Sem resposta do serviço de whois." : r.WhoisText;
            TxtLkNotes.Text = string.Join("\n", new[] { "Fontes: RDAP (registro regional do bloco), RIPEstat/RIPE RIS (roteamento BGP, vizinhos, prefixos, RPKI, whois), Team Cymru (conferência do AS)." }.Concat(r.Notes).Select(x => "• " + x));
            PnlLookup.Visibility = Visibility.Visible;
            TxtLookupStatus.Text = $"Consulta de {r.Ip} concluída em {DateTime.Now:HH:mm:ss}. Dados dos registros públicos, sem estimativas.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { TxtLookupStatus.Text = "Falha na consulta: " + ex.Message; }
        finally { BtnLookup.IsEnabled = true; }
    }

}
