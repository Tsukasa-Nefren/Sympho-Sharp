using System.Runtime.InteropServices;

public unsafe static class AudioPlayer
{
    private static class NativeMethodsLinux
    {
        [DllImport("audioplayer.so", CallingConvention = CallingConvention.Cdecl)]
        public static extern void NativeSetPlayerHearing(int slot, bool hearing);

        [DllImport("audioplayer.so", CallingConvention = CallingConvention.Cdecl)]
        public static extern void NativeSetAllPlayerHearing(bool hearing);

        [DllImport("audioplayer.so", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool NativeIsHearing(int slot);

        [DllImport("audioplayer.so", CallingConvention = CallingConvention.Cdecl)]
        public static extern void NativeSetPlayerAudioBufferString(int slot, [MarshalAs(UnmanagedType.LPArray)] byte[] audioBuffer, int audioBufferSize, string audioPath, int audioPathSize);

        [DllImport("audioplayer.so", CallingConvention = CallingConvention.Cdecl)]
        public static extern void NativeSetAllAudioBufferString([MarshalAs(UnmanagedType.LPArray)] byte[] audioBuffer, int audioBufferSize, string audioPath, int audioPathSize);

        [DllImport("audioplayer.so", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool NativeIsPlaying(int slot);

        [DllImport("audioplayer.so", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool NativeIsAllPlaying();
    }

    private static class NativeMethodsWindows
    {
        [DllImport("audioplayer.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void NativeSetPlayerHearing(int slot, bool hearing);

        [DllImport("audioplayer.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void NativeSetAllPlayerHearing(bool hearing);

        [DllImport("audioplayer.dll", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool NativeIsHearing(int slot);

        [DllImport("audioplayer.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void NativeSetPlayerAudioBufferString(int slot, [MarshalAs(UnmanagedType.LPArray)] byte[] audioBuffer, int audioBufferSize, string audioPath, int audioPathSize);

        [DllImport("audioplayer.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void NativeSetAllAudioBufferString([MarshalAs(UnmanagedType.LPArray)] byte[] audioBuffer, int audioBufferSize, string audioPath, int audioPathSize);

        [DllImport("audioplayer.dll", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool NativeIsPlaying(int slot);

        [DllImport("audioplayer.dll", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool NativeIsAllPlaying();
    }

    /*
    * @param slot - player slot to set
    * @param hearing - whether player can hear
    */
    public static void SetPlayerHearing(int slot, bool hearing)
    {
        if(RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            NativeMethodsLinux.NativeSetPlayerHearing(slot, hearing);

        else
            NativeMethodsWindows.NativeSetPlayerHearing(slot, hearing);
    }

    /*
    * @param hearing - whether all players can hear
    */
    public static void SetAllPlayerHearing(bool hearing)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            NativeMethodsLinux.NativeSetAllPlayerHearing(hearing);

        else
            NativeMethodsWindows.NativeSetAllPlayerHearing(hearing);
    }

    /*
    * @param slot - player slot to get
    * @return whether player can hear
    */
    public static bool IsHearing(int slot)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return NativeMethodsLinux.NativeIsHearing(slot);

        return NativeMethodsWindows.NativeIsHearing(slot);
    }

    /*
    * @param slot - player slot to set
    * @param audioBuffer - buffer string, contains audio data (like mp3, wav), will be decoded to pcm by ffmpeg,
        pass empty string means stop playing
    */
    public static void SetPlayerAudioBuffer(int slot, byte[] audioBuffer)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            NativeMethodsLinux.NativeSetPlayerAudioBufferString(slot, audioBuffer, audioBuffer.Length, "", 0);

        else
            NativeMethodsWindows.NativeSetPlayerAudioBufferString(slot, audioBuffer, audioBuffer.Length, "", 0);
    }

    /*
    * @param slot - player slot to set
    * @param audioFile - audio file path, must be absolute path to a audio file (like mp3, wav),
        will be decoded to pcm by ffmpeg, pass empty string means stop playing
    */
    public static void SetPlayerAudioFile(int slot, string audioFile)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            NativeMethodsLinux.NativeSetPlayerAudioBufferString(slot, [], 0, audioFile, audioFile.Length);

        else
            NativeMethodsWindows.NativeSetPlayerAudioBufferString(slot, [], 0, audioFile, audioFile.Length);
    }

    /*
    * @param audioBuffer - buffer string, contains audio data (like mp3, wav), will be decoded to pcm by ffmpeg,
        pass empty string means stop playing
    */
    public static void SetAllAudioBuffer(byte[] audioBuffer)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            NativeMethodsLinux.NativeSetAllAudioBufferString(audioBuffer, audioBuffer.Length, "", 0);

        else
            NativeMethodsWindows.NativeSetAllAudioBufferString(audioBuffer, audioBuffer.Length, "", 0);
    }

    /*
    * @param audioFile - audio file path, must be absolute path to a audio file (like mp3, wav),
        will be decoded to pcm by ffmpeg, pass empty string means stop playing
    */
    public static void SetAllAudioFile(string audioFile)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            NativeMethodsLinux.NativeSetAllAudioBufferString([], 0, audioFile, audioFile.Length);

        else
            NativeMethodsWindows.NativeSetAllAudioBufferString([], 0, audioFile, audioFile.Length);
    }

    /*
    * @param slot - player slot to get
    * @return whether there are audio playing for a specific player
    */
    public static bool IsPlaying(int slot)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return NativeMethodsLinux.NativeIsPlaying(slot);

        return NativeMethodsWindows.NativeIsPlaying(slot);
    }

    /*
    * @return whether there are audio playing for all players
    */
    public static bool IsAllPlaying()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return NativeMethodsLinux.NativeIsAllPlaying();

        return NativeMethodsWindows.NativeIsAllPlaying();
    }
}