using System.Diagnostics;

namespace DicomPrintClientGui.Services;

public enum ServiceStatus { Running, Stopped, Unknown }

public class DicomServiceController
{
    // يجرب كل أسماء الخدمة الممكنة (القديمة والجديدة)
    private static readonly string[] KnownNames =
    {
        "DicomPrintServer",
        "DicomPrintServerTrial",
        "DICOM Print Server",
        "DicomPrint"
    };

    private string? _resolvedName;

    private string? FindInstalledName()
    {
        if (_resolvedName != null) return _resolvedName;
        foreach (var name in KnownNames)
        {
            string output = RunSc($"query \"{name}\"");
            if (!output.Contains("FAILED") && !output.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                && output.Length > 20)
            {
                _resolvedName = name;
                return name;
            }
        }
        return null;
    }

    private static string RunSc(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd()
                          + p.StandardError.ReadToEnd();
            p.WaitForExit(8_000);
            return output;
        }
        catch { return ""; }
    }

    public ServiceStatus GetStatus()
    {
        try
        {
            var name = FindInstalledName();
            if (name == null) return ServiceStatus.Unknown;
            string output = RunSc($"query \"{name}\"");
            if (output.Contains("RUNNING")) return ServiceStatus.Running;
            if (output.Contains("STOPPED")) return ServiceStatus.Stopped;
            return ServiceStatus.Unknown;
        }
        catch { return ServiceStatus.Unknown; }
    }

    public bool IsInstalled() => FindInstalledName() != null;

    public string InstalledServiceName => FindInstalledName() ?? "—";

    public (bool ok, string error) Start()
    {
        var name = FindInstalledName();
        if (name == null) return (false, "الخدمة غير مثبتة");
        try
        {
            RunSc($"start \"{name}\"");
            for (int i = 0; i < 15; i++)
            {
                Thread.Sleep(1000);
                if (GetStatus() == ServiceStatus.Running) return (true, "");
            }
            return (false, "انتهت مهلة الانتظار");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public (bool ok, string error) Stop()
    {
        var name = FindInstalledName();
        if (name == null) return (false, "الخدمة غير مثبتة");
        try
        {
            RunSc($"stop \"{name}\"");
            for (int i = 0; i < 15; i++)
            {
                Thread.Sleep(1000);
                if (GetStatus() == ServiceStatus.Stopped) return (true, "");
            }
            return (false, "انتهت مهلة الانتظار");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public (bool ok, string error) Restart()
    {
        Stop();
        Thread.Sleep(1500);
        return Start();
    }
}
