using CounterStrikeSharp.API.Core;
using System.Text.Json.Serialization;

namespace Sympho.Models
{
    public class Settings : BasePluginConfig
    {
        [JsonPropertyName("AllowYoutube")]
        public bool AllowYoutube { get; set; } = true;
        [JsonPropertyName("BypassYoutubeAdmin")]
        public bool BypassYoutubeAdmin { get; set; } = true;
        [JsonPropertyName("MaxAudioLength")]
        public int MaxAudioLength { get; set; } = 300; // Default increased to 5 minutes
        [JsonPropertyName("EnableAntiSpam")]
        public bool EnableAntiSpam { get; set; } = true;
        [JsonPropertyName("MaxSpamPerInterval")]
        public int MaxSpamPerInterval { get; set; } = 5;
        [JsonPropertyName("AntiSpamCooldown")]
        public float AntiSpamCooldown { get; set; } = 60.0f;
        
        [JsonPropertyName("TmpDir")]
        public string TmpDir { get; set; } = "sympho_tmp"; // Default name changed to be more specific
        [JsonPropertyName("TmpMaxAgeMinutes")]
        public int TmpMaxAgeMinutes { get; set; } = 120; // Default increased to 2 hours
        [JsonPropertyName("CacheMaxCount")]
        public int CacheMaxCount { get; set; } = 50;
        [JsonPropertyName("CacheReuseEnabled")]
        public bool CacheReuseEnabled { get; set; } = true;
        [JsonPropertyName("QueueEnabled")]
        public bool QueueEnabled { get; set; } = true; // Default changed to enabled
        [JsonPropertyName("MaxQueueLength")]
        public int MaxQueueLength { get; set; } = 10;
    }
}