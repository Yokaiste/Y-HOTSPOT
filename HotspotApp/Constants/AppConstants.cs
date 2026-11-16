namespace HotspotApp.Constants;

public static class AppConstants
{
    public const string AppName = "Y-HOTSPOT";
    public const string Version = "2.0.0";
    public const string Author = "Yokaiste";
    public const string GithubUrl = "https://github.com/Yokaiste/Y-HOTSPOT";
    public const string ReleasesUrl = "https://github.com/Yokaiste/Y-HOTSPOT/releases";
    public static string AppDisplayName => $"{AppName} {Version}";

    public const string EncryptionKey = "5FF98DBA-C81F-4B6A-9E26-836E306AD66D";

    public static string VirtualAdapterName => AppDisplayName;

    public const int DefaultMaxClientCount = 8;

    public const int NetworkInitRetryCount = 10;
    public const int NetworkInitRetryDelayMs = 1000;

    // Files
    public const string ConfigFileName = "config.bin";
    public const string SystemConfigBackupFileName = "system_config_backup.json";
    public const string ServiceLogFileName = "service.log";
    public const string UiLogFileName = "ui.log";
}
