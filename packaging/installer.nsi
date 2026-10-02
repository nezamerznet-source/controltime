Unicode True
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"

!ifndef PAYLOAD
  !define PAYLOAD "..\artifacts\app"
!endif
!ifndef OUTPUT
  !define OUTPUT "..\artifacts\FamilyTime-0.1.2-Setup.exe"
!endif
!ifndef DELETE_LIST
  !define DELETE_LIST "..\artifacts\uninstall-files.nsh"
!endif
!ifndef APP_VERSION
!define APP_VERSION "0.1.3"
!endif

Name "Family Time"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\FamilyTime"
InstallDirRegKey HKCU "Software\FamilyTime" "InstallDir"
RequestExecutionLevel user
ManifestSupportedOS all
SetCompressor /SOLID lzma
SetCompressorDictSize 32
BrandingText "Family Time ${APP_VERSION}"
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey /LANG=1049 "ProductName" "Family Time"
VIAddVersionKey /LANG=1049 "FileDescription" "Установка Family Time"
VIAddVersionKey /LANG=1049 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1049 "LegalCopyright" "Family Time contributors"
!define MUI_ICON "..\src\FamilyTime.Windows\Assets\app.ico"
!define MUI_UNICON "..\src\FamilyTime.Windows\Assets\app.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "Family Time"
!define MUI_WELCOMEPAGE_TEXT "Семейный учёт времени для Windows 11 x64.$\r$\n$\r$\nУстановите приложение из учётной записи Windows ребёнка. Права администратора не нужны.$\r$\n$\r$\nПеред обновлением закройте Chrome и Edge. Family Time завершит учёт и сохранит историю.$\r$\n$\r$\nЭто предварительная сборка для проверки на вашем компьютере."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\FamilyTime.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Открыть Family Time и настроить учёт"
!insertmacro MUI_PAGE_FINISH
!define MUI_UNCONFIRMPAGE_TEXT_TOP "Закройте Chrome и Edge перед удалением. Family Time завершит учёт и удалит программу. Локальную историю можно сохранить; выбор появится после удаления файлов."
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"

Function .onInit
  SetShellVarContext current
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "Нужна Windows 11 x64."
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin11}
    MessageBox MB_OK|MB_ICONSTOP "Эта сборка рассчитана на Windows 11."
    Abort
  ${EndIf}
  SetRegView 64
  ReadRegStr $0 HKCU "Software\FamilyTime" "InstallDir"
  ${If} $0 != ""
    StrCpy $INSTDIR $0
  ${EndIf}
FunctionEnd

Section "Family Time" SecMain
  SetShellVarContext current
  IfFileExists "$INSTDIR\FamilyTime.exe" 0 install_files
    ExecWait '"$INSTDIR\FamilyTime.exe" --shutdown' $0
    ${If} $0 != 0
      MessageBox MB_OK|MB_ICONEXCLAMATION "Закройте Family Time через меню в области уведомлений и повторите установку."
      Abort
    ${EndIf}
  install_files:
  SetOutPath "$INSTDIR"
  File /r "${PAYLOAD}\*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\FamilyTime" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FamilyTime" "DisplayName" "Family Time"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FamilyTime" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FamilyTime" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FamilyTime" "DisplayIcon" "$INSTDIR\FamilyTime.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FamilyTime" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FamilyTime" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FamilyTime" "NoRepair" 1
  CreateShortcut "$SMPROGRAMS\Family Time.lnk" "$INSTDIR\FamilyTime.exe"
  ExecWait '"$INSTDIR\FamilyTime.exe" --register' $0
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONINFORMATION "Связь с браузером не зарегистрирована. Откройте настройки Family Time → Браузер → Восстановить связь."
  ${EndIf}
SectionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
FunctionEnd
Section "Uninstall"
  ExecWait '"$INSTDIR\FamilyTime.exe" --shutdown' $0
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONEXCLAMATION "Закройте Family Time и повторите удаление."
    Abort
  ${EndIf}
  ExecWait '"$INSTDIR\FamilyTime.exe" --unregister' $0
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FamilyTime"
  DeleteRegKey HKCU "Software\FamilyTime"
  Delete "$SMPROGRAMS\Family Time.lnk"
  !include "${DELETE_LIST}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "Удалить локальную историю, настройки и привязку Telegram этой учётной записи? По умолчанию данные сохраняются. Расширение браузера удаляется отдельно." IDNO keep_data
    Delete "$LOCALAPPDATA\FamilyTime\activity.db"
    Delete "$LOCALAPPDATA\FamilyTime\activity.db-wal"
    Delete "$LOCALAPPDATA\FamilyTime\activity.db-shm"
    Delete "$LOCALAPPDATA\FamilyTime\native-host.json"
    RMDir "$LOCALAPPDATA\FamilyTime"
  keep_data:
SectionEnd
