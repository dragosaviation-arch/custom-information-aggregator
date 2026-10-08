Unicode true

!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"
!include "nsDialogs.nsh"

!ifndef CIA_VERSION
  !error "CIA_VERSION must be supplied by the installer build."
!endif

!ifndef CIA_PUBLISH_DIR
  !error "CIA_PUBLISH_DIR must point to the self-contained win-x64 publication."
!endif

!ifndef CIA_INSTALLER_OUTPUT
  !error "CIA_INSTALLER_OUTPUT must identify the installer output file."
!endif

!ifndef CIA_MANAGED_DATA_DIRECTORY
  !define CIA_MANAGED_DATA_DIRECTORY "$LOCALAPPDATA\Custom Information Aggregator"
!endif

!ifndef CIA_START_MENU_DIRECTORY
  !define CIA_START_MENU_DIRECTORY "$SMPROGRAMS\Custom Information Aggregator"
!endif

!ifndef CIA_DESKTOP_SHORTCUT
  !define CIA_DESKTOP_SHORTCUT "$DESKTOP\Custom Information Aggregator.lnk"
!endif

!ifndef CIA_CLASSES_KEY
  !define CIA_CLASSES_KEY "Software\Classes"
!endif

!ifndef CIA_UNINSTALL_KEY
  !define CIA_UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Custom Information Aggregator"
!endif

Var RemoveManagedDataCheckbox
Var RemoveManagedData

Name "Custom Information Aggregator ${CIA_VERSION}"
Caption "Custom Information Aggregator Setup"
BrandingText "Custom Information Aggregator"
OutFile "${CIA_INSTALLER_OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\Custom Information Aggregator"
RequestExecutionLevel user
SetCompressor /SOLID lzma
ShowInstDetails nevershow

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\CIA.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Launch CIA"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
UninstPage custom un.ManagedDataPageCreate un.ManagedDataPageLeave
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "English"

Section "CIA application" SecApplication
  SectionIn RO
  SetShellVarContext current
  RMDir /r "$INSTDIR"
  SetOutPath "$INSTDIR"
  File /r "${CIA_PUBLISH_DIR}\*"
  WriteUninstaller "$INSTDIR\Uninstall CIA.exe"

  CreateDirectory "${CIA_START_MENU_DIRECTORY}"
  CreateShortcut "${CIA_START_MENU_DIRECTORY}\Custom Information Aggregator.lnk" "$INSTDIR\CIA.exe"

  SetRegView 64
  WriteRegStr HKCU "${CIA_CLASSES_KEY}\.cia" "" "CIA.WorkingState"
  WriteRegStr HKCU "${CIA_CLASSES_KEY}\CIA.WorkingState" "" "CIA Working State"
  WriteRegStr HKCU "${CIA_CLASSES_KEY}\CIA.WorkingState\DefaultIcon" "" "$\"$INSTDIR\CIA.exe$\",0"
  WriteRegStr HKCU "${CIA_CLASSES_KEY}\CIA.WorkingState\shell\open\command" "" "$\"$INSTDIR\CIA.exe$\" $\"%1$\""

  WriteRegStr HKCU "${CIA_UNINSTALL_KEY}" "DisplayName" "Custom Information Aggregator"
  WriteRegStr HKCU "${CIA_UNINSTALL_KEY}" "DisplayVersion" "${CIA_VERSION}"
  WriteRegStr HKCU "${CIA_UNINSTALL_KEY}" "DisplayIcon" "$\"$INSTDIR\CIA.exe$\",0"
  WriteRegStr HKCU "${CIA_UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${CIA_UNINSTALL_KEY}" "UninstallString" "$\"$INSTDIR\Uninstall CIA.exe$\""
  WriteRegStr HKCU "${CIA_UNINSTALL_KEY}" "QuietUninstallString" "$\"$INSTDIR\Uninstall CIA.exe$\" /S"
  WriteRegDWORD HKCU "${CIA_UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${CIA_UNINSTALL_KEY}" "NoRepair" 1
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
SectionEnd

Section /o "Desktop shortcut" SecDesktopShortcut
  SetShellVarContext current
  CreateShortcut "${CIA_DESKTOP_SHORTCUT}" "$INSTDIR\CIA.exe"
SectionEnd

Function un.onInit
  StrCpy $RemoveManagedData ${BST_UNCHECKED}
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/RemoveManagedData" $1
  ${IfNot} ${Errors}
    StrCpy $RemoveManagedData ${BST_CHECKED}
  ${EndIf}
FunctionEnd

Function un.ManagedDataPageCreate
  !insertmacro MUI_HEADER_TEXT "Application data" "Choose whether CIA-managed application data should also be removed."
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}

  ${NSD_CreateLabel} 0 0 100% 24u "CIA-managed settings, profiles, logs, working data, databases and temporary data are preserved by default."
  Pop $0
  ${NSD_CreateCheckbox} 0 34u 100% 12u "Remove CIA managed application data"
  Pop $RemoveManagedDataCheckbox
  ${If} $RemoveManagedData == ${BST_CHECKED}
    ${NSD_Check} $RemoveManagedDataCheckbox
  ${Else}
    ${NSD_Uncheck} $RemoveManagedDataCheckbox
  ${EndIf}

  nsDialogs::Show
FunctionEnd

Function un.ManagedDataPageLeave
  ${NSD_GetState} $RemoveManagedDataCheckbox $RemoveManagedData
FunctionEnd

Section "Uninstall"
  SetShellVarContext current
  SetRegView 64

  Delete "${CIA_START_MENU_DIRECTORY}\Custom Information Aggregator.lnk"
  RMDir "${CIA_START_MENU_DIRECTORY}"
  Delete "${CIA_DESKTOP_SHORTCUT}"

  ReadRegStr $0 HKCU "${CIA_CLASSES_KEY}\.cia" ""
  ${If} $0 == "CIA.WorkingState"
    DeleteRegKey HKCU "${CIA_CLASSES_KEY}\.cia"
  ${EndIf}
  DeleteRegKey HKCU "${CIA_CLASSES_KEY}\CIA.WorkingState"
  DeleteRegKey HKCU "${CIA_UNINSTALL_KEY}"
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'

  RMDir /r "$INSTDIR"
  ${If} $RemoveManagedData == ${BST_CHECKED}
    RMDir /r "${CIA_MANAGED_DATA_DIRECTORY}"
  ${EndIf}
SectionEnd
