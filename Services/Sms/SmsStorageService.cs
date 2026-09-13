using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ModemController
{
    /// <summary>
    /// Persistent local SMS history backed by SQLite.
    /// The modem remains the source of truth for SIM storage; this database
    /// keeps a local conversation history after modem messages are deleted.
    /// </summary>
    public sealed class SmsStorageService
    {
        public static SmsStorageService Instance { get; } = new();

        private readonly SemaphoreSlim _databaseGate = new(1, 1);
        private readonly string _databasePath;
        private bool _initialized;

        private SmsStorageService()
        {
            string appDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Qualcomm MDM9K 4G Control Center");

            Directory.CreateDirectory(appDirectory);
            _databasePath = Path.Combine(appDirectory, "sms.db");
        }

        public string DatabasePath => _databasePath;

        private SqliteConnection CreateConnection()
        {
            var connection = new SqliteConnection($"Data Source={_databasePath}");
            return connection;
        }

        private async Task EnsureInitializedAsync()
        {
            if (_initialized)
                return;

            await _databaseGate.WaitAsync();
            try
            {
                if (_initialized)
                    return;

                await using var connection = CreateConnection();
                await connection.OpenAsync();

                await using var command = connection.CreateCommand();
                command.CommandText = @"
CREATE TABLE IF NOT EXISTS SmsMessages (
    Id TEXT NOT NULL PRIMARY KEY,
    ConversationKey TEXT NOT NULL,
    Sender TEXT NOT NULL,
    Content TEXT NOT NULL,
    Timestamp TEXT NOT NULL,
    IsIncoming INTEGER NOT NULL,
    IsFailed INTEGER NOT NULL,
    CreatedAtUtc INTEGER NOT NULL,
    ModemIndex INTEGER NOT NULL DEFAULT -1
);
CREATE INDEX IF NOT EXISTS IX_SmsMessages_Conversation
    ON SmsMessages (ConversationKey, CreatedAtUtc);

CREATE TABLE IF NOT EXISTS Contacts (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    NormalizedAddress TEXT NOT NULL UNIQUE,
    Address TEXT NOT NULL,
    DisplayName TEXT NOT NULL,
    Notes TEXT NOT NULL DEFAULT '',
    CreatedAtUtc INTEGER NOT NULL,
    UpdatedAtUtc INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Contacts_NormalizedAddress
    ON Contacts (NormalizedAddress);
";
                await command.ExecuteNonQueryAsync();
                _initialized = true;
            }
            finally
            {
                _databaseGate.Release();
            }
        }

        public async Task SaveMessageAsync(SmsMessage message)
        {
            if (message == null || message.History.Count == 0)
                return;

            await SaveBubblesAsync(message.Sender, message.History, message.Index);
        }

        public async Task SaveBubblesAsync(
            string conversationAddress,
            IEnumerable<ChatBubble> bubbles,
            int modemIndex = -1)
        {
            var items = bubbles?.ToList() ?? new List<ChatBubble>();
            if (items.Count == 0 || string.IsNullOrWhiteSpace(conversationAddress))
                return;

            await EnsureInitializedAsync();
            string conversationKey = NormalizePhoneNumber(conversationAddress);
            if (string.IsNullOrWhiteSpace(conversationKey))
                conversationKey = conversationAddress.Trim();

            await _databaseGate.WaitAsync();
            try
            {
                await using var connection = CreateConnection();
                await connection.OpenAsync();
                await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

                foreach (var bubble in items)
                {
                    if (string.IsNullOrWhiteSpace(bubble.Id))
                        bubble.Id = Guid.NewGuid().ToString("N");

                    if (bubble.CreatedAtUtcUnixMs <= 0)
                        bubble.CreatedAtUtcUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT OR IGNORE INTO SmsMessages
    (Id, ConversationKey, Sender, Content, Timestamp, IsIncoming, IsFailed, CreatedAtUtc, ModemIndex)
VALUES
    ($id, $conversationKey, $sender, $content, $timestamp, $isIncoming, $isFailed, $createdAtUtc, $modemIndex);";

                    command.Parameters.AddWithValue("$id", bubble.Id);
                    command.Parameters.AddWithValue("$conversationKey", conversationKey);
                    command.Parameters.AddWithValue("$sender", conversationAddress.Trim());
                    command.Parameters.AddWithValue("$content", bubble.Text ?? string.Empty);
                    command.Parameters.AddWithValue("$timestamp", bubble.Timestamp ?? string.Empty);
                    command.Parameters.AddWithValue("$isIncoming", bubble.IsIncoming ? 1 : 0);
                    command.Parameters.AddWithValue("$isFailed", bubble.IsFailed ? 1 : 0);
                    command.Parameters.AddWithValue("$createdAtUtc", bubble.CreatedAtUtcUnixMs);
                    command.Parameters.AddWithValue("$modemIndex", modemIndex);

                    await command.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
            }
            finally
            {
                _databaseGate.Release();
            }
        }

        public async Task UpdateMessageStatusAsync(ChatBubble bubble)
        {
            if (bubble == null || string.IsNullOrWhiteSpace(bubble.Id))
                return;

            await EnsureInitializedAsync();
            await _databaseGate.WaitAsync();
            try
            {
                await using var connection = CreateConnection();
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = @"
UPDATE SmsMessages
SET IsFailed = $isFailed
WHERE Id = $id;";
                command.Parameters.AddWithValue("$isFailed", bubble.IsFailed ? 1 : 0);
                command.Parameters.AddWithValue("$id", bubble.Id);
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                _databaseGate.Release();
            }
        }

        public async Task<List<SmsMessage>> LoadConversationsAsync()
        {
            await EnsureInitializedAsync();
            await _databaseGate.WaitAsync();
            try
            {
                await using var connection = CreateConnection();
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT Id, ConversationKey, Sender, Content, Timestamp,
       IsIncoming, IsFailed, CreatedAtUtc, ModemIndex
FROM SmsMessages
ORDER BY CreatedAtUtc ASC;";

                var grouped = new Dictionary<string, SmsMessage>(StringComparer.OrdinalIgnoreCase);

                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    string id = reader.GetString(0);
                    string conversationKey = reader.GetString(1);
                    string sender = reader.GetString(2);
                    string content = reader.GetString(3);
                    string timestamp = reader.GetString(4);
                    bool isIncoming = reader.GetInt32(5) != 0;
                    bool isFailed = reader.GetInt32(6) != 0;
                    long createdAt = reader.GetInt64(7);
                    int modemIndex = reader.GetInt32(8);

                    var bubble = new ChatBubble
                    {
                        Id = id,
                        Text = content,
                        Timestamp = timestamp,
                        IsIncoming = isIncoming,
                        IsFailed = isFailed,
                        CreatedAtUtcUnixMs = createdAt
                    };

                    if (!grouped.TryGetValue(conversationKey, out var conversation))
                    {
                        conversation = new SmsMessage
                        {
                            Index = modemIndex,
                            Sender = sender,
                            Content = content,
                            Timestamp = timestamp,
                            IsRead = true,
                            IsIncoming = isIncoming,
                            History = new List<ChatBubble>()
                        };
                        grouped[conversationKey] = conversation;
                    }

                    conversation.History.Add(bubble);
                    conversation.Sender = sender;
                    conversation.Content = content;
                    conversation.Timestamp = timestamp;
                    conversation.IsIncoming = isIncoming;
                    conversation.IsRead = true;
                    if (modemIndex >= 0)
                        conversation.Index = modemIndex;
                }

                return grouped.Values
                    .OrderByDescending(c => c.History.Count == 0 ? 0 : c.History[^1].CreatedAtUtcUnixMs)
                    .ToList();
            }
            finally
            {
                _databaseGate.Release();
            }
        }

        public async Task<List<Contact>> LoadContactsAsync()
        {
            await EnsureInitializedAsync();
            await _databaseGate.WaitAsync();
            try
            {
                await using var connection = CreateConnection();
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT Id, NormalizedAddress, Address, DisplayName, Notes, CreatedAtUtc, UpdatedAtUtc
FROM Contacts
ORDER BY DisplayName COLLATE NOCASE ASC;";

                var contacts = new List<Contact>();
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    contacts.Add(new Contact
                    {
                        Id = reader.GetInt64(0),
                        Address = reader.GetString(2),
                        DisplayName = reader.GetString(3),
                        Notes = reader.GetString(4),
                        CreatedAtUtcUnixMs = reader.GetInt64(5),
                        UpdatedAtUtcUnixMs = reader.GetInt64(6)
                    });
                }

                return contacts;
            }
            finally
            {
                _databaseGate.Release();
            }
        }

        public async Task<string?> GetContactNameAsync(string address)
        {
            string normalized = NormalizeAddress(address);
            if (string.IsNullOrWhiteSpace(normalized))
                return null;

            await EnsureInitializedAsync();
            await _databaseGate.WaitAsync();
            try
            {
                await using var connection = CreateConnection();
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT DisplayName
FROM Contacts
WHERE NormalizedAddress = $address
LIMIT 1;";
                command.Parameters.AddWithValue("$address", normalized);
                object? result = await command.ExecuteScalarAsync();
                return result is string name && !string.IsNullOrWhiteSpace(name) ? name : null;
            }
            finally
            {
                _databaseGate.Release();
            }
        }

        public async Task SaveContactAsync(Contact contact)
        {
            if (contact == null || string.IsNullOrWhiteSpace(contact.Address) ||
                string.IsNullOrWhiteSpace(contact.DisplayName))
                throw new ArgumentException("Contact address and name are required.");

            await EnsureInitializedAsync();
            string normalized = NormalizeAddress(contact.Address);
            if (string.IsNullOrWhiteSpace(normalized))
                throw new ArgumentException("The contact address is invalid.");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await _databaseGate.WaitAsync();
            try
            {
                await using var connection = CreateConnection();
                await connection.OpenAsync();
                await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = contact.Id > 0
                    ? @"
UPDATE Contacts
SET NormalizedAddress = $normalizedAddress,
    Address = $address,
    DisplayName = $displayName,
    Notes = $notes,
    UpdatedAtUtc = $updatedAt
WHERE Id = $id;"
                    : @"
INSERT INTO Contacts
    (NormalizedAddress, Address, DisplayName, Notes, CreatedAtUtc, UpdatedAtUtc)
VALUES
    ($normalizedAddress, $address, $displayName, $notes, $createdAt, $updatedAt);";

                command.Parameters.AddWithValue("$normalizedAddress", normalized);
                command.Parameters.AddWithValue("$address", contact.Address.Trim());
                command.Parameters.AddWithValue("$displayName", contact.DisplayName.Trim());
                command.Parameters.AddWithValue("$notes", contact.Notes?.Trim() ?? string.Empty);
                command.Parameters.AddWithValue("$updatedAt", now);
                if (contact.Id > 0)
                    command.Parameters.AddWithValue("$id", contact.Id);
                else
                    command.Parameters.AddWithValue("$createdAt", now);

                await command.ExecuteNonQueryAsync();
                await transaction.CommitAsync();

                if (contact.Id <= 0)
                {
                    await using var idCommand = connection.CreateCommand();
                    idCommand.CommandText = "SELECT last_insert_rowid();";
                    contact.Id = Convert.ToInt64(await idCommand.ExecuteScalarAsync());
                    contact.CreatedAtUtcUnixMs = now;
                }

                contact.Address = contact.Address.Trim();
                contact.DisplayName = contact.DisplayName.Trim();
                contact.Notes = contact.Notes?.Trim() ?? string.Empty;
                contact.UpdatedAtUtcUnixMs = now;
            }
            finally
            {
                _databaseGate.Release();
            }
        }

        public async Task DeleteContactAsync(Contact contact)
        {
            if (contact == null || contact.Id <= 0)
                return;

            await EnsureInitializedAsync();
            await _databaseGate.WaitAsync();
            try
            {
                await using var connection = CreateConnection();
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM Contacts WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", contact.Id);
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                _databaseGate.Release();
            }
        }

        private static string NormalizeAddress(string value)
        {
            string digits = new(value.Where(char.IsDigit).ToArray());
            if (digits.StartsWith("00", StringComparison.Ordinal))
                digits = digits[2..];
            if (digits.StartsWith("0", StringComparison.Ordinal) && digits.Length > 1)
                digits = "62" + digits[1..];
            return digits;
        }

        public async Task DeleteConversationAsync(SmsMessage conversation)
        {
            if (conversation == null || conversation.History.Count == 0)
                return;

            await EnsureInitializedAsync();
            await _databaseGate.WaitAsync();
            try
            {
                await using var connection = CreateConnection();
                await connection.OpenAsync();
                await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

                foreach (var bubble in conversation.History.Where(b => !string.IsNullOrWhiteSpace(b.Id)))
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM SmsMessages WHERE Id = $id;";
                    command.Parameters.AddWithValue("$id", bubble.Id);
                    await command.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
            }
            finally
            {
                _databaseGate.Release();
            }
        }

        public async Task ClearHistoryAsync()
        {
            await EnsureInitializedAsync();
            await _databaseGate.WaitAsync();
            try
            {
                await using var connection = CreateConnection();
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM SmsMessages;";
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                _databaseGate.Release();
            }
        }

        private static string NormalizePhoneNumber(string value)
        {
            string digits = new(value.Where(char.IsDigit).ToArray());
            if (digits.StartsWith("00", StringComparison.Ordinal))
                digits = digits[2..];
            if (digits.StartsWith("0", StringComparison.Ordinal))
                digits = "62" + digits[1..];
            return digits;
        }
    }
}
