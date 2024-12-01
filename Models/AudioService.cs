namespace Sympho.Models
{
    public class AudioService
    {
        public AudioService()
        {
            AudioList = new List<CAudioConfig>();
            ConfigsLoaded = false;
            PluginDirectory = null;
        }

        public List<CAudioConfig>? AudioList { get; set; }
        public bool ConfigsLoaded { get; set; } = false;
        public string? PluginDirectory { get; set; }
    }
}
