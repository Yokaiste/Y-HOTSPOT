using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HotspotApp.Constants;

namespace HotspotApp.Config;

public static class ConfigManager
{
    public const int CurrentSchemaVersion = 1;
    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        AppConstants.AppName
    );
    private static readonly string ConfigPath = Path.Combine(
        AppDataFolder,
        AppConstants.ConfigFileName
    );

    public static string ConfigFilePath => ConfigPath;
    public static string ConfigFolderPath => AppDataFolder;

    public static async Task<HotspotConfig> LoadConfigAsync()
    {
        var config = CreateDefaultConfig();

        if (File.Exists(ConfigPath))
        {
            try
            {
                var encryptedBytes = await File.ReadAllBytesAsync(ConfigPath);
                var key = GetKey();
                var json = Decrypt(encryptedBytes, key);

                var loadedConfig = JsonSerializer.Deserialize<HotspotConfig>(json);
                if (loadedConfig != null)
                {
                    if (loadedConfig.SchemaVersion != CurrentSchemaVersion)
                    {
                        throw new InvalidOperationException(
                            $"Schema version mismatch. Expected {CurrentSchemaVersion}, got {loadedConfig.SchemaVersion}"
                        );
                    }
                    config = ValidateAndNormalize(loadedConfig);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Config load failed: {ex.Message}");
                throw;
            }
        }

        await SaveConfigAsync(config);
        return config;
    }

    public static async Task<HotspotConfig?> SaveConfigAsync(HotspotConfig config)
    {
        try
        {
            EnsureDirectory();
            var normalized = ValidateAndNormalize(config);
            var json = JsonSerializer.Serialize(normalized);
            var key = GetKey();
            var encryptedBytes = Encrypt(json, key);
            await WriteAtomicAsync(encryptedBytes);
            return normalized;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Config save failed: {ex.Message}");
            return null;
        }
    }

    public static void DeleteConfigFolder()
    {
        try
        {
            if (Directory.Exists(AppDataFolder))
            {
                Directory.Delete(AppDataFolder, true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Config folder delete failed: {ex.Message}");
        }
    }

    private static HotspotConfig CreateDefaultConfig()
    {
        return new HotspotConfig { SchemaVersion = CurrentSchemaVersion };
    }

    private static HotspotConfig ValidateAndNormalize(HotspotConfig config)
    {
        var normalized = CreateDefaultConfig();

        if (!string.IsNullOrWhiteSpace(config.Ssid))
        {
            normalized.Ssid = config.Ssid.Trim();
        }

        if (!string.IsNullOrWhiteSpace(config.Passphrase) && config.Passphrase.Length >= 8)
        {
            normalized.Passphrase = config.Passphrase;
        }

        if (Enum.IsDefined(config.Band))
        {
            normalized.Band = config.Band;
        }

        if (Enum.IsDefined(config.AuthenticationKind))
        {
            normalized.AuthenticationKind = config.AuthenticationKind;
        }

        if (config.MaxClientCount >= 1 && config.MaxClientCount <= 32)
        {
            normalized.MaxClientCount = config.MaxClientCount;
        }

        if (Enum.IsDefined(config.LogLevel))
        {
            normalized.LogLevel = config.LogLevel;
        }

        normalized.Channel = HotspotApp.Services.Infrastructure.ChannelRecoveryPolicy.IsSupportedChannel(config.Channel) ? config.Channel : 0;
        normalized.UseCustomDns = config.UseCustomDns;
        normalized.UseManualDns = config.UseManualDns;
        normalized.PreferredDns = (config.PreferredDns ?? "").Trim();
        normalized.AlternateDns = (config.AlternateDns ?? "").Trim();
        normalized.DisableAutoConfig = config.DisableAutoConfig;
        normalized.ForceLanFallback = config.ForceLanFallback;
        normalized.ApplySystemOptimizations = config.ApplySystemOptimizations;
        normalized.UpstreamAdapterId = config.UpstreamAdapterId;
        normalized.TargetAdapterId = config.TargetAdapterId;
        if (Enum.IsDefined(config.DisconnectMode))
        {
            normalized.DisconnectMode = config.DisconnectMode;
        }

        return normalized;
    }

    private static async Task WriteAtomicAsync(byte[] data)
    {
        var tempPath = Path.Combine(
            AppDataFolder,
            $"{AppConstants.ConfigFileName}.{Guid.NewGuid():N}.tmp"
        );
        await File.WriteAllBytesAsync(tempPath, data);
        File.Move(tempPath, ConfigPath, true);
    }

    private static void EnsureDirectory()
    {
        if (!Directory.Exists(AppDataFolder))
        {
            Directory.CreateDirectory(AppDataFolder);
        }
    }

    private static byte[] GetKey()
    {
        var keyBytes = Encoding.UTF8.GetBytes(ConfigKeyProvider.GetEncryptionKey());
        return SHA256.HashData(keyBytes);
    }

    private static byte[] Encrypt(string plainText, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using var ms = new MemoryStream();
        ms.Write(aes.IV); // Prepend IV

        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
        using (var sw = new StreamWriter(cs))
        {
            sw.Write(plainText);
        }
        return ms.ToArray();
    }

    private static string Decrypt(byte[] cipherText, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;

        using var ms = new MemoryStream(cipherText);
        var iv = new byte[aes.BlockSize / 8];
        var read = ms.Read(iv, 0, iv.Length);
        if (read < iv.Length)
            throw new InvalidOperationException("Invalid config file");

        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
        using var sr = new StreamReader(cs);
        return sr.ReadToEnd();
    }
}
