using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Bot.Core.Models;
using Bot.Core.Logging;

namespace Bot.Core.Config
{
    public static class ConfigLoader
    {
        private static readonly LogService _logger = new LogService();
        private static readonly object _fileLock = new object();
        private const int MAX_SAVE_RETRIES = 5;
        private const int RETRY_DELAY_MS = 100;

        public static BotConfig Load(string path)
        {
            try
            {
                _logger.LogInfo($"Attempting to load config from: {path}");
                if (!File.Exists(path))
                {
                    _logger.LogWarning($"Config file not found at: {path}. Returning new default BotConfig.");
                    return new BotConfig(); 
                }

                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    _logger.LogWarning($"Config file at {path} is empty. Returning new default BotConfig.");
                    return new BotConfig();
                }

                // _logger.LogInfo($"Config file contents from {path}:\n{json}"); // Optional: can be too verbose
                
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new TaskTypeJsonConverter(), new JsonStringEnumConverter(allowIntegerValues: false) },
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                };
                
                BotConfig? loadedConfig = JsonSerializer.Deserialize<BotConfig>(json, options);
                
                if (loadedConfig == null)
                {
                    _logger.LogWarning($"Deserialization of config from {path} resulted in null. Returning new default BotConfig.");
                    return new BotConfig();
                }
                
                _logger.LogInfo($"Successfully loaded and deserialized config from {path}.");
                return loadedConfig;
            }
            catch (JsonException ex)
            {
                _logger.LogError($"Config deserialization failed for {path}: {ex.Message}");
                // Attempt to log problematic content if possible, but be careful if file doesn't exist or is unreadable
                try { _logger.LogInfo($"Problematic JSON content from {path}:\n{(File.Exists(path) ? File.ReadAllText(path) : "File no longer exists or was not readable.")}"); }
                catch (Exception logEx) { _logger.LogError($"Additionally, failed to read problematic JSON for logging: {logEx.Message}"); }
                _logger.LogWarning($"Returning new default BotConfig due to deserialization error from {path}.");
                return new BotConfig();
            }
            catch (Exception ex) // Catch other potential errors (e.g., IO issues if File.Exists passed but ReadAllText fails for permissions)
            {
                _logger.LogError($"Failed to load config from {path} due to an unexpected error: {ex.ToString()}"); // Log full exception
                _logger.LogWarning($"Returning new default BotConfig due to unexpected error from {path}.");
                return new BotConfig();
            }
        }

        public static void Save(string path, BotConfig config)
        {
            lock (_fileLock)
            {
                SaveWithRetry(path, config);
            }
        }

        private static void SaveWithRetry(string path, BotConfig config)
        {
            for (int attempt = 1; attempt <= MAX_SAVE_RETRIES; attempt++)
            {
                try
                {
                    _logger.LogInfo($"Saving config to: {path} (attempt {attempt})");
                    var options = new JsonSerializerOptions 
                    { 
                        WriteIndented = true,
                        Converters = { new JsonStringEnumConverter(null, allowIntegerValues: true) }
                    };
                    var json = JsonSerializer.Serialize(config, options);
                    
                    // Ensure directory exists
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                        _logger.LogInfo($"Created directory for config file: {directory}");
                    }
                    
                    // Use FileStream with FileShare.Read to prevent conflicts
                    var tempPath = path + ".tmp";
                    using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(fileStream))
                    {
                        writer.Write(json);
                        writer.Flush();
                        fileStream.Flush(true); // Flush to disk
                    }
                    
                    // Atomic replace - move temp file to target
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    File.Move(tempPath, path);
                    
                    _logger.LogInfo($"Successfully saved config to {path} (attempt {attempt}).");
                    return; // Success
                }
                catch (IOException ex) when (attempt < MAX_SAVE_RETRIES)
                {
                    _logger.LogWarning($"Config save attempt {attempt}/{MAX_SAVE_RETRIES} failed due to file access: {ex.Message}. Retrying in {RETRY_DELAY_MS * attempt}ms...");
                    Thread.Sleep(RETRY_DELAY_MS * attempt); // Exponential backoff
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to save config to {path} on attempt {attempt}: {ex.Message}");
                    if (attempt == MAX_SAVE_RETRIES)
                    {
                        _logger.LogError($"All {MAX_SAVE_RETRIES} save attempts failed. Full exception: {ex}");
                        // Clean up temp file if it exists
                        var tempPath = path + ".tmp";
                        if (File.Exists(tempPath))
                        {
                            try { File.Delete(tempPath); } catch { /* Ignore cleanup errors */ }
                        }
                    }
                    else
                    {
                        // Wait before retry for non-IO exceptions too
                        Thread.Sleep(RETRY_DELAY_MS * attempt);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Custom JSON converter for TaskType that gracefully handles invalid enum values
    /// </summary>
    public class TaskTypeJsonConverter : JsonConverter<TaskType>
    {
        private static readonly LogService _logger = new LogService();

        public override TaskType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var enumString = reader.GetString();
                if (string.IsNullOrEmpty(enumString))
                {
                    _logger.LogWarning("Empty TaskType string found in JSON, defaulting to Startup");
                    return TaskType.Startup;
                }

                if (Enum.TryParse<TaskType>(enumString, ignoreCase: true, out var result))
                {
                    return result;
                }

                _logger.LogWarning($"Invalid TaskType value '{enumString}' found in JSON, defaulting to Startup");
                return TaskType.Startup;
            }

            if (reader.TokenType == JsonTokenType.Number)
            {
                var enumInt = reader.GetInt32();
                if (Enum.IsDefined(typeof(TaskType), enumInt))
                {
                    return (TaskType)enumInt;
                }

                _logger.LogWarning($"Invalid TaskType integer value '{enumInt}' found in JSON, defaulting to Startup");
                return TaskType.Startup;
            }

            _logger.LogWarning($"Unexpected JSON token type '{reader.TokenType}' for TaskType, defaulting to Startup");
            return TaskType.Startup;
        }

        public override void Write(Utf8JsonWriter writer, TaskType value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }
    }
} 