namespace DotnetToolkit.McpServer.Output;

/// <summary>
/// Turns a hit's <see cref="ShapeFacts"/> into the one thing a caller actually wants from them: which
/// <c>get_symbol</c> include to pass next. Rendered as the terse <c>read</c> column beside
/// <c>search_index</c>'s <c>shape</c>, with its legend stated once per response.
/// </summary>
/// <remarks>
/// <c>shape</c> states the facts and leaves the inference to the reader, which is the right division
/// of labour only for a reader who reliably makes it. Measured against smaller local models, that
/// inference is exactly what does not happen: <c>L2342 M87</c> is read as a description and the whole
/// 2342-line type is fetched anyway. So this column is deliberately redundant with <c>shape</c> — it
/// carries no fact <c>shape</c> does not already carry, and exists only because a derivation nobody
/// performs is not information.
///
/// It is kept honest by two rules. It is absent whenever the default fetch is already right, so a
/// result of nothing but small symbols renders exactly as it did before the column existed. Once any
/// row does carry advice the tabular form gains the column for all of them and the silent rows pay an
/// empty cell, so the column is cheap rather than free — claiming otherwise would misprice it against
/// the very analysis meant to judge it. And every value names a real include string rather than a
/// mood, so following it is mechanical.
///
/// The thresholds below are the one part of this that is a guess rather than a measurement. Analysis
/// 3d in the dotnet-selfeval skill exists to price advice like this — follow the column, ignore it,
/// compare — and these constants are what it should move.
/// </remarks>
public static class ReadAdvice
{
    /// <summary>Legend for the read column, emitted once per response rather than repeated per row.</summary>
    /// <remarks>
    /// States the absence rule explicitly, because absence here is an assertion rather than the usual
    /// "not computed" — the same deliberate exception <see cref="SymbolShape.Legend"/> makes, and for
    /// the same reason: a column whose blank means nothing in particular is not worth its delimiter.
    /// It does not name the column itself: the legend is emitted under the key <c>read</c>, so a leading
    /// "read:" inside the value rendered as <c>read: "read: mem=…"</c> — the restatement analysis 3b
    /// exists to catch, shipped in the one field whose whole job is to be read once and understood.
    /// </remarks>
    public const string Legend =
        "mem=include:members out=include:bodyOutline then source:code@from-to code=source:code "
        + "all=include:all; absent=default fetch is right";

    /// <summary>
    /// Lines past which a whole-declaration fetch is worth redirecting when the caller stated no
    /// intent.
    /// </summary>
    /// <remarks>
    /// Set well above the size of an ordinary method so the column stays quiet on the results where it
    /// would only be noise. With an intent given, the caller has already said what it is after and the
    /// size question stops deciding anything — so this bound applies to the no-intent path alone.
    /// </remarks>
    private const int LargeDeclaration = 60;

    /// <summary>
    /// The include to pass next, or null when the default fetch is already the right call.
    /// </summary>
    /// <param name="intent">What the caller is about to do: <c>"edit"</c>, <c>"logic"</c>,
    /// <c>"surface"</c>, or null/unrecognized to derive the answer from the facts alone.</param>
    /// <param name="facts">The same counted facts <see cref="SymbolShape"/> renders.</param>
    /// <returns>One of <c>mem</c>, <c>out</c>, <c>code</c>, <c>all</c>, or null.</returns>
    public static string? For(string? intent, in ShapeFacts facts)
    {
        // A body patch needs the body-carrying contentVersion whatever the symbol looks like, so this
        // answer is not derived from the facts at all — which is also why stating the intent beats
        // reading the shape here.
        if (intent is "edit")
            return "all";

        // "What is its API" is a member-list question on a type and a signature question on everything
        // else — and the signature is what the default fetch already leads with.
        if (intent is "surface")
            return facts.MemberCount is > 0 ? "mem" : null;

        var lines = facts.LineCount ?? 0;

        // A caller after behaviour is better served by the source argument at any size: the default fetch
        // returns docs and reference counts and no code at all, so "small enough to fetch whole" is
        // not the question being asked.
        if (intent is "logic")
            return Route(facts, mapBody: facts.LandmarkCount is > 0 && lines >= LargeDeclaration);

        return lines < LargeDeclaration ? null : Route(facts, mapBody: facts.LandmarkCount is > 0);
    }

    /// <summary>
    /// The shared ordering: a type's member list answers "what is in here" without reading any of it,
    /// an outline maps a long body so one region can be sliced out, and code-without-docs is what is
    /// left when neither applies.
    /// </summary>
    private static string Route(in ShapeFacts facts, bool mapBody) =>
        facts.MemberCount is > 0 ? "mem" : mapBody ? "out" : "code";
}
