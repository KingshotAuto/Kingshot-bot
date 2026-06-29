using System.Drawing;

namespace Bot.Core.Models
{
    /// <summary>
    /// Centralized game UI coordinates to eliminate hardcoded values throughout the codebase
    /// </summary>
    public static class GameCoordinates
    {
        // Startup sequence coordinates
        public static readonly Point AdDismiss = new(614, 111);
        public static readonly Point WelcomeBackDismiss = new(370, 1011);
        
        // Troop training coordinates
        public static readonly Rectangle MenuTriggerArea = new(5, 522, 17, 52);        // x=5,y=522 & x=22,y=574
        public static readonly Rectangle ScrollArea = new(77, 343, 302, 510);          // Scroll bounds
        public static readonly Rectangle FirstTrainingBox = new(280, 527, 134, 128);   // Training box 1
        public static readonly Rectangle SecondTrainingBox = new(459, 704, 47, 62);    // Training box 2
        public static readonly Point BackButton = new(42, 36);
        
        // Scroll parameters
        public static readonly int MinScrollDistance = 500;
        
        // Common timeouts and thresholds
        public static class Timeouts
        {
            public const int DefaultImageWait = 10000;    // 10 seconds
            public const int QuickImageWait = 2000;       // 2 seconds
            public const int SideMenuWait = 2000;         // 2 seconds
            public const int TrainButtonWait = 2000;      // 2 seconds
        }
        
        public static class Thresholds
        {
            public const double StandardConfidence = 0.8;
            public const double HighConfidence = 0.8;
            public const double LowConfidence = 0.6;
            public const double EnhancedConfidence = 0.7;
        }
        
        public static class Delays
        {
            public const int AfterClick = 1000;
            public const int AfterCompletion = 2000;
            public const int AfterTraining = 1000;
            public const int BetweenTasks = 2000;
            public const int AfterAdClick = 1000;
            public const int BeforeAdClick = 1500;
            public const int SmallDelay = 1000;
            public const int MediumDelay = 2000;
            public const int QuickDelay = 500;
            public const int BetweenRetries = 1500;
            public const int AfterMenuClick = 1200;
            public const int AfterSettingAmount = 1000;
            public const int AfterConfirm = 1000;
            public const int BetweenErrorRetries = 2500;
        }
        
        /// <summary>
        /// Convert Rectangle to coordinate bounds for random clicking
        /// </summary>
        public static (int x1, int y1, int x2, int y2) ToBounds(this Rectangle rect)
        {
            return (rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height);
        }
        
        /// <summary>
        /// Get random point within rectangle bounds
        /// </summary>
        public static Point GetRandomPoint(this Rectangle rect, Random? random = null)
        {
            random ??= new Random();
            int x = random.Next(rect.X, rect.X + rect.Width + 1);
            int y = random.Next(rect.Y, rect.Y + rect.Height + 1);
            return new Point(x, y);
        }
        
        /// <summary>
        /// Get center point of rectangle
        /// </summary>
        public static Point GetCenter(this Rectangle rect)
        {
            return new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        }
    }
} 