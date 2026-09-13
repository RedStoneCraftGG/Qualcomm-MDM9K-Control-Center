using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ModemController
{
    internal static class SmsParser
    {
        private static readonly Regex HeaderRegex = new(
            @"^\+CMGL:\s*(\d+)\s*,\s*""([^""\r\n]*)""\s*,\s*""([^""\r\n]*)""\s*,\s*""([^""\r\n]*)""\s*,\s*""([^""\r\n]*)""\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static List<SmsMessage> Parse(string response)
        {
            var list = new List<SmsMessage>();
            if (string.IsNullOrWhiteSpace(response))
                return list;

            var lines = response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < lines.Length; i++)
            {
                var match = HeaderRegex.Match(lines[i].Trim());
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out int index))
                    continue;

                string status = match.Groups[2].Value.Trim();
                string sender = SmsTextDecoder.DecodeSender(match.Groups[3].Value.Trim());
                string timestamp = DateTime.Now.ToString("HH:mm");

                var bodyLines = new List<string>();
                for (int j = i + 1; j < lines.Length; j++)
                {
                    string line = lines[j].TrimEnd();
                    if (line.StartsWith("+CMGL:", StringComparison.OrdinalIgnoreCase) ||
                        line.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
                        line.Equals("ERROR", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("+CME ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    if (!line.StartsWith("AT+", StringComparison.OrdinalIgnoreCase))
                        bodyLines.Add(line);
                }

                string content = SmsTextDecoder.DecodeUcs2HexIfNeeded(
                    string.Join(Environment.NewLine, bodyLines).Trim());

                list.Add(new SmsMessage
                {
                    Index = index,
                    Sender = sender,
                    Content = content,
                    Timestamp = timestamp,
                    IsRead = status.Contains("READ", StringComparison.OrdinalIgnoreCase),
                    IsIncoming = status.StartsWith("REC", StringComparison.OrdinalIgnoreCase),
                    History = new List<ChatBubble>
                    {
                        new ChatBubble
                        {
                            Text = content,
                            Timestamp = timestamp,
                            IsIncoming = status.StartsWith("REC", StringComparison.OrdinalIgnoreCase)
                        }
                    }
                });
            }

            return list;
        }
    }
}
