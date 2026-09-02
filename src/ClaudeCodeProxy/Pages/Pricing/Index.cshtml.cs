using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ClaudeCodeProxy.Pages.Pricing;

public sealed class IndexModel(PricingCatalog catalog) : PageModel
{
    public IReadOnlyList<PricingCatalog.PricingRow> Rows { get; private set; } = [];
    public DateTimeOffset? LastLoadedAt { get; private set; }

    public void OnGet()
    {
        Rows = catalog.GetAllCurrentRates();
        LastLoadedAt = catalog.LastLoadedAt;
    }
}
