# Photogrammetry Cloud Job Sync

Windows tray app that signs in with **Trimble ID**, picks a **Connect project**, and downloads finished **Photogrammetry Cloud** job outputs to a local folder.

---

## Where is the code?

| What | Path |
|------|------|
| **Source (this app)** | `H:\SampleApp\PhotogrammetryCloudJobSync\` |
| **Project file** | `PhotogrammetryCloudJobSync.csproj` |
| **Easy start (double-click)** | `Start Photogrammetry Cloud Job Sync.bat` |
| **Runnable app** | `App\PhotogrammetryCloudJobSync.exe` |
| **User settings / tokens** | `%LocalAppData%\PhotogrammetryCloudJobSync\` |

Related folders under `H:\SampleApp\` (reference — **not** this app):

- `Photogrammetry-SampleApp` — Trimble SDK DLLs used by this project  
- `JobOutputBatchDownloader` — older WPF variant  


---

## Quick start

**Easiest:** double-click  
`Start Photogrammetry Cloud Job Sync.bat`  
(builds into `App\` if needed, then launches)

Or:

```bat
cd /d H:\SampleApp\PhotogrammetryCloudJobSync
dotnet build -c Release
App\PhotogrammetryCloudJobSync.exe
```

Requirements: .NET 8 SDK/runtime (Windows), network access to Trimble ID + Connect + Photogrammetry APIs.

---

## How the app works (short)

```text
UI / tray  →  SyncService  →  AuthSession (Trimble ID tokens)
                          →  ConnectCatalog (servers / projects)
                          →  BatchDownloader (datasets → jobs → files)
                                       ↓
                              Local output folder
```

1. User signs in (browser → Trimble ID). Refresh token saved under LocalAppData.  
2. App lists Connect **servers** and **projects**.  
3. On **Sync now** / schedule: Photogrammetry API lists datasets & jobs, downloads missing outputs.  
4. Completed jobs get a `.download_ok` marker so they are skipped next pass.

### Cloud hosts (by Environment dropdown)

| Environment | Photogrammetry API | Connect API |
|-------------|-------------------|-------------|
| Production | `https://cloud.api.trimble.com/photogrammetry/v1/` | `https://app.connect.trimble.com/tc/api/2.0/` |
| QA – Production | `…/photogrammetry/rc/v1/` | same Connect prod |
| QA – Staging | `https://cloud.stage.api.trimblecloud.com/photogrammetry/qa/v1/` | stage Connect |
| Development | `…/photogrammetry/dev/v1/` | stage Connect |

Login URLs / consumer keys / scopes: see `AuthSession.cs` → `ResolveEnvironment`.

---

## Code map (where to look)

| File | Responsibility |
|------|----------------|
| `Program.cs` | App entry, exception handlers, starts tray |
| `TrayAppContext.cs` | System tray menu + NotifyIcon |
| `MainForm.cs` | Settings UI, progress bars, activity log |
| `SyncService.cs` | Background loop: sync → wait → repeat |
| `AuthSession.cs` | Trimble ID login, tokens, env URLs, Photogrammetry client |
| `FileTokenStorage.cs` | Encrypted token files on disk |
| `ConnectCatalog.cs` | List Connect regions + projects |
| `BatchDownloader.cs` | List datasets/jobs/files + download/repair |
| `SyncProgress.cs` | Progress DTO for the UI panel |
| `AppConfig.cs` | Runtime config model |
| `UserSettings.cs` / `ConfigStore` | Load/save `%LocalAppData%\…\user-settings.json` |
| `appsettings.json` | Default config shipped with the build |
| `AppInfo.cs` | Display name + AppData folder name |
| `AppIcon.cs` / `app.ico` / `app-logo.png` | Window + tray branding |

### Dependencies (Trimble SDKs)

Referenced from `..\Photogrammetry-SampleApp\` in the `.csproj`:

- `Trimble.Gcs.Photogrammetry.Sdk`  
- `Trimble.ID` / `Trimble.ID.Desktop`  
- `Trimble.Connect.Client` / `Trimble.Connect.Client.Common`  
- `SampleShared`  

---

## Config & data on disk

| Item | Location |
|------|----------|
| Defaults | `appsettings.json` (next to exe) |
| User overrides | `%LocalAppData%\PhotogrammetryCloudJobSync\user-settings.json` |
| Auth tokens | `%LocalAppData%\PhotogrammetryCloudJobSync\{Prod\|QA\|RC\|Dev}\` |
| Crash log | `%LocalAppData%\PhotogrammetryCloudJobSync\last-error.txt` |
| Downloads | Folder chosen in UI (`Output folder`) |
| Auth alert | `{OutputFolder}\LOGIN_FAILED_ALERT.txt` |

---

## Hand-off notes for another developer

1. Open folder **`PhotogrammetryCloudJobSync`** in your IDE.  
2. Double-click `Start Photogrammetry Cloud Job Sync.bat`, or run `App\PhotogrammetryCloudJobSync.exe`.  
3. **Sign in with your own Trimble ID** (tokens are per Windows user — you do not inherit someone else’s login).  
4. Pick Environment → Refresh → Server → Project → Output folder → Save → Sync now.  
5. Change `OutputRoot` if `F:\…` from defaults does not exist on your machine.

Login is not locked to one account. After login, what you see depends on **your** Connect / Photogrammetry permissions.

---

## Build / packaging tips

- Target: `net8.0-windows` WinForms (`OutputType=WinExe`).  
- Icon: `app.ico` (also copied to output).  
- **Release** builds directly into `App\` (no separate `bin\Release` copy).  
- Give teammates either:
  - the `App\` folder, **or**
  - this source folder so they can `dotnet build -c Release` / use the Start bat.

---

## Suggested reading order for new contributors

1. `Program.cs` → `TrayAppContext.cs` → `MainForm.cs`  
2. `SyncService.cs`  
3. `AuthSession.cs` (login + cloud base URLs)  
4. `ConnectCatalog.cs`  
5. `BatchDownloader.cs` (download pipeline + progress reporting)
