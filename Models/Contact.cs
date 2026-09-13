using System;

namespace ModemController
{
    public sealed class Contact
    {
        public long Id { get; set; }
        public string Address { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public long CreatedAtUtcUnixMs { get; set; }
        public long UpdatedAtUtcUnixMs { get; set; }
    }
}
