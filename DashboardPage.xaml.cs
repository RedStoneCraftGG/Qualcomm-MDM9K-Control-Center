using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Threading.Tasks;

namespace ModemController
{
    public sealed partial class DashboardPage : Page
    {
        private readonly ModemAtService _modemService = ModemAtService.Instance;
        private readonly RasService _rasService = RasService.Instance;
        private DispatcherTimer? _signalTimer;

        private bool _isRasConnected;
        private bool _isInitialWifiCheck = true;
        private bool _isInitialized;
        private bool _isRefreshingStatus;
        private bool _isRefreshingWifi;

        private int _lastSignalPercent;
        private string _lastNetworkState = "Checking...";
        private string _lastOperatorName = "Unknown";
        private string _lastNetworkType = string.Empty;
        private string _lastPortInfo = "Port: Not Found";
        private bool _hasStatusSnapshot;

        public DashboardPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;

            Loaded += DashboardPage_Loaded;
            Unloaded += DashboardPage_Unloaded;
        }

        private async void DashboardPage_Loaded(object sender, RoutedEventArgs e)
        {
            // WinUI dapat melepas visual tree saat berpindah halaman meskipun Page
            // masih di-cache. Selalu pulihkan snapshot terakhir ketika kembali.
            // SmsPage uses the legacy ModemService connection. Release it before
            // Dashboard opens the same COM port through ModemAtService.
            ModemService.Disconnect();

            RestoreCachedUi();

            StartSignalTimer();

            if (_isInitialized)
            {
                // Jangan scan port dan membuat profil ulang setiap kali kembali ke dashboard.
                // Cukup ambil status terbaru untuk mengisi kembali visual.
                await RefreshStatusAsync();
                await RefreshWifiStatusAsync();
                return;
            }

            await InitializeDashboardAsync();
        }

        private async Task InitializeDashboardAsync()
        {
            TxtOperatorName.Text = "Scanning Network...";

            var detectedPorts = await _modemService.ScanAndSetPortAsync();
            UpdatePortSnapshot();

            if (detectedPorts.Count > 0)
            {
                await RefreshWifiStatusAsync();
                await RefreshStatusAsync();
            }
            else
            {
                _lastSignalPercent = 0;
                _lastNetworkState = "Port not detected";
                _lastOperatorName = "No Device";
                _hasStatusSnapshot = true;
                RestoreCachedUi();
            }

            bool profileExists = await _rasService.IsProfileExistsAsync();
            if (profileExists)
            {
                TxtRasStatus.Text = "Status: Ready to Connect";
            }
            else
            {
                TxtRasStatus.Text = "Creating profile...";
                var result = await _rasService.RecreateProfileDetailedAsync("*99***1#");
                TxtRasStatus.Text = result.Success
                    ? "Status: Ready to Connect"
                    : "Failed to create profile";
            }

            _isInitialized = true;
        }

