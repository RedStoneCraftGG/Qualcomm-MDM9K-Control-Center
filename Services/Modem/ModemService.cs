using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ModemController
{
    public static class ModemService
    {
        private const string SmsStorageCommand = "AT+CPMS=\"SM\",\"SM\",\"SM\"";
        private static SerialPort? _serialPort;

        public static bool IsConnected => _serialPort?.IsOpen == true;

        public static bool Connect(string portName, int baudRate = 115200)
        {
            ModemSerialGate.Semaphore.Wait();
            try
            {
                return ConnectInternal(portName, baudRate);
            }
            finally
            {
                ModemSerialGate.Semaphore.Release();
            }
        }

        private static bool ConnectInternal(string portName, int baudRate)
        {
            try
            {
                Disconnect();

                _serialPort = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 2000,
                    WriteTimeout = 2000,
                    NewLine = "\r\n"
                };

                _serialPort.Open();
                SendCommand("AT+CMGF=1");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to connect to modem: {ex.Message}");
                Disconnect();
                return false;
            }
        }

        public static async Task<bool> ConnectAsync(string portName, int baudRate = 115200)
        {
            await ModemSerialGate.Semaphore.WaitAsync();
            try
            {
                return await Task.Run(() => ConnectInternal(portName, baudRate));
            }
            finally
            {
                ModemSerialGate.Semaphore.Release();
            }
        }

        public static string SendCommand(string command, int waitTimeMs = 3000)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
                return string.Empty;

            lock (_serialPort)
            {
                try
                {
                    _serialPort.DiscardInBuffer();
                    _serialPort.Write(command + "\r");

                    var response = new System.Text.StringBuilder();
                    var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(waitTimeMs, 500));
                    bool receivedData = false;

                    while (DateTime.UtcNow < deadline)
                    {
                        string chunk = _serialPort.ReadExisting();
                        if (!string.IsNullOrEmpty(chunk))
                        {
                            response.Append(chunk);
                            receivedData = true;

                            string text = response.ToString();
                            if (Regex.IsMatch(text, @"(?:^|\r?\n)(?:OK|ERROR|COMMAND NOT SUPPORT|\+CME ERROR[^\r\n]*)(?:\r?\n|$)", RegexOptions.IgnoreCase))
                                break;
                        }

                        System.Threading.Thread.Sleep(receivedData ? 40 : 75);
                    }

                    return response.ToString();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error AT Command: {ex.Message}");
                    return string.Empty;
                }
            }
        }

        public static async Task<List<SmsMessage>> FetchInboxMessagesAsync()
        {
            await ModemSerialGate.Semaphore.WaitAsync();
            try
            {
                return await Task.Run(FetchInboxMessages);
            }
            finally
            {
                ModemSerialGate.Semaphore.Release();
            }
        }

        public static List<SmsMessage> FetchInboxMessages()
        {
            var list = new List<SmsMessage>();
            SendCommand(SmsStorageCommand, 2500);
            SendCommand("AT+CMGF=1", 1500);
            string response = SendCommand("AT+CMGL=\"ALL\"", 4000);
            ParseSmsResponse(response, list);
            return list;
        }

        public static async Task<List<SmsMessage>> FetchUnreadMessagesAsync()
        {
            await ModemSerialGate.Semaphore.WaitAsync();
            try
            {
                return await Task.Run(() =>
                {
                    var list = new List<SmsMessage>();
                    SendCommand(SmsStorageCommand, 2500);
                    SendCommand("AT+CMGF=1", 1500);
                    string response = SendCommand("AT+CMGL=\"REC UNREAD\"", 4000);
                    ParseSmsResponse(response, list);
                    return list;
                });
            }
            finally
            {
                ModemSerialGate.Semaphore.Release();
            }
        }

        private static void ParseSmsResponse(string response, List<SmsMessage> list)
        {
            list.AddRange(SmsParser.Parse(response));
        }

        public static async Task<bool> SendSmsAsync(string phoneNumber, string message)
        {
            await ModemSerialGate.Semaphore.WaitAsync();
            try
            {
                return await Task.Run(() => SendSms(phoneNumber, message));
            }
            finally
            {
                ModemSerialGate.Semaphore.Release();
            }
        }

        public static bool SendSms(string phoneNumber, string message)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
                return false;

            try
            {
                lock (_serialPort)
                {
                    _serialPort.DiscardInBuffer();
                    _serialPort.WriteLine($"AT+CMGS=\"{phoneNumber}\"");
                    System.Threading.Thread.Sleep(500);
                    _serialPort.Write(message + (char)26);
                    System.Threading.Thread.Sleep(1500);
                    return _serialPort.ReadExisting().Contains("OK", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        public static async Task DeleteSmsAsync(int index)
        {
            await ModemSerialGate.Semaphore.WaitAsync();
            try
            {
                await Task.Run(() => DeleteSms(index));
            }
            finally
            {
                ModemSerialGate.Semaphore.Release();
            }
        }

        public static bool DeleteSms(int index)
        {
            if (index < 0)
                return false;

            SendCommand(SmsStorageCommand, 2500);
            string response = SendCommand($"AT+CMGD={index}");
            return response.Contains("OK", StringComparison.OrdinalIgnoreCase);
        }

        public static async Task ClearSmsStorageAsync(IEnumerable<SmsMessage> messages)
        {
            var indices = new List<int>();
            foreach (var message in messages)
            {
                if (message.Index >= 0)
                    indices.Add(message.Index);
            }

            if (indices.Count == 0)
                return;

            await ModemSerialGate.Semaphore.WaitAsync();
            try
            {
                await Task.Run(() =>
                {
                    foreach (int index in indices)
                        DeleteSms(index);
                });
            }
            finally
            {
                ModemSerialGate.Semaphore.Release();
            }
        }

        public static void Disconnect()
        {
            try
            {
                if (_serialPort?.IsOpen == true)
                    _serialPort.Close();
            }
            catch
            {
            }
            finally
            {
                _serialPort?.Dispose();
                _serialPort = null;
            }
        }
    }
}
