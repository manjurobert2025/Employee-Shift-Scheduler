using System.Windows;
using System.Windows.Controls;

namespace ShiftScheduler;

public partial class LeaveWindow : Window
{
    private sealed record LeaveRow(DateOnly Date)
    {
        public override string ToString() => Date.ToString("ddd dd MMM yyyy");
    }

    private readonly AppData _data;

    public LeaveWindow(AppData data)
    {
        _data = data;
        InitializeComponent();

        FromPicker.SelectedDate = DateTime.Today;
        EmployeeBox.ItemsSource = _data.Employees;
        if (_data.Employees.Count > 0)
            EmployeeBox.SelectedIndex = 0;
    }

    private void EmployeeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshList();

    private void RefreshList()
    {
        if (EmployeeBox.SelectedItem is not Employee emp)
        {
            LeaveList.ItemsSource = null;
            return;
        }

        LeaveList.ItemsSource = _data.Leave
            .Where(l => l.EmployeeId == emp.Id)
            .OrderBy(l => l.Date)
            .Select(l => new LeaveRow(l.Date))
            .ToList();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (EmployeeBox.SelectedItem is not Employee emp)
        {
            MessageBox.Show("Add an employee first.", "Leave");
            return;
        }
        if (FromPicker.SelectedDate is not DateTime fromDate)
        {
            MessageBox.Show("Pick a From date.", "Leave");
            return;
        }

        var toDate = ToPicker.SelectedDate ?? fromDate;
        if (toDate < fromDate)
        {
            MessageBox.Show("The To date cannot be before the From date.", "Leave");
            return;
        }
        if ((toDate - fromDate).TotalDays > 366)
        {
            MessageBox.Show("Please add leave in ranges of one year or less.", "Leave");
            return;
        }

        var last = DateOnly.FromDateTime(toDate);
        for (var d = DateOnly.FromDateTime(fromDate); d <= last; d = d.AddDays(1))
        {
            if (!_data.Leave.Any(l => l.EmployeeId == emp.Id && l.Date == d))
                _data.Leave.Add(new LeaveEntry { EmployeeId = emp.Id, Date = d });
        }

        RefreshList();
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (EmployeeBox.SelectedItem is not Employee emp) return;

        var dates = LeaveList.SelectedItems.OfType<LeaveRow>().Select(r => r.Date).ToHashSet();
        if (dates.Count == 0) return;

        _data.Leave.RemoveAll(l => l.EmployeeId == emp.Id && dates.Contains(l.Date));
        RefreshList();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
