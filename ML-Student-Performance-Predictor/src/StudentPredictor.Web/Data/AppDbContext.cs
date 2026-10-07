using Microsoft.EntityFrameworkCore;
using StudentPredictor.Web.Models;

namespace StudentPredictor.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Student> Students => Set<Student>();
    public DbSet<PredictionRecord> Predictions => Set<PredictionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Student>().HasIndex(s => s.StudentNumber).IsUnique();
        modelBuilder.Entity<PredictionRecord>().HasIndex(p => p.CreatedAt);
    }
}
