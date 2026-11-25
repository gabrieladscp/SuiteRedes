using System.ComponentModel;
using System.Windows.Media;
namespace SuiteRedes;

public static class Pal
{
    static SolidColorBrush B(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
    public static readonly Brush Ok = B("#4CC38A"), Warn = B("#E8B44C"), Err = B("#EF6B65"), Info = B("#4C8DFF"), Gray = B("#A3A9B5"),
        OkBg = B("#16291F"), WarnBg = B("#2E2614"), ErrBg = B("#351C1D"), GrayBg = B("#2A2E37"), InfoBg = B("#1A2640");
}

public enum Sev { Ok, Info, Warn, Error }


/// <summary>Linha de estatística: usada para destinos de ping e para saltos de rota.</summary>
public class StatRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public int Index { get; set; }
    public string Title { get; set; } = "";
    public string Address { get; set; } = "";
    public string Host { get; set; } = "";
    public bool HostChecked;
    /// <summary>(Sent no momento do cálculo, mediana). Tupla imutável: leitura/escrita atômica da referência.</summary>
    public (int at, double value) MedianCache = (-1, -1);
    public string Extra { get; set; } = "";
    public string Asn { get; set; } = "";
    public string Org { get; set; } = "";
    public string Delta { get; set; } = "";
    public string HopLabel => Index > 0 ? Index.ToString() : "★";
    public int Sent { get; private set; }
    public int Received { get; private set; }
    public double Loss => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
    double min, max, sum, jsum; int jn; long? prev;
    public double Avg => Received == 0 ? 0 : sum / Received;
    public double Jitter => jn == 0 ? 0 : jsum / jn;
    public double Min => min;
    public double Max => max;
    public string LossTxt => $"{Loss:F1}%";
    public string MinTxt => Received == 0 ? "—" : $"{min:F0} ms";
    public string AvgTxt => Received == 0 ? "—" : $"{Avg:F1} ms";
    public string MaxTxt => Received == 0 ? "—" : $"{max:F0} ms";
    public string JitTxt => jn == 0 ? "—" : $"{Jitter:F1} ms";
    public string LastTxt { get; private set; } = "—";
    public string LastError { get; private set; } = "";
    Dictionary<string, int> errs = new(); public Dictionary<string, int> Errors => errs;
    public string Status { get; private set; } = "Aguardando";
    public Brush StatusBrush { get; private set; } = Pal.Gray;
    List<long?> hist = new(); public List<long?> History => hist;
    /// <summary>Horário de cada amostra de History (mesmo índice).</summary>
    List<DateTime> times = new(); public List<DateTime> Times => times;

    /// <summary>Cópia independente para análise em outra thread.</summary>
    public StatRow Clone()
    {
        var c = (StatRow)MemberwiseClone();
        c.hist = new List<long?>(History); c.times = new List<DateTime>(Times); c.errs = new Dictionary<string, int>(Errors);
        c.PropertyChanged = null; return c;
    }
    public void Reset() { Sent = Received = 0; min = max = sum = jsum = 0; jn = 0; prev = null; History.Clear(); Times.Clear(); Errors.Clear(); }
    public void Record(long? rtt, string? err = null, bool notify = true)
    {
        Sent++;
        if (rtt is long r)
        {
            Received++; sum += r;
            if (Received == 1 || r < min) min = r;
            if (r > max) max = r;
            if (prev is long pv) { jsum += Math.Abs(r - pv); jn++; }
            prev = r; LastTxt = $"{r} ms";
        }
        else
        {
            prev = null; LastTxt = "timeout";
            if (err is string e && e.Length > 0) { LastError = e; Errors[e] = Errors.GetValueOrDefault(e) + 1; }
        }
        History.Add(rtt); Times.Add(DateTime.Now); if (History.Count > 300) { History.RemoveAt(0); Times.RemoveAt(0); }
        Evaluate(); if (notify) Raise();
    }
    public void SetStatus(string s, Brush b) { Status = s; StatusBrush = b; Raise(); }
    // Situação mostrada na tabela de saltos do monitor (leva em conta o contexto da rota: salto mudo no meio do caminho é normal)
    string? hopStatus; Brush? hopBrush;
    public string HopStatus => hopStatus ?? Status;
    public Brush HopBrush => hopBrush ?? StatusBrush;
    public void SetHop(string s, Brush b) { hopStatus = s; hopBrush = b; }
    void Evaluate()
    {
        if (Received == 0) { (Status, StatusBrush) = Sent >= 3 ? ("Sem resposta", Pal.Err) : ("Aguardando...", Pal.Gray); return; }
        (Status, StatusBrush) =
            Loss >= 50 ? ("Perda crítica", Pal.Err) :
            Loss >= 5 ? ("Perda", Pal.Warn) :
            Avg > 200 ? ("Latência alta", Pal.Warn) :
            Jitter > 30 ? ("Jitter alto", Pal.Warn) :
            Loss > 0 ? ("Perda leve", Pal.Warn) : ("OK", Pal.Ok);
    }
    public void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public class DnsRow
{
    public string Server { get; set; } = ""; public string Provider { get; set; } = "";
    public string Type { get; set; } = ""; public string Success { get; set; } = "";
    public string AvgTxt { get; set; } = ""; public string MinTxt { get; set; } = ""; public string MaxTxt { get; set; } = "";
    public string Status { get; set; } = ""; public string Answers { get; set; } = "";
    public Brush StatusBrush { get; set; } = Pal.Gray;
}
public class PortRow
{
    public int Port { get; set; } public string Service { get; set; } = ""; public string Status { get; set; } = "";
    public string Ms { get; set; } = ""; public Brush StatusBrush { get; set; } = Pal.Gray;
}
public class HttpRow
{
    public string Url { get; set; } = ""; public string Status { get; set; } = ""; public int Code { get; set; }
    public long DnsMs { get; set; } public long TtfbMs { get; set; } public long TotalMs { get; set; }
    public long Bytes { get; set; } public string Cert { get; set; } = "-";
    public Brush StatusBrush => Status == "OK" ? Pal.Ok : Pal.Err;
}
