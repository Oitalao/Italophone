; Instalador do Italophone (Inno Setup 6). Gerado por publicar.ps1 a partir de dist\Italophone.
; Instala só para o usuário atual: não pede senha de administrador.

#define Nome "Italophone"
#ifndef Versao
  #define Versao "1.1.0"
#endif
#define Exe "Italophone.exe"

[Setup]
AppId={{8C1F4E2A-6B3D-4F7A-9E25-1D0B7A3C5E91}
AppName={#Nome}
AppVersion={#Versao}
AppVerName={#Nome} {#Versao}
AppPublisher=Ítalo Sêne
AppPublisherURL=https://github.com/Oitalao/Italophone
AppSupportURL=https://github.com/Oitalao/Italophone/issues
AppUpdatesURL=https://github.com/Oitalao/Italophone/releases
VersionInfoVersion={#Versao}
VersionInfoDescription=Instalador do {#Nome}
DefaultDirName={localappdata}\Programs\{#Nome}
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\dist
OutputBaseFilename=Italophone-{#Versao}-instalador
SetupIconFile=..\host\italophone.ico
UninstallDisplayIcon={app}\{#Exe}
UninstallDisplayName={#Nome}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=no
RestartApplications=no
ShowLanguageDialog=no

[Languages]
Name: "pt"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Messages]
pt.WelcomeLabel2=Isto vai instalar o [name/ver] no seu computador.%n%nCom o celular Android ligado no cabo USB, o mouse e o teclado do computador passam para o celular, o som do celular toca no computador e a tela dele pode aparecer numa janela. Não precisa de senha de administrador.
pt.FinishedLabel=O [name] foi instalado e fica perto do relógio.%n%nNo celular, ligue a "Depuração USB" (veja o guia no site) e ligue o cabo. Na primeira vez, toque em "Permitir" na tela do celular.

[Tasks]
Name: "iniciar"; Description: "Abrir o Italophone quando o computador ligar (recomendado)"
Name: "atalho"; Description: "Criar um atalho na área de trabalho"; Flags: unchecked

[Files]
; config.txt e o registro (italophone.log) ficam: atualizar não apaga a arrumação das telas.
Source: "..\dist\Italophone\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#Nome}"; Filename: "{app}\{#Exe}"
Name: "{autodesktop}\{#Nome}"; Filename: "{app}\{#Exe}"; Tasks: atalho

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Italophone"; ValueData: """{app}\{#Exe}"""; Tasks: iniciar; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#Exe}"; Description: "Abrir o Italophone agora"; Flags: postinstall nowait skipifsilent
Filename: "{app}\{#Exe}"; Flags: nowait skipifnotsilent

[UninstallDelete]
Type: files; Name: "{app}\config.txt"
Type: files; Name: "{app}\italophone.log"
Type: files; Name: "{app}\scrcpy-som.txt"
Type: files; Name: "{app}\scrcpy-tela.txt"

[Code]
// Fecha o Italophone e o que ele abriu (som, tela e o adb da pasta dele) antes de trocar os arquivos.
procedure FecharItalophone();
var
  R: Integer;
  Pasta: String;
begin
  Pasta := ExpandConstant('{app}');
  // apostrofo no caminho (ex.: usuario D'Avila) quebraria o texto do PowerShell
  StringChangeEx(Pasta, '''', '''''', True);
  Exec('taskkill.exe', '/IM Italophone.exe /F', '', SW_HIDE, ewWaitUntilTerminated, R);
  Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -Command "Get-Process scrcpy,adb -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith(''' + Pasta + '\'', ''OrdinalIgnoreCase'') } | Stop-Process -Force"',
       '', SW_HIDE, ewWaitUntilTerminated, R);
  Sleep(800);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  FecharItalophone();
  Result := '';
end;

// "Abrir quando o computador ligar" desmarcado numa atualizacao: tira o que a instalacao anterior pos
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('iniciar') then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Italophone');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then FecharItalophone();
end;
