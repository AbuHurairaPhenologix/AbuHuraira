using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using StudentPredictor.Web.Data;
using StudentPredictor.Web.Models;

namespace StudentPredictor.Web.Pages;

public class StudentsModel(AppDbContext db) : PageModel
{
    private const int PageSize = 20;

    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string? Category { get; set; }
    [BindProperty(SupportsGet = true)] public string? Programme { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;

    public List<Student> Students { get; private set; } = [];
    public List<string> Programmes { get; private set; } = [];
    public int TotalMatches { get; private set; }
    public int TotalPages { get; private set; }
    public double MatchMae { get; private set; }

    public async Task OnGetAsync()
    {
        Programmes = await db.Students.Select(s => s.Programme).Distinct().OrderBy(p => p).ToListAsync();

        var query = db.Students.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Search))
            query = query.Where(s => s.Name.Contains(Search) || s.StudentNumber.Contains(Search));
        if (!string.IsNullOrWhiteSpace(Category)) query = query.Where(s => s.Category == Category);
        if (!string.IsNullOrWhiteSpace(Programme)) query = query.Where(s => s.Programme == Programme);

        var matches = await query.OrderBy(s => s.StudentNumber).ToListAsync();
        TotalMatches = matches.Count;
        TotalPages = Math.Max(1, (int)Math.Ceiling(TotalMatches / (double)PageSize));
        PageNumber = Math.Clamp(PageNumber, 1, TotalPages);
        MatchMae = matches.Count == 0 ? 0 : matches.Average(s => Math.Abs(s.FinalScore - (s.PredictedScore ?? 0)));
        Students = matches.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList();
    }

    public string PageLink(int page) =>
        Url.Page("/Students", new { Search, Category, Programme, PageNumber = page })!;
}
