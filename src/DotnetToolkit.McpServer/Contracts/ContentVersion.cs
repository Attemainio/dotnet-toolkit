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

    public override string ToString()
    {
        if (IsEmpty)
            return "";
        var layers = _layers;
        return string.Join('|', Order.Where(layers.ContainsKey).Select(l => $"{l}:{layers[l]}"));
    }
}
