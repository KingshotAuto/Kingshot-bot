using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Bot.Core.Logging;

namespace Bot.Core.Utils
{
    public class TroopParsingConfiguration
    {
        public int MaxLevenshteinDistance { get; set; } = 2;
        public string[] KnownTroopTypes { get; set; } = { "infantry", "cavalry", "archers" };
        public string[] CompletedPatterns { get; set; } = { "complet", "done", "finish", "ready", "collect" };
        public string[] TrainingPatterns { get; set; } = { "train", "progress", "building", "wait" };
        
        public Dictionary<string, string> SpecialCaseMappings { get; set; } = new()
        {
            { "infentry", "Infantry" },
            { "infatry", "Infantry" },
            { "cavalty", "Cavalry" },
            { "cavaliy", "Cavalry" },
            { "arcners", "Archers" },
            { "archeis", "Archers" },
            { "archets", "Archers" }
        };
    }

    public class TroopStatusParser
    {
        private readonly LogService _logger;
        private readonly TroopParsingConfiguration _config;

        public TroopStatusParser(LogService logger, TroopParsingConfiguration? config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? new TroopParsingConfiguration();
            
            // Validate configuration
            if (_config.KnownTroopTypes == null || _config.KnownTroopTypes.Length == 0)
                throw new ArgumentException("KnownTroopTypes cannot be null or empty", nameof(config));
            
            if (_config.MaxLevenshteinDistance < 0)
                throw new ArgumentException("MaxLevenshteinDistance must be non-negative", nameof(config));
        }

        public TroopStatusResult ParseTroopStatus(string ocrText)
        {
            var result = new TroopStatusResult();
            
            try
            {
                if (string.IsNullOrWhiteSpace(ocrText))
                {
                    _logger.LogError("OCR text is empty or null");
                    return result;
                }

                _logger.LogInfo($"Parsing troop status from OCR text: '{ocrText}'");

                // Split text into lines and clean them
                var lines = ocrText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(line => CleanLine(line))
                                  .Where(line => !string.IsNullOrEmpty(line))
                                  .ToList();

                // Process lines in pairs (troop type, status)
                for (int i = 0; i < lines.Count - 1; i += 2)
                {
                    var troopType = NormalizeTroopType(lines[i]);
                    var status = lines[i + 1];

                    if (!string.IsNullOrEmpty(troopType))
                    {
                        var troopStatus = ParseSingleTroopStatus(troopType, status);
                        result.TroopStatuses[troopType] = troopStatus;

                        _logger.LogInfo($"Parsed {troopType}: {troopStatus.Status}" + 
                                       (troopStatus.TimeRemaining.HasValue ? $" (Time: {troopStatus.TimeRemaining})" : ""));
                    }
                    else
                    {
                        _logger.LogError($"Could not normalize troop type from: '{lines[i]}'");
                    }
                }

                LogResults(result);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error parsing troop status: {ex.Message}");
                return result;
            }
        }

        private string CleanLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return string.Empty;

            // Remove extra whitespace and common OCR artifacts
            var cleaned = line.Trim()
                             .Replace("  ", " ")  // Multiple spaces to single space
                             .Replace("\t", " ")  // Tabs to spaces
                             .Replace("_", "")    // Underscores
                             .Replace("|", "l")   // Pipe to lowercase L
                             .Replace("1", "l")   // Number 1 to lowercase L in text context
                             .Replace("0", "O");  // Zero to capital O in text context

            return cleaned;
        }

        private string NormalizeTroopType(string troopText)
        {
            if (string.IsNullOrWhiteSpace(troopText))
                return string.Empty;

            // Remove anything that isn't a letter and convert to lowercase
            var clean = new string(troopText
                .Where(c => char.IsLetter(c))
                .ToArray())
                .ToLowerInvariant();

            _logger.LogInfo($"Cleaning troop type: '{troopText}' -> '{clean}'");

            // Direct prefix matching for common patterns
            if (clean.StartsWith("infan") || clean.StartsWith("infant"))
                return "Infantry";
            if (clean.StartsWith("caval") || clean.StartsWith("cavai") || clean.StartsWith("cavalry"))
                return "Cavalry";
            if (clean.StartsWith("arch") || clean.StartsWith("arcl") || clean.StartsWith("arche") || clean.StartsWith("archer"))
                return "Archers";

            // Fuzzy matching using Levenshtein distance
            foreach (var knownType in _config.KnownTroopTypes)
            {
                if (LevenshteinDistance(clean, knownType) <= _config.MaxLevenshteinDistance) // Allow up to 2 character differences
                {
                    var result = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(knownType);
                    _logger.LogInfo($"Fuzzy matched '{clean}' to '{result}' (distance: {LevenshteinDistance(clean, knownType)})");
                    return result;
                }
            }

            // Special case handling for common OCR misreads
            if (_config.SpecialCaseMappings.ContainsKey(clean))
            {
                _logger.LogInfo($"Special case matched '{clean}' to '{_config.SpecialCaseMappings[clean]}'");
                return _config.SpecialCaseMappings[clean];
            }

            _logger.LogError($"Could not normalize troop type: '{troopText}' (cleaned: '{clean}')");
            return string.Empty;
        }

