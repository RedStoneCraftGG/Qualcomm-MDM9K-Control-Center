using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace ModemWidgetApp
{
    public sealed partial class MainWindow : Window
    {
        private readonly AppWindow? _appWindow;
        private readonly OverlappedPresenter? _presenter;
        private readonly ModemController.TrayService _trayService;
        private readonly IntPtr _hwnd;
        private bool _allowClose;
        private bool _hiddenToTray;

        public MainWindow()
        {
            this.InitializeComponent();
            Closed += MainWindow_Closed;

            _hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _presenter = _appWindow.Presenter as OverlappedPresenter;

            if (_appWindow != null)
            {
                _appWindow.Resize(new Windows.Graphics.SizeInt32(440, 540));
                _appWindow.Changed += AppWindow_Changed;
                _appWindow.Closing += AppWindow_Closing;

                if (_presenter != null)
                {
                    _presenter.IsResizable = false;
                    _presenter.IsMaximizable = false;
                }
            }

            _trayService = new ModemController.TrayService(ShowFromTray, ExitApplication);

            NavView.SelectedItem = NavDashboard;
            ModemController.SmsMonitorService.Start();
        }

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (!_hiddenToTray &&
                ModemController.AppSettings.Current.MinimizeToTray &&
                _presenter?.State == OverlappedPresenterState.Minimized)
            {
                HideToTray();
            }
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_allowClose)
                return;

            if (ModemController.AppSettings.Current.CloseToTray)
            {
                args.Cancel = true;
                HideToTray();
            }
        }

        private void HideToTray()
        {
            _hiddenToTray = true;
            _trayService.Show();
            ShowWindow(_hwnd, SW_HIDE);
        }

        public void ShowFromTray()
        {
            // Keep the hidden-to-tray guard active while restoring.
            // Restore() can raise AppWindow.Changed while the presenter is still
            // transitioning from Minimized; clearing the guard too early would
            // immediately hide the window again.
            _hiddenToTray = true;
            _trayService.Hide();
            ShowWindow(_hwnd, SW_SHOW);
            _presenter?.Restore(false);
            Activate();
            _hiddenToTray = false;
        }

        public void ApplyStartupVisibility(bool startupLaunch)
        {
            if (startupLaunch && ModemController.AppSettings.Current.MinimizeToTray)
                HideToTray();
        }

        public void ExitApplication()
        {
            _allowClose = true;
            _trayService.Hide();
            _trayService.Dispose();
            ModemController.SmsMonitorService.Stop();
            _appWindow?.Destroy();
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            ModemController.SmsMonitorService.Stop();
            _trayService.Dispose();
        }

        public void NavigateToContacts(string? address = null)
        {
            // Keep the NavigationView selection synchronized with the page being
            // displayed. Navigating directly from SmsPage via Frame.Navigate()
            // changes the content but leaves the SMS item selected, which makes
            // the navigation UI appear stuck on SMS.
            NavView.SelectedItem = NavContacts;
            ContentFrame.Navigate(typeof(ModemController.ContactsPage), address);
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected) return;

            var selectedItem = args.SelectedItem as NavigationViewItem;
            if (selectedItem?.Tag == null) return;

            string pageTag = selectedItem.Tag.ToString() ?? string.Empty;

            switch (pageTag)
            {
                case "dashboard":
                    ContentFrame.Navigate(typeof(ModemController.DashboardPage));
                    break;
                case "sms":
                    ContentFrame.Navigate(typeof(ModemController.SmsPage));
                    break;
                case "contacts":
                    ContentFrame.Navigate(typeof(ModemController.ContactsPage));
                    break;
                case "settings":
                    ContentFrame.Navigate(typeof(ModemController.SettingsPage));
                    break;
                case "about":
                    ContentFrame.Navigate(typeof(ModemController.AboutPage));
                    break;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
    }
}
