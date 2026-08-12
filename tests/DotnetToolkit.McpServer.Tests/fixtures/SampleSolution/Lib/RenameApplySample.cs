namespace Sample.Lib;

/// <summary>
/// Rename fixture owned by the one rename test that APPLIES to disk, and read by nothing else.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="RenameSample"/>, which the dry-run rename tests assert on. Every
/// test in the integration class shares a single fixture copy, so an applied rename mutates the target out
/// from under whichever sibling xUnit happens to schedule next — and xUnit's method order changes whenever
/// the assembly does, which is why that shows up as a test failing for unrelated reasons.
/// </remarks>
public sealed class RenameApplySample
{
    /// <summary>The seed value the other members derive from.</summary>
    public int Seed() => 7;

    /// <summary>Doubles <see cref="Seed"/>, giving the rename a same-member-scope reference to rewrite.</summary>
    public int Doubled() => Seed() * 2;
}

/// <summary>A separate type calling into <see cref="RenameApplySample"/>, so the rename spans more than one declaration.</summary>
public static class RenameApplySampleUser
{
    /// <summary>Adds one to the sample's seed.</summary>
    public static int Use(RenameApplySample sample) => sample.Seed() + 1;
}
