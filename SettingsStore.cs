using System.Text.Json;

namespace CodexApiBridge;

public sealed class SettingsStore
{
    private readonly object _lock = new();
    private BridgeSettings _settings;

    public SettingsStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        _settings = BridgeSettings.Load(Path).Clone();
    }

    public string Path { get; }

    public BridgeSettings Current
    {
        get
        {
            lock (_lock)
            {
                return _settings.Clone();
            }
        }
    }

    public void Save(BridgeSettings settings)
    {
        lock (_lock)
        {
            _settings = settings.Clone();
            _settings.Save(Path);
        }
    }

    public JsonObjectEnvelope Snapshot()
    {
        lock (_lock)
        {
            return new JsonObjectEnvelope
            {
                ConfigPath = Path,
                Settings = _settings.Clone()
            };
        }
    }
}

public sealed class JsonObjectEnvelope
{
    public string ConfigPath { get; set; } = "";
    public BridgeSettings Settings { get; set; } = new();
}
