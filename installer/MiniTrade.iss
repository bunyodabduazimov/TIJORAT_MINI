; Установщик TIJORAT LITE (Inno Setup 6). Собирается скриптом build-installer.ps1.
#ifndef AppVersion
  #define AppVersion "1.7.125"
#endif
#define AppName "TIJORAT LITE"
; Прежнее название продукта (до 1.8.1): по нему удаляются старые ярлыки при обновлении.
#define OldAppName "TIJORAT.PRO"
#define AppExe "MiniTrade.exe"

[Setup]
; AppId не менять: по нему обновляются установки, сделанные ещё под старым названием.
AppId={{6E1C2F4A-8B3D-4C5E-9A7F-1D2E3F4A5B6C}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Tijorat
AppPublisherURL=https://tijorat.pro
AppSupportURL=https://tijorat.pro
; Новые установки — в «Tijorat Lite»; при обновлении остаётся прежняя папка (UsePreviousAppDir по умолчанию).
DefaultDirName={autopf}\Tijorat Lite
DefaultGroupName={#AppName}
; Папка в меню «Пуск» — всегда с новым названием, а не прежняя «TIJORAT.PRO».
UsePreviousGroup=no
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=TijoratLite-Setup-{#AppVersion}
SetupIconFile=..\src\MiniTrade.Desktop\Resources\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0
CloseApplications=yes

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; appsettings.json не перезаписывается при обновлении, если пользователь его менял
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "appsettings.json"
Source: "publish\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall

[InstallDelete]
; Ярлыки старого названия: иначе после обновления на рабочем столе и в «Пуске» их будет по два.
Type: files; Name: "{autodesktop}\{#OldAppName}.lnk"
Type: files; Name: "{autoprograms}\{#OldAppName}\{#OldAppName}.lnk"
Type: files; Name: "{autoprograms}\{#OldAppName}\Деинсталлировать {#OldAppName}.lnk"
Type: files; Name: "{autoprograms}\{#OldAppName}\Uninstall {#OldAppName}.lnk"
Type: dirifempty; Name: "{autoprograms}\{#OldAppName}"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
