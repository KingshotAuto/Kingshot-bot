# Architecture

Detailed architecture documentation for KingshotAuto.

## Core Components

### Task System (Command + Strategy Pattern)
The task system is the heart of the automation framework:

```
ITask (Interface)
├── BaseTaskWithCommonPatterns (Abstract Base)
│   ├── Unified ADB operations, screenshot caching
│   ├── Template matching and error recovery
│   └── Common automation patterns
└── Concrete Tasks (Tasks/Modules/)
    ├── FarmingTask, AutoHuntTask, AutoShieldTask
    └── 15+ specialized automation tasks
```

**Adding new tasks:**
1. Inherit from `BaseTaskWithCommonPatterns`
2. Use unified template matching methods (`FindAndClickImageAsync`, etc.)
3. Implement proper error recovery patterns
4. Add task-specific configuration model if needed
5. Register in TaskManager constructor

### Multi-Instance Management (Resource Pool Pattern)
```
CycleManagementService (Orchestrator)
├── InstanceManager (LDPlayer lifecycle)
├── ADBConnectionManager (Connection pooling)
├── TaskManager (Task orchestration)
└── SystemResourceMonitor (Resource throttling)
```

Key features: Semaphore-based throttling, progressive startup delays, circuit breaker pattern, connection pooling with health monitoring.

### Configuration System (Singleton Pattern)
```
ConfigurationManager (Singleton)
├── Template System (farming_focused, combat_focused, alliance_focused)
├── Multi-Account Support (independent settings per account)
├── Validation Framework
└── Hot Reloading
```

Structure: `accounts[]`, `enabledTasks[]`, `taskSettings` (JSON), `cycleManagement`, `farmingTargets`

### Image Detection & Template Matching
```
UnifiedTemplateMatchingService
├── Multi-scale matching (0.8x-1.2x scales)
├── Dual color space (BGR + HSV for UI elements)
├── Thread-safe caching (LRU)
└── Parallel processing
```

### Device Management (Connection Pool)
```
ADBConnectionManager (Static Service)
├── Connection pooling per instance
├── Health monitoring & automatic recovery
├── Dynamic throttling limits
└── Circuit breaker protection
```

## Service Layer

### Cycle Management Flow
1. Start cycle with random wait time (20-40 min configurable)
2. Process accounts (parallel or sequential based on `TotalRunningInstances`)
3. Per account: Start instance → Establish ADB → Run tasks → Shutdown (optional)
4. Wait between cycles → Repeat

### Error Recovery Hierarchy
1. Task-level retry (BaseTaskWithCommonPatterns)
2. Recovery task execution (RecoveryTask for UI recovery)
3. Instance reboot (InstanceManager)
4. ADB server restart (ADBConnectionManager)
5. Circuit breaker activation (CycleManagementService)

## Code Patterns

### Task Execution Pattern
```csharp
// All tasks follow this in ExecuteTaskLogicAsync:
try {
    // 1. Initialize/validate state
    // 2. Ensure correct view (MapView, BaseView, etc.)
    // 3. Execute core task logic with proper delays
    // 4. Handle specific error conditions
    return new TaskExecutionDetails(true, message: "Task completed");
}
catch (Exception ex) {
    logger.LogError($"Error in task: {ex.Message}");
    return TaskExecutionDetails.Failed($"Task failed: {ex.Message}");
}
```

### Configuration Access Pattern
```csharp
var config = ConfigurationManager.Instance.GetConfig();
var account = config.Accounts.FirstOrDefault(a => a.AccountName == accountName);

// Task-specific settings
if (!account.TaskSettings.TryGetValue("TaskName", out var settingsJson))
    return new TaskSettings();
var settings = JsonSerializer.Deserialize<TaskSettings>(settingsJson);
```

### Multi-Instance Safety Pattern
```csharp
// Always use instance-specific operations
var screenshot = await TakeScreenshotAsync(account.InstanceNumber, logger);
var success = await ClickAsync(account.InstanceNumber, logger, clickPoint);
var cacheKey = $"template_{account.InstanceNumber}_{templateName}";
```

### Template Matching Usage
```csharp
// UI elements (buttons, icons) - use isUIElement: true
FindAndClickImageAsync("button.png", instanceNumber, logger,
    threshold: 0.6, searchArea: ButtonArea, isUIElement: true);

// Game elements - default grayscale matching
FindAndClickImageAsync("target.png", instanceNumber, logger,
    threshold: 0.6, searchArea: GameArea);

// Always use search areas and appropriate thresholds (0.5-0.8)
```

## Versioning

Version is set via the build parameter `-p:Version=x.x.x` (single source of truth). Releases are distributed as GitHub Releases; the app has no in-app auto-update.
