using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.UI.Xaml;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace ModemController
{
    public partial class App : Application
    {
        private MainWindow? m_window;
        private bool _notificationsRegistered;

        public App()
        {
            this.InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            RegisterNotifications();

            var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activatedArgs.Kind == ExtendedActivationKind.AppNotification &&
                activatedArgs.Data is AppNotificationActivatedEventArgs notificationArgs)
            {
                _ = HandleNotificationAsync(notificationArgs);
                return;
            }

            ShowMainWindow();
        }

        private void RegisterNotifications()
        {
            if (_notificationsRegistered)
                return;

            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += NotificationManager_NotificationInvoked;
            manager.Register();
            _notificationsRegistered = true;
        }

        private void NotificationManager_NotificationInvoked(
            AppNotificationManager sender,
            AppNotificationActivatedEventArgs args)
        {
            _ = HandleNotificationAsync(args);
        }

        private async Task HandleNotificationAsync(AppNotificationActivatedEventArgs args)
        {
            try
            {
                if (args.Arguments.TryGetValue("action", out string? action) &&
                    string.Equals(action, "replySms", StringComparison.OrdinalIgnoreCase))
                {
                    if (!args.Arguments.TryGetValue("targetNumber", out string? targetNumber) ||
                        string.IsNullOrWhiteSpace(targetNumber))
                    {
                        return;
                    }

                    string reply = string.Empty;
                    if (args.UserInput.ContainsKey("replyBox"))
                        reply = args.UserInput["replyBox"]?.ToString()?.Trim() ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(reply))
                        return;

                    bool sent = await ModemAtService.Instance
                        .SendSmsAsync(targetNumber, reply);

                    System.Diagnostics.Debug.WriteLine(
                        $"[SMS] Notification reply {(sent ? "sent" : "failed")} to {targetNumber}");

                    return;
                }

                ShowMainWindow();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Notification activation failed: {ex.Message}");
            }
        }

        public void NavigateToContacts(string? address = null)
        {
            ShowMainWindow();
            m_window?.NavigateToContacts(address);
        }

        private void ShowMainWindow()
        {
            bool startupLaunch = Environment.GetCommandLineArgs()
                .Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));

            if (m_window == null)
                m_window = new MainWindow();

            m_window.Activate();
            m_window.ApplyStartupVisibility(startupLaunch);
        }
    }
}
