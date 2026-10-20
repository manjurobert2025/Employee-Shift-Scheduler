using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ShiftScheduler;

public static class PrintService
{
    /// <summary>
    /// toPdf = false: opens the normal Print dialog.
    /// toPdf = true: sends the roster to "Microsoft Print to PDF" (built into Windows 10/11),
    /// which asks you where to save the PDF file.
    /// </summary>
    public static void Print(Window owner, RosterTable table, bool toPdf)
    {
        try
        {
            var pd = new PrintDialog();

            if (toPdf)
            {
                var queue = FindPdfQueue();
                if (queue == null)
                {
                    MessageBox.Show(owner,
                        "The 'Microsoft Print to PDF' printer was not found on this PC.\n\n" +
                        "Use the Print... button instead and choose any PDF printer.",
                        "Export PDF");
                    return;
                }
                pd.PrintQueue = queue;
            }

            pd.PrintTicket.PageOrientation = PageOrientation.Landscape;

            if (!toPdf && pd.ShowDialog() != true)
                return;

            var doc = BuildDocument(table);

            double width = pd.PrintableAreaWidth;
            double height = pd.PrintableAreaHeight;
            if (width <= 0 || height <= 0)
            {
                width = 1123;   // A4 landscape at 96 dpi
                height = 794;
            }

            doc.PageWidth = width;
            doc.PageHeight = height;
            doc.PagePadding = new Thickness(36);
            doc.ColumnWidth = width;

            pd.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, table.Title);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Could not print or export: {ex.Message}", "Shift Scheduler");
        }
    }

    private static PrintQueue? FindPdfQueue()
    {
        try
        {
            return new LocalPrintServer()
                .GetPrintQueues()
                .FirstOrDefault(q => q.Name.Contains("Print to PDF", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static FlowDocument BuildDocument(RosterTable t)
    {
        var doc = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 11 };

        doc.Blocks.Add(new Paragraph(new Run(t.Title))
        {
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 12)
        });

        var table = new Table { CellSpacing = 0 };
        table.Columns.Add(new TableColumn { Width = new GridLength(2.2, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1.5, GridUnitType.Star) });
        for (int d = 0; d < 7; d++)
            table.Columns.Add(new TableColumn { Width = new GridLength(1.2, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(0.9, GridUnitType.Star) });

        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        var header = new List<string> { "Employee", "Role" };
        header.AddRange(t.DayHeaders);
        header.Add("Hours");
        group.Rows.Add(MakeRow(header, true));

        foreach (var row in t.Rows)
        {
            var cells = new List<string> { row.Name, row.Role };
            cells.AddRange(row.Cells);
            cells.Add(row.Hours.ToString("0.#"));
            group.Rows.Add(MakeRow(cells, false));
        }

        doc.Blocks.Add(table);

        doc.Blocks.Add(new Paragraph(new Run("Legend: " + string.Join("   |   ", t.Legend)))
        {
            FontSize = 10,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 12, 0, 0)
        });

        return doc;
    }

    private static TableRow MakeRow(IEnumerable<string> cells, bool header)
    {
        var row = new TableRow { Background = header ? Brushes.LightGray : Brushes.Transparent };
        int i = 0;
        foreach (var text in cells)
        {
            var paragraph = new Paragraph(new Run(text))
            {
                Margin = new Thickness(0),
                TextAlignment = i >= 2 ? TextAlignment.Center : TextAlignment.Left,
                FontWeight = header ? FontWeights.Bold : FontWeights.Normal
            };

            row.Cells.Add(new TableCell(paragraph)
            {
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(4, 3, 4, 3)
            });
            i++;
        }
        return row;
    }
}
