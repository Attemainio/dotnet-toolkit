namespace Lib;

/// <summary>
/// The same shape as <see cref="AnalyzerBlockSample"/>, but its .editorconfig section leaves CA1822 at
/// warning severity — so the identical patch must be reported and must still apply.
/// </summary>
internal sealed class AnalyzerAdvisorySample
{
    private readonly int _seed = 5;

    /// <summary>Reads the seed field.</summary>
    internal int Seeded() => _seed;

    /// <summary>Calls an instance member, so it too is not a CA1822 candidate.</summary>
    internal int Doubled() => Seeded() * 2;
}
