; ===========================================================================
;  DotShell — установщик (Inno Setup 6)
;
;  ЗАЧЕМ ЭТО НУЖНО
;  Пока ты разрабатываешь, программа запускается прямо из папки сборки — это
;  нормально. Но чтобы (а) самому не запариваться и (б) дать кому-то ещё,
;  нужен нормальный установщик: ярлык в меню Пуск, запись в "Установка и
;  удаление программ", корректное обновление поверх старой версии и чистое
;  удаление вместе с ключом автозапуска.
;
;  ЧТО ПОСТАВИТЬ
;  Inno Setup 6 — бесплатный: https://jrsoftware.org/isdl.php
;  (при установке можно добавить "Inno Setup Preprocessor", но тут он не нужен)
;
;  КАК СОБРАТЬ (два шага из папки dotshell\)
;
;    1) Опубликовать self-contained single-file сборку:
;         dotnet publish src\DotShell.Island.Wpf\DotShell.Island.Wpf.csproj ^
;           -c Release -r win-x64 --self-contained true ^
;           -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true ^
;           -p:IncludeNativeLibrariesForSelfExtract=true ^
;           -o publish
;
;    2) Скомпилировать этот скрипт:
;         "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\DotShell.iss
;
;  Готовый DotShell-Setup-0.1.0.exe появится в папке installer\Output\
;
;  ВАЖНО ПРО АНТИВИРУС И SMARTSCREEN
;  Установщик не подписан сертификатом, поэтому Windows SmartScreen скажет
;  "Система Windows защитила ваш компьютер". Это происходит с ЛЮБОЙ неподписанной
;  программой. Для себя — "Подробнее" → "Выполнить в любом случае".
;  Чтобы такого не было у других людей, нужен сертификат подписи кода
;  (~100–400 $/год) или публикация через Microsoft Store.
; ===========================================================================

#define MyAppName      "DotShell"
#define MyAppVersion   "0.5.0"
#define MyAppPublisher "DotShell"
#define MyAppExeName   "DotShell.exe"
#define PublishDir     "..\publish"

[Setup]
; AppId однозначно определяет "это та же программа" — без него обновление
; поставится рядом второй копией вместо замены.
AppId={{7C1F4A2E-9B6D-4E33-8A21-6D5E7F0A1B23}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppSupportURL=https://github.com/yourname/dotshell
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; Ставим в Program Files → нужны права администратора.
; Если хочешь установку без UAC, поменяй на:
;   PrivilegesRequired=lowest  и  DefaultDirName={localappdata}\Programs\{#MyAppName}
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

OutputDir=Output
OutputBaseFilename=DotShell-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
SetupIconFile=..\src\DotShell.Island\Assets\dotshell.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription=DotShell — Nothing-шелл для Windows
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian";  MessagesFile: "compiler:Languages\Russian.isl"
Name: "english";  MessagesFile: "compiler:Default.isl"

[Tasks]
; Галочка "Запускать вместе с Windows" прямо в мастере установки.
Name: "autostart"; Description: "Запускать DotShell вместе с Windows"; \
    GroupDescription: "Поведение:"; Flags: unchecked checkedonce
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; \
    GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Single-file сборка: почти всё уже внутри DotShell.exe, но берём всю папку
; publish целиком — если рядом появятся .pdb или отдельные native-библиотеки,
; они тоже попадут в установщик.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Автозапуск. Записываем именно здесь, а не в [Run], чтобы ключ гарантированно
; удалился при деинсталляции — иначе в системе останется битая ссылка.
; Флаг --minimized: при входе в Windows остров не показывает приветственную анимацию.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#MyAppName}"; \
    ValueData: """{app}\{#MyAppExeName}"" --minimized"; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
; Предложить запустить сразу после установки — но БЕЗ --minimized,
; чтобы человек увидел остров и понял, что установка удалась.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; На всякий случай гасим процесс перед удалением файлов.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyAppExeName}"; \
    Flags: runhidden; RunOnceId: "KillDotShell"

[UninstallDelete]
; Пользовательские данные (настройки, заметки, история буфера) НАМЕРЕННО
; не удаляем: человек может переустановить программу и не потерять заметки.
; Если хочешь полное удаление — раскомментируй строку ниже.
; Type: filesandordirs; Name: "{localappdata}\DotShell"
Type: filesandordirs; Name: "{app}"

[Code]
// Перед установкой закрываем уже работающий DotShell, иначе файлы не заменятся.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ' + '{#MyAppExeName}',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

</Code>
