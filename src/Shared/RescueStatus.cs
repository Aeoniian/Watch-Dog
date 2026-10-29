namespace WatchDog.Shared;

/// <summary>Observational process-local rescue status. Never issues game calls or writes preferences.</summary>
public sealed record RescueStatusSnapshot(DateTime UpdatedUtc, bool Enabled, bool Allowed, int Deaths,
    int ElapsedMs, int DelayMinutes, int DeathThreshold, bool CanRequest, bool Calling,
    string Helper, string Phase, string Outcome, int[] CooldownSeconds);

public static class RescueStatus
{
    private static RescueStatusSnapshot? _latest;
    public static RescueStatusSnapshot? Latest => System.Threading.Volatile.Read(ref _latest);
    public static void Publish(RescueStatusSnapshot status) => System.Threading.Volatile.Write(ref _latest, status);
    public static string Describe(RescueStatusSnapshot? s, DateTime now)
    {
        if (s is null || now - s.UpdatedUtc > TimeSpan.FromSeconds(10))
            return "Live rescue status unavailable — account may be offline, loading, or recovering.";
        if (!s.Enabled) return "Rescue Allies is off.";
        string request = s.Calling ? "Help requested; waiting for a kill or another normal stop condition." : !s.CanRequest
            ? "Requesting help requires an active, unpaused script." : $"Deaths: {s.Deaths}/{s.DeathThreshold}. Time since first death: {Math.Max(0,s.ElapsedMs)/60000}:{Math.Max(0,s.ElapsedMs)/1000%60:00} / {s.DelayMinutes} min. Timer requires at least two deaths.";
        string cooldown = s.CooldownSeconds.Length == 0 ? "" : $"\nCooldowns for {s.CooldownSeconds.Length} previously helped requester(s): " + string.Join(", ", s.CooldownSeconds.Select(v => $"{v/60}:{v%60:00}")) + ". Other requesters remain eligible.";
        return request + "\nHelper: " + (s.Allowed ? s.Helper : "This account is excluded from helping; it can still receive help.")
            + (s.Phase.Length == 0 ? "" : "\nPhase: " + s.Phase) + cooldown
            + (s.Outcome.Length == 0 ? "" : "\nLast outcome: " + s.Outcome);
    }
}
