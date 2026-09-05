Unicode true
RequestExecutionLevel user
SetCompressor /SOLID lzma
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"
!include "StrFunc.nsh"
${Using:StrFunc} StrTrimNewLines
!include "${CONFIG_INCLUDE}"

Name "KnowledgeApp (C#)"
OutFile "${OUTPUT_FILE}"
BrandingText "KnowledgeApp C# ${PRODUCT_VERSION} — 利用者単位インストール"
VIProductVersion "${PRODUCT_VERSION}.0"
VIAddVersionKey "ProductName" "KnowledgeApp (C#)"
VIAddVersionKey "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey "FileDescription" "KnowledgeApp C# installer"
VIAddVersionKey "LegalCopyright" "KnowledgeApp contributors"
InstallDir "${INSTALL_ROOT}"
ShowInstDetails show
ShowUninstDetails show
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Japanese"

Var ActivityHandle
Var RuntimeFound
Var AssetSearch
Var AssetName
Var AssetCharacter
Var AssetIndex
Var AssetValid
Var AssetPath
Var AssetIndexHandle
Var AssetIndexLine
Var AssetOwned

!macro Fail MESSAGE CODE
  IfSilent +2
  MessageBox MB_OK|MB_ICONSTOP "${MESSAGE}"
  SetErrorLevel ${CODE}
  Quit
!macroend

!macro NormalPath PREFIX
Function ${PREFIX}RequireNormalPath
  Exch $8
  Push $9
  Push $7
  ${Do}
    System::Call 'kernel32::GetFileAttributesW(w r8) i.r9'
    ${If} $9 != -1
      IntOp $7 $9 & 0x400
      ${If} $7 != 0
        !insertmacro Fail "インストール先にリンクが含まれるため中止しました。データは変更していません。" 3
      ${EndIf}
    ${EndIf}
    ${GetParent} "$8" $9
    ${If} $9 == $8
      ${ExitDo}
    ${EndIf}
    StrCpy $8 $9
  ${LoopUntil} $8 == ""
  Pop $7
  Pop $9
  Pop $8
FunctionEnd
!macroend
!insertmacro NormalPath ""
!insertmacro NormalPath "un."

!macro AcquireActivity PREFIX
Function ${PREFIX}AcquireActivity
  Push "$INSTDIR\${ACTIVITY_FILE}"
  Call ${PREFIX}RequireNormalPath
  System::Call 'kernel32::CreateFileW(w "$INSTDIR\${ACTIVITY_FILE}", i 0xC0000000, i 0, p 0, i 4, i 0x80, p 0) p.r0'
  StrCpy $ActivityHandle $0
  ${If} $ActivityHandle == -1
    !insertmacro Fail "KnowledgeApp C# が起動中、またはインストール先を使用中です。アプリを閉じてからもう一度実行してください。強制終了は行いません。" 2
  ${EndIf}
  System::Call 'kernel32::GetFileSize(p $ActivityHandle, *i .r1) i.r0'
  ${If} $0 != 0
  ${OrIf} $1 != 0
    !insertmacro Fail "インストール用のロックファイルが不正です。内容は変更していません。" 3
  ${EndIf}
FunctionEnd
!macroend
!insertmacro AcquireActivity ""
!insertmacro AcquireActivity "un."

Function CheckRuntime
!ifdef SYNTHETIC_MISSING_RUNTIME
  !insertmacro Fail "Microsoft .NET 10 Desktop Runtime (x64) と Microsoft Edge WebView2 Runtime が必要です。会社の方針に沿って準備してください。自動ダウンロードは行いません。" 4
!else
  StrCpy $RuntimeFound 0
  SetRegView 32
  StrCpy $0 0
  ${Do}
    EnumRegValue $1 HKLM "SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App" $0
    ${If} $1 == ""
      ${ExitDo}
    ${EndIf}
    StrCpy $2 $1 3
    ${If} $2 == "10."
      StrCpy $RuntimeFound 1
    ${EndIf}
    IntOp $0 $0 + 1
  ${Loop}
  ${If} $RuntimeFound != 1
    !insertmacro Fail "Microsoft .NET 10 Desktop Runtime (x64) が必要です。会社の方針に沿って準備してから再実行してください。自動ダウンロードは行いません。" 4
  ${EndIf}
  ReadRegStr $0 HKLM "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${If} $0 == ""
    ReadRegStr $0 HKCU "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${EndIf}
  ${If} $0 == ""
  ${OrIf} $0 == "0.0.0.0"
    !insertmacro Fail "Microsoft Edge WebView2 Runtime が必要です。会社の方針に沿って準備してから再実行してください。自動ダウンロードは行いません。" 4
  ${EndIf}
!endif
FunctionEnd

