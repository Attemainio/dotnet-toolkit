namespace Lib;

/// <summary>
/// Clean on disk: every member reads instance state, so CA1822 does not fire. The .editorconfig raises
/// CA1822 to error for this file only, and the test patches <see cref="Doubled"/> into a body that stops
/// touching instance state — an analyzer error the compile rungs cannot see.
/// </summary>
internal sealed class AnalyzerBlockSample
{
    private readonly int _seed = 3;

    /// <summary>Reads the seed field.</summary>
    internal int Seeded() => _seed;

    /// <summary>Calls an instance member, so it too is not a CA1822 candidate.</summary>
    internal int Doubled() => Seeded() * 2;
}
