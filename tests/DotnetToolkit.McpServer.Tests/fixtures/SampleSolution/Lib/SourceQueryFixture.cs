using System;

namespace Sample.Lib;

/// <summary>Attribute/comment stripping fixture for source query modifier tests.</summary>
public static class SourceQueryFixture
{
    // A standalone comment line, dropped only when -comments is requested.
    [Obsolete]
    public static int WithOwnLineAttribute() => 1;

    [Obsolete] public static int WithInlineAttribute() => 2; // trailing comment stays even with -comments
}
