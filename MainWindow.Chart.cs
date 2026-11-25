using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Animation;
namespace SuiteRedes;

public partial class MainWindow
{
    // ---------- conexão do cliente ----------
    PublicInfo? pubInfo;
    static string AsDigits(string? a) => new string((a ?? "").Where(char.IsDigit).ToArray());
    static bool SameAs(string? a, string? b) { var x = AsDigits(a); return x != "" && x == AsDigits(b); }

    async Task LoadPublic(CancellationToken ct)
    {
        TxtPubIp.Text = "…"; TxtPubIsp.Text = "Identificando IP público e provedor"; TxtPubMore.Text = "";
        try
        {
            var p = await NetInfo.GetPublicAsync(ct); pubInfo = p;
            if (!p.Ok) { TxtPubIp.Text = "—"; TxtPubIsp.Text = "Não foi possível identificar o IP público."; return; }
            TxtPubIp.Text = p.Ip;
            var asn = p.Asn != "" ? (p.Asn.StartsWith("AS") ? p.Asn : "AS" + p.Asn) : "";
            TxtPubIsp.Text = string.Join("  ", new[] { asn, p.IspName }.Where(x => x != "")) is { Length: > 0 } t ? t : "Provedor não identificado";
            var more = new List<string>();
            if (p.Place != "") more.Add(p.Place + " (localização aproximada)");
            if (p.Rdns != "") more.Add(p.Rdns);
            if (p.SeenIps.Distinct().Count() > 1) more.Add($"Serviços diferentes viram {p.SeenIps.Distinct().Count()} IPs públicos diferentes: {string.Join(", ", p.SeenIps.Distinct())}");
            if (p.Ipv6 != "") more.Add("IPv6 ativo: " + p.Ipv6);
            TxtPubMore.Text = string.Join("\n", more);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log(ex); TxtPubIp.Text = "—"; TxtPubIsp.Text = "Não foi possível identificar o IP público."; }
    }

    static double Nice(double v) { foreach (var s in new[] { 20, 50, 100, 150, 200, 300, 400, 500, 750, 1000, 1500, 2000, 3000, 5000 }) if (v <= s) return s; return Math.Ceiling(v / 1000) * 1000; }
}
