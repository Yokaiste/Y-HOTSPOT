using System.Diagnostics;
using System.Globalization;
using System.Text;
using HotspotApp.Services.Infrastructure;
using HotspotApp.Services.UI;
using Microsoft.Extensions.DependencyInjection;

namespace HotspotApp.Localization;

public static class LanguagePreferences
{
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Y-HOTSPOT", "language.txt");

    public static string CurrentLanguage { get; private set; } = "en-US";

    public static void Initialize()
    {
        // A preferência visual é individual e não modifica a configuração do serviço.
        try
        {
            CurrentLanguage = Normalize(File.ReadAllText(PreferencePath, Encoding.UTF8).Trim());
        }
        catch (IOException) { CurrentLanguage = "en-US"; }
        catch (UnauthorizedAccessException) { CurrentLanguage = "en-US"; }

        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = CurrentLanguage;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(CurrentLanguage);
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture;
    }

    public static string Normalize(string? language) => language == "pt-BR" ? "pt-BR" : "en-US";

    public static void SaveAndRestart(string language)
    {
        language = Normalize(language);
        if (language == CurrentLanguage)
            return;
        if (App.Services.GetRequiredService<ManualHotspotSession>().OwnsSession)
            throw new InvalidOperationException(AppStrings.Get("Text_StopTestFirst"));

        Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
        var temporaryPath = PreferencePath + ".tmp";
        File.WriteAllText(temporaryPath, language, new UTF8Encoding(false));
        File.Move(temporaryPath, PreferencePath, true);

        // Reabrir somente a interface recarrega também x:Uid e o menu da bandeja.
        // O processo substituto aguarda a liberação do mutex; o serviço não é tocado.
        var start = new ProcessStartInfo(ProcessExecutablePathProvider.GetExecutablePath())
        {
            UseShellExecute = true,
            Arguments = $"--wait-for-ui-exit {Environment.ProcessId} --settings",
        };
        try
        {
            using var next = Process.Start(start)
                ?? throw new InvalidOperationException(AppStrings.Get("Text_LanguageChangeFailed"));
        }
        catch
        {
            File.WriteAllText(PreferencePath, CurrentLanguage, new UTF8Encoding(false));
            throw;
        }
        App.Services.GetRequiredService<TrayIconManager>().Dispose();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    public static bool WaitForPreviousUi(string[] args)
    {
        var index = Array.IndexOf(args, "--wait-for-ui-exit");
        if (index < 0)
            return true;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var processId))
            return false;
        try
        {
            using var previous = Process.GetProcessById(processId);
            return previous.WaitForExit(15000);
        }
        catch (ArgumentException) { return true; }
    }
}