Function .onInit
  SetShellVarContext current
  SetRegView 64
  StrCpy $INSTDIR "${INSTALL_ROOT}"
  ${IfNot} ${RunningX64}
    !insertmacro Fail "Windows 64ビット環境が必要です。" 4
  ${EndIf}
  Call CheckRuntime
  SetRegView 64
  Push "$INSTDIR"
  Call RequireNormalPath
  IfFileExists "$INSTDIR\${OWNER_FILE}" owned
  FindFirst $0 $1 "$INSTDIR\*"
  ${DoWhile} $1 != ""
    ${If} $1 != "."
    ${AndIf} $1 != ".."
      FindClose $0
      !insertmacro Fail "指定のインストール先に、このC#版が所有していないファイルがあります。上書きせず中止しました。" 3
    ${EndIf}
    FindNext $0 $1
  ${Loop}
  FindClose $0
  Goto create
owned:
  Push "$INSTDIR\${OWNER_FILE}"
  Call RequireNormalPath
  ClearErrors
  FileOpen $0 "$INSTDIR\${OWNER_FILE}" r
  FileRead $0 $1
  FileClose $0
  ${If} ${Errors}
    !insertmacro Fail "インストール先の所有情報を読み取れません。上書きせず中止しました。" 3
  ${EndIf}
  ${If} $1 != "${OWNER_VALUE}"
    !insertmacro Fail "インストール先の所有情報が一致しません。上書きせず中止しました。" 3
  ${EndIf}
create:
  CreateDirectory "$INSTDIR"
  Call AcquireActivity
  Push "$INSTDIR\${ASSET_INDEX_FILE}"
  Call RequireNormalPath
  Push "$INSTDIR\Uninstall-KnowledgeApp-CSharp.exe"
  Call RequireNormalPath
  Push "${SHORTCUT_PATH}"
  Call RequireNormalPath
  ClearErrors
  FileOpen $0 "$INSTDIR\${OWNER_FILE}" w
  FileWrite $0 "${OWNER_VALUE}"
  FileClose $0
  ${If} ${Errors}
    !insertmacro Fail "インストール先へ所有情報を保存できませんでした。データ保存先は変更していません。" 5
  ${EndIf}
FunctionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  StrCpy $INSTDIR "${INSTALL_ROOT}"
  Push "$INSTDIR\${OWNER_FILE}"
  Call un.RequireNormalPath
  FileOpen $0 "$INSTDIR\${OWNER_FILE}" r
  FileRead $0 $1
  FileClose $0
  ${If} $1 != "${OWNER_VALUE}"
    !insertmacro Fail "アンインストール先を確認できません。ファイルを削除せず中止しました。" 3
  ${EndIf}
  Call un.AcquireActivity
  Push "$INSTDIR\${ASSET_INDEX_FILE}"
  Call un.RequireNormalPath
  Push "${SHORTCUT_PATH}"
  Call un.RequireNormalPath
FunctionEnd

Function ValidateAssets
  Push "$INSTDIR\ui\assets"
  Call RequireNormalPath
  FindFirst $AssetSearch $AssetName "$INSTDIR\ui\assets\*"
  ${DoWhile} $AssetName != ""
    ${If} $AssetName != "."
    ${AndIf} $AssetName != ".."
      StrCpy $AssetPath "$INSTDIR\ui\assets\$AssetName"
      Push "$AssetPath"
      Call RequireNormalPath
      System::Call 'kernel32::GetFileAttributesW(w "$AssetPath") i.r0'
      IntOp $0 $0 & 0x10
      ${If} $0 != 0
        !insertmacro Fail "同梱UIに未確認のフォルダがあります。上書きせず中止しました。" 3
      ${EndIf}
      ${GetFileExt} "$AssetName" $0
      ${If} $0 != "js"
      ${AndIf} $0 != "css"
      ${AndIf} $0 != "woff"
      ${AndIf} $0 != "woff2"
      ${AndIf} $0 != "png"
      ${AndIf} $0 != "jpg"
      ${AndIf} $0 != "jpeg"
      ${AndIf} $0 != "webp"
      ${AndIf} $0 != "gif"
      ${AndIf} $0 != "ico"
        !insertmacro Fail "同梱UIに未確認のファイルがあります。上書きせず中止しました。" 3
      ${EndIf}
      StrCpy $AssetIndex 0
      ${Do}
        StrCpy $AssetCharacter $AssetName 1 $AssetIndex
        ${If} $AssetCharacter == ""
          ${ExitDo}
        ${EndIf}
        StrCpy $AssetValid 0
        StrCpy $1 0
        ${Do}
          StrCpy $2 "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-." 1 $1
          ${If} $2 == ""
            ${ExitDo}
          ${EndIf}
          ${If} $AssetCharacter == $2
            StrCpy $AssetValid 1
          ${EndIf}
          IntOp $1 $1 + 1
        ${Loop}
        ${If} $AssetValid != 1
          !insertmacro Fail "同梱UIのファイル名が不正です。上書きせず中止しました。" 3
        ${EndIf}
        IntOp $AssetIndex $AssetIndex + 1
      ${Loop}
      ; A plausible extension is not proof of installer ownership. Only names
      ; listed by the previous validated payload may be removed on update.
      StrCpy $AssetOwned 0
      ClearErrors
      FileOpen $AssetIndexHandle "$INSTDIR\${ASSET_INDEX_FILE}" r
      ${If} ${Errors}
        !insertmacro Fail "既存UIの所有一覧を確認できません。ファイルを変更せず中止しました。" 3
      ${EndIf}
      ${Do}
        ClearErrors
        FileRead $AssetIndexHandle $AssetIndexLine
        ${If} ${Errors}
          ${ExitDo}
        ${EndIf}
        ${StrTrimNewLines} $AssetIndexLine "$AssetIndexLine"
        ${If} $AssetIndexLine == $AssetName
          StrCpy $AssetOwned 1
          ${ExitDo}
        ${EndIf}
      ${Loop}
      FileClose $AssetIndexHandle
      ${If} $AssetOwned != 1
        !insertmacro Fail "同梱UIに、このインストーラが所有していないファイルがあります。ファイルを変更せず中止しました。" 3
      ${EndIf}
    ${EndIf}
    FindNext $AssetSearch $AssetName
  ${Loop}
  FindClose $AssetSearch
