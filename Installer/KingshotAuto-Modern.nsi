; KingshotAuto Modern NSIS Installer Script
!define APPNAME "KingshotAuto"
!define COMPANYNAME "KingshotAuto Team"
!define DESCRIPTION "Professional Game Automation Tool"
; Version will be replaced by PowerShell script
!define VERSION "${VERSION_PLACEHOLDER}"

; TODO: replace OWNER with the project's GitHub org/user before publishing.
!define HELPURL "https://github.com/KingshotAuto/Kingshot-bot/issues"
!define UPDATEURL "https://github.com/KingshotAuto/Kingshot-bot/releases"
!define ABOUTURL "https://github.com/KingshotAuto/Kingshot-bot"

!define INSTALLSIZE 150000

; Modern UI
!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"

; Configuration
RequestExecutionLevel admin
InstallDir "$PROGRAMFILES\${APPNAME}"
Name "${APPNAME} ${VERSION}"
OutFile "dist\Installers\KingshotAuto-Setup-v${VERSION}.exe"
BrandingText "${COMPANYNAME}"

; Interface Settings
!define MUI_ABORTWARNING
!define MUI_ICON "${NSISDIR}\Contrib\Graphics\Icons\modern-install.ico"
!define MUI_UNICON "${NSISDIR}\Contrib\Graphics\Icons\modern-uninstall.ico"

; Welcome page
!define MUI_WELCOMEPAGE_TITLE "Welcome to ${APPNAME} Setup"
!define MUI_WELCOMEPAGE_TEXT "This wizard will guide you through the installation of ${APPNAME}.$\r$\n$\r$\n${APPNAME} is a professional game automation tool that helps you manage your gaming experience efficiently.$\r$\n$\r$\nClick Next to continue."

; License page
!define MUI_LICENSEPAGE_TEXT_TOP "Please review the license terms before installing ${APPNAME}."
!define MUI_LICENSEPAGE_TEXT_BOTTOM "If you accept the terms of the agreement, click I Agree to continue. You must accept the agreement to install ${APPNAME}."

; Directory page
!define MUI_DIRECTORYPAGE_TEXT_TOP "Setup will install ${APPNAME} in the following folder. To install in a different folder, click Browse and select another folder."

; Install page
!define MUI_INSTFILESPAGE_FINISHHEADER_TEXT "Installation Complete"
!define MUI_INSTFILESPAGE_FINISHHEADER_SUBTEXT "Setup has finished installing ${APPNAME} on your computer."

; Finish page
!define MUI_FINISHPAGE_TITLE "Completing ${APPNAME} Setup"
!define MUI_FINISHPAGE_TEXT "${APPNAME} has been installed on your computer.$\r$\n$\r$\nClick Finish to close this wizard."
!define MUI_FINISHPAGE_RUN "$INSTDIR\KingshotAuto.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Run ${APPNAME}"
!define MUI_FINISHPAGE_LINK "Visit the project on GitHub"
!define MUI_FINISHPAGE_LINK_LOCATION "${HELPURL}"

; Uninstaller
!define MUI_UNCONFIRMPAGE_TEXT_TOP "This wizard will uninstall ${APPNAME} from your computer."

; Pages
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "Installer\License.txt"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

; Languages
!insertmacro MUI_LANGUAGE "English"

; Version Information
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APPNAME}"
VIAddVersionKey "CompanyName" "${COMPANYNAME}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "FileDescription" "${DESCRIPTION}"
VIAddVersionKey "LegalCopyright" "Copyright © 2025 ${COMPANYNAME}"

