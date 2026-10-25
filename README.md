<div align="center">

<img src="logo.png" alt="Game Cloud Sync" width="140">

# Game Cloud Sync

**Automatic backups of your Steam and GOG saves to your own Google Drive.**

[![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows&logoColor=white)](#requirements)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download)
[![WinUI 3](https://img.shields.io/badge/UI-WinUI%203-0F6CBD)](https://learn.microsoft.com/windows/apps/winui/winui3/)
[![License: MIT](https://img.shields.io/badge/license-MIT-2ea44f)](LICENSE)

[Features](#-features) •
[Getting started](#-getting-started) •
[Privacy](#-privacy) •
[Building](#-building-from-source) •
[Contributing](#-contributing)

**English** | [Русский](README.ru.md)

</div>

---

I used Cloud Redirect to keep my saves safe until it stopped working, so I wrote a replacement. Game Cloud Sync finds your installed games, works out where each one keeps its saves, and uploads them to Google Drive every time you close a game. No account, no third-party server: files go straight from your PC to your Drive.

## ✨ Features

| | |
|---|---|
| 🎮 **Game detection** | Reads every Steam library and every installed GOG game. |
| 🔍 **Save discovery** | Uses the [Ludusavi manifest](https://github.com/mtkennerly/ludusavi-manifest), a community database of save locations for tens of thousands of games. Missing games can be pointed at a folder by hand. |
| ☁️ **Backup on exit** | A few seconds after you close a game, its saves are on Drive. The rest of the time the app sits in the tray. |
| 🕓 **History** | The last 10 backups of each game are kept, so you can roll back to an older save. |
| 🛟 **Safe restores** | Your current saves are zipped locally before anything is overwritten. |
| 💻 **Multiple PCs** | Restore on another machine, even with a different Windows user name. |
| 🌍 **8 languages** | English, Russian, Ukrainian, German, Spanish, French, Polish, Simplified Chinese. |

## 🚀 Getting started

### Requirements

- Windows 10 or 11 (x64)
- A Google account
- [rclone](https://rclone.org) (the app downloads it for you, about 25 MB)

### First run

1. Run `GameCloudSync.exe`. There's nothing to install.
2. Click **Connect**. On first launch the app offers to download rclone, then opens your browser so you can sign in to Google and allow access.
3. Tick the games you want to back up.
4. Play. When you close a ticked game, its saves are uploaded automatically.

### Saving and restoring

| Button | What it does |
|---|---|
| **Save** / **Load** on a game | Backs up that game now / restores a backup you pick from the list. |
| **Save selected** | Backs up every ticked game. |
| **Load selected** | Restores the newest backup of every ticked game. |
| **Select all** | Ticks or unticks the whole list. |

> [!IMPORTANT]
> Close the game before restoring its saves.

> [!TIP]
> If a game shows *No saves found*, open the **⋯** menu on its row, choose **Custom folder** and select the save folder. The app remembers it.

### Settings

Open them with the ⚙️ button next to **Connect**.

| Setting | Default | Description |
|---|---|---|
| **Minimize to tray on close** | On | Closing the window hides the app in the tray so auto-backup keeps running. Quit from the tray icon menu → **Exit**. |
| **Start with Windows** | Off | Starts the app in the tray when you sign in. |
| **Language** | Automatic | Follows the Windows language, falling back to English. |

Only one instance runs at a time: launching the app again brings up the existing window.

> [!NOTE]
> Windows 11 hides new tray icons under the **^** arrow. Drag the icon onto the taskbar to keep it visible.

## 🔒 Privacy

- Saves are uploaded **only to your Google Drive**, into a `GameCloudSync` folder.
- The app requests the narrowest Drive scope, `drive.file`: it can see only the files it created, nothing else on your Drive.
- The access token stays on your PC in `%APPDATA%\GameCloudSync\rclone.conf`. Don't share that file.

<details>
<summary><b>What the Drive folder looks like</b></summary>

```
GameCloudSync/
├── Steam/
│   ├── 1245620/                  ← ELDEN RING
│   │   ├── 2026-10-25_14-30-00.zip
│   │   └── ...
│   └── 1091500/                  ← Cyberpunk 2077
└── GOG/
    └── The Witcher 3 Wild Hunt - Game of the Year Edition/
        └── ...
```

Steam games are stored by app ID, which is the same on every PC and survives renames. Other games are stored by name. Each backup is a plain zip you can open yourself.

</details>

## 🛠️ Building from source

You'll need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Visual Studio 2022/2026 with the *WinUI application development* workload is optional.

```bash
dotnet build GameCloudSync.sln -c Release -p:Platform=x64
```

Or open `GameCloudSync.sln` and press <kbd>F5</kbd>.

The output lands in `bin\Release\` (or `bin\Debug\`). The app is unpackaged and bundles the Windows App SDK, so the folder can be copied anywhere and run as is. Debug builds additionally show a full log and the save folders found for the selected game.

<details>
<summary><b>Project layout</b></summary>

| Path | Contents |
|---|---|
| `Core/SaveCore.cs` | Steam and GOG detection, Ludusavi database, save discovery, zipping, rclone |
| `Core/Native.cs` | Tray icon, autostart, single instance (Win32 interop) |
| `Core/Loc.cs` | Language detection and string lookup |
| `Strings/*.json` | Translations, one file per language |
| `MainPage.xaml(.cs)` | Main screen and auto-backup |
| `MainWindow.xaml(.cs)`, `App.xaml(.cs)` | Window, tray behaviour, startup |

Local data lives in `%APPDATA%\GameCloudSync`: settings (`config.json`), the Drive token (`rclone.conf`), the cached Ludusavi database and pre-restore safety copies (`local_backups\`).

</details>

## 🤝 Contributing

**Translations** are the easiest way to help:

1. Copy `Strings/en.json` to `Strings/<code>.json` (e.g. `it.json`, `pt.json`) and translate the values. Missing keys fall back to English.
2. Add the language to the `Languages` list in `Core/Loc.cs`.
3. Build, check that everything fits, and open a pull request.

**Bug reports** are welcome too. If saves for a game aren't found, open an issue with the game's name and where it actually keeps them.

## ⚠️ Known limitations

- Saves are backed up when you **quit** a game. They aren't downloaded automatically before launch, so after playing on another PC, restore manually first.
- Games that keep saves in the Windows registry aren't supported.
- Epic, EA app and Ubisoft Connect games aren't detected yet.
- If a game also uses Steam Cloud, Steam may ask which saves to keep after a restore. Choose the local ones.

## 🙏 Acknowledgements

- [Ludusavi manifest](https://github.com/mtkennerly/ludusavi-manifest) by mtkennerly — save locations
- [rclone](https://rclone.org) — Google Drive transfers
- [YamlDotNet](https://github.com/aaubry/YamlDotNet) — manifest parsing

## 📄 License

Released under the [MIT License](LICENSE).
