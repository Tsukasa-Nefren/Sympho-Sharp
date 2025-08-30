namespace Sympho.Models
{
    /// <summary>
    /// Represents information about a single available subtitle track.
    /// </summary>
    public class SubtitleInfo
    {
        /// <summary>
        /// The language code for the subtitle (e.g., "ko", "en").
        /// </summary>
        public required string LangCode { get; set; }

        /// <summary>
        /// The display name of the language (e.g., "Korean", "English").
        /// </summary>
        public required string LanguageName { get; set; }
    }
}