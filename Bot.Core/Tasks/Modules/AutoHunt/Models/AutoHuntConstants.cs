using System.Drawing;

namespace Bot.Core.Tasks.Modules.AutoHunt.Models
{
    /// <summary>
    /// Contains all UI coordinates, search areas, and constants used by the AutoHunt system
    /// </summary>
    public static class AutoHuntConstants
    {
        // UI element coordinates and search areas
        public static readonly Rectangle HuntButtonArea = new(618, 808, 92, 278);
        public static readonly Rectangle OcrPredictionArea = new(0, 516, 716, 45);
        public static readonly Rectangle MaxMarchClickArea = new(637, 340, 1, 1);
        public static readonly Rectangle VictoryDefeatClickArea = new(129, 1091, 459, 120);
        public static readonly Rectangle StaminaReturnClickArea = new(500, 105, 1, 1);
        public static readonly Rectangle MarchingTextArea = new(286, 860, 150, 50);  // Area to check for "Marching" text
        public static readonly Rectangle MarchCountArea = new(550, 68, 40, 25);      // Area to read march count from UI
        public static readonly Point CloseDetailsButton = new(337, 1049);  // Point to click to close details
        public static readonly Point ReturnToHuntButton = new(347, 1052);  // Point to click to return to hunt mode after marching
        public static readonly Rectangle DeployButtonArea = new(388, 1159, 315, 107);  // From 388,1159 to 703,1266
        public static readonly Point TickConfirmPoint = new(358, 1099);  // Point to click after processing a tick
        public static readonly Rectangle QuickDeployArea = new(35, 1151, 310, 103);  // Area for quick-deploy detection (35,1151 to 345,1254)
        public static readonly Rectangle StaminaArea = new(530, 11, 56, 57);  // Fixed area where stamina icon appears
        public static readonly Rectangle CompassArea = new(56, 1178, 70, 56);  // Area for compass icon
        // Castle-hunt detection uses the full target search area (no specific location restriction)
        public static readonly Rectangle TargetSearchArea = new(0, 110, 700, 1050);  // Area to search for all targets
        public static readonly Rectangle LevelUpArea = new(248, 208, 16, 64);  // Area to check for level-up image
        public static readonly Point LevelUpDismissPoint = new(384, 1103);  // Point to click to dismiss level-up

        // Configuration constants
        public const int TARGET_AREA_PADDING = 15;  // 15 pixels of padding around the actual icon to ensure complete blocking

        // Timeout constants
        public const int DEFAULT_WAIT_TIMEOUT = 5000;
        public const int HUNT_BUTTON_TIMEOUT = 2000;
        public const int MAP_VIEW_STABILIZE_DELAY = 2000;

        // Template matching thresholds
        public const double DEFAULT_TEMPLATE_THRESHOLD = 0.6;
        public const double HIGH_CONFIDENCE_THRESHOLD = 0.8;
        public const double LOW_CONFIDENCE_THRESHOLD = 0.5;

        // Scout cooldown settings
        public const int SCOUT_COOLDOWN_MINUTES = 5;

        // Visual debugging settings
        public const string DEBUG_SCREENSHOT_FOLDER = "autohunt-blocking";
    }
}