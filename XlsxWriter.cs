using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace ShiftScheduler;

/// <summary>Writes a simple .xlsx file with no extra libraries (an .xlsx is a zip of XML files).</summary>
public static class XlsxWriter
{
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PkgRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static void Write(string path, RosterTable table)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        AddEntry(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "</Types>");

        AddEntry(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            $"<Relationships xmlns=\"{PkgRelNs}\">" +
            $"<Relationship Id=\"rId1\" Type=\"{RelNs}/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>");

        AddEntry(zip, "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            $"<workbook xmlns=\"{MainNs}\" xmlns:r=\"{RelNs}\">" +
            "<sheets><sheet name=\"Roster\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");

        AddEntry(zip, "xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            $"<Relationships xmlns=\"{PkgRelNs}\">" +
            $"<Relationship Id=\"rId1\" Type=\"{RelNs}/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            $"<Relationship Id=\"rId2\" Type=\"{RelNs}/styles\" Target=\"styles.xml\"/>" +
            "</Relationships>");

        AddEntry(zip, "xl/styles.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            $"<styleSheet xmlns=\"{MainNs}\">" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"2\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
            "</cellXfs></styleSheet>");

        AddEntry(zip, "xl/worksheets/sheet1.xml", BuildSheet(table));
    }

    private static string BuildSheet(RosterTable t)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append($"<worksheet xmlns=\"{MainNs}\">");
        sb.Append("<cols>");
        sb.Append("<col min=\"1\" max=\"1\" width=\"24\" customWidth=\"1\"/>");
        sb.Append("<col min=\"2\" max=\"2\" width=\"16\" customWidth=\"1\"/>");
        sb.Append("<col min=\"3\" max=\"9\" width=\"13\" customWidth=\"1\"/>");
        sb.Append("<col min=\"10\" max=\"11\" width=\"12\" customWidth=\"1\"/>");
        sb.Append("</cols><sheetData>");

        int r = 1;

        // Title
        sb.Append($"<row r=\"{r}\">");
        TextCell(sb, 0, r, t.Title, true);
        sb.Append("</row>");
        r += 2;

        // Header
        sb.Append($"<row r=\"{r}\">");
        int c = 0;
        TextCell(sb, c++, r, "Employee", true);
        TextCell(sb, c++, r, "Role", true);
        foreach (var h in t.DayHeaders) TextCell(sb, c++, r, h, true);
        TextCell(sb, c++, r, "Hours", true);
        TextCell(sb, c++, r, "Max hours", true);
        sb.Append("</row>");
        r++;

        // Data
        foreach (var row in t.Rows)
        {
            sb.Append($"<row r=\"{r}\">");
            c = 0;
            TextCell(sb, c++, r, row.Name, false);
            TextCell(sb, c++, r, row.Role, false);
            foreach (var cell in row.Cells) TextCell(sb, c++, r, cell, false);
            NumCell(sb, c++, r, row.Hours);
            NumCell(sb, c++, r, row.MaxHours);
            sb.Append("</row>");
            r++;
        }

        // Legend
        r++;
        sb.Append($"<row r=\"{r}\">");
        TextCell(sb, 0, r, "Legend", true);
        sb.Append("</row>");
        r++;
        foreach (var line in t.Legend)
        {
            sb.Append($"<row r=\"{r}\">");
            TextCell(sb, 0, r, line, false);
            sb.Append("</row>");
            r++;
        }

        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static void TextCell(StringBuilder sb, int col, int row, string text, bool bold)
    {
        var style = bold ? " s=\"1\"" : "";
        sb.Append($"<c r=\"{ColName(col)}{row}\" t=\"inlineStr\"{style}><is><t>{SecurityElement.Escape(text ?? "")}</t></is></c>");
    }

    private static void NumCell(StringBuilder sb, int col, int row, double value)
    {
        sb.Append($"<c r=\"{ColName(col)}{row}\"><v>{value.ToString("0.##", CultureInfo.InvariantCulture)}</v></c>");
    }

    private static string ColName(int index)
    {
        var name = "";
        index++;
        while (index > 0)
        {
            int rem = (index - 1) % 26;
            name = (char)('A' + rem) + name;
            index = (index - 1) / 26;
        }
        return name;
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
