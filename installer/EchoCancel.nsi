; EchoCancel — one-click installer (English / Türkçe)
; Build: makensis -INPUTCHARSET UTF8 -DPAYLOAD=<publish dir> -DVERSION=x.y.z -DOUTFILE=<setup.exe> EchoCancel.nsi
Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma
SetCompressorDictSize 64

!ifndef VERSION
  !define VERSION "0.3.0"
!endif
!ifndef PAYLOAD
  !error "PAYLOAD is not defined"
!endif
!define APP "EchoCancel"
!define EXE "EchoCancel.exe"
!define UNKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\EchoCancel"
!define OLDKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\YankiKes"
!define RUNKEY "Software\Microsoft\Windows\CurrentVersion\Run"
!define REPO "https://github.com/tolgayalkan/EchoCancel-for-Discord"

Name "${APP}"
OutFile "${OUTFILE}"
InstallDir "$PROGRAMFILES64\EchoCancel"
RequestExecutionLevel admin
BrandingText "${APP} ${VERSION}"
ShowInstDetails show
ShowUninstDetails show

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1033 "ProductName" "${APP}"
VIAddVersionKey /LANG=1033 "FileDescription" "${APP} Setup"
VIAddVersionKey /LANG=1033 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "MIT License"
VIAddVersionKey /LANG=1033 "Comments" "${REPO}"

!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"
!include "WinVer.nsh"

!define MUI_ICON "app.ico"
!define MUI_UNICON "app.ico"
!define MUI_ABORTWARNING

!define MUI_WELCOMEPAGE_TITLE "$(WelcomeTitle)"
!define MUI_WELCOMEPAGE_TEXT "$(WelcomeText)"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TITLE "$(FinishTitle)"
!define MUI_FINISHPAGE_TEXT "$(FinishText)"
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "$(RunText)"
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchApp
!define MUI_FINISHPAGE_SHOWREADME
!define MUI_FINISHPAGE_SHOWREADME_TEXT "$(AutostartText)"
!define MUI_FINISHPAGE_SHOWREADME_FUNCTION EnableAutostart
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

; İlk eklenen dil varsayılan olur; Windows dili Türkçe ise Türkçe seçilir.
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Turkish"

