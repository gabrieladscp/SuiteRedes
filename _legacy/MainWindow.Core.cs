using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
namespace SuiteRedes;

// Rotinas de teste reutilizáveis (chamadas tanto pelas abas quanto pelo diagnóstico completo).
// Todas são async e retomam na thread de UI, então podem atualizar StatRow/ObservableCollection diretamente.
public partial class MainWindow
{
    static readonly Dictionary<string, string> DnsProviders = new()
    {
        ["1.1.1.1"] = "Cloudflare", ["1.0.0.1"] = "Cloudflare", ["8.8.8.8"] = "Google", ["8.8.4.4"] = "Google",
        ["9.9.9.9"] = "Quad9", ["208.67.222.222"] = "OpenDNS", ["208.67.220.220"] = "OpenDNS"
    };

    /// <summary>Executa N pings e acumula na linha (usada para gateway/internet no diagnóstico).</summary>
    async Task PingLoopAsync(StatRow row, IPAddress ip, int count, int intervalMs, int timeout, int size, bool df, CancellationToken ct)
    {
        row.Address = ip.ToString();
        for (int i = 0; count == 0 || i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            var r = await Net.ProbeAsync(ip, timeout, 128, size, df, ct);
            row.Record(r.Status == IPStatus.Success ? r.Rtt : null, r.Status.ToString());
            int wait = intervalMs - (int)sw.ElapsedMilliseconds;
            if (wait > 0 && (count == 0 || i < count - 1)) await Task.Delay(wait, ct);
        }
    }

    async Task ResolveHostAsync(StatRow row, IPAddress ip)
    {
        var h = await Net.ReverseAsync(ip);
        if (h != null) { row.Host = h; row.Raise(); }
    }

    /// <summary>Descobre os saltos e depois mede cada um por várias rodadas (estilo MTR). Retorna true se o destino foi alcançado.</summary>
    async Task<bool> TraceAsync(IPAddress ip, int maxHops, int rounds, int timeout, bool resolve, ICollection<StatRow> rows, CancellationToken ct, Action<string>? status = null)
    {
        int fails = 0; bool reached = false;
        for (int ttl = 1; ttl <= maxHops; ttl++)
        {
            ct.ThrowIfCancellationRequested();
            status?.Invoke($"Descobrindo salto {ttl}...");
            ProbeResult pr = new(IPStatus.Unknown, 0, null);
            for (int a = 0; a < 3; a++) { pr = await Net.ProbeAsync(ip, timeout, ttl, 32, false, ct); if (Net.IsHopReply(pr.Status)) break; }
            var row = new StatRow { Index = ttl, Title = ttl.ToString() };
            if (Net.IsHopReply(pr.Status)) { row.Address = pr.From?.ToString() ?? "?"; fails = 0; if (resolve && pr.From != null) _ = ResolveHostAsync(row, pr.From); }
            else { row.Address = "*"; fails++; }
            rows.Add(row);
            if (pr.Status == IPStatus.Success) { reached = true; break; }
            if (fails >= 6) break;
        }
        var list = rows.ToList();
        for (int r = 0; r < rounds; r++)
        {
            ct.ThrowIfCancellationRequested();
            status?.Invoke($"Medindo rodada {r + 1}/{rounds}...");
            await MeasureRoundAsync(ip, list, timeout, ct);
            await Task.Delay(400, ct);
        }
        return reached;
    }

    async Task<List<DnsRow>> DnsTestAsync(string domain, string qtype, List<string> servers, int attempts, int timeout, CancellationToken ct)
    {
        var tasks = servers.Select(async s =>
        {
            var rs = new List<DnsResponse>();
            for (int i = 0; i < attempts; i++) { rs.Add(await DnsClient.QueryAsync(s, domain, qtype, timeout, ct)); await Task.Delay(100, ct); }
            var ok = rs.Where(x => x.Ok).ToList();
            var last = rs.Last();
            var answers = string.Join("; ", rs.SelectMany(x => x.Answers).Select(a => a.Data).Distinct().Take(6));
            string st = ok.Count == attempts ? "OK" : ok.Count == 0 ? last.Status : "INSTÁVEL";
            return new DnsRow
            {
                Server = s, Provider = DnsProviders.GetValueOrDefault(s, Diagnostics.SystemDns().Contains(s) ? "Sistema" : ""), Type = qtype,
                Success = $"{ok.Count}/{attempts}", Status = st,
                AvgTxt = ok.Count > 0 ? $"{ok.Average(x => x.Ms):F0} ms" : "—", MinTxt = ok.Count > 0 ? $"{ok.Min(x => x.Ms):F0} ms" : "—", MaxTxt = ok.Count > 0 ? $"{ok.Max(x => x.Ms):F0} ms" : "—",
                Answers = answers, StatusBrush = st == "OK" ? Pal.Ok : st == "INSTÁVEL" ? Pal.Warn : Pal.Err
            };
        });
        return (await Task.WhenAll(tasks)).ToList();
    }


