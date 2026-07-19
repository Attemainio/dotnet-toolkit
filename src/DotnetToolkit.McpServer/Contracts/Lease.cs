namespace DotnetToolkit.McpServer.Contracts;

/// <summary>
/// Standard lease outcome for a content-bearing request (spec §8). Given the caller's optional
/// <c>knownVersion</c> and <c>refetch</c> flag, decides whether content must be transmitted.
/// </summary>
public static class Lease
{
    /// <summary>Terse by design: this string is emitted on every lease hit, so its token cost recurs.</summary>
    public const string RefetchHint = "Content omitted; resend with refetch:true if you no longer hold it.";

    public readonly record struct Decision(bool Changed, string? HeldVersion)
    {
        public bool OmitContent => !Changed;
    }

    /// <summary>
    /// <paramref name="current"/> is the live version. Returns <c>Changed=false</c> only when the
    /// caller supplied a <c>knownVersion</c>, did not force <c>refetch</c>, and every supplied layer
    /// still matches — in which case content is omitted and <c>heldVersion</c> echoes what was held.
    /// </summary>
    public static Decision Evaluate(ContentVersion current, string? knownVersion, bool refetch) =>
        Evaluate(current, knownVersion, refetch, requiredLayers: null);

    /// <summary>
    /// As above, but for a request that asked for a specific component set. <paramref name="requiredLayers"/>
    /// are the layers that set is derived from; content is transmitted whenever the held token does not
    /// carry all of them, even if every layer it does carry still matches.
    ///
    /// Without this, partial fetches silently lose data: a caller that fetched signature-only holds a
    /// decl token, and <see cref="ContentVersion.Satisfies"/> compares only supplied layers — so a later
    /// request for the source would match on decl alone, report <c>changed:false</c>, and omit a body the
    /// caller has never seen. The caller cannot detect this; it looks exactly like an unchanged symbol.
    /// </summary>
    public static Decision Evaluate(
        ContentVersion current, string? knownVersion, bool refetch, IEnumerable<string>? requiredLayers)
    {
        if (refetch || string.IsNullOrWhiteSpace(knownVersion))
            return new Decision(Changed: true, HeldVersion: null);

        var known = ContentVersion.Parse(knownVersion);
        // Only layers the server actually computed can be demanded of the caller. refs/api stay null
        // until the semantic index materializes them, and requiring a layer that does not exist would
        // mean never granting a lease at all on a workspace that has not finished indexing.
        if (requiredLayers is not null && !known.Covers(requiredLayers.Where(l => current.Get(l) is not null)))
            return new Decision(Changed: true, HeldVersion: null);

        return current.Satisfies(known)
            ? new Decision(Changed: false, HeldVersion: knownVersion)
            : new Decision(Changed: true, HeldVersion: null);
    }
}
