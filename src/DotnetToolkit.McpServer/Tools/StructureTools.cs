using System.ComponentModel;
using System.Text;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Workspace;
using ModelContextProtocol.Server;

namespace DotnetToolkit.McpServer.Tools;

[McpServerToolType]
public static class StructureTools
{
    private const int MaxLimit = 100;

    [McpServerTool(Name = "project_tree")]
    [Description("Folder tree of the repository's C# code with per-folder file/type counts. Use this to orient in the codebase instead of ls/Glob.")]
    public static async Task<string> ProjectTree(
        ProjectIndex index,
        SolutionLocator locator,
        [Description("Root-relative folder to start from; omit for the repo root.")] string? path = null,
        [Description("Folder depth to expand (default 3).")] int depth = 3,
        [Description("compact | json")] string? format = null)
    {
        await index.EnsureFreshAsync();
        var prefix = NormalizeDir(path);
        var root = BuildTree(index, prefix);
        if (root.FileCount == 0)
            return $"no indexed .cs files under: {(prefix.Length == 0 ? "." : prefix)}";

        if (Formats.Parse(format ?? locator.Config.DefaultFormat) == OutputFormat.Json)
            return Formats.ToJson(ToDto(prefix.Length == 0 ? "." : prefix.TrimEnd('/'), root, depth));

        var sb = new StringBuilder();
        sb.Append(prefix.Length == 0 ? "." : prefix.TrimEnd('/')).Append('/')
          .Append(" (").Append(root.FileCount).Append(" files, ").Append(root.TypeCount).Append(" types)\n");
        RenderDir(sb, root, indent: 1, remainingDepth: depth);
        return sb.ToString().TrimEnd('\n');
    }

    [McpServerTool(Name = "list_folder")]
    [Description("Contents of one folder ('ensemble'): subfolders with counts, and each file with its top-level types and doc summary. Prefer this over reading files to learn what a folder contains.")]
    public static async Task<string> ListFolder(
        ProjectIndex index,
        SolutionLocator locator,
        [Description("Root-relative folder path.")] string path,
        [Description("compact | json")] string? format = null)
    {
        await index.EnsureFreshAsync();
        var prefix = NormalizeDir(path);
        var node = BuildTree(index, prefix);
        if (node.FileCount == 0)
            return $"no indexed .cs files under: {(prefix.Length == 0 ? "." : prefix)}";

        if (Formats.Parse(format ?? locator.Config.DefaultFormat) == OutputFormat.Json)
            return Formats.ToJson(ToDto(prefix.Length == 0 ? "." : prefix.TrimEnd('/'), node, depth: 1));

        var sb = new StringBuilder();
        sb.Append(prefix.Length == 0 ? "." : prefix.TrimEnd('/')).Append('/')
          .Append(": ").Append(node.Dirs.Count).Append(" dirs, ").Append(node.Files.Count).Append(" files\n");
        foreach (var (name, sub) in node.Dirs)
            sb.Append(name).Append("/ (").Append(sub.FileCount).Append(" files, ").Append(sub.TypeCount).Append(" types)\n");
        foreach (var (name, entry) in node.Files)
            AppendFileLine(sb, name, entry);
        return sb.ToString().TrimEnd('\n');
    }

    [McpServerTool(Name = "outline")]
    [Description("Full member outline of one .cs file: types, signatures, and doc summaries in compact form. Use this INSTEAD of reading the file, unless you are about to edit specific lines.")]
    public static async Task<string> Outline(
        ProjectIndex index,
        SolutionLocator locator,
        [Description("Root-relative path of a .cs file.")] string path,
        [Description("Include private/internal members (default false).")] bool include_private = false,
        [Description("compact | json")] string? format = null)
    {
        await index.EnsureFreshAsync();
        var rel = path.Replace('\\', '/').TrimStart('/');
        var entry = index.GetFile(rel);
        if (entry is null)
            return $"not indexed: {rel} (not a .cs file under the root, or excluded)";

        return Formats.Parse(format ?? locator.Config.DefaultFormat) == OutputFormat.Json
            ? Formats.ToJson(new { file = rel, entry.Namespaces, entry.Types })
            : OutlineRenderer.RenderFile(rel, entry, include_private);
    }

