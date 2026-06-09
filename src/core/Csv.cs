using System.Text;

namespace SqlSpider;

// minimal RFC-4180-ish CSV reader (handles quoted fields, embedded commas/quotes/newlines, BOM).
static class Csv
{
    public static List<Dictionary<string, string>> Read(string path)
    {
        var rows = new List<Dictionary<string, string>>();
        var records = Parse(File.ReadAllText(path));
        if (records.Count == 0) return rows;
        var header = records[0];
        for (int i = 1; i < records.Count; i++)
        {
            var rec = records[i];
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < header.Count; c++) d[header[c]] = c < rec.Count ? rec[c] : "";
            rows.Add(d);
        }
        return rows;
    }

    static List<List<string>> Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);   // strip BOM
        var records = new List<List<string>>();
        var field = new StringBuilder();
        var record = new List<string>();
        bool inQuotes = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
            }
            else
            {
                switch (ch)
                {
                    case '"': inQuotes = true; break;
                    case ',': record.Add(field.ToString()); field.Clear(); break;
                    case '\r': break;
                    case '\n': record.Add(field.ToString()); field.Clear(); records.Add(record); record = new List<string>(); break;
                    default: field.Append(ch); break;
                }
            }
        }
        if (field.Length > 0 || record.Count > 0) { record.Add(field.ToString()); records.Add(record); }
        // drop a trailing fully-empty record (file ending in newline)
        records.RemoveAll(r => r.Count == 1 && r[0].Length == 0);
        return records;
    }
}
