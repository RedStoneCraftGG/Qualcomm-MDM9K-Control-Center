using System;
using System.Collections.Generic;
using System.Linq;

namespace ModemController
{
    internal static class SmsTextDecoder
    {
        public static string DecodeUcs2HexIfNeeded(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            string text = value.Trim();
            if (text.Length < 4 || text.Length % 4 != 0)
                return value;

            if (!System.Text.RegularExpressions.Regex.IsMatch(text, "^[0-9A-Fa-f]+$"))
                return value;

            try
            {
                byte[] bytes = ConvertHexToBytes(text);
                string decoded = System.Text.Encoding.BigEndianUnicode.GetString(bytes).Trim('\0');

                if (string.IsNullOrEmpty(decoded))
                    return value;

                bool hasBom = decoded[0] == '\uFEFF';
                if (hasBom)
                    decoded = decoded.TrimStart('\uFEFF');

                int zeroHighBytes = 0;
                int codeUnits = bytes.Length / 2;
                for (int i = 0; i < bytes.Length; i += 2)
                {
                    if (bytes[i] == 0)
                        zeroHighBytes++;
                }

                // Qualcomm firmware commonly exposes SMS text as UTF-16BE/UCS-2
                // hexadecimal (for example 005300610073...). Avoid decoding
                // arbitrary hexadecimal text unless it strongly resembles that form.
                bool looksLikeAsciiUcs2 = codeUnits > 0 &&
                                          zeroHighBytes >= Math.Max(1, (int)Math.Ceiling(codeUnits * 0.5));

                bool printable = decoded.All(c =>
                    !char.IsControl(c) || c is '\r' or '\n' or '\t');

                if ((hasBom || looksLikeAsciiUcs2) && printable)
                    return decoded;
            }
            catch
            {
            }

            return value;
        }


        public static string DecodeSender(string value)
        {
            string decoded = DecodeUcs2HexIfNeeded(value);
            return TryDecodeDecimalAscii(decoded) ?? decoded;
        }

        private static string? TryDecodeDecimalAscii(string value)
        {
            if (string.IsNullOrEmpty(value) ||
                value.Length < 6 ||
                value.Length > 60 ||
                value.Any(c => c < '0' || c > '9'))
            {
                return null;
            }

            // A normal numeric phone number must remain numeric. Only accept a
            // decimal-ASCII interpretation when the complete string can be
            // split into printable ASCII characters and the result contains
            // at least one letter. Dynamic programming avoids assuming that
            // every character uses exactly two or three decimal digits.
            var memo = new Dictionary<int, string?>();

            string? DecodeFrom(int position)
            {
                if (position == value.Length)
                    return string.Empty;

                if (memo.TryGetValue(position, out string? cached))
                    return cached;

                for (int length = 2; length <= 3; length++)
                {
                    if (position + length > value.Length)
                        continue;

                    string token = value.Substring(position, length);
                    if (!int.TryParse(token, out int code) || code < 32 || code > 126)
                        continue;

                    string? remainder = DecodeFrom(position + length);
                    if (remainder != null)
                    {
                        string candidate = ((char)code) + remainder;
                        memo[position] = candidate;
                        return candidate;
                    }
                }

                memo[position] = null;
                return null;
            }

            string? result = DecodeFrom(0);
            if (string.IsNullOrEmpty(result) || !result.Any(char.IsLetter))
                return null;

            return result;
        }

        private static byte[] ConvertHexToBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }
    }

    public class SmsMessage
    {
        public int Index { get; set; }
        public string Sender { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
        public bool IsRead { get; set; }
        public bool IsIncoming { get; set; } = true;
        public List<ChatBubble> History { get; set; } = new List<ChatBubble>();
    }

    public class ChatBubble
    {
        // Stable ID used by SQLite so failed/successful status can be updated
        // without relying on message text or timestamps.
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public long CreatedAtUtcUnixMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public string Text { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
        public bool IsIncoming { get; set; }
        public bool IsFailed { get; set; }
    }
}
