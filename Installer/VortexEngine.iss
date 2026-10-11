; Vortex Engine Installer Script
; Inno Setup 6.x
;
; This script creates a professional installation wizard for Vortex Engine

#define MyAppName "Vortex Engine"
#define MyAppVersion "3.4.2"
#define MyAppPublisher "Vortex Engine Team"
#define MyAppURL "https://github.com/shadow-kernel/Vortex-Engine"
; The editor is the cross-platform one (the same UI as on macOS and Linux) since v3.0 — self-contained .NET 10, nothing to
; install. The WPF editor ("Vortex Engine (Classic)", .NET Framework 4.8) shipped next to it for one release and was retired
; in v3.1 (#311): [InstallDelete] removes it from older installs.
#define MyAppExeName "Editor\Vortex.Editor.exe"
#define MyAppDataFolder "VortexEngine"

[Setup]
; Unique Application ID
AppId={{8A5D3C2E-1F4B-4E8A-9C6D-7B2A1E3F5D8C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
; Output settings
OutputDir=..\Installer\Output
OutputBaseFilename=VortexEngine-Setup-{#MyAppVersion}
; Compression
Compression=lzma2/ultra64
SolidCompression=yes
; Installer appearance
SetupIconFile=..\Editor\Logo.ico
WizardStyle=modern
WizardSizePercent=120
; Require admin for Program Files installation
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
; Auto-update: AppMutex lets a silent updater (/CLOSEAPPLICATIONS) cleanly close the running editor before
; overwriting files; SetupMutex stops two updater installs racing. AppMutex MUST match the mutex the app holds
; (Vortex.Editor's Program.Main creates "VortexEngineSingleInstance").
AppMutex=VortexEngineSingleInstance
SetupMutex=VortexEngineSetup
; Minimum Windows version (Windows 10)
MinVersion=10.0
; Architecture
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Uninstaller settings
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
; Create uninstaller in app directory
CreateUninstallRegKey=yes
; Allow uninstall to be found in Windows Settings
Uninstallable=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
english.DeleteUserData=Delete user settings and project cache?
english.DeleteUserDataDesc=This will remove all saved preferences and cached data from %1
german.DeleteUserData=Benutzereinstellungen und Projektcache löschen?
german.DeleteUserDataDesc=Dies entfernt alle gespeicherten Einstellungen und Cache-Daten aus %1

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; OnlyBelowVersion: 6.1; Check: not IsAdminInstallMode

[Files]
; The native engine (VortexAPI.dll from the CMake build) and the DLLs it loads (Assimp, optional Streamline/DLSS)
Source: "..\x64\Release\*.dll"; DestDir: "{app}"; Flags: ignoreversion
; Additional resources (if any)
Source: "..\x64\Release\Resources\*"; DestDir: "{app}\Resources"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
; Templates and assets
Source: "..\x64\Release\Templates\*"; DestDir: "{app}\Templates"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
; Engine shaders — REQUIRED for rendering. The native renderer resolves <exe>\Shaders first (installed
; layout) and only falls back to walking up to the repo's Engine\Shaders in dev checkouts. Without this
; an installed editor renders a WHITE viewport (no PSOs compile). Ships .hlsl + any precompiled bin\*.cso.
Source: "..\Engine\Shaders\*"; DestDir: "{app}\Shaders"; Flags: ignoreversion recursesubdirs createallsubdirs
; The editor (#183): self-contained .NET 10 (nothing to install), staged by tools/windows/stage-editor.ps1. It loads
; {app}\VortexAPI.dll and uses {app}\Shaders + {app}\Templates; its Build dialog exports Windows games with the player
; in Editor\player.
Source: "..\x64\Release\Editor\*"; DestDir: "{app}\Editor"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: quicklaunchicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; Silent auto-update relaunch: fires ONLY on a /SILENT or /VERYSILENT install (the in-app updater), so the editor
; comes back after updating. runasoriginaluser relaunches it de-elevated (the installer itself runs elevated).
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser skipifnotsilent

[Registry]
; File associations (optional - for .vortex project files)
Root: HKCR; Subkey: ".vortex"; ValueType: string; ValueName: ""; ValueData: "VortexEngine.Project"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "VortexEngine.Project"; ValueType: string; ValueName: ""; ValueData: "Vortex Engine Project"; Flags: uninsdeletekey
Root: HKCR; Subkey: "VortexEngine.Project\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Flags: uninsdeletekey
Root: HKCR; Subkey: "VortexEngine.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey
; Application settings in registry (for quick uninstall cleanup)
Root: HKCU; Subkey: "Software\{#MyAppPublisher}\{#MyAppName}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\{#MyAppPublisher}\{#MyAppName}"; Flags: uninsdeletekey

[UninstallDelete]
; Delete files created during runtime
Type: files; Name: "{app}\*.log"
Type: files; Name: "{app}\*.tmp"
Type: filesandordirs; Name: "{app}\Logs"
Type: filesandordirs; Name: "{app}\Cache"
Type: filesandordirs; Name: "{app}\Temp"
; Delete user data folder (only if user confirms - handled in code)
Type: dirifempty; Name: "{app}"

[InstallDelete]
; Clean up from previous installations
; the self-contained editor folder is replaced as a whole (no stale runtime files from an older .NET)
Type: filesandordirs; Name: "{app}\Editor"
; #311: the classic WPF editor (v2.x, and v3.0.x next to the new editor) with its .NET Framework assemblies — the new
; layout keeps only native DLLs in {app}, so these patterns cannot hit anything that is still needed
Type: files; Name: "{app}\Vortex Engine.exe"
Type: files; Name: "{app}\Vortex Engine.exe.config"
Type: files; Name: "{app}\Vortex Engine.exe.mda.config"
Type: files; Name: "{app}\Vortex Engine.pdb"
Type: files; Name: "{app}\AvalonDock*.dll"
Type: files; Name: "{app}\Xceed.Wpf.AvalonDock*.dll"
Type: files; Name: "{app}\System.*.dll"
Type: files; Name: "{app}\Microsoft.Bcl.*.dll"
Type: files; Name: "{app}\*.xml"
Type: files; Name: "{app}\Engine.lib"
Type: files; Name: "{group}\{#MyAppName} (Classic).lnk"
Type: files; Name: "{app}\*.log"
Type: filesandordirs; Name: "{app}\Cache"

[Code]
var
  DeleteUserDataCheckbox: TNewCheckBox;

// Nothing to check before setup: the editor is self-contained (.NET 10 inside Editor\), the engine is native.
function InitializeSetup(): Boolean;
begin
  Result := True;
end;

// Custom welcome page message
procedure InitializeWizard();
begin
  WizardForm.WelcomeLabel2.Caption := 
    'This will install Vortex Engine on your computer.'#13#10#13#10 +
    'Vortex Engine is a powerful game engine for creating 2D and 3D games.'#13#10#13#10 +
    'It is recommended that you close all other applications before continuing.';
end;

// Check if directory exists (for file installation checks)
function DirExistsCheck(DirName: String): Boolean;
begin
  Result := DirExists(ExpandConstant(DirName));
end;

// Check if file exists (for file installation checks)
function FileExistsCheck(FileName: String): Boolean;
begin
  Result := FileExists(ExpandConstant(FileName));
end;

// Get user data folder path
function GetUserDataPath(): String;
begin
  Result := ExpandConstant('{localappdata}\{#MyAppDataFolder}');
end;

// Delete a directory recursively
procedure DeleteDirectory(const DirPath: String);
var
  FindRec: TFindRec;
  FilePath: String;
begin
  if FindFirst(DirPath + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          FilePath := DirPath + '\' + FindRec.Name;
          if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
            DeleteDirectory(FilePath)
          else
            DeleteFile(FilePath);
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
  RemoveDir(DirPath);
end;

// Initialize uninstall - add checkbox for user data deletion
procedure InitializeUninstallProgressForm();
var
  UserDataPath: String;
  UninstallPage: TNewNotebookPage;
  StaticText: TNewStaticText;
begin
  UserDataPath := GetUserDataPath();
  
  // Only show option if user data folder exists
  if DirExists(UserDataPath) then
  begin
    UninstallPage := UninstallProgressForm.InnerNotebook.Pages[0];
    
    // Add description text
    StaticText := TNewStaticText.Create(UninstallProgressForm);
    StaticText.Parent := UninstallPage;
    StaticText.Caption := FmtMessage(CustomMessage('DeleteUserDataDesc'), [UserDataPath]);
    StaticText.Left := 0;
    StaticText.Top := UninstallProgressForm.StatusLabel.Top + UninstallProgressForm.StatusLabel.Height + 40;
    StaticText.Width := UninstallPage.Width;
    StaticText.AutoSize := False;
    StaticText.WordWrap := True;
    StaticText.Height := 40;
    
    // Add checkbox
    DeleteUserDataCheckbox := TNewCheckBox.Create(UninstallProgressForm);
    DeleteUserDataCheckbox.Parent := UninstallPage;
    DeleteUserDataCheckbox.Caption := CustomMessage('DeleteUserData');
    DeleteUserDataCheckbox.Left := 0;
    DeleteUserDataCheckbox.Top := StaticText.Top + StaticText.Height + 8;
    DeleteUserDataCheckbox.Width := UninstallPage.Width;
    DeleteUserDataCheckbox.Checked := False;
  end;
end;

// Uninstall step - delete user data if requested
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UserDataPath: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Delete user data if checkbox was checked
    if (DeleteUserDataCheckbox <> nil) and DeleteUserDataCheckbox.Checked then
    begin
      UserDataPath := GetUserDataPath();
      if DirExists(UserDataPath) then
      begin
        DeleteDirectory(UserDataPath);
      end;
    end;
    
    // Clean up registry entries that might remain
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\{#MyAppPublisher}\{#MyAppName}');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\{#MyAppPublisher}');
    
    // Try to delete empty publisher key
    RegDeleteKeyIfEmpty(HKLM, 'Software\{#MyAppPublisher}');
  end;
end;

// Check if uninstaller should proceed
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  
  // Check if application is running
  if CheckForMutexes('VortexEngineSingleInstance') then
  begin
    if MsgBox('Vortex Engine is currently running. Please close it before uninstalling.' + #13#10#13#10 +
              'Would you like to force close the application?', mbConfirmation, MB_YESNO) = IDYES then
    begin
      // Try to close the application
      Exec('taskkill', '/F /IM "Vortex.Editor.exe" /IM "Vortex Engine.exe"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(1000);
    end
    else
    begin
      Result := False;
    end;
  end;
end;
