// Camada de compatibilidade para .NET Framework 4.7.2: recursos de linguagem e de API usados pelo programa.
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
    internal static partial class RuntimeHelpersCompat { }
}

namespace System
{
    internal readonly struct Index
    {
        readonly int v;
        public Index(int value, bool fromEnd = false) { v = fromEnd ? ~value : value; }
        public static Index Start => new(0); public static Index End => new(0, true);
        public bool IsFromEnd => v < 0; public int Value => v < 0 ? ~v : v;
        public int GetOffset(int length) => IsFromEnd ? length - Value : Value;
        public static implicit operator Index(int value) => new(value);
        public static Index FromStart(int value) => new(value); public static Index FromEnd(int value) => new(value, true);
    }
    internal readonly struct Range
    {
        public Index Start { get; } public Index End { get; }
        public Range(Index start, Index end) { Start = start; End = end; }
        public static Range StartAt(Index s) => new(s, Index.End); public static Range EndAt(Index e) => new(Index.Start, e); public static Range All => new(Index.Start, Index.End);
        public (int Offset, int Length) GetOffsetAndLength(int length) { int s = Start.GetOffset(length), e = End.GetOffset(length); return (s, e - s); }
    }
}

namespace SuiteRedes
{
    internal static class Compat
    {
        // ---- texto ----
        public static bool Contains(this string s, string value, StringComparison cmp) => s.IndexOf(value, cmp) >= 0;
        public static string Replace(this string s, string oldValue, string? newValue, StringComparison cmp)
        {
            if (string.IsNullOrEmpty(oldValue)) return s;
            var sb = new System.Text.StringBuilder(); int i = 0, j;
            while ((j = s.IndexOf(oldValue, i, cmp)) >= 0) { sb.Append(s, i, j - i).Append(newValue); i = j + oldValue.Length; }
            return sb.Append(s, i, s.Length - i).ToString();
        }
        public static bool StartsWith(this string s, char c) => s.Length > 0 && s[0] == c;
        public static bool EndsWith(this string s, char c) => s.Length > 0 && s[s.Length - 1] == c;
        public static string[] Split(this string s, char c, StringSplitOptions o) => s.Split(new[] { c }, o);
        public static string Substring(this string s, Range r) { var (o, l) = r.GetOffsetAndLength(s.Length); return s.Substring(o, l); }

        // ---- coleções ----
        public static TV GetValueOrDefault<TK, TV>(this IDictionary<TK, TV> d, TK k, TV def = default!) => d.TryGetValue(k, out var v) ? v : def;
        public static TV GetValueOrDefault<TK, TV>(this Dictionary<TK, TV> d, TK k, TV def = default!) where TK : notnull => d.TryGetValue(k, out var v) ? v : def;
        public static IEnumerable<T> TakeLast<T>(this IEnumerable<T> src, int n) { var l = src.ToList(); return l.Skip(Math.Max(0, l.Count - n)); }
        public static void Deconstruct<TK, TV>(this KeyValuePair<TK, TV> kv, out TK k, out TV v) { k = kv.Key; v = kv.Value; }
        public static T[] Slice<T>(this T[] a, int start, int length) { var r = new T[length]; Array.Copy(a, start, r, 0, length); return r; }

        // ---- números ----
        public static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;
        public static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
        public static long Clamp(long v, long min, long max) => v < min ? min : v > max ? max : v;

        // ---- rede: sobrecargas com cancelamento que não existem no .NET Framework ----
        static async Task<T> WithCancel<T>(Task<T> t, CancellationToken ct)
        {
            if (!ct.CanBeCanceled) return await t.ConfigureAwait(false);
            var tcs = new TaskCompletionSource<bool>();
            using (ct.Register(() => tcs.TrySetResult(true)))
                if (await Task.WhenAny(t, tcs.Task).ConfigureAwait(false) != t) { _ = t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted); throw new OperationCanceledException(ct); }
            return await t.ConfigureAwait(false);
        }
        public static async Task<string> GetStringAsync(this HttpClient h, string url, CancellationToken ct)
        { using var r = await h.GetAsync(url, ct).ConfigureAwait(false); r.EnsureSuccessStatusCode(); return await r.Content.ReadAsStringAsync().ConfigureAwait(false); }
        public static Task<string> ReadAsStringAsync(this HttpContent c, CancellationToken ct) => WithCancel(c.ReadAsStringAsync(), ct);
        public static Task<byte[]> ReadAsByteArrayAsync(this HttpContent c, CancellationToken ct) => WithCancel(c.ReadAsByteArrayAsync(), ct);
        public static Task<Stream> ReadAsStreamAsync(this HttpContent c, CancellationToken ct) => WithCancel(c.ReadAsStreamAsync(), ct);
        public static Task<UdpReceiveResult> ReceiveAsync(this UdpClient u, CancellationToken ct) => WithCancel(u.ReceiveAsync(), ct);
        public static Task<int> SendAsync(this UdpClient u, byte[] data, IPEndPoint ep, CancellationToken ct) => WithCancel(u.SendAsync(data, data.Length, ep), ct);
        public static Task<PingReply> SendPingAsync(this Ping p, IPAddress ip, TimeSpan timeout, byte[] buffer, PingOptions opt, CancellationToken ct)
            => WithCancel(p.SendPingAsync(ip, (int)timeout.TotalMilliseconds, buffer, opt), ct);
        public static Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken ct) => WithCancel(Dns.GetHostAddressesAsync(host), ct);
        public static Task ConnectAsync(this TcpClient c, IPAddress ip, int port, CancellationToken ct) => WithCancel(c.ConnectAsync(ip, port).ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }), ct);
        public static Task<int> ReadAsync(this Stream s, byte[] buf, CancellationToken ct) => s.ReadAsync(buf, 0, buf.Length, ct);
    }
}