        public SingleTroopStatus ParseSingleTroopStatus(string troopType, string statusText)
        {
            var status = new SingleTroopStatus { TroopType = troopType };

            if (string.IsNullOrWhiteSpace(statusText))
            {
                status.Status = TroopCompletionStatus.Unknown;
                return status;
            }

            var normalizedStatus = CleanLine(statusText.ToLowerInvariant());
            _logger.LogInfo($"Parsing status for {troopType}: '{statusText}' -> '{normalizedStatus}'");

            // Check if it's completed (with fuzzy matching)
            if (_config.CompletedPatterns.Any(pattern => normalizedStatus.Contains(pattern)))
            {
                status.Status = TroopCompletionStatus.Completed;
                return status;
            }

            // Check if it's a time format (HH:MM:SS, MM:SS, or H:MM)
            var timePatterns = new[]
            {
                @"(\d{1,2}):(\d{1,2}):(\d{1,2})", // HH:MM:SS
                @"(\d{1,2}):(\d{1,2})",          // MM:SS or HH:MM
                @"(\d{1,2})h\s*(\d{1,2})m",      // 1h 30m format
                @"(\d{1,2})min"                   // 30min format
            };

            foreach (var pattern in timePatterns)
            {
                var timeMatch = Regex.Match(statusText, pattern);
                if (timeMatch.Success)
                {
                    status.Status = TroopCompletionStatus.Training;
                    status.TimeRemaining = ParseTimeFromMatch(timeMatch, pattern);
                    return status;
                }
            }

            // Check for "training" or "in progress" keywords
            if (_config.TrainingPatterns.Any(pattern => normalizedStatus.Contains(pattern)))
            {
                status.Status = TroopCompletionStatus.Training;
                return status;
            }

            // Default to unknown if we can't parse it
            status.Status = TroopCompletionStatus.Unknown;
            _logger.LogError($"Could not parse status text: '{statusText}' for {troopType}");
            
            return status;
        }

        private TimeSpan? ParseTimeFromMatch(Match timeMatch, string pattern)
        {
            try
            {
                if (pattern.Contains("h") && pattern.Contains("m")) // 1h 30m format
                {
                    if (int.TryParse(timeMatch.Groups[1].Value, out int hours) &&
                        int.TryParse(timeMatch.Groups[2].Value, out int minutes))
                    {
                        return new TimeSpan(hours, minutes, 0);
                    }
                }
                else if (pattern.Contains("min")) // 30min format
                {
                    if (int.TryParse(timeMatch.Groups[1].Value, out int minutes))
                    {
                        return new TimeSpan(0, minutes, 0);
                    }
                }
                else if (timeMatch.Groups.Count == 4) // HH:MM:SS
                {
                    if (int.TryParse(timeMatch.Groups[1].Value, out int hours) &&
                        int.TryParse(timeMatch.Groups[2].Value, out int minutes) &&
                        int.TryParse(timeMatch.Groups[3].Value, out int seconds))
                    {
                        return new TimeSpan(hours, minutes, seconds);
                    }
                }
                else if (timeMatch.Groups.Count == 3) // MM:SS or HH:MM
                {
                    if (int.TryParse(timeMatch.Groups[1].Value, out int first) &&
                        int.TryParse(timeMatch.Groups[2].Value, out int second))
                    {
                        // If first number > 59, assume HH:MM format, otherwise MM:SS
                        if (first > 59)
                        {
                            return new TimeSpan(first, second, 0); // Hours:Minutes
                        }
                        else
                        {
                            return new TimeSpan(0, first, second); // Minutes:Seconds
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error parsing time from match: {ex.Message}");
            }

            return null;
        }

        private int LevenshteinDistance(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1)) return string.IsNullOrEmpty(s2) ? 0 : s2.Length;
            if (string.IsNullOrEmpty(s2)) return s1.Length;

            var distance = new int[s1.Length + 1, s2.Length + 1];

            for (int i = 0; i <= s1.Length; i++)
                distance[i, 0] = i;
            for (int j = 0; j <= s2.Length; j++)
                distance[0, j] = j;

            for (int i = 1; i <= s1.Length; i++)
            {
                for (int j = 1; j <= s2.Length; j++)
                {
                    int cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                    distance[i, j] = Math.Min(
                        Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                        distance[i - 1, j - 1] + cost);
                }
            }

            return distance[s1.Length, s2.Length];
        }

        private void LogResults(TroopStatusResult result)
        {
            _logger.LogInfo("=== Troop Status Summary ===");
            
            foreach (var kvp in result.TroopStatuses)
            {
                var troopType = kvp.Key;
                var status = kvp.Value;
                
                var timeInfo = status.TimeRemaining.HasValue ? $" (Remaining: {status.TimeRemaining})" : "";
                _logger.LogInfo($"{troopType}: {status.Status}{timeInfo}");
            }

            var completedCount = result.TroopStatuses.Values.Count(s => s.Status == TroopCompletionStatus.Completed);
            _logger.LogInfo($"Completed troops: {completedCount}/{result.TroopStatuses.Count}");
        }
    }

    public class TroopStatusResult
    {
        public Dictionary<string, SingleTroopStatus> TroopStatuses { get; set; } = new();

        public bool HasAnyCompleted => TroopStatuses.Values.Any(s => s.Status == TroopCompletionStatus.Completed);
        public bool AllCompleted => TroopStatuses.Values.All(s => s.Status == TroopCompletionStatus.Completed);
        public List<string> CompletedTroops => TroopStatuses.Where(kvp => kvp.Value.Status == TroopCompletionStatus.Completed)
                                                           .Select(kvp => kvp.Key)
                                                           .ToList();
    }

    public class SingleTroopStatus
    {
        public string TroopType { get; set; } = string.Empty;
        public TroopCompletionStatus Status { get; set; } = TroopCompletionStatus.Unknown;
        public TimeSpan? TimeRemaining { get; set; }
    }

    public enum TroopCompletionStatus
    {
        Unknown,
        Completed,
        Training
    }
} 