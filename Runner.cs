using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

// Embedded as source in the converter; compiled with the selected registry payload.
internal static class SilentImport
{
    [STAThread]
    private static int Main()
    {
        string work = null;
        int result = 1;
        try
        {
            work = Path.Combine(Path.GetTempPath(), "SilentReg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string payload = Path.Combine(work, "payload.reg");
            using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.reg"))
            using (Stream output = new FileStream(payload, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (input == null) throw new InvalidOperationException("Embedded registry file is missing.");
                input.CopyTo(output);
            }

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string system = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
                ? "Sysnative" : "System32";
            string view = "__REGISTRY_VIEW__";
            string viewArgument = view == "Native" ? "" : " /reg:" + view;
            var start = new ProcessStartInfo(Path.Combine(windows, system, "reg.exe"),
                "import \"" + payload + "\"" + viewArgument);
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using (var process = new Process())
            {
                process.StartInfo = start;
                // Drain both streams without displaying or saving their contents.
                process.OutputDataReceived += delegate { };
                process.ErrorDataReceived += delegate { };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                result = process.ExitCode;
            }
        }
        catch (Exception)
        {
            result = 1;
        }
        finally
        {
            if (work != null)
            {
                try { Directory.Delete(work, true); }
                catch (Exception) { }
            }
        }
        return result;
    }
}
