using System;

namespace Bot.Core.Exceptions
{
    public class BotLostException : Exception
    {
        public bool NeedsRecovery { get; set; }

        public BotLostException() : base() { }
        public BotLostException(string message) : base(message)
        {
            NeedsRecovery = false; // Default to false
        }
        public BotLostException(string message, Exception innerException) : base(message, innerException)
        {
            NeedsRecovery = false;
        }
    }
} 