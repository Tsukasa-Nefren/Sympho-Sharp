using CounterStrikeSharp.API.Core;

namespace Sympho.Models
{
    public class Settings : BasePluginConfig
    {
        public bool AllowYoutube { get; set; } = true;
        public int MaxYoutubeLength { get; set; } = 7;
        public bool EnableAntiSpam { get; set; } = true;
        public int MaxSpamPerInterval { get; set; } = 10;
        public float SpamCheckInterval { get; set; } = 20.0f;
        public bool AntiSpamIgnoreAdmin { get; set; } = true;
    }
}
