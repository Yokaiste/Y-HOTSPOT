using HotspotApp.Localization;
using Microsoft.UI.Xaml;
using HotspotApp.Services.UI;
using HotspotApp.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace HotspotApp.Views;

public sealed partial class SettingsPage : ViewModelPage
{
    public SettingsViewModel ViewModel { get; }
    private readonly DialogService _dialogService;

    public SettingsPage()
    {
        InitializeComponent();
        _dialogService = App.Services.GetRequiredService<DialogService>();
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeViewModel(ViewModel);
        Unloaded += (_, _) => SetPasswordVisible(false);
    }

    public void ShowPassword_Click(object sender, RoutedEventArgs e)
        => SetPasswordVisible(ShowPasswordButton.IsChecked == true);

    private async void Channel_DropDownClosed(object sender, object e)
    {
        // Wait until TwoWay binding has committed the user's choice.
        await Task.Yield();
        await ViewModel.ConfirmManualChannelAsync();
    }

    private void SetPasswordVisible(bool visible)
    {
        // A regular text field allows explicit selection/copy; never copy automatically.
        HiddenPassword.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        VisiblePassword.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ShowPasswordButton.IsChecked = visible;
        ShowPasswordButton.Content = AppStrings.Get(visible ? "Text_HidePassword" : "Text_ShowPassword");
    }

    protected override async Task OnLoadedAsync()
    {
        _dialogService.SetXamlRoot(XamlRoot);
        await ViewModel.RefreshSessionStateAsync();
    }
}
