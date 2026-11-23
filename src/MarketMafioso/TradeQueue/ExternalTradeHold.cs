using System;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace MarketMafioso.TradeQueue;

internal sealed class ExternalTradeHold(Func<string?> busy, Action<string> info, Func<long> milliseconds, Action<string>? warn = null)
{
    private const long MaximumHoldMilliseconds = 10 * 60 * 1000L;
    private readonly object gate = new();
    private string? holder;
    private long acquired;
    private long expires;
    private void Expire()
    {
        if (holder == null || milliseconds() < Math.Min(expires, acquired + MaximumHoldMilliseconds)) return;
        info($"Trade hold expired: {holder}");
        holder = null;
    }
    internal string? Reason
    {
        get { lock (gate) { Expire(); return holder == null ? null : $"Trading is held by {holder} ({RemainingSeconds()}s)."; } }
    }
    internal string? Holder
    {
        get { lock (gate) { Expire(); return holder; } }
    }
    private long RemainingSeconds() =>
        Math.Max(0L, (Math.Min(expires, acquired + MaximumHoldMilliseconds) - milliseconds() + 999L) / 1000L);
    internal (bool Acknowledged, string Reason) Acquire(string name, int seconds)
    {
        lock (gate)
        {
            Expire();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 160 || seconds is < 1 or > 300)
                return (false, "Provide a holder and a lease of 1-300 seconds.");
            if (holder != null && name != holder) return (false, Reason!);
            if (busy() is {} reason) return (false, reason);
            if (holder == null) acquired = milliseconds();
            holder = name;
            expires = milliseconds() + seconds * 1000L;
            info($"Trade hold acquired: {name}");
            return (true, "Acquired");
        }
    }
    internal bool Renew(string name, int seconds)
    {
        lock (gate)
        {
            Expire();
            if (holder == null || name != holder || seconds is < 1 or > 300)
            { info($"Trade hold renewal lost: {name}"); return false; }
            expires = milliseconds() + seconds * 1000L;
            return true;
        }
    }
    internal bool Release(string name)
    {
        lock (gate)
        {
            Expire();
            if (holder == null || name != holder) return false;
            info($"Trade hold released: {holder}");
            holder = null;
            return true;
        }
    }
    internal void Clear()
    {
        lock (gate)
        {
            if (holder == null) return;
            (warn ?? info)($"Trade hold force-released: dropped the hold held by {holder}.");
            holder = null;
        }
    }
}

internal sealed class TradeHoldProvider : IDisposable
{
    private const string Prefix = "MarketMafioso.v1.TradeHold.";
    private readonly ExternalTradeHold hold;
    private readonly ICallGateProvider<string,int,string> acquire;
    private readonly ICallGateProvider<string,int,bool> renew;
    private readonly ICallGateProvider<string,bool> release;
    internal TradeHoldProvider(IDalamudPluginInterface pi, ExternalTradeHold hold)
    {
        this.hold = hold;
        acquire = pi.GetIpcProvider<string,int,string>(Prefix+"Acquire");
        renew = pi.GetIpcProvider<string,int,bool>(Prefix+"Renew");
        release = pi.GetIpcProvider<string,bool>(Prefix+"Release");
        acquire.RegisterFunc((name, seconds) => {
            var result = hold.Acquire(name, seconds);
            return JsonSerializer.Serialize(new { result.Acknowledged, result.Reason });
        });
        renew.RegisterFunc(hold.Renew);
        release.RegisterFunc(hold.Release);
    }
    public void Dispose() { acquire.UnregisterFunc(); renew.UnregisterFunc(); release.UnregisterFunc(); hold.Clear(); }
}
