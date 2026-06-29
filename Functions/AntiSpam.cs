namespace Sympho.Functions;

public static class AntiSpamData
{
    private static int _playedCount;
    private static float _availableAgain;

    public static int GetPlayedCount() => _playedCount;

    public static void SetPlayedCount(int value) => _playedCount = value;

    public static float GetCooldownLeft(float now) => _availableAgain - now;

    public static void SetCooldown(float value) => _availableAgain = value;
}
