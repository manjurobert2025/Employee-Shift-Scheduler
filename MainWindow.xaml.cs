using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ShiftScheduler;

public partial class MainWindow : Window
{
    private sealed record CellTag(Employee Employee, DateOnly Date);

    private const int MaxUndo = 50;

    private readonly AppData _data = DataStore.Load();
    private readonly List<List<Assignment>> _undo = new();
    private readonly Dictionary<Guid, TextBlock> _hoursLabels = new();
    private DateOnly _weekStart = StartOfWeek(DateOnly.FromDateTime(DateTime.Today));
    private bool _building;

    public MainWindow()
    {
        InitializeComponent();
        BuildGrid();
    }

    private static DateOnly StartOfWeek(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)); // Monday

    // ---------- Roster grid ----------

    private void BuildGrid()
    {
        _building = true;
        _hoursLabels.Clear();
        RosterGrid.Children.Clear();
        RosterGrid.RowDefinitions.Clear();
        RosterGrid.ColumnDefinitions.Clear();

        WeekLabel.Text = $"Week of {_weekStart:dd MMM yyyy}";
        UndoButton.IsEnabled = _undo.Count > 0;

        RosterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        for (int d = 0; d < 7; d++)
            RosterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        RosterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

        RosterGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int d = 0; d < 7; d++)
            Place(new TextBlock
            {
                Text = _weekStart.AddDays(d).ToString("ddd dd MMM"),
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(4)
            }, 0, d + 1);
        Place(new TextBlock { Text = "Hours / limit", FontWeight = FontWeights.Bold, Margin = new Thickness(8, 4, 4, 4) }, 0, 8);

        var options = new[] { "Off" }.Concat(_data.ShiftTypes.Select(t => t.Code)).ToList();

        for (int i = 0; i < _data.Employees.Count; i++)
        {
            var emp = _data.Employees[i];
            int row = i + 1;
            RosterGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = string.IsNullOrWhiteSpace(emp.Role) ? emp.Name : $"{emp.Name} ({emp.Role})";
            Place(new TextBlock { Text = label, Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center }, row, 0);

            for (int d = 0; d < 7; d++)
            {
                var date = _weekStart.AddDays(d);
                var tag = new CellTag(emp, date);
                var current = _data.Assignments.FirstOrDefault(a => a.EmployeeId == emp.Id && a.Date == date);

                var combo = new ComboBox { ItemsSource = options, Tag = tag, Margin = new Thickness(2) };
                combo.SelectedItem = current != null && options.Contains(current.ShiftCode) ? current.ShiftCode : "Off";
                combo.SelectionChanged += OnShiftChanged;

                var border = new Border { Child = combo, Tag = tag, Padding = new Thickness(2), Margin = new Thickness(1) };
                Place(border, row, d + 1);
            }

            var hoursLabel = new TextBlock { Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            _hoursLabels[emp.Id] = hoursLabel;
            Place(hoursLabel, row, 8);
        }

        _building = false;
        RefreshConflicts();
    }

    private void Place(UIElement element, int row, int col)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, col);
        RosterGrid.Children.Add(element);
    }

    private void OnShiftChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building) return;
        var combo = (ComboBox)sender;
        var tag = (CellTag)combo.Tag;

        PushUndo();

        _data.Assignments.RemoveAll(a => a.EmployeeId == tag.Employee.Id && a.Date == tag.Date);
        if (combo.SelectedItem is string code && code != "Off")
            _data.Assignments.Add(new Assignment { EmployeeId = tag.Employee.Id, Date = tag.Date, ShiftCode = code });

        DataStore.Save(_data);
        RefreshConflicts();
    }

    private void RefreshConflicts()
    {
        foreach (var emp in _data.Employees)
        {
            if (!_hoursLabels.TryGetValue(emp.Id, out var label)) continue;
            var hours = ScheduleMath.WeeklyHours(_data, emp, _weekStart);
            label.Text = $"{hours:0.#}h / {emp.MaxWeeklyHours:0.#}h";
            label.Foreground = hours > emp.MaxWeeklyHours ? Brushes.Firebrick : Brushes.Black;
        }

        var conflicts = ConflictChecker.Check(_data, _weekStart);
        ConflictList.ItemsSource = conflicts.Select(c => c.Message).ToList();

        foreach (var border in RosterGrid.Children.OfType<Border>())
        {
            var tag = (CellTag)border.Tag;
            bool bad = conflicts.Any(c => c.EmployeeId == tag.Employee.Id && c.Date == tag.Date);
            bool onLeave = _data.Leave.Any(l => l.EmployeeId == tag.Employee.Id && l.Date == tag.Date);
            bool unavailable = tag.Employee.UnavailableDays.Contains(tag.Date.DayOfWeek);
            border.Background = bad ? Brushes.MistyRose
                : onLeave ? Brushes.LightBlue
                : unavailable ? Brushes.LightGray
                : Brushes.Transparent;
            border.ToolTip = onLeave ? "On leave" : unavailable ? "Not available on this weekday" : null;
        }
    }

    // ---------- Undo and clear week ----------

    private void PushUndo()
    {
        _undo.Add(_data.Assignments
            .Select(a => new Assignment { EmployeeId = a.EmployeeId, Date = a.Date, ShiftCode = a.ShiftCode })
            .ToList());
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        UndoButton.IsEnabled = true;
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;

        var last = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        _data.Assignments.Clear();
        _data.Assignments.AddRange(last);

        DataStore.Save(_data);
        BuildGrid();
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e) => Undo();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control
            && Keyboard.FocusedElement is not TextBox)
        {
            Undo();
            e.Handled = true;
        }
    }

    private void ClearWeek_Click(object sender, RoutedEventArgs e)
    {
        var weekEnd = _weekStart.AddDays(6);
        int count = _data.Assignments.Count(a => a.Date >= _weekStart && a.Date <= weekEnd);

        if (count == 0)
        {
            MessageBox.Show("There are no scheduled shifts in this week.", "Clear week");
            return;
        }

        var answer = MessageBox.Show(
            $"Clear all {count} scheduled shift(s) in the week of {_weekStart:dd MMM yyyy}?\n\nYou can bring them back with Undo.",
            "Clear week", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        PushUndo();
        _data.Assignments.RemoveAll(a => a.Date >= _weekStart && a.Date <= weekEnd);
        DataStore.Save(_data);
        BuildGrid();
    }

    // ---------- Copy last week, print and export ----------

    private void CopyLastWeek_Click(object sender, RoutedEventArgs e)
    {
        var prevStart = _weekStart.AddDays(-7);
        var prevEnd = _weekStart.AddDays(-1);
        var source = _data.Assignments.Where(a => a.Date >= prevStart && a.Date <= prevEnd).ToList();

        if (source.Count == 0)
        {
            MessageBox.Show("Last week has no scheduled shifts to copy.", "Copy last week");
            return;
        }

        var weekEnd = _weekStart.AddDays(6);
        int existing = _data.Assignments.Count(a => a.Date >= _weekStart && a.Date <= weekEnd);
        if (existing > 0)
        {
            var answer = MessageBox.Show(
                $"This week already has {existing} scheduled shift(s). Replace them with last week's schedule?\n\nYou can bring them back with Undo.",
                "Copy last week", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }

        PushUndo();
        _data.Assignments.RemoveAll(a => a.Date >= _weekStart && a.Date <= weekEnd);

        int copied = 0, skipped = 0;
        foreach (var a in source)
        {
            var newDate = a.Date.AddDays(7);
            if (_data.Leave.Any(l => l.EmployeeId == a.EmployeeId && l.Date == newDate))
            {
                skipped++;
                continue;
            }
            _data.Assignments.Add(new Assignment { EmployeeId = a.EmployeeId, Date = newDate, ShiftCode = a.ShiftCode });
            copied++;
        }

        DataStore.Save(_data);
        BuildGrid();

        if (skipped > 0)
            MessageBox.Show($"Copied {copied} shift(s). {skipped} shift(s) were skipped because the employee is on leave that day.",
                "Copy last week");
    }

    private void Print_Click(object sender, RoutedEventArgs e) =>
        PrintService.Print(this, RosterTable.Build(_data, _weekStart), toPdf: false);

    private void ExportPdf_Click(object sender, RoutedEventArgs e) =>
        PrintService.Print(this, RosterTable.Build(_data, _weekStart), toPdf: true);

    private void ExportExcel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = $"Roster_{_weekStart:yyyy-MM-dd}.xlsx"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            XlsxWriter.Write(dialog.FileName, RosterTable.Build(_data, _weekStart));
            MessageBox.Show($"Saved to:\n{dialog.FileName}", "Export Excel");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not save the file. If it is open in Excel, close it and try again.\n\n{ex.Message}",
                "Export Excel");
        }
    }

    // ---------- Navigation and other windows ----------

    private void PrevButton_Click(object sender, RoutedEventArgs e) { _weekStart = _weekStart.AddDays(-7); BuildGrid(); }
    private void NextButton_Click(object sender, RoutedEventArgs e) { _weekStart = _weekStart.AddDays(7); BuildGrid(); }

    private void AddEmployee_Click(object sender, RoutedEventArgs e)
    {
        var name = NewEmployeeBox.Text.Trim();
        if (name.Length == 0) return;
        _data.Employees.Add(new Employee { Name = name });
        NewEmployeeBox.Clear();
        DataStore.Save(_data);
        BuildGrid();
    }

    private void EmployeesButton_Click(object sender, RoutedEventArgs e)
    {
        if (new EmployeesWindow(_data) { Owner = this }.ShowDialog() == true)
        {
            _undo.Clear(); // old snapshots may refer to deleted employees
            DataStore.Save(_data);
            BuildGrid();
        }
    }

    private void LeaveButton_Click(object sender, RoutedEventArgs e)
    {
        new LeaveWindow(_data) { Owner = this }.ShowDialog();
        DataStore.Save(_data);
        BuildGrid();
    }

    private void ShiftTypesButton_Click(object sender, RoutedEventArgs e)
    {
        if (new ShiftTypesWindow(_data) { Owner = this }.ShowDialog() == true)
        {
            _undo.Clear(); // old snapshots may use shift codes that no longer exist
            DataStore.Save(_data);
            BuildGrid();
        }
    }
}
