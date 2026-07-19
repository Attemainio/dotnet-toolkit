namespace DotnetToolkit.McpServer.Contracts;

/// <summary>
/// A layered content-identity token (spec §7), e.g. <c>decl:a1b2c3d4e5f6|body:84c3f19a02bd</c>.
/// A response carries whichever layers the server currently supports; lease comparison is always
/// per supplied layer. Layers appear in canonical order (decl, body, refs, api) when formatted.
/// </summary>
public readonly struct ContentVersion
{
    private static readonly string[] Order = ["decl", "body", "refs", "api"];

    private readonly IReadOnlyDictionary<string, string> _layers;

    private ContentVersion(IReadOnlyDictionary<string, string> layers) => _layers = layers;

    public bool IsEmpty => _layers is null || _layers.Count == 0;

    public static ContentVersion Of(string? decl = null, string? body = null, string? refs = null, string? api = null)
    {
        var layers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (decl is not null) layers["decl"] = decl;
        if (body is not null) layers["body"] = body;
        if (refs is not null) layers["refs"] = refs;
        if (api is not null) layers["api"] = api;
        return new ContentVersion(layers);
    }

    public static ContentVersion Parse(string? token)
    {
        var layers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(token))
        {
            foreach (var part in token.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var colon = part.IndexOf(':');
                if (colon > 0)
                    layers[part[..colon]] = part[(colon + 1)..];
            }
        }
        return new ContentVersion(layers);
    }

    public string? Get(string layer) => _layers is not null && _layers.TryGetValue(layer, out var hash) ? hash : null;

    /// <summary>The layer names this token actually carries, in canonical order.</summary>
    public IReadOnlyList<string> Layers
    {
        get
        {
            if (IsEmpty)
                return [];
            var layers = _layers;
            return [.. Order.Where(layers.ContainsKey)];
        }
    }

    /// <summary>
    /// A copy carrying only <paramref name="layers"/>. A response that served a subset of the symbol must
    /// hand back a token describing that subset and no more: a token claiming layers whose content was
    /// never sent would satisfy a later, wider request and omit content the caller has never held.
    /// </summary>
    public ContentVersion Narrow(IEnumerable<string> layers)
    {
        if (IsEmpty)
            return this;
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var layer in layers)
        {
            if (Get(layer) is { } hash)
                kept[layer] = hash;
        }
        return new ContentVersion(kept);
    }

    /// <summary>True when this token carries every one of <paramref name="layers"/>.</summary>
    public bool Covers(IEnumerable<string> layers)
    {
        foreach (var layer in layers)
        {
            if (Get(layer) is null)
                return false;
        }
        return true;
    }

    /// <summary>
    /// True when, for every layer present in <paramref name="known"/>, this (current) version has the
    /// same hash. This is the lease predicate: all supplied layers match ⇒ <c>changed:false</c> (§8).
    /// An empty <paramref name="known"/> never satisfies (nothing was actually held).
    /// </summary>
    public bool Satisfies(ContentVersion known)
    {
        if (known.IsEmpty)
            return false;
        foreach (var (layer, hash) in known._layers)
        {
            if (Get(layer) != hash)
                return false;
        }
        return true;
    }

    /// <summary>
    /// True when no layer present in BOTH tokens disagrees. Unlike <see cref="Satisfies"/> this does not
    /// require the other token's layers to all be present here: a layer one side never computed is not
    /// evidence of a change. This is the correct test when comparing tokens produced by different tiers
    /// (e.g. a four-layer token held by the agent against a two-layer syntax-only token), where raw
    /// string equality would report a spurious mismatch.
    /// </summary>
    public bool AgreesWith(ContentVersion other)
    {
        if (IsEmpty || other.IsEmpty)
            return false;
        var shared = 0;
        foreach (var (layer, hash) in other._layers)
        {
            var mine = Get(layer);
            if (mine is null)
                continue;
            if (mine != hash)
                return false;
            shared++;
        }
        // No overlapping layer at all means nothing was actually verified.
        return shared > 0;
    }

    public override string ToString()
    {
        if (IsEmpty)
            return "";
        var layers = _layers;
        return string.Join('|', Order.Where(layers.ContainsKey).Select(l => $"{l}:{layers[l]}"));
    }
}
