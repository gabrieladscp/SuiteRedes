using System.Globalization;
using System.Net;
using System.Text;
namespace SuiteRedes;

public class ReportData
{
    public Report Rep = null!; public Evidence Ev = null!; public string Narrative = "", History = "";
    public List<StatRow> Dests = new(); public Dictionary<string, List<StatRow>> Traces = new();
    public List<DnsRow> Dns = new(); public List<HttpRow> Http = new();
    public List<EventRow> Events = new(); public Dictionary<string, List<Sample>> Samples = new();
    public List<InfoItem> Local = new(), Net = new(), Route = new(); public TimeSpan Monitored; public int SampleCount;
    public bool Anonymized; public string Machine = "";
}

/// <summary>Gerador de relatório HTML padronizado (arquivo único, sem dependências, pronto para imprimir/PDF).</summary>
public static class HtmlReport
{
    static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    static string F(double v, string f = "F1") => v.ToString(f, CultureInfo.GetCultureInfo("pt-BR"));
    static string Cls(Sev s) => s switch { Sev.Ok => "ok", Sev.Info => "info", Sev.Warn => "warn", _ => "err" };
    static string StCls(StageState s) => s switch { StageState.Ok => "ok", StageState.Warn => "warn", StageState.Error => "err", _ => "na" };
    static string StatCls(StatRow r) => r.Status switch { "OK" => "ok", "Perda leve" or "Perda" or "Latência alta" or "Jitter alto" => "warn", "Aguardando" or "Aguardando..." => "na", var s when s.StartsWith("Ping bloqueado") => "info", _ => "err" };

    static string Lbl(Sev s) => s switch { Sev.Ok => "Normal", Sev.Info => "Informativo", Sev.Warn => "Atenção", _ => "Crítico" };
    static string StLbl(StageState s) => s switch { StageState.Ok => "Normal", StageState.Warn => "Atenção", StageState.Error => "Falha", _ => "Sem dados" };

