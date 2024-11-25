using Microsoft.Extensions.Logging;

namespace Sympho
{
    public partial class Sympho
    {
        public void AudioCommandCheck(string command, bool specific, int soundIndex = -1)
        {
            if (!configsLoaded)
                return;

            // null or there is no audio in list.
            if (audioList == null || audioList.Count <= 0) return;

            // index always start with 0 but since we receive command from player who start count '1'
            if (specific)
                soundIndex -= 1;

            // if not specific let's assume as 0 first.
            else
                soundIndex = 0;

            // get an main array index of 'audioList'
            var index = GetAudioIndex(command);

            // -1 mean not found
            if (index == -1) return;

            // in case there is only 1 sound
            if (audioList[index].sounds!.Count <= 1)
            {
                soundIndex = 0;
            }

            // if has a lot
            else
            {
                // if not specific then random number!
                if (!specific)
                {
                    var random = new Random();
                    soundIndex = random.Next(0, audioList[index].sounds!.Count - 1);
                }
            }

            // combine path of sound file
            var soundPath = Path.Combine(ModuleDirectory, $"sounds\\{audioList[index].sounds![soundIndex]}");
            PlayAudio(soundPath);
        }

        public void PlayAudio(string path)
        {
            if (!File.Exists(path))
            {
                _logger.LogWarning("File is not found : {0}", path);
                return;
            }

            AudioPlayer.SetAllAudioFile(path);
        }
    }
}