        private void DashboardPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // Jangan biarkan timer melakukan update ketika halaman tidak sedang terlihat.
            StopSignalTimer();
        }

        private void StartSignalTimer()
        {
            if (_signalTimer == null)
            {
                _signalTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                _signalTimer.Tick += SignalTimer_Tick;
            }

            if (!_signalTimer.IsEnabled)
            {
                _signalTimer.Start();
            }
        }

        private void StopSignalTimer()
        {
            _signalTimer?.Stop();
        }

        private async void SignalTimer_Tick(object? sender, object? e)
        {
            await RefreshStatusAsync();
        }

        private async Task RefreshStatusAsync()
        {
            if (!_modemService.IsPortFound || _isRefreshingStatus)
                return;

            _isRefreshingStatus = true;

            try
            {
                var status = await _modemService.GetModemStatusDetailedAsync();

                // A modem can briefly return an empty response while the port is
                // busy or the radio is changing state. Never replace good UI data
                // with those transient failures.
                if (status.SignalPercent >= 0)
                    _lastSignalPercent = Math.Clamp(status.SignalPercent, 0, 100);

                if (!string.IsNullOrWhiteSpace(status.NetworkState))
                    _lastNetworkState = status.NetworkState;

                if (!string.IsNullOrWhiteSpace(status.OperatorName))
                    _lastOperatorName = status.OperatorName;

                if (!string.IsNullOrWhiteSpace(status.NetworkType))
                    _lastNetworkType = status.NetworkType;

                _hasStatusSnapshot = true;
                RestoreCachedUi();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Dashboard status refresh failed: {ex.Message}");
            }
            finally
            {
                _isRefreshingStatus = false;
            }
        }

        private void UpdatePortSnapshot()
        {
            if (!_modemService.IsPortFound)
            {
                _lastPortInfo = "Port: Not Found";
                return;
            }

            string activePortLabel = string.IsNullOrEmpty(_modemService.AlternativeComPort)
                ? _modemService.ComPort
                : $"{_modemService.ComPort} / {_modemService.AlternativeComPort}";

            _lastPortInfo = $"Port: {activePortLabel}";
        }

        private void RestoreCachedUi()
        {
            UpdatePortSnapshot();
            TxtPortInfo.Text = _lastPortInfo;

            if (!_hasStatusSnapshot) return;

            TxtSignalPercent.Text = $"{_lastSignalPercent}%";
            ProgSignal.Value = _lastSignalPercent;

            if (TxtOperatorName is not null)
            {
                string provider = !string.IsNullOrWhiteSpace(_lastOperatorName)
                    ? _lastOperatorName
                    : _lastNetworkState;

                TxtOperatorName.Text = !string.IsNullOrWhiteSpace(_lastNetworkType)
                    ? $"{_lastNetworkType} - {provider}"
                    : provider;
            }
        }

        private async void BtnConnectRas_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRasConnected)
            {
                TxtRasStatus.Text = "Connecting...";
                BtnConnectRas.IsEnabled = false;

                var result = await _rasService.ConnectAsync();

                if (result.Success)
                {
                    _isRasConnected = true;
                    TxtRasStatus.Text = "Status: Connected";
                    BtnConnectRas.Content = "Disconnect";
                }
                else
                {
                    TxtRasStatus.Text = $"Failed: {result.Message}";
                }

                BtnConnectRas.IsEnabled = true;
            }
            else
            {
                TxtRasStatus.Text = "Disconnecting...";
                BtnConnectRas.IsEnabled = false;

                await _rasService.DisconnectAsync(_modemService);

                _isRasConnected = false;
                TxtRasStatus.Text = "Status: Disconnected";
                BtnConnectRas.Content = "Connect Internet";
                BtnConnectRas.IsEnabled = true;
            }
        }

        private async void BtnEditProfile_Click(object sender, RoutedEventArgs e)
        {
            TxtDialNumber.Text = _rasService.GetSavedPhoneNumber();

            DlgEditProfile.XamlRoot = Content.XamlRoot;
            var result = await DlgEditProfile.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                string dialNum = TxtDialNumber.Text.Trim();
                if (string.IsNullOrEmpty(dialNum))
                    dialNum = "*99***1#";

                TxtRasStatus.Text = "Updating...";
                var profileResult = await _rasService.RecreateProfileDetailedAsync(dialNum);

                TxtRasStatus.Text = profileResult.Success
                    ? "Status: Ready to Connect"
                    : profileResult.Message;
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            BtnRefresh.IsEnabled = false;

            try
            {
                if (!_modemService.IsPortFound)
                {
                    TxtOperatorName.Text = "Rescanning...";
                    var detectedPorts = await _modemService.ScanAndSetPortAsync();

                    if (detectedPorts.Count == 0)
                    {
                        _lastSignalPercent = 0;
                        _lastNetworkState = "Port not detected";
                        _lastOperatorName = "No Device";
                        _hasStatusSnapshot = true;
                        RestoreCachedUi();
                        return;
                    }
                }

                await RefreshStatusAsync();
                await RefreshWifiStatusAsync();
            }
            finally
            {
                BtnRefresh.IsEnabled = true;
            }
        }

        private async Task RefreshWifiStatusAsync()
        {
            if (!_modemService.IsPortFound || _isRefreshingWifi)
                return;

            _isRefreshingWifi = true;
            _isInitialWifiCheck = true;

            try
            {
                bool isWifiOn = await _modemService.GetWifiStatusAsync();
                ToggleWifi.IsOn = isWifiOn;
            }
            finally
            {
                _isInitialWifiCheck = false;
                _isRefreshingWifi = false;
            }
        }

        private async void ToggleWifi_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitialWifiCheck)
                return;

            bool targetState = ToggleWifi.IsOn;
            ToggleWifi.IsEnabled = false;

            try
            {
                bool success = await _modemService.SetWifiStatusAsync(targetState);

                if (!success)
                {
                    _isInitialWifiCheck = true;
                    ToggleWifi.IsOn = !targetState;
                    _isInitialWifiCheck = false;
                }
            }
            finally
            {
                ToggleWifi.IsEnabled = true;
            }
        }
    }
}
