using HotspotApp.Config;
using HotspotApp.Constants;

namespace HotspotApp.Logging;

public static class LogPaths
{
    private static readonly object WriteLock = new();
    // AppContext.BaseDirectory pode ser a pasta de extração do single-file.
    // Usar o caminho do processo mantém os logs ao lado do executável distribuído.
    public static string DirectoryPath => Path.GetDirectoryName(
        Services.Infrastructure.ProcessExecutablePathProvider.GetExecutablePath())
        ?? throw new InvalidOperationException("Diretório do executável indisponível.");

    public static string GetServiceLogPath() => Path.Combine(DirectoryPath, AppConstants.ServiceLogFileName);
    public static string GetUiLogPath() => Path.Combine(DirectoryPath, AppConstants.UiLogFileName);

    // Append content to a log file path, ensuring directory exists
    public static void Append(string path, string content)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            lock (WriteLock)
                File.AppendAllText(path, content, new System.Text.UTF8Encoding(false));
        }
        catch (Exception)
        {
            // Fail silently or maybe write to Debug
            System.Diagnostics.Debug.WriteLine($"Failed to write to log: {path}");
        }
    }
}
