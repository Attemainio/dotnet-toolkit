namespace Sample.Lib;

/// <summary>Transforms an integer into another integer.</summary>
/// <param name="value">The value to transform.</param>
/// <returns>The transformed value.</returns>
public delegate int Transform(int value);

/// <summary>Projects an input value onto a result of a different type.</summary>
/// <typeparam name="TInput">The input type.</typeparam>
/// <typeparam name="TResult">The projected result type.</typeparam>
/// <param name="input">The value to project.</param>
/// <returns>The projected result.</returns>
public delegate TResult Projector<TInput, TResult>(TInput input);

/// <summary>Holds delegate-typed members so delegate declaration, invocation and reference lookup all have a target.</summary>
public sealed class DelegateSample
{
    /// <summary>Reports progress as a whole percentage.</summary>
    /// <param name="percent">How far the run has progressed, 0 to 100.</param>
    public delegate void Progress(int percent);

    /// <summary>Raised after a transform has been applied.</summary>
    public event Transform? Applied;

    /// <summary>Doubles a value; the stock <see cref="Transform"/> target.</summary>
    /// <param name="value">The value to double.</param>
    /// <returns>Twice <paramref name="value"/>.</returns>
    public static int Double(int value) => value * 2;

    /// <summary>Applies a transform to a value and raises <see cref="Applied"/>.</summary>
    /// <param name="transform">The transform to invoke.</param>
    /// <param name="value">The value to transform.</param>
    /// <returns>The transformed value.</returns>
    public int Apply(Transform transform, int value)
    {
        var result = transform(value);
        Applied?.Invoke(result);
        return result;
    }

    /// <summary>Projects a value through a projector.</summary>
    /// <param name="projector">The projection to invoke.</param>
    /// <param name="value">The value to project.</param>
    /// <returns>The projected text.</returns>
    public string Describe(Projector<int, string> projector, int value) => projector(value);

    /// <summary>Reports completion through the callback.</summary>
    /// <param name="progress">The callback to invoke.</param>
    public void Report(Progress progress) => progress(100);

    /// <summary>Applies <see cref="Double"/> to a value.</summary>
    /// <param name="value">The value to double.</param>
    /// <returns>Twice <paramref name="value"/>.</returns>
    public int ApplyDouble(int value) => Apply(Double, value);
}
