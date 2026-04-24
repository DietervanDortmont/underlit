; Inno Setup 6 script for Underlit.
; Build the app first:
;   dotnet publish src/Underlit/Underlit.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
; Then run Inno Setup Compiler on this .iss.

#define MyAppName "Underlit"
; MyAppVersion is injected by CI on the command line: /DMyAppVersion=x.y.z
; If compiling locally (without /D), fall back to this default.
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#define MyAppPublisher "Underlit"
#define MyAppExeName "Underlit.exe"

[Setup]
AppId={{A4F9E6D2-0001-4F40-8F2B-3EA7D9E8F111}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename=UnderlitSetup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startwithwindows"; Description: "Start {#MyAppName} when I sign in"; GroupDescription: "Auto-start:"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
; {autodesktop} = user's desktop on per-user installs, all-users desktop on admin installs.
; Using {commondesktop} here caused "Access is denied" (0x80070005) on per-user installs
; because writing to C:\Users\Public\Desktop needs admin.
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Underlit"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startwithwindows

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
