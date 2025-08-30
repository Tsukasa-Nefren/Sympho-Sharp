using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

public unsafe static class Audio
{
  public delegate void PlayStartHandler(int slot);
  public delegate void PlayEndHandler(int slot);
  public delegate void PlayHandler(int slot, int progress);

  private static class NativeMethods
  {
    [DllImport("audio", CallingConvention = CallingConvention.Cdecl)]
    public static extern void NativeSetPlayerHearing(int slot, bool hearing);

    [DllImport("audio", CallingConvention = CallingConvention.Cdecl)]
    public static extern void NativeSetAllPlayerHearing(bool hearing);

    [DllImport("audio", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool NativeIsHearing(int slot);

    [DllImport("audio", CallingConvention = CallingConvention.Cdecl)]
    public static extern void NativePlayToPlayer(int slot, [MarshalAs(UnmanagedType.LPArray)] byte[] audioBuffer, int audioBufferSize, string audioPath, int audioPathSize, float volume = 1f);

    [DllImport("audio", CallingConvention = CallingConvention.Cdecl)]
    public static extern void NativePlay([MarshalAs(UnmanagedType.LPArray)] byte[] audioBuffer, int audioBufferSize, string audioPath, int audioPathSize, float volume = 1f);

    [DllImport("audio", CallingConvention = CallingConvention.Cdecl)]
    public static extern void NativeStopAllPlaying();

    [DllImport("audio", CallingConvention = CallingConvention.Cdecl)]
    public static extern void NativeStopPlaying(int slot);

    [DllImport("audio", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool NativeIsPlaying(int slot);

    [DllImport("audio", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool NativeIsAllPlaying();

    [DllImport("audio", CallingConvention = CallingConvention.Cdecl)]
    public static extern int NativeRegisterPlayStartListener([MarshalAs(UnmanagedType.FunctionPtr)] PlayStartHandler callback);

    [DllImport("audio", CallingConvention = CallingConvention.Cdecl)]
    public static extern void NativeUnregisterPlayStartListener(int id);

    [DllImport("audio", CallingConvention = CallingConvention.Cdecl)]
    public static extern int NativeRegisterPlayEndListener([MarshalAs(UnmanagedType.FunctionPtr)] PlayEndHandler callback);

    [DllImport("audio", CallingConvention = CallingConvention.Cdecl)]
    public static extern void NativeUnregisterPlayEndListener(int id);

    [DllImport("audio", CallingConvention = CallingConvention.Cdecl)]
    public static extern int NativeRegisterPlayListener([MarshalAs(UnmanagedType.FunctionPtr)] PlayHandler callback);

    [DllImport("audio", CallingConvention = CallingConvention.Cdecl)]
    public static extern void NativeUnregisterPlayListener(int id);


    [DllImport("audio", CallingConvention = CallingConvention.Cdecl)]
    public static extern void NativeSetPlayer(int slot);

    private static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
      if (libraryName == "audio")
      {
        return NativeLibrary.Load(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "audio.dll" : "audio.so", assembly, searchPath);
      }

      return IntPtr.Zero;
    }

    private static void SetDllImportResolver()
    {
      NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), DllImportResolver);
    }

    static NativeMethods()
    {
      SetDllImportResolver();
    }
  }

  private static Dictionary<PlayStartHandler, int> _PlayStartListeners = new Dictionary<PlayStartHandler, int>();
  private static Dictionary<PlayEndHandler, int> _PlayEndListeners = new Dictionary<PlayEndHandler, int>();
  private static Dictionary<PlayHandler, int> _PlayListeners = new Dictionary<PlayHandler, int>();
  
  public static void SetPlayerHearing(int slot, bool hearing)
  {
    NativeMethods.NativeSetPlayerHearing(slot, hearing);
  }

  public static void SetAllPlayerHearing(bool hearing)
  {
    NativeMethods.NativeSetAllPlayerHearing(hearing);
  }

  public static bool IsHearing(int slot)
  {
    return NativeMethods.NativeIsHearing(slot);
  }

  public static void PlayToPlayerFromBuffer(int slot, byte[] audioBuffer, float volume = 1f)
  {
    NativeMethods.NativePlayToPlayer(slot, audioBuffer, audioBuffer.Length, "", 0, volume);
  }

  public static void PlayToPlayerFromFile(int slot, string audioFile, float volume = 1f)
  {
    NativeMethods.NativePlayToPlayer(slot, [], 0, audioFile, audioFile.Length, volume);
  }

  public static void PlayFromBuffer(byte[] audioBuffer, float volume = 1f)
  {
    NativeMethods.NativePlay(audioBuffer, audioBuffer.Length, "", 0, volume);
  }

  public static void PlayFromFile(string audioFile, float volume = 1f)
  {
    NativeMethods.NativePlay([], 0, audioFile, audioFile.Length, volume);
  }

  public static void StopAllPlaying()
  {
    NativeMethods.NativeStopAllPlaying();
  }

  public static void StopPlaying(int slot)
  {
    NativeMethods.NativeStopPlaying(slot);
  }

  public static bool IsPlaying(int slot)
  {
    return NativeMethods.NativeIsPlaying(slot);
  }

  public static bool IsAllPlaying()
  {
    return NativeMethods.NativeIsAllPlaying();
  }

  public static int RegisterPlayStartListener(PlayStartHandler handler)
  {
    var id = NativeMethods.NativeRegisterPlayStartListener(handler);
    _PlayStartListeners[handler] = id;
    return id;
  }

  public static void UnregisterPlayStartListener(PlayStartHandler handler)
  {
    if (_PlayStartListeners.TryGetValue(handler, out var listenerId))
    {
        NativeMethods.NativeUnregisterPlayStartListener(listenerId);
        _PlayStartListeners.Remove(handler);
    }
  }

  public static int RegisterPlayEndListener(PlayEndHandler handler)
  {
    var id = NativeMethods.NativeRegisterPlayEndListener(handler);
    _PlayEndListeners[handler] = id;
    return id;
  }

  public static void UnregisterPlayEndListener(PlayEndHandler handler)
  {
    if (_PlayEndListeners.TryGetValue(handler, out var listenerId))
    {
        NativeMethods.NativeUnregisterPlayEndListener(listenerId);
        _PlayEndListeners.Remove(handler);
    }
  }
  public static int RegisterPlayListener(PlayHandler handler)
  {
    var id = NativeMethods.NativeRegisterPlayListener(handler);
    _PlayListeners[handler] = id;
    return id;
  }
  public static void UnregisterPlayListener(PlayHandler handler)
  {
    if (_PlayListeners.TryGetValue(handler, out var listenerId))
    {
        NativeMethods.NativeUnregisterPlayListener(listenerId);
        _PlayListeners.Remove(handler);
    }
  }

  public static void SetPlayer(int slot)
  {
    NativeMethods.NativeSetPlayer(slot);
  }
}