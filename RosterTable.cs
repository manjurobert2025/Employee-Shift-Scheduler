namespace ShiftScheduler;

public static class ScheduleMath
{
    public static double WeeklyHours(AppData data, Employee emp, DateOnly weekStart)
    {
        var weekEnd = weekStart.AddDays(6);
        var types = data.ShiftTypes.ToDictionary(t => t.Code);
        return data.Assignments
            .Where(a => a.EmployeeId == emp.Id && a.Date >= weekStart && a.Date <= weekEnd && types.ContainsKey(a.ShiftCode))
            .Sum(a => types[a.ShiftCode].Duration.TotalHours);
    }
}

public sealed class RosterRow
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public List<string> Cells { get; set; } = new();   // 7 entries, Monday to Sunday
    public double Hours { get; set; }
    public double MaxHours { get; set; }
}

/// <summary>A plain table of one week's roster, used by both print/PDF and Excel export.</summary>
public sealed class RosterTable
{
    public string Title { get; set; } = "";
    public List<string> DayHeaders { get; set; } = new();
    public List<RosterRow> Rows { get; set; } = new();
    public List<string> Legend { get; set; } = new();

    public static RosterTable Build(AppData data, DateOnly weekStart)
    {
        var table = new RosterTable
        {
            Title = $"Shift roster: week of {weekStart:dd MMM yyyy} to {weekStart.AddDays(6):dd MMM yyyy}"
        };

        for (int d = 0; d < 7; d++)
            table.DayHeaders.Add(weekStart.AddDays(d).ToString("ddd dd MMM"));

        foreach (var emp in data.Employees)
        {
            var row = new RosterRow
            {
                Name = emp.Name,
                Role = emp.Role,
                Hours = ScheduleMath.WeeklyHours(data, emp, weekStart),
                MaxHours = emp.MaxWeeklyHours
            };

            for (int d = 0; d < 7; d++)
            {
                var date = weekStart.AddDays(d);
                var assignment = data.Assignments.FirstOrDefault(a => a.EmployeeId == emp.Id && a.Date == date);
                if (assignment != null)
                    row.Cells.Add(assignment.ShiftCode);
                else if (data.Leave.Any(l => l.EmployeeId == emp.Id && l.Date == date))
                    row.Cells.Add("Leave");
                else
                    row.Cells.Add("-");
            }

            table.Rows.Add(row);
        }

        foreach (var t in data.ShiftTypes)
            table.Legend.Add($"{t.Code} = {t.Name} ({t.Start:hh\\:mm} to {t.End:hh\\:mm})");
        table.Legend.Add("Leave = on leave, - = not scheduled");

        return table;
    }
}