FunctionEnd

Section "KnowledgeApp C#（アプリ本体のみ）"
  !include "${VALIDATE_INCLUDE}"
  Call ValidateAssets
  ; Only the validated flat application-code assets are replaced. No recursive
  ; deletion is used, and no application-data root is referenced by this script.
  FindFirst $AssetSearch $AssetName "$INSTDIR\ui\assets\*"
  ${DoWhile} $AssetName != ""
    ${If} $AssetName != "."
    ${AndIf} $AssetName != ".."
      Delete "$INSTDIR\ui\assets\$AssetName"
    ${EndIf}
    FindNext $AssetSearch $AssetName
  ${Loop}
  FindClose $AssetSearch
  SetOverwrite on
  ClearErrors
  !include "${PAYLOAD_INCLUDE}"
  SetOutPath "$INSTDIR"
  File /oname=${ASSET_INDEX_FILE} "${ASSET_INDEX_SOURCE}"
  ${If} ${Errors}
    !insertmacro Fail "アプリの配置が完了しませんでした。データ保存先は変更していません。アプリを閉じたまま、このインストーラを再実行してください。" 5
  ${EndIf}
  FileOpen $0 "$INSTDIR\${OWNER_FILE}" w
  FileWrite $0 "${OWNER_VALUE}"
  FileClose $0
  WriteUninstaller "$INSTDIR\Uninstall-KnowledgeApp-CSharp.exe"
  CreateShortcut "${SHORTCUT_PATH}" "$INSTDIR\KnowledgeApp.CSharp.exe"
  WriteRegStr HKCU "${PRODUCT_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "KnowledgeApp (C#)"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall-KnowledgeApp-CSharp.exe$\"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall-KnowledgeApp-CSharp.exe$\" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  ${If} ${Errors}
    !insertmacro Fail "ショートカットまたは登録情報の作成に失敗しました。データ保存先は変更していません。再実行してください。" 5
  ${EndIf}
  System::Call 'kernel32::CloseHandle(p $ActivityHandle)'
  SetErrorLevel 0
SectionEnd

Section "Uninstall"
  !include "${UNINSTALL_VALIDATE_INCLUDE}"
  ClearErrors
  !include "${DELETE_INCLUDE}"
  ; Keep ownership and registry information when a managed file cannot be
  ; removed, so retrying uninstall remains possible without force deletion.
  ${If} ${Errors}
    !insertmacro Fail "アプリの一部を削除できませんでした。利用者データは変更していません。使用中のファイルを閉じて再実行してください。" 5
  ${EndIf}
  ; Unknown files remain in place; removing their now-nonempty parent is not an
  ; uninstall failure and never permits recursive deletion.
  !include "${REMOVE_DIRECTORIES_INCLUDE}"
  Delete "$INSTDIR\${ASSET_INDEX_FILE}"
  Delete "$INSTDIR\${OWNER_FILE}"
  Delete "$INSTDIR\Uninstall-KnowledgeApp-CSharp.exe"
  Delete "${SHORTCUT_PATH}"
  DeleteRegKey HKCU "${PRODUCT_KEY}"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  System::Call 'kernel32::CloseHandle(p $ActivityHandle)'
  Delete "$INSTDIR\${ACTIVITY_FILE}"
  RMDir "$INSTDIR"
  SetErrorLevel 0
SectionEnd
