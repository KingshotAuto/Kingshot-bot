# KingshotAuto

A Windows desktop automation tool for the mobile strategy game **Kingshot**. It runs the game inside the [LDPlayer](https://www.ldplayer.net/) Android emulator and automates in-game tasks using computer vision (template matching) and OCR.

> **Status: early / not fully working — contributions welcome.**
> KingshotAuto was previously a paid, closed-source product. It is now MIT-licensed open source. **Not every automation task works reliably yet.** It is published so the community can fix, improve, and extend it. See [Known issues / roadmap](#known-issues--roadmap) and [CONTRIBUTING.md](CONTRIBUTING.md) — beginners are very welcome.

---

## Features

KingshotAuto drives the game through a set of pluggable automation **tasks**. Each task uses screen captures from the emulator plus template matching / OCR to find and click the right UI elements. Current task modules include:

- **Farming** — gather resources on the world map
- **Auto Hunt** — attack hunting / beast targets
- **Auto Rally** — start and join rallies
- **Auto Shield** — keep your city shield active
- **Auto Heal** — heal wounded troops
- **Auto Build** — queue building upgrades
- **Troop Training** — train troops
- **Auto Technology** & **Alliance Technology** — research / alliance tech
- **Auto Claim Hero** — claim hero rewards
- **Alliance Help** — send alliance help
- **Conquest Collect** — collect conquest rewards
- **Collect VIP** — claim VIP rewards
- **Claim Mail** & **Claim Missions** — collect mail and mission rewards
- **Resident Welcome** — welcome residents
- **Account Detection / Change Account** — multi-account switching
- **Startup / Recovery** — bring the game to a known state and recover from unexpected UI

Additional capabilities:

- **Multi-instance** support — run multiple emulator instances / accounts with per-instance throttling
- **Cycle management** — repeat task batches on a randomized schedule
- **Computer-vision pipeline** — multi-scale template matching with grayscale and HSV color spaces
- **OCR** via Tesseract for reading numbers and text on screen

## Screenshots

> _Screenshots coming soon — contributions welcome! (PRs adding screenshots of the UI and a running cycle are appreciated.)_

## Prerequisites

You will need the following on a 64-bit Windows machine:

- **Windows 10 or 11 (x64)**
- **.NET 8 SDK** — <https://dotnet.microsoft.com/download/dotnet/8.0>
- **Git with Git LFS** — the native OCR DLLs under `runtimes/win-x64/native/` and `tools/VC_redist.x64.exe` are stored via Git LFS. **Without git-lfs they clone as tiny pointer files and Tesseract OCR fails at runtime.** Install Git LFS once with `git lfs install` *before* cloning (or run `git lfs pull` afterwards).
- **LDPlayer 9 (64-bit)** emulator — <https://www.ldplayer.net/>
- **Microsoft Visual C++ 2015–2022 x64 Redistributable** — required by the OpenCV / Tesseract native libraries
- **Administrator privileges** — the app must run **as Administrator** to control the emulator over ADB

## Quick Start

```sh
# 1. Make sure Git LFS is installed (only needed once per machine)
git lfs install

# 2. Clone the repository (LFS-backed native files download automatically)
git clone https://github.com/KingshotAuto/Kingshot-bot.git
cd KingshotAuto

# If you cloned before installing Git LFS, pull the real binaries now:
git lfs pull

# 3. Restore dependencies
dotnet restore BotApp.sln

# 4. Build (Debug)
dotnet build Bot.GUI/Bot.GUI.csproj -c Debug

# 5. Run (launch your terminal as Administrator)
dotnet run --project Bot.GUI/Bot.GUI.csproj
```

> **Run as Administrator.** Controlling LDPlayer over ADB requires elevated privileges. Start your terminal (or the built app) as Administrator, otherwise the emulator/ADB connection will fail.

Maintainers can produce a portable packaged build:

```sh
# Produces a portable zip in dist/ ; add -SkipInstaller to skip the NSIS installer
./Build.ps1 -Version 0.1.0
```

## Configuration

- On first run, the app **auto-creates** its configuration at `configs/config.json`. If that file is missing it falls back to a built-in default, so you can start without any manual setup.
- A documented example of every option lives at `configs/default_config.example.json` — copy it to `configs/config.json` if you want to edit settings by hand.
- The **LDPlayer install path** defaults to `C:\LDPlayer\LDPlayer9`. The app tries to auto-detect it, and you can also set it from the in-app **Browse** button.
- Most settings (which accounts to run, which tasks are enabled, per-task options, and cycle timing) are configurable from the UI.

## How it works

1. **Emulator control** — KingshotAuto talks to LDPlayer over **ADB** (via `AdvancedSharpAdbClient`) to launch instances, capture screenshots, and send taps/swipes.
2. **Computer vision** — each task captures the emulator screen and uses **OpenCVSharp4** template matching to locate buttons, icons, and targets, then clicks them. Matching uses search areas and tuned thresholds, with grayscale and HSV color spaces and multiple scales for robustness.
3. **OCR** — **Tesseract** reads on-screen numbers/text where template matching is not enough.
4. **Orchestration** — a cycle manager runs the enabled tasks per account on a randomized schedule, with retry and recovery logic for flaky UI/connectivity.

Template images for the CV pipeline live in `templates/images/`, organized into one folder per task. Each task maps to its folder via `GetImageFolderName()`.

## Project structure

```
KingshotAuto/
├── BotApp.sln                 # Solution file
├── Bot.Core/                  # Core automation logic
│   ├── Tasks/
│   │   ├── TaskManager.cs      # Registers and runs tasks
│   │   └── Modules/            # Individual automation tasks
│   ├── Services/               # Cycle management, instance control, etc.
│   ├── ImageDetection/         # Template matching (OpenCV)
│   ├── LDPlayer/               # Emulator / ADB control
│   ├── Config/                 # Configuration management
│   ├── Models/                 # Data models (incl. TaskType enum)
│   └── Utils/                  # OCR, locators, helpers
├── Bot.GUI/                    # WPF UI (MVVM)
│   ├── ViewModels/
│   ├── Views/
│   └── Converters/
├── Bot.Core.Tests/            # Tests
├── configs/                    # Config + documented example
├── templates/images/          # CV template images (one folder per task)
└── docs/                       # ARCHITECTURE.md, etc.
```

## Contributing

Contributions of all sizes are welcome — bug fixes, new/working tasks, tests, docs, and screenshots. Please read **[CONTRIBUTING.md](CONTRIBUTING.md)** for dev setup, how to add a new automation task, how to capture template images, and the PR workflow.

Use **[Issues](https://github.com/KingshotAuto/Kingshot-bot/issues)** to report bugs and **[Discussions](https://github.com/KingshotAuto/Kingshot-bot/discussions)** to ask questions and coordinate work.

## Known issues / roadmap

This project is honest about its rough edges — these are great places to start contributing:

- **Not all tasks work reliably.** Several task modules need fixing or tuning.
- **Resolution sensitivity.** Template images were captured at a specific emulator resolution, so matching can fail on other resolutions/DPI settings. Making matching resolution-independent is a high-value contribution.
- **Flaky ADB / emulator connectivity.** Connection handling can be brittle; improvements to reconnection and recovery are welcome.
- **Test coverage is thin.** More unit/integration tests would help a lot.

If you want to help, see [CONTRIBUTING.md](CONTRIBUTING.md) and open/claim an issue.

## Privacy

KingshotAuto runs entirely on your machine. **No telemetry, no analytics, no license server, no phone-home.** The original licensing/activation, analytics, and auto-update systems have been **completely removed** — a build contacts **no servers** and sends **no data anywhere**.

## Disclaimer

This software automates a third-party game and **may violate that game's Terms of Service**. Using automation tools can put your account at risk of suspension or banning. **Use at your own risk.** KingshotAuto is provided **"as is", without warranty of any kind** (see the [MIT License](LICENSE)). The authors and contributors are not affiliated with the makers of Kingshot and accept no liability for how you use this software.

## License

Released under the **MIT License**. See [LICENSE](LICENSE).

Copyright (c) KingshotAuto
