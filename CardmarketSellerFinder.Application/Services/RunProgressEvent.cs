namespace CMBuyerStudio.Application.RunAnalysis;

public abstract record RunProgressEvent;

// Inicio
public record RunStartedEvent(int TotalCards) : RunProgressEvent;

// Scraping
public record CardScrapingStartedEvent(string CardName, int Current, int Total) : RunProgressEvent;
public record CardScrapedEvent(string CardName) : RunProgressEvent;

// Cálculo
public record CalculationStartedEvent(string Scope) : RunProgressEvent; // EU / Local
public record CalculationFinishedEvent(string Scope) : RunProgressEvent;

// Reportes
public record ReportGeneratedEvent(string Path, string Scope) : RunProgressEvent;

// Final
public record RunCompletedEvent() : RunProgressEvent;