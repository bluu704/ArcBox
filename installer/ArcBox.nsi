; ArcBox installer (per-user, no admin rights needed)
Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma
!include "MUI2.nsh"
!include "x64.nsh"
!include "LogicLib.nsh"

!define APP "ArcBox"
!ifndef VERSION
  !define VERSION "1.0.0"
!endif
!define UNINST "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP}"

Name "${APP}"
OutFile "..\dist\ArcBox-Setup-${VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\${APP}"
InstallDirRegKey HKCU "Software\${APP}" "InstallDir"
RequestExecutionLevel user
BrandingText "${APP} ${VERSION}"
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APP}"
VIAddVersionKey "FileDescription" "${APP} 설치 프로그램"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "ArcBox / 7-Zip engine (c) Igor Pavlov"

!define MUI_ICON "..\assets\app.ico"
!define MUI_UNICON "..\assets\app.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\ArcBox.exe"
!define MUI_FINISHPAGE_RUN_TEXT "ArcBox 실행"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\engine\COPYING.txt"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Korean"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "ArcBox는 64비트 Windows 10/11에서 동작합니다."
    Abort
  ${EndIf}
  SetRegView 64
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  ${If} $0 < 528040
    MessageBox MB_YESNO|MB_ICONEXCLAMATION ".NET Framework 4.8 이상이 필요합니다.$\n다운로드 페이지를 열까요?" IDNO +2
    ExecShell "open" "https://dotnet.microsoft.com/download/dotnet-framework/net48"
    Abort
  ${EndIf}
FunctionEnd

!macro WaitForAppClosed
  retry:
  System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "ArcBoxAppRunning") p .r1'
  ${If} $1 P<> 0
    System::Call 'kernel32::CloseHandle(p r1)'
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "ArcBox가 실행 중입니다. 모든 ArcBox 창을 닫은 뒤 다시 시도를 누르세요." IDRETRY retry
    Abort
  ${EndIf}
!macroend

Section "ArcBox (필수)" SecMain
  SectionIn RO
  !insertmacro WaitForAppClosed
  SetOutPath "$INSTDIR"
  File "..\dist\app\ArcBox.exe"
  File "..\dist\app\ArcBox.exe.config"
  File "..\dist\app\*.dll"
  File "..\dist\app\7-Zip-License.txt"
  File "..\dist\app\THIRD-PARTY-NOTICES.txt"
  File "..\dist\app\COPYING.txt"
  SetOutPath "$INSTDIR\icons"
  File "..\dist\app\icons\*.ico"
  SetOutPath "$INSTDIR"

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\${APP}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINST}" "DisplayName" "${APP}"
  WriteRegStr HKCU "${UNINST}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINST}" "Publisher" "${APP}"
  WriteRegStr HKCU "${UNINST}" "DisplayIcon" "$INSTDIR\ArcBox.exe,0"
  WriteRegStr HKCU "${UNINST}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINST}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINST}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINST}" "EstimatedSize" 6500

  CreateShortcut "$SMPROGRAMS\${APP}.lnk" "$INSTDIR\ArcBox.exe"
  ; file associations, icons and Explorer menus (per user)
  ExecWait '"$INSTDIR\ArcBox.exe" /register'
SectionEnd

Section "바탕 화면 바로 가기" SecDesktop
  CreateShortcut "$DESKTOP\${APP}.lnk" "$INSTDIR\ArcBox.exe"
SectionEnd

LangString DESC_Main ${LANG_KOREAN} "ArcBox 프로그램, 압축 엔진, 파일 아이콘, 탐색기 메뉴"
LangString DESC_Desktop ${LANG_KOREAN} "바탕 화면에 ArcBox 바로 가기를 만듭니다"
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMain} $(DESC_Main)
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} $(DESC_Desktop)
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Section "Uninstall"
  !insertmacro WaitForAppClosed
  ExecWait '"$INSTDIR\ArcBox.exe" /unregister'
  Delete "$INSTDIR\ArcBox.exe"
  Delete "$INSTDIR\ArcBox.exe.config"
  Delete "$INSTDIR\*.dll"
  Delete "$INSTDIR\7-Zip-License.txt"
  Delete "$INSTDIR\THIRD-PARTY-NOTICES.txt"
  Delete "$INSTDIR\COPYING.txt"
  RMDir /r "$INSTDIR\icons"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\${APP}.lnk"
  Delete "$DESKTOP\${APP}.lnk"
  DeleteRegKey HKCU "${UNINST}"
  DeleteRegKey HKCU "Software\${APP}"
  RMDir /r "$TEMP\ArcBox"
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
SectionEnd
