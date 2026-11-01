using System;
using System.IO;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
internal static class IconTests
{
    private static int checks;
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); checks++; Console.WriteLine("PASS: " + text); }
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            string root = args[0];
            string setting = Path.Combine(root, "icon.ini");
            string custom = Path.Combine(root, "custom.ico");
            using (var stream = File.Create(custom)) SystemIcons.Warning.Save(stream);
            var preference = new IconPreference(setting);
            Check(preference.Mode == "Built-in", "Default uses the built-in icon.");
            preference.SelectCustom(custom);
            Check(preference.Save(), "Custom icon setting saves.");
            File.Delete(custom);
            preference = new IconPreference(setting);
            Check(preference.Mode == "Custom" && preference.CustomBytes.Length > 0, "Custom icon survives restart and removal of the original file.");
            string input = Path.Combine(root, "sample.reg");
            File.WriteAllText(input, "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\Software\\SilentRegIconTest]\r\n", System.Text.Encoding.Unicode);
            string withCustom = Path.Combine(root, "custom.exe");
            Builder.Build(input, withCustom, false, "Native", false, preference.CustomBytes, false);
            Check(File.Exists(withCustom), "Custom-icon importer compiles.");
            string withoutIcon = Path.Combine(root, "generic.exe");
            Builder.Build(input, withoutIcon, false, "Native", false, null, false);
            Check(File.Exists(withoutIcon), "Generic executable compiles without a selected icon.");
            using (var form = new ConverterForm(preference))
            {
                string replacement = Path.Combine(root, "replacement.ico");
                using (var stream = File.Create(replacement)) SystemIcons.Error.Save(stream);
                bool selected = (bool)typeof(ConverterForm).GetMethod("SelectIcon", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { replacement });
                Check(selected && form.Controls["IconPath"].Text == replacement, "Selecting another icon while Custom is active updates the field.");
                Check(new IconPreference(setting).CustomPath == replacement, "Replacement icon persists across sessions.");
                ((ComboBox)form.Controls["IconMode"]).SelectedItem = "None";
                Check(new IconPreference(setting).Mode == "None", "Generic icon selection persists from the interface.");
                custom = Path.Combine(root, "dropped.ico");
                using (var stream = File.Create(custom)) SystemIcons.Information.Save(stream);
                var data = new DataObject(DataFormats.FileDrop, new string[] { custom });
                typeof(Control).GetMethod("OnDragDrop", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form,
                    new object[] { new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.Copy) });
                Check(((ComboBox)form.Controls["IconMode"]).SelectedItem.ToString() == "Custom", "Dropping an ICO selects Custom mode.");
                Check(new IconPreference(setting).CustomPath == custom, "Dropped icon is saved for subsequent sessions.");
                ((ComboBox)form.Controls["IconMode"]).SelectedItem = "None";
                typeof(Control).GetMethod("OnDragDrop", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form.Controls["IconPath"],
                    new object[] { new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.Copy) });
                Check(((ComboBox)form.Controls["IconMode"]).SelectedItem.ToString() == "Custom", "Dropping on the icon field also selects Custom mode.");
            }
            preference.Mode = "Built-in";
            Check(preference.Save() && new IconPreference(setting).Mode == "Built-in", "Built-in selection persists.");
            string bad = Path.Combine(root, "invalid.ico"); File.WriteAllText(bad, "not an icon");
            bool rejected = false;
            try { preference.SelectCustom(bad); } catch { rejected = true; }
            Check(rejected, "Invalid icon contents are rejected.");
            File.WriteAllText(setting, "invalid setting");
            Check(new IconPreference(setting).Mode == "Built-in", "Damaged preferences revert to built-in.");
            Console.WriteLine("Passed " + checks + " icon checks. No registry changes were applied."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
