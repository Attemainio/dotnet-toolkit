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
