using System;
using System.Collections.Generic;

namespace Sample.Lib;

/// <summary>Control-flow fixture for get_symbol's bodyOutline component.</summary>
public static class BodyOutlineFixture
{
    /// <summary>Exercises switch/case, foreach, if, and try/catch/finally landmarks.</summary>
    public static string Classify(object node, IEnumerable<string> names)
    {
        var result = "";
        switch (node)
        {
            case int n when n > 0:
                result = "positive";
                break;
            case int:
                result = "nonpositive";
                break;
            default:
                result = "other";
                break;
        }

        foreach (var name in names)
        {
            if (name.Length > 3)
            {
                result += name;
            }
        }

        try
        {
            result += "!";
        }
        catch (InvalidOperationException ex)
        {
            result = ex.Message;
        }
        finally
        {
            result += ".";
        }

        return result;
    }

    /// <summary>Too short to clear the outline's worthwhile-line threshold.</summary>
    public static int TooShortForOutline(int x) => x + 1;
}
