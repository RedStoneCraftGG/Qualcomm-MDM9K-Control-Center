using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ModemController
{
    public enum SmsSendResult
    {
        FailedBeforeSubmit,
        UnknownAfterSubmit,
        Success
    }

    public class ModemAtService
    {
        private const string SmsStorageCommand = "AT+CPMS=\"SM\",\"SM\",\"SM\"";
        public static ModemAtService Instance { get; } = new ModemAtService();
        public string ComPort { get; set; } = string.Empty;
        public string AlternativeComPort { get; set; } = string.Empty;
        public List<string> DetectedPorts { get; private set; } = new List<string>();

        public bool IsPortFound => !string.IsNullOrEmpty(ComPort);

        public ModemAtService(string initialPort = "")
        {
            ComPort = initialPort;
        }


        public async Task<List<string>> ScanAndSetPortAsync()
        {
            return await Task.Run(async () =>
            {
                await ModemSerialGate.Semaphore.WaitAsync();
                try
                {
                DetectedPorts.Clear();
                string[] availablePorts = SerialPort.GetPortNames();

                foreach (string portName in availablePorts)
                {
                    try
                    {
                        using var port = new SerialPort(portName, 115200)
                        {
                            ReadTimeout = 400,
                            WriteTimeout = 400
                        };

                        port.Open();
                        port.Write("AT\r");
                        await Task.Delay(200);

                        string response = port.ReadExisting();


                        if (response.Contains("OK"))
                        {
                            DetectedPorts.Add(portName);
                        }
                    }
                    catch
                    {

                    }
                }


                if (DetectedPorts.Count > 0)
                {
                    ComPort = DetectedPorts[0];
                    AlternativeComPort = DetectedPorts.Count > 1 ? DetectedPorts[1] : string.Empty;
                }
                else
                {
                    ComPort = string.Empty;
                    AlternativeComPort = string.Empty;
                }

                return DetectedPorts;
                }
                finally
                {
                    ModemSerialGate.Semaphore.Release();
                }
            });
        }

        public async Task<string> SendAtCommandAsync(string command, int timeoutMs = 3000)
        {
            if (!IsPortFound)
                return string.Empty;

            return await Task.Run(async () =>
            {
                string[] targetPorts = string.IsNullOrEmpty(AlternativeComPort)
                    ? new[] { ComPort }
                    : new[] { ComPort, AlternativeComPort };

                foreach (var currentPort in targetPorts)
                {
                    await ModemSerialGate.Semaphore.WaitAsync();
                    try
                    {
                        try
                        {
                            using var port = new SerialPort(currentPort, 115200, Parity.None, 8, StopBits.One)
                            {
                                ReadTimeout = 100,
                                WriteTimeout = 1000,
                                NewLine = "\r\n",
                                DtrEnable = false,
                                RtsEnable = false
                            };

                            port.Open();
                            port.DiscardInBuffer();
                            port.Write(command + "\r");

                            var response = new System.Text.StringBuilder();
                            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(timeoutMs, 500));
                            bool receivedData = false;

                            while (DateTime.UtcNow < deadline)
                            {
                                string chunk = port.ReadExisting();
                                if (!string.IsNullOrEmpty(chunk))
                                {
                                    response.Append(chunk);
                                    receivedData = true;

                                    string text = response.ToString();
                                    if (Regex.IsMatch(text, @"(?:^|\r?\n)(?:OK|ERROR|COMMAND NOT SUPPORT|\+CME ERROR[^\r\n]*)(?:\r?\n|$)", RegexOptions.IgnoreCase))
                                        break;
                                }

                                await Task.Delay(receivedData ? 40 : 75);
                            }

                            string result = response.ToString();
                            if (!string.IsNullOrWhiteSpace(result))
                                return result;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"AT command on {currentPort} failed: {ex.Message}");
                        }
                    }
                    finally
                    {
                        ModemSerialGate.Semaphore.Release();
                    }
                }

                return string.Empty;
            });
        }

        public async Task<List<SmsMessage>> GetStoredSmsAsync()
        {
            if (!IsPortFound)
                return new List<SmsMessage>();

            await SendAtCommandAsync(SmsStorageCommand, 2500);
            await SendAtCommandAsync("AT+CMGF=1", 1500);
            string response = await SendAtCommandAsync("AT+CMGL=\"ALL\"", 4000);
            return SmsParser.Parse(response);
        }

        public async Task<bool> DeleteStoredSmsAsync(int index)
        {
            if (!IsPortFound || index < 0)
                return false;

            await SendAtCommandAsync(SmsStorageCommand, 2500);
            string response = await SendAtCommandAsync($"AT+CMGD={index}", 2500);
            return response.Contains("OK", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<bool> ClearStoredSmsAsync(IEnumerable<SmsMessage> messages)
        {
            bool success = true;
            foreach (var message in messages.Where(m => m.Index >= 0))
            {
                if (!await DeleteStoredSmsAsync(message.Index))
                    success = false;
            }

            return success;
        }

        public async Task<bool> ClearAllSmsStorageAsync()
        {
            if (!IsPortFound)
            {
                var ports = await ScanAndSetPortAsync();
                if (ports.Count == 0)
                    return false;
            }

            await SendAtCommandAsync(SmsStorageCommand, 2500);
            string response = await SendAtCommandAsync("AT+CMGDA=\"DEL ALL\"", 5000);

            if (response.Contains("OK", StringComparison.OrdinalIgnoreCase))
                return true;

            // Fallback for firmware that does not implement CMGDA.
            var stored = await GetStoredSmsAsync();
            if (stored.Count == 0)
                return !response.Contains("ERROR", StringComparison.OrdinalIgnoreCase);

            return await ClearStoredSmsAsync(stored);
        }

        public async Task<List<SmsMessage>> PollIncomingSmsAsync(CancellationToken cancellationToken = default)
        {
            if (!IsPortFound)
            {
                var ports = await ScanAndSetPortAsync();
                if (ports.Count == 0)
                    return new List<SmsMessage>();
            }

            string[] targetPorts = string.IsNullOrEmpty(AlternativeComPort)
                ? new[] { ComPort }
                : new[] { ComPort, AlternativeComPort };

            foreach (string currentPort in targetPorts.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var poll = await PollSmsOnPortAsync(currentPort, cancellationToken);
                if (!poll.Success)
                    continue;

                // A successfully opened and initialized port is the active port,
                // even when there are currently no unread messages. Do not poll
                // another port in the same cycle because both ports can expose
                // the same modem SMS storage.
                PromoteActivePort(currentPort);
                return poll.Messages;
            }

            return new List<SmsMessage>();
        }

        private async Task<(bool Success, List<SmsMessage> Messages)> PollSmsOnPortAsync(
            string currentPort, CancellationToken cancellationToken)
        {
            return await Task.Run(async () =>
            {
                await ModemSerialGate.Semaphore.WaitAsync(cancellationToken);
                try
                {
                    using var port = CreateSerialPort(currentPort);
                    port.Open();
                    port.DiscardInBuffer();

                    if (!await SendAndWaitAsync(port, SmsStorageCommand, "OK", 2500))
                        return (false, new List<SmsMessage>());

                    if (!await SendAndWaitAsync(port, "AT+CMGF=1", "OK", 2500))
                        return (false, new List<SmsMessage>());

                    // Keep new-message notifications enabled when the port is open.
                    await SendAndWaitAsync(port, "AT+CNMI=2,1,0,0,0", "OK", 2500);

                    // Only unread messages are candidates for notification. Using
                    // CMGL="ALL" here caused already-read messages to be processed
                    // repeatedly and made primary/fallback ports race over storage.
                    string listResponse = await SendAndReadAsync(port, "AT+CMGL=\"REC UNREAD\"", 5000);
                    System.Diagnostics.Debug.WriteLine($"[SMS] CMGL response on {currentPort}:\n{listResponse}");

                    var indexes = new List<int>();
                    AddCmglIndexes(listResponse, indexes);
                    AddCmtiIndexes(port.ReadExisting(), indexes);

                    if (indexes.Count == 0 &&
                        listResponse.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        string alternateResponse = await SendAndReadAsync(port, "AT+CMGL", 5000);
                        AddCmglIndexes(alternateResponse, indexes);
                    }

                    var result = new List<SmsMessage>();
                    var seen = new HashSet<int>();

                    foreach (int index in indexes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!seen.Add(index))
                            continue;

                        System.Diagnostics.Debug.WriteLine($"[SMS] Reading CMGR index {index} on {currentPort}");
                        SmsMessage? sms = ReadSmsFromOpenPort(port, index);
                        if (sms == null || !sms.IsIncoming)
                            continue;

                        result.Add(sms);

                        // Delete only after CMGR has been parsed successfully.
                        // This prevents a malformed response from losing the SMS.
                        if (await SendAndWaitAsync(port, $"AT+CMGD={index}", "OK", 2500))
                            System.Diagnostics.Debug.WriteLine($"[SMS] Deleted index {index} on {currentPort}");
                    }

                    return (true, result);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"SMS poll failed on {currentPort}: {ex.Message}");
                    return (false, new List<SmsMessage>());
                }
                finally
                {
                    ModemSerialGate.Semaphore.Release();
                }
            }, cancellationToken);
        }

        private void PromoteActivePort(string port)
        {
            if (string.Equals(ComPort, port, StringComparison.OrdinalIgnoreCase))
                return;

            string previousPrimary = ComPort;
            ComPort = port;

            if (!string.IsNullOrWhiteSpace(previousPrimary) &&
                !string.Equals(previousPrimary, port, StringComparison.OrdinalIgnoreCase))
            {
                AlternativeComPort = previousPrimary;
            }
        }

        public async Task<List<SmsMessage>> ReadAndClearStoredSmsAsync()
        {
            return await PollIncomingSmsAsync();
        }

        private static SerialPort CreateSerialPort(string portName)
        {
            return new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 100,
                WriteTimeout = 1500,
                NewLine = "\r\n",
                DtrEnable = false,
                RtsEnable = false
            };
        }

        private static void AddCmtiIndexes(string response, List<int> indexes)
        {
            if (string.IsNullOrWhiteSpace(response))
                return;

            foreach (Match match in Regex.Matches(
                         response,
                         @"\+CMTI:\s*""[^""\r\n]*""\s*,\s*(\d+)",
                         RegexOptions.IgnoreCase))
            {
                if (int.TryParse(match.Groups[1].Value, out int index))
                    indexes.Add(index);
            }
        }

        private static void AddCmglIndexes(string response, List<int> indexes)
        {
            if (string.IsNullOrWhiteSpace(response))
                return;

            foreach (Match match in Regex.Matches(
                         response,
                         @"\+CMGL:\s*(\d+)",
                         RegexOptions.IgnoreCase))
            {
                if (int.TryParse(match.Groups[1].Value, out int index))
                    indexes.Add(index);
            }
        }

        /// <summary>
        /// Reads one SMS from an already-open modem port using AT+CMGR.
        /// This follows the command sequence used by the modem firmware more
        /// reliably than trying to extract the complete body from CMGL alone.
        /// </summary>
        public SmsMessage? ReadSmsFromOpenPort(SerialPort openPort, int index)
        {
            if (!openPort.IsOpen || index < 0)
                return null;

            try
            {
                openPort.DiscardInBuffer();
                openPort.Write($"AT+CMGR={index}\r");

                string response = ReadUntilTerminal(openPort, 3500);
                System.Diagnostics.Debug.WriteLine($"[SMS] CMGR {index} response:\n{response}");
                var lines = response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                int headerLine = Array.FindIndex(lines, line =>
                    line.TrimStart().StartsWith("+CMGR:", StringComparison.OrdinalIgnoreCase));

                if (headerLine < 0)
                    return null;

                string header = lines[headerLine].Trim();
                var match = Regex.Match(
                    header,
                    @"\+CMGR:\s*""([^""\r\n]*)""\s*,\s*""([^""\r\n]*)""\s*,\s*(?:""([^""\r\n]*)""|)\s*,\s*""([^""\r\n]*)""",
                    RegexOptions.IgnoreCase);

                if (!match.Success)
                    return null;

                string status = match.Groups[1].Value.Trim();
                string sender = SmsTextDecoder.DecodeSender(match.Groups[2].Value.Trim());
                string timestamp = DateTime.Now.ToString("HH:mm");

                var bodyLines = new List<string>();
                for (int i = headerLine + 1; i < lines.Length; i++)
                {
                    string line = lines[i].TrimEnd();
                    if (line.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
                        line.Equals("ERROR", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("+CME ERROR", StringComparison.OrdinalIgnoreCase))
                        break;

                    if (!line.StartsWith("AT+", StringComparison.OrdinalIgnoreCase))
                        bodyLines.Add(line);
                }

                string body = SmsTextDecoder.DecodeUcs2HexIfNeeded(
                    string.Join(Environment.NewLine, bodyLines).Trim());
                if (string.IsNullOrWhiteSpace(body))
                    body = "Empty message";

                bool incoming = status.StartsWith("REC", StringComparison.OrdinalIgnoreCase);
                bool read = status.Equals("REC READ", StringComparison.OrdinalIgnoreCase) ||
                            status.Equals("STO SENT", StringComparison.OrdinalIgnoreCase);

                return new SmsMessage
                {
                    Index = index,
                    Sender = sender,
                    Content = body,
                    Timestamp = timestamp,
                    IsRead = read,
                    IsIncoming = incoming,
                    History = new List<ChatBubble>
                    {
                        new ChatBubble
                        {
                            Text = body,
                            Timestamp = timestamp,
                            IsIncoming = incoming
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CMGR {index} failed: {ex.Message}");
                return null;
            }
        }

        private static async Task<string> SendAndReadAsync(SerialPort port, string command, int timeoutMs)
        {
            port.DiscardInBuffer();
            port.Write(command + "\r");
            return await ReadUntilTerminalAsync(port, timeoutMs);
        }

        private static async Task<bool> SendAndWaitAsync(SerialPort port, string command, string expected, int timeoutMs)
        {
            string response = await SendAndReadAsync(port, command, timeoutMs);
            return response.Contains(expected, StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadUntilTerminal(SerialPort port, int timeoutMs)
        {
            var response = new System.Text.StringBuilder();
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(timeoutMs, 250));

            while (DateTime.UtcNow < deadline)
            {
                string chunk = port.ReadExisting();
                if (!string.IsNullOrEmpty(chunk))
                {
                    response.Append(chunk);
                    string text = response.ToString();
                    if (Regex.IsMatch(text, @"(?:^|\r?\n)(?:OK|ERROR|\+CME ERROR[^\r\n]*)(?:\r?\n|$)", RegexOptions.IgnoreCase))
                        break;
                }

                System.Threading.Thread.Sleep(50);
            }

            return response.ToString();
        }

        public async Task<bool> DisableWifiHotspotAsync()
        {
            if (!IsPortFound)
                return false;

            return await SetWifiStatusAsync(false);
        }

        public async Task<ModemIdentity> GetModemIdentityAsync()
        {
            if (!IsPortFound)
                return new ModemIdentity();

            try
            {
                string ati = await SendAtCommandAsync("ATI", 3000);
                string manufacturer = ExtractLineValue(ati, "Manufacturer:");
                string model = ExtractLineValue(ati, "Model:");
                string revision = ExtractLineValue(ati, "Revision:");

                if (string.IsNullOrWhiteSpace(manufacturer))
                    manufacturer = CleanResponseValue(await SendAtCommandAsync("AT+CGMI", 2000));

                if (string.IsNullOrWhiteSpace(model))
                    model = CleanResponseValue(await SendAtCommandAsync("AT+CGMM", 2000));

                if (string.IsNullOrWhiteSpace(revision))
                    revision = CleanResponseValue(await SendAtCommandAsync("AT+CGMR", 3000));

                string hardwareResponse = await SendAtCommandAsync("AT^HWVER", 2500);
                string hardware = ExtractLineValue(hardwareResponse, "^HWVER:");

                string platform = ExtractPlatform(ati, revision, model);

                return new ModemIdentity
                {
                    Manufacturer = NormalizeIdentityValue(manufacturer),
                    Model = NormalizeIdentityValue(model),
                    Platform = NormalizeIdentityValue(platform),
                    Hardware = NormalizeIdentityValue(hardware),
                    Firmware = NormalizeIdentityValue(revision),
                    RawAti = ati
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Modem] Identity query failed: {ex.Message}");
                return new ModemIdentity();
            }
        }

        private static string ExtractLineValue(string response, string prefix)
        {
            if (string.IsNullOrWhiteSpace(response))
                return string.Empty;

            foreach (string rawLine in response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                int index = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                    return line[(index + prefix.Length)..].Trim();
            }

            return string.Empty;
        }

        private static string CleanResponseValue(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
                return string.Empty;

            foreach (string rawLine in response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("ERROR", StringComparison.OrdinalIgnoreCase) || line.StartsWith("AT", StringComparison.OrdinalIgnoreCase))
                    continue;

                return line;
            }

            return string.Empty;
        }

        private static string ExtractPlatform(string ati, string revision, string model)
        {
            string combined = $"{ati}\n{revision}\n{model}";
            Match match = Regex.Match(combined, @"\b(MDM9K)\b", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() : string.Empty;
        }

        private static string NormalizeIdentityValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Unknown";

            string normalized = value.Trim();
            if (normalized.Equals("0", StringComparison.OrdinalIgnoreCase))
                return "Unknown";

            return normalized;
        }

        public async Task<ModemStatus> GetModemStatusAsync()
        {
            var status = new ModemStatus();

            if (!IsPortFound)
            {
                status.NetworkState = "AT port not found";
                return status;
            }

            return await Task.Run(async () =>
            {
                try
                {
                    using var port = new SerialPort(ComPort, 115200)
                    {
                        ReadTimeout = 500,
                        WriteTimeout = 500
                    };

                    port.Open();


                    port.WriteLine("AT+CSQ\r");
                    await Task.Delay(100);
                    string csqResp = port.ReadExisting();
                    var matchCsq = Regex.Match(csqResp, @"\+CSQ:\s*(\d+)");

                    if (matchCsq.Success && int.TryParse(matchCsq.Groups[1].Value, out int rssi))
                    {
                        status.SignalPercent = (rssi == 99) ? 0 : (int)Math.Round((rssi / 31.0) * 100);
                    }


                    port.WriteLine("AT+CGREG?\r");
                    await Task.Delay(100);
                    string cgregResp = port.ReadExisting();
                    var matchCgreg = Regex.Match(cgregResp, @"\+CGREG:\s*\d+,(\d+)");

                    if (matchCgreg.Success)
                    {
                        status.NetworkState = matchCgreg.Groups[1].Value switch
                        {
                            "1" => "Connected (Home Network)",
                            "2" => "Searching for Network...",
                            "3" => "Access Denied",
                            "5" => "Connected (Roaming)",
                            _ => "Not Registered"
                        };
                        status.IsConnected = (matchCgreg.Groups[1].Value == "1" || matchCgreg.Groups[1].Value == "5");
                    }
                }
                catch
                {

                    if (!string.IsNullOrEmpty(AlternativeComPort))
                    {
                        try
                        {
                            using var altPort = new SerialPort(AlternativeComPort, 115200) { ReadTimeout = 500, WriteTimeout = 500 };
                            altPort.Open();
                            altPort.WriteLine("AT+CSQ\r");
                            await Task.Delay(100);
                            string csqResp = altPort.ReadExisting();
                            var matchCsq = Regex.Match(csqResp, @"\+CSQ:\s*(\d+)");
                            if (matchCsq.Success && int.TryParse(matchCsq.Groups[1].Value, out int rssi))
                            {
                                status.SignalPercent = (rssi == 99) ? 0 : (int)Math.Round((rssi / 31.0) * 100);
                                status.NetworkState = "Connected (Secondary Port)";
                                status.IsConnected = true;
                                return status;
                            }
                        }
                        catch { }
                    }

                    status.NetworkState = "Port disconnected / busy";
                    status.SignalPercent = 0;
                    status.IsConnected = false;
                }

                return status;
            });
        }



        public async Task<bool> GetWifiStatusAsync()
        {
            if (!IsPortFound)
                return false;

            string response = await SendAtCommandAsync("AT+WIFI?", 2000);
            var match = Regex.Match(response, @"\+?WIFI\s*:\s*(0|1)", RegexOptions.IgnoreCase);

            if (match.Success)
                return match.Groups[1].Value == "1";

            // Some firmware returns only a status value.
            var bareStatus = Regex.Match(response.Trim(), @"^[01]$");
            return bareStatus.Success && bareStatus.Value == "1";
        }

        public async Task<bool> SetWifiStatusAsync(bool enable)
        {
            if (!IsPortFound)
                return false;

            string response = await SendAtCommandAsync(enable ? "AT+WIFI=1" : "AT+WIFI=0", 3000);
            if (!response.Contains("OK", StringComparison.OrdinalIgnoreCase))
                return false;

            // Verify the modem's actual state instead of trusting the switch.
            bool actualState = await GetWifiStatusAsync();
            return actualState == enable;
        }

        public async Task<bool> SendSmsAsync(string phoneNumber, string message)
        {
            SmsSendResult result = await SendSmsWithStatusAsync(phoneNumber, message);
            return result == SmsSendResult.Success;
        }

        public async Task<SmsSendResult> SendSmsWithStatusAsync(string phoneNumber, string message)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber) || string.IsNullOrEmpty(message))
                return SmsSendResult.FailedBeforeSubmit;

            var candidates = new List<string>();

            void AddCandidate(string port)
            {
                if (!string.IsNullOrWhiteSpace(port) &&
                    !candidates.Contains(port, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(port);
                }
            }

            AddCandidate(ComPort);
            AddCandidate(AlternativeComPort);

            foreach (string detectedPort in DetectedPorts)
                AddCandidate(detectedPort);

            if (candidates.Count == 0)
            {
                var ports = await ScanAndSetPortAsync();
                foreach (string port in ports)
                    AddCandidate(port);
            }

            foreach (string currentPort in candidates)
            {
                SendSmsAttemptResult result =
                    await SendSmsOnPortAsync(currentPort, phoneNumber, message);

                if (result == SendSmsAttemptResult.Success)
                {
                    PromoteActivePort(currentPort);
                    return SmsSendResult.Success;
                }

                // Before the CMGS prompt, failover/retry is safe. Once the
                // prompt has been accepted, the SMS may already be submitted,
                // so never retry merely because the final response is missing.
                if (result == SendSmsAttemptResult.Submitted)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[SMS] Submission status is unknown on {currentPort}; " +
                        "automatic retry/failover is disabled to prevent duplicates.");
                    return SmsSendResult.UnknownAfterSubmit;
                }
            }

            var refreshedPorts = await ScanAndSetPortAsync();
            foreach (string currentPort in refreshedPorts)
            {
                if (candidates.Contains(currentPort, StringComparer.OrdinalIgnoreCase))
                    continue;

                SendSmsAttemptResult result =
                    await SendSmsOnPortAsync(currentPort, phoneNumber, message);

                if (result == SendSmsAttemptResult.Success)
                {
                    PromoteActivePort(currentPort);
                    return SmsSendResult.Success;
                }

                if (result == SendSmsAttemptResult.Submitted)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[SMS] Submission status is unknown on {currentPort}; " +
                        "automatic retry/failover is disabled to prevent duplicates.");
                    return SmsSendResult.UnknownAfterSubmit;
                }
            }

            return SmsSendResult.FailedBeforeSubmit;
        }

        private enum SendSmsAttemptResult
        {
            FailedBeforeSubmit,
            Submitted,
            Success
        }

        private async Task<SendSmsAttemptResult> SendSmsOnPortAsync(
            string currentPort,
            string phoneNumber,
            string message)
        {
            await ModemSerialGate.Semaphore.WaitAsync();
            try
            {
                using var port = CreateSerialPort(currentPort);
                port.Open();
                port.DiscardInBuffer();

                if (!await SendAndWaitAsync(port, "AT", "OK", 1500))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[SMS] {currentPort} is not responding to AT; trying fallback.");
                    return SendSmsAttemptResult.FailedBeforeSubmit;
                }

                if (!await SendAndWaitAsync(port, SmsStorageCommand, "OK", 2500))
                    return SendSmsAttemptResult.FailedBeforeSubmit;

                if (!await SendAndWaitAsync(port, "AT+CMGF=1", "OK", 2000))
                    return SendSmsAttemptResult.FailedBeforeSubmit;

                // The modem may currently be configured for UCS2 because its
                // incoming SMS responses are exposed as hexadecimal UCS2. In
                // text mode, first try the normal GSM character set. If the
                // modem rejects CMGS before showing the '>' prompt, retry the
                // same command using UCS2-encoded destination and body.
                if (await SendAndWaitAsync(port, "AT+CSCS=\"GSM\"", "OK", 2000))
                {
                    SendSmsAttemptResult gsmResult =
                        await SendSmsPayloadAsync(port, phoneNumber, message, false, 5000);

                    if (gsmResult == SendSmsAttemptResult.Success)
                        return SendSmsAttemptResult.Success;

                    // This is the critical boundary: after the prompt has been
                    // accepted, do not retry with another charset or port.
                    if (gsmResult == SendSmsAttemptResult.Submitted)
                        return SendSmsAttemptResult.Submitted;
                }

                // A prompt failure happens before the SMS is submitted, so it
                // is safe to retry with UCS2 on the same modem port.
                if (!await SendAndWaitAsync(port, "AT+CSCS=\"UCS2\"", "OK", 2000))
                    return SendSmsAttemptResult.FailedBeforeSubmit;

                SendSmsAttemptResult ucs2Result =
                    await SendSmsPayloadAsync(port, phoneNumber, message, true, 5000);

                return ucs2Result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"SMS send failed before submission on {currentPort}: {ex.Message}");
                return SendSmsAttemptResult.FailedBeforeSubmit;
            }
            finally
            {
                ModemSerialGate.Semaphore.Release();
            }
        }

        private static async Task<SendSmsAttemptResult> SendSmsPayloadAsync(
            SerialPort port,
            string phoneNumber,
            string message,
            bool ucs2,
            int promptTimeoutMs)
        {
            bool promptAccepted = false;

            try
            {
                string destination = ucs2
                    ? EncodeUcs2Hex(phoneNumber)
                    : phoneNumber;
                string body = ucs2
                    ? EncodeUcs2Hex(message)
                    : message;

                port.DiscardInBuffer();
                port.Write($"AT+CMGS=\"{destination}\"\r");

                string prompt = await ReadUntilPromptAsync(port, promptTimeoutMs);
                if (!prompt.Contains(">", StringComparison.Ordinal))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[SMS] CMGS prompt failed ({(ucs2 ? "UCS2" : "GSM")}):\n{prompt}");
                    return SendSmsAttemptResult.FailedBeforeSubmit;
                }

                promptAccepted = true;

                // From this point onward, never automatically retry. The modem
                // has accepted the text-entry phase and the final result may be
                // delayed even when the network already accepted the SMS.
                port.Write(body);
                port.Write("\x1A");

                string response = await ReadUntilTerminalAsync(port, 20000);
                bool success = Regex.IsMatch(
                    response,
                    @"(?:^|\r?\n)OK(?:\r?\n|$)",
                    RegexOptions.IgnoreCase) ||
                               response.Trim().EndsWith("OK", StringComparison.OrdinalIgnoreCase);

                System.Diagnostics.Debug.WriteLine(
                    $"[SMS] Send ({(ucs2 ? "UCS2" : "GSM")}): {(success ? "success" : "submitted/unknown")}\n{response}");

                return success
                    ? SendSmsAttemptResult.Success
                    : SendSmsAttemptResult.Submitted;
            }
            catch (Exception ex)
            {
                if (promptAccepted)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[SMS] Send ({(ucs2 ? "UCS2" : "GSM")}) reached CMGS prompt but final state is unknown: {ex.Message}");
                    return SendSmsAttemptResult.Submitted;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[SMS] Send ({(ucs2 ? "UCS2" : "GSM")}) failed before CMGS prompt: {ex.Message}");
                return SendSmsAttemptResult.FailedBeforeSubmit;
            }
        }

        private static string EncodeUcs2Hex(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            byte[] bytes = System.Text.Encoding.BigEndianUnicode.GetBytes(value);
            return Convert.ToHexString(bytes);
        }

        private static async Task<string> ReadUntilPromptAsync(SerialPort port, int timeoutMs)
        {
            var response = new System.Text.StringBuilder();
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(timeoutMs, 500));

            while (DateTime.UtcNow < deadline)
            {
                string chunk = port.ReadExisting();
                if (!string.IsNullOrEmpty(chunk))
                {
                    response.Append(chunk);
                    if (response.ToString().Contains(">", StringComparison.Ordinal))
                        break;

                    string text = response.ToString();
                    if (Regex.IsMatch(
                            text,
                            @"(?:^|\r?\n)(?:ERROR|\+CME ERROR[^\r\n]*)(?:\r?\n|$)",
                            RegexOptions.IgnoreCase))
                    {
                        break;
                    }
                }

                await Task.Delay(50);
            }

            return response.ToString();
        }

        private static async Task<bool> WaitForSerialResponseAsync(SerialPort port, string expected, int timeoutMs)
        {
            string response = await ReadUntilTerminalAsync(port, timeoutMs, expected);
            return response.Contains(expected, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string> ReadUntilTerminalAsync(SerialPort port, int timeoutMs, string? terminal = null)
        {
            var response = new System.Text.StringBuilder();
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(timeoutMs, 250));

            while (DateTime.UtcNow < deadline)
            {
                string chunk = port.ReadExisting();
                if (!string.IsNullOrEmpty(chunk))
                {
                    response.Append(chunk);
                    string text = response.ToString();
                    if (terminal != null && text.Contains(terminal, StringComparison.OrdinalIgnoreCase))
                        break;
                    if (terminal == null && Regex.IsMatch(text, @"(?:^|\r?\n)(?:OK|ERROR|\+CME ERROR[^\r\n]*)(?:\r?\n|$)", RegexOptions.IgnoreCase))
                        break;
                }

                await Task.Delay(50);
            }

            return response.ToString();
        }

        public async Task<bool> ForceHardwareHangupAsync()
        {
            if (!IsPortFound) return false;

            return await Task.Run(async () =>
            {
                try
                {

                    string response = await SendAtCommandAsync("ATH", 1000);


                    if (!response.Contains("OK"))
                    {
                        await SendAtCommandAsync("AT+CHUP", 1000);
                    }
                    return true;
                }
                catch
                {
                    return false;
                }
            });
        }


        public async Task<(string OperatorName, string NetworkType)> GetOperatorInfoAsync()
        {
            if (!IsPortFound)
                return (string.Empty, string.Empty);

            try
            {
                // COPS? reports the currently selected operator and, when supported,
                // the access technology (AcT). Do not use COPS=? because that performs
                // a network scan and can take a long time.
                string response = await SendAtCommandAsync("AT+COPS?");

                if (response.Contains("+COPS:", StringComparison.OrdinalIgnoreCase))
                {
                    string operatorName = string.Empty;
                    string networkType = string.Empty;

                    var nameMatch = Regex.Match(response, @"\+COPS:[^\r\n]*?""([^""]*)""");
                    if (nameMatch.Success)
                        operatorName = nameMatch.Groups[1].Value.Trim();

                    // +COPS: <mode>,<format>,<oper>[,<AcT>]
                    var actMatch = Regex.Match(response, @"\+COPS:\s*\d+\s*,\s*\d+\s*,\s*(?:""[^""]*""|[^,\r\n]+)\s*,\s*(\d+)");
                    if (actMatch.Success && int.TryParse(actMatch.Groups[1].Value, out int act))
                        networkType = GetNetworkTypeLabel(act);

                    return (operatorName, networkType);
                }
            }
            catch
            {
                // Preserve the last known dashboard value on transient AT failures.
            }

            return (string.Empty, string.Empty);
        }

        public async Task<string> GetOperatorNameAsync()
        {
            var info = await GetOperatorInfoAsync();
            return info.OperatorName;
        }

        private static string GetNetworkTypeLabel(int act)
        {
            return act switch
            {
                0 or 1 or 3 => "2G GSM",
                2 => "3G UMTS",
                4 => "3G HSDPA",
                5 => "3G HSUPA",
                6 => "3G HSPA",
                7 => "4G LTE",
                8 => "2G EC-GSM",
                9 => "4G LTE NB-IoT",
                10 => "4G LTE Cat-M1",
                _ => string.Empty
            };
        }

        public async Task<(int SignalPercent, string NetworkState, string OperatorName, string NetworkType)> GetModemStatusDetailedAsync()
        {
            if (!IsPortFound)
                return (-1, string.Empty, string.Empty, string.Empty);

            int signal = await GetSignalQualityAsync();
            var operatorInfo = await GetOperatorInfoAsync();
            string operatorName = operatorInfo.OperatorName;
            string networkType = operatorInfo.NetworkType;
            string networkState = string.Empty;

            try
            {
                string regResponse = await SendAtCommandAsync("AT+CGREG?");
                var match = Regex.Match(regResponse, @"\+CGREG:\s*\d+,(\d+)");

                if (match.Success)
                {
                    string state = match.Groups[1].Value;
                    string displayOperator = string.IsNullOrWhiteSpace(operatorName)
                        ? "Unknown"
                        : operatorName;

                    networkState = state switch
                    {
                        "1" => displayOperator,
                        "2" => $"{displayOperator} (Searching...)",
                        "3" => $"{displayOperator} (Access Denied)",
                        "5" => $"{displayOperator} (Roaming)",
                        _ => $"{displayOperator} (No Service)"
                    };
                }
            }
            catch
            {
                // Preserve the last known dashboard value on transient AT failures.
            }

            return (signal, networkState, operatorName, networkType);
        }

        public async Task<int> GetSignalQualityAsync()
        {
            if (!IsPortFound) return -1;

            try
            {
                string response = await SendAtCommandAsync("AT+CSQ");


                if (response.Contains("+CSQ:"))
                {
                    var parts = response.Split(':');
                    if (parts.Length > 1)
                    {
                        var values = parts[1].Trim().Split(',');
                        if (int.TryParse(values[0].Trim(), out int rssi))
                        {

                            if (rssi == 99) return 0;


                            int percentage = (int)Math.Round((rssi / 31.0) * 100);
                            return Math.Clamp(percentage, 0, 100);
                        }
                    }
                }
            }
            catch { }

            return -1;
        }

        public async Task<List<SmsMessage>> GetInboxSmsAsync()
        {
            return await GetStoredSmsAsync();
        }

    }
}
