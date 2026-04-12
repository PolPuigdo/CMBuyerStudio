namespace CMBuyerStudio.Application.RunAnalysis;

public abstract record RunProgressEvent;

// Start
public record RunStartedEvent(int Progress = 0) : RunProgressEvent;

// Cache
public record RecoverCacheStartEvent(int Progress = 0) : RunProgressEvent;
public record RecoverCacheCompletedEvent(int Progress = 0) : RunProgressEvent;

// Scraping
public record CardScrapingStartedEvent(int Progress) : RunProgressEvent;
public record CardScrapedEvent(int Progress) : RunProgressEvent;

// Optimization
public record BuildPhasesStartEvent() : RunProgressEvent;

public record PurgeStartEvent(int Progress) : RunProgressEvent;

public record EUCalculationStartEvent(int Progress) : RunProgressEvent;
public record EUCalculationCompleteEvent(int Progress, decimal TotalPrice) : RunProgressEvent;

public record LocalCalculationStartEvent(int Progress) : RunProgressEvent;
public record LocalCalculationCompleteEvent(int Progress, decimal TotalPrice) : RunProgressEvent;


// Reports
public record ReportStartEvent(int Progress) : RunProgressEvent;
public record ReportGeneratedEvent(int Progress, string Path, string Scope) : RunProgressEvent;
