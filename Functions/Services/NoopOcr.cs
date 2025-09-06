using Functions.Services.Abstractions;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Functions.Services;


public sealed class NoopOcr : IReceiptOcr
{
    public Task<string> ReadAsync(Stream image, CancellationToken ct) => Task.FromResult(string.Empty);
}