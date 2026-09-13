using System;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace ModemController
{
    public static class ToastService
    {
        public static void ShowSmsNotification(string senderNumber, string messageBody)
        {
            try
            {
                senderNumber = SmsTextDecoder.DecodeSender(senderNumber);
                var builder = new AppNotificationBuilder()
                    .AddArgument("action", "openSms")
                    .AddText($"New SMS from: {senderNumber}")
                    .AddText(messageBody);

                if (AppIcon.TryGetPngUri(out Uri logoUri))
                    builder.SetAppLogoOverride(logoUri, AppNotificationImageCrop.Default);

                var notification = builder.BuildNotification();

                AppNotificationManager.Default.Show(notification);
            }
            catch
            {

            }
        }
    }
}
