namespace ModemController
{
    public sealed class ModemIdentity
    {
        public string Manufacturer { get; init; } = "Unknown";
        public string Model { get; init; } = "Unknown";
        public string Platform { get; init; } = "Unknown";
        public string Hardware { get; init; } = "Unknown";
        public string Firmware { get; init; } = "Unknown";
        public string RawAti { get; init; } = string.Empty;
    }
}
