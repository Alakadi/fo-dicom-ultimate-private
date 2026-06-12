using Microsoft.Data.Sqlite;

namespace DicomPrintClientGui.Services;

public class PrintJob
{
    public int    Id          { get; set; }
    public string Timestamp   { get; set; } = "";
    public string PatientName { get; set; } = "";
    public string PatientId   { get; set; } = "";
    public string PrinterName { get; set; } = "";
    public int    Port        { get; set; }
    public string Status      { get; set; } = "";
    public int    FilmBoxes   { get; set; }
    public string OutputJpg   { get; set; } = "";
    public string OutputPdf   { get; set; } = "";
}

public class DailyStats
{
    public string Date        { get; set; } = "";
    public string PrinterName { get; set; } = "";
    public int    PrintCount  { get; set; }
    public int    PdfCount    { get; set; }
}

public class PrintStatsReader
{
    private static readonly string DbPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     "DicomPrintServer", "printlog.db");

    private SqliteConnection? OpenDb()
    {
        if (!File.Exists(DbPath)) return null;
        var conn = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
        conn.Open();
        return conn;
    }

    public int GetTodayTotal()
    {
        try
        {
            using var conn = OpenDb();
            if (conn is null) return 0;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM PrintOperations WHERE date(Timestamp)=date('now','localtime')";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch { return 0; }
    }

    public int GetAllTimeTotal()
    {
        try
        {
            using var conn = OpenDb();
            if (conn is null) return 0;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM PrintOperations";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch { return 0; }
    }

    public int GetTodayPdfCount()
    {
        try
        {
            using var conn = OpenDb();
            if (conn is null) return 0;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM PrintOperations WHERE date(Timestamp)=date('now','localtime') AND OutputPdf IS NOT NULL AND OutputPdf!=''";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch { return 0; }
    }

    public List<PrintJob> GetRecentJobs(int limit = 200)
    {
        var list = new List<PrintJob>();
        try
        {
            using var conn = OpenDb();
            if (conn is null) return list;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT Id,Timestamp,PatientName,PatientId,PrinterName,Port,Status,FilmBoxes,
                                       COALESCE(OutputJpg,''),COALESCE(OutputPdf,'')
                                FROM PrintOperations ORDER BY Id DESC LIMIT $lim";
            cmd.Parameters.AddWithValue("$lim", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new PrintJob
                {
                    Id          = r.GetInt32(0),
                    Timestamp   = r.GetString(1),
                    PatientName = r.IsDBNull(2) ? "" : r.GetString(2),
                    PatientId   = r.IsDBNull(3) ? "" : r.GetString(3),
                    PrinterName = r.IsDBNull(4) ? "" : r.GetString(4),
                    Port        = r.IsDBNull(5) ? 0  : r.GetInt32(5),
                    Status      = r.IsDBNull(6) ? "" : r.GetString(6),
                    FilmBoxes   = r.IsDBNull(7) ? 0  : r.GetInt32(7),
                    OutputJpg   = r.GetString(8),
                    OutputPdf   = r.GetString(9)
                });
        }
        catch { }
        return list;
    }

    public List<DailyStats> GetLast7Days()
    {
        var list = new List<DailyStats>();
        try
        {
            using var conn = OpenDb();
            if (conn is null) return list;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT date(Timestamp,'localtime') as D,
                                       COALESCE(PrinterName,'Unknown'),
                                       COUNT(*),
                                       SUM(CASE WHEN OutputPdf!='' THEN 1 ELSE 0 END)
                                FROM PrintOperations
                                WHERE D >= date('now','-6 days','localtime')
                                GROUP BY D, PrinterName ORDER BY D DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new DailyStats
                {
                    Date        = r.GetString(0),
                    PrinterName = r.GetString(1),
                    PrintCount  = r.GetInt32(2),
                    PdfCount    = r.GetInt32(3)
                });
        }
        catch { }
        return list;
    }
}
