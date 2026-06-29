using System;
using System.IO;

namespace Bot.Core.ImageDetection
{
    /// <summary>
    /// Enumeration of all game templates with their file paths.
    /// Provides type-safe access to template images and centralized template management.
    /// </summary>
    public enum GameTemplates
    {
        // Startup templates
        STARTUP_CONFIRM_WELCOME,
        STARTUP_KINGSHOT_APP,

        // Account management
        CHANGE_ACCOUNT_SWITCH,

        // Alliance templates
        ALLIANCE_BUTTON,
        ALLIANCE_TECHNOLOGY,
        ALLIANCE_HELP,
        ALLIANCE_WAR,

        // Auto Build templates
        AUTOBUILD_BACK_BUTTON,
        AUTOBUILD_GO_BUTTON,
        AUTOBUILD_KITCHEN,
        AUTOBUILD_PERSON_HEAD,
        AUTOBUILD_QUICK_USE_BUTTON,
        AUTOBUILD_RED_X,
        AUTOBUILD_SIDE_MENU,
        AUTOBUILD_SPEEDUP_BUTTON,
        AUTOBUILD_TRAIN_ICON,
        AUTOBUILD_UPGRADE_BUTTON,
        AUTOBUILD_UPGRADE_ICON,
        AUTOBUILD_USE_BUTTON,

        // Auto Claim Hero templates
        AUTOCLAIMHERO_BACK_ARROW,
        AUTOCLAIMHERO_FREE_BUTTON,
        AUTOCLAIMHERO_HERO_BUTTON,
        AUTOCLAIMHERO_RECRUIT_HEROS_BUTTON,
        AUTOCLAIMHERO_REWARDS,

        // Auto Heal templates
        AUTOHEAL_HEAL_BUTTON,

        // Auto Hunt templates
        AUTOHUNT_ATTACK_BUTTON,
        AUTOHUNT_HUNT_BUTTON,

        // Auto Shield templates
        AUTOSHIELD_SHIELD_BUTTON,

        // Auto Technology templates
        AUTOTECH_RESEARCH_BUTTON,
        AUTOTECH_UPGRADE_BUTTON,

        // Button templates
        BUTTONS_BACK,
        BUTTONS_CLOSE,
        BUTTONS_CONFIRM,
        BUTTONS_OK,

        // Claim Mail templates
        CLAIMMAIL_CLAIM_ALL,
        CLAIMMAIL_MAIL_ICON,

        // Claim Missions templates
        CLAIMMISSIONS_CLAIM_BUTTON,
        CLAIMMISSIONS_MISSIONS_BUTTON,

        // Collect VIP templates
        COLLECTVIP_CLAIM_BUTTON,
        COLLECTVIP_VIP_BUTTON,

        // Conquest templates
        CONQUEST_ATTACK_BUTTON,
        CONQUEST_CONQUEST_BUTTON,

        // Farming templates
        FARMING_FARM_BUTTON,
        FARMING_RESOURCE_NODE,

        // Auto Rally templates
        AUTO_RALLY_RALLY_BUTTON,
        AUTO_RALLY_WAR_ICON,

        // Troop Training templates
        TROOPTRAINING_TRAIN_BUTTON,
        TROOPTRAINING_TROOPS_ICON,

        // Recovery templates
        RECOVERY_HOME_BUTTON,

        // Locator templates
        LOCATOR_MAP_BUTTON
    }

    /// <summary>
    /// Extension methods and utilities for GameTemplates enum.
    /// </summary>
    public static class GameTemplatesExtensions
    {
        private static readonly string _templatesBasePath = Path.Combine(AppContext.BaseDirectory, "templates", "images");

