using System.Globalization;
using System.IO;
using ClosedXML.Excel;

namespace ShiftScheduler;

/// <summary>What an import will do. Nothing is changed until Apply() is called.</summary>
public sealed class ImportPlan
{
    public List<string> Summary { get; } = new();
    public List<string> Problems { get; } = new();
    public bool HasChanges { get; set; }
    public DateOnly? FocusDate { get; set; }          // week to show after the import
    public Action Apply { get; set; } = () => { };
}

public static class ExcelImport
{

    public static ImportPlan PlanCombined(AppData data, string path)
    {
        var plan = new ImportPlan();
        using var wb = OpenWorkbook(path);

        if (!wb.Worksheets.TryGetWorksheet("Employees", out var employeesWs))
            throw new InvalidOperationException(
                "The workbook must contain an Employees worksheet.");

        if (!wb.Worksheets.TryGetWorksheet("Leave", out var leaveWs))
            throw new InvalidOperationException(
                "The workbook must contain a Leave worksheet.");

        // ---------------------------------------------------------
        // 1. Read employee data
        // ---------------------------------------------------------
        int empLastRow = LastRow(employeesWs);
        int empLastCol = LastCol(employeesWs);

        int empHeaderRow = FindHeaderRow(
            employeesWs, empLastRow, empLastCol, NameKeys);

        if (empHeaderRow == 0)
            throw new InvalidOperationException(
                "Could not find the Name column in Employees.");

        var eh = ReadHeaders(employeesWs, empHeaderRow, empLastCol);

        int cName = Col(eh, NameKeys);
        int cRole = Col(eh, "role");
        int cAvailable = Col(eh, "availabledays", "available");
        int cUnavailable = Col(eh, "unavailabledays", "unavailable",
            "daysoff", "cannotwork");
        int cHours = Col(eh, "maxhoursperweek", "maxhoursweek",
            "maxhours", "maxweeklyhours", "weeklyhours");

        if (cAvailable > 0 && cUnavailable > 0)
            throw new InvalidOperationException(
                "Use either Available Days or Unavailable Days, not both.");

        if (cAvailable == 0 && cUnavailable == 0)
            throw new InvalidOperationException(
                "Employees must have an Available Days or Unavailable Days column.");

        var existing = data.Employees
            .GroupBy(e => e.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(),
                StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toAdd = new List<Employee>();
        var toUpdate = new List<(
            Employee Emp, string? Role, double? Hours,
            List<DayOfWeek>? UnavailableDays)>();

        for (int r = empHeaderRow + 1; r <= empLastRow; r++)
        {
            if (RowIsEmpty(employeesWs, r, empLastCol))
                continue;

            string name = Text(employeesWs.Cell(r, cName));

            // Ignore the example row in the downloadable template.
            if (name.Equals("Example Guard",
                StringComparison.OrdinalIgnoreCase))
                continue;

            if (name.Length == 0)
            {
                plan.Problems.Add($"Employees row {r}: name is empty.");
                continue;
            }

            if (!seen.Add(name))
            {
                plan.Problems.Add(
                    $"Employees row {r}: '{name}' appears more than once.");
                continue;
            }

            string? role = cRole > 0 &&
                !employeesWs.Cell(r, cRole).IsEmpty()
                    ? Text(employeesWs.Cell(r, cRole))
                    : null;

            double? hours = null;

            if (cHours > 0 && !employeesWs.Cell(r, cHours).IsEmpty())
            {
                if (!TryGetNumber(employeesWs.Cell(r, cHours), out double h)
                    || h <= 0 || h > 168)
                {
                    plan.Problems.Add(
                        $"Employees row {r}: '{name}' has invalid weekly hours.");
                    continue;
                }

                hours = h;
            }

            List<DayOfWeek>? unavailable = null;

            int daysColumn = cAvailable > 0 ? cAvailable : cUnavailable;

            if (!employeesWs.Cell(r, daysColumn).IsEmpty())
            {
                string dayText = Text(employeesWs.Cell(r, daysColumn));

                if (cAvailable > 0)
                {
                    if (dayText.Equals("All", StringComparison.OrdinalIgnoreCase))
                    {
                        unavailable = new List<DayOfWeek>();
                    }
                    else if (dayText.Equals("None", StringComparison.OrdinalIgnoreCase))
                    {
                        unavailable = Enum.GetValues<DayOfWeek>().ToList();
                    }
                    else if (TryParseDays(dayText, out var available, out var bad))
                    {
                        unavailable = Enum.GetValues<DayOfWeek>()
                            .Where(d => !available.Contains(d)).ToList();
                    }
                    else
                    {
                        plan.Problems.Add(
                            $"Employees row {r}: unknown available day '{bad}'.");
                        continue;
                    }
                }
                else
                {
                    if (TryParseDays(dayText, out var parsed, out var bad))
                    {
                        unavailable = parsed;
                    }
                    else
                    {
                        plan.Problems.Add(
                            $"Employees row {r}: unknown unavailable day '{bad}'.");
                        continue;
                    }
                }
            }

            if (existing.TryGetValue(name, out var emp))
            {
                bool changed =
                    (role != null && role != emp.Role) ||
                    (hours != null &&
                        Math.Abs(hours.Value - emp.MaxWeeklyHours) > 0.0001) ||
                    (unavailable != null &&
                        !SameDays(unavailable, emp.UnavailableDays));

                if (changed)
                    toUpdate.Add((emp, role, hours, unavailable));
            }
            else
            {
                toAdd.Add(new Employee
                {
                    Name = name,
                    Role = role ?? "",
                    MaxWeeklyHours = hours ?? 48,
                    UnavailableDays = unavailable ?? new List<DayOfWeek>()
                });
            }
        }

        // ---------------------------------------------------------
        // 2. Read leave data, including newly imported employees
        // ---------------------------------------------------------
        int leaveLastRow = LastRow(leaveWs);
        int leaveLastCol = LastCol(leaveWs);

        int leaveHeaderRow = FindHeaderRow(
            leaveWs, leaveLastRow, leaveLastCol, NameKeys);

        if (leaveHeaderRow == 0)
            throw new InvalidOperationException(
                "Could not find the Employee column in Leave.");

        var lh = ReadHeaders(leaveWs, leaveHeaderRow, leaveLastCol);

        int cEmp = Col(lh, NameKeys);
        int cFrom = Col(lh, "fromdate", "from", "startdate", "start", "date");
        int cTo = Col(lh, "todate", "to", "enddate", "end");

        if (cFrom == 0)
            throw new InvalidOperationException(
                "Could not find the From date column in Leave.");

        var byName = new Dictionary<string, Employee>(
            existing, StringComparer.OrdinalIgnoreCase);

        foreach (var emp in toAdd)
            byName[emp.Name.Trim()] = emp;

        var existingLeave = data.Leave
            .Select(l => (l.EmployeeId, l.Date)).ToHashSet();

        var newLeave = new HashSet<(Guid Emp, DateOnly Date)>();

        for (int r = leaveHeaderRow + 1; r <= leaveLastRow; r++)
        {
            if (RowIsEmpty(leaveWs, r, leaveLastCol))
                continue;

            string name = Text(leaveWs.Cell(r, cEmp));

            // Ignore blank example rows.
            if (name.Length == 0)
                continue;

            if (!byName.TryGetValue(name, out var emp))
            {
                plan.Problems.Add(
                    $"Leave row {r}: employee '{name}' was not found.");
                continue;
            }

            if (!TryGetDate(leaveWs.Cell(r, cFrom), out var from))
            {
                plan.Problems.Add(
                    $"Leave row {r}: '{name}' has an invalid From date.");
                continue;
            }

            DateOnly to = from;

            if (cTo > 0 && !leaveWs.Cell(r, cTo).IsEmpty()
                && !TryGetDate(leaveWs.Cell(r, cTo), out to))
            {
                plan.Problems.Add(
                    $"Leave row {r}: '{name}' has an invalid To date.");
                continue;
            }

            if (to < from || to.DayNumber - from.DayNumber > 366)
            {
                plan.Problems.Add(
                    $"Leave row {r}: '{name}' has an invalid date range.");
                continue;
            }

            for (var date = from; date <= to; date = date.AddDays(1))
            {
                if (!existingLeave.Contains((emp.Id, date)))
                    newLeave.Add((emp.Id, date));
            }
        }

        // ---------------------------------------------------------
        // 3. Prepare the preview and apply action
        // ---------------------------------------------------------
        plan.Summary.Add($"{toAdd.Count} new employee(s) will be added.");
        plan.Summary.Add($"{toUpdate.Count} existing employee(s) will be updated.");
        plan.Summary.Add($"{newLeave.Count} new leave day(s) will be added.");

        plan.HasChanges =
            toAdd.Count > 0 || toUpdate.Count > 0 || newLeave.Count > 0;

        if (newLeave.Count > 0)
            plan.FocusDate = newLeave.Min(x => x.Date);

        plan.Apply = () =>
        {
            data.Employees.AddRange(toAdd);

            foreach (var (emp, role, hours, unavailable) in toUpdate)
            {
                if (role != null) emp.Role = role;
                if (hours != null) emp.MaxWeeklyHours = hours.Value;
                if (unavailable != null) emp.UnavailableDays = unavailable;
            }

            foreach (var (empId, date) in newLeave)
            {
                data.Leave.Add(new LeaveEntry
                {
                    EmployeeId = empId,
                    Date = date
                });
            }
        };

        return plan;
    }

    private static readonly string[] NameKeys = { "name", "employee", "employeename" };

    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy",
        "dd MMM yyyy", "d MMM yyyy", "dd-MMM-yyyy", "d-MMM-yyyy", "dd MMMM yyyy", "d MMMM yyyy",
        "ddd dd MMM yyyy"
    };

