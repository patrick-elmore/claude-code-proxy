using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ClaudeCodeProxy.Pages.Pricing;

public sealed class IndexModel(PricingCatalog catalog) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public DateOnly? Date { get; set; }

    public DateOnly EffectiveDate { get; private set; }
    public IReadOnlyList<PricingCatalog.PricingRow> Rows { get; private set; } = [];
    public IReadOnlyList<PricingCatalog.ModelSchedule> Schedule { get; private set; } = [];
    public DateTimeOffset? LastLoadedAt { get; private set; }

    public void OnGet()
    {
        EffectiveDate = Date ?? DateOnly.FromDateTime(DateTime.Now);
        Rows = catalog.GetAllCurrentRates(EffectiveDate);
        Schedule = catalog.GetRateSchedule();
        LastLoadedAt = catalog.LastLoadedAt;
    }
}
