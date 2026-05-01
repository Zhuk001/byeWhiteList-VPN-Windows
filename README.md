# byeWhiteList VPN (Windows)

Open-source WPF VPN client for Windows with Xray (TUN mode), onboarding, routing profiles, and server testing.

## Download
- Releases: https://github.com/Zhuk001/byeWhiteList-VPN-Windows/releases

## Included in v1.0.0
- First-run tutorial overlay.
- Auto-download of `xray.exe` when missing.
- Fallback install path for Xray to `%LocalAppData%\ByeWhiteList\bin` if app folder is not writable.
- Stable VPN stop/exit flow (routes cleanup + xray process stop).
- Updated installer (Inno Setup), custom icons, and admin launch support.

## Build
```powershell
dotnet publish .\ByeWhiteList.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist\publish
```

Installer:
```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" ".\installer\byeWhiteList.iss"
```

## Requirements
- Windows 10/11
- Administrator rights for VPN routing operations (UAC)

## License
See repository license file.
