using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WatchDog.Shared;

// Kept byte-identical in Watch Dog and VibeSkuffle. No client-specific dependencies.
internal sealed class SharedRescueSettings : IDisposable
{
    internal sealed record State(bool Enabled, string[] ExcludedAccounts, long Revision);
    private static readonly Lazy<SharedRescueSettings> Default = new(() => new(DefaultPath));
    internal static SharedRescueSettings Instance => Default.Value;
    internal static string DefaultPath => Path.Combine(
        Environment.GetEnvironmentVariable("WATCHDOG_SHARED_RESCUE_ROOT") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Skua", "WatchDog"),
        "rescue-settings.json");
    private readonly string _path;
    private readonly Mutex _writer;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _fallback;
    private readonly object _gate = new();
    private State _state = new(false, [], 0);
    private bool _disposed;
    internal event Action? Changed;
    internal SharedRescueSettings(string path)
    {
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())));
        _writer = new Mutex(false, "WatchDog.RescueSettings." + hash);
        Reload();
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(_path)!, Path.GetFileName(_path))
        { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        _watcher.Changed += OnFileChanged; _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileChanged; _watcher.Renamed += OnFileChanged;
        _watcher.EnableRaisingEvents = true;
        _fallback = new Timer(_ => Reload(), null, 500, 500);
    }
    internal bool Enabled { get { lock (_gate) return _state.Enabled; } }
    internal bool IsAllowed(string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) return false;
        lock (_gate) return !_state.ExcludedAccounts.Contains(account.Trim(), StringComparer.OrdinalIgnoreCase);
    }
    internal void SetEnabled(bool value) => Update(s => s with { Enabled = value });
    internal void SetAllowed(string account, bool value)
    {
        if (string.IsNullOrWhiteSpace(account)) return;
        account = account.Trim();
        Update(s => s with { ExcludedAccounts = value
            ? s.ExcludedAccounts.Where(a => !a.Equals(account, StringComparison.OrdinalIgnoreCase)).ToArray()
            : s.ExcludedAccounts.Where(a => !a.Equals(account, StringComparison.OrdinalIgnoreCase)).Append(account).ToArray() });
    }
    private State Read()
    {
        if (!File.Exists(_path)) return new(false, [], 0);
        using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        State state = JsonSerializer.Deserialize<State>(file) ?? throw new InvalidDataException("Invalid rescue settings.");
        return state with { ExcludedAccounts = (state.ExcludedAccounts ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
    }
    private void Update(Func<State, State> change)
    {
        bool held = false;
        try
        {
            try { held = _writer.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("Rescue settings are busy; try again.");
            State before = Read(), after = change(before);
            if (Same(before, after)) { Reload(); return; }
            after = after with { Revision = before.Revision + 1 };
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(after), new UTF8Encoding(false));
                File.Move(temporary, _path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { if (held) _writer.ReleaseMutex(); }
        Reload();
    }
    private static bool Same(State a, State b) => a.Enabled == b.Enabled &&
        a.ExcludedAccounts.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).SequenceEqual(
            b.ExcludedAccounts.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    private void OnFileChanged(object sender, FileSystemEventArgs args) => Reload();
    internal void Reload()
    {
        bool changed;
        lock (_gate)
        {
            if (_disposed) return;
            State next;
            try { next = Read(); } catch { return; } // A bad/incomplete read never resets a valid preference.
            changed = !Same(_state, next);
            _state = next;
        }
        if (changed && Changed is { } handlers)
            foreach (Action handler in handlers.GetInvocationList())
                try { handler(); } catch { }
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _watcher.Dispose(); _fallback.Dispose(); _writer.Dispose(); Changed = null;
    }
}
