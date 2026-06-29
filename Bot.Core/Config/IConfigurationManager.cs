using Bot.Core.Models;

namespace Bot.Core.Config
{
    public interface IConfigurationManager
    {
        BotConfig GetConfig();
        void SaveConfig(BotConfig config);
        void ReloadConfig();
    }
} 