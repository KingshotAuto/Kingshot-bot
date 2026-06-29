# Contributing to KingshotAuto

Thanks for your interest in improving KingshotAuto! This project is open source precisely because it is **not finished** — bug fixes, working tasks, tests, docs, and screenshots are all genuinely useful. Beginners are welcome.

Please use **[Issues](https://github.com/KingshotAuto/Kingshot-bot/issues)** to report bugs and propose features, and **[Discussions](https://github.com/KingshotAuto/Kingshot-bot/discussions)** to ask questions and coordinate so two people don't fix the same thing.

---

## Development setup

See the [Prerequisites](README.md#prerequisites) and [Quick Start](README.md#quick-start) sections of the README for the full list. In short, you need:

- Windows 10/11 (x64)
- .NET 8 SDK
- **Git with Git LFS** — run `git lfs install` *before* cloning, or `git lfs pull` afterwards. Without LFS the native OCR DLLs clone as pointer files and Tesseract fails at runtime.
- LDPlayer 9 (64-bit), the VC++ 2015–2022 x64 Redistributable, and Administrator rights to run the app.

```sh
git lfs install
git clone https://github.com/KingshotAuto/Kingshot-bot.git
cd KingshotAuto
git lfs pull            # if you cloned before installing LFS
dotnet restore BotApp.sln
```

## Build, run, and test

```sh
# Build (Debug — has console output)
dotnet build Bot.GUI/Bot.GUI.csproj -c Debug

# Run (launch your terminal as Administrator)
dotnet run --project Bot.GUI/Bot.GUI.csproj

# Run tests
dotnet test
```

Maintainers can produce a portable packaged build with `./Build.ps1 -Version 0.1.0` (add `-SkipInstaller` to skip the NSIS installer; output goes to `dist/`).

Please make sure `dotnet build` and `dotnet test` pass before opening a PR.

## Project layout

```
Bot.Core/                  # Core automation logic
  Tasks/
    TaskManager.cs          # Registers all tasks
    Modules/                # One file per automation task
  Services/                 # Cycle management, instance control, ADB pooling
  ImageDetection/           # OpenCV template matching
  LDPlayer/                 # Emulator / ADB control
  Config/                   # ConfigurationManager (singleton)
  Models/                   # Data models, incl. the TaskType enum
  Utils/                    # OCR, locators, helpers
Bot.GUI/                   # WPF UI (MVVM: ViewModels/, Views/, Converters/)
Bot.Core.Tests/            # Tests
configs/                   # config.json (auto-created) + default_config.example.json
templates/images/          # CV template images, one folder per task
docs/                      # ARCHITECTURE.md and more
```

For deeper detail on the task system, multi-instance management, configuration, and the CV pipeline, read **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)**. There is also a **[CLAUDE.md](CLAUDE.md)** with project conventions for contributors who use AI coding assistants.

## How to add a new automation task

The task system uses a common abstract base so each task only has to implement its own logic. To add one:

1. **Create a class** in `Bot.Core/Tasks/Modules/` that inherits `BaseTaskWithCommonPatterns`.
2. **Override the basics**, for example:
   ```csharp
   public override TaskType TaskType => TaskType.MyNewTask;
   public override string Name => "My New Task";
   protected override string GetImageFolderName() => "mynewtask"; // folder under templates/images/
   ```
3. **Add a `TaskType` enum value** for your task in `Bot.Core/Models/AccountSettings.cs` (where the `TaskType` enum lives).
4. **Implement the task logic** using the base class's unified helpers rather than raw ADB calls — e.g. `FindAndClickImageAsync(...)`, screenshot caching, and the retry/recovery patterns. Follow the established structure: validate state → ensure the correct view → run the core logic with sensible randomized delays → handle errors and return a `TaskExecutionDetails` result.
5. **Register the task** in the `TaskManager` constructor (`Bot.Core/Tasks/TaskManager.cs`), e.g. `_availableTasks[TaskType.MyNewTask] = new MyNewTask();`.
6. **Add template images** for the buttons/icons your task needs (see below).
7. Prefer instance-specific operations everywhere (pass the instance number through) so the task is multi-instance safe.

Use existing modules (for example `AutoBuildTask.cs`) as a reference for structure and the available helpers.

## Capturing and adding template images

The CV pipeline matches small reference images ("templates") against the emulator screen.

- Template images live in `templates/images/<taskFolder>/`, where `<taskFolder>` is the name returned by your task's `GetImageFolderName()`.
- Capture crops of the **exact UI element** you want to match (button, icon, label) from a screenshot of the running game, and save them as PNG.
- Keep crops tight and unambiguous; avoid including large amounts of changing background.
- **Resolution matters.** Templates were originally captured at a specific emulator resolution, so matches can fail at other resolutions/DPI. When adding templates, note the resolution you captured at, and prefer thresholds and search areas that the base helpers already use (`isUIElement: true` for UI buttons, thresholds roughly `0.5`–`0.8`).
- Test your templates against real screenshots before submitting; flaky matching is the most common source of bugs here.

## Coding conventions

- **Match the existing C# style** in the file you're editing (naming, layout, async patterns).
- **Nullable reference types are enabled** — keep nullability annotations correct and avoid introducing new nullable warnings.
- Prefer the base-class helpers and existing services over duplicating ADB/CV logic.
- Use `async`/`await` with `CancellationToken` support, as the existing tasks do.
- Keep logging meaningful but **never log secrets** (see below).
- Don't change the version number by hand — version is set only via the build script.

## Branch and PR workflow

1. Fork the repo (or create a branch if you have write access).
2. Create a focused branch, e.g. `fix/auto-shield-threshold` or `feat/new-task-xyz`.
3. Make your change; build and test locally (`dotnet build` + `dotnet test`).
4. Open a Pull Request against `main`. Fill out the PR template: what changed, the related issue, and what you tested (did it build? did it run? which tasks did you exercise?).
5. Keep PRs reasonably small and single-purpose where possible — it makes review much faster.

### Commit messages

- Write clear, present-tense messages that explain *what* and *why* (e.g. `fix(auto-build): correct upgrade button search area`).
- A short, descriptive subject line plus an optional body is plenty. Conventional-commit style (`fix:`, `feat:`, `docs:`, `chore:`) is appreciated but not required.

## No secrets in commits

Never commit secrets, credentials, API keys, account passwords, or personal data. Do not log or expose anything sensitive. If you find a security issue, follow **[SECURITY.md](SECURITY.md)** instead of opening a public issue with exploit details.

## Coordinating

Not sure where to start? The [Known issues / roadmap](README.md#known-issues--roadmap) lists good first contribution areas (resolution-independent matching, ADB reliability, fixing flaky tasks, and tests). Comment on an issue to claim it, or open a Discussion to talk through an idea before you build it.

Thanks for helping make KingshotAuto better!
