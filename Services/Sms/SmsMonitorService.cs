using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ModemController
{
    public static class SmsMonitorService
    {
        private static readonly object Sync = new();
        private static readonly HashSet<string> NotifiedMessages = new();
        private static readonly List<SmsMessage> RecentMessages = new();
        private static CancellationTokenSource? _cts;
        private static Task? _monitorTask;
        private static bool _polling;

        public static event EventHandler<SmsMessage>? SmsReceived;

        public static void Start()
        {
            lock (Sync)
            {
                if (_monitorTask is { IsCompleted: false })
                    return;

                _cts = new CancellationTokenSource();
                NotifiedMessages.Clear();
                _monitorTask = MonitorAsync(_cts.Token);
            }
        }

        public static void Stop()
        {
            lock (Sync)
            {
                _cts?.Cancel();
                _cts = null;
            }
        }


        public static List<SmsMessage> GetRecentMessages()
        {
            lock (Sync)
                return new List<SmsMessage>(RecentMessages);
        }

        private static async Task MonitorAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_polling)
                {
                    _polling = true;
                    try
                    {
                        var messages = await ModemAtService.Instance.PollIncomingSmsAsync(cancellationToken);

                        foreach (var message in messages)
                        {
                            if (!message.IsIncoming)
                                continue;

                            string key = $"{message.Sender}|{message.Timestamp}|{message.Content}";
                            if (!NotifiedMessages.Add(key))
                                continue;

                            lock (Sync)
                            {
                                RecentMessages.Insert(0, message);
                                if (RecentMessages.Count > 100)
                                    RecentMessages.RemoveAt(RecentMessages.Count - 1);
                            }

                            // Persist before raising the UI event. The modem deletes
                            // the SIM copy after a successful read, so SQLite becomes
                            // the durable local conversation history.
                            try
                            {
                                await SmsStorageService.Instance.SaveMessageAsync(message);
                            }
                            catch (Exception storageEx)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"[SMS] SQLite save failed: {storageEx.Message}");
                            }

                            // The monitor owns notification delivery so a message
                            // received while the SMS page is closed is not lost.
                            ToastService.ShowSmsNotification(message.Sender, message.Content);
                            System.Diagnostics.Debug.WriteLine($"[SMS] Incoming SMS dispatched: {message.Sender} | {message.Content}");
                            SmsReceived?.Invoke(null, message);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"SMS monitor failed: {ex.Message}");
                    }
                    finally
                    {
                        _polling = false;
                    }
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
