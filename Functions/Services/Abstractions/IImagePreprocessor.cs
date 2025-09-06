using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Functions.Services.Abstractions
{
    public interface IImagePreprocessor
    {
        Task<Stream> PrepareAsync(Stream original, CancellationToken ct);
    }
}