LangString WelcomeTitle ${LANG_ENGLISH} "${APP} Setup"
LangString WelcomeTitle ${LANG_TURKISH} "${APP} kurulumu"
LangString WelcomeText ${LANG_ENGLISH} "${APP} keeps your speakers on while sending only your voice to Discord — game, video and other people's voices from your speakers are removed from your microphone.$\r$\n$\r$\nSetup will:$\r$\n$\r$\n•  Install ${APP} (nothing else is needed)$\r$\n•  Download and install the free VB-CABLE virtual audio cable if it is missing. If Windows asks to install a driver, click $\"Install$\".$\r$\n•  Add shortcuts to your desktop and Start menu$\r$\n$\r$\nInternet is only needed for VB-CABLE. Click Install to continue."
LangString WelcomeText ${LANG_TURKISH} "${APP}, hoparlörün açıkken Discord'a yalnızca senin sesini gönderir — hoparlörden gelen oyun, video ve diğer kişilerin sesleri mikrofonundan temizlenir.$\r$\n$\r$\nKurulum şunları yapacak:$\r$\n$\r$\n•  ${APP}'i bilgisayarına kurar (başka bir program gerekmez)$\r$\n•  Ücretsiz VB-CABLE sanal ses kablosu yoksa indirip kurar. Windows bir sürücü onayı sorarsa $\"Yükle$\"ye bas.$\r$\n•  Masaüstüne ve Başlat menüsüne kısayol ekler$\r$\n$\r$\nİnternet yalnızca VB-CABLE için gerekir. Devam etmek için Kur'a bas."
LangString FinishTitle ${LANG_ENGLISH} "${APP} is ready"
LangString FinishTitle ${LANG_TURKISH} "${APP} hazır"
LangString FinishText ${LANG_ENGLISH} "One last step (only once):$\r$\n$\r$\nDiscord → User Settings → Voice & Video$\r$\n•  Input Device: CABLE Output (VB-Audio Virtual Cable)$\r$\n•  Echo Cancellation: off$\r$\n$\r$\n${APP} lives in the tray next to the clock — click the microphone icon to open it."
LangString FinishText ${LANG_TURKISH} "Son bir adım (yalnızca bir kez):$\r$\n$\r$\nDiscord → Kullanıcı Ayarları → Ses ve Görüntü$\r$\n•  Giriş Aygıtı: CABLE Output (VB-Audio Virtual Cable)$\r$\n•  Yankı Engelleme: kapalı$\r$\n$\r$\n${APP} saatin yanındaki mikrofon simgesinde çalışır; simgeye tıklayınca açılır."
LangString RunText ${LANG_ENGLISH} "Start ${APP} now"
LangString RunText ${LANG_TURKISH} "${APP}'i şimdi başlat"
LangString AutostartText ${LANG_ENGLISH} "Start in the tray when Windows starts"
LangString AutostartText ${LANG_TURKISH} "Windows açılınca tepside başlat"
LangString Need64 ${LANG_ENGLISH} "${APP} requires 64-bit Windows 10 or 11."
LangString Need64 ${LANG_TURKISH} "${APP} 64 bit Windows 10 ya da 11 gerektirir."
LangString Closing ${LANG_ENGLISH} "Closing running ${APP}…"
LangString Closing ${LANG_TURKISH} "Çalışan ${APP} kapatılıyor…"
LangString OldRemove ${LANG_ENGLISH} "Removing the old version (YankıKes)…"
LangString OldRemove ${LANG_TURKISH} "Eski sürüm (YankıKes) kaldırılıyor…"
LangString Copying ${LANG_ENGLISH} "Copying files…"
LangString Copying ${LANG_TURKISH} "Dosyalar kopyalanıyor…"
LangString Shortcuts ${LANG_ENGLISH} "Creating shortcuts…"
LangString Shortcuts ${LANG_TURKISH} "Kısayollar oluşturuluyor…"
LangString CableCheck ${LANG_ENGLISH} "Checking VB-CABLE virtual audio cable (downloads and installs it if missing, ~1 min)…"
LangString CableCheck ${LANG_TURKISH} "VB-CABLE sanal ses kablosu kontrol ediliyor (yoksa indirilip kurulur, ~1 dk)…"
LangString CableDriver ${LANG_ENGLISH} "If Windows asks to install a driver, click 'Install'."
LangString CableDriver ${LANG_TURKISH} "Windows bir sürücü onayı sorarsa 'Yükle'ye bas."
LangString CableOk ${LANG_ENGLISH} "VB-CABLE is ready."
LangString CableOk ${LANG_TURKISH} "VB-CABLE hazır."
LangString CableFail ${LANG_ENGLISH} "VB-CABLE could not be installed (internet connection or driver approval).$\r$\n$\r$\nYou can retry with one click from ${APP} → Settings → Setup check."
LangString CableFail ${LANG_TURKISH} "VB-CABLE kurulamadı (internet bağlantısı ya da sürücü onayı).$\r$\n$\r$\n${APP} → Ayarlar → Kurulum kontrolü ekranından tek tıkla tekrar deneyebilirsin."
LangString UnDone ${LANG_ENGLISH} "${APP} was removed.$\r$\n$\r$\nThe VB-CABLE driver stays installed. Remember to set Discord's Input Device back to your microphone."
LangString UnDone ${LANG_TURKISH} "${APP} kaldırıldı.$\r$\n$\r$\nVB-CABLE sürücüsü kurulu kalır. Discord'da Giriş Aygıtı'nı mikrofonuna geri almayı unutma."

