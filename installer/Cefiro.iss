; Instalador de Céfiro (Inno Setup 6). Se genera con build\publicar.ps1.
;
; Instala:
;   - La interfaz (Cefiro.exe), que se ejecuta SIN permisos de administrador.
;   - El servicio de Windows "Cefiro" (LocalSystem, inicio automático), que es el que toca el
;     hardware. Si falla, Windows lo reinicia; si se detiene, los ventiladores vuelven a la BIOS.
;   - cefiro-cli.exe, herramienta de diagnóstico por consola.

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\Cefiro"
#endif

#define MyAppName "Céfiro"
#define MyAppExe "Cefiro.exe"
#define MyServiceExe "Cefiro.Service.exe"
#define MyServiceName "Cefiro"

[Setup]
AppId={{6F1C2A0E-5C7B-4C1D-9C3E-0C3F1A0E0001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Céfiro
AppComments=Ventiladores e iluminación RGB, con seguridad para tu líquida.
DefaultDirName={autopf}\Cefiro
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern
SetupIconFile=..\src\Cefiro.App\Assets\cefiro.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
OutputBaseFilename=Cefiro-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=no
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription=Instalador de Céfiro

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "autostart"; Description: "Abrir Céfiro en la bandeja al iniciar sesión (el servicio arranca siempre con Windows)"; GroupDescription: "Inicio:"
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Accesos directos:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Comment: "Ventiladores e iluminación RGB"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Cefiro"; \
    ValueData: """{app}\{#MyAppExe}"" --minimized"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
; La interfaz se abre como el usuario normal, no como administrador.
Filename: "{app}\{#MyAppExe}"; Description: "Abrir Céfiro ahora"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Al detener el servicio, los ventiladores vuelven a la BIOS.
Filename: "{sys}\net.exe"; Parameters: "stop {#MyServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyAppExe}"; Flags: runhidden waituntilterminated; RunOnceId: "CloseApp"

[Code]
var
  RequirementsPage: TOutputMsgMemoWizardPage;

function ServiceExists(const Name: String): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\' + Name);
end;

function RunHidden(const FileName, Params: String): Integer;
var
  Code: Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Result := Code;
end;

function OpenRgbFound(): Boolean;
begin
  Result := ServiceExists('OpenRGB')
    or FileExists(ExpandConstant('{commonpf64}\OpenRGB\OpenRGB.exe'))
    or FileExists(ExpandConstant('{commonpf32}\OpenRGB\OpenRGB.exe'))
    or FileExists(ExpandConstant('{localappdata}\Programs\OpenRGB\OpenRGB.exe'));
end;

function RequirementsText(): String;
var
  S: String;
begin
  S := '';
  if ServiceExists('PawnIO') then
    S := S + '• PawnIO: instalado. Céfiro podrá leer y controlar los ventiladores de la placa.' + #13#10#13#10
  else
    S := S + '• PawnIO: NO está instalado. Sin él no se pueden leer ni controlar los ventiladores de la placa. ' +
             'Descárgalo de https://pawnio.eu, instálalo y reinicia el servicio de Céfiro (o el PC).' + #13#10#13#10;

  if OpenRgbFound() then
    S := S + '• OpenRGB: encontrado. Comprueba que su servidor SDK está activo (puerto 6742) y que se inicia con Windows.' + #13#10#13#10
  else
    S := S + '• OpenRGB: NO encontrado. Las luces se controlan a través de OpenRGB 1.x: instálalo desde https://openrgb.org ' +
             'y activa su servidor SDK. Los ventiladores funcionan sin él.' + #13#10#13#10;

  if ServiceExists('Mystic_Light_Service') or ServiceExists('LEDKeeper2') then
    S := S + '• Aviso: Mystic Light (MSI Center) está instalado y puede cambiar las luces por su cuenta. ' +
             'Lo mejor es desinstalar el módulo Mystic Light de MSI Center.' + #13#10#13#10;
  if ServiceExists('CorsairService') or ServiceExists('CorsairLLAService') then
    S := S + '• Aviso: Corsair iCUE está instalado. Ciérralo (o desinstálalo) para que no se pelee con Céfiro.' + #13#10#13#10;
  if ServiceExists('ArmouryCrateService') or ServiceExists('LightingService') then
    S := S + '• Aviso: ASUS Armoury Crate / Aura está instalado y puede controlar las luces a la vez.' + #13#10#13#10;

  S := S + 'Recuerda poner en la BIOS los conectores de ventilador de la placa en modo PWM (o Auto) y, si lo usas, ' +
           'apagar la iluminación dinámica de Windows (Configuración > Personalización > Iluminación dinámica).';
  Result := S;
end;

procedure InitializeWizard();
begin
  RequirementsPage := CreateOutputMsgMemoPage(wpWelcome,
    'Antes de instalar', 'Lo que Céfiro necesita en este PC',
    'Céfiro funciona sin estas piezas, pero sin ellas no podrá controlar todo:',
    RequirementsText());
end;

{ Antes de copiar: se detiene el servicio (los ventiladores vuelven a la BIOS) para poder reemplazar sus archivos. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if ServiceExists('{#MyServiceName}') then
    RunHidden(ExpandConstant('{sys}\net.exe'), 'stop {#MyServiceName}');
end;

procedure InstallService();
var
  Exe, Sc: String;
begin
  Exe := ExpandConstant('{app}\{#MyServiceExe}');
  Sc := ExpandConstant('{sys}\sc.exe');

  if ServiceExists('{#MyServiceName}') then
    RunHidden(Sc, 'config {#MyServiceName} binPath= "\"' + Exe + '\"" start= auto')
  else
    RunHidden(Sc, 'create {#MyServiceName} binPath= "\"' + Exe + '\"" start= auto DisplayName= "Céfiro"');

  RunHidden(Sc, 'description {#MyServiceName} "Controla los ventiladores y las luces RGB de Céfiro. Si se detiene, los ventiladores vuelven a la BIOS."');
  { Si falla, Windows lo reinicia (5 s, 15 s y 60 s); el contador se reinicia cada día. }
  RunHidden(Sc, 'failure {#MyServiceName} reset= 86400 actions= restart/5000/restart/15000/restart/60000');
  RunHidden(Sc, 'failureflag {#MyServiceName} 1');

  if RunHidden(Sc, 'start {#MyServiceName}') <> 0 then
    MsgBox('El servicio de Céfiro se ha instalado pero no ha podido arrancar. ' +
           'Revisa los registros en %ProgramData%\Cefiro\logs o reinicia el PC.', mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallService();
end;

function InitializeUninstall(): Boolean;
begin
  if UninstallSilent() then
  begin
    Result := True;
    Exit;
  end;

  Result := MsgBox('Se va a desinstalar Céfiro. Los ventiladores volverán a la BIOS y las luces se quedarán como estén. ' +
                   'Tu configuración se conserva en %ProgramData%\Cefiro por si lo vuelves a instalar.' + #13#10#13#10 +
                   '¿Continuar?', mbConfirmation, MB_YESNO) = IDYES;
end;
