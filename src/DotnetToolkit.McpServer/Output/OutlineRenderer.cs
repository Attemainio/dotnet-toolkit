using System.Text;
using DotnetToolkit.McpServer.Indexing;

namespace DotnetToolkit.McpServer.Output;

/// <summary>Renders file/type outlines in the compact format (kind codes + // doc summaries).</summary>
public static class OutlineRenderer
{
    public static string RenderFile(string relPath, FileEntry entry, bool includePrivate)
    {
        var sb = new StringBuilder();
        sb.Append(relPath);
        if (entry.Namespaces.Count > 0)
            sb.Append(" ns ").Append(string.Join(", ", entry.Namespaces));
        sb.Append('\n');
        foreach (var type in entry.Types)
            RenderType(sb, type, indent: 0, includePrivate);
        return sb.ToString().TrimEnd('\n');
    }

    public static void RenderType(StringBuilder sb, TypeEntry type, int indent, bool includePrivate)
    {
        if (!includePrivate && !type.IsPublic)
            return;
        Indent(sb, indent);
        sb.Append(type.Kind).Append(' ').Append(type.Name);
        if (type.Bases.Length > 0)
            sb.Append(" : ").Append(string.Join(", ", type.Bases));
        AppendModifierMarkers(sb, type.Modifiers);
        AppendDoc(sb, type.Doc);
        sb.Append('\n');

        foreach (var member in type.Members)
        {
            if (!includePrivate && !member.IsPublic)
                continue;
            Indent(sb, indent + 1);
            sb.Append(member.Kind).Append(' ').Append(member.Signature);
            AppendDoc(sb, member.Doc);
            sb.Append('\n');
        }
        foreach (var nested in type.Nested)
            RenderType(sb, nested, indent + 1, includePrivate);
    }

    private static void AppendModifierMarkers(StringBuilder sb, string modifiers)
    {
        foreach (var marker in new[] { "static", "abstract" })
        {
            if (modifiers.Contains(marker, StringComparison.Ordinal))
                sb.Append("  [").Append(marker).Append(']');
        }
    }

    private static void AppendDoc(StringBuilder sb, string? doc)
    {
        if (CompactFormatter.FirstSentence(doc) is { } summary)
            sb.Append("  // ").Append(summary);
    }

    private static void Indent(StringBuilder sb, int level) => sb.Append(' ', level * 2);
}
