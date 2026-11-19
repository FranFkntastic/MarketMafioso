using System.Reflection;
using Dalamud.Plugin.Services;
using MarketMafioso.TradeQueue;

namespace MarketMafioso.SpecTests;

public class ExternalTradeHoldTests
{
    public class Stub : DispatchProxy
    {
        public int Actions, ReadinessChecks;
        public bool Confirmable;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch(method!.Name)
            {
                case "get_IsTradeOpen": case "get_IsPartnerReadyForTrade": case "get_CanClickReady": return true;
                case "get_CanConfirmTrade": return Confirmable;
                case "TryConfirmTrade": case "TryClickReady": Actions++; return true;
                case "TryGetExecutionReadiness": ReadinessChecks++; args![0]="Patch unavailable"; return false;
                default: return method.ReturnType==typeof(bool) ? false : null;
            }
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeaseBlocksBothDriversAndExpiryRestoresThem(bool confirmable)
    {
        long now=0;
        var hold=new ExternalTradeHold(()=>null,_=>{},()=>now);
        var window=DispatchProxy.Create<ITradeAutoAcceptIo,Stub>();
        ((Stub)(object)window).Confirmable=confirmable;
        var queue=DispatchProxy.Create<ITradeQueueIo,Stub>();
        var log=DispatchProxy.Create<IPluginLog,Stub>();
        var timing=new TradeQueueTimingOptions();
        var auto=new TradeAutoAcceptController(window,timing,log,()=>DateTimeOffset.UnixEpoch.AddMilliseconds(now)){ExternalHold=hold};
        var runner=new TradeQueueRunner([],timing,()=>{},queue,null!,null!,log){ExternalHold=hold};
        Assert.True(hold.Acquire("Courier",10).Acknowledged);
        auto.Tick(true);now=5000;auto.Tick(true);
        Assert.Equal(0,((Stub)(object)window).Actions);
        Assert.Contains("Courier",runner.Start().Message);
        Assert.Contains("Courier",runner.Start(null!).Message);
        Assert.Equal(0,((Stub)(object)queue).ReadinessChecks);
        now=10000;auto.Tick(true);now+=5000;auto.Tick(true);
        Assert.Equal(1,((Stub)(object)window).Actions);
        Assert.DoesNotContain("Courier",runner.Start().Message);
        Assert.Equal(1,((Stub)(object)queue).ReadinessChecks);
    }
}
