# Configuration

KingshotAuto stores its settings in a JSON file inside this `configs/` folder.

## Files

| File | Purpose |
|------|---------|
| `config.json` | **The live configuration the app actually reads and writes.** It is created automatically the first time you run the app (and re-saved whenever you change settings in the UI). It is intentionally git-ignored, so your personal accounts and paths are never committed. |
| `default_config.example.json` | A reference template showing every supported section and the exact property names/casing the app expects. **It is not loaded by the app.** Copy it to `config.json` if you want to pre-seed your configuration instead of starting from the auto-generated defaults. |

To start from the template:

```sh
# from the repo root
cp configs/default_config.example.json configs/config.json
```

Then edit `configs/config.json` (or just use the in-app settings UI, which writes the same file).

> Note: most users never need to edit JSON by hand. Running the app creates a
> working `config.json` for you, and everything in it can be changed from the
> application UI.

## Top-level sections

| Property | Description |
|----------|-------------|
| `totalRunningInstances` | How many emulator instances to run concurrently (capped by `maxConcurrentInstances`). |
| `maxConcurrentInstances` | Hard safety ceiling for concurrent instances. |
| `instanceStartupDelayMs` | Progressive delay (ms) inserted between starting each instance. |
| `enableResourceThrottling` | Throttles work across instances to reduce CPU/RAM pressure. |
| `accounts` | Array of per-account settings (see below). Each account maps to one emulator instance. |
| `cycleManagement` | Controls the wait time between full task cycles, whether emulators shut down after a cycle, and the maximum number of cycles to run (`0` = run forever). |
| `performance` | Fine-tuning knobs for click/retry/task delays, instance startup batching, screenshot caching, and status-cache lifetime. |
| `AutoRestart` | Automatically restart automation after a failure. |
| `LDPlayerPath` / `DNPlayerPath` | Install path of the LDPlayer / LDPlayer (DNPlayer) emulator. See note below. |
| `UseDNPlayer` | Use the DNPlayer path/binary instead of the default LDPlayer one. |
| `LogLevel` | Logging verbosity (e.g. `Info`, `Debug`). |
| `SaveDebugVisuals` | Save annotated screenshots for debugging image detection. |
| `ScreenshotPath` | Folder where debug screenshots are written. |
| `MaxRetries` / `RetryDelaySeconds` | Global retry policy for transient failures. |
| `CustomSettings` | Free-form string key/value bag for advanced/experimental options. |

### Per-account settings (`accounts[]`)

| Property | Description |
|----------|-------------|
| `accountName` | Friendly label for the account. |
| `instanceNumber` | The emulator instance index this account runs on. |
| `taskSequence` | Optional explicit ordering of tasks. |
| `enabledTasks` | The tasks to run for this account, encoded as numeric `TaskType` values (for example `2` = Farming, `3` = AutoHunt, `4` = AutoHeal, `9` = ClaimMail). |
| `taskSettings` | Free-form string key/value overrides for individual tasks. |
| `farmingTargets` | Resources to gather, each with a numeric `resourceType` (`0` = Bread, `1` = Wood, `2` = Stone, `3` = Iron) and a tile `level`. |
| `isEnabled` | Whether this account participates in automation. |
| `customConfigPath` | Optional path to an alternate config for this account (`null` for none). |
| `AutoHuntSettings`, `autoRallySettings`, `autoBuildSettings`, `autoShieldSettings`, `farmingSettings`, `troopTrainingSettings`, `farmingBoostSettings`, `allianceTechnologySettings`, `conquestCollectSettings` | Per-task option objects. Any field you omit falls back to that task's built-in default, so these objects can be as small as you like. |
| `doNotShutdown` | Keep this instance running even when the cycle would otherwise shut emulators down. |

> Enum-typed values (such as task types, resource types, and shield/boost
> durations) are serialized as **integers**, not strings.

## A note on property casing

The app serializes with `System.Text.Json` and **no global naming policy**, so the
casing of each key depends on the model:

- Most properties carry an explicit `[JsonPropertyName(...)]` and serialize in
  **camelCase** (e.g. `totalRunningInstances`, `accountName`, `cycleManagement`).
- A few properties have no attribute and therefore serialize with their **PascalCase**
  C# name (e.g. `AutoRestart`, `LDPlayerPath`, `LogLevel`, the account-level
  `AutoHuntSettings` object, and the inner fields of `autoBuildSettings` /
  `autoShieldSettings` such as `MaxSpeedupMinutes` and `UseRechargeableShield`).

Keep the casing exactly as shown in `default_config.example.json`. (Reading is
case-insensitive, but it is safest to match the template.)

## LDPlayer path

The LDPlayer install location (`LDPlayerPath`) can usually be **auto-detected**, or
you can set it explicitly in the application UI — there is no need to edit the JSON by
hand. The example value `C:\LDPlayer\LDPlayer9` is just a common default.