Function .onInit
  ${IfNot} ${RunningX64}
  ${OrIfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP "$(Need64)"
    Abort
  ${EndIf}
  SetRegView 64
FunctionEnd

Function LaunchApp
  ; Yönetici olarak değil, normal kullanıcı olarak başlat (explorer üzerinden).
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\${EXE}"'
FunctionEnd

Function EnableAutostart
  WriteRegStr HKCU "${RUNKEY}" "EchoCancel" '"$INSTDIR\${EXE}" --tray'
FunctionEnd

Section "EchoCancel" SecMain
  SectionIn RO
  SetShellVarContext current

  DetailPrint "$(Closing)"
  nsExec::Exec 'taskkill /F /IM ${EXE}'
  Pop $0
  nsExec::Exec 'taskkill /F /IM YankiKes.exe'
  Pop $0
  Sleep 800

  ; Eski adla (YankıKes) kurulmuş sürüm varsa kaldır; ayarlar uygulama tarafından taşınır.
  ReadRegStr $1 HKLM "${OLDKEY}" "InstallLocation"
  ${If} $1 != ""
    DetailPrint "$(OldRemove)"
    RMDir /r "$1"
    DeleteRegKey HKLM "${OLDKEY}"
    Delete "$DESKTOP\YankıKes.lnk"
    Delete "$SMPROGRAMS\YankıKes.lnk"
  ${EndIf}
  ReadRegStr $2 HKCU "${RUNKEY}" "YankiKes"
  DeleteRegValue HKCU "${RUNKEY}" "YankiKes"

  DetailPrint "$(Copying)"
  RMDir /r "$INSTDIR"
  SetOutPath "$INSTDIR"
  File /r "${PAYLOAD}\*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  DetailPrint "$(Shortcuts)"
  CreateShortCut "$DESKTOP\${APP}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0
  CreateShortCut "$SMPROGRAMS\${APP}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0
  ; Önceden Windows ile başlıyorduysa öyle kalsın
  ReadRegStr $1 HKCU "${RUNKEY}" "EchoCancel"
  ${If} $1 != ""
  ${OrIf} $2 != ""
    WriteRegStr HKCU "${RUNKEY}" "EchoCancel" '"$INSTDIR\${EXE}" --tray'
  ${EndIf}

  WriteRegStr HKLM "${UNKEY}" "DisplayName" "${APP}"
  WriteRegStr HKLM "${UNKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNKEY}" "Publisher" "${APP}"
  WriteRegStr HKLM "${UNKEY}" "URLInfoAbout" "${REPO}"
  WriteRegStr HKLM "${UNKEY}" "DisplayIcon" "$INSTDIR\${EXE}"
  WriteRegStr HKLM "${UNKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKLM "${UNKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNKEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${UNKEY}" "EstimatedSize" "$0"

  DetailPrint "$(CableCheck)"
  DetailPrint "$(CableDriver)"
  ExecWait '"$INSTDIR\${EXE}" --ensure-cable' $0
  ${If} $0 == 0
    DetailPrint "$(CableOk)"
  ${Else}
    DetailPrint "VB-CABLE: $0"
    MessageBox MB_ICONEXCLAMATION|MB_OK "$(CableFail)" /SD IDOK
  ${EndIf}

  IfSilent 0 +2
    Exec '"$WINDIR\explorer.exe" "$INSTDIR\${EXE}"'
SectionEnd

Function un.onInit
  SetRegView 64
FunctionEnd

Section "Uninstall"
  SetShellVarContext current
  nsExec::Exec 'taskkill /F /IM ${EXE}'
  Pop $0
  Sleep 800
  Delete "$DESKTOP\${APP}.lnk"
  Delete "$SMPROGRAMS\${APP}.lnk"
  DeleteRegValue HKCU "${RUNKEY}" "EchoCancel"
  DeleteRegKey HKLM "${UNKEY}"
  RMDir /r "$INSTDIR"
  ; Ayarlar ve günlük
  RMDir /r "$APPDATA\EchoCancel"
  RMDir /r "$APPDATA\YankiKes"
  MessageBox MB_ICONINFORMATION|MB_OK "$(UnDone)" /SD IDOK
SectionEnd
