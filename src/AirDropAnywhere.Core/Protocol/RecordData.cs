using System.Collections.Generic;
using System.Linq;

namespace AirDropAnywhere.Core.Protocol
{
    internal class RecordData
    {
        public IEnumerable<string> ValidatedEmailHashes { get; private set; } = Enumerable.Empty<string>();
        public IEnumerable<string> ValidatedPhoneHashes { get; private set; } = Enumerable.Empty<string>();
    }
}