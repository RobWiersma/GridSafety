using System.Text.RegularExpressions;

namespace GridSafety.Core;

/// <summary>
/// An employee's record beyond their crew role: HR and contact details a supervisor keeps. Signed-in users only; guests
/// never see it. Deliberately holds nothing a utility shouldn't keep in an ops tool (no date of birth, government IDs or
/// protected characteristics). Every field is optional.
/// </summary>
public sealed record EmployeeProfile(
    string? EmployeeId = null,
    DateOnly? HireDate = null,
    string? Phone = null,
    string? Email = null,
    string? HomeCity = null,
    string? EmergencyContact = null,
    string? EmergencyPhone = null,
    /// <summary>E.g. "FAA Part 107" for a remote pilot, or "Qualified electrical worker".</summary>
    string? Certification = null,
    DateOnly? CertificationExpires = null,
    string? Notes = null)
{
    /// <summary>Trimmed, with blanks as null; null when nothing is left.</summary>
    public EmployeeProfile? Cleaned()
    {
        static string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var c = new EmployeeProfile(T(EmployeeId), HireDate, T(Phone), T(Email), T(HomeCity), T(EmergencyContact), T(EmergencyPhone),
            T(Certification), CertificationExpires, T(Notes));
        return c == new EmployeeProfile() ? null : c;
    }
}

/// <summary>Checks on an employee's record.</summary>
public static partial class Employees
{
    public static Dictionary<string, string[]> Validate(EmployeeProfile? p)
    {
        var e = new Dictionary<string, string[]>();
        if (p is null) return e;
        void Max(string key, string? value, int max, string label)
        {
            if (value is { } v && v.Length > max) e[key] = [$"{label}: max {max} characters."];
        }
        Max("employeeId", p.EmployeeId, 20, "Employee ID");
        Max("homeCity", p.HomeCity, 60, "Home city");
        Max("emergencyContact", p.EmergencyContact, 60, "Emergency contact");
        Max("certification", p.Certification, 80, "Certification");
        Max("notes", p.Notes, 500, "Notes");
        if (p.Email is { } email && (email.Length > 100 || !EmailPattern().IsMatch(email))) e["email"] = ["Enter an email like name@example.com."];
        if (p.Phone is { } phone && (phone.Length > 25 || !PhonePattern().IsMatch(phone))) e["phone"] = ["Phone: digits, spaces and + ( ) - . only."];
        if (p.EmergencyPhone is { } ep && (ep.Length > 25 || !PhonePattern().IsMatch(ep))) e["emergencyPhone"] = ["Phone: digits, spaces and + ( ) - . only."];
        if (p.HireDate is { Year: < 1950 or > 2100 }) e["hireDate"] = ["Enter a real hire date."];
        if (p.CertificationExpires is { Year: < 1950 or > 2100 }) e["certificationExpires"] = ["Enter a real expiry date."];
        return e;
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^[0-9 +()\-.x]+$")]
    private static partial Regex PhonePattern();
}
