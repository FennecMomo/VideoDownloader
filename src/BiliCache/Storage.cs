using System.IO;
using System.Text.Json;

namespace BiliCache;

public sealed class Storage
{
    public string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliCache");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public T Load<T>(string name, T fallback)
    {
        string path = Path.Combine(Root, name);
        if (!File.Exists(path)) return fallback;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? fallback; }
        catch (JsonException)
        {
            File.Copy(path, path + ".invalid-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
            return fallback;
        }
    }
    public void Save<T>(string name, T data)
    {
        Directory.CreateDirectory(Root);
        string path = Path.Combine(Root, name);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(data, Options));
        File.Move(path + ".tmp", path, true);
    }
}
