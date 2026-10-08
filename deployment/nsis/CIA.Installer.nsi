Unicode true

!include "MUI2.nsh"

!ifndef CIA_VERSION
  !error "CIA_VERSION must be supplied by the installer build."
!endif

!ifndef CIA_PUBLISH_DIR
  !error "CIA_PUBLISH_DIR must point to the self-contained win-x64 publication."
!endif

!ifndef CIA_INSTALLER_OUTPUT
  !error "CIA_INSTALLER_OUTPUT must identify the installer output file."
!endif

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

!insertmacro MUI_LANGUAGE "English"

Section "CIA application" SecApplication
  SectionIn RO
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  File /r "${CIA_PUBLISH_DIR}\*"

  CreateDirectory "$SMPROGRAMS\Custom Information Aggregator"
  CreateShortcut "$SMPROGRAMS\Custom Information Aggregator\Custom Information Aggregator.lnk" "$INSTDIR\CIA.exe"

  SetRegView 64
  WriteRegStr HKCU "Software\Classes\.cia" "" "CIA.WorkingState"
  WriteRegStr HKCU "Software\Classes\CIA.WorkingState" "" "CIA Working State"
  WriteRegStr HKCU "Software\Classes\CIA.WorkingState\DefaultIcon" "" "$\"$INSTDIR\CIA.exe$\",0"
  WriteRegStr HKCU "Software\Classes\CIA.WorkingState\shell\open\command" "" "$\"$INSTDIR\CIA.exe$\" $\"%1$\""
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
SectionEnd

Section /o "Desktop shortcut" SecDesktopShortcut
  SetShellVarContext current
  CreateShortcut "$DESKTOP\Custom Information Aggregator.lnk" "$INSTDIR\CIA.exe"
SectionEnd
