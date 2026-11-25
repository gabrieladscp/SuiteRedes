; Instalador da Suíte de Redes (Inno Setup 6).
; Escolhas pensadas para reduzir falsos positivos de antivírus em executáveis sem assinatura:
;  - instala em Program Files (local padrão), com desinstalador registrado e metadados completos;
;  - sem compressão "solid"/LZMA máxima e sem empacotadores (UPX etc.), que a heurística associa a malware;
;  - não executa scripts, não altera Defender/firewall, não cria inicialização automática.
#define AppName "Suíte de Redes"
#define AppVer  "2.0.0"
#define AppExe  "SuiteRedes.exe"
#define Src     "..\bin\Release\net462"

[Setup]
AppId={{6B7F2C1E-4A3D-4E8B-9C21-5D0F3A7B8E14}
AppName={#AppName}
AppVersion={#AppVer}
AppVerName={#AppName} {#AppVer}
AppPublisher=Skytins
AppPublisherURL=https://www.skytins.com.br
AppCopyright=© 2026 Skytins
VersionInfoVersion={#AppVer}.0
VersionInfoCompany=Skytins
VersionInfoDescription=Instalador da {#AppName}
VersionInfoProductName={#AppName}
VersionInfoCopyright=© 2026 Skytins
DefaultDirName={autopf}\Skytins\Suite de Redes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish\instalador
OutputBaseFilename=SuiteRedes-Setup
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=zip
SolidCompression=no
WizardStyle=modern
MinVersion=10.0

[Languages]
Name: "pt"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; Flags: unchecked

[Files]
Source: "{#Src}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Src}\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#Src}\{#AppExe}.config"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

; Sem [Run] para abrir o programa ao final: antivírus comportamentais tratam "instalador que executa
; o que acabou de gravar" como dropper e apagam o instalador junto.
