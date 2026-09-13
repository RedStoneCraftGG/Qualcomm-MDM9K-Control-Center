using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace ModemController
{
    public sealed partial class ContactsPage : Page
    {
        public ObservableCollection<Contact> Contacts { get; } = new();

        private string? _addressToEdit;

        public ContactsPage()
        {
            InitializeComponent();
            ListContacts.ItemsSource = Contacts;
            Loaded += ContactsPage_Loaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _addressToEdit = e.Parameter as string;
        }

        private async void ContactsPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= ContactsPage_Loaded;
            await LoadContactsAsync();
        }

        private async Task LoadContactsAsync()
        {
            try
            {
                Contacts.Clear();
                foreach (var contact in await SmsStorageService.Instance.LoadContactsAsync())
                    Contacts.Add(contact);

                if (!string.IsNullOrWhiteSpace(_addressToEdit))
                {
                    string address = _addressToEdit;
                    _addressToEdit = null;
                    var existing = Contacts.FirstOrDefault(c => SameAddress(c.Address, address));
                    await EditContactAsync(existing ?? new Contact { Address = address });
                }
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("Unable to load contacts", ex.Message);
            }
        }

        private async void BtnAddContact_Click(object sender, RoutedEventArgs e)
        {
            await EditContactAsync(null);
        }

        private async void ListContacts_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Contact contact)
                await EditContactAsync(contact);
        }

        private async Task EditContactAsync(Contact? contact)
        {
            bool isNew = contact == null;
            var working = contact == null
                ? new Contact()
                : new Contact
                {
                    Id = contact.Id,
                    Address = contact.Address,
                    DisplayName = contact.DisplayName,
                    Notes = contact.Notes,
                    CreatedAtUtcUnixMs = contact.CreatedAtUtcUnixMs,
                    UpdatedAtUtcUnixMs = contact.UpdatedAtUtcUnixMs
                };

            var panel = new StackPanel { Spacing = 10 };
            var nameBox = new TextBox
            {
                Header = "Name",
                PlaceholderText = "e.g. Budi",
                Text = working.DisplayName
            };
            var addressBox = new TextBox
            {
                Header = "Phone number or short code",
                PlaceholderText = "e.g. 0812xxxx or 8000",
                Text = working.Address,
                InputScope = new Microsoft.UI.Xaml.Input.InputScope
                {
                    Names = { new Microsoft.UI.Xaml.Input.InputScopeName(Microsoft.UI.Xaml.Input.InputScopeNameValue.TelephoneNumber) }
                }
            };
            var notesBox = new TextBox
            {
                Header = "Notes",
                PlaceholderText = "Optional",
                Text = working.Notes,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap
            };

            panel.Children.Add(nameBox);
            panel.Children.Add(addressBox);
            panel.Children.Add(notesBox);

            var dialog = new ContentDialog
            {
                Title = isNew ? "Add Contact" : "Edit Contact",
                Content = panel,
                PrimaryButtonText = "Save",
                SecondaryButtonText = isNew ? string.Empty : "Delete",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };

            bool saved = false;
            bool deleted = false;

            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    string name = nameBox.Text.Trim();
                    string address = addressBox.Text.Trim();
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(address))
                    {
                        args.Cancel = true;
                        return;
                    }

                    working.DisplayName = name;
                    working.Address = address;
                    working.Notes = notesBox.Text.Trim();

                    try
                    {
                        await SmsStorageService.Instance.SaveContactAsync(working);
                        saved = true;
                    }
                    catch (Exception ex)
                    {
                        args.Cancel = true;
                        await ShowErrorAsync("Unable to save contact", ex.Message);
                    }
                }
                finally
                {
                    deferral.Complete();
                }
            };

            dialog.SecondaryButtonClick += async (_, args) =>
            {
                if (isNew)
                    return;

                var deferral = args.GetDeferral();
                try
                {
                    await SmsStorageService.Instance.DeleteContactAsync(working);
                    deleted = true;
                }
                catch (Exception ex)
                {
                    args.Cancel = true;
                    await ShowErrorAsync("Unable to delete contact", ex.Message);
                }
                finally
                {
                    deferral.Complete();
                }
            };

            await dialog.ShowAsync();

            if (deleted)
            {
                if (contact != null)
                    Contacts.Remove(contact);
                return;
            }

            if (!saved)
                return;

            if (isNew)
                Contacts.Add(working);
            else
            {
                int index = Contacts.IndexOf(contact!);
                if (index >= 0)
                    Contacts[index] = working;
            }
        }

        private static bool SameAddress(string left, string right)
        {
            string Normalize(string value)
            {
                string digits = new(value.Where(char.IsDigit).ToArray());
                if (digits.StartsWith("00", StringComparison.Ordinal))
                    digits = digits[2..];
                if (digits.StartsWith("0", StringComparison.Ordinal) && digits.Length > 1)
                    digits = "62" + digits[1..];
                return digits;
            }

            return Normalize(left) == Normalize(right);
        }

        private async Task ShowErrorAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
        }
    }
}
