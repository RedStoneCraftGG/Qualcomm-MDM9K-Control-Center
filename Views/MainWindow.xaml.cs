using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace ModemController
{
    public sealed partial class MainWindow : Window
    {
        private readonly AppWindow? _appWindow;
        private readonly OverlappedPresenter? _presenter;
        private readonly TrayService _trayService;
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
                ApplyWindowIcon();

                if (_presenter != null)
                {
                    _presenter.IsResizable = false;
                    _presenter.IsMaximizable = false;
                }
            }

            _trayService = new TrayService(ShowFromTray, ExitApplication);

            NavView.SelectedItem = NavDashboard;
            SmsMonitorService.Start();
        }

        private void ApplyWindowIcon()
        {
            if (_appWindow == null || !AppIcon.TryGetIcoPath(out string iconPath))
                return;

            _appWindow.SetIcon(iconPath);
        }

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (!_hiddenToTray &&
                AppSettings.Current.MinimizeToTray &&
                _presenter?.State == OverlappedPresenterState.Minimized)
            {
                HideToTray();
            }
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_allowClose)
                return;

            if (AppSettings.Current.CloseToTray)
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
            if (startupLaunch && AppSettings.Current.MinimizeToTray)
                HideToTray();
        }

        public void ExitApplication()
        {
            _allowClose = true;
            _trayService.Hide();
            _trayService.Dispose();
            SmsMonitorService.Stop();
            _appWindow?.Destroy();
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            SmsMonitorService.Stop();
            _trayService.Dispose();
        }

        public void NavigateToContacts(string? address = null)
        {
            // Keep the NavigationView selection synchronized with the page being
            // displayed. Navigating directly from SmsPage via Frame.Navigate()
            // changes the content but leaves the SMS item selected, which makes
            // the navigation UI appear stuck on SMS.
            NavView.SelectedItem = NavContacts;
            ContentFrame.Navigate(typeof(ContactsPage), address);
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
                    ContentFrame.Navigate(typeof(DashboardPage));
                    break;
                case "sms":
                    ContentFrame.Navigate(typeof(SmsPage));
                    break;
                case "contacts":
                    ContentFrame.Navigate(typeof(ContactsPage));
                    break;
                case "settings":
                    ContentFrame.Navigate(typeof(SettingsPage));
                    break;
                case "about":
                    ContentFrame.Navigate(typeof(AboutPage));
                    break;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
    }
}
