using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Bot.Core.Models;
using Bot.Core.Logging;

namespace Bot.Core.Services
{
    public class ShieldTimeManager
    {
        private static readonly ConcurrentDictionary<int, (DateTime StartTime, TimeSpan Duration)> _shieldTimers = new();
        private static readonly ConcurrentDictionary<int, Timer> _updateTimers = new();
        private readonly LogService _logger;

        public event Action<int, TimeSpan?>? ShieldTimeUpdated;

        private static ShieldTimeManager? _instance;
        private static readonly object _lock = new();

        public static ShieldTimeManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new ShieldTimeManager(new LogService());
                    }
                }
                return _instance;
            }
        }

        private ShieldTimeManager(LogService logger)
        {
            _logger = logger;
        }

        public void UpdateShieldTime(int instanceNumber, DateTime startTime, TimeSpan duration)
        {
            _shieldTimers[instanceNumber] = (startTime, duration);
            StartTimer(instanceNumber);
            NotifyTimeUpdated(instanceNumber);
        }

        public void ClearShieldTime(int instanceNumber)
        {
            _shieldTimers.TryRemove(instanceNumber, out _);
            if (_updateTimers.TryRemove(instanceNumber, out var timer))
            {
                timer.Dispose();
            }
            NotifyTimeUpdated(instanceNumber);
        }

        public TimeSpan? GetRemainingTime(int instanceNumber)
        {
            if (_shieldTimers.TryGetValue(instanceNumber, out var shieldInfo))
            {
                var elapsed = DateTime.UtcNow - shieldInfo.StartTime;
                if (elapsed < shieldInfo.Duration)
                {
                    return shieldInfo.Duration - elapsed;
                }
            }
            return null;
        }

        private void StartTimer(int instanceNumber)
        {
            if (_updateTimers.TryGetValue(instanceNumber, out var existingTimer))
            {
                existingTimer.Dispose();
            }

            var timer = new Timer(_ => NotifyTimeUpdated(instanceNumber), null, 0, 5000);
            _updateTimers[instanceNumber] = timer;
        }

        private void NotifyTimeUpdated(int instanceNumber)
        {
            var remaining = GetRemainingTime(instanceNumber);
            if (remaining == null || remaining.Value.TotalSeconds <= 0)
            {
                ClearShieldTime(instanceNumber);
                remaining = null;
            }
            ShieldTimeUpdated?.Invoke(instanceNumber, remaining);
        }

        public void LoadShieldTime(int instanceNumber, AutoShieldSettings settings)
        {
            if (settings.LastShieldActivatedTime.HasValue && settings.LastShieldDuration.HasValue)
            {
                var remaining = settings.LastShieldDuration.Value - (DateTime.UtcNow - settings.LastShieldActivatedTime.Value);
                if (remaining.TotalSeconds > 0)
                {
                    UpdateShieldTime(instanceNumber, settings.LastShieldActivatedTime.Value, settings.LastShieldDuration.Value);
                }
            }
        }
    }
} 