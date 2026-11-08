using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WatchDog.Shared;

/// <summary>Local file bridge between per-account Watch Dog workers in one VibeSkua host.</summary>
internal static class RescueAccountHub
{
    internal sealed record AccountSnapshot(string Account, string Server, string Map, string Cell,
        DateTime UpdatedUtc, RescueStatusSnapshot Status);

    private sealed record ManualRequest(string Id, DateTime CreatedUtc);
    private static string Root => Environment.GetEnvironmentVariable("WATCHDOG_ACCOUNT_HUB_ROOT") ??
        Path.Combine(Path.GetTempPath(), "VibeSkuaTabs", "watchdog-accounts");

    private static string Key(string account) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.Trim().ToUpperInvariant())))[..24];
    private static string AccountPath(string account) => Path.Combine(Root, Key(account) + ".json");
    private static string RequestPath(string account) => Path.Combine(Root, Key(account) + ".request");

    internal static void Publish(string account, string server, string map, string cell, RescueStatusSnapshot status)
    {
        if (string.IsNullOrWhiteSpace(account)) return;
        try
        {
            Directory.CreateDirectory(Root);
            string path = AccountPath(account), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(new AccountSnapshot(account, server, map, cell, DateTime.UtcNow, status)), new UTF8Encoding(false));
                File.Move(temp, path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        catch { }
    }

    internal static IReadOnlyList<AccountSnapshot> ReadAccounts()
    {
        try
        {
            if (!Directory.Exists(Root)) return [];
            DateTime now = DateTime.UtcNow;
            List<AccountSnapshot> result = [];
            foreach (string path in Directory.GetFiles(Root, "*.json"))
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    AccountSnapshot? snapshot = JsonSerializer.Deserialize<AccountSnapshot>(stream);
                    if (snapshot is not null && now - snapshot.UpdatedUtc < TimeSpan.FromSeconds(12)) result.Add(snapshot);
                }
                catch { }
            }
            return result.OrderBy(a => a.Account, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch { return []; }
    }

    internal static string RequestManualHelp(string account)
    {
        if (string.IsNullOrWhiteSpace(account)) return "Select an account first.";
        AccountSnapshot? target = ReadAccounts().FirstOrDefault(a => a.Account.Equals(account, StringComparison.OrdinalIgnoreCase));
        if (target is null) return $"{account} is no longer online in Watch Dog.";
        if (!target.Status.Enabled) return $"Rescue Allies is off for {account}; enable it before requesting help.";
        if (target.Status.Calling) return $"{account} already has a help request active.";
        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(RequestPath(account), JsonSerializer.Serialize(new ManualRequest(Guid.NewGuid().ToString("N"), DateTime.UtcNow)), new UTF8Encoding(false));
            return $"Manual help request sent to {account}.";
        }
        catch (Exception ex) { return $"Couldn't send the request to {account}: {ex.Message}"; }
    }

    /// <summary>Consumes at most one fresh request per worker; stale files cannot trigger after a client restart.</summary>
    internal static bool ConsumeManualHelpRequest(string account)
    {
        try
        {
            string path = RequestPath(account);
            if (!File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            ManualRequest? request = JsonSerializer.Deserialize<ManualRequest>(stream);
            stream.Dispose();
            File.Delete(path);
            return request is not null && DateTime.UtcNow - request.CreatedUtc < TimeSpan.FromSeconds(15);
        }
        catch { return false; }
    }
}
