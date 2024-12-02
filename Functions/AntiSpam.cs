using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;

namespace Sympho.Functions
{
    public class AntiSpamData
    {
        public static int PlayedCount = 0;
        public static bool BlockPlay = false;

        public static int GetPlayedCount()
        { 
            return PlayedCount; 
        }

        public static void SetPlayedCount(int value)
        {
            PlayedCount = value;
        }

        public static bool GetBlockStatus()
        {
            return BlockPlay;
        }

        public static void SetBlock(bool value)
        {
            BlockPlay = value;
        }
    }
}
