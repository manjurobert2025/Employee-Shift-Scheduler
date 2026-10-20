using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace ShiftScheduler;

public sealed class EmployeeRow
{
    public Guid? Id { get; set; }               // null for employees added in this window
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public double MaxWeeklyHours { get; set; } = 48;

    // Tick = cannot work that weekday
    public bool UnMon { get; set; }
    public bool UnTue { get; set; }
    public bool UnWed { get; set; }
    public bool UnThu { get; set; }
    public bool UnFri { get; set; }
    public bool UnSat { get; set; }
    public bool UnSun { get; set; }

    public void SetUnavailable(IEnumerable<DayOfWeek> days)
    {
        var set = days.ToHashSet();
        UnMon = set.Contains(DayOfWeek.Monday);
        UnTue = set.Contains(DayOfWeek.Tuesday);
        UnWed = set.Contains(DayOfWeek.Wednesday);
        UnThu = set.Contains(DayOfWeek.Thursday);
        UnFri = set.Contains(DayOfWeek.Friday);
        UnSat = set.Contains(DayOfWeek.Saturday);
        UnSun = set.Contains(DayOfWeek.Sunday);
    }

    public List<DayOfWeek> GetUnavailable()
    {
        var list = new List<DayOfWeek>();
        if (UnMon) list.Add(DayOfWeek.Monday);
        if (UnTue) list.Add(DayOfWeek.Tuesday);
        if (UnWed) list.Add(DayOfWeek.Wednesday);
        if (UnThu) list.Add(DayOfWeek.Thursday);
        if (UnFri) list.Add(DayOfWeek.Friday);
        if (UnSat) list.Add(DayOfWeek.Saturday);
        if (UnSun) list.Add(DayOfWeek.Sunday);
        return list;
    }
}

public partial class EmployeesWindow : Window
{
    private readonly AppData _data;
    private readonly ObservableCollection<EmployeeRow> _rows;

    public EmployeesWindow(AppData data)
    {
        _data = data;
        InitializeComponent();

        _rows = new ObservableCollection<EmployeeRow>(_data.Employees.Select(e =>
        {
            var row = new EmployeeRow
            {
                Id = e.Id,
                Name = e.Name,
                Role = e.Role,
                MaxWeeklyHours = e.MaxWeeklyHours
            };
            row.SetUnavailable(e.UnavailableDays);
            return row;
        }));

        EmployeeGrid.ItemsSource = _rows;
    }

    private void AddEmployee_Click(object sender, RoutedEventArgs e)
    {
        var row = new EmployeeRow { Name = "New employee" };
        _rows.Add(row);
        EmployeeGrid.SelectedItem = row;
        EmployeeGrid.ScrollIntoView(row);
    }

    private void DeleteEmployee_Click(object sender, RoutedEventArgs e)
    {
        if (EmployeeGrid.SelectedItem is EmployeeRow row)
            _rows.Remove(row);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        EmployeeGrid.CommitEdit(DataGridEditingUnit.Row, true);

        // 1. Validate everything before changing any data
        var cleaned = new List<(EmployeeRow Row, string Name, string Role)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in _rows)
        {
            var name = (row.Name ?? "").Trim();
            if (name.Length == 0)
            {
                MessageBox.Show("Every employee needs a name.", "Employees");
                return;
            }
            if (!names.Add(name))
            {
                MessageBox.Show($"The name '{name}' is used more than once. Please make the names different.", "Employees");
                return;
            }
            if (row.MaxWeeklyHours <= 0 || row.MaxWeeklyHours > 168)
            {
                MessageBox.Show($"{name}: max hours per week must be between 1 and 168.", "Employees");
                return;
            }
            cleaned.Add((row, name, (row.Role ?? "").Trim()));
        }

        // 2. Work out who was deleted and confirm
        var keptIds = _rows.Where(r => r.Id != null).Select(r => r.Id!.Value).ToHashSet();
        var removedIds = _data.Employees.Where(e => !keptIds.Contains(e.Id)).Select(e => e.Id).ToHashSet();

        if (removedIds.Count > 0)
        {
            int shifts = _data.Assignments.Count(a => removedIds.Contains(a.EmployeeId));
            int leave = _data.Leave.Count(l => removedIds.Contains(l.EmployeeId));
            var answer = MessageBox.Show(
                $"Delete {removedIds.Count} employee(s)?\n\nTheir {shifts} scheduled shift(s) and {leave} leave day(s) will also be removed.",
                "Employees", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        // 3. Apply
        _data.Assignments.RemoveAll(a => removedIds.Contains(a.EmployeeId));
        _data.Leave.RemoveAll(l => removedIds.Contains(l.EmployeeId));

        var updated = new List<Employee>();
        foreach (var (row, name, role) in cleaned)
        {
            var emp = row.Id != null
                ? _data.Employees.First(e => e.Id == row.Id)
                : new Employee();

            emp.Name = name;
            emp.Role = role;
            emp.MaxWeeklyHours = row.MaxWeeklyHours;
            emp.UnavailableDays = row.GetUnavailable();
            updated.Add(emp);
        }

        _data.Employees.Clear();
        _data.Employees.AddRange(updated);

        DialogResult = true;
    }
}
