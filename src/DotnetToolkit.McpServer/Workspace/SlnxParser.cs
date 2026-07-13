using System.Xml.Linq;

namespace DotnetToolkit.McpServer.Workspace;

/// <summary>
/// Minimal .slnx reader used as a fallback when MSBuildWorkspace cannot open the
/// XML solution format directly: extracts project paths so each can be opened individually.
/// </summary>
public static class SlnxParser
{
    public static List<string> ProjectPaths(string slnxPath)
    {
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(slnxPath))!;
        return XDocument.Load(slnxPath)
            .Descendants("Project")
            .Select(p => p.Attribute("Path")?.Value)
            .OfType<string>()
            .Select(p => Path.GetFullPath(Path.Combine(baseDir, p.Replace('\\', '/'))))
            .Where(File.Exists)
            .ToList();
    }
}
