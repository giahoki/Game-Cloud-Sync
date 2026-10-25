; Установщик Game Cloud Sync (Inno Setup 6.6+).
; Сборка: ISCC GameCloudSync.iss  (для ARM64: ISCC /DArch=arm64 GameCloudSync.iss)
; Перед компиляцией программа сама публикуется через dotnet publish -c Release.
; Готовый setup.exe появится в папке Output.

#ifndef Arch
  #define Arch "x64"
#endif

#define AppName      "Game Cloud Sync"
#define AppExe       "GameCloudSync.exe"
#define AppPublisher "giahoki"
#define AppUrl       "https://github.com/giahoki/Game-Cloud-Sync"
#define PublishDir   "bin\Release\net10.0-windows10.0.26100.0\win-" + Arch + "\publish"

; свежая публикация, чтобы в установщик не попала старая сборка
#if Exec(GetEnv("ComSpec"), "/c dotnet publish GameCloudSync.csproj -c Release -p:Platform=" + Arch, SourcePath, 1, SW_HIDE) != 0
  #error dotnet publish завершился с ошибкой — собери проект в Visual Studio и посмотри, что не так
#endif

; версия берётся из exe, то есть из <Version> в GameCloudSync.csproj
#define AppVersion   GetStringFileInfo(PublishDir + "\" + AppExe, "ProductVersion")

#if AppVersion == ""
  #error Не найден GameCloudSync.exe в папке publish
#endif

[Setup]
AppId={{6F1C9B2E-4D7A-4E5B-9C3A-2B8E1F0D7A41}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#GetVersionNumbersString(PublishDir + "\" + AppExe)}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; настройки программы лежат в %AppData% текущего пользователя, поэтому ставим без прав администратора
PrivilegesRequired=lowest
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.17763
WizardStyle=modern dark
SetupIconFile=Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile=LICENSE
OutputDir=Output
OutputBaseFilename=GameCloudSync-{#AppVersion}-{#Arch}-setup
Compression=lzma2/max
SolidCompression=yes
; программа живёт в трее — перед установкой закрываем её
CloseApplications=force
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; закрываем программу (она может сидеть в трее), иначе её файлы не удалятся
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#AppExe}"; Flags: runhidden; RunOnceId: "KillApp"

[Registry]
; запись автозапуска создаёт сама программа — при удалении убираем её
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "GameCloudSync"; Flags: uninsdeletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "SteamSaveSync"; Flags: uninsdeletevalue dontcreatekey

[UninstallDelete]
; всё, что программа создаёт сама: настройки, токен Google Drive, rclone, манифест, локальные копии сейвов, crash.log
Type: filesandordirs; Name: "{userappdata}\GameCloudSync"
Type: filesandordirs; Name: "{userappdata}\SteamSaveSync"
Type: filesandordirs; Name: "{app}"
