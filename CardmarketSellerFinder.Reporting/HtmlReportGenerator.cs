using System.Globalization;
using System.Net;
using System.Text;
using CMBuyerStudio.Application.Abstractions;
using CMBuyerStudio.Application.Enums;
using CMBuyerStudio.Application.Models;
using CMBuyerStudio.Domain.Market;

namespace CMBuyerStudio.Reporting;

public sealed class HtmlReportGenerator : IHtmlReportGenerator
{
    private readonly IAppPaths _paths;

    public HtmlReportGenerator(IAppPaths paths)
    {
        _paths = paths;
    }

    public async Task<GeneratedHtmlReport> GenerateAsync(
        HtmlReportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.OptimizationResult);
        ArgumentNullException.ThrowIfNull(request.Snapshot);

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_paths.ReportsPath);

        var report = BuildReport(request);
        var filePath = Path.Combine(
            _paths.ReportsPath,
            $"best-seller-result-{request.GeneratedAt:yyyyMMdd-HHmmss}-{GetScopeSlug(request.Scope)}.html");

        await File.WriteAllTextAsync(filePath, RenderHtml(report), Encoding.UTF8, cancellationToken);

        return new GeneratedHtmlReport
        {
            Scope = request.Scope,
            Path = filePath
        };
    }

    private static ReportViewModel BuildReport(HtmlReportRequest request)
    {
        var snapshot = request.Snapshot;
        var result = request.OptimizationResult;
        var selectedSellerNames = result.SelectedSellerNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sellerCountryByName = snapshot.ScopedMarketData
            .SelectMany(card => card.Offers)
            .GroupBy(offer => offer.SellerName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(offer => offer.Country).FirstOrDefault(country => !string.IsNullOrWhiteSpace(country)) ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        var shippingTotal = selectedSellerNames.Sum(sellerName =>
            snapshot.FixedCostBySellerName.TryGetValue(sellerName, out var value) ? value : 0m);
        var uncoveredCardNames = ResolveUncoveredCardNames(snapshot.ScopedMarketData, result.UncoveredCardKeys);
        var assignmentsBySeller = result.Assignments
            .GroupBy(assignment => assignment.SellerName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var sellers = selectedSellerNames
            .Select(sellerName =>
            {
                assignmentsBySeller.TryGetValue(sellerName, out var assignments);
                assignments ??= [];

                var rows = assignments
                    .OrderBy(assignment => assignment.CardName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(assignment => assignment.SetName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(assignment => assignment.ProductUrl, StringComparer.OrdinalIgnoreCase)
                    .Select(assignment => new SellerAssignmentViewModel(
                        CardName: assignment.CardName,
                        Quantity: assignment.Quantity,
                        UnitPrice: assignment.UnitPrice,
                        TotalPrice: assignment.TotalPrice,
                        ProductUrl: assignment.ProductUrl,
                        SetOptions: BuildSellerSetOptions(snapshot.ScopedMarketData, sellerName, assignment.CardName)))
                    .ToList();

                var cardsTotal = assignments.Sum(assignment => assignment.TotalPrice);
                var shipping = snapshot.FixedCostBySellerName.TryGetValue(sellerName, out var sellerShipping)
                    ? sellerShipping
                    : 0m;

                return new SellerViewModel(
                    SellerName: sellerName,
                    Country: sellerCountryByName.TryGetValue(sellerName, out var country) ? country : string.Empty,
                    SellerUrl: BuildSellerUrl(sellerName),
                    CardsTotal: cardsTotal,
                    ShippingTotal: shipping,
                    GrandTotal: cardsTotal + shipping,
                    Rows: rows);
            })
            .ToList();

        var cards = snapshot.ScopedMarketData
            .GroupBy(card => card.Target.CardName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var offers = group.SelectMany(card => card.Offers).ToList();
                var distinctUrls = offers
                    .Select(offer => offer.ProductUrl)
                    .Where(url => !string.IsNullOrWhiteSpace(url))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(url => url, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new CardAnalysisViewModel(
                    CardName: group.Key,
                    RequestedQuantity: group.Max(card => card.Target.DesiredQuantity),
                    FirstPrice: offers.Count == 0 ? null : offers.Min(offer => offer.Price),
                    MaxPrice: offers.Count == 0 ? null : offers.Max(offer => offer.Price),
                    OffersCount: offers.Count,
                    AlternativeUrls: distinctUrls);
            })
            .ToList();

        return new ReportViewModel(
            Scope: request.Scope,
            GeneratedAt: request.GeneratedAt,
            IsFullyCovered: uncoveredCardNames.Count == 0,
            SelectedSellerNames: selectedSellerNames,
            UncoveredCardNames: uncoveredCardNames,
            SellerCount: result.SellerCount,
            CardsTotalPrice: result.CardsTotalPrice,
            ShippingTotal: shippingTotal,
            GrandTotal: result.CardsTotalPrice + shippingTotal,
            AssignmentCount: result.Assignments.Count,
            Sellers: sellers,
            Cards: cards);
    }

    private static IReadOnlyList<SellerSetOptionViewModel> BuildSellerSetOptions(
        IReadOnlyList<MarketCardData> marketData,
        string sellerName,
        string cardName)
    {
        var options = marketData
            .Where(card => string.Equals(card.Target.CardName, cardName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(card => card.Offers)
            .Where(offer =>
                string.Equals(offer.SellerName, sellerName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(offer.CardName, cardName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(offer => new { offer.ProductUrl, offer.SetName })
            .Select(group => new SellerSetOptionViewModel(
                ProductUrl: group.Key.ProductUrl,
                SetName: group.Key.SetName,
                Stock: group.Sum(offer => offer.AvailableQuantity),
                BestPrice: group.Min(offer => offer.Price)))
            .OrderBy(option => option.BestPrice)
            .ThenBy(option => option.SetName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return options.Count > 1 ? options : [];
    }

    private static List<string> ResolveUncoveredCardNames(
        IReadOnlyList<MarketCardData> marketData,
        IReadOnlySet<string> uncoveredCardKeys)
    {
        if (uncoveredCardKeys.Count == 0)
        {
            return [];
        }

        return marketData
            .Where(card => uncoveredCardKeys.Contains(ResolveCardKey(card.Target)))
            .Select(card => card.Target.CardName)
            .Where(cardName => !string.IsNullOrWhiteSpace(cardName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(cardName => cardName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ResolveCardKey(ScrapingTarget target)
        => string.IsNullOrWhiteSpace(target.RequestKey)
            ? target.ProductUrl
            : target.RequestKey;

    private static string RenderHtml(ReportViewModel report)
    {
        var builder = new StringBuilder(32_768);

        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("  <meta charset=\"utf-8\" />");
        builder.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        builder.AppendLine("  <title>Best Seller Report</title>");
        builder.AppendLine("  <style>");
        builder.AppendLine(@"    :root {
      --bg: #070b14;
      --bg-soft: #0d1322;
      --surface: #111b30;
      --surface-2: #182642;
      --text: #e8edf8;
      --muted: #9eb0d1;
      --ok: #42d392;
      --warn: #f4b158;
      --danger: #ff6b7a;
      --accent: #68b8ff;
      --border: #263856;
      --shadow: 0 14px 35px rgba(0, 0, 0, 0.35);
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      color: var(--text);
      font-family: 'Segoe UI', 'Trebuchet MS', sans-serif;
      background: radial-gradient(1200px 900px at 85% -10%, #1e2c4f 0%, transparent 55%),
                  radial-gradient(900px 700px at -10% 15%, #173459 0%, transparent 50%),
                  linear-gradient(160deg, var(--bg) 0%, #080f1d 55%, #060a13 100%);
      min-height: 100vh;
      line-height: 1.45;
    }
    .container { max-width: 1220px; margin: 32px auto 48px; padding: 0 20px; }
    .hero { background: linear-gradient(135deg, #121f3a 0%, #0f1830 45%, #1d3052 100%); border: 1px solid var(--border); border-radius: 20px; padding: 26px; box-shadow: var(--shadow); }
    .title { margin: 0; font-size: clamp(1.6rem, 2vw, 2.3rem); letter-spacing: 0.02em; }
    .subtitle { margin: 10px 0 0; color: var(--muted); font-size: 0.96rem; }
    .pill { display: inline-flex; align-items: center; border-radius: 999px; padding: 4px 10px; font-size: 0.78rem; border: 1px solid var(--border); margin-top: 12px; }
    .pill.ok { color: var(--ok); background: rgba(66, 211, 146, 0.12); }
    .pill.warn { color: var(--warn); background: rgba(244, 177, 88, 0.12); }
    .grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 12px; margin-top: 16px; }
    .metric { background: var(--surface); border: 1px solid var(--border); border-radius: 14px; padding: 14px; }
    .metric .label { color: var(--muted); font-size: 0.78rem; text-transform: uppercase; letter-spacing: 0.08em; }
    .metric .value { margin-top: 6px; font-size: 1.2rem; font-weight: 600; }
    .section { margin-top: 20px; background: rgba(17, 27, 48, 0.88); border: 1px solid var(--border); border-radius: 16px; padding: 18px; box-shadow: var(--shadow); }
    .section h2 { margin: 0 0 14px; font-size: 1.15rem; letter-spacing: 0.03em; }
    .chips { display: flex; flex-wrap: wrap; gap: 8px; }
    .chip { background: var(--surface-2); border: 1px solid var(--border); color: var(--text); border-radius: 999px; padding: 5px 11px; font-size: 0.82rem; }
    .seller-card { background: linear-gradient(180deg, #0f172a 0%, #111c33 100%); border: 1px solid var(--border); border-radius: 14px; padding: 14px; margin-bottom: 12px; }
    .seller-head { display: flex; justify-content: space-between; align-items: center; gap: 12px; margin-bottom: 8px; }
    .seller-name { font-size: 1.02rem; font-weight: 600; }
    .seller-total { font-size: 0.9rem; color: var(--accent); }
    table { width: 100%; border-collapse: collapse; margin-top: 8px; }
    th, td { padding: 8px 6px; border-bottom: 1px solid #223352; text-align: left; font-size: 0.88rem; vertical-align: top; }
    th { color: var(--muted); font-weight: 600; font-size: 0.76rem; text-transform: uppercase; letter-spacing: 0.07em; }
    .muted { color: var(--muted); }
    .link { color: var(--accent); text-decoration: none; font-weight: 600; }
    .link:hover { text-decoration: underline; }
    .issues { margin: 6px 0 0; padding-left: 18px; }
    .issues li { margin: 4px 0; }
    details { margin-top: 6px; }
    summary { cursor: pointer; color: var(--muted); font-size: 0.82rem; }
    .cards-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; }
    .card-item { background: var(--surface); border: 1px solid var(--border); border-radius: 12px; padding: 12px; }
    .card-title { margin: 0 0 8px; font-size: 0.96rem; }
    .kv { display: grid; grid-template-columns: 1fr auto; gap: 6px; font-size: 0.84rem; }
    .kv .k { color: var(--muted); }
    @media (max-width: 980px) { .grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } .cards-grid { grid-template-columns: 1fr; } }
    @media (max-width: 700px) { .grid { grid-template-columns: 1fr; } .container { padding: 0 12px; } .seller-head { flex-direction: column; align-items: flex-start; } table { display: block; overflow-x: auto; } }");
        builder.AppendLine("  </style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("  <div class=\"container\">");
        builder.AppendLine("    <section class=\"hero\">");
        builder.AppendLine("      <h1 class=\"title\">Cardmarket Best Seller Report</h1>");
        builder.AppendLine($"      <p class=\"subtitle\">Generated at {Encode(report.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture))}</p>");
        builder.AppendLine($"      <span class=\"pill {(report.IsFullyCovered ? "ok" : "warn")}\">{(report.IsFullyCovered ? "Fully Covered" : "Partially Covered")}</span>");
        builder.AppendLine($"      <span class=\"pill\" style=\"margin-left:8px;\">Scope: {Encode(GetScopeHeroLabel(report.Scope))}</span>");
        builder.AppendLine("      <div class=\"grid\">");
        AppendMetric(builder, "Selected Sellers", report.SellerCount.ToString(CultureInfo.InvariantCulture));
        AppendMetric(builder, "Cards Total Price", FormatCurrency(report.CardsTotalPrice));
        AppendMetric(builder, "Shipping Total", FormatCurrency(report.ShippingTotal));
        AppendMetric(builder, "Grand Total", FormatCurrency(report.GrandTotal));
        AppendMetric(builder, "Assignments", report.AssignmentCount.ToString(CultureInfo.InvariantCulture));
        AppendMetric(builder, "Uncovered Cards", report.UncoveredCardNames.Count.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("      </div>");
        builder.AppendLine("    </section>");

        builder.AppendLine("    <section class=\"section\">");
        builder.AppendLine("      <h2>Selected Sellers</h2>");
        if (report.SelectedSellerNames.Count == 0)
        {
            builder.AppendLine("      <div class=\"muted\">No sellers selected.</div>");
        }
        else
        {
            builder.AppendLine("      <div class=\"chips\">");
            foreach (var sellerName in report.SelectedSellerNames)
            {
                builder.AppendLine($"        <span class=\"chip\">{Encode(sellerName)}</span>");
            }
            builder.AppendLine("      </div>");
        }

        if (report.UncoveredCardNames.Count > 0)
        {
            builder.AppendLine("      <h2 style=\"margin-top:14px;\">Uncovered Cards</h2>");
            builder.AppendLine("      <div class=\"chips\">");
            foreach (var cardName in report.UncoveredCardNames)
            {
                builder.AppendLine($"        <span class=\"chip\">{Encode(cardName)}</span>");
            }
            builder.AppendLine("      </div>");
        }
        builder.AppendLine("    </section>");

        builder.AppendLine("    <section class=\"section\">");
        builder.AppendLine("      <h2>Sellers Breakdown</h2>");
        if (report.Sellers.Count == 0)
        {
            builder.AppendLine("      <div class=\"muted\">No seller breakdown available.</div>");
        }
        else
        {
            foreach (var seller in report.Sellers)
            {
                builder.AppendLine("      <article class=\"seller-card\">");
                builder.AppendLine("        <div class=\"seller-head\">");
                builder.AppendLine($"          <div class=\"seller-name\">{Encode(seller.SellerName)}</div>");
                builder.AppendLine($"          <div class=\"seller-total\">Cards: {FormatCurrency(seller.CardsTotal)} | Shipping: {FormatCurrency(seller.ShippingTotal)} | Grand: {FormatCurrency(seller.GrandTotal)}</div>");
                builder.AppendLine("        </div>");
                builder.AppendLine($"        <div class=\"muted\">Country: {Encode(string.IsNullOrWhiteSpace(seller.Country) ? "Unknown" : seller.Country)}</div>");
                builder.AppendLine($"        <div><a class=\"link\" href=\"{Encode(seller.SellerUrl)}\" target=\"_blank\" rel=\"noopener noreferrer\">See Seller</a></div>");

                if (seller.Rows.Count == 0)
                {
                    builder.AppendLine("        <div class=\"muted\" style=\"margin-top:8px;\">No assigned cards for this seller.</div>");
                }
                else
                {
                    builder.AppendLine("        <table>");
                    builder.AppendLine("          <thead><tr><th>Card</th><th>Qty</th><th>Unit</th><th>Total</th><th>Link</th></tr></thead>");
                    builder.AppendLine("          <tbody>");
                    foreach (var row in seller.Rows)
                    {
                        builder.AppendLine("            <tr>");
                        builder.AppendLine($"              <td>{Encode(row.CardName)}</td>");
                        builder.AppendLine($"              <td>{row.Quantity.ToString(CultureInfo.InvariantCulture)}</td>");
                        builder.AppendLine($"              <td>{FormatCurrency(row.UnitPrice)}</td>");
                        builder.AppendLine($"              <td>{FormatCurrency(row.TotalPrice)}</td>");
                        builder.AppendLine($"              <td><a class=\"link\" href=\"{Encode(row.ProductUrl)}\" target=\"_blank\" rel=\"noopener noreferrer\">See Card</a></td>");
                        builder.AppendLine("            </tr>");

                        if (row.SetOptions.Count > 0)
                        {
                            builder.AppendLine("            <tr><td colspan=\"5\">");
                            builder.AppendLine("              <details>");
                            builder.AppendLine("                <summary>Available Set Options</summary>");
                            builder.AppendLine("                <ul class=\"issues\">");
                            foreach (var option in row.SetOptions)
                            {
                                builder.AppendLine($"                  <li><a class=\"link\" href=\"{Encode(option.ProductUrl)}\" target=\"_blank\" rel=\"noopener noreferrer\">See Set</a> | {Encode(option.SetName)} | stock {option.Stock.ToString(CultureInfo.InvariantCulture)} | best {FormatCurrency(option.BestPrice)}</li>");
                            }
                            builder.AppendLine("                </ul>");
                            builder.AppendLine("              </details>");
                            builder.AppendLine("            </td></tr>");
                        }
                    }
                    builder.AppendLine("          </tbody>");
                    builder.AppendLine("        </table>");
                }

                builder.AppendLine("      </article>");
            }
        }
        builder.AppendLine("    </section>");

        builder.AppendLine("    <section class=\"section\">");
        builder.AppendLine("      <h2>Cards Analyzed</h2>");
        if (report.Cards.Count == 0)
        {
            builder.AppendLine("      <div class=\"muted\">No cards analyzed.</div>");
        }
        else
        {
            builder.AppendLine("      <div class=\"cards-grid\">");
            foreach (var card in report.Cards)
            {
                builder.AppendLine("        <article class=\"card-item\">");
                builder.AppendLine($"          <h3 class=\"card-title\">{Encode(card.CardName)}</h3>");
                builder.AppendLine("          <div class=\"kv\">");
                builder.AppendLine($"            <div class=\"k\">Requested Qty</div><div>{card.RequestedQuantity.ToString(CultureInfo.InvariantCulture)}</div>");
                builder.AppendLine($"            <div class=\"k\">First Price</div><div>{FormatNullableCurrency(card.FirstPrice)}</div>");
                builder.AppendLine($"            <div class=\"k\">Max Price</div><div>{FormatNullableCurrency(card.MaxPrice)}</div>");
                builder.AppendLine($"            <div class=\"k\">Offers Count</div><div>{card.OffersCount.ToString(CultureInfo.InvariantCulture)}</div>");
                builder.AppendLine("          </div>");

                if (card.AlternativeUrls.Count > 0)
                {
                    builder.AppendLine("          <details>");
                    builder.AppendLine("            <summary>Alternative URLs</summary>");
                    builder.AppendLine("            <ul class=\"issues\">");
                    for (var index = 0; index < card.AlternativeUrls.Count; index++)
                    {
                        builder.AppendLine($"              <li><a class=\"link\" href=\"{Encode(card.AlternativeUrls[index])}\" target=\"_blank\" rel=\"noopener noreferrer\">Source {(index + 1).ToString(CultureInfo.InvariantCulture)}</a></li>");
                    }
                    builder.AppendLine("            </ul>");
                    builder.AppendLine("          </details>");
                }

                builder.AppendLine("        </article>");
            }
            builder.AppendLine("      </div>");
        }
        builder.AppendLine("    </section>");

        builder.AppendLine("  </div>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");

        return builder.ToString();
    }

    private static void AppendMetric(StringBuilder builder, string label, string value)
    {
        builder.AppendLine($"        <div class=\"metric\"><div class=\"label\">{Encode(label)}</div><div class=\"value\">{Encode(value)}</div></div>");
    }

    private static string BuildSellerUrl(string sellerName)
        => $"https://www.cardmarket.com/es/Magic/Users/{Uri.EscapeDataString(sellerName)}";

    private static string GetScopeSlug(SellerScopeMode scope)
        => scope == SellerScopeMode.Eu ? "eu" : "local";

    private static string GetScopeHeroLabel(SellerScopeMode scope)
        => scope == SellerScopeMode.Eu ? "EU" : "LOCAL";

    private static string FormatCurrency(decimal value)
        => $"{value.ToString("0.00", CultureInfo.InvariantCulture)} EUR";

    private static string FormatNullableCurrency(decimal? value)
        => value.HasValue ? FormatCurrency(value.Value) : "N/A";

    private static string Encode(string value)
        => WebUtility.HtmlEncode(value);

    private sealed record ReportViewModel(
        SellerScopeMode Scope,
        DateTimeOffset GeneratedAt,
        bool IsFullyCovered,
        IReadOnlyList<string> SelectedSellerNames,
        IReadOnlyList<string> UncoveredCardNames,
        int SellerCount,
        decimal CardsTotalPrice,
        decimal ShippingTotal,
        decimal GrandTotal,
        int AssignmentCount,
        IReadOnlyList<SellerViewModel> Sellers,
        IReadOnlyList<CardAnalysisViewModel> Cards);

    private sealed record SellerViewModel(
        string SellerName,
        string Country,
        string SellerUrl,
        decimal CardsTotal,
        decimal ShippingTotal,
        decimal GrandTotal,
        IReadOnlyList<SellerAssignmentViewModel> Rows);

    private sealed record SellerAssignmentViewModel(
        string CardName,
        int Quantity,
        decimal UnitPrice,
        decimal TotalPrice,
        string ProductUrl,
        IReadOnlyList<SellerSetOptionViewModel> SetOptions);

    private sealed record SellerSetOptionViewModel(
        string ProductUrl,
        string SetName,
        int Stock,
        decimal BestPrice);

    private sealed record CardAnalysisViewModel(
        string CardName,
        int RequestedQuantity,
        decimal? FirstPrice,
        decimal? MaxPrice,
        int OffersCount,
        IReadOnlyList<string> AlternativeUrls);
}