    const string Css = @"
:root{--bg:#0b1114;--page:#121a1e;--card:#172227;--ink:#e6eef2;--mut:#8a9da2;--line:#26353b;--acc:#7cb4ff;--ok:#4ade80;--warn:#fbbf24;--err:#f87171;--info:#7cb4ff;--na:#8a9da2;--grid:#22303a}
*{box-sizing:border-box}
body{font-family:'Segoe UI',Calibri,Arial,sans-serif;color:var(--ink);background:var(--bg);margin:0;font-size:13.5px;line-height:1.55;-webkit-print-color-adjust:exact;print-color-adjust:exact}
.page{max-width:1000px;margin:24px auto;background:var(--page);padding:40px 48px 32px;border:1px solid var(--line);border-radius:8px}
.doc-head{display:flex;justify-content:space-between;align-items:flex-end;border-bottom:2px solid var(--acc);padding-bottom:12px}
.doc-head h1{margin:0;font-size:22px;font-weight:600;color:#fff;letter-spacing:.01em}
.doc-head .org{font-size:11px;text-transform:uppercase;letter-spacing:.12em;color:var(--acc);margin-bottom:2px}
.doc-head .date{font-size:12px;color:var(--mut);text-align:right}.doc-head .date b{color:var(--ink)}
.meta{display:grid;grid-template-columns:repeat(4,1fr);border:1px solid var(--line);border-radius:6px;margin:16px 0 4px;background:var(--card)}
.meta div{padding:9px 12px;border-right:1px solid var(--line)}.meta div:last-child{border-right:0}
.meta span{display:block;font-size:10.5px;text-transform:uppercase;letter-spacing:.08em;color:var(--mut)}
.meta b{font-weight:600}
h2{font-size:15px;color:#fff;margin:30px 0 10px;padding-bottom:5px;border-bottom:1px solid var(--line);font-weight:600}
h2 .n{color:var(--acc);font-weight:600;margin-right:8px}
h3{font-size:13.5px;margin:16px 0 6px;font-weight:600;color:var(--ink)}
p{margin:6px 0}
.summary{background:var(--card);border:1px solid var(--line);border-left:4px solid var(--na);border-radius:6px;padding:14px 18px;margin:10px 0}
.summary.ok{border-left-color:var(--ok)}.summary.warn{border-left-color:var(--warn)}.summary.err{border-left-color:var(--err)}.summary.info{border-left-color:var(--info)}
.summary .k{font-size:10.5px;text-transform:uppercase;letter-spacing:.08em;color:var(--mut)}
.summary .t{font-size:17px;font-weight:600;margin:2px 0 4px;color:#fff}
.tag{display:inline-block;font-size:10.5px;font-weight:600;text-transform:uppercase;letter-spacing:.06em;padding:1px 7px;border:1px solid currentColor;border-radius:3px}
table{width:100%;border-collapse:collapse;font-size:12.5px;margin:6px 0 4px}
th{text-align:left;font-weight:600;color:var(--mut);font-size:11px;text-transform:uppercase;letter-spacing:.05em;padding:7px 8px;border-bottom:1px solid var(--acc);background:var(--card)}
td{padding:6px 8px;border-bottom:1px solid var(--line);vertical-align:top}
tr:hover td{background:#16232a}
td.num,th.num{text-align:right;font-variant-numeric:tabular-nums;white-space:nowrap}
table.kv th{width:30%;text-transform:none;letter-spacing:0;font-size:12.5px;font-weight:400;border-bottom:1px solid var(--line);color:var(--mut);background:transparent}
table.kv td{font-weight:500}
.b{font-weight:600}.ok{color:var(--ok)}.warn{color:var(--warn)}.err{color:var(--err)}.info{color:var(--info)}.na{color:var(--na)}
.cause{border-top:1px solid var(--line);padding:12px 0}
.cause:first-of-type{border-top:0}
.cause .row{display:flex;justify-content:space-between;gap:16px;align-items:baseline}
.cause .name{font-weight:600;font-size:14px;color:#fff}.cause .conf{font-size:12px;color:var(--mut);white-space:nowrap}
.lbl{font-size:10.5px;text-transform:uppercase;letter-spacing:.08em;color:var(--mut);margin-top:8px}
ul,ol{margin:3px 0 3px 20px;padding:0}li{margin:2px 0}
.fig{margin:14px 0 4px;page-break-inside:avoid}.fig .cap{font-size:12px;font-weight:600;margin-bottom:4px}
.note{font-size:12px;color:var(--mut)}
.notes{border-top:1px solid var(--line);margin-top:30px;padding-top:10px}
.privacy{background:var(--card);border:1px solid var(--line);border-radius:6px;padding:12px 16px;margin-top:18px;font-size:12px;color:var(--mut)}
.privacy b{color:var(--ink)}
footer{display:flex;justify-content:space-between;border-top:1px solid var(--line);margin-top:26px;padding-top:8px;font-size:11px;color:var(--mut)}
@media print{body{background:var(--bg)}.page{margin:0;max-width:none;border:0;border-radius:0}h2{break-after:avoid}tr{break-inside:avoid}.cause{break-inside:avoid}}
";

    static string Chart(List<Sample> all, int w = 880, int h = 110)
    {
        if (all.Count < 2) return "";
        int buckets = Math.Min(all.Count, 320); var pts = new List<(long max, bool loss)>();
        for (int i = 0; i < buckets; i++)
        {
            int a = i * all.Count / buckets, b = Math.Max(a + 1, (i + 1) * all.Count / buckets);
            var seg = all.Skip(a).Take(b - a).ToList();
            pts.Add((seg.Where(s => s.Rtt >= 0).Select(s => s.Rtt).DefaultIfEmpty(-1).Max(), seg.Any(s => s.Rtt < 0)));
        }
        double max = Math.Max(40, pts.Max(p => p.max) * 1.15); double dx = (double)w / Math.Max(1, buckets - 1);
        string I(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
        var sb = new StringBuilder($"<svg viewBox='0 0 {w} {h}' width='100%' height='{h}' preserveAspectRatio='none' style='border:1px solid #26353b;border-radius:4px;background:#0f171a'>");
        foreach (var f in new[] { .25, .5, .75 }) sb.Append($"<line x1='0' x2='{w}' y1='{I(h * f)}' y2='{I(h * f)}' stroke='#22303a' stroke-width='1'/>");
        for (int i = 0; i < buckets; i++) if (pts[i].loss) sb.Append($"<rect x='{I(i * dx)}' y='0' width='{I(Math.Max(2, dx))}' height='{h}' fill='#f87171' opacity='.25'/>");
        var line = string.Join(" ", pts.Select((p, i) => p.max < 0 ? null : $"{I(i * dx)},{I(h - 4 - p.max / max * (h - 8))}").Where(x => x != null));
        sb.Append($"<polyline points='{line}' fill='none' stroke='#7cb4ff' stroke-width='1.6' vector-effect='non-scaling-stroke'/></svg>");
        sb.Append($"<div class='note'>Escala de 0 a {F(max, "F0")} ms. {all.Count} amostras, de {all[0].T:HH:mm:ss} a {all[^1].T:HH:mm:ss}. Faixas vermelhas indicam perda de pacotes.</div>");
        return sb.ToString();
    }

    static void Kv(StringBuilder sb, string title, List<InfoItem> items)
    {
        if (items.Count == 0) return;
        sb.Append($"<h3>{E(title)}</h3><table class='kv'>");
        foreach (var i in items) sb.Append($"<tr><th>{E(i.Label)}</th><td>{E(i.Value)}</td></tr>");
        sb.Append("</table>");
    }

    public static string Build(ReportData d)
    {
        var top = d.Rep.Top; var sb = new StringBuilder(); int sec = 0;
        string H2(string t) => $"<h2><span class='n'>{++sec}.</span>{E(t)}</h2>";
        sb.Append("<!DOCTYPE html><html lang='pt-BR'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Relatório de Diagnóstico de Rede</title><style>").Append(Css).Append("</style></head><body><div class='page'>");
        sb.Append($"<div class='doc-head'><div><div class='org'>Suíte de Redes</div><h1>Relatório de Diagnóstico de Rede</h1></div><div class='date'>Emitido em<br><b>{DateTime.Now:dd/MM/yyyy HH:mm}</b></div></div>");
        sb.Append($"<div class='meta'><div><span>Computador</span><b>{E(d.Machine)}</b></div><div><span>Provedor</span><b>{E(d.Net.FirstOrDefault(x => x.Label.Contains("rovedor") || x.Label.Contains("perador"))?.Value is { Length: > 0 } pv ? pv : "—")}</b></div><div><span>Período monitorado</span><b>{d.Monitored:hh\\:mm\\:ss}</b></div><div><span>Amostras coletadas</span><b>{d.SampleCount}</b></div></div>");

        // Resumo
        sb.Append(H2("Resumo"));
        sb.Append($"<div class='summary {Cls(top.Severity)}'><div class='k'>{(top.Severity == Sev.Ok ? "Resultado" : "Causa mais provável")}</div><div class='t'>{E(top.Title)}</div>");
        if (top.Severity != Sev.Ok && top.WhoText != "") sb.Append($"<div class='b'>{E(top.WhoText)}</div>");
        sb.Append($"<div>{E(top.Summary)}</div><div class='note' style='margin-top:6px'>Grau de certeza da conclusão: {E(top.ConfText)}</div></div>");
        sb.Append($"<p>{E(d.Narrative).Replace("\n", "<br>")}</p>");
        if (d.History != "") sb.Append($"<p class='note'>{E(d.History)}</p>");

        // Etapas
        sb.Append(H2("Etapas da conexão"));
        sb.Append("<table><tr><th>Etapa</th><th>Situação</th><th>Detalhe</th></tr>");
        foreach (var s in d.Rep.Stages) sb.Append($"<tr><td class='b'>{E(s.Name)}</td><td class='b {StCls(s.State)}'>{StLbl(s.State)}</td><td>{E(s.Detail)}</td></tr>");
        sb.Append("</table>");

        // Informações
        if (d.Net.Count + d.Local.Count + d.Route.Count > 0)
        {
            sb.Append(H2("Informações da conexão"));
            Kv(sb, "Internet e provedor", d.Net); Kv(sb, "Rede local", d.Local); Kv(sb, "Rota", d.Route);
        }

        // Diagnóstico
        sb.Append(H2("Diagnóstico e recomendações"));
        foreach (var h in d.Rep.Hyps)
        {
            sb.Append($"<div class='cause'><div class='row'><span><span class='tag {Cls(h.Severity)}'>{Lbl(h.Severity)}</span>&nbsp; <span class='name'>{E(h.Title)}</span></span><span class='conf'>{E(h.ConfText)}</span></div>");
            if (h.WhoText != "") sb.Append($"<div class='note'>{E(h.WhoText)}</div>");
            sb.Append($"<p>{E(h.Summary)}</p>");
            if (h.Why.Count > 0) { sb.Append("<div class='lbl'>Evidências</div><ul>"); foreach (var w in h.Why) sb.Append($"<li>{E(w)}</li>"); sb.Append("</ul>"); }
            if (h.Steps.Count > 0) { sb.Append("<div class='lbl'>Ações recomendadas</div><ol>"); foreach (var s in h.Steps) sb.Append($"<li>{E(s)}</li>"); sb.Append("</ol>"); }
            sb.Append("</div>");
        }

        // Destinos
        sb.Append(H2("Destinos testados"));
        sb.Append("<table><tr><th>Destino</th><th>IP</th><th>Operadora</th><th class='num'>Env.</th><th class='num'>Perda</th><th class='num'>Mín.</th><th class='num'>Média</th><th class='num'>Máx.</th><th class='num'>Oscil.</th><th>Situação</th></tr>");
        foreach (var r in d.Dests)
            sb.Append($"<tr><td class='b'>{E(r.Title)}</td><td>{E(r.Address)}</td><td>{E((r.Asn + " " + r.Org).Trim())}</td><td class='num'>{r.Sent}</td><td class='num'>{E(r.LossTxt)}</td><td class='num'>{E(r.MinTxt)}</td><td class='num'>{E(r.AvgTxt)}</td><td class='num'>{E(r.MaxTxt)}</td><td class='num'>{E(r.JitTxt)}</td><td class='b {StatCls(r)}'>{E(r.Status)}</td></tr>");
        sb.Append("</table>");
        foreach (var r in d.Dests)
            if (d.Samples.TryGetValue(r.Title, out var smp) && smp.Count > 5) sb.Append($"<div class='fig'><div class='cap'>Latência ao longo do tempo: {E(r.Title)}</div>{Chart(smp)}</div>");

        // Rotas
        if (d.Traces.Count > 0)
        {
            sb.Append(H2("Rotas até os destinos"));
            foreach (var kv in d.Traces)
            {
                sb.Append($"<h3>Destino: {E(kv.Key)}</h3><table><tr><th>Salto</th><th>IP</th><th>Equipamento</th><th>Operadora</th><th class='num'>Perda</th><th class='num'>Média</th><th class='num'>Δ trecho</th><th class='num'>Oscil.</th><th>Situação</th></tr>");
                foreach (var r in kv.Value)
                    sb.Append($"<tr><td>{r.Index}</td><td>{E(r.Address)}</td><td>{E(r.Host)}</td><td>{E((r.Asn + " " + r.Org).Trim())}</td><td class='num {(r.Loss >= 20 ? "err b" : "")}'>{E(r.LossTxt)}</td><td class='num'>{E(r.AvgTxt)}</td><td class='num'>{E(r.Delta)}</td><td class='num'>{E(r.JitTxt)}</td><td class='b {StatCls(r)}'>{E(r.Status)}</td></tr>");
                sb.Append("</table>");
                var path = NetInfo.AsPath(kv.Value); if (path != "") sb.Append($"<p class='note'>Operadoras no caminho: {E(path)}</p>");
            }
        }

        // DNS / HTTPS
        if (d.Dns.Count > 0)
        {
            sb.Append(H2("Resolução de nomes (DNS)")).Append("<table><tr><th>Servidor</th><th>Empresa</th><th>Acertos</th><th class='num'>Média</th><th>Situação</th><th>Resposta</th></tr>");
            foreach (var r in d.Dns) sb.Append($"<tr><td>{E(r.Server)}</td><td>{E(r.Provider)}</td><td>{E(r.Success)}</td><td class='num'>{E(r.AvgTxt)}</td><td class='b {(r.Status == "OK" ? "ok" : r.Status == "INSTÁVEL" ? "warn" : "err")}'>{E(r.Status)}</td><td>{E(r.Answers)}</td></tr>");
            sb.Append("</table>");
        }
        if (d.Http.Count > 0)
        {
            sb.Append(H2("Acesso a sites (HTTPS)")).Append("<table><tr><th>Site</th><th>Situação</th><th>Código</th><th class='num'>DNS</th><th class='num'>1ª resposta</th><th class='num'>Total</th><th>Certificado até</th></tr>");
            foreach (var r in d.Http) sb.Append($"<tr><td>{E(r.Url)}</td><td class='b {(r.Status == "OK" ? "ok" : "err")}'>{E(r.Status)}</td><td>{r.Code}</td><td class='num'>{r.DnsMs} ms</td><td class='num'>{r.TtfbMs} ms</td><td class='num'>{r.TotalMs} ms</td><td>{E(r.Cert)}</td></tr>");
            sb.Append("</table>");
        }

        // Eventos
        sb.Append(H2("Eventos registrados"));
        if (d.Events.Count == 0) sb.Append("<p>Nenhum evento registrado no período.</p>");
        else
        {
            sb.Append("<table><tr><th>Hora</th><th>Alvo</th><th>Evento</th><th>Detalhe</th></tr>");
            foreach (var ev in d.Events.OrderBy(x => x.T)) sb.Append($"<tr><td class='num'>{E(ev.Time)}</td><td>{E(ev.Target)}</td><td class='b'>{E(ev.Kind)}</td><td>{E(ev.Detail)}</td></tr>");
            sb.Append("</table>");
        }

        sb.Append("<div class='privacy'><b>Privacidade e proteção de dados (LGPD).</b> ");
        sb.Append(d.Anonymized
            ? "Este relatório foi gerado em modo anonimizado: nome do computador, IP público, DNS reverso, endereço MAC, IPv6, nome da rede Wi-Fi e cidade foram ocultados ou mascarados, mantendo apenas o necessário para o diagnóstico (princípio da necessidade, art. 6º, III, da Lei 13.709/2018). "
            : "Este relatório contém dados que podem identificar o usuário ou o equipamento (por exemplo IP público, endereço MAC, nome do computador e da rede Wi-Fi), considerados dados pessoais pela Lei 13.709/2018 (LGPD) quando associáveis a uma pessoa. Compartilhe apenas com quem precisa dele para o atendimento técnico. ");
        sb.Append("O arquivo foi gerado localmente e não é enviado automaticamente a terceiros. Durante a verificação, o IP público e os destinos informados são consultados em serviços públicos de rede (RDAP, RIPEstat, Team Cymru, ipwho.is, Cloudflare) apenas para identificar provedor, rota e localização aproximada. Os endereços IP de roteadores e servidores das operadoras exibidos nas rotas são dados de infraestrutura pública de rede.</div>");
        sb.Append("<div class='notes note'><b>Notas.</b> Perda: percentual de pacotes sem resposta (ideal 0%). Média: tempo de ida e volta (bom abaixo de 50 ms). Oscilação: variação entre medições (bom abaixo de 30 ms). Δ trecho: tempo acrescentado em relação ao salto anterior. Perda em salto intermediário que não se propaga até o destino é comum, pois roteadores podem ignorar o teste. O diagnóstico é calculado por regras a partir das medições; o percentual indica o grau de certeza, não uma garantia.</div>");
        sb.Append($"<footer><span>Suíte de Redes</span><span>Relatório emitido em {DateTime.Now:dd/MM/yyyy HH:mm}</span></footer></div></body></html>");
        return sb.ToString();
    }

    public static string Csv(Dictionary<string, List<Sample>> samples)
    {
        var sb = new StringBuilder("data_hora;alvo;rtt_ms;perdido\n");
        foreach (var s in samples.Values.SelectMany(x => x).OrderBy(x => x.T)) sb.Append($"{s.T:yyyy-MM-dd HH:mm:ss.fff};{s.Target};{(s.Rtt >= 0 ? s.Rtt.ToString() : "")};{(s.Rtt < 0 ? 1 : 0)}\n");
        return sb.ToString();
    }
}
