# Suíte de Redes

Análise de rotas em tempo real, diagnóstico da rede local e relatórios (Windows 10/11, .NET Framework 4.6.2).

## Privacidade e comportamento (transparência)
- Não coleta nem envia dados pessoais; consultas de rede: Cloudflare (`1.1.1.1/cdn-cgi/trace`), RIPEstat, RDAP e testes de conectividade (Microsoft, Google).
- Não altera o registro, não cria inicialização automática, não abre processos ocultos.
- Pings com TTL variado (traceroute) e sondas TCP/UDP são a função do programa.

## Compilar
`build-instalador.cmd` (requer .NET SDK e Inno Setup 6).
