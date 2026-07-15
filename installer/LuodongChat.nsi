Unicode True
!include "MUI2.nsh"

!ifndef VERSION
  !error "VERSION is required"
!endif
!ifndef FILE_VERSION
  !error "FILE_VERSION is required"
!endif
!ifndef SOURCE_EXE
  !error "SOURCE_EXE is required"
!endif
!ifndef OUTPUT_FILE
  !error "OUTPUT_FILE is required"
!endif
!ifndef APP_ICON
  !error "APP_ICON is required"
!endif

Name "泺栋chat"
Caption "泺栋chat ${VERSION} 安装程序"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\LuodongChat"
InstallDirRegKey HKCU "Software\LuodongChat" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 64
BrandingText "泺栋chat"
Icon "${APP_ICON}"
UninstallIcon "${APP_ICON}"
ShowInstDetails show
ShowUninstDetails show

VIProductVersion "${FILE_VERSION}"
VIAddVersionKey "ProductName" "泺栋chat"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileDescription" "泺栋chat 安装程序"
VIAddVersionKey "FileVersion" "${FILE_VERSION}"

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\LuodongChat.exe"
!define MUI_FINISHPAGE_RUN_TEXT "启动泺栋chat"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Section "泺栋chat" MainSection
  SetOutPath "$INSTDIR"
  SetOverwrite on
  File /oname=LuodongChat.exe "${SOURCE_EXE}"
  CreateDirectory "$INSTDIR\data"
  CreateDirectory "$INSTDIR\data\logs"
  CreateDirectory "$INSTDIR\data\updates"
  FileOpen $0 "$INSTDIR\.installed" w
  FileWrite $0 "${VERSION}"
  FileClose $0
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  WriteRegStr HKCU "Software\LuodongChat" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LuodongChat" "DisplayName" "泺栋chat"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LuodongChat" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LuodongChat" "DisplayIcon" "$INSTDIR\LuodongChat.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LuodongChat" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LuodongChat" "UninstallString" '$"$INSTDIR\Uninstall.exe$"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LuodongChat" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LuodongChat" "NoRepair" 1
SectionEnd

Section "Uninstall"
  MessageBox MB_YESNO|MB_ICONQUESTION "是否同时删除本机登录状态、日志和更新缓存？" /SD IDYES IDNO keepData
  RMDir /r "$INSTDIR\data"
keepData:
  Delete "$INSTDIR\LuodongChat.exe"
  Delete "$INSTDIR\.installed"
  Delete "$INSTDIR\Uninstall.exe"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LuodongChat"
  DeleteRegKey HKCU "Software\LuodongChat"
  RMDir "$INSTDIR"
SectionEnd