    [McpServerTool(Name = "find_symbol")]
    [Description("Find C# declarations (types and members) by name substring. Fast and available immediately; use this instead of Grep to locate a class, method, or property.")]
    public static async Task<string> FindSymbol(
        ProjectIndex index,
        SolutionLocator locator,
        [Description("Name or name fragment, case-insensitive.")] string query,
        [Description("Optional kind filter: class|interface|struct|record|enum|delegate|method|ctor|property|field|event.")] string? kind = null,
        [Description("Max results (default 20, cap 100).")] int limit = 20,
        [Description("compact | json")] string? format = null)
    {
        await index.EnsureFreshAsync();
        limit = Math.Clamp(limit, 1, MaxLimit);
        var (hits, total) = index.FindSymbol(query, kind, limit);

        if (Formats.Parse(format ?? locator.Config.DefaultFormat) == OutputFormat.Json)
            return Formats.ToJson(new { total, hits });

        var rows = hits.Select(h => new[]
        {
            h.Kind,
            h.FqName,
            $"{h.File}:{h.Line}",
            h.Signature ?? CompactFormatter.FirstSentence(h.Doc) ?? "",
        }).ToList();
        return CompactFormatter.Table("symbols", ["kind", "symbol", "file:line", "info"], rows, total);
    }

    // ---- helpers -------------------------------------------------------------

    private sealed class DirNode
    {
        public SortedDictionary<string, DirNode> Dirs { get; } = new(StringComparer.Ordinal);
        public List<(string Name, FileEntry Entry)> Files { get; } = [];
        public int FileCount { get; set; }
        public int TypeCount { get; set; }
    }

    private static string NormalizeDir(string? path)
    {
        var p = (path ?? "").Replace('\\', '/').Trim('/', ' ');
        return p.Length == 0 || p == "." ? "" : p + "/";
    }

    private static DirNode BuildTree(ProjectIndex index, string prefix)
    {
        var root = new DirNode();
        foreach (var (rel, entry) in index.Snapshot())
        {
            if (prefix.Length > 0 && !rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var remainder = rel[prefix.Length..];
            var segments = remainder.Split('/');
            var node = root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (!node.Dirs.TryGetValue(segments[i], out var child))
                    node.Dirs[segments[i]] = child = new DirNode();
                node = child;
            }
            node.Files.Add((segments[^1], entry));
        }
        ComputeCounts(root);
        return root;
    }

    private static void ComputeCounts(DirNode node)
    {
        node.FileCount = node.Files.Count;
        node.TypeCount = node.Files.Sum(f => f.Entry.Types.Count);
        foreach (var child in node.Dirs.Values)
        {
            ComputeCounts(child);
            node.FileCount += child.FileCount;
            node.TypeCount += child.TypeCount;
        }
    }

    private static void RenderDir(StringBuilder sb, DirNode node, int indent, int remainingDepth)
    {
        foreach (var (name, sub) in node.Dirs)
        {
            sb.Append(' ', indent * 2).Append(name).Append('/')
              .Append(" (").Append(sub.FileCount).Append(" files, ").Append(sub.TypeCount).Append(" types)\n");
            if (remainingDepth > 1)
                RenderDir(sb, sub, indent + 1, remainingDepth - 1);
        }
        foreach (var (name, entry) in node.Files)
        {
            sb.Append(' ', indent * 2);
            AppendFileLine(sb, name, entry);
        }
    }

    private static void AppendFileLine(StringBuilder sb, string name, FileEntry entry)
    {
        sb.Append(name);
        var typeNames = entry.Types.Select(t => t.Name).ToList();
        if (typeNames.Count > 0)
        {
            sb.Append(": ").Append(string.Join(", ", typeNames.Take(6)));
            if (typeNames.Count > 6)
                sb.Append(" +").Append(typeNames.Count - 6);
        }
        var doc = entry.Types.Select(t => CompactFormatter.FirstSentence(t.Doc)).FirstOrDefault(d => d is not null);
        if (doc is not null)
            sb.Append("  // ").Append(doc);
        sb.Append('\n');
    }

    private static object ToDto(string name, DirNode node, int depth) => new
    {
        dir = name,
        files = node.FileCount,
        types = node.TypeCount,
        children = depth <= 0
            ? null
            : (object)node.Dirs.Select(kv => ToDto(kv.Key, kv.Value, depth - 1))
                .Cast<object>()
                .Concat(node.Files.Select(f => (object)new
                {
                    file = f.Name,
                    types = f.Entry.Types.Select(t => t.Name),
                }))
                .ToList(),
    };
}
