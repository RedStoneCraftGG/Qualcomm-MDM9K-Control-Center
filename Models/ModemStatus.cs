namespace ModemController
{
    public class ModemStatus
    {
        public int SignalPercent { get; set; }
        public string NetworkState { get; set; } = "Unknown";
        public bool IsConnected { get; set; }
    }
}
