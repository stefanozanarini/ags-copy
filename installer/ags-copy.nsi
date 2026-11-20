; AGS-Copy setup (NSIS). Built by build/installer.sh:
;   makensis -DVERSION=x.y.z -DSOURCE=<publish folder> -DOUTFILE=<setup.exe> ags-copy.nsi
; Silent install: AGS-Copy-Setup.exe /S [/D=C:\Path]   Silent removal: uninstall.exe /S

Unicode true
!include "MUI2.nsh"
!include "x64.nsh"

!ifndef VERSION
  !define VERSION "0.0.0"
!endif
!define SERVICE "AGS-Copy"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\AGS-Copy"
!define RUNKEY "Software\Microsoft\Windows\CurrentVersion\Run"

Name "AGS-Copy ${VERSION}"
OutFile "${OUTFILE}"
InstallDir "$PROGRAMFILES64\AGS-Copy"
InstallDirRegKey HKLM "${UNINSTKEY}" "InstallLocation"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
BrandingText "AGS-Copy ${VERSION}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=0 "ProductName" "AGS-Copy"
VIAddVersionKey /LANG=0 "CompanyName" "AGS Media"
VIAddVersionKey /LANG=0 "FileDescription" "AGS-Copy setup"
VIAddVersionKey /LANG=0 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=0 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=0 "LegalCopyright" "AGS Media"

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\AGS-Copy.exe"
!define MUI_FINISHPAGE_RUN_TEXT "$(RunNow)"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Italian"
!insertmacro MUI_LANGUAGE "English"

LangString RunNow ${LANG_ITALIAN} "Apri AGS-Copy"
LangString RunNow ${LANG_ENGLISH} "Open AGS-Copy"
LangString Need64 ${LANG_ITALIAN} "AGS-Copy richiede Windows a 64 bit."
LangString Need64 ${LANG_ENGLISH} "AGS-Copy requires 64-bit Windows."
LangString Stopping ${LANG_ITALIAN} "Arresto del servizio AGS-Copy (i job in corso vengono interrotti)..."
LangString Stopping ${LANG_ENGLISH} "Stopping the AGS-Copy service (running jobs are interrupted)..."
LangString Registering ${LANG_ITALIAN} "Registrazione e avvio del servizio AGS-Copy..."
LangString Registering ${LANG_ENGLISH} "Registering and starting the AGS-Copy service..."
LangString RemoveData ${LANG_ITALIAN} "Eliminare anche job, credenziali salvate, storico e log (cartella $COMMONPROGRAMDATA\AGS-Copy)?$\n$\nScegli No per conservarli in vista di una reinstallazione."
LangString RemoveData ${LANG_ENGLISH} "Also delete jobs, stored credentials, history and logs (folder $COMMONPROGRAMDATA\AGS-Copy)?$\n$\nChoose No to keep them for a reinstall."

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "$(Need64)" /SD IDOK
    Abort
  ${EndIf}
  SetRegView 64
  !insertmacro MUI_LANGDLL_DISPLAY
FunctionEnd

Function un.onInit
  SetRegView 64
  !insertmacro MUI_UNGETLANGUAGE
FunctionEnd

!macro StopEverything
  DetailPrint "$(Stopping)"
  ; Stop-Service waits for the service to stop, so its files are free to overwrite.
  nsExec::Exec 'powershell.exe -NoProfile -NonInteractive -Command "Stop-Service -Name ${SERVICE} -Force -ErrorAction SilentlyContinue"'
  Pop $0
  nsExec::Exec 'taskkill.exe /F /IM AGS-Copy.exe'
  Pop $0
!macroend

Section "AGS-Copy" SecMain
  SectionIn RO
  SetShellVarContext all
  !insertmacro StopEverything

  SetOutPath "$INSTDIR"
  File /r /x install.ps1 /x uninstall.ps1 "${SOURCE}\*.*"
  WriteUninstaller "$INSTDIR\uninstall.exe"

  ; Service: create it on a first install, point it at this folder on an update.
  DetailPrint "$(Registering)"
  nsExec::Exec '"$SYSDIR\sc.exe" create ${SERVICE} binPath= "\"$INSTDIR\AgsCopy.Service.exe\"" start= auto DisplayName= "AGS-Copy"'
  Pop $0
  nsExec::Exec '"$SYSDIR\sc.exe" config ${SERVICE} binPath= "\"$INSTDIR\AgsCopy.Service.exe\"" start= auto'
  Pop $0
  nsExec::Exec '"$SYSDIR\sc.exe" description ${SERVICE} "Scheduled copies and backups with robocopy."'
  Pop $0
  nsExec::Exec '"$SYSDIR\sc.exe" failure ${SERVICE} reset= 86400 actions= restart/60000/restart/60000/restart/60000'
  Pop $0
  nsExec::Exec '"$SYSDIR\sc.exe" start ${SERVICE}'
  Pop $0

  ; Tray icon at every logon, for every user.
  WriteRegStr HKLM "${RUNKEY}" "AGS-Copy" '"$INSTDIR\AGS-Copy.exe" --tray'

  CreateShortcut "$SMPROGRAMS\AGS-Copy.lnk" "$INSTDIR\AGS-Copy.exe"

  ; Apps & features entry.
  WriteRegStr HKLM "${UNINSTKEY}" "DisplayName" "AGS-Copy"
  WriteRegStr HKLM "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNINSTKEY}" "Publisher" "AGS Media"
  WriteRegStr HKLM "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\AGS-Copy.exe"
  WriteRegStr HKLM "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINSTKEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKLM "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoRepair" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "EstimatedSize" 170000

  ; Tray icon now, unelevated, in the session of the user who ran the setup.
  nsExec::Exec 'schtasks.exe /Create /TN "AGS-Copy tray (setup)" /TR "\"$INSTDIR\AGS-Copy.exe\" --tray" /SC ONCE /ST 00:00 /RL LIMITED /IT /F'
  Pop $0
  nsExec::Exec 'schtasks.exe /Run /TN "AGS-Copy tray (setup)"'
  Pop $0
  nsExec::Exec 'schtasks.exe /Delete /TN "AGS-Copy tray (setup)" /F'
  Pop $0
SectionEnd

Section "Uninstall"
  SetShellVarContext all
  !insertmacro StopEverything
  nsExec::Exec '"$SYSDIR\sc.exe" delete ${SERVICE}'
  Pop $0

  DeleteRegValue HKLM "${RUNKEY}" "AGS-Copy"
  Delete "$SMPROGRAMS\AGS-Copy.lnk"
  DeleteRegKey HKLM "${UNINSTKEY}"
  RMDir /r "$INSTDIR"

  ; Jobs and credentials stay unless the user asks (silent removal keeps them).
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "$(RemoveData)" /SD IDNO IDNO keep
    RMDir /r "$COMMONPROGRAMDATA\AGS-Copy"
  keep:
SectionEnd
