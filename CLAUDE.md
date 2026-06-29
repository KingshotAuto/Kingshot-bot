# CLAUDE.md

## Project: KingshotAuto
Windows desktop app for game automation using C# (.NET 8.0), WPF, LDPlayer emulator, OpenCV, and Tesseract OCR. Open source under the MIT license. It was previously a paid product; the licensing/activation, analytics/telemetry, and auto-update systems have all been removed, so a build contacts no servers and sends no data.

## Critical Limitation
**You cannot run dotnet commands.** Always ask the user to run them.

## Commands
| Command | Purpose |
|---------|---------|
| `dotnet restore BotApp.sln` | Restore dependencies |
| `dotnet build Bot.GUI/Bot.GUI.csproj -c Debug` | Debug build |
| `dotnet run --project Bot.GUI/Bot.GUI.csproj` | Run app |
| `dotnet test` | Run tests |
| `.\Build-Release.ps1 -Version "x.x.x"` | Package a portable zip + NSIS installer (optional) |
| `.\Build-Release.ps1 -Version "x.x.x" -SkipInstaller` | Portable zip only |

## Project Structure
```
Bot.GUI/              # WPF UI (MVVM pattern)
  ViewModels/         # UI logic
  Views/              # XAML views
  Converters/         # WPF value converters
Bot.Core/             # Core automation logic
  Tasks/              # Task system (TaskManager, BaseTask, Modules/)
  Services/           # Business services (CycleManagement, etc.)
  ImageDetection/     # Computer vision (template matching)
  LDPlayer/           # Android emulator control (ADB)
  Config/             # Configuration management
  Utils/              # OCR, Locator, helpers
configs/              # default_config.example.json (live config is configs/config.json, auto-created on first run)
templates/images/     # Template images for CV (organized by task)
```

## Tech Stack
- **OpenCVSharp4**: Template matching and image recognition
- **Tesseract**: OCR for text extraction
- **Serilog**: Structured logging
- **AdvancedSharpAdbClient**: ADB communication

## Key Patterns
- **Tasks**: Inherit `BaseTaskWithCommonPatterns`, register in `TaskManager`
- **Config**: Singleton `ConfigurationManager`, JSON-serialized task settings per account
- **Multi-instance**: Per-instance locks, cached screenshots, progressive delays
- **Template matching**: Use search areas, thresholds 0.5-0.8, `isUIElement: true` for UI

## Gotchas
- **Native deps via Git LFS**: the Tesseract/Leptonica DLLs (`runtimes/`) and `tools/VC_redist.x64.exe` are Git LFS objects. Run `git lfs install` before cloning (or `git lfs pull` after) or OCR will fail at runtime.
- **StaticResourceExtension error**: Check for duplicate converters between App.xaml and views.
- **Version**: Pass `-p:Version=x.x.x` to the build (single source of truth).
- **Debug vs Release**: Debug allocates a console for log output.

## Security / Open-source notes
- Never commit secrets/keys.
- The app sends no telemetry and performs no license or online checks.
- This automates a third-party game and may violate that game's Terms of Service (see `SECURITY.md`). Use at your own risk.

## More Info
- `README.md` - Overview, prerequisites, build/run
- `CONTRIBUTING.md` - How to contribute
- `docs/ARCHITECTURE.md` - Detailed architecture, code patterns, task system
