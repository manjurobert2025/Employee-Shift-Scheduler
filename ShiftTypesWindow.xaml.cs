using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace ShiftScheduler;

public sealed class ShiftRow
{
    public string? OriginalCode { get; set; }   // null for shifts added in this window
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Start { get; set; } = "08:00";
    public string End { get; set; } = "16:00";
    public int RequiredStaff { get; set; } = 1;
    public string Roles { get; set; } = "";      // text such as "Supervisor:1, Guard:2"
}

public partial class ShiftTypesWindow : Window
{
    private readonly AppData _data;
    private readonly ObservableCollection<ShiftRow> _rows;

    public ShiftTypesWindow(AppData data)
    {
        _data = data;
        InitializeComponent();

        _rows = new ObservableCollection<ShiftRow>(_data.ShiftTypes.Select(t => new ShiftRow
        {
            OriginalCode = t.Code,
            Code = t.Code,
            Name = t.Name,
            Start = FormatTime(t.Start),
            End = FormatTime(t.End),
            RequiredStaff = t.RequiredStaff,
            Roles = string.Join(", ", t.RoleRequirements.Select(r => $"{r.Role}:{r.MinCount}"))
        }));

        ShiftGrid.ItemsSource = _rows;
        RestBox.Text = _data.Rules.MinRestHours.ToString("0.##", CultureInfo.CurrentCulture);
    }

    private static string FormatTime(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}";

    private static bool TryParseTime(string text, out TimeSpan result)
    {
        result = default;
        if (TimeOnly.TryParseExact(text.Trim(), new[] { "H:mm", "HH:mm" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            result = t.ToTimeSpan();
            return true;
        }
        return false;
    }

    private static bool TryParseRoles(string text, out List<RoleRequirement> roles, out string error)
    {
        roles = new List<RoleRequirement>();
        error = "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int idx = part.LastIndexOf(':');
            var role = (idx < 0 ? part : part[..idx]).Trim();
            int count = 1;

            if (idx >= 0 && !int.TryParse(part[(idx + 1)..].Trim(), out count))
            {
                error = $"'{part}' is not valid. Use Role:Number, for example Supervisor:1.";
                return false;
            }
            if (role.Length == 0 || count < 1)
            {
                error = $"'{part}' is not valid. Use a role name and a number of 1 or more.";
                return false;
            }
            if (!seen.Add(role))
            {
                error = $"The role '{role}' is listed more than once.";
                return false;
            }

            roles.Add(new RoleRequirement { Role = role, MinCount = count });
        }
        return true;
    }

    private void AddShift_Click(object sender, RoutedEventArgs e)
    {
        var row = new ShiftRow { Name = "New shift" };
        _rows.Add(row);
        ShiftGrid.SelectedItem = row;
        ShiftGrid.ScrollIntoView(row);
    }

    private void DeleteShift_Click(object sender, RoutedEventArgs e)
    {
        if (ShiftGrid.SelectedItem is ShiftRow row)
            _rows.Remove(row);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ShiftGrid.CommitEdit(DataGridEditingUnit.Row, true);

        if (!double.TryParse(RestBox.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var rest)
            || rest < 0 || rest > 48)
        {
            MessageBox.Show("Minimum rest must be a number between 0 and 48.", "Shift types");
            return;
        }

        var newTypes = new List<ShiftType>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in _rows)
        {
            var code = (row.Code ?? "").Trim();
            if (code.Length == 0)
            {
                MessageBox.Show("Every shift needs a code (for example M, E or N).", "Shift types");
                return;
            }
            if (!seen.Add(code))
            {
                MessageBox.Show($"The code '{code}' is used more than once.", "Shift types");
                return;
            }
            if (code.Equals("Off", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("'Off' is reserved. Please use a different code.", "Shift types");
                return;
            }
            if (!TryParseTime(row.Start ?? "", out var start) || !TryParseTime(row.End ?? "", out var end))
            {
                MessageBox.Show($"Shift '{code}': use times like 06:00 or 22:30.", "Shift types");
                return;
            }
            if (start == end)
            {
                MessageBox.Show($"Shift '{code}': start and end times cannot be the same.", "Shift types");
                return;
            }
            if (row.RequiredStaff < 0)
            {
                MessageBox.Show($"Shift '{code}': required staff cannot be negative.", "Shift types");
                return;
            }

            if (!TryParseRoles(row.Roles ?? "", out var roles, out var roleError))
            {
                MessageBox.Show($"Shift '{code}': {roleError}", "Shift types");
                return;
            }

            row.Code = code;
            newTypes.Add(new ShiftType
            {
                Code = code,
                Name = (row.Name ?? "").Trim(),
                Start = start,
                End = end,
                RequiredStaff = row.RequiredStaff,
                RoleRequirements = roles
            });
        }

        // Old code -> new code, so renamed shifts keep their assignments.
        var map = _rows
            .Where(r => r.OriginalCode != null)
            .ToDictionary(r => r.OriginalCode!, r => r.Code);

        // Assignments whose shift was deleted
        int orphaned = _data.Assignments.Count(a => !map.ContainsKey(a.ShiftCode));
        if (orphaned > 0)
        {
            var answer = MessageBox.Show(
                $"{orphaned} scheduled shift(s) use a shift type you removed and will be cleared. Continue?",
                "Shift types", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        _data.Assignments.RemoveAll(a => !map.ContainsKey(a.ShiftCode));
        foreach (var a in _data.Assignments)
            a.ShiftCode = map[a.ShiftCode];

        _data.ShiftTypes.Clear();
        _data.ShiftTypes.AddRange(newTypes);
        _data.Rules.MinRestHours = rest;

        DialogResult = true;
    }
}
