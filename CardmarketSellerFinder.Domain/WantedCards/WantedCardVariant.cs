namespace CMBuyerStudio.Domain.WantedCards;

public class WantedCardVariant
{
    public string SetName { get; set; } = string.Empty;
    public string? ProductUrl { get; set; }
    public decimal? Price { get; set; }
    public string? ImagePath { get; set; }
}
