# Suíte de Redes

Análise de rotas em tempo real, diagnóstico da rede local e relatórios (Windows 10/11, .NET Framework 4.6.2).

## Privacidade e comportamento (transparência)
- Não coleta nem envia dados pessoais; consultas de rede: Cloudflare (`1.1.1.1/cdn-cgi/trace`), RIPEstat, RDAP e testes de conectividade (Microsoft, Google).
- Não altera o registro, não cria inicialização automática, não abre processos ocultos.
- Pings com TTL variado (traceroute) e sondas TCP/UDP são a função do programa.

## Compilar
`build-instalador.cmd` (requer .NET SDK e Inno Setup 6).

## Assinatura
Binários de release são assinados via SignPath Foundation (código aberto, licença MIT).
Verifique o SHA-256 publicado em cada release.

## Code signing policy
Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org/).

- Signed artifacts are built only from this repository by the GitHub Actions workflow `release.yml`.
- Roles: author/reviewer/approver — [@gabrieladscp](https://github.com/gabrieladscp). Multi-factor authentication is required for GitHub and SignPath.
- **Privacy policy:** this program does not collect, store or transmit personal data. It only makes the network requests listed above, to measure connectivity.
- **System changes:** the installer installs to Program Files, registers an uninstaller (Apps & Features) and makes no other system changes.
- This program is a connectivity/route diagnostic tool. It does not scan for vulnerabilities or exploit anything.
