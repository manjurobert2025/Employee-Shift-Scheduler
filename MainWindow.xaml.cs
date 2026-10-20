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

    // ---------- Excel import and templates ----------


    private void AutoSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (_data.Employees.Count == 0 || _data.ShiftTypes.Count == 0)
        {
            MessageBox.Show(
                "Please add employees and configure shift types first.",
                "Auto Schedule");
            return;
        }

        var weekEnd = _weekStart.AddDays(6);

        var existing = _data.Assignments
            .Where(a => a.Date >= _weekStart && a.Date <= weekEnd)
            .ToList();

        if (existing.Count > 0)
        {
            var answer = MessageBox.Show(
                $"This week contains {existing.Count} assignment(s).\n\n" +
                "Generate a new roster and replace this week's assignments?",
                "Auto Schedule",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
                return;
        }

        var proposed = new List<Assignment>();
        var shortages = new List<string>();

        var shiftTypes = _data.ShiftTypes
            .Where(t => !string.IsNullOrWhiteSpace(t.Code))
            .ToList();

        var employees = _data.Employees.ToList();

        // Existing assignments outside this week remain in place.
        var otherAssignments = _data.Assignments
            .Where(a => a.Date < _weekStart || a.Date > weekEnd)
            .ToList();

        bool CanAssign(Employee emp, DateOnly date, ShiftType shift)
        {
            if (emp.UnavailableDays.Contains(date.DayOfWeek))
                return false;

            if (_data.Leave.Any(l =>
                l.EmployeeId == emp.Id && l.Date == date))
                return false;

            // Conservatively allow at most one shift per employee per day.
            if (proposed.Any(a =>
                a.EmployeeId == emp.Id && a.Date == date))
                return false;

            var currentHours = otherAssignments
                .Where(a => a.EmployeeId == emp.Id &&
                            a.Date >= _weekStart &&
                            a.Date <= weekEnd)
                .Concat(proposed.Where(a => a.EmployeeId == emp.Id))
                .Sum(a =>
                {
                    var existingType = shiftTypes.FirstOrDefault(
                        t => string.Equals(
                            t.Code, a.ShiftCode,
                            StringComparison.OrdinalIgnoreCase));

                    if (existingType == null)
                        existingType = _data.ShiftTypes.FirstOrDefault(
                            t => string.Equals(
                                t.Code, a.ShiftCode,
                                StringComparison.OrdinalIgnoreCase));

                    return existingType?.Duration.TotalHours ?? 0;
                });

            if (currentHours + shift.Duration.TotalHours >
                emp.MaxWeeklyHours)
                return false;

            DateTime newStart =
                date.ToDateTime(TimeOnly.FromTimeSpan(shift.Start));
            DateTime newEnd = newStart + shift.Duration;

            var employeeAssignments = otherAssignments
                .Concat(proposed.Where(a => a.EmployeeId == emp.Id))
                .Where(a => a.EmployeeId == emp.Id)
                .ToList();

            foreach (var a in employeeAssignments)
            {
                var otherShift = _data.ShiftTypes.FirstOrDefault(
                    t => string.Equals(
                        t.Code, a.ShiftCode,
                        StringComparison.OrdinalIgnoreCase));

                if (otherShift == null)
                    continue;

                DateTime otherStart =
                    a.Date.ToDateTime(
                        TimeOnly.FromTimeSpan(otherShift.Start));

                DateTime otherEnd = otherStart + otherShift.Duration;

                // Reject overlapping shifts and insufficient rest.
                if (newStart < otherEnd &&
                    otherStart < newEnd)
                    return false;

                if (otherEnd <= newStart &&
                    (newStart - otherEnd).TotalHours <
                    _data.Rules.MinRestHours)
                    return false;

                if (newEnd <= otherStart &&
                    (otherStart - newEnd).TotalHours <
                    _data.Rules.MinRestHours)
                    return false;
            }

            return true;
        }

        Employee? FindCandidate(
            DateOnly date,
            ShiftType shift,
            string? requiredRole)
        {
            return employees
                .Where(emp =>
                    string.IsNullOrWhiteSpace(requiredRole) ||
                    string.Equals(
                        emp.Role.Trim(),
                        requiredRole.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                .Where(emp => CanAssign(emp, date, shift))
                .OrderBy(emp =>
                    otherAssignments
                        .Concat(proposed.Where(a => a.EmployeeId == emp.Id))
                        .Where(a =>
                            a.EmployeeId == emp.Id &&
                            a.Date >= _weekStart &&
                            a.Date <= weekEnd)
                        .Sum(a =>
                        {
                            var t = _data.ShiftTypes.FirstOrDefault(
                                x => string.Equals(
                                    x.Code, a.ShiftCode,
                                    StringComparison.OrdinalIgnoreCase));

                            return t?.Duration.TotalHours ?? 0;
                        }))
                .ThenBy(emp => proposed.Count(a => a.EmployeeId == emp.Id))
                .FirstOrDefault();
        }

        void AddAssignment(
            Employee emp,
            DateOnly date,
            ShiftType shift)
        {
            proposed.Add(new Assignment
            {
                EmployeeId = emp.Id,
                Date = date,
                ShiftCode = shift.Code
            });
        }

        // Fill each shift on each day.
        for (int day = 0; day < 7; day++)
        {
            var date = _weekStart.AddDays(day);

            foreach (var shift in shiftTypes)
            {
                int assigned = 0;

                // Assign employees to required roles first.
                foreach (var requirement in shift.RoleRequirements)
                {
                    int haveRole = proposed.Count(a =>
                        a.Date == date &&
                        string.Equals(
                            a.ShiftCode, shift.Code,
                            StringComparison.OrdinalIgnoreCase) &&
                        employees.Any(emp =>
                            emp.Id == a.EmployeeId &&
                            string.Equals(
                                emp.Role.Trim(),
                                requirement.Role.Trim(),
                                StringComparison.OrdinalIgnoreCase)));

                    while (haveRole < requirement.MinCount)
                    {
                        var candidate = FindCandidate(
                            date, shift, requirement.Role);

                        if (candidate == null)
                        {
                            shortages.Add(
                                $"{date:ddd dd MMM} — {shift.Name}: " +
                                $"missing {requirement.Role}");
                            break;
                        }

                        AddAssignment(candidate, date, shift);
                        assigned++;
                        haveRole++;
                    }
                }

                // Then fill the total staffing requirement.
                int total = proposed.Count(a =>
                    a.Date == date &&
                    string.Equals(
                        a.ShiftCode, shift.Code,
                        StringComparison.OrdinalIgnoreCase));

                while (total < shift.RequiredStaff)
                {
                    var candidate = FindCandidate(date, shift, null);

                    if (candidate == null)
                    {
                        shortages.Add(
                            $"{date:ddd dd MMM} — {shift.Name}: " +
                            $"staffing {total}/{shift.RequiredStaff}");
                        break;
                    }

                    AddAssignment(candidate, date, shift);
                    total++;
                    assigned++;
                }
            }
        }

        if (proposed.Count == 0)
        {
            MessageBox.Show(
                "No eligible assignments could be generated.\n\n" +
                "Check employee availability, leave, weekly hour limits, " +
                "shift settings, and minimum rest.",
                "Auto Schedule");
            return;
        }

        var summary = new System.Text.StringBuilder();
        summary.AppendLine(
            $"Proposed assignments: {proposed.Count}");
        summary.AppendLine(
            $"Week: {_weekStart:dd MMM yyyy} – {weekEnd:dd MMM yyyy}");

        if (shortages.Count > 0)
        {
            summary.AppendLine();
            summary.AppendLine(
                $"Unfilled staffing/role requirement(s): {shortages.Count}");

            foreach (var item in shortages.Distinct().Take(15))
                summary.AppendLine("• " + item);

            if (shortages.Distinct().Count() > 15)
                summary.AppendLine("• Additional shortages omitted.");
        }
        else
        {
            summary.AppendLine();
            summary.AppendLine(
                "All configured staffing and role requirements were filled.");
        }

        summary.AppendLine();
        summary.AppendLine(
            "Apply this proposed roster? The existing week's assignments " +
            "will be replaced.");

        if (MessageBox.Show(
                summary.ToString(),
                "Review automatic roster",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        PushUndo();

        _data.Assignments.RemoveAll(a =>
            a.Date >= _weekStart && a.Date <= weekEnd);

        _data.Assignments.AddRange(proposed);

        DataStore.Save(_data);
        BuildGrid();

        MessageBox.Show(
            $"Roster generated with {proposed.Count} assignment(s).\n" +
            $"Unfilled requirement(s): {shortages.Distinct().Count()}\n\n" +
            "Review the Conflicts panel before using the roster.",
            "Auto Schedule");
    }


    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu != null)
        {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.IsOpen = true;
        }
    }


    private void ImportCombined_Click(object sender, RoutedEventArgs e)
    {
        RunImport(
            "Import combined employee and leave workbook",
            path => ExcelImport.PlanCombined(_data, path),
            pushUndo: false);
    }

    private void CombinedTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save combined Excel template",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = "CorporateShiftScheduler_CombinedTemplate.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            ExcelTemplates.WriteCombinedTemplate(dialog.FileName);

            MessageBox.Show(
                "Combined Excel template saved successfully.",
                "Template Download",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not save the template:\n{ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ImportEmployees_Click(object sender, RoutedEventArgs e) =>
        RunImport("Import employees", path => ExcelImport.PlanEmployees(_data, path), pushUndo: false);

    private void ImportLeave_Click(object sender, RoutedEventArgs e) =>
        RunImport("Import leave", path => ExcelImport.PlanLeave(_data, path), pushUndo: false);

    private void ImportShifts_Click(object sender, RoutedEventArgs e) =>
        RunImport("Import shifts", path => ExcelImport.PlanShifts(_data, path), pushUndo: true);

    private void RunImport(string title, Func<string, ImportPlan> makePlan, bool pushUndo)
    {
        var dialog = new OpenFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", Title = title };
        if (dialog.ShowDialog(this) != true) return;

        ImportPlan plan;
        try
        {
            plan = makePlan(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read the file.\n\n{ex.Message}", title);
            return;
        }

        if (!ConfirmImport(title, plan)) return;

        if (pushUndo) PushUndo();
        plan.Apply();
        if (plan.FocusDate is DateOnly focus) _weekStart = StartOfWeek(focus);

        DataStore.Save(_data);
        BuildGrid();
    }

    private static bool ConfirmImport(string title, ImportPlan plan)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in plan.Summary) sb.AppendLine(line);

        if (plan.Problems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{plan.Problems.Count} problem(s), these rows or cells will be skipped:");
            foreach (var problem in plan.Problems.Take(10)) sb.AppendLine("  - " + problem);
            if (plan.Problems.Count > 10) sb.AppendLine($"  ...and {plan.Problems.Count - 10} more");
        }

        if (!plan.HasChanges)
        {
            sb.AppendLine();
            sb.Append("Nothing to import.");
            MessageBox.Show(sb.ToString(), title);
            return false;
        }

        sb.AppendLine();
        sb.Append("Apply these changes?");
        return MessageBox.Show(sb.ToString(), title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private void EmployeeTemplate_Click(object sender, RoutedEventArgs e) =>
        SaveTemplate("Employee template", "Employees_template.xlsx", ExcelTemplates.WriteEmployees);

    private void LeaveTemplate_Click(object sender, RoutedEventArgs e) =>
        SaveTemplate("Leave template", "Leave_template.xlsx", ExcelTemplates.WriteLeave);

    private void ShiftTemplate_Click(object sender, RoutedEventArgs e) =>
        SaveTemplate("Shift template", $"Shifts_template_{_weekStart:yyyy-MM-dd}.xlsx",
            path => ExcelTemplates.WriteShiftGrid(path, _data, _weekStart));

    private void SaveTemplate(string title, string fileName, Action<string> write)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = fileName,
            Title = title
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            write(dialog.FileName);
            MessageBox.Show($"Saved to:\n{dialog.FileName}", title);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not save the file. If it is open in Excel, close it and try again.\n\n{ex.Message}",
                title);
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
