using System.Text.Json.Serialization;

namespace ShiftScheduler;

public class Employee
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public double MaxWeeklyHours { get; set; } = 48;
    public List<DayOfWeek> UnavailableDays { get; set; } = new();   // weekdays this person cannot work
}

public class ShiftType
{
    public string Code { get; set; } = "";      // e.g. M, E, N
    public string Name { get; set; } = "";      // e.g. Morning
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public int RequiredStaff { get; set; } = 1; // minimum people per day
    public List<RoleRequirement> RoleRequirements { get; set; } = new(); // e.g. 1 Supervisor

    [JsonIgnore]
    public TimeSpan Duration =>
        End > Start ? End - Start : End + TimeSpan.FromHours(24) - Start; // handles overnight shifts
}

public class RoleRequirement
{
    public string Role { get; set; } = "";
    public int MinCount { get; set; } = 1;
}

public class Assignment
{
    public Guid EmployeeId { get; set; }
    public DateOnly Date { get; set; }
    public string ShiftCode { get; set; } = "";
}

public class LeaveEntry
{
    public Guid EmployeeId { get; set; }
    public DateOnly Date { get; set; }
}

public class Rules
{
    public double MinRestHours { get; set; } = 11;
}

public class AppData
{
    public List<Employee> Employees { get; set; } = new();
    public List<ShiftType> ShiftTypes { get; set; } = new();
    public List<Assignment> Assignments { get; set; } = new();
    public List<LeaveEntry> Leave { get; set; } = new();
    public Rules Rules { get; set; } = new();
}

public record Conflict(string Message, Guid? EmployeeId = null, DateOnly? Date = null);
