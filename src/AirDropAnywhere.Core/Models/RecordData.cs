using System.Collections.Generic;
using System.Linq;

namespace AirDropAnywhere.Core.Models
{
    public class RecordData
    {
        public IEnumerable<string> ValidatedEmailHashes { get; private set; } = Enumerable.Empty<string>();
        public IEnumerable<string> ValidatedPhoneHashes { get; private set; } = Enumerable.Empty<string>();
    }
}