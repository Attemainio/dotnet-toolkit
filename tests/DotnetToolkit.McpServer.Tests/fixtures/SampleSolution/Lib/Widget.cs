namespace Sample.Lib;

public interface IWidget
{
    int Spin(int turns);
}

/// <summary>A spinning widget.</summary>
public class Widget : IWidget
{
    /// <summary>Spins the widget.</summary>
    public int Spin(int turns) => turns * 2;
}

/// <summary>A second widget implementation, for implementations traversal.</summary>
public sealed class TurboWidget : IWidget
{
    public int Spin(int turns) => turns * 4;
}

/// <summary>Base gear with an overridable ratio.</summary>
public abstract class GearBase
{
    public virtual int Ratio() => 1;
}

public sealed class HighGear : GearBase
{
    public override int Ratio() => 5;
}

/// <summary>
/// An abstract intermediate, so GearBase's derived list mixes an abstract type with concrete leaves —
/// the shape that makes get_type_hierarchy's isAbstract flag load-bearing rather than decorative.
/// </summary>
public abstract class MidGear : GearBase
{
    public override int Ratio() => 3;
}

/// <summary>Doc-section filter fixture for search_index's xmlDoc filter tests.</summary>
public static class DocSectionsFixture
{
    /// <returns>Always zero.</returns>
    /// <remarks>Has both returns and remarks, for the xmlDoc AND/exclude tests.</remarks>
    public static int Full() => 0;

    /// <returns>Always zero.</returns>
    public static int ReturnsOnly() => 0;

    public static int Undocumented() => 0;
}
