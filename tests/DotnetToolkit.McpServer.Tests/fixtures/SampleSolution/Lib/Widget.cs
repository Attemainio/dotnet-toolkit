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
