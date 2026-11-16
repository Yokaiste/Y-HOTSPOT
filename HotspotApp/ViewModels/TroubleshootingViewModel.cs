using System.Collections.ObjectModel;
using HotspotApp.Localization;
using HotspotApp.Models;
using HotspotApp.Services.Core;
using HotspotApp.Services.Infrastructure;
using HotspotApp.Services.UI;
using Microsoft.Extensions.Logging;

namespace HotspotApp.ViewModels;

public sealed partial class TroubleshootingViewModel(
    HotspotOptimizer optimizer,
    ServiceStateManager serviceStateManager,
    WslDnsRepairService wslDnsRepair,
    DialogService dialogService,
    ILogger<TroubleshootingViewModel> logger
) : ViewModelBase, ViewModelBase.IInitializable
{
    public ObservableCollection<TroubleshootingItem> Items { get; } = [];

    public Task InitializeAsync()
    {
        if (Items.Count > 0)
        {
            return Task.CompletedTask;
        }

        Items.Add(CreateItem("Text_WslDnsTitle", "Text_WslDnsDescription", RepairWslDnsAsync));
        Items.Add(
            CreateItem(
                "Text_TroubleshootingRemoveServiceTitle",
                "Text_TroubleshootingRemoveServiceDescription",
                RemoveServiceAsync
            )
        );
        Items.Add(
            CreateItem(
                "Text_TroubleshootingRemoveAutostartTitle",
                "Text_TroubleshootingRemoveAutostartDescription",
                RemoveAutostartAsync
            )
        );
        Items.Add(
            CreateItem(
                "Text_TroubleshootingRemoveAdapterTitle",
                "Text_TroubleshootingRemoveAdapterDescription",
                RemoveAdapterAsync
            )
        );
        Items.Add(
            CreateItem(
                "Text_TroubleshootingRestoreBackupTitle",
                "Text_TroubleshootingRestoreBackupDescription",
                optimizer.RestoreSystemSettingsAsync
            )
        );
        Items.Add(
            CreateItem(
                "Text_TroubleshootingEnableAutoConfigTitle",
                "Text_TroubleshootingEnableAutoConfigDescription",
                optimizer.EnableWirelessAutoConfigAsync
            )
        );
        Items.Add(
            CreateItem(
                "Text_TroubleshootingResetRegistryTitle",
                "Text_TroubleshootingResetRegistryDescription",
                optimizer.ResetHotspotRegistrySettingsAsync
            )
        );
        Items.Add(
            CreateItem(
                "Text_TroubleshootingResetAdapterPowerTitle",
                "Text_TroubleshootingResetAdapterPowerDescription",
                optimizer.ResetAdapterPowerSettingsAsync
            )
        );
        Items.Add(
            CreateItem(
                "Text_TroubleshootingResetTcpTitle",
                "Text_TroubleshootingResetTcpDescription",
                optimizer.ResetTcpGlobalSettingsAsync
            )
        );

        return Task.CompletedTask;
    }

    private async Task RemoveServiceAsync()
    {
        await serviceStateManager.ApplyAsync(false);
    }

    private async Task RepairWslDnsAsync()
    {
        if (!await dialogService.ShowWslDnsRepairConfirmationAsync()) return;
        await wslDnsRepair.RepairAsync();
        var health = await HotspotDnsHealth.CheckAsync();
        await dialogService.ShowWslDnsRepairResultAsync(wslDnsRepair.DirectoryPath,
            HotspotDnsHealth.Describe(health));
    }

    private Task RemoveAutostartAsync()
    {
        serviceStateManager.RemoveAutostart();
        return Task.CompletedTask;
    }

    private Task RemoveAdapterAsync()
    {
        serviceStateManager.RemoveAdapter();
        return Task.CompletedTask;
    }

    private TroubleshootingItem CreateItem(
        string titleKey,
        string descriptionKey,
        Func<Task> action
    )
    {
        return new TroubleshootingItem
        {
            Title = AppStrings.Get(titleKey),
            Description = AppStrings.Get(descriptionKey),
            FixLabel = AppStrings.Get("Text_TroubleshootingFixButton"),
            FixCommand = new AsyncRelayCommand(() => RunFixAsync(action)),
        };
    }

    private async Task RunFixAsync(Func<Task> action)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            LogTroubleshootingFixFailed(ex);
            await dialogService.ShowErrorAsync(
                AppStrings.Format("Text_ServiceErrorFormat", ex.Message)
            );
        }
        finally
        {
            IsBusy = false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "RemoveAdapterFailed")]
    private partial void LogRemoveAdapterFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "TroubleshootingFixFailed")]
    private partial void LogTroubleshootingFixFailed(Exception ex);
}
