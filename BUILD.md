# Building Zomboid Manager from source

For end users: download the ready-made EXE from [Releases](https://github.com/Vuscoo/zomboidmanager/releases) instead.

## Requirements

- Windows 11 (x64) recommended
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)

## Setup

1. Clone this repository.
2. Optional: copy `config.example.json` to `%LocalAppData%\ZomboidManager\config.json` and fill in paths / RCON / Discord placeholders.  
   You can also skip this and configure everything in the app **Settings** UI on first launch.
3. Build and run:

```powershell
dotnet build
dotnet run
```

Or open `ZomboidManager.csproj` in Visual Studio and press F5.

> Tip: keep RCON on `127.0.0.1` / your LAN. Do not expose it to the public internet.

## Building a Release EXE

Produces one self-contained `ZomboidManager.exe` (win-x64). The target PC does **not** need the .NET runtime installed. Code signing is a separate step afterward (SignPath, Azure Trusted Signing, etc.).

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "dist\ZomboidManager"
```

Or use the included publish profile:

```powershell
dotnet publish -p:PublishProfile=Release-Win-x64
```

Output: `dist\ZomboidManager\ZomboidManager.exe` — that is the file you sign and upload to GitHub Releases.

## Sensitive data

- Real `config.json` (paths, RCON password, Discord webhook) must **never** be committed.
- Use `config.example.json` as the public template (placeholders only).
- Runtime settings live under `%LocalAppData%\ZomboidManager\`.
