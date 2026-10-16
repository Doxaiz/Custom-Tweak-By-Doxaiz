; ─────────────────────────────────────────────────────────────────────────────────────────────────────
;  Установщик Custom Tweak By Doxaiz.
;
;  Что он делает — и только это:
;    1) кладёт файлы программы и службы в «C:\Program Files\Custom Tweak By Doxaiz»;
;    2) по желанию регистрирует службу PingForge (тем же скриптом install-service.ps1, что и в архиве);
;    3) создаёт ярлыки: в меню «Пуск» и на рабочем столе;
;    4) появляется в списке «Программы и компоненты» с честными кнопками «Удалить»;
;    5) при удалении снимает службу и убирает свои файлы, а данные (%ProgramData%\PingForge —
;       журнал и снапшоты) оставляет, если человек не согласился их удалить.
;
;  Чего установщик не делает никогда: не меняет настройки сети, не трогает реестр Windows ради «ускорения»,
;  не отключает Защитник, брандмауэр, обновления и UAC, не подменяет MAC/IP, не ставит драйверов,
;  не создаёт маршрутов, не собирает телеметрию. В реестр он пишет ровно две вещи: свою запись в списке
;  установленных программ и путь установки — чтобы «Удалить» и переустановка работали.
;
;  Сборка:  makensis -INPUTCHARSET UTF8 -DVERSION=0.2.0 "-DSOURCE_DIR=<полный путь>\dist\portable" "-DOUT_DIR=<полный путь>\dist" installer\CustomTweakByDoxaiz.nsi
;  Пути makensis ищет от папки скрипта, поэтому в сборке передаются полные — иначе он смотрит в installer\.
;  Обычная сборка на Windows (в GitHub Actions) и на Linux (для проверки синтаксиса) — одна и та же.
; ─────────────────────────────────────────────────────────────────────────────────────────────────────

Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 64

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"

; ── что подставляется при сборке ────────────────────────────────────────────────────────────────────
!ifndef VERSION
  !define VERSION "0.0.0"
!endif

!ifndef SOURCE_DIR
  !define SOURCE_DIR "..\dist\portable"
!endif

!ifndef OUT_DIR
  !define OUT_DIR "..\dist"
!endif

; ── имена и пути ────────────────────────────────────────────────────────────────────────────────────
!define APP_NAME "Custom Tweak By Doxaiz"
!define APP_EXE "CustomTweakByDoxaiz.exe"
!define SERVICE_EXE "CustomTweakByDoxaiz.Service.exe"
!define SERVICE_NAME "PingForge"
!define PUBLISHER "Doxaiz"
!define WEBSITE "https://github.com/Doxaiz/Custom-Tweak-By-Doxaiz"
!define REG_UNINSTALL "Software\Microsoft\Windows\CurrentVersion\Uninstall\CustomTweakByDoxaiz"
!define REG_APP "Software\CustomTweakByDoxaiz"
!define DOC_NAME "УСТАНОВКА.md"

Name "${APP_NAME} ${VERSION}"
Caption "${APP_NAME} ${VERSION} — установка"
BrandingText "${APP_NAME} ${VERSION}"
OutFile "${OUT_DIR}\CustomTweakByDoxaiz-Setup-${VERSION}.exe"
InstallDir "$PROGRAMFILES64\${APP_NAME}"
InstallDirRegKey HKLM "${REG_APP}" "InstallDir"
RequestExecutionLevel admin
ShowInstDetails show
ShowUninstDetails show

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1049 "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=1049 "FileDescription" "${APP_NAME} — установщик"
VIAddVersionKey /LANG=1049 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1049 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1049 "CompanyName" "${PUBLISHER}"
VIAddVersionKey /LANG=1049 "LegalCopyright" "MIT, ${PUBLISHER}"

; ── страницы установки ──────────────────────────────────────────────────────────────────────────────
!define MUI_WELCOMEPAGE_TITLE "Установка ${APP_NAME}"
!define MUI_WELCOMEPAGE_TEXT "Программа ставит настройки игры и сети в порядок: уменьшает задержку, $\r$\nровняет кадры, снижает задержку ввода.$\r$\n$\r$\nВажное правило: сразу после установки программа НИЧЕГО не меняет. Любое изменение —$\r$\nтолько после нажатия кнопки в окне и с вашего подтверждения. Перед изменениями$\r$\nпредлагается точка восстановления, а прежние настройки сохраняются в снапшот.$\r$\n$\r$\nСлужба нужна, потому что менять системные настройки можно только с правами администратора,$\r$\nа окно программы должно работать от обычного пользователя."
!define MUI_WELCOMEPAGE_TITLE_3LINES

