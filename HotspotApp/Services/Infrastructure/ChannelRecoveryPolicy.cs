namespace HotspotApp.Services.Infrastructure;

public static class ChannelRecoveryPolicy
{
    // Canais primários de 20 MHz; o driver ainda decide a permissão regulatória/DFS.
    public static readonly IReadOnlyList<int> FiveGhzChannels = Array.AsReadOnly(new[]
        { 36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161, 165 });
    public static bool IsSupportedChannel(int channel) => channel is >= 0 and <= 13 || FiveGhzChannels.Contains(channel);
    public static int DriverBand(int channel) => FiveGhzChannels.Contains(channel) ? 1 : 0;

    // The network class also contains protected metadata (for example Properties).
    // Only four ASCII-digit subkeys represent adapter instances.
    public static bool IsAdapterInstanceKey(string name) => name.Length == 4
        && name.All(char.IsAsciiDigit);
    // A partial apply/restore may contain either our requested value or the original.
    // Any third value belongs to an unknown change and blocks the entire rollback.
    public static bool CanRestore(IReadOnlyList<int?> original, IReadOnlyList<int?> current,
        IReadOnlyList<int> requested) => original.Count == 3 && current.Count == 3 && requested.Count == 3
        && Enumerable.Range(0, 3).All(i => current[i] == original[i] || current[i] == requested[i]);
}
