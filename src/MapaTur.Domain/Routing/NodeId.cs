namespace MapaTur.Domain.Routing;

/// <summary>
/// Opaque identifier for a node in the routing graph. Wrapping the underlying int
/// prevents accidental arithmetic and clarifies intent in signatures.
/// </summary>
/// <param name="Value">Underlying integer identifier.</param>
public readonly record struct NodeId(int Value)
{
    /// <summary>Sentinel value used to denote an absent node.</summary>
    public static NodeId None => new(-1);

    /// <inheritdoc />
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
