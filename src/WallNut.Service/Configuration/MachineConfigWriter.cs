using System.Text.Json;
using System.Text.Json.Nodes;

namespace WallNut.Service.Configuration;

/// <summary>
/// Changes the machine settings file (config.json) in place: only the parts being changed, keeping
/// anything else in the file. Writes atomically and keeps one backup (config.json.bak). The running
/// service picks the change up on its own.
/// </summary>
public sealed class MachineConfigWriter(string path)
{
    private static readonly JsonDocumentOptions Read = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions Write = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public string Path { get; } = path;

    public void Update(Action<JsonObject> change)
    {
        lock (Gate)
        {
            var root = File.Exists(Path)
                ? JsonNode.Parse(File.ReadAllText(Path), documentOptions: Read) as JsonObject ?? new JsonObject()
                : new JsonObject();
            var wallnut = root["WallNut"] as JsonObject ?? (JsonObject)(root["WallNut"] = new JsonObject());
            change(wallnut);

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (File.Exists(Path)) File.Copy(Path, Path + ".bak", overwrite: true);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(Write));
            File.Move(temp, Path, overwrite: true);
        }
    }

    /// <summary>The WallNut:Endpoints object, created if missing.</summary>
    public static JsonObject Endpoints(JsonObject wallnut) =>
        wallnut["Endpoints"] as JsonObject ?? (JsonObject)(wallnut["Endpoints"] = new JsonObject());

    /// <summary>The stored password for an endpoint, if any (case-insensitive id lookup, like configuration).</summary>
    public string? StoredPassword(string id)
    {
        lock (Gate)
        {
            if (!File.Exists(Path)) return null;
            var root = JsonNode.Parse(File.ReadAllText(Path), documentOptions: Read) as JsonObject;
            var endpoints = root?["WallNut"]?["Endpoints"] as JsonObject;
            var match = endpoints?.FirstOrDefault(kv => kv.Key.Equals(id, StringComparison.OrdinalIgnoreCase)).Value;
            return match?["Password"]?.GetValue<string>();
        }
    }
}
