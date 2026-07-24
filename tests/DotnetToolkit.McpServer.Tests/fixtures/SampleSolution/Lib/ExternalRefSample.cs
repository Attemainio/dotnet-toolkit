using System;
using System.Collections.Generic;
using System.Linq;

namespace Sample.Lib;

/// <summary>
/// Fixture type solely for exercising external-reference indexing: implements a BCL interface,
/// constructs a BCL type, and calls a reduced extension method.
/// </summary>
public sealed class ExternalRefSample : IDisposable
{
    public void Dispose() { }

    public List<int> MakeList() => new List<int>();

    public IEnumerable<int> EvensFrom(List<int> values) => values.Where(v => v % 2 == 0);
}
