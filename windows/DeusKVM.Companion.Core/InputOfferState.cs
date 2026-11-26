namespace DeusKVM.Companion.Core;

public enum InputOfferChange { Unchanged, Replace, Reconnect }

/// <summary>
/// A stream offer can be replayed after helper startup. Replacing a live capture's transport
/// requires returning locally: physical input snapshots belong to that transport lifetime.
/// </summary>
public sealed class InputOfferState
{
    private FileNetworkOffer? current;
    public InputOfferChange Receive(FileNetworkOffer offer, bool remote)
    {
        if (!offer.Valid) throw new InvalidDataException("Invalid input offer");
        if (current is { } previous)
        {
            if (previous.Port == offer.Port && previous.Key == offer.Key && previous.Hosts.SequenceEqual(offer.Hosts))
                return InputOfferChange.Unchanged;
            if (remote) return InputOfferChange.Reconnect;
        }
        current = offer with { Hosts = offer.Hosts.ToArray() };
        return InputOfferChange.Replace;
    }
}