    async Task<(string state, long ms)> TcpProbeAsync(IPAddress ip, int port, int timeoutMs, CancellationToken ct)
    {
        using var c = new TcpClient(ip.AddressFamily);
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeoutMs);
        try { await c.ConnectAsync(ip, port, cts.Token); return ("Aberta", sw.ElapsedMilliseconds); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return ("Filtrada", sw.ElapsedMilliseconds); }
        catch (SocketException se) when (se.SocketErrorCode == SocketError.ConnectionRefused) { return ("Fechada", sw.ElapsedMilliseconds); }
        catch (SocketException) { return ("Inalcançável", sw.ElapsedMilliseconds); }
    }

    async Task<HttpRow> HttpTestAsync(string url, CancellationToken ct)
    {
        var row = new HttpRow { Url = url };
        try
        {
            var uri = new Uri(url.Contains("://") ? url : "https://" + url);
            var sw = Stopwatch.StartNew();
            await Compat.GetHostAddressesAsync(uri.Host, ct); row.DnsMs = sw.ElapsedMilliseconds;
            DateTime? exp = null;
            var h = new HttpClientHandler();
            h.ServerCertificateCustomValidationCallback = (m, c, ch, e) => { if (c != null) exp = c.NotAfter; return e == SslPolicyErrors.None; };
            using var http = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(15) };
            sw.Restart();
            using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            row.TtfbMs = sw.ElapsedMilliseconds; row.Code = (int)resp.StatusCode;
            using var s = await resp.Content.ReadAsStreamAsync(ct);
            var buf = new byte[8192]; int n; long bytes = 0;
            while ((n = await s.ReadAsync(buf, ct)) > 0 && bytes < 2_000_000) bytes += n;
            row.TotalMs = sw.ElapsedMilliseconds; row.Bytes = bytes;
            row.Cert = exp?.ToString("dd/MM/yyyy") ?? "-";
            row.Status = row.Code < 400 ? "OK" : "HTTP " + row.Code;
            if (exp is DateTime d && d < DateTime.Now.AddDays(10)) row.Status += " (cert. expira!)";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            var m = e.InnerException?.Message ?? e.Message;
            row.Status = m.Length > 60 ? m[..60] : m;
            if (e is TaskCanceledException) row.Status = "Timeout";
        }
        return row;
    }

    /// <summary>Busca binária do maior pacote que passa sem fragmentação. Retorna MTU (payload+28) ou null.</summary>
    async Task<int?> FindMtuAsync(IPAddress ip, Action<string> log, CancellationToken ct)
    {
        var first = await Net.ProbeAsync(ip, 2000, 128, 32, true, ct);
        if (first.Status != IPStatus.Success) { log($"Destino não respondeu ao ping base ({first.Status}). Impossível medir MTU."); return null; }
        int lo = 32, hi = 1472, best = 32;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2; bool ok = false;
            for (int t = 0; t < 2 && !ok; t++) ok = (await Net.ProbeAsync(ip, 2000, 128, mid, true, ct)).Status == IPStatus.Success;
            log($"  payload {mid} bytes (pacote {mid + 28}): {(ok ? "passa" : "fragmentaria/bloqueado")}");
            if (ok) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return best + 28;
    }
}

public record SpeedResult(double Mbps, long Bytes, double Secs, double? LoadedAvg, double LoadedLoss);

public partial class MainWindow
{
    /// <summary>Baixa dados por N segundos medindo a velocidade e, ao mesmo tempo, o ping (detecta bufferbloat).</summary>
    async Task<SpeedResult?> SpeedTestAsync(int seconds, Action<string>? status, CancellationToken ct)
    {
        var row = new StatRow { Title = "carga" };
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pingTask = PingLoopAsync(row, IPAddress.Parse("1.1.1.1"), 0, 250, 1500, 32, false, pingCts.Token);
        long total = 0; var sw = Stopwatch.StartNew(); bool ok = true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(seconds + 10) };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(seconds));
            while (!cts.IsCancellationRequested) {
            using var resp = await http.GetAsync("https://speed.cloudflare.com/__down?bytes=25000000", HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) break;
            using var st = await resp.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[65536]; int n;
            try { while ((n = await st.ReadAsync(buf, cts.Token)) > 0) { total += n; status?.Invoke($"Medindo velocidade... {total * 8 / 1e6 / Math.Max(0.2, sw.Elapsed.TotalSeconds):F0} Mbps"); } }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { pingCts.Cancel(); throw; }
        catch { ok = total > 0; }
        pingCts.Cancel();
        try { await pingTask; } catch (OperationCanceledException) { }
        if (!ok || total < 500_000) return null;
        double secs = Math.Max(0.2, sw.Elapsed.TotalSeconds);
        return new SpeedResult(total * 8 / 1e6 / secs, total, secs, row.Received > 2 ? row.Avg : null, row.Loss);
    }
}

public partial class MainWindow
{
}

public partial class MainWindow
{
    /// <summary>Uma rodada de MTR: sonda todos os saltos em paralelo (TTL = nº do salto).</summary>
    static Task MeasureRoundAsync(IPAddress ip, List<StatRow> rows, int timeout, CancellationToken ct) =>
        Task.WhenAll(rows.Select(async row =>
        {
            var p = await Net.ProbeAsync(ip, timeout, row.Index, 32, false, ct);
            bool ok = Net.IsHopReply(p.Status);
            if (ok && row.Address == "*" && p.From != null) row.Address = p.From.ToString();
            row.Record(ok ? p.Rtt : null, ok ? null : p.Status.ToString());
        }));
}
