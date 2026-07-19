namespace Sample.Lib;

/// <summary>Extension methods, for get_scope's extension resolution.</summary>
public static class WidgetExtensions
{
    public static int SpinTwice(this IWidget widget, int turns) => widget.Spin(turns) * 2;
}

/// <summary>Entry of a deliberate multi-hop chain: Start -> Middle -> Deep -> Widget.Spin.</summary>
public sealed class Pipeline
{
    private readonly Widget _widget = new();

    public int Start(int turns) => Middle(turns);

    private int Middle(int turns) => Deep(turns);

    private int Deep(int turns) => _widget.Spin(turns);
}
