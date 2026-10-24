using DMRoute_ng.Gateways;
using DMRoute_ng.Types;

namespace DMRoute_ng.Services;

public sealed class SdsServiceRegistry : IDisposable
{
    private readonly SdsGateway _gateway;
    private readonly Dictionary<int, ISdsService> _services;

    public SdsServiceRegistry(SdsGateway gateway, IEnumerable<ISdsService> services)
    {
        _gateway = gateway;
        var registered = services.ToArray();
        _services = new Dictionary<int, ISdsService>(registered.Length);
        foreach (var service in registered)
        {
            if (!DmrServiceIds.IsReserved(service.DestinationId))
                throw new ArgumentOutOfRangeException(nameof(services), "SDS services require a reserved destination ID.");
            _services.Add(service.DestinationId, service);
        }
        gateway.OnMessageReceived += Dispatch;
        gateway.OnDeliveryReportReceived += DispatchDeliveryReport;
    }

    private void Dispatch(in SdsMessageEvent message, ReadOnlySpan<byte> textBytes)
    {
        if (_services.TryGetValue(message.DestinationId, out var service))
            service.HandleMessage(in message, textBytes);
    }

    private void DispatchDeliveryReport(in SdsDeliveryReportEvent report)
    {
        if (_services.TryGetValue(report.DestinationId, out var service))
            service.HandleDeliveryReport(in report);
    }

    public void Dispose()
    {
        _gateway.OnMessageReceived -= Dispatch;
        _gateway.OnDeliveryReportReceived -= DispatchDeliveryReport;
    }
}
