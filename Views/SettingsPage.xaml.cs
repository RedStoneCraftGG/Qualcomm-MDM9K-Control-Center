using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace ModemController
{
    public sealed partial class SettingsPage : Page
    {
        private readonly AppSettings _settings = AppSettings.Current;
        private bool _loading;

        public SettingsPage()
        {
            InitializeComponent();
            Loaded += SettingsPage_Loaded;
        }

        private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            _loading = true;
            ToggleStartup.IsOn = StartupService.IsEnabled();
            _settings.StartAtStartup = ToggleStartup.IsOn;
            ToggleMinimizeToTray.IsOn = _settings.MinimizeToTray;
            ToggleCloseToTray.IsOn = _settings.CloseToTray;
            _loading = false;
        }

        private void ToggleStartup_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            bool enabled = ToggleStartup.IsOn;
            if (!StartupService.SetEnabled(enabled))
            {
                _loading = true;
                ToggleStartup.IsOn = StartupService.IsEnabled();
                _loading = false;
                return;
            }

            _settings.StartAtStartup = enabled;
            _settings.Save();
        }

        private void ToggleMinimizeToTray_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            _settings.MinimizeToTray = ToggleMinimizeToTray.IsOn;
            _settings.Save();
        }

        private void ToggleCloseToTray_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            _settings.CloseToTray = ToggleCloseToTray.IsOn;
            _settings.Save();
        }


        private async void BtnClearMessageHistory_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Clear local message history?",
                Content = "This permanently deletes the SMS conversation history saved on this PC. It does not delete messages currently stored in the modem.",
                PrimaryButtonText = "Clear History",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            BtnClearMessageHistory.IsEnabled = false;
            try
            {
                await SmsStorageService.Instance.ClearHistoryAsync();

                var resultDialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Message history cleared",
                    Content = "All locally stored SMS conversations have been deleted.",
                    CloseButtonText = "OK",
                    DefaultButton = ContentDialogButton.Close
                };

                await resultDialog.ShowAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SMS] Clear local history failed: {ex.Message}");

                var errorDialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Unable to clear message history",
                    Content = ex.Message,
                    CloseButtonText = "OK",
                    DefaultButton = ContentDialogButton.Close
                };

                await errorDialog.ShowAsync();
            }
            finally
            {
                BtnClearMessageHistory.IsEnabled = true;
            }
        }

        private async void BtnClearSms_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Clear SMS storage?",
                Content = "This will delete all SMS messages currently stored in the modem's SIM memory. This action cannot be undone.",
                PrimaryButtonText = "Clear",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            BtnClearSms.IsEnabled = false;
            try
            {
                bool success = await ModemAtService.Instance.ClearAllSmsStorageAsync();

                var resultDialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = success ? "SMS storage cleared" : "Unable to clear SMS storage",
                    Content = success
                        ? "All SMS messages stored in the modem were deleted."
                        : "The modem did not confirm that all SMS messages were deleted.",
                    CloseButtonText = "OK",
                    DefaultButton = ContentDialogButton.Close
                };

                await resultDialog.ShowAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SMS] Clear storage failed: {ex.Message}");

                var errorDialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Unable to clear SMS storage",
                    Content = ex.Message,
                    CloseButtonText = "OK",
                    DefaultButton = ContentDialogButton.Close
                };

                await errorDialog.ShowAsync();
            }
            finally
            {
                BtnClearSms.IsEnabled = true;
            }
        }
    }
}
