using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StudentPredictor.Web.Data;
using StudentPredictor.Web.Models;

namespace StudentPredictor.Web.Pages;

public class PredictModel(DataSeeder predictions) : PageModel
{
    [BindProperty]
    public PredictionInput Input { get; set; } = new();

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();

        var features = new StudentFeatures(Input.Attendance, Input.StudyHours, Input.AssignmentAvg, Input.QuizAvg,
            Input.PreviousExam);
        var record = await predictions.SavePredictionAsync(Input.StudentName.Trim(), features, DateTime.Now);
        return RedirectToPage("/Result", new { id = record.Id });
    }

    public class PredictionInput
    {
        [Required, StringLength(80), Display(Name = "Student name")]
        public string StudentName { get; set; } = "";

        [Range(0, 100), Display(Name = "Attendance (%)")]
        public double Attendance { get; set; } = 85;

        [Range(0, 40), Display(Name = "Study hours per week")]
        public double StudyHours { get; set; } = 10;

        [Range(0, 100), Display(Name = "Assignment average")]
        public double AssignmentAvg { get; set; } = 72;

        [Range(0, 100), Display(Name = "Quiz average")]
        public double QuizAvg { get; set; } = 68;

        [Range(0, 100), Display(Name = "Previous exam score")]
        public double PreviousExam { get; set; } = 65;
    }
}