!define MUI_COMPONENTSPAGE_SMALLDESC
!define MUI_DIRECTORYPAGE_TEXT_TOP "Папка установки. Обычно менять её не нужно."

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES

!define MUI_FINISHPAGE_TITLE "Готово: ${APP_NAME} установлена"
!define MUI_FINISHPAGE_TEXT "Служба установлена и запущена. Программа пока ничего не изменила:$\r$\nэто произойдёт только после нажатия кнопки в окне.$\r$\n$\r$\nЕсли что-то не понравится — «Вернуть как было» в окне программы или пункт «Удалить»$\r$\nв «Программах и компонентах»."
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Запустить ${APP_NAME}"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Russian"

; ── проверки перед началом ──────────────────────────────────────────────────────────────────────────
Function .onInit
  ; Учёт 64-разрядного реестра: SetRegView допустим только внутри функции или секции.
  SetRegView 64

  ; По умолчанию NSIS кладёт ярлыки в меню вошедшего пользователя. Программа ставится
  ; в Program Files и записана в HKLM — значит и ярлыки должны быть общими для всех,
  ; иначе у второго пользователя на машине ярлыка просто не окажется.
  SetShellVarContext all

  ${IfNot} ${RunningX64}
    ; В тихом режиме окошко показывать некому — там просто отказ, без зависшего вопроса.
    ${IfNot} ${Silent}
      MessageBox MB_ICONSTOP "Нужна 64-разрядная Windows 10 или 11: программа работает в 64-разрядном виде."
    ${EndIf}
    Abort
  ${EndIf}

  ; Программу перед установкой закрываем: иначе Windows не даст заменить файлы.
  ; Прыжки — только по меткам: относительные здесь считать нельзя, «+2» от IfSilent
  ; попадает не на ту инструкцию и в тихом режиме установка отменяла сама себя.
  IfSilent on_init_quiet
  MessageBox MB_OKCANCEL|MB_ICONINFORMATION \
    "Установка закроет программу, если она сейчас открыта. Продолжить?" IDOK on_init_quiet
  Abort

on_init_quiet:
FunctionEnd

; ── файлы программы ─────────────────────────────────────────────────────────────────────────────────
Section "Программа (обязательно)" SEC_APP
  SectionIn RO

  SetOutPath "$INSTDIR"
  SetOverwrite on

  DetailPrint "Закрываю программу, если она запущена…"
  nsExec::ExecToLog 'taskkill /IM "${APP_EXE}" /F'

  DetailPrint "Копирую файлы в $INSTDIR"
  File "${SOURCE_DIR}\${APP_EXE}"
  File "${SOURCE_DIR}\${SERVICE_EXE}"
  File "${SOURCE_DIR}\install-service.ps1"
  File "${SOURCE_DIR}\uninstall-service.ps1"

  ; Инструкция кладётся как есть: имя файла русское, и пусть оно таким и остаётся.
  File "/oname=${DOC_NAME}" "${SOURCE_DIR}\${DOC_NAME}"

  ; Запасной вариант на случай компактной сборки: рядом лежат .dll и .json рантайма.
  File /nonfatal "${SOURCE_DIR}\*.dll"
  File /nonfatal "${SOURCE_DIR}\*.json"

  WriteRegStr HKLM "${REG_APP}" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "${REG_APP}" "Version" "${VERSION}"

  ; Запись в «Программах и компонентах»: имя, версия, издатель, значок и то, чем удалять.
  WriteRegStr HKLM "${REG_UNINSTALL}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKLM "${REG_UNINSTALL}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${REG_UNINSTALL}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKLM "${REG_UNINSTALL}" "URLInfoAbout" "${WEBSITE}"
  WriteRegStr HKLM "${REG_UNINSTALL}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${REG_UNINSTALL}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKLM "${REG_UNINSTALL}" "UninstallString" "$\"$INSTDIR\Uninstall.exe$\""
  WriteRegStr HKLM "${REG_UNINSTALL}" "QuietUninstallString" "$\"$INSTDIR\Uninstall.exe$\" /S"
  WriteRegDWORD HKLM "${REG_UNINSTALL}" "NoModify" 1
  WriteRegDWORD HKLM "${REG_UNINSTALL}" "NoRepair" 1

  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${REG_UNINSTALL}" "EstimatedSize" "$0"

  WriteUninstaller "$INSTDIR\Uninstall.exe"
SectionEnd

; ── ярлыки ──────────────────────────────────────────────────────────────────────────────────────────
Section "Ярлык в меню «Пуск»" SEC_START
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\${DOC_NAME}.lnk" "$INSTDIR\${DOC_NAME}"
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\Удалить ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"
SectionEnd

