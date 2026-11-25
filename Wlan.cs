using System.Runtime.InteropServices;
using System.Text;
namespace SuiteRedes;

/// <summary>
/// Dados do Wi-Fi conectado pela API nativa do Windows (wlanapi.dll), sem abrir processos ocultos (netsh).
/// Antivírus tratam "processo oculto + saída redirecionada" como comportamento suspeito.
/// Retorna texto no mesmo formato "Chave : valor" que NetInfo.WifiDetails e Diagnosis.GatherLocal já leem.
/// </summary>
static class Wlan
{
    [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint ver, IntPtr res, out uint negVer, out IntPtr h);
    [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr h, IntPtr res);
    [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr h, IntPtr res, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr h, ref Guid g, int op, IntPtr res, out int size, out IntPtr data, IntPtr type);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);

    const int OpCurrentConnection = 7, OpChannel = 8, ItemSize = 532, StateConnected = 1;

    public static string Info()
    {
        IntPtr h = IntPtr.Zero, list = IntPtr.Zero;
        // só usa a wlanapi se houver adaptador Wi-Fi ativo (PCs com cabo/servidores podem nem ter o serviço WLAN)
        if (!System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(x => x.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 && x.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)) return "";
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out var oh) != 0) return ""; // serviço WLAN parado/ausente
            h = oh;
            if (WlanEnumInterfaces(h, IntPtr.Zero, out var ol) != 0) return "";
            list = ol;
            int n = Marshal.ReadInt32(list);
            for (int i = 0; i < n; i++)
            {
                var item = list + 8 + i * ItemSize;
                var g = (Guid)Marshal.PtrToStructure(item, typeof(Guid))!;
                if (Marshal.ReadInt32(item + 16 + 512) != StateConnected) continue;
                if (WlanQueryInterface(h, ref g, OpCurrentConnection, IntPtr.Zero, out _, out var c, IntPtr.Zero) != 0) continue;
                var sb = new StringBuilder();
                try
                {
                    int len = Math.Min(32, Marshal.ReadInt32(c + 520)); var ssid = new byte[len]; Marshal.Copy(c + 524, ssid, 0, len);
                    int phy = Marshal.ReadInt32(c + 568), sig = Marshal.ReadInt32(c + 576), rx = Marshal.ReadInt32(c + 580), tx = Marshal.ReadInt32(c + 584), auth = Marshal.ReadInt32(c + 596);
                    string radio = phy switch { 4 => "802.11a", 5 => "802.11b", 6 => "802.11g", 7 => "802.11n", 8 => "802.11ac", 10 => "802.11ax", 11 => "802.11be", _ => "" };
                    sb.Append("SSID : ").AppendLine(Encoding.UTF8.GetString(ssid));
                    if (radio != "") sb.Append("Radio type : ").AppendLine(radio);
                    sb.Append("Authentication : ").AppendLine(auth switch { 1 => "Aberta", 2 => "Chave compartilhada", 3 => "WPA-Enterprise", 4 => "WPA-Personal", 6 => "WPA2-Enterprise", 7 => "WPA2-Personal", 8 or 11 => "WPA3-Enterprise", 9 => "WPA3-Personal", 10 => "OWE", _ => "Outra" });
                    sb.Append("Receive rate (Mbps) : ").AppendLine((rx / 1000).ToString());
                    sb.Append("Transmit rate (Mbps) : ").AppendLine((tx / 1000).ToString());
                    sb.Append("Signal : ").Append(sig).AppendLine("%");
                    if (WlanQueryInterface(h, ref g, OpChannel, IntPtr.Zero, out _, out var ch, IntPtr.Zero) == 0)
                    {
                        int chn = Marshal.ReadInt32(ch); WlanFreeMemory(ch);
                        sb.Append("Channel : ").AppendLine(chn.ToString());
                        // Wi-Fi 6/6E/7 podem usar 6 GHz com os mesmos números de canal: só informa banda quando não há ambiguidade
                        if (phy is not (10 or 11)) sb.Append("Band : ").AppendLine(chn <= 14 ? "2,4 GHz" : "5 GHz");
                    }
                }
                finally { WlanFreeMemory(c); }
                return sb.ToString();
            }
            return "";
        }
        catch { return ""; } // sem serviço WLAN (servidores, PCs só com cabo)
        finally { if (list != IntPtr.Zero) WlanFreeMemory(list); if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero); }
    }
}
