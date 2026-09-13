using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace ModemController
{
    public sealed partial class SmsPage : Page
    {
        public ObservableCollection<SmsMessage> SmsList { get; } = new();
        public ObservableCollection<ChatBubble> CurrentChatHistory { get; } = new();

        private bool _isDeleteMode;
        private bool _isChangingMode;
        private bool _isLoading;
        private string _currentRecipient = string.Empty;
        private bool _isSendingReply;

        public SmsPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;
            ListSmsBox.ItemsSource = SmsList;
            ListChatHistory.ItemsSource = CurrentChatHistory;
            Loaded += SmsPage_Loaded;
            Unloaded += SmsPage_Unloaded;
            SmsMonitorService.SmsReceived += SmsMonitorService_SmsReceived;
        }

        private void SmsPage_Unloaded(object sender, RoutedEventArgs e)
        {
        }

        private async void SmsPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            _isLoading = true;
            BtnRefreshSms.IsEnabled = false;

            try
            {
                await LoadSmsFromModemAsync();
            }
            finally
            {
                _isLoading = false;
                BtnRefreshSms.IsEnabled = true;
                // Do not force the page back to Inbox after an asynchronous
                // load. When the user returns to the SMS navigation item,
                // Loaded can overlap with a click on an existing conversation.
                // The old unconditional ShowInboxView() here could hide the
                // conversation immediately after it was opened.
            }
        }

        private async Task LoadSmsFromModemAsync()
        {
            SmsList.Clear();

            // Load durable local history first. SMS messages are removed from
            // the modem after successful retrieval, so the SQLite database is
            // what keeps conversations available across application restarts.
            var storedConversations = await SmsStorageService.Instance.LoadConversationsAsync();
            foreach (var conversation in storedConversations)
            {
                conversation.DisplayName = await GetDisplayNameAsync(conversation.Sender);
                SmsList.Add(conversation);
            }

            var modem = ModemAtService.Instance;
            var validPorts = await modem.ScanAndSetPortAsync();

            if (validPorts.Count == 0)
            {
                if (SmsList.Count == 0)
                    LoadDummyData();
                return;
            }

            var messages = await modem.ReadAndClearStoredSmsAsync();
            var cachedMessages = SmsMonitorService.GetRecentMessages();

            foreach (var sms in cachedMessages.AsEnumerable().Reverse())
                AddIncomingSms(sms, false);

            foreach (var sms in messages)
                AddIncomingSms(sms, false);
        }

        private void LoadDummyData()
        {
            SmsList.Clear();

            // Example: Contact with known display name
            SmsList.Add(new SmsMessage
            {
                Index = 1,
                Sender = "+14155552671",
                DisplayName = "Saseko",
                Content = "Hey, are we still on for tonight? Let me know.",
                Timestamp = "Yesterday",
                IsRead = false,
                History = new List<ChatBubble>
                {
                    new ChatBubble
                    {
                        Text = "Hey, are we still on for tonight? Let me know.",
                        Timestamp = "Yesterday",
                        IsIncoming = true
                    }
                }
            });

            // Example: Number only, with country code, no name
            SmsList.Add(new SmsMessage
            {
                Index = 2,
                Sender = "+6281234567890",
                DisplayName = "+6281234567890",
                Content = "This is a test SMS to demonstrate the interface.",
                Timestamp = "Today",
                IsRead = false,
                History = new List<ChatBubble>
                {
                    new ChatBubble
                    {
                        Text = "This is a test SMS to demonstrate the interface.",
                        Timestamp = "Today",
                        IsIncoming = true
                    }
                }
            });

            // Example: Code number (shortcode, e.g., bank or operator)
            SmsList.Add(new SmsMessage
            {
                Index = 3,
                Sender = "3636",
                DisplayName = "3636",
                Content = "Your verification code is 839201.",
                Timestamp = "Today",
                IsRead = false,
                History = new List<ChatBubble>
                {
                    new ChatBubble
                    {
                        Text = "Your verification code is 839201.",
                        Timestamp = "Today",
                        IsIncoming = true
                    }
                }
            });
        }

        private async void BtnRefreshSms_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            _isLoading = true;
            BtnRefreshSms.IsEnabled = false;

            try
            {
                await LoadSmsFromModemAsync();
            }
            finally
            {
                _isLoading = false;
                BtnRefreshSms.IsEnabled = true;
            }
        }

        private void SmsMonitorService_SmsReceived(object? sender, SmsMessage message)
        {
            DispatcherQueue.TryEnqueue(() => AddIncomingSms(message, false));
        }

        private static string NormalizeRecipient(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var digits = new string(value.Where(char.IsDigit).ToArray());
            if (digits.StartsWith("00", StringComparison.Ordinal))
                digits = digits[2..];

            if (digits.StartsWith("0", StringComparison.Ordinal))
                digits = "62" + digits[1..];

            return digits;
        }

        private static bool SameRecipient(string left, string right)
        {
            string normalizedLeft = NormalizeRecipient(left);
            string normalizedRight = NormalizeRecipient(right);

            if (normalizedLeft.Length == 0 || normalizedRight.Length == 0)
                return left.Equals(right, StringComparison.OrdinalIgnoreCase);

            return normalizedLeft == normalizedRight;
        }

        private static bool ContainsBubble(SmsMessage message, ChatBubble bubble)
        {
            return message.History.Any(existing =>
                existing.IsIncoming == bubble.IsIncoming &&
                existing.Text == bubble.Text &&
                existing.Timestamp == bubble.Timestamp);
        }

        private static async Task<string> GetDisplayNameAsync(string address)
        {
            try
            {
                return await SmsStorageService.Instance.GetContactNameAsync(address) ?? address;
            }
            catch
            {
                return address;
            }
        }

        private void AddIncomingSms(SmsMessage message, bool showToast = true)
        {
            int existingIndex = -1;
            for (int i = 0; i < SmsList.Count; i++)
            {
                if (SameRecipient(SmsList[i].Sender, message.Sender))
                {
                    existingIndex = i;
                    break;
                }
            }

            SmsMessage conversation;
            message.DisplayName = message.Sender;

            if (existingIndex < 0)
            {
                conversation = message;
                SmsList.Insert(0, conversation);
            }
            else
            {
                conversation = SmsList[existingIndex];

                foreach (var bubble in message.History)
                {
                    if (!ContainsBubble(conversation, bubble))
                        conversation.History.Add(bubble);
                }

                conversation.Sender = message.Sender;
                conversation.Content = message.Content;
                conversation.Timestamp = message.Timestamp;
                conversation.IsRead = message.IsRead;
                conversation.IsIncoming = true;

                // x:Bind defaults to one-time binding, so replace the item to
                // refresh the inbox preview and timestamp immediately.
                SmsList[existingIndex] = conversation;
                if (existingIndex > 0)
                    SmsList.Move(existingIndex, 0);
            }

            if (showToast)
                ToastService.ShowSmsNotification(message.Sender, message.Content);

            if (SameRecipient(_currentRecipient, message.Sender) &&
                PanelChatDetail.Visibility == Visibility.Visible)
            {
                foreach (var bubble in message.History)
                {
                    if (!CurrentChatHistory.Any(existing =>
                        existing.IsIncoming == bubble.IsIncoming &&
                        existing.Text == bubble.Text &&
                        existing.Timestamp == bubble.Timestamp))
                    {
                        CurrentChatHistory.Add(bubble);
                    }
                }

                if (CurrentChatHistory.Count > 0)
                {
                    ListChatHistory.UpdateLayout();
                    ListChatHistory.ScrollIntoView(CurrentChatHistory[^1]);
                }
            }

            _ = PersistIncomingMessageAsync(message);
            _ = RefreshConversationDisplayNameAsync(message.Sender);
        }

        private async Task RefreshConversationDisplayNameAsync(string address)
        {
            string displayName = await GetDisplayNameAsync(address);
            DispatcherQueue.TryEnqueue(() =>
            {
                var conversation = SmsList.FirstOrDefault(c => SameRecipient(c.Sender, address));
                if (conversation == null)
                    return;

                conversation.DisplayName = displayName;
                int index = SmsList.IndexOf(conversation);
                if (index >= 0)
                    SmsList[index] = conversation;

                if (SameRecipient(_currentRecipient, address))
                {
                    TxtChatTitle.Text = displayName;
                    TxtChatAddress.Text = address;
                }
            });
        }

        private static async Task PersistIncomingMessageAsync(SmsMessage message)
        {
            try
            {
                await SmsStorageService.Instance.SaveMessageAsync(message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SMS] SQLite save failed: {ex.Message}");
            }
        }

        private async void BtnEditContact_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_currentRecipient))
                return;

            if (ModemWidgetApp.App.Current is ModemWidgetApp.App app)
            {
                app.NavigateToContacts(_currentRecipient);
            }
        }

        private void BtnBackToInbox_Click(object sender, RoutedEventArgs e) => ShowInboxView();

        private void BtnNewMessage_Click(object sender, RoutedEventArgs e)
        {
            ToggleDeleteMode(false);
            TxtBoxNewRecipient.Text = string.Empty;
            TxtBoxNewMessage.Text = string.Empty;
            ShowNewMessageView();
            TxtBoxNewRecipient.Focus(FocusState.Programmatic);
        }

        private void BtnBackFromNewMessage_Click(object sender, RoutedEventArgs e) => ShowInboxView();

        private void BtnSendNewMessage_Click(object sender, RoutedEventArgs e) => SendNewMessage();

        private void TxtBoxNewMessage_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter)
                return;

            e.Handled = true;
            SendNewMessage();
        }

        private void BtnSendSmsGlyph_Click(object sender, RoutedEventArgs e) => SendReplyMessage();

        private void TxtBoxReply_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter)
                return;

            e.Handled = true;
            SendReplyMessage();
        }

        private void SendReplyMessage()
        {
            string replyText = TxtBoxReply.Text.Trim();
            string recipient = _currentRecipient.Trim();

            if (string.IsNullOrEmpty(replyText) || string.IsNullOrWhiteSpace(recipient) || _isSendingReply)
                return;

            _ = SendMessageAsync(recipient, replyText, false);
        }

        private void SendNewMessage()
        {
            string recipient = TxtBoxNewRecipient.Text.Trim();
            string messageText = TxtBoxNewMessage.Text.Trim();

            if (string.IsNullOrWhiteSpace(recipient) || string.IsNullOrWhiteSpace(messageText) || _isSendingReply)
                return;

            _ = SendMessageAsync(recipient, messageText, true);
        }

        private async Task SendMessageAsync(string recipient, string messageText, bool isNewMessage)
        {
            recipient = NormalizeRecipient(recipient);
            if (string.IsNullOrWhiteSpace(recipient))
                return;

            _isSendingReply = true;
            BtnSendSmsGlyph.IsEnabled = false;
            BtnSendNewMessage.IsEnabled = false;

            var bubble = new ChatBubble
            {
                Text = messageText,
                Timestamp = DateTime.Now.ToString("HH:mm"),
                IsIncoming = false
            };

            int conversationIndex = -1;
            for (int i = 0; i < SmsList.Count; i++)
            {
                if (SameRecipient(SmsList[i].Sender, recipient))
                {
                    conversationIndex = i;
                    break;
                }
            }

            if (conversationIndex >= 0)
            {
                var conversation = SmsList[conversationIndex];
                if (!ContainsBubble(conversation, bubble))
                    conversation.History.Add(bubble);

                conversation.Content = messageText;
                conversation.Timestamp = bubble.Timestamp;
                conversation.IsRead = true;
                conversation.IsIncoming = false;

                SmsList[conversationIndex] = conversation;
                if (conversationIndex > 0)
                    SmsList.Move(conversationIndex, 0);
            }
            else
            {
                var conversation = new SmsMessage
                {
                    Index = -1,
                    Sender = recipient,
                    Content = messageText,
                    Timestamp = bubble.Timestamp,
                    IsRead = true,
                    IsIncoming = false,
                    History = new List<ChatBubble> { bubble }
                };

                SmsList.Insert(0, conversation);
            }

            _currentRecipient = recipient;
            string displayName = await GetDisplayNameAsync(recipient);
            var displayedConversation = SmsList.FirstOrDefault(c => SameRecipient(c.Sender, recipient));
            if (displayedConversation != null)
            {
                displayedConversation.DisplayName = displayName;
                int displayedIndex = SmsList.IndexOf(displayedConversation);
                if (displayedIndex >= 0)
                    SmsList[displayedIndex] = displayedConversation;
            }
            TxtChatTitle.Text = displayName;
            TxtChatAddress.Text = recipient;
            CurrentChatHistory.Clear();

            var currentConversation = SmsList.FirstOrDefault(c => SameRecipient(c.Sender, recipient));
            if (currentConversation != null)
            {
                foreach (var historyBubble in currentConversation.History)
                    CurrentChatHistory.Add(historyBubble);
            }

            if (isNewMessage)
            {
                TxtBoxNewRecipient.Text = string.Empty;
                TxtBoxNewMessage.Text = string.Empty;
                ShowChatDetailView();
            }
            else
            {
                TxtBoxReply.Text = string.Empty;
            }

            ListChatHistory.UpdateLayout();
            ListChatHistory.ScrollIntoView(bubble);

            // Save the outgoing bubble before touching the modem. The user's
            // send action therefore survives application restarts even when
            // the modem later reports a failure or an unknown final result.
            try
            {
                await SmsStorageService.Instance.SaveBubblesAsync(recipient, new[] { bubble });
            }
            catch (Exception storageEx)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[SMS] SQLite outgoing save failed: {storageEx.Message}");
            }

            try
            {
                SmsSendResult sendResult = await ModemAtService.Instance
                    .SendSmsWithStatusAsync(recipient, messageText);

                if (sendResult == SmsSendResult.FailedBeforeSubmit)
                {
                    bubble.IsFailed = true;
                    int bubbleIndex = CurrentChatHistory.IndexOf(bubble);
                    if (bubbleIndex >= 0)
                        CurrentChatHistory[bubbleIndex] = bubble;

                    try
                    {
                        await SmsStorageService.Instance.UpdateMessageStatusAsync(bubble);
                    }
                    catch (Exception storageEx)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[SMS] SQLite status update failed: {storageEx.Message}");
                    }

                    System.Diagnostics.Debug.WriteLine(
                        $"[SMS] Send failed before submission for {recipient}; " +
                        "outgoing bubble marked as failed.");
                }
                else if (sendResult == SmsSendResult.UnknownAfterSubmit)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[SMS] Send result is not confirmed for {recipient}; " +
                        "outgoing bubble kept without a failure indicator.");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[SMS] Send operation ended with an exception for {recipient}: {ex.Message}");
            }
            finally
            {
                _isSendingReply = false;
                BtnSendSmsGlyph.IsEnabled = true;
                BtnSendNewMessage.IsEnabled = true;

                TxtBoxReply.Focus(FocusState.Programmatic);
            }
        }

        private void ShowInboxView()
        {
            PanelInbox.Visibility = Visibility.Visible;
            PanelChatDetail.Visibility = Visibility.Collapsed;
            PanelNewMessage.Visibility = Visibility.Collapsed;
        }

        private void ShowChatDetailView()
        {
            PanelInbox.Visibility = Visibility.Collapsed;
            PanelChatDetail.Visibility = Visibility.Visible;
            PanelNewMessage.Visibility = Visibility.Collapsed;
        }

        private void ShowNewMessageView()
        {
            PanelInbox.Visibility = Visibility.Collapsed;
            PanelChatDetail.Visibility = Visibility.Collapsed;
            PanelNewMessage.Visibility = Visibility.Visible;
        }

        private void ToggleDeleteMode(bool enable)
        {
            _isChangingMode = true;
            _isDeleteMode = enable;
            ListSmsBox.SelectionMode = enable ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single;
            PanelDeleteActions.Visibility = enable ? Visibility.Visible : Visibility.Collapsed;

            if (!enable)
            {
                try { ListSmsBox.SelectedItems.Clear(); }
                catch { ListSmsBox.SelectedIndex = -1; }
            }

            _isChangingMode = false;
        }

        private void ListSmsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isDeleteMode || _isChangingMode)
                return;

            if (ListSmsBox.SelectedItem is SmsMessage selectedMsg)
            {
                TxtChatTitle.Text = string.IsNullOrWhiteSpace(selectedMsg.DisplayName) ? selectedMsg.Sender : selectedMsg.DisplayName;
                TxtChatAddress.Text = selectedMsg.Sender;
                _currentRecipient = selectedMsg.Sender;
                CurrentChatHistory.Clear();

                foreach (var bubble in selectedMsg.History)
                    CurrentChatHistory.Add(bubble);

                ShowChatDetailView();
                ListSmsBox.SelectedItem = null;
            }
        }

        private void BtnClearSelection_Click(object sender, RoutedEventArgs e)
        {
            try { ListSmsBox.SelectedItems.Clear(); }
            catch { ListSmsBox.SelectedIndex = -1; }
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => ListSmsBox.SelectAll();

        private async void BtnTrashAction_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = ListSmsBox.SelectedItems.Cast<SmsMessage>().ToList();
            if (selectedItems.Count == 0)
                return;

            foreach (var item in selectedItems)
            {
                if (item.Index >= 0)
                    await ModemAtService.Instance.DeleteStoredSmsAsync(item.Index);

                try
                {
                    await SmsStorageService.Instance.DeleteConversationAsync(item);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[SMS] SQLite conversation delete failed: {ex.Message}");
                }

                SmsList.Remove(item);
            }

            ToggleDeleteMode(false);
        }

        private void BtnToggleDeleteMode_Click(object sender, RoutedEventArgs e) => ToggleDeleteMode(!_isDeleteMode);
    }

    public class BoolToAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            (bool)value ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }

    public class BoolToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return (bool)value
                ? Application.Current.Resources["CardBackgroundFillColorDefaultBrush"]
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 30, 80, 120));
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }

    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotImplementedException();
    }

    public class ListViewItemSelectionToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is bool b && b
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 50, 90, 130))
                : Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
    }
}
