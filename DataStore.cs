using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Data.Sqlite;

namespace ShiftScheduler;

/// <summary>
/// Stores all data in a SQLite database file:
/// %AppData%\ShiftScheduler\shiftscheduler.db
/// The rest of the app still loads and saves one AppData object, so the screens did not change.
/// Every save runs in a single transaction, so a crash mid-save cannot leave half-written data.
/// </summary>
public static class DataStore
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShiftScheduler");

    private static readonly string DbPath = Path.Combine(Folder, "shiftscheduler.db");
    private static readonly string OldJsonPath = Path.Combine(Folder, "data.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string ConnectionString =>
        new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString();

    // ---------- Public API (same as before) ----------

    public static AppData Load()
    {
        try
        {
            Directory.CreateDirectory(Folder);

            using var conn = Open();
            EnsureSchema(conn);

            if (GetSetting(conn, "initialized") != "1")
            {
                // First run with SQLite: import the old data.json if there is one, otherwise sample data.
                var initial = TryLoadOldJson() ?? Seed();
                WriteAll(conn, initial);
                RenameOldJson();
                return initial;
            }

            return ReadAll(conn);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not open the database:\n{DbPath}\n\n{ex.Message}\n\nThe app will start with sample data and may not be able to save.",
                "Shift Scheduler");
            return Seed();
        }
    }

    public static void Save(AppData data)
    {
        try
        {
            using var conn = Open();
            EnsureSchema(conn);
            WriteAll(conn, data);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Your last change could not be saved.\n\n{ex.Message}",
                "Shift Scheduler");
        }
    }

    // ---------- Database setup ----------

    private static SqliteConnection Open()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        Exec(conn, @"
CREATE TABLE IF NOT EXISTS settings (
    setting_key   TEXT PRIMARY KEY,
    setting_value TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS employees (
    id               TEXT PRIMARY KEY,
    name             TEXT NOT NULL,
    role             TEXT NOT NULL,
    max_weekly_hours REAL NOT NULL,
    unavailable_days TEXT NOT NULL,
    sort_order       INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS shift_types (
    code              TEXT PRIMARY KEY,
    name              TEXT NOT NULL,
    start_time        TEXT NOT NULL,
    end_time          TEXT NOT NULL,
    required_staff    INTEGER NOT NULL,
    role_requirements TEXT NOT NULL,
    sort_order        INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS assignments (
    employee_id TEXT NOT NULL,
    work_date   TEXT NOT NULL,
    shift_code  TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_assignments_date ON assignments(work_date);
CREATE TABLE IF NOT EXISTS leave_days (
    employee_id TEXT NOT NULL,
    leave_date  TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_leave_date ON leave_days(leave_date);
PRAGMA user_version = 1;");
    }

    // ---------- Writing ----------

    private static void WriteAll(SqliteConnection conn, AppData data)
    {
        using var tx = conn.BeginTransaction();

        Exec(conn, "DELETE FROM employees; DELETE FROM shift_types; DELETE FROM assignments; DELETE FROM leave_days;", tx);

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO employees (id, name, role, max_weekly_hours, unavailable_days, sort_order) " +
                "VALUES ($id, $name, $role, $max, $days, $sort)";
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);
            var pRole = cmd.Parameters.Add("$role", SqliteType.Text);
            var pMax = cmd.Parameters.Add("$max", SqliteType.Real);
            var pDays = cmd.Parameters.Add("$days", SqliteType.Text);
            var pSort = cmd.Parameters.Add("$sort", SqliteType.Integer);

            for (int i = 0; i < data.Employees.Count; i++)
            {
                var e = data.Employees[i];
                pId.Value = e.Id.ToString();
                pName.Value = e.Name ?? "";
                pRole.Value = e.Role ?? "";
                pMax.Value = e.MaxWeeklyHours;
                pDays.Value = JsonSerializer.Serialize(e.UnavailableDays ?? new List<DayOfWeek>());
                pSort.Value = i;
                cmd.ExecuteNonQuery();
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO shift_types (code, name, start_time, end_time, required_staff, role_requirements, sort_order) " +
                "VALUES ($code, $name, $start, $end, $req, $roles, $sort)";
            var pCode = cmd.Parameters.Add("$code", SqliteType.Text);
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);
            var pStart = cmd.Parameters.Add("$start", SqliteType.Text);
            var pEnd = cmd.Parameters.Add("$end", SqliteType.Text);
            var pReq = cmd.Parameters.Add("$req", SqliteType.Integer);
            var pRoles = cmd.Parameters.Add("$roles", SqliteType.Text);
            var pSort = cmd.Parameters.Add("$sort", SqliteType.Integer);

            for (int i = 0; i < data.ShiftTypes.Count; i++)
            {
                var t = data.ShiftTypes[i];
                pCode.Value = t.Code;
                pName.Value = t.Name ?? "";
                pStart.Value = t.Start.ToString("c", CultureInfo.InvariantCulture);
                pEnd.Value = t.End.ToString("c", CultureInfo.InvariantCulture);
                pReq.Value = t.RequiredStaff;
                pRoles.Value = JsonSerializer.Serialize(t.RoleRequirements ?? new List<RoleRequirement>());
                pSort.Value = i;
                cmd.ExecuteNonQuery();
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO assignments (employee_id, work_date, shift_code) VALUES ($emp, $date, $code)";
            var pEmp = cmd.Parameters.Add("$emp", SqliteType.Text);
            var pDate = cmd.Parameters.Add("$date", SqliteType.Text);
            var pCode = cmd.Parameters.Add("$code", SqliteType.Text);

            foreach (var a in data.Assignments)
            {
                pEmp.Value = a.EmployeeId.ToString();
                pDate.Value = FormatDate(a.Date);
                pCode.Value = a.ShiftCode;
                cmd.ExecuteNonQuery();
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO leave_days (employee_id, leave_date) VALUES ($emp, $date)";
            var pEmp = cmd.Parameters.Add("$emp", SqliteType.Text);
            var pDate = cmd.Parameters.Add("$date", SqliteType.Text);

            foreach (var l in data.Leave)
            {
                pEmp.Value = l.EmployeeId.ToString();
                pDate.Value = FormatDate(l.Date);
                cmd.ExecuteNonQuery();
            }
        }

        SetSetting(conn, tx, "min_rest_hours", data.Rules.MinRestHours.ToString(CultureInfo.InvariantCulture));
        SetSetting(conn, tx, "initialized", "1");

        tx.Commit();
    }

    // ---------- Reading ----------

    private static AppData ReadAll(SqliteConnection conn)
    {
        var data = new AppData();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT id, name, role, max_weekly_hours, unavailable_days FROM employees ORDER BY sort_order";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                data.Employees.Add(new Employee
                {
                    Id = Guid.Parse(r.GetString(0)),
                    Name = r.GetString(1),
                    Role = r.GetString(2),
                    MaxWeeklyHours = r.GetDouble(3),
                    UnavailableDays = JsonSerializer.Deserialize<List<DayOfWeek>>(r.GetString(4)) ?? new List<DayOfWeek>()
                });
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT code, name, start_time, end_time, required_staff, role_requirements FROM shift_types ORDER BY sort_order";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                data.ShiftTypes.Add(new ShiftType
                {
                    Code = r.GetString(0),
                    Name = r.GetString(1),
                    Start = TimeSpan.Parse(r.GetString(2), CultureInfo.InvariantCulture),
                    End = TimeSpan.Parse(r.GetString(3), CultureInfo.InvariantCulture),
                    RequiredStaff = r.GetInt32(4),
                    RoleRequirements = JsonSerializer.Deserialize<List<RoleRequirement>>(r.GetString(5)) ?? new List<RoleRequirement>()
                });
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT employee_id, work_date, shift_code FROM assignments";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                data.Assignments.Add(new Assignment
                {
                    EmployeeId = Guid.Parse(r.GetString(0)),
                    Date = ParseDate(r.GetString(1)),
                    ShiftCode = r.GetString(2)
                });
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT employee_id, leave_date FROM leave_days";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                data.Leave.Add(new LeaveEntry
                {
                    EmployeeId = Guid.Parse(r.GetString(0)),
                    Date = ParseDate(r.GetString(1))
                });
            }
        }

        if (double.TryParse(GetSetting(conn, "min_rest_hours"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rest))
            data.Rules.MinRestHours = rest;

        return data;
    }

    // ---------- Helpers ----------

    private static string FormatDate(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly ParseDate(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void Exec(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    private static string? GetSetting(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT setting_value FROM settings WHERE setting_key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void SetSetting(SqliteConnection conn, SqliteTransaction tx, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO settings (setting_key, setting_value) VALUES ($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    // ---------- Import from the old JSON file ----------

    private static AppData? TryLoadOldJson()
    {
        try
        {
            if (!File.Exists(OldJsonPath)) return null;
            return JsonSerializer.Deserialize<AppData>(File.ReadAllText(OldJsonPath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void RenameOldJson()
    {
        try
        {
            if (File.Exists(OldJsonPath))
                File.Move(OldJsonPath, OldJsonPath + ".imported", overwrite: true);
        }
        catch
        {
            // Not important: the data is already in the database.
        }
    }

    // ---------- Sample data for a brand-new install ----------

    private static AppData Seed() => new()
    {
        Employees =
        {
            new Employee { Name = "Anil", Role = "Guard" },
            new Employee { Name = "Meera", Role = "Guard" },
            new Employee { Name = "Rahul", Role = "Supervisor" },
        },
        ShiftTypes =
        {
            new ShiftType { Code = "M", Name = "Morning", Start = TimeSpan.FromHours(6),  End = TimeSpan.FromHours(14), RequiredStaff = 1 },
            new ShiftType { Code = "E", Name = "Evening", Start = TimeSpan.FromHours(14), End = TimeSpan.FromHours(22), RequiredStaff = 1 },
            new ShiftType { Code = "N", Name = "Night",   Start = TimeSpan.FromHours(22), End = TimeSpan.FromHours(6),  RequiredStaff = 1 },
        }
    };
}
