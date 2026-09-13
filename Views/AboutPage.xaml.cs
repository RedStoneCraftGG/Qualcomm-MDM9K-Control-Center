using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;

namespace ModemController
{
    public sealed partial class AboutPage : Page
    {
        private readonly ModemAtService _modemService = ModemAtService.Instance;

        public AboutPage()
        {
            InitializeComponent();
            Loaded += AboutPage_Loaded;

            if (AppIcon.TryGetPngUri(out Uri logoUri))
                AppLogoImage.Source = new BitmapImage(logoUri);
        }

        private async void AboutPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= AboutPage_Loaded;

            if (!_modemService.IsPortFound)
            {
                var ports = await _modemService.ScanAndSetPortAsync();
                if (ports.Count == 0)
                {
                    TxtIdentityStatus.Text = "No compatible modem was detected.";
                    return;
                }
            }

            ModemIdentity identity = await _modemService.GetModemIdentityAsync();
            TxtManufacturer.Text = identity.Manufacturer;
            TxtPlatform.Text = identity.Platform;
            TxtModel.Text = identity.Model;
            TxtHardware.Text = identity.Hardware;
            TxtFirmware.Text = identity.Firmware;
            TxtIdentityStatus.Text = $"Detected on {_modemService.ComPort}.";
        }
    }
}
