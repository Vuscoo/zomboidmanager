# Zomboid Manager – Publish & Auto-Update

No installer. **Distribute the EXE only** (UI is embedded; settings live in AppData).

Repo: https://github.com/Vuscoo/zomboidmanager  
Settings: `%LocalAppData%\ZomboidManager\config.json`  
UI-Cache: `%LocalAppData%\ZomboidManager\ui\`

---

## Release bauen

```powershell
dotnet publish -c Release -o "dist\ZomboidManager"
```

Optional EXE-only zip:

```powershell
Compress-Archive -Path "dist\ZomboidManager\ZomboidManager.exe" -DestinationPath "dist\ZomboidManager-1.1.0.zip" -Force
```

Ergebnis: eine self-contained `ZomboidManager.exe`. Die reicht zum Verteilen und für Auto-Updates.

---

## Auf GitHub hochladen

1. Version in `ZomboidManager.csproj` setzen (`<Version>1.1.0</Version>`).
2. Publish wie oben.
3. GitHub → **Releases** → **Draft a new release**
   - Tag: `v1.1.0` (muss zur Version passen)
   - Als Asset **`ZomboidManager.exe`** anhängen (nicht den ganzen Ordner)
   - Publish

Oder:

```powershell
gh release create v1.1.0 "dist\ZomboidManager\ZomboidManager.exe" --title "Zomboid Manager 1.1.0" --notes-file CHANGELOG.md --repo Vuscoo/zomboidmanager
```

---

## Nutzer

1. Einmal `ZomboidManager.exe` holen und starten.  
2. Später nur noch **Update** in der App.
