namespace StudyPlanner.Web.Models;

/// <summary>Categorical palette (validated for the dark surface). Slots are assigned in fixed order, never by rank.</summary>
public static class SubjectColors
{
    private static readonly string[] Palette =
        ["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767"];

    public const string Removed = "#5f5e5a";

    public static string Get(int colorIndex) => Palette[colorIndex % Palette.Length];

    public static int NextIndex(IEnumerable<int> used)
    {
        var taken = used.ToHashSet();
        for (var i = 0; i < Palette.Length; i++)
            if (!taken.Contains(i)) return i;
        return taken.Count % Palette.Length;
    }
}
