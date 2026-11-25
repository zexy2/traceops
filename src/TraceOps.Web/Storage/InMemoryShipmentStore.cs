using TraceOps.Application;
using TraceOps.Domain;

namespace TraceOps.Web.Storage;

public sealed class InMemoryShipmentStore : IShipmentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ShipmentState> _shipments = new(StringComparer.Ordinal);

    public IReadOnlyList<ShipmentState> List()
    {
        lock (_gate)
        {
            var snapshot = _shipments.Values
                .OrderBy(state => state.TrackingNumber, StringComparer.Ordinal)
                .ToArray();
            return Array.AsReadOnly(snapshot);
        }
    }

    public ShipmentState? Find(string trackingNumber)
    {
        ArgumentNullException.ThrowIfNull(trackingNumber);
        lock (_gate)
        {
            return _shipments.GetValueOrDefault(trackingNumber);
        }
    }

    public bool TryAdd(ShipmentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            return _shipments.TryAdd(state.TrackingNumber, state);
        }
    }

    public ApplyEventResult? ApplyAtomically(LifecycleEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_gate)
        {
            if (input.TrackingNumber is null
                || !_shipments.TryGetValue(input.TrackingNumber, out var current))
            {
                return null;
            }

            var result = ShipmentLifecycle.Apply(current, input);
            if (result.Code is ApplyEventCode.Applied)
            {
                _shipments[current.TrackingNumber] = result.State;
            }

            return result;
        }
    }
}
