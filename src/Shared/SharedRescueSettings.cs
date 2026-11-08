using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WatchDog.Shared;

// Kept byte-identical in Watch Dog and VibeSkuffle. No client-specific dependencies.
internal sealed class SharedRescueSettings : IDisposable
{
    internal sealed record State(bool Enabled, string[] ExcludedAccounts, long Revision,
        int DelayMinutes = 15, int DeathsBeforeRescue = 10, bool CooldownEnabled = true, int CooldownMinutes = 10,
        int HelperDeathsWithoutKill = 5, int HoldLimitMinutes = 30, bool UseBossLoadout = true);
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
    internal int DelayMinutes { get { lock (_gate) return _state.DelayMinutes; } }
    internal int DeathsBeforeRescue { get { lock (_gate) return _state.DeathsBeforeRescue; } }
    internal int CooldownMinutes { get { lock (_gate) return _state.CooldownMinutes; } }
    internal bool CooldownEnabled { get { lock (_gate) return _state.CooldownEnabled; } }
    internal int HelperDeathsWithoutKill { get { lock (_gate) return _state.HelperDeathsWithoutKill; } }
    /// <summary>Helpers are released after this many minutes without a kill or death of the caller (0 = never; the caller leaving the map always releases them).</summary>
    internal int HoldLimitMinutes { get { lock (_gate) return _state.HoldLimitMinutes; } }
    /// <summary>Helpers switch to the CoreBots Boss class and gear (default on).</summary>
    internal bool UseBossLoadout { get { lock (_gate) return _state.UseBossLoadout; } }
    internal void SetHoldLimitMinutes(int value)
    {
        if (value is < 0 or > 240) throw new ArgumentOutOfRangeException(nameof(value), "Use 0 to 240 minutes (0 = no limit).");
        Update(s => s with { HoldLimitMinutes = value });
    }
    internal void SetUseBossLoadout(bool value) => Update(s => s with { UseBossLoadout = value });
    internal void SetDelayMinutes(int value)
    {
        if (value is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(value), "Use 1 to 120 minutes.");
        Update(s => s with { DelayMinutes = value });
    }
    internal void SetDeathsBeforeRescue(int value)
    {
        if (value is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(value), "Use 1 to 100 deaths.");
        Update(s => s with { DeathsBeforeRescue = value });
    }
    internal void SetCooldownMinutes(int value)
    {
        if (value is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(value), "Use 1 to 30 minutes.");
        Update(s => s with { CooldownMinutes = value });
    }
    internal void SetCooldownEnabled(bool value) => Update(s => s with { CooldownEnabled = value });
    internal void SetHelperDeathsWithoutKill(int value)
    {
        if (value is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(value), "Use 1 to 100 deaths.");
        Update(s => s with { HelperDeathsWithoutKill = value });
    }
    // Either threshold requests help. The timed path still requires repeated deaths without a kill.
    internal bool ShouldRequest(int deaths, int elapsedMilliseconds)
    {
        lock (_gate) return deaths >= _state.DeathsBeforeRescue ||
            (deaths >= 2 && elapsedMilliseconds >= _state.DelayMinutes * 60000);
    }
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
        return state with {
            ExcludedAccounts = (state.ExcludedAccounts ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            DelayMinutes = state.DelayMinutes is >= 1 and <= 120 ? state.DelayMinutes : 15,
            DeathsBeforeRescue = state.DeathsBeforeRescue is >= 1 and <= 100 ? state.DeathsBeforeRescue : 10,
            CooldownMinutes = state.CooldownMinutes is >= 1 and <= 30 ? state.CooldownMinutes : 10,
            HelperDeathsWithoutKill = state.HelperDeathsWithoutKill is >= 1 and <= 100 ? state.HelperDeathsWithoutKill : 5,
            HoldLimitMinutes = state.HoldLimitMinutes is >= 0 and <= 240 ? state.HoldLimitMinutes : 30
        };
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
        a.DelayMinutes == b.DelayMinutes && a.DeathsBeforeRescue == b.DeathsBeforeRescue &&
        a.CooldownEnabled == b.CooldownEnabled && a.CooldownMinutes == b.CooldownMinutes &&
        a.HelperDeathsWithoutKill == b.HelperDeathsWithoutKill &&
        a.HoldLimitMinutes == b.HoldLimitMinutes && a.UseBossLoadout == b.UseBossLoadout &&
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
