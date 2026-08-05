using System;
using System.Runtime.CompilerServices;

namespace Sample.Lib;

/// <summary>
/// An attribute whose every argument the compiler supplies from the use site, the shape xUnit v3's
/// [Fact] and [Theory] have.
/// </summary>
/// <remarks>
/// Nothing is written between the brackets at a use site, so get_symbol's attributes component must
/// report no arguments for it — reporting the caller-info values put an absolute machine path into a
/// response whose every other path is repo-relative.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TracedAttribute : Attribute
{
    /// <summary>Records where the attribute was applied.</summary>
    /// <param name="sourceFilePath">Supplied by the compiler from the use site's file.</param>
    /// <param name="sourceLineNumber">Supplied by the compiler from the use site's line.</param>
    public TracedAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
    {
        SourceFilePath = sourceFilePath;
        SourceLineNumber = sourceLineNumber;
    }

    /// <summary>The file the attribute was applied in.</summary>
    public string? SourceFilePath { get; }

    /// <summary>The line the attribute was applied on.</summary>
    public int SourceLineNumber { get; }
}

/// <summary>Members carrying attributes with and without author-written arguments.</summary>
public static class AttributeArgumentSample
{
    /// <summary>Carries an attribute written with no arguments at all.</summary>
    [Traced]
    public static int Bare() => 0;

    /// <summary>Carries an attribute with an argument the author actually wrote.</summary>
    [Obsolete("call Bare instead")]
    public static int Legacy() => 1;
}
