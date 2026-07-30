namespace Sample.Lib;

/// <summary>Members that declare their own generic type parameters, distinct from a generic container.</summary>
public sealed class GenericSample
{
    /// <summary>Returns the value unchanged.</summary>
    public int Pick(int only) => only;

    /// <summary>Returns the value unchanged, generically.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    public T Pick<T>(T only) => only;

    /// <summary>Scales a count, with a body worth rewriting in a patch test.</summary>
    /// <typeparam name="T">Names the caller's element type; unused by the computation.</typeparam>
    public int Scale<T>(int value)
    {
        return value * 2;
    }
}
