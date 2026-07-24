using System;
using System.Collections.Generic;

namespace Sample.Lib;

/// <summary>Fixture type solely for exercising get_symbol's usings component.</summary>
public class UsingsSample
{
    public List<string> Names { get; } = new();

    public DateTime Stamp() => DateTime.UtcNow;
}
