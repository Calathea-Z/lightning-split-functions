using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Functions.Services.Abstractions
{
    public interface IReceiptNormalizer
    {
        Task<string> NormalizeAsync(string rawText, IDictionary<string, object?>? hints = null, CancellationToken ct = default);
    }
}