; Installation section
Section "!${APPNAME}" SecMain
    SectionIn RO
    
    ; Set output path to the installation directory
    SetOutPath $INSTDIR
    
    ; Show installation progress
    DetailPrint "Installing ${APPNAME} files..."
    
    ; Install main executable
    File "dist\KingshotAuto-v${VERSION}-win-x64\KingshotAuto.exe"
    
    ; Install Visual C++ Redistributable
    File "tools\VC_redist.x64.exe"
    
    ; Install DLL files
    File "dist\KingshotAuto-v${VERSION}-win-x64\*.dll"
    
    ; Install configuration files
    File "dist\KingshotAuto-v${VERSION}-win-x64\*.json"
    File /nonfatal "dist\KingshotAuto-v${VERSION}-win-x64\*.manifest"
    File /nonfatal "dist\KingshotAuto-v${VERSION}-win-x64\*.exe"
    File /nonfatal "dist\KingshotAuto-v${VERSION}-win-x64\*.txt"
    
    ; Install directories
    DetailPrint "Installing application data..."
    
    SetOutPath $INSTDIR\configs
    File /nonfatal /r "dist\KingshotAuto-v${VERSION}-win-x64\configs\*"
    
    SetOutPath $INSTDIR\templates
    File /r "dist\KingshotAuto-v${VERSION}-win-x64\templates\*"
    
    SetOutPath $INSTDIR\tessdata
    File /r "dist\KingshotAuto-v${VERSION}-win-x64\tessdata\*"
    
    SetOutPath $INSTDIR\x64
    File /r "dist\KingshotAuto-v${VERSION}-win-x64\x64\*"

    ; x86 folder removed for package size optimization (app is x64 only)
    
    ; Install language resources (optional - may not exist in all builds)
    DetailPrint "Installing language resources..."
    
    SetOutPath $INSTDIR\cs
    File /nonfatal /r "dist\KingshotAuto-v${VERSION}-win-x64\cs\*"
    
    SetOutPath $INSTDIR\de
    File /nonfatal /r "dist\KingshotAuto-v${VERSION}-win-x64\de\*"
    
    SetOutPath $INSTDIR\es
    File /nonfatal /r "dist\KingshotAuto-v${VERSION}-win-x64\es\*"
    
    SetOutPath $INSTDIR\fr
    File /nonfatal /r "dist\KingshotAuto-v${VERSION}-win-x64\fr\*"
    
    SetOutPath $INSTDIR\it
    File /nonfatal /r "dist\KingshotAuto-v${VERSION}-win-x64\it\*"
    
    SetOutPath $INSTDIR\ja
    File /nonfatal /r "dist\KingshotAuto-v${VERSION}-win-x64\ja\*"
    
    SetOutPath $INSTDIR\ko
    File /nonfatal /r "dist\KingshotAuto-v${VERSION}-win-x64\ko\*"
    
    SetOutPath $INSTDIR\pl
    File /nonfatal /r "dist\KingshotAuto-v${VERSION}-win-x64\pl\*"
    
    SetOutPath $INSTDIR\pt-BR
    File /nonfatal /r "dist\KingshotAuto-v${VERSION}-win-x64\pt-BR\*"
    
    SetOutPath $INSTDIR\ru
    File /nonfatal /r "dist\KingshotAuto-v${VERSION}-win-x64\ru\*"
    
    SetOutPath $INSTDIR\tr
    File /nonfatal /r "dist\KingshotAuto-v${VERSION}-win-x64\tr\*"
    
    SetOutPath $INSTDIR\zh-Hans
    File /nonfatal /r "dist\KingshotAuto-v${VERSION}-win-x64\zh-Hans\*"
    
    SetOutPath $INSTDIR\zh-Hant
    File /nonfatal /r "dist\KingshotAuto-v${VERSION}-win-x64\zh-Hant\*"
    
    ; Back to main directory
    SetOutPath $INSTDIR
    
    ; Install Visual C++ Redistributable if needed
    DetailPrint "Installing Visual C++ Redistributable..."
    ExecWait '"$INSTDIR\VC_redist.x64.exe" /install /quiet /norestart' $0
    ${If} $0 != 0
        ${AndIf} $0 != 1638  ; 1638 = already installed
        DetailPrint "Visual C++ Redistributable installation returned code: $0"
    ${EndIf}
    
    ; Remove the redistributable installer after use
    Delete "$INSTDIR\VC_redist.x64.exe"
    
    ; Create shortcuts
    DetailPrint "Creating shortcuts..."
    
    CreateDirectory "$SMPROGRAMS\${APPNAME}"
    CreateShortcut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\KingshotAuto.exe" "" "$INSTDIR\KingshotAuto.exe" 0
    CreateShortcut "$SMPROGRAMS\${APPNAME}\Uninstall ${APPNAME}.lnk" "$INSTDIR\uninstall.exe" "" "$INSTDIR\uninstall.exe" 0
    CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\KingshotAuto.exe" "" "$INSTDIR\KingshotAuto.exe" 0
    
    ; Create uninstaller
    DetailPrint "Creating uninstaller..."
    WriteUninstaller "$INSTDIR\uninstall.exe"
    
    ; Registry entries for Add/Remove Programs
    DetailPrint "Registering application..."
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayName" "${APPNAME}"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayVersion" "${VERSION}"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "Publisher" "${COMPANYNAME}"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "UninstallString" "$INSTDIR\uninstall.exe"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "QuietUninstallString" "$INSTDIR\uninstall.exe /S"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayIcon" "$INSTDIR\KingshotAuto.exe"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "URLInfoAbout" "${ABOUTURL}"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "HelpLink" "${HELPURL}"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "URLUpdateInfo" "${UPDATEURL}"
    WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "NoModify" 1
    WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "NoRepair" 1
    WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "EstimatedSize" ${INSTALLSIZE}
    
    ; Get installed size
    ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
    IntFmt $0 "0x%08X" $0
    WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "EstimatedSize" "$0"
    
SectionEnd

; Uninstaller section
Section "Uninstall"
    
    ; Remove shortcuts
    Delete "$DESKTOP\${APPNAME}.lnk"
    Delete "$SMPROGRAMS\${APPNAME}\*.*"
    RMDir "$SMPROGRAMS\${APPNAME}"
    
    ; Remove files and directories
    RMDir /r "$INSTDIR\configs"
    RMDir /r "$INSTDIR\templates"
    RMDir /r "$INSTDIR\tessdata"
    RMDir /r "$INSTDIR\x64"
    ; x86 no longer included in package
    
    ; Remove language resource directories (may not exist in all installations)
    RMDir /r "$INSTDIR\cs"
    RMDir /r "$INSTDIR\de"
    RMDir /r "$INSTDIR\es"
    RMDir /r "$INSTDIR\fr"
    RMDir /r "$INSTDIR\it"
    RMDir /r "$INSTDIR\ja"
    RMDir /r "$INSTDIR\ko"
    RMDir /r "$INSTDIR\pl"
    RMDir /r "$INSTDIR\pt-BR"
    RMDir /r "$INSTDIR\ru"
    RMDir /r "$INSTDIR\tr"
    RMDir /r "$INSTDIR\zh-Hans"
    RMDir /r "$INSTDIR\zh-Hant"
    
    ; Remove main files
    Delete "$INSTDIR\*.*"
    
    ; Remove main directory
    RMDir "$INSTDIR"
    
    ; Remove registry entries
    DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"
    
SectionEnd

; Modern UI customizations
Function .onInit
    ; Check if already installed
    ReadRegStr $R0 HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "UninstallString"
    StrCmp $R0 "" done
    
    MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION "${APPNAME} is already installed. $\n$\nClick OK to remove the previous version or Cancel to cancel this upgrade." IDOK uninst
    Abort
    
    uninst:
        ClearErrors
        ExecWait '$R0 _?=$INSTDIR'
        
        IfErrors no_remove_uninstaller done
        no_remove_uninstaller:
    
    done:
FunctionEnd