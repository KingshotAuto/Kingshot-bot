using System;
using System.Threading.Tasks;
using System.Threading;
using Bot.Core.Logging;

namespace Bot.Core.LDPlayer
{
    /// <summary>
    /// Unified ADB connection helper that uses the enhanced V2 ADB system.
    /// Provides automatic initialization and consistent interface for all ADB operations.
    /// </summary>
    public static class ADBMigrationHelper
    {
        private static bool _v2SystemInitialized = false;
        private static readonly object _initLock = new object();

        /// <summary>
        /// Initialize the V2 ADB system (automatically called when needed)
        /// </summary>
        public static async Task InitializeV2SystemAsync(LogService logger, int maxConcurrentEmulators = 3)
        {
            lock (_initLock)
            {
                if (_v2SystemInitialized) return;
            }

            if (await ADBConnectionManagerV2.InitializeAsync(logger, maxConcurrentEmulators))
            {
                lock (_initLock)
                {
                    _v2SystemInitialized = true;
                }
                logger.LogInfo("[ADB] V2 system initialized successfully");
            }
            else
            {
                throw new InvalidOperationException("Failed to initialize ADB V2 system. Check ADB path and emulator configuration.");
            }
        }

        /// <summary>
        /// Get connection using the V2 ADB system (auto-initializes if needed)
        /// </summary>
        public static async Task<object> GetConnectionAsync(int instanceNumber, LogService logger, CancellationToken cancellationToken = default)
        {
            // Auto-initialize V2 system if not already done
            if (!_v2SystemInitialized)
            {
                await InitializeV2SystemAsync(logger);
            }

            return await ADBConnectionManagerV2.GetConnectionAsync(instanceNumber, logger, cancellationToken);
        }

        /// <summary>
        /// Execute a screenshot operation using V2 system
        /// </summary>
        public static async Task<byte[]> TakeScreenshotAsync(object connection, LogService logger, CancellationToken cancellationToken = default)
        {
            if (connection is ADBControllerV2 v2Controller)
            {
                return await v2Controller.TakeScreenshotAsync(cancellationToken);
            }
            else
            {
                throw new ArgumentException("Invalid connection type - expected ADBControllerV2");
            }
        }

        /// <summary>
        /// Execute a tap operation using V2 system
        /// </summary>
        public static async Task<bool> TapAsync(object connection, int x, int y, LogService logger, CancellationToken cancellationToken = default)
        {
            if (connection is ADBControllerV2 v2Controller)
            {
                return await v2Controller.TapAsync(x, y, cancellationToken);
            }
            else
            {
                throw new ArgumentException("Invalid connection type - expected ADBControllerV2");
            }
        }

        /// <summary>
        /// Execute a random tap in rectangle using V2 system
        /// </summary>
        public static async Task<bool> TapRandomInRectAsync(object connection, int x1, int y1, int x2, int y2, LogService logger, CancellationToken cancellationToken = default)
        {
            if (connection is ADBControllerV2 v2Controller)
            {
                return await v2Controller.TapRandomInRectAsync(x1, y1, x2, y2, cancellationToken);
            }
            else
            {
                throw new ArgumentException("Invalid connection type - expected ADBControllerV2");
            }
        }

        /// <summary>
        /// Execute a swipe operation using V2 system
        /// </summary>
        public static async Task<bool> SwipeAsync(object connection, int startX, int startY, int endX, int endY, int durationMs, LogService logger, CancellationToken cancellationToken = default)
        {
            if (connection is ADBControllerV2 v2Controller)
            {
                return await v2Controller.SwipeAsync(startX, startY, endX, endY, durationMs, cancellationToken);
            }
            else
            {
                throw new ArgumentException("Invalid connection type - expected ADBControllerV2");
            }
        }

        /// <summary>
        /// Check if V2 system is initialized
        /// </summary>
        public static bool IsV2SystemInitialized => _v2SystemInitialized;

        /// <summary>
        /// Get performance statistics from V2 system
        /// </summary>
        public static System.Collections.Generic.Dictionary<string, (double AvgMs, int Count)>? GetPerformanceStats()
        {
            if (_v2SystemInitialized)
            {
                return ADBControllerV2.GetPerformanceStats();
            }
            return null;
        }

        /// <summary>
        /// Acquire emulator slot using V2 system
        /// </summary>
        public static async Task<bool> AcquireEmulatorSlotAsync(int instanceNumber, LogService logger, CancellationToken cancellationToken = default)
        {
            // Auto-initialize V2 system if not already done
            if (!_v2SystemInitialized)
            {
                await InitializeV2SystemAsync(logger);
            }

            return await ADBConnectionManagerV2.AcquireEmulatorSlotAsync(instanceNumber, logger, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Release emulator slot using V2 system
        /// </summary>
        public static void ReleaseEmulatorSlot(int instanceNumber, LogService logger)
        {
            if (_v2SystemInitialized)
            {
                ADBConnectionManagerV2.ReleaseEmulatorSlot(instanceNumber, logger);
            }
        }

        /// <summary>
        /// Close connection using V2 system
        /// </summary>
        public static void CloseConnection(object connection, int instanceNumber, LogService logger)
        {
            if (connection is ADBControllerV2 v2Controller)
            {
                v2Controller.Dispose();
                // V2 system manages connections internally
            }
        }
    }
}