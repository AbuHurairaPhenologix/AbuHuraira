using Microsoft.EntityFrameworkCore;
using StudyPlanner.Web.Models;

namespace StudyPlanner.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Subject> Subjects => Set<Subject>();
    public DbSet<DayAvailability> Availability => Set<DayAvailability>();
    public DbSet<PlanRun> PlanRuns => Set<PlanRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DayAvailability>().Property(d => d.DayOfWeek).ValueGeneratedNever();
        modelBuilder.Entity<PlanRun>().HasIndex(r => r.CreatedAt);
    }
}