Section "Ярлык на рабочем столе" SEC_DESKTOP
  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
SectionEnd

; ── служба ──────────────────────────────────────────────────────────────────────────────────────────
Section "Служба PingForge (регистрируется и запускается)" SEC_SERVICE
  DetailPrint "Регистрирую службу ${SERVICE_NAME}…"

  ; Тот же скрипт, что лежит в архиве: одна логика на всех, а не две разные.
  nsExec::ExecToLog 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\install-service.ps1" -NoShortcut'
  Pop $0

  ${If} $0 != "0"
    DetailPrint "Служба не зарегистрировалась, код возврата: $0"
    ${IfNot} ${Silent}
      MessageBox MB_ICONEXCLAMATION \
        "Служба не зарегистрировалась (код $0).$\r$\n$\r$\nПрограмма установится и запустится, но менять настройки не сможет: для этого нужна служба.$\r$\nПопробуйте запустить install-service.ps1 из папки установки от имени администратора."
    ${EndIf}
  ${Else}
    DetailPrint "Служба ${SERVICE_NAME} зарегистрирована и запущена."
  ${EndIf}
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_APP} "Программа и служба. Обязательная часть: без файлов ничего не работает."
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_START} "Ярлыки в меню «Пуск»: программа, инструкция и удаление."
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESKTOP} "Ярлык на рабочем столе — для запуска в один щелчок."
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_SERVICE} "Служба PingForge работает от LocalSystem и выполняет команды окна: маршрут, твики, чтение состояния. Без неё программа запустится, но менять настройки не сможет."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ── удаление ────────────────────────────────────────────────────────────────────────────────────────
Function un.onInit
  SetRegView 64
  SetShellVarContext all   ; снимаем ярлыки там же, где ставили: у общих

  IfSilent un_init_quiet
  MessageBox MB_OKCANCEL|MB_ICONQUESTION \
    "Удалить ${APP_NAME}?$\r$\n$\r$\nСлужба ${SERVICE_NAME} будет снята, файлы программы удалены. Журнал и снапшоты в папке данных по умолчанию остаются." \
    IDOK un_init_quiet
  Abort

un_init_quiet:
FunctionEnd

Section "Uninstall"
  SetRegView 64
  SetShellVarContext all

  DetailPrint "Закрываю программу…"
  nsExec::ExecToLog 'taskkill /IM "${APP_EXE}" /F'

  DetailPrint "Снимаю службу ${SERVICE_NAME}…"
  ; -KeepFiles: файлы удалит сам установщик, а данные (журнал и снапшоты) не трогаем.
  nsExec::ExecToLog 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\uninstall-service.ps1" -KeepFiles'
  Pop $0

  DetailPrint "Удаляю файлы программы…"
  Delete "$INSTDIR\${APP_EXE}"
  Delete "$INSTDIR\${SERVICE_EXE}"
  Delete "$INSTDIR\install-service.ps1"
  Delete "$INSTDIR\uninstall-service.ps1"
  Delete "$INSTDIR\${DOC_NAME}"
  Delete "$INSTDIR\*.dll"
  Delete "$INSTDIR\*.json"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"

  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\${DOC_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\Удалить ${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"
  Delete "$DESKTOP\${APP_NAME}.lnk"

  DeleteRegKey HKLM "${REG_UNINSTALL}"
  DeleteRegKey HKLM "${REG_APP}"

  ; Данные — по прямому вопросу. Молча сносить снапшоты нельзя: по ним возвращают прежние настройки.
  ; В тихом режиме (например, при проверке на сборочной машине) данные остаются — и об этом сказано в журнале.
  ${IfNot} ${Silent}
    ReadEnvStr $1 "ProgramData"
    StrCpy $1 "$1\PingForge"
    IfFileExists "$1\*.*" 0 no_data
    MessageBox MB_YESNO|MB_ICONEXCLAMATION|MB_DEFBUTTON2 \
      "Удалить ещё и данные программы?$\r$\n$\r$\nТам журнал изменений, настройки и снапшоты — по ним программа возвращала прежние настройки.$\r$\nЕсли удалить, вернуть настройки средствами программы будет нельзя.$\r$\n$\r$\nПапка: $1" \
      IDNO no_data
    DetailPrint "Удаляю данные: $1"
    RMDir /r "$1"
    Goto data_done

  no_data:
    DetailPrint "Данные оставлены на месте: $1"

  data_done:
  ${EndIf}

  DetailPrint "Готово: программа удалена."
SectionEnd
