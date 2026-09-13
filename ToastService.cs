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
                var notification = new AppNotificationBuilder()
                    .AddArgument("action", "openSms")
                    .AddText($"New SMS from: {senderNumber}")
                    .AddText(messageBody)
                    .BuildNotification();

                AppNotificationManager.Default.Show(notification);
            }
            catch
            {

            }
        }
    }
}
