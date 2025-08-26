using CounterStrikeSharp.API.Core;

namespace Sympho.Models
{
    public class Settings : BasePluginConfig
    {
        public bool AllowYoutube { get; set; } = true;
        public bool BypassYoutubeAdmin { get; set; } = true;
        public int MaxAudioLength { get; set; } = 7;
        public bool EnableAntiSpam { get; set; } = true;
        public int MaxSpamPerInterval { get; set; } = 10;
        public float SpamCheckInterval { get; set; } = 20.0f;
        public float AntiSpamCooldown { get; set; } = 60.0f;
        
        public string TmpDir { get; set; } = "temp";
        public int TmpMaxAgeMinutes { get; set; } = 30;
        public int CacheMaxCount { get; set; } = 100;
        public bool CacheReuseEnabled { get; set; } = true;
        public bool QueueEnabled { get; set; } = false;
        public int MaxQueueLength { get; set; } = 10;
    }
}