using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

internal static class ConfirmationTests
{
    private static int checks;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string path = Path.Combine(args[0], "preferences.ini");
            var preference = new ConfirmationPreference(path);
            Check(!preference.AutoDismiss, "Automatic dismissal defaults to off.");
            Check(preference.TrySet(true), "Enabled preference saves successfully.");
            preference = new ConfirmationPreference(path);
            Check(preference.AutoDismiss, "Enabled preference survives a fresh load.");
            using (var dialog = HiddenDialog(preference))
            {
                Check(Checkbox(dialog).Checked, "Saved enabled setting is reflected in the checkbox.");
                double duration = RunDialog(dialog, null);
                Check(duration >= 4.9 && duration < 7, "Enabled confirmation closes after five seconds.");
                Check(dialog.DialogResult == DialogResult.OK, "Automatic dismissal completes the confirmation.");
            }

            using (var dialog = HiddenDialog(preference))
            {
                bool disabled = false;
                bool observed = false;
                RunDialog(dialog, delegate(double seconds)
                {
                    if (!disabled && seconds >= 1)
                    {
                        Checkbox(dialog).Checked = false;
                        disabled = true;
                    }
                    if (seconds >= 6)
                    {
                        observed = true;
                        Check(dialog.Visible, "Unchecking stops automatic dismissal beyond the original deadline.");
                        Check(dialog.Controls["CountdownStatus"].Text == "Automatic dismissal is off.", "Unchecked state explains that dismissal is off.");
                        ((Button)dialog.Controls["Confirm"]).PerformClick();
                    }
                });
                Check(observed, "Unchecked confirmation waits for manual dismissal.");
            }
            preference = new ConfirmationPreference(path);
            Check(!preference.AutoDismiss, "Disabled preference survives a fresh load.");

            using (var dialog = HiddenDialog(preference))
            {
                var sinceEnabled = new Stopwatch();
                RunDialog(dialog, delegate(double seconds)
                {
                    if (!sinceEnabled.IsRunning && seconds >= 1)
                    {
                        Checkbox(dialog).Checked = true;
                        sinceEnabled.Start();
                        Check(dialog.Controls["PreferenceStatus"].Text.Contains("Setting saved"), "Checking the box confirms that the setting is saved.");
                    }
                });
                Check(sinceEnabled.Elapsed.TotalSeconds >= 4.9 && sinceEnabled.Elapsed.TotalSeconds < 7,
                    "Checking during a confirmation starts a fresh five-second countdown.");
            }
            preference = new ConfirmationPreference(path);
            Check(preference.AutoDismiss, "Interactive checkbox selection persists for future confirmations.");
            using (var dialog = HiddenDialog(preference))
            {
                double duration = RunDialog(dialog, delegate(double seconds)
                {
                    ((Button)dialog.Controls["Confirm"]).PerformClick();
                });
                Check(duration < 1, "OK can dismiss an enabled confirmation immediately.");
            }
            Check(new ConfirmationPreference(path).AutoDismiss, "Manual dismissal keeps the saved preference.");

            string blockedPath = Path.Combine(args[0], "not-a-file");
            Directory.CreateDirectory(blockedPath);
            preference = new ConfirmationPreference(blockedPath);
            using (var dialog = HiddenDialog(preference))
            {
                Checkbox(dialog).Checked = true;
                Check(preference.AutoDismiss, "An unwritable preference can still apply during the current session.");
                Check(dialog.Controls["PreferenceStatus"].Text.Contains("Could not save"), "A persistence error is shown in the confirmation.");
            }
            File.WriteAllText(path, "invalid preference data");
            Check(!new ConfirmationPreference(path).AutoDismiss, "Invalid saved data safely defaults to off.");
            Console.WriteLine("Passed " + checks + " confirmation checks. No user settings or registry values were changed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
        Console.WriteLine("PASS: " + message);
    }

    private static CheckBox Checkbox(CreationConfirmation dialog)
    {
        return (CheckBox)dialog.Controls["AutoDismiss"];
    }

    private static CreationConfirmation HiddenDialog(ConfirmationPreference preference)
    {
        return new CreationConfirmation(@"C:\Example\settings.silent.exe", preference)
        {
            ShowInTaskbar = false,
            Opacity = 0,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000)
        };
    }

    private static double RunDialog(CreationConfirmation dialog, Action<double> onTick)
    {
        var elapsed = Stopwatch.StartNew();
        Exception failure = null;
        using (var observer = new System.Windows.Forms.Timer { Interval = 50 })
        {
            observer.Tick += delegate
            {
                try
                {
                    if (elapsed.Elapsed.TotalSeconds >= 8) throw new Exception("Confirmation test exceeded its deadline.");
                    if (onTick != null) onTick(elapsed.Elapsed.TotalSeconds);
                }
                catch (Exception ex) { failure = ex; dialog.Close(); }
            };
            observer.Start();
            dialog.ShowDialog();
            observer.Stop();
        }
        if (failure != null) throw failure;
        return elapsed.Elapsed.TotalSeconds;
    }
}
