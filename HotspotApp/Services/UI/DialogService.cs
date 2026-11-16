using System.Runtime.InteropServices.WindowsRuntime;
using HotspotApp.Constants;
using HotspotApp.Helpers;
using HotspotApp.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace HotspotApp.Services.UI;

public sealed class DialogService
{
    private XamlRoot? _xamlRoot;

    public void SetXamlRoot(XamlRoot xamlRoot)
    {
        _xamlRoot = xamlRoot;
    }

    public async Task ShowErrorAsync(string message)
    {
        if (_xamlRoot == null)
        {
            return;
        }

        ContentDialog dialog = new()
        {
            XamlRoot = _xamlRoot,
            Title = AppStrings.Get("Text_ErrorDialogTitle"),
            Content = message,
            CloseButtonText = AppStrings.Get("Text_ErrorDialogCloseButtonText"),
        };

        await dialog.ShowAsync();
    }

    public async Task<bool> ShowManualChannelWarningAsync()
    {
        if (_xamlRoot == null) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = _xamlRoot,
            Title = AppStrings.Get("Text_ChannelWarningTitle"),
            Content = AppStrings.Get("Text_ChannelWarningContent"),
            PrimaryButtonText = AppStrings.Get("Text_ChannelWarningContinue"),
            CloseButtonText = AppStrings.Get("Text_ChannelAuto"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task<bool> ShowResetConfirmationAsync()
    {
        if (_xamlRoot == null)
        {
            return false;
        }

        ContentDialog dialog = new()
        {
            XamlRoot = _xamlRoot,
            Title = AppStrings.Get("Text_ResetDialogTitle"),
            Content = AppStrings.Get("Text_ResetDialogContent"),
            PrimaryButtonText = AppStrings.Get("Text_ResetDialogPrimaryButtonText"),
            CloseButtonText = AppStrings.Get("Text_ResetDialogCancelButtonText"),
            DefaultButton = ContentDialogButton.Close,
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public async Task<bool> ShowWslDnsRepairConfirmationAsync(bool dnsFailed = false)
    {
        if (_xamlRoot == null) return false;
        ContentDialog dialog = new()
        {
            XamlRoot = _xamlRoot,
            Title = AppStrings.Get("Text_WslDnsTitle"),
            Content = (dnsFailed ? AppStrings.Get("Text_HotspotDnsFailed") + "\n\n" : "")
                + AppStrings.Get("Text_WslDnsConfirmation") + "\n\n"
                + AppStrings.Get("Text_HotspotRecoveredChannel"),
            PrimaryButtonText = AppStrings.Get("Text_WslDnsConfirmButton"),
            CloseButtonText = AppStrings.Get("Text_ResetDialogCancelButtonText"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task ShowWslDnsRepairResultAsync(string logDirectory, string? verification = null)
    {
        if (_xamlRoot == null) return;
        ContentDialog dialog = new()
        {
            XamlRoot = _xamlRoot,
            Title = AppStrings.Get("Text_WslDnsTitle"),
            Content = AppStrings.Format("Text_WslDnsCompleted", logDirectory)
                + (verification == null ? "" : "\n\n" + verification),
            CloseButtonText = AppStrings.Get("Text_ErrorDialogCloseButtonText"),
        };
        await dialog.ShowAsync();
    }

    public async Task ShowHotspotQrAsync(string ssid, string passphrase)
    {
        if (_xamlRoot == null)
        {
            return;
        }

        var bytes = QrCodeHelper.GetWifiQrBytes(ssid, passphrase);

        var imageSource = new BitmapImage();
        using (var stream = new InMemoryRandomAccessStream())
        {
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            await imageSource.SetSourceAsync(stream);
        }

        var image = new Image
        {
            Width = 240,
            Height = 240,
            Stretch = Stretch.Uniform,
            Source = imageSource,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var copyButton = new Button
        {
            Content = AppStrings.Get("Text_QrDialogCopyImageButton"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = 240,
            CornerRadius = new CornerRadius(0),
            Margin = new Thickness(0, -12, 0, 0),
        };

        var ssidText = new TextBlock
        {
            Text = $"{AppStrings.Get("Text_SsidLabel")}: {ssid}",
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

        var passText = new TextBlock
        {
            Text = $"{AppStrings.Get("Text_PasswordLabel")}: {passphrase}",
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Margin = new Thickness(0, -12, 0, 0),
        };

        copyButton.Click += async (s, e) =>
        {
            try
            {
                var dataPackage = new DataPackage
                {
                    RequestedOperation = DataPackageOperation.Copy,
                };

                var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);

                dataPackage.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                Clipboard.SetContent(dataPackage);

                var originalContent = copyButton.Content;
                copyButton.Content = AppStrings.Get("Text_QrDialogCopiedButton");
                await Task.Delay(2000);
                copyButton.Content = originalContent;
            }
            catch { }
        };

        var content = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        content.Children.Add(image);
        content.Children.Add(copyButton);
        content.Children.Add(ssidText);
        content.Children.Add(passText);

        ContentDialog dialog = new()
        {
            XamlRoot = _xamlRoot,
            Content = content,
            CloseButtonText = AppStrings.Get("Text_QrDialogCloseButtonText"),
            DefaultButton = ContentDialogButton.Close,
        };

        await dialog.ShowAsync();
    }

    public async Task ShowAboutAsync()
    {
        if (_xamlRoot == null)
        {
            return;
        }

        var stack = new StackPanel { Spacing = 16, MinWidth = 300 };
        // The release belongs to the application; author credits remain separate.
        var version = new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(20, 12, 20, 12),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = AppStrings.Format("Text_AboutVersionFormat", AppConstants.Version),
                FontSize = 24,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            },
        };
        stack.Children.Add(version);
        stack.Children.Add(CreateAuthorCard(AppConstants.Author, "Text_AboutOriginalAuthor",
            (AppStrings.Get("Text_AboutGithubButton"), AppConstants.GithubUrl),
            (AppStrings.Get("Text_AboutReleasesButton"), AppConstants.ReleasesUrl)));
        stack.Children.Add(CreateAuthorCard("Thomas Vanini", "Text_AboutForkAuthor",
            ("Linktree", "https://linktr.ee/ThomasVanini")));

        ContentDialog dialog = new()
        {
            XamlRoot = _xamlRoot,
            Title = AppConstants.AppName,
            Content = stack,
            CloseButtonText = AppStrings.Get("Text_QrDialogCloseButtonText"),
            DefaultButton = ContentDialogButton.Close,
        };

        await dialog.ShowAsync();
    }

    private static Border CreateAuthorCard(string author, string roleKey,
        params (string Label, string Url)[] links)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = AppStrings.Format("Text_AboutAuthorFormat", author),
            FontSize = 19,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = AppStrings.Get(roleKey),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (label, url) in links)
            buttons.Children.Add(new HyperlinkButton { Content = label, NavigateUri = new Uri(url) });
        content.Children.Add(buttons);
        return new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Child = content,
        };
    }

    public async Task<bool> ShowConfigLoadErrorAsync()
    {
        if (_xamlRoot == null)
        {
            return false;
        }

        ContentDialog dialog = new()
        {
            XamlRoot = _xamlRoot,
            Title = AppStrings.Get("Text_ConfigLoadErrorTitle"),
            Content = AppStrings.Get("Text_ConfigLoadErrorMessage"),
            PrimaryButtonText = AppStrings.Get("Text_ConfigLoadErrorContinueButton"),
            SecondaryButtonText = AppStrings.Get("Text_ConfigLoadErrorExitButton"),
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }
}
