using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace AutoSphere.SharedKernel.Versioning;

/// <summary>
/// ECU application software version in <c>MAJOR.MINOR.PATCH</c> form.
/// Pre-release and build metadata of full SemVer 2.0 are intentionally not supported:
/// ECU software in this prototype is only ever released as a plain three-part version.
/// </summary>
public sealed record SoftwareVersion : IComparable<SoftwareVersion>
{
    public SoftwareVersion(int major, int minor, int patch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public static SoftwareVersion Parse(string value) =>
        TryParse(value, out var version)
            ? version
            : throw new FormatException($"'{value}' is not a valid MAJOR.MINOR.PATCH software version.");

    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out SoftwareVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (parts[i].Length == 0 || parts[i].Length > 9 || !parts[i].All(char.IsAsciiDigit)
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new SoftwareVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public int CompareTo(SoftwareVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        return result != 0 ? result : Patch.CompareTo(other.Patch);
    }

    public static bool operator >(SoftwareVersion left, SoftwareVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(SoftwareVersion left, SoftwareVersion right) => left.CompareTo(right) < 0;

    public static bool operator >=(SoftwareVersion left, SoftwareVersion right) => left.CompareTo(right) >= 0;

    public static bool operator <=(SoftwareVersion left, SoftwareVersion right) => left.CompareTo(right) <= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
