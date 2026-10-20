namespace ShiftScheduler;

public static class ConflictChecker
{
    private record Resolved(Assignment A, DateTime Start, DateTime End);

    public static List<Conflict> Check(AppData data, DateOnly weekStart)
    {
        var result = new List<Conflict>();
        var weekEnd = weekStart.AddDays(6);
        var types = data.ShiftTypes.ToDictionary(t => t.Code);
        var empById = data.Employees.ToDictionary(x => x.Id);

        foreach (var emp in data.Employees)
        {
            var shifts = data.Assignments
                .Where(a => a.EmployeeId == emp.Id && types.ContainsKey(a.ShiftCode))
                .Select(a =>
                {
                    var t = types[a.ShiftCode];
                    var start = a.Date.ToDateTime(TimeOnly.FromTimeSpan(t.Start));
                    return new Resolved(a, start, start + t.Duration);
                })
                .OrderBy(s => s.Start)
                .ToList();

            // 1. Overlaps and rest-period violations between consecutive shifts
            for (int i = 1; i < shifts.Count; i++)
            {
                var prev = shifts[i - 1];
                var cur = shifts[i];
                if (cur.A.Date < weekStart || cur.A.Date > weekEnd) continue;

                if (cur.Start < prev.End)
                {
                    result.Add(new Conflict(
                        $"{emp.Name}: double-booked on {cur.A.Date:ddd dd MMM}", emp.Id, cur.A.Date));
                }
                else
                {
                    var rest = (cur.Start - prev.End).TotalHours;
                    if (rest < data.Rules.MinRestHours)
                        result.Add(new Conflict(
                            $"{emp.Name}: only {rest:0.#}h rest before {cur.A.Date:ddd dd MMM} (min {data.Rules.MinRestHours:0.#}h)",
                            emp.Id, cur.A.Date));
                }
            }

            // 2. Assigned while on leave
            foreach (var s in shifts.Where(s => s.A.Date >= weekStart && s.A.Date <= weekEnd))
            {
                if (data.Leave.Any(l => l.EmployeeId == emp.Id && l.Date == s.A.Date))
                    result.Add(new Conflict(
                        $"{emp.Name}: assigned on leave day {s.A.Date:ddd dd MMM}", emp.Id, s.A.Date));
            }

            // 2b. Assigned on a weekday this employee cannot work
            foreach (var s in shifts.Where(s => s.A.Date >= weekStart && s.A.Date <= weekEnd))
            {
                if (emp.UnavailableDays.Contains(s.A.Date.DayOfWeek))
                    result.Add(new Conflict(
                        $"{emp.Name}: not available on {s.A.Date:dddd} ({s.A.Date:dd MMM})", emp.Id, s.A.Date));
            }

            // 3. Weekly hour limit
            var hours = shifts
                .Where(s => s.A.Date >= weekStart && s.A.Date <= weekEnd)
                .Sum(s => (s.End - s.Start).TotalHours);
            if (hours > emp.MaxWeeklyHours)
                result.Add(new Conflict(
                    $"{emp.Name}: {hours:0.#}h this week (limit {emp.MaxWeeklyHours:0.#}h)", emp.Id));
        }

        // 4. Understaffed shifts
        for (int d = 0; d < 7; d++)
        {
            var date = weekStart.AddDays(d);
            foreach (var t in data.ShiftTypes)
            {
                var count = data.Assignments.Count(a => a.Date == date && a.ShiftCode == t.Code);
                if (count < t.RequiredStaff)
                    result.Add(new Conflict(
                        $"Understaffed: {t.Name} on {date:ddd dd MMM} ({count}/{t.RequiredStaff})",
                        null, date));

                // 5. Role requirements (only checked once someone is assigned to the shift)
                if (count > 0)
                {
                    foreach (var req in t.RoleRequirements)
                    {
                        int have = data.Assignments.Count(a =>
                            a.Date == date && a.ShiftCode == t.Code
                            && empById.TryGetValue(a.EmployeeId, out var who)
                            && string.Equals(who.Role.Trim(), req.Role.Trim(), StringComparison.OrdinalIgnoreCase));

                        if (have < req.MinCount)
                            result.Add(new Conflict(
                                $"Missing {req.Role}: {t.Name} on {date:ddd dd MMM} ({have}/{req.MinCount})",
                                null, date));
                    }
                }
            }
        }

        return result;
    }
}
