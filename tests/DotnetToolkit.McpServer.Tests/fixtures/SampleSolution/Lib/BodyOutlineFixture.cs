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

    /// <summary>
    /// Long enough to clear the worthwhile-line threshold but almost entirely sequential, so its outline
    /// holds a single landmark and describes nearly none of the body - the density case the note warns about.
    /// </summary>
    public static int LongButLinear(int seed)
    {
        var a = seed + 1;
        var b = a * 2;
        var c = b - 3;
        var d = c * c;
        var e = d + a;
        var f = e - b;
        var g = f * 2;
        var h = g + c;
        var i = h - d;
        var j = i * 3;
        var k = j + e;
        var l = k - f;
        var m = l * 2;
        var n = m + g;
        var o = n - h;
        var p = o * 4;
        var q = p + i;
        var r = q - j;
        var s = r * 2;
        var t = s + k;
        var u = t - l;
        var v = u * 5;
        var w = v + m;
        var x = w - n;
        var y = x * 2;
        var z = y + o;

        if (z < 0)
        {
            z = -z;
        }

        return z + p + q + r + s + t + u + v + w + x + y;
    }
}
