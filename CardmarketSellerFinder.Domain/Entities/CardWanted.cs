namespace CMBuyerStudio.Domain.Entities;

public sealed class CardWanted
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Nombre de la carta (ej: "Lightning Bolt")
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Set o expansión (ej: "Innistrad Remastered")
    /// </summary>
    public string? Expansion { get; init; }

    /// <summary>
    /// URL o identificador de Cardmarket para esta carta/variante
    /// </summary>
    public string? ProductUrl { get; init; }

    /// <summary>
    /// Cantidad deseada
    /// </summary>
    public int Quantity { get; init; }
}