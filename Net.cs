using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
namespace SuiteRedes;

public record ProbeResult(IPStatus Status, long Rtt, IPAddress? From);

public static class Net
{
    public static async Task<IPAddress?> ResolveAsync(string host, CancellationToken ct = default)
    {
        host = host.Trim();
        if (IPAddress.TryParse(host, out var ip)) return ip;
        try
        {
            var a = await Compat.GetHostAddressesAsync(host, ct);
            return a.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork) ?? a.FirstOrDefault();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public static async Task<ProbeResult> ProbeAsync(IPAddress ip, int timeoutMs, int ttl = 128, int size = 32, bool df = false, CancellationToken ct = default)
    {
        using var p = new Ping();
        var buf = new byte[Math.Max(0, size)]; for (int k = 0; k < buf.Length; k++) buf[k] = (byte)'a';
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = await p.SendPingAsync(ip, TimeSpan.FromMilliseconds(timeoutMs), buf, new PingOptions(ttl, df), ct);
            // O Windows devolve RoundtripTime = 0 nas respostas "TTL expirado"; nesse caso usa o tempo medido aqui.
            long rtt = r.Status != IPStatus.Success && r.RoundtripTime == 0 ? sw.ElapsedMilliseconds : r.RoundtripTime;
            return new(r.Status, rtt, r.Address);
        }
        catch (OperationCanceledException) { throw; }
        catch { return new(IPStatus.Unknown, 0, null); }
    }

    public static bool IsHopReply(IPStatus s) => s is IPStatus.Success or IPStatus.TtlExpired or IPStatus.TimeExceeded;

    public static async Task<string?> ReverseAsync(IPAddress ip, int timeoutMs = 2000)
    {
        try
        {
            var t = Dns.GetHostEntryAsync(ip);
            _ = t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted); // evita exceção "não observada" quando expira o tempo
            if (await Task.WhenAny(t, Task.Delay(timeoutMs)) == t) return t.Result.HostName;
        }
        catch { }
        return null;
    }

    public static List<string> Targets(string text) =>
        text.Split(new[] { '\r', '\n', ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

}

public record DnsAnswer(string Type, string Data, int Ttl);
public record DnsResponse(string Server, string QType, double Ms, string Status, List<DnsAnswer> Answers, bool Ok);

/// <summary>Cliente DNS UDP próprio: consulta qualquer servidor/tipo de registro e mede o tempo de resposta.</summary>
public static class DnsClient
{
    static readonly Random Rng = new();
    static readonly Dictionary<string, ushort> Types = new() { ["A"] = 1, ["AAAA"] = 28, ["CNAME"] = 5, ["MX"] = 15, ["NS"] = 2, ["TXT"] = 16 };
    static readonly Dictionary<int, string> Names = Types.ToDictionary(k => (int)k.Value, k => k.Key);
    public static string[] SupportedTypes => Types.Keys.ToArray();

    public static async Task<DnsResponse> QueryAsync(string server, string domain, string qtype, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var ip = IPAddress.Parse(server);
            var q = Build(domain, Types[qtype], (ushort)Rng.Next(65536));
            using var udp = new UdpClient(ip.AddressFamily);
            udp.Connect(ip, 53);
            await udp.SendAsync(q, q.Length);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeoutMs);
            var res = await udp.ReceiveAsync(cts.Token);
            sw.Stop();
            return Parse(res.Buffer, server, qtype, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(server, qtype, sw.Elapsed.TotalMilliseconds, "TIMEOUT", new(), false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return new(server, qtype, sw.Elapsed.TotalMilliseconds, "ERRO: " + e.Message, new(), false); }
    }

    static byte[] Build(string name, ushort qtype, ushort id)
    {
        var m = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var l in name.TrimEnd('.').Split('.')) { var b = Encoding.ASCII.GetBytes(l); m.Add((byte)b.Length); m.AddRange(b); }
        m.Add(0); m.Add((byte)(qtype >> 8)); m.Add((byte)qtype); m.Add(0); m.Add(1);
        return m.ToArray();
    }
    static int U16(byte[] b, int p) => (b[p] << 8) | b[p + 1];

    static string ReadName(byte[] b, ref int pos)
    {
        var sb = new StringBuilder(); int p = pos; bool jumped = false; int guard = 0;
        while (guard++ < 128)
        {
            int len = b[p];
            if (len == 0) { p++; break; }
            if ((len & 0xC0) == 0xC0) { int off = ((len & 0x3F) << 8) | b[p + 1]; if (!jumped) { pos = p + 2; jumped = true; } p = off; continue; }
            p++; sb.Append(Encoding.ASCII.GetString(b, p, len)).Append('.'); p += len;
        }
        if (!jumped) pos = p;
        return sb.ToString().TrimEnd('.');
    }

    static DnsResponse Parse(byte[] b, string server, string qtype, double ms)
    {
        if (b.Length < 12) return new(server, qtype, ms, "RESPOSTA INVÁLIDA", new(), false);
        int flags = U16(b, 2), rcode = flags & 0xF, qd = U16(b, 4), an = U16(b, 6);
        var answers = new List<DnsAnswer>(); int pos = 12;
        try
        {
            for (int i = 0; i < qd; i++) { ReadName(b, ref pos); pos += 4; }
            for (int i = 0; i < an; i++)
            {
                ReadName(b, ref pos);
                int t = U16(b, pos); int ttl = (U16(b, pos + 4) << 16) | U16(b, pos + 6); int rdlen = U16(b, pos + 8);
                int start = pos + 10, tmp = start;
                string data = t switch
                {
                    1 => new IPAddress(b.Slice(start, 4)).ToString(),
                    28 => new IPAddress(b.Slice(start, 16)).ToString(),
                    2 or 5 => ReadName(b, ref tmp),
                    15 => U16(b, start) + " " + ReadMx(b, start + 2),
                    16 => ReadTxt(b, start, rdlen),
                    _ => $"({rdlen} bytes)"
                };
                answers.Add(new(Names.GetValueOrDefault(t, "T" + t), data, ttl));
                pos = start + rdlen;
            }
        }
        catch { }
        string st = rcode switch { 0 => answers.Count > 0 ? "OK" : "SEM DADOS", 1 => "FORMERR", 2 => "SERVFAIL", 3 => "NXDOMAIN", 5 => "RECUSADO", _ => "RCODE " + rcode };
        return new(server, qtype, ms, st, answers, rcode == 0 && answers.Count > 0);
    }

    static string ReadMx(byte[] b, int p) => ReadName(b, ref p);

    static string ReadTxt(byte[] b, int p, int len)
    {
        var sb = new StringBuilder(); int end = p + len;
        while (p < end) { int l = b[p++]; sb.Append(Encoding.UTF8.GetString(b, p, l)); p += l; }
        return sb.ToString();
    }
}
