using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;

namespace Sympho.Functions
{
    public class AntiSpamData
    {
        public static int PlayedCount = 0;
        public static float AvailableAgain = 0f;

        public static int GetPlayedCount()
        { 
            return PlayedCount; 
        }

        public static void SetPlayedCount(int value)
        {
            PlayedCount = value;
        }

        public static float GetCooldownLeft()
        {
            return AvailableAgain - Server.CurrentTime;
        }

        public static void SetCooldown(float value)
        {
            AvailableAgain = value;
        }
    }
}
