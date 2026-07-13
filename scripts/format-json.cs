// Pretty-prints a JSON file. Used to inspect the compact caches under
// .claude/dotnet-toolkit/cache/ (index.json, devlog-index.json) without a Python dependency.
// Run: dotnet run scripts/format-json.cs -- <path-to-json>

using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: dotnet run scripts/format-json.cs -- <path-to-json>");
    return 1;
}

var path = args[0];
if (!File.Exists(path))
{
    Console.Error.WriteLine($"not found: {path}");
    return 1;
}

var node = JsonNode.Parse(File.ReadAllText(path));
Console.WriteLine(node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
return 0;
