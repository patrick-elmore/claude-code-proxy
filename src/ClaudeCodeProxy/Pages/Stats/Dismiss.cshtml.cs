using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ClaudeCodeProxy.Pages.Stats;

public sealed class DismissModel(StatsTracker tracker) : PageModel
{
    public IActionResult OnGet() => NotFound();

    public IActionResult OnPost(string mode)
    {
        if (!Enum.TryParse<PromptFailureMode>(mode, ignoreCase: true, out var parsed))
            return BadRequest($"Unknown failure mode: {mode}");
        tracker.AcknowledgeFailureMode(parsed);
        return RedirectToPage("/Stats/Index");
    }
}
