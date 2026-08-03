namespace Sample.Lib;

/// <summary>Rename fixture: members that exist only so rename_symbol tests have a target no other test asserts on.</summary>
public sealed class RenameSample
{
    /// <summary>The seed value the other members derive from.</summary>
    public int Seed() => 7;

    /// <summary>Doubles <see cref="Seed"/>, giving the rename a same-member-scope reference to rewrite.</summary>
    public int Doubled() => Seed() * 2;
}

/// <summary>A separate type calling into <see cref="RenameSample"/>, so a rename spans more than one declaration.</summary>
public static class RenameSampleUser
{
    /// <summary>Adds one to the sample's seed.</summary>
    public static int Use(RenameSample sample) => sample.Seed() + 1;
}
