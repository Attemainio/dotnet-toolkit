namespace Sample.Lib;

/// <summary>Extension methods, for get_scope's extension resolution.</summary>
public static class WidgetExtensions
{
    public static int SpinTwice(this IWidget widget, int turns) => widget.Spin(turns) * 2;
}

/// <summary>
/// Two overloads sharing one name, so search_index has a case where a hit cannot be pinned to a single
/// line: the syntax index keys members without their parameter lists, so both collapse to one name.
/// </summary>
public static class Overloads
{
    public static int Ambiguous(int only) => only;

    public static int Ambiguous(int first, int second) => first + second;
}

/// <summary>Entry of a deliberate multi-hop chain: Start -> Middle -> Deep -> Widget.Spin.</summary>
public sealed class Pipeline
{
    private readonly Widget _widget = new();

    public int Start(int turns) => Middle(turns);

    private int Middle(int turns) => Deep(turns);

    private int Deep(int turns) => _widget.Spin(turns);
}
