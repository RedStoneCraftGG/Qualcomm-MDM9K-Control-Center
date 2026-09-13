using System.Threading;

namespace ModemController
{
    internal static class ModemSerialGate
    {
        internal static readonly SemaphoreSlim Semaphore = new(1, 1);
    }
}