        /// <summary>
        /// Gets the full file path for a template.
        /// </summary>
        /// <param name="template">The template enum value</param>
        /// <returns>Full file path to the template image</returns>
        public static string GetTemplatePath(this GameTemplates template)
        {
            return template switch
            {
                // Startup templates
                GameTemplates.STARTUP_CONFIRM_WELCOME => Path.Combine(_templatesBasePath, "startup", "confirm-welcome.png"),
                GameTemplates.STARTUP_KINGSHOT_APP => Path.Combine(_templatesBasePath, "startup", "kingshot-app.png"),

                // Account management
                GameTemplates.CHANGE_ACCOUNT_SWITCH => Path.Combine(_templatesBasePath, "ChangeAccount", "switch-account.png"),

                // Alliance templates
                GameTemplates.ALLIANCE_BUTTON => Path.Combine(_templatesBasePath, "alliance", "alliance-button.png"),
                GameTemplates.ALLIANCE_TECHNOLOGY => Path.Combine(_templatesBasePath, "alliancetech", "alliance-tech.png"),
                GameTemplates.ALLIANCE_HELP => Path.Combine(_templatesBasePath, "alliance", "help-button.png"),
                GameTemplates.ALLIANCE_WAR => Path.Combine(_templatesBasePath, "alliance", "war-button.png"),

                // Auto Build templates
                GameTemplates.AUTOBUILD_BACK_BUTTON => Path.Combine(_templatesBasePath, "autobuild", "back-button.png"),
                GameTemplates.AUTOBUILD_GO_BUTTON => Path.Combine(_templatesBasePath, "autobuild", "go-button.png"),
                GameTemplates.AUTOBUILD_KITCHEN => Path.Combine(_templatesBasePath, "autobuild", "kitchen.png"),
                GameTemplates.AUTOBUILD_PERSON_HEAD => Path.Combine(_templatesBasePath, "autobuild", "person-head.png"),
                GameTemplates.AUTOBUILD_QUICK_USE_BUTTON => Path.Combine(_templatesBasePath, "autobuild", "quick-use-button.png"),
                GameTemplates.AUTOBUILD_RED_X => Path.Combine(_templatesBasePath, "autobuild", "red-x.png"),
                GameTemplates.AUTOBUILD_SIDE_MENU => Path.Combine(_templatesBasePath, "autobuild", "side-menu.png"),
                GameTemplates.AUTOBUILD_SPEEDUP_BUTTON => Path.Combine(_templatesBasePath, "autobuild", "speedup-button.png"),
                GameTemplates.AUTOBUILD_TRAIN_ICON => Path.Combine(_templatesBasePath, "autobuild", "train-icon.png"),
                GameTemplates.AUTOBUILD_UPGRADE_BUTTON => Path.Combine(_templatesBasePath, "autobuild", "upgrade-button.png"),
                GameTemplates.AUTOBUILD_UPGRADE_ICON => Path.Combine(_templatesBasePath, "autobuild", "upgrade-icon.png"),
                GameTemplates.AUTOBUILD_USE_BUTTON => Path.Combine(_templatesBasePath, "autobuild", "use-button.png"),

                // Auto Claim Hero templates
                GameTemplates.AUTOCLAIMHERO_BACK_ARROW => Path.Combine(_templatesBasePath, "autoclaimhero", "back-arrow.png"),
                GameTemplates.AUTOCLAIMHERO_FREE_BUTTON => Path.Combine(_templatesBasePath, "autoclaimhero", "free-button.png"),
                GameTemplates.AUTOCLAIMHERO_HERO_BUTTON => Path.Combine(_templatesBasePath, "autoclaimhero", "hero-button.png"),
                GameTemplates.AUTOCLAIMHERO_RECRUIT_HEROS_BUTTON => Path.Combine(_templatesBasePath, "autoclaimhero", "recruit-heros-button.png"),
                GameTemplates.AUTOCLAIMHERO_REWARDS => Path.Combine(_templatesBasePath, "autoclaimhero", "rewards.png"),

                // Auto Heal templates
                GameTemplates.AUTOHEAL_HEAL_BUTTON => Path.Combine(_templatesBasePath, "autoheal", "heal-button.png"),

                // Auto Hunt templates
                GameTemplates.AUTOHUNT_ATTACK_BUTTON => Path.Combine(_templatesBasePath, "autohunt", "attack-button.png"),
                GameTemplates.AUTOHUNT_HUNT_BUTTON => Path.Combine(_templatesBasePath, "autohunt", "hunt-button.png"),

                // Auto Shield templates
                GameTemplates.AUTOSHIELD_SHIELD_BUTTON => Path.Combine(_templatesBasePath, "autoshield", "shield-button.png"),

                // Auto Technology templates
                GameTemplates.AUTOTECH_RESEARCH_BUTTON => Path.Combine(_templatesBasePath, "autotech", "research-button.png"),
                GameTemplates.AUTOTECH_UPGRADE_BUTTON => Path.Combine(_templatesBasePath, "autotech", "upgrade-button.png"),

                // Button templates
                GameTemplates.BUTTONS_BACK => Path.Combine(_templatesBasePath, "buttons", "back.png"),
                GameTemplates.BUTTONS_CLOSE => Path.Combine(_templatesBasePath, "buttons", "close.png"),
                GameTemplates.BUTTONS_CONFIRM => Path.Combine(_templatesBasePath, "buttons", "confirm.png"),
                GameTemplates.BUTTONS_OK => Path.Combine(_templatesBasePath, "buttons", "ok.png"),

                // Claim Mail templates
                GameTemplates.CLAIMMAIL_CLAIM_ALL => Path.Combine(_templatesBasePath, "claimmail", "claim-all.png"),
                GameTemplates.CLAIMMAIL_MAIL_ICON => Path.Combine(_templatesBasePath, "claimmail", "mail-icon.png"),

                // Claim Missions templates
                GameTemplates.CLAIMMISSIONS_CLAIM_BUTTON => Path.Combine(_templatesBasePath, "claimmissions", "claim-button.png"),
                GameTemplates.CLAIMMISSIONS_MISSIONS_BUTTON => Path.Combine(_templatesBasePath, "claimmissions", "missions-button.png"),

                // Collect VIP templates
                GameTemplates.COLLECTVIP_CLAIM_BUTTON => Path.Combine(_templatesBasePath, "collectvip", "claim-button.png"),
                GameTemplates.COLLECTVIP_VIP_BUTTON => Path.Combine(_templatesBasePath, "collectvip", "vip-button.png"),

                // Conquest templates
                GameTemplates.CONQUEST_ATTACK_BUTTON => Path.Combine(_templatesBasePath, "conquest", "attack-button.png"),
                GameTemplates.CONQUEST_CONQUEST_BUTTON => Path.Combine(_templatesBasePath, "conquest", "conquest-button.png"),

                // Farming templates
                GameTemplates.FARMING_FARM_BUTTON => Path.Combine(_templatesBasePath, "farming", "farm-button.png"),
                GameTemplates.FARMING_RESOURCE_NODE => Path.Combine(_templatesBasePath, "farming", "resource-node.png"),

                // Auto Rally templates
                GameTemplates.AUTO_RALLY_RALLY_BUTTON => Path.Combine(_templatesBasePath, "Auto Rally", "rally-button.png"),
                GameTemplates.AUTO_RALLY_WAR_ICON => Path.Combine(_templatesBasePath, "Auto Rally", "war-icon.png"),

                // Troop Training templates
                GameTemplates.TROOPTRAINING_TRAIN_BUTTON => Path.Combine(_templatesBasePath, "trooptraining", "train-button.png"),
                GameTemplates.TROOPTRAINING_TROOPS_ICON => Path.Combine(_templatesBasePath, "trooptraining", "troops-icon.png"),

                // Recovery templates
                GameTemplates.RECOVERY_HOME_BUTTON => Path.Combine(_templatesBasePath, "recovery", "home-button.png"),

                // Locator templates
                GameTemplates.LOCATOR_MAP_BUTTON => Path.Combine(_templatesBasePath, "locator", "map-button.png"),

                _ => throw new ArgumentException($"Template path not defined for {template}")
            };
        }

        /// <summary>
        /// Gets the category (folder) name for a template.
        /// </summary>
        /// <param name="template">The template enum value</param>
        /// <returns>Category name</returns>
        public static string GetCategory(this GameTemplates template)
        {
            var name = template.ToString();
            var underscore = name.IndexOf('_');
            return underscore > 0 ? name.Substring(0, underscore).ToLower() : "misc";
        }

        /// <summary>
        /// Checks if a template file exists.
        /// </summary>
        /// <param name="template">The template enum value</param>
        /// <returns>True if the template file exists</returns>
        public static bool Exists(this GameTemplates template)
        {
            try
            {
                return File.Exists(template.GetTemplatePath());
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets all templates in a specific category.
        /// </summary>
        /// <param name="category">The category name</param>
        /// <returns>Array of templates in that category</returns>
        public static GameTemplates[] GetTemplatesInCategory(string category)
        {
            var allTemplates = Enum.GetValues<GameTemplates>();
            var result = new System.Collections.Generic.List<GameTemplates>();

            foreach (var template in allTemplates)
            {
                if (string.Equals(template.GetCategory(), category, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(template);
                }
            }

            return result.ToArray();
        }
    }
}