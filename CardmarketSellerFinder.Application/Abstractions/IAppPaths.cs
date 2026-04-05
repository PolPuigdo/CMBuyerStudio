namespace CMBuyerStudio.Application.Abstractions;

public interface IAppPaths
{
    string CardsPath { get; }
    string CachePath { get; }
    string ReportsPath { get; }
    string LogsPath { get; }
    string CardsCachePath { get; }
    string ImageCardsPath { get; }
}