    // =====================================================================
    // 1. EMPLOYEES
    // =====================================================================

    public static ImportPlan PlanEmployees(AppData data, string path)
    {
        var plan = new ImportPlan();
        using var wb = OpenWorkbook(path);
        var ws = FirstSheet(wb);
        int lastRow = LastRow(ws), lastCol = LastCol(ws);

        int hr = FindHeaderRow(ws, lastRow, lastCol, NameKeys);
        if (hr == 0)
            throw new InvalidOperationException("Could not find a 'Name' column in the first rows. Please use the template.");

        var h = ReadHeaders(ws, hr, lastCol);
        int cName = Col(h, NameKeys);
        int cRole = Col(h, "role");
        int cHours = Col(h, "maxhoursperweek", "maxhoursweek", "maxhours", "maxweeklyhours", "weeklyhours");
        int cDays = Col(h, "unavailabledays", "unavailable", "daysoff", "cannotwork");

        var existing = data.Employees
            .GroupBy(e => e.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toAdd = new List<Employee>();
        var toUpdate = new List<(Employee Emp, string? Role, double? Hours, List<DayOfWeek>? Days)>();
        int unchanged = 0;

        for (int r = hr + 1; r <= lastRow; r++)
        {
            if (RowIsEmpty(ws, r, lastCol)) continue;

            var name = Text(ws.Cell(r, cName));
            if (name.Length == 0) { plan.Problems.Add($"Row {r}: the name is empty."); continue; }
            if (!seen.Add(name)) { plan.Problems.Add($"Row {r}: '{name}' appears more than once in the file."); continue; }

            string? role = null;
            if (cRole > 0 && !ws.Cell(r, cRole).IsEmpty()) role = Text(ws.Cell(r, cRole));

            double? hours = null;
            if (cHours > 0 && !ws.Cell(r, cHours).IsEmpty())
            {
                if (!TryGetNumber(ws.Cell(r, cHours), out var hv) || hv <= 0 || hv > 168)
                {
                    plan.Problems.Add($"Row {r}: '{name}' has invalid max hours per week (use a number from 1 to 168).");
                    continue;
                }
                hours = hv;
            }

            List<DayOfWeek>? days = null;
            if (cDays > 0 && !ws.Cell(r, cDays).IsEmpty())
            {
                if (!TryParseDays(Text(ws.Cell(r, cDays)), out var parsed, out var bad))
                {
                    plan.Problems.Add($"Row {r}: '{name}' has an unknown day '{bad}' (use Mon, Tue, Wed, Thu, Fri, Sat, Sun).");
                    continue;
                }
                days = parsed;
            }

            if (existing.TryGetValue(name, out var emp))
            {
                bool changed =
                    (role != null && role != emp.Role) ||
                    (hours != null && Math.Abs(hours.Value - emp.MaxWeeklyHours) > 0.0001) ||
                    (days != null && !SameDays(days, emp.UnavailableDays));

                if (changed) toUpdate.Add((emp, role, hours, days));
                else unchanged++;
            }
            else
            {
                toAdd.Add(new Employee
                {
                    Name = name,
                    Role = role ?? "",
                    MaxWeeklyHours = hours ?? 48,
                    UnavailableDays = days ?? new List<DayOfWeek>()
                });
            }
        }

        plan.Summary.Add($"{toAdd.Count} new employee(s) will be added.");
        plan.Summary.Add($"{toUpdate.Count} existing employee(s) will be updated (blank cells keep the current value).");
        if (unchanged > 0) plan.Summary.Add($"{unchanged} existing employee(s) already match.");
        plan.HasChanges = toAdd.Count + toUpdate.Count > 0;

        plan.Apply = () =>
        {
            data.Employees.AddRange(toAdd);
            foreach (var (emp, role, hours, days) in toUpdate)
            {
                if (role != null) emp.Role = role;
                if (hours != null) emp.MaxWeeklyHours = hours.Value;
                if (days != null) emp.UnavailableDays = days;
            }
        };

        return plan;
    }

    // =====================================================================
    // 2. LEAVE
    // =====================================================================

    public static ImportPlan PlanLeave(AppData data, string path)
    {
        var plan = new ImportPlan();
        using var wb = OpenWorkbook(path);
        var ws = FirstSheet(wb);
        int lastRow = LastRow(ws), lastCol = LastCol(ws);

        int hr = FindHeaderRow(ws, lastRow, lastCol, NameKeys);
        if (hr == 0)
            throw new InvalidOperationException("Could not find an 'Employee' column in the first rows. Please use the template.");

        var h = ReadHeaders(ws, hr, lastCol);
        int cEmp = Col(h, NameKeys);
        int cFrom = Col(h, "fromdate", "from", "startdate", "start", "date");
        int cTo = Col(h, "todate", "to", "enddate", "end");
        if (cFrom == 0)
            throw new InvalidOperationException("Could not find a 'From date' column. Please use the template.");

        var byName = EmployeesByName(data);
        var existingLeave = data.Leave.Select(l => (l.EmployeeId, l.Date)).ToHashSet();
        var newLeave = new HashSet<(Guid Emp, DateOnly Date)>();
        int alreadyOnLeave = 0;

        for (int r = hr + 1; r <= lastRow; r++)
        {
            if (RowIsEmpty(ws, r, lastCol)) continue;

            var name = Text(ws.Cell(r, cEmp));
            if (!byName.TryGetValue(name, out var emp))
            {
                plan.Problems.Add($"Row {r}: employee '{name}' was not found. Add them first or fix the spelling.");
                continue;
            }

            if (!TryGetDate(ws.Cell(r, cFrom), out var from))
            {
                plan.Problems.Add($"Row {r}: '{name}' has a missing or invalid From date.");
                continue;
            }

            var to = from;
            if (cTo > 0 && !ws.Cell(r, cTo).IsEmpty())
            {
                if (!TryGetDate(ws.Cell(r, cTo), out to))
                {
                    plan.Problems.Add($"Row {r}: '{name}' has an invalid To date.");
                    continue;
                }
            }

            if (to < from)
            {
                plan.Problems.Add($"Row {r}: '{name}' has a To date before the From date.");
                continue;
            }
            if (to.DayNumber - from.DayNumber > 366)
            {
                plan.Problems.Add($"Row {r}: '{name}' has a range longer than one year.");
                continue;
            }

            for (var d = from; d <= to; d = d.AddDays(1))
            {
                if (existingLeave.Contains((emp.Id, d)) || newLeave.Contains((emp.Id, d))) alreadyOnLeave++;
                else newLeave.Add((emp.Id, d));
            }
        }

        int people = newLeave.Select(x => x.Emp).Distinct().Count();
        plan.Summary.Add($"{newLeave.Count} leave day(s) will be added for {people} employee(s).");
        if (alreadyOnLeave > 0) plan.Summary.Add($"{alreadyOnLeave} day(s) were already on leave and will be ignored.");
        plan.HasChanges = newLeave.Count > 0;
        if (newLeave.Count > 0) plan.FocusDate = newLeave.Min(x => x.Date);

        plan.Apply = () =>
        {
            foreach (var (empId, date) in newLeave)
                data.Leave.Add(new LeaveEntry { EmployeeId = empId, Date = date });
        };

        return plan;
    }

    // =====================================================================
    // 3. SHIFTS (weekly grid or simple list)
    // =====================================================================

    public static ImportPlan PlanShifts(AppData data, string path)
    {
        var plan = new ImportPlan();
        using var wb = OpenWorkbook(path);
        var ws = FirstSheet(wb);
        int lastRow = LastRow(ws), lastCol = LastCol(ws);

        int hr = FindHeaderRow(ws, lastRow, lastCol, NameKeys);
        if (hr == 0)
            throw new InvalidOperationException("Could not find an 'Employee' column in the first rows. Please use the template or an exported roster.");

        var h = ReadHeaders(ws, hr, lastCol);
        int cEmp = Col(h, NameKeys);

        var byName = EmployeesByName(data);
        var codes = data.ShiftTypes.ToDictionary(t => t.Code, t => t.Code, StringComparer.OrdinalIgnoreCase);

        // null = clear that day
        var pending = new Dictionary<(Guid Emp, DateOnly Date), string?>();
        var unknownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Real dates in the header row mean the weekly grid layout
        var gridCols = new List<(int Col, DateOnly Date)>();
        for (int c = 1; c <= lastCol; c++)
            if (c != cEmp && TryGetDate(ws.Cell(hr, c), out var d))
                gridCols.Add((c, d));

        // Returns true when the cell should be skipped (reason already logged by caller)
        bool ReadShiftValue(string value, out string? code)
        {
            code = null;
            var v = value.Trim();
            if (v.Length == 0 || v.Equals("off", StringComparison.OrdinalIgnoreCase) || v == "-" || v == "\u2013")
                return true;                                   // clear
            if (v.Equals("leave", StringComparison.OrdinalIgnoreCase))
                return false;                                  // ignored
            if (codes.TryGetValue(v, out var canonical)) { code = canonical; return true; }
            return false;
        }

        if (gridCols.Count > 0)
        {
            for (int r = hr + 1; r <= lastRow; r++)
            {
                var name = Text(ws.Cell(r, cEmp));
                if (name.Length == 0) continue;
                if (name.Equals("legend", StringComparison.OrdinalIgnoreCase)) break;

                if (!byName.TryGetValue(name, out var emp))
                {
                    if (unknownNames.Add(name))
                        plan.Problems.Add($"Row {r}: employee '{name}' was not found. Add them first or fix the spelling.");
                    continue;
                }

                foreach (var (col, date) in gridCols)
                {
                    var text = Text(ws.Cell(r, col));
                    bool handled = ReadShiftValue(text, out var code);

                    if (handled)
                        pending[(emp.Id, date)] = code;        // code null = clear
                    else if (!text.Equals("leave", StringComparison.OrdinalIgnoreCase))
                        plan.Problems.Add($"Row {r}: unknown shift code '{text}' for {name} on {date:dd MMM}.");
                }
            }
        }
        else
        {
            int cDate = Col(h, "date", "workdate", "day");
            int cShift = Col(h, "shiftcode", "shift", "code");
            if (cDate == 0 || cShift == 0)
                throw new InvalidOperationException(
                    "Could not find the layout. Use either a weekly grid with real dates in the header row, " +
                    "or a list with the columns Employee, Date and Shift.");

            for (int r = hr + 1; r <= lastRow; r++)
            {
                if (RowIsEmpty(ws, r, lastCol)) continue;

                var name = Text(ws.Cell(r, cEmp));
                if (!byName.TryGetValue(name, out var emp))
                {
                    plan.Problems.Add($"Row {r}: employee '{name}' was not found. Add them first or fix the spelling.");
                    continue;
                }
                if (!TryGetDate(ws.Cell(r, cDate), out var date))
                {
                    plan.Problems.Add($"Row {r}: '{name}' has a missing or invalid date.");
                    continue;
                }

                var text = Text(ws.Cell(r, cShift));
                bool handled = ReadShiftValue(text, out var code);
                if (handled)
                {
                    if (pending.ContainsKey((emp.Id, date)))
                        plan.Problems.Add($"Row {r}: {name} on {date:dd MMM yyyy} is listed twice. The last entry is used.");
                    pending[(emp.Id, date)] = code;
                }
                else if (!text.Equals("leave", StringComparison.OrdinalIgnoreCase))
                {
                    plan.Problems.Add($"Row {r}: unknown shift code '{text}' for {name} on {date:dd MMM yyyy}.");
                }
            }
        }

        // Compare with what is already scheduled
        var current = new Dictionary<(Guid Emp, DateOnly Date), string>();
        foreach (var a in data.Assignments) current[(a.EmployeeId, a.Date)] = a.ShiftCode;

        int added = 0, replaced = 0, cleared = 0, same = 0;
        foreach (var kv in pending)
        {
            current.TryGetValue(kv.Key, out var cur);
            if (kv.Value == null) { if (cur != null) cleared++; }
            else if (cur == null) added++;
            else if (string.Equals(cur, kv.Value, StringComparison.Ordinal)) same++;
            else replaced++;
        }

        plan.Summary.Add($"{added} shift(s) will be added.");
        plan.Summary.Add($"{replaced} shift(s) will be changed.");
        plan.Summary.Add($"{cleared} shift(s) will be cleared (blank, Off or - in the file).");
        if (same > 0) plan.Summary.Add($"{same} shift(s) already match.");
        if (pending.Count > 0)
        {
            var first = pending.Keys.Min(k => k.Date);
            var last = pending.Keys.Max(k => k.Date);
            plan.Summary.Add($"Dates covered: {first:dd MMM yyyy} to {last:dd MMM yyyy}.");
            plan.FocusDate = first;
        }
        plan.HasChanges = added + replaced + cleared > 0;

        plan.Apply = () =>
        {
            var keys = pending.Keys.ToHashSet();
            data.Assignments.RemoveAll(a => keys.Contains((a.EmployeeId, a.Date)));
            foreach (var kv in pending)
            {
                if (kv.Value != null)
                    data.Assignments.Add(new Assignment { EmployeeId = kv.Key.Emp, Date = kv.Key.Date, ShiftCode = kv.Value });
            }
        };

        return plan;
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static XLWorkbook OpenWorkbook(string path)
    {
        // FileShare.ReadWrite lets us read a file that is still open in Excel.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var ms = new MemoryStream();
        fs.CopyTo(ms);
        ms.Position = 0;
        return new XLWorkbook(ms);
    }

    private static IXLWorksheet FirstSheet(XLWorkbook wb) =>
        wb.Worksheets.FirstOrDefault() ?? throw new InvalidOperationException("The workbook has no sheets.");

    private static int LastRow(IXLWorksheet ws) => ws.LastRowUsed()?.RowNumber() ?? 0;
    private static int LastCol(IXLWorksheet ws) => ws.LastColumnUsed()?.ColumnNumber() ?? 0;

    private static string Text(IXLCell cell) =>
        cell.IsEmpty() ? "" : (cell.GetFormattedString() ?? "").Trim();

    private static string Norm(string s) =>
        new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool RowIsEmpty(IXLWorksheet ws, int row, int lastCol)
    {
        for (int c = 1; c <= lastCol; c++)
            if (!ws.Cell(row, c).IsEmpty()) return false;
        return true;
    }

    private static int FindHeaderRow(IXLWorksheet ws, int lastRow, int lastCol, string[] keys)
    {
        for (int r = 1; r <= Math.Min(lastRow, 15); r++)
            for (int c = 1; c <= lastCol; c++)
                if (keys.Contains(Norm(Text(ws.Cell(r, c))))) return r;
        return 0;
    }

    private static Dictionary<string, int> ReadHeaders(IXLWorksheet ws, int row, int lastCol)
    {
        var map = new Dictionary<string, int>();
        for (int c = 1; c <= lastCol; c++)
        {
            var key = Norm(Text(ws.Cell(row, c)));
            if (key.Length > 0) map.TryAdd(key, c);
        }
        return map;
    }

    private static int Col(Dictionary<string, int> headers, params string[] keys)
    {
        foreach (var k in keys)
            if (headers.TryGetValue(k, out var col)) return col;
        return 0;
    }

    private static Dictionary<string, Employee> EmployeesByName(AppData data) =>
        data.Employees
            .GroupBy(e => e.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private static bool TryGetNumber(IXLCell cell, out double value)
    {
        value = 0;
        if (cell.IsEmpty()) return false;
        if (cell.DataType == XLDataType.Number)
        {
            value = cell.GetDouble();
            return true;
        }
        var text = Text(cell);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    private static bool TryGetDate(IXLCell cell, out DateOnly date)
    {
        date = default;
        if (cell.IsEmpty()) return false;

        if (cell.DataType == XLDataType.DateTime)
        {
            date = DateOnly.FromDateTime(cell.GetDateTime());
            return true;
        }

        if (cell.DataType == XLDataType.Number)
        {
            var n = cell.GetDouble();
            if (n >= 20000 && n <= 80000)       // Excel date serial numbers for roughly 1954 to 2118
            {
                date = DateOnly.FromDateTime(DateTime.FromOADate(n));
                return true;
            }
            return false;
        }

        if (DateTime.TryParseExact(Text(cell), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            date = DateOnly.FromDateTime(dt);
            return true;
        }
        return false;
    }

    private static bool TryParseDays(string text, out List<DayOfWeek> days, out string bad)
    {
        days = new List<DayOfWeek>();
        bad = "";
        var t = text.Trim();
        if (t.Length == 0 || t == "-" || t.Equals("none", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var token in t.Split(new[] { ',', ';', '/', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var key = token.Length >= 3 ? token[..3].ToLowerInvariant() : "";
            DayOfWeek? day = key switch
            {
                "mon" => DayOfWeek.Monday,
                "tue" => DayOfWeek.Tuesday,
                "wed" => DayOfWeek.Wednesday,
                "thu" => DayOfWeek.Thursday,
                "fri" => DayOfWeek.Friday,
                "sat" => DayOfWeek.Saturday,
                "sun" => DayOfWeek.Sunday,
                _ => null
            };

            if (day == null) { bad = token; return false; }
            if (!days.Contains(day.Value)) days.Add(day.Value);
        }
        return true;
    }

    private static bool SameDays(List<DayOfWeek> a, List<DayOfWeek> b) =>
        a.Count == b.Count && !a.Except(b).Any();
}

/// <summary>Creates the downloadable Excel templates.</summary>
public static class ExcelTemplates
{

    public static void WriteCombinedTemplate(string path)
    {
        using var wb = new XLWorkbook();

        // Employees worksheet
        var employees = wb.AddWorksheet("Employees");
        string[] employeeHeaders =
        {
        "Name", "Role", "Available Days", "Max hours per week"
    };

        for (int i = 0; i < employeeHeaders.Length; i++)
            employees.Cell(1, i + 1).Value = employeeHeaders[i];

        employees.Row(1).Style.Font.Bold = true;
        employees.Column(1).Width = 26;
        employees.Column(2).Width = 18;
        employees.Column(3).Width = 28;
        employees.Column(4).Width = 22;

        employees.Cell(2, 1).Value = "Example Guard";
        employees.Cell(2, 2).Value = "Guard";
        employees.Cell(2, 3).Value = "Mon, Tue, Wed, Thu, Fri";
        employees.Cell(2, 4).Value = 48;

        // Leave worksheet
        var leave = wb.AddWorksheet("Leave");
        string[] leaveHeaders = { "Employee", "From date", "To date" };

        for (int i = 0; i < leaveHeaders.Length; i++)
            leave.Cell(1, i + 1).Value = leaveHeaders[i];

        leave.Row(1).Style.Font.Bold = true;
        leave.Column(1).Width = 26;
        leave.Column(2).Width = 16;
        leave.Column(3).Width = 16;
        leave.Column(2).Style.DateFormat.Format = "yyyy-mm-dd";
        leave.Column(3).Style.DateFormat.Format = "yyyy-mm-dd";

        // Instructions worksheet
        var instructions = wb.AddWorksheet("Instructions");
        string[] lines =
        {
        "Corporate Shift Scheduler — Combined Excel Template",
        "",
        "1. Employees sheet: enter each employee once.",
        "Name: required and should be unique.",
        "Role: for example Guard or Supervisor.",
        "Available Days: list weekdays the employee CAN work, e.g. Mon, Tue, Wed.",
        "Use All for every day, or None if the employee is unavailable all week.",
        "Max hours per week: enter a number from 1 to 168.",
        "",
        "2. Leave sheet: enter employee name and leave dates.",
        "From date is required. To date can be blank for one day.",
        "Employee names must match the Employees sheet or an existing employee.",
        "",
        "3. Replace the example row with your real data before importing.",
        "4. Save the workbook as an Excel .xlsx file."
    };

        for (int i = 0; i < lines.Length; i++)
            instructions.Cell(i + 1, 1).Value = lines[i];

        instructions.Cell(1, 1).Style.Font.Bold = true;
        instructions.Column(1).Width = 100;

        wb.SaveAs(path);
    }

    public static void WriteEmployees(string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Employees");

        string[] headers = { "Name", "Role", "Max hours per week", "Unavailable days" };
        for (int i = 0; i < headers.Length; i++)
            ws.Cell(1, i + 1).Value = headers[i];
        ws.Row(1).Style.Font.Bold = true;

        ws.Column(1).Width = 26;
        ws.Column(2).Width = 18;
        ws.Column(3).Width = 20;
        ws.Column(4).Width = 24;

        AddInstructions(wb, new[]
        {
            "Employee import: how to fill the 'Employees' sheet",
            "",
            "Name: required. If the name already exists in the app, that employee is updated. Otherwise a new employee is added.",
            "Role: optional, for example Guard or Supervisor. Used for role requirements on shifts.",
            "Max hours per week: optional number from 1 to 168. New employees default to 48.",
            "Unavailable days: optional weekdays the person cannot work, for example: Sat, Sun",
            "",
            "Blank cells keep the current value for existing employees. To clear unavailable days, type none.",
            "Start typing from row 2 and delete any example rows you do not want imported."
        });

        wb.SaveAs(path);
    }

    public static void WriteLeave(string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Leave");

        string[] headers = { "Employee", "From date", "To date" };
        for (int i = 0; i < headers.Length; i++)
            ws.Cell(1, i + 1).Value = headers[i];
        ws.Row(1).Style.Font.Bold = true;

        ws.Column(1).Width = 26;
        ws.Column(2).Width = 16;
        ws.Column(3).Width = 16;
        ws.Column(2).Style.DateFormat.Format = "yyyy-mm-dd";
        ws.Column(3).Style.DateFormat.Format = "yyyy-mm-dd";

        AddInstructions(wb, new[]
        {
            "Leave import: how to fill the 'Leave' sheet",
            "",
            "Employee: the name exactly as in the app. Unknown names are skipped and reported.",
            "From date: first day of leave (a real Excel date, or text such as 2026-10-21).",
            "To date: last day of leave. Leave it empty for a single day.",
            "",
            "One row per leave period. Days that are already on leave are ignored."
        });

        wb.SaveAs(path);
    }

    public static void WriteShiftGrid(string path, AppData data, DateOnly weekStart)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Roster");

        ws.Cell(1, 1).Value = $"Shift roster template: week of {weekStart:dd MMM yyyy}";
        ws.Cell(1, 1).Style.Font.Bold = true;

        ws.Cell(3, 1).Value = "Employee";
        ws.Cell(3, 2).Value = "Role";
        for (int d = 0; d < 7; d++)
        {
            var cell = ws.Cell(3, 3 + d);
            cell.Value = weekStart.AddDays(d).ToDateTime(TimeOnly.MinValue);
            cell.Style.DateFormat.Format = "ddd dd mmm";
        }
        ws.Range(3, 1, 3, 9).Style.Font.Bold = true;

        for (int i = 0; i < data.Employees.Count; i++)
        {
            ws.Cell(4 + i, 1).Value = data.Employees[i].Name;
            ws.Cell(4 + i, 2).Value = data.Employees[i].Role;
        }

        ws.Column(1).Width = 26;
        ws.Column(2).Width = 16;
        for (int c = 3; c <= 9; c++) ws.Column(c).Width = 13;

        var lines = new List<string>
        {
            "Shift import: how to fill the 'Roster' sheet",
            "",
            "Type a shift code in each cell. Codes are not case sensitive:"
        };
        foreach (var t in data.ShiftTypes)
            lines.Add($"   {t.Code} = {t.Name} ({t.Start:hh\\:mm} to {t.End:hh\\:mm})");
        lines.Add("");
        lines.Add("Blank, Off or - clears that day. 'Leave' cells are ignored (manage leave on the Leave screen).");
        lines.Add("Keep the real dates in the header row. Employee names must match the app.");
        lines.Add("");
        lines.Add("Importing several weeks at once: use a list instead, with the columns Employee, Date and Shift (one row per shift).");
        AddInstructions(wb, lines.ToArray());

        wb.SaveAs(path);
    }

    private static void AddInstructions(XLWorkbook wb, string[] lines)
    {
        var ws = wb.AddWorksheet("Instructions");
        for (int i = 0; i < lines.Length; i++)
            ws.Cell(i + 1, 1).Value = lines[i];
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Column(1).Width = 110;
    }
}
