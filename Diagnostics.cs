using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
namespace SuiteRedes;

public static class Diagnostics
{
    static readonly string[] VirtualKeys = { "virtual", "vmware", "hyper-v", "vethernet", "virtualbox", "bluetooth", "npcap", "tap-", "wan miniport", "loopback", "vpn", "wireguard", "tunnel", "docker", "wsl", "filter", "scheduler", "wfp", "lightweight", "kaspersky", "sophos" };
    public static bool IsVirtual(NetworkInterface n) => VirtualKeys.Any(k => (n.Description + " " + n.Name).Contains(k, StringComparison.OrdinalIgnoreCase));


    public static List<NetworkInterface> Nics() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)).ToList();

    public static IEnumerable<IPAddress> GatewaysV4(NetworkInterface n) =>
        n.GetIPProperties().GatewayAddresses.Select(g => g.Address)
         .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

    /// <summary>Adaptador físico ativo com gateway IPv4 (o "principal").</summary>
    public static (NetworkInterface? Nic, IPAddress? Gateway) Primary()
    {
        var c = Nics().Where(n => n.OperationalStatus == OperationalStatus.Up).Select(n => (n, g: GatewaysV4(n).FirstOrDefault()))
                      .Where(x => x.g != null).OrderBy(x => IsVirtual(x.n) ? 1 : 0).FirstOrDefault();
        return c.n == null ? (null, null) : (c.n, c.g);
    }

    public static List<string> SystemDns() => Nics().Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().DnsAddresses).Where(a => a.AddressFamily == AddressFamily.InterNetwork)
        .Select(a => a.ToString()).Distinct().ToList();

    public static string WifiInfo() => Wlan.Info();
}
