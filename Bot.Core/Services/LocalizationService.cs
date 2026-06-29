using System;
using System.Threading;
using System.Globalization;
using System.Collections.Generic;

namespace Bot.Core.Services
{
    public class LocalizationService
    {
        private static readonly Dictionary<string, Dictionary<string, string>> _translations = new();
        private static CultureInfo _currentCulture = Thread.CurrentThread.CurrentUICulture;

        public static void Initialize()
        {
            // Initialize with some common messages
            AddTranslation("AdminRightsRequired", new Dictionary<string, string>
            {
                {"en", "Administrator Rights Required"},
                {"ja", "管理者権限が必要です"},
                {"zh-cn", "需要管理员权限"},
                {"zh-tw", "需要管理員權限"},
                {"de", "Administratorrechte erforderlich"},
                {"fr", "Droits d'administrateur requis"}
            });

            AddTranslation("AdminRightsMessage", new Dictionary<string, string>
            {
                {"en", "This application requires administrator privileges to function properly. Please run as administrator."},
                {"ja", "このアプリケーションは管理者権限で実行する必要があります。"},
                {"zh-cn", "此应用程序需要管理员权限才能正常运行。请以管理员身份运行。"},
                {"zh-tw", "此應用程序需要管理員權限才能正常運行。請以管理員身份運行。"},
                {"de", "Diese Anwendung benötigt Administratorrechte, um ordnungsgemäß zu funktionieren. Bitte als Administrator ausführen."},
                {"fr", "Cette application nécessite des privilèges administrateur pour fonctionner correctement. Veuillez exécuter en tant qu'administrateur."}
            });

            // Add more common translations here
        }

        public static void SetCulture(CultureInfo culture)
        {
            _currentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
        }

        public static void AddTranslation(string key, Dictionary<string, string> translations)
        {
            if (!_translations.ContainsKey(key))
            {
                _translations[key] = new Dictionary<string, string>();
            }
            foreach (var translation in translations)
            {
                _translations[key][translation.Key] = translation.Value;
            }
        }

        public static string GetString(string key)
        {
            if (!_translations.ContainsKey(key))
            {
                return key; // Return the key if no translation exists
            }

            var translations = _translations[key];
            var cultureName = _currentCulture.Name.ToLower();
            var languageCode = _currentCulture.TwoLetterISOLanguageName.ToLower();

            // Try exact culture match first
            if (translations.ContainsKey(cultureName))
            {
                return translations[cultureName];
            }

            // Try language code match
            if (translations.ContainsKey(languageCode))
            {
                return translations[languageCode];
            }

            // Fallback to English
            return translations.ContainsKey("en") ? translations["en"] : key;
        }

        public static string GetString(string key, params object[] args)
        {
            var format = GetString(key);
            return string.Format(format, args);
        }
    }
} 