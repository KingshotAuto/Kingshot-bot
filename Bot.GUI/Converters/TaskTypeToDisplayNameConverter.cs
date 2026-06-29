using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using System.Text.RegularExpressions;
using Bot.Core.Models;

namespace Bot.GUI.Converters
{
    public class TaskTypeToDisplayNameConverter : IValueConverter
    {
        private static readonly HashSet<TaskType> SystemTasks = new()
        {
            TaskType.Startup,
            TaskType.Recovery,
            TaskType.AccountDetection
        };
        
        private static readonly HashSet<TaskType> DisabledTasks = new()
        {
            TaskType.AutoTechnology // Disabled as it's not ready for customers
        };

        private static readonly Dictionary<TaskType, string> DisplayNames = new()
        {
            { TaskType.AutoHunt, "Intel Missions" },
            { TaskType.AutoBuild, "Auto Build" },
            { TaskType.AutoHeal, "Auto Heal" },
            { TaskType.AutoAllianceHelp, "Alliance Help" },
            { TaskType.ClaimMail, "Claim Mail" },
            { TaskType.ConquestCollect, "Conquest Collect" },
            { TaskType.TroopTraining, "Troop Training" },
            { TaskType.Farming, "Farming" },
            { TaskType.ChangeAccount, "Change Account" },
            { TaskType.AutoClaimHero, "Auto Claim Hero" },
            { TaskType.AutoShield, "Auto Shield" },
            { TaskType.CollectVip, "Collect Vip" },
            { TaskType.ClaimMissions, "Claim Missions (BETA)" },
            { TaskType.ResidentWelcome, "Resident Welcome" },
            { TaskType.AllianceTechnology, "Alliance Technology" },
            { TaskType.AutoRally, "Auto Rally (BETA)" }
        };

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // If converting a collection, filter out system tasks and disabled tasks and convert the rest
            if (value is IEnumerable<TaskType> taskTypes)
            {
                return taskTypes
                    .Where(t => !SystemTasks.Contains(t) && !DisabledTasks.Contains(t))
                    .Select(t => DisplayNames.TryGetValue(t, out var name) ? name : t.ToString())
                    .ToList(); // Ensure it's materialized to prevent multiple enumeration
            }

            // If converting a single TaskType
            if (value is TaskType taskType)
            {
                if (SystemTasks.Contains(taskType) || DisabledTasks.Contains(taskType))
                    return null;
                return DisplayNames.TryGetValue(taskType, out var name) ? name : taskType.ToString();
            }

            return value?.ToString() ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string displayName)
            {
                var pair = DisplayNames.FirstOrDefault(kvp => kvp.Value == displayName);
                if (pair.Value != null)
                {
                    return pair.Key;
                }

                // Try parsing the string directly as a TaskType
                if (Enum.TryParse<TaskType>(displayName, out var taskType) && !SystemTasks.Contains(taskType) && !DisabledTasks.Contains(taskType))
                {
                    return taskType;
                }
            }

            return null;
        }
    }
} 