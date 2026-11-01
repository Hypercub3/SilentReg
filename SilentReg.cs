using System;
using System.CodeDom.Compiler;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.CSharp;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 0)
        {
            Application.Run(new ConverterForm());
            return 0;
        }
        // Command-line mode writes a result file instead of opening UI.
        string statusPath = null;
        try
        {
            string input = null, output = null, view = "Native";
            bool admin = false, force = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--input": input = Next(args, ref i); break;
                    case "--output": output = Next(args, ref i); break;
                    case "--view": view = Next(args, ref i); break;
                    case "--status": statusPath = Path.GetFullPath(Next(args, ref i)); break;
                    case "--admin": admin = true; break;
                    case "--force": force = true; break;
                    default: throw new ArgumentException("Unknown option: " + args[i]);
                }
            }
            Builder.Build(input, output, admin, view, force);
            if (statusPath != null) File.WriteAllText(statusPath, "Created: " + Path.GetFullPath(output));
            return 0;
        }
        catch (Exception ex)
        {
            if (statusPath != null)
            {
                try { File.WriteAllText(statusPath, "ERROR: " + ex.Message); } catch { }
            }
            return 1;
        }
    }

    private static string Next(string[] args, ref int index)
    {
        if (++index >= args.Length) throw new ArgumentException("An option value is missing.");
        return args[index];
    }
}

internal static class Builder
{
    internal static byte[] ReadPayload(string path)
    {
        if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new ArgumentException("Choose an existing .reg file.");
        if (!String.Equals(Path.GetExtension(path), ".reg", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The input must have a .reg extension.");
        byte[] bytes = File.ReadAllBytes(path);
        using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.Default, true))
        {
            string header = reader.ReadLine();
            if (header != "Windows Registry Editor Version 5.00" && header != "REGEDIT4")
                throw new ArgumentException("The file does not have a recognized registry-file header.");
        }
        return bytes;
    }

    internal static bool SuggestAdmin(string path)
    {
        byte[] bytes = ReadPayload(path);
        string content;
        using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.Default, true))
            content = reader.ReadToEnd();
        // A hint only: registry permissions cannot be inferred completely from hive names.
        foreach (Match m in Regex.Matches(content, @"^\s*\[-?(HKEY_[A-Z_]+)(?:\\|\])", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            if (!m.Groups[1].Value.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static void Build(string input, string output, bool admin, string view, bool overwrite, byte[] customIcon = null, bool defaultIcon = true)
    {
        byte[] bytes = ReadPayload(input);
        if (String.IsNullOrWhiteSpace(output)) throw new ArgumentException("Choose an output .exe path.");
        output = Path.GetFullPath(output);
        if (!String.Equals(Path.GetExtension(output), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output must have an .exe extension.");
        if (!Directory.Exists(Path.GetDirectoryName(output))) throw new ArgumentException("The output folder does not exist.");
        if (File.Exists(output) && !overwrite) throw new IOException("The output already exists. Choose another name or use --force.");
        if (String.Equals(view, "native", StringComparison.OrdinalIgnoreCase)) view = "Native";
        if (view != "Native" && view != "32" && view != "64") throw new ArgumentException("Registry view must be Native, 32, or 64.");

        string work = Path.Combine(Path.GetTempPath(), "SilentRegBuild-" + Guid.NewGuid().ToString("N"));
        // Compile locally: synced/virtual drives can lock newly created EXEs and
        // reject the rename/replace operation used to publish a staged file.
        string staged = Path.Combine(work, "importer.exe");
        Directory.CreateDirectory(work);
        try
        {
            string payload = Path.Combine(work, "payload.reg");
            File.WriteAllBytes(payload, bytes);
            string iconPath = Path.Combine(work, "SilentReg.ico");
            if (defaultIcon)
            using (Stream icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("SilentReg.ico"))
            using (Stream outputIcon = File.Create(iconPath))
            {
                if (icon == null) throw new InvalidOperationException("The application icon is missing.");
                icon.CopyTo(outputIcon);
            }
            if (!defaultIcon && customIcon != null) File.WriteAllBytes(iconPath, customIcon);
            string manifest = Path.Combine(work, "app.manifest");
            File.WriteAllText(manifest,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><assembly manifestVersion=\"1.0\" xmlns=\"urn:schemas-microsoft-com:asm.v1\">" +
                "<assemblyIdentity version=\"1.0.0.0\" name=\"SilentReg.Generated\"/>" +
                "<trustInfo xmlns=\"urn:schemas-microsoft-com:asm.v3\"><security><requestedPrivileges>" +
                "<requestedExecutionLevel level=\"" + (admin ? "requireAdministrator" : "asInvoker") + "\" uiAccess=\"false\"/>" +
                "</requestedPrivileges></security></trustInfo></assembly>", new UTF8Encoding(false));
            string source;
            using (var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("Runner.cs")))
                source = reader.ReadToEnd().Replace("__REGISTRY_VIEW__", view);
            using (var compiler = new CSharpCodeProvider())
            {
                var options = new CompilerParameters();
                options.GenerateExecutable = true;
                options.OutputAssembly = staged;
                options.CompilerOptions = "/target:winexe /platform:anycpu /optimize+ /win32manifest:\"" + manifest + "\"";
                if (File.Exists(iconPath)) options.CompilerOptions += " /win32icon:\"" + iconPath + "\"";
                options.ReferencedAssemblies.Add("System.dll");
                options.EmbeddedResources.Add(payload);
                CompilerResults result = compiler.CompileAssemblyFromSource(options, source);
                if (result.Errors.HasErrors)
                {
                    var errors = new StringBuilder("Could not build the silent executable:");
                    foreach (CompilerError error in result.Errors) if (!error.IsWarning) errors.AppendLine().Append(error.ErrorText);
                    throw new InvalidOperationException(errors.ToString());
                }
            }
            SaveOutput(staged, output, overwrite);
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch { }
            try { Directory.Delete(work, true); } catch { }
        }
    }

    private static void SaveOutput(string source, string destination, bool overwrite)
    {
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            FileStream output = null;
            // Retry only acquiring the destination, before copying any bytes.
            // CreateNew retains the no-overwrite guarantee even if another process
            // creates the destination while compilation is in progress.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    output = new FileStream(destination, overwrite ? FileMode.Create : FileMode.CreateNew,
                        FileAccess.Write, FileShare.None);
                    break;
                }
                catch (IOException ex)
                {
                    int error = ex.HResult & 0xffff;
                    if ((error != 32 && error != 33) || attempt >= 6)
                        throw new IOException("Could not save the silent importer to:\n" + destination +
                            "\n\n" + ((error == 32 || error == 33)
                                ? "The output file is in use. Close any program using it, or choose another filename."
                                : ex.Message), ex);
                    Thread.Sleep(Math.Min(100 << attempt, 1000));
                }
            }
            using (output)
            {
                input.CopyTo(output);
                output.Flush();
            }
        }
    }
}

internal static class AppArtwork
{
    internal static void Apply(Form form)
    {
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SilentReg.ico"))
        {
            if (stream == null) return;
            using (var icon = new Icon(stream)) form.Icon = (Icon)icon.Clone();
        }
    }
}

internal sealed class ConfirmationPreference
{
    private readonly string path;
    internal bool AutoDismiss { get; private set; }

    internal ConfirmationPreference(string path)
    {
        this.path = path;
        try
        {
            AutoDismiss = File.ReadAllText(path).Trim().Equals(
                "DismissCreationPromptAfter5Seconds=true", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
    }

    internal static ConfirmationPreference ForCurrentUser()
    {
        return new ConfirmationPreference(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SilentReg", "preferences.ini"));
    }

    internal bool TrySet(bool enabled)
    {
        AutoDismiss = enabled;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "DismissCreationPromptAfter5Seconds=" +
                (enabled ? "true" : "false"), new UTF8Encoding(false));
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (System.Security.SecurityException) { return false; }
    }
}

internal sealed class CreationConfirmation : Form
{
    private readonly ConfirmationPreference preference;
    private readonly CheckBox autoDismiss = new CheckBox();
    private readonly Label preferenceStatus = new Label();
    private readonly Label countdownStatus = new Label();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private readonly Stopwatch elapsed = new Stopwatch();

    internal CreationConfirmation(string output, ConfirmationPreference preference)
    {
        this.preference = preference;
        Text = "Silent importer ready";
        AppArtwork.Apply(this);
        ClientSize = new Size(600, 310);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;

        var heading = new Label { Text = "Created:" };
        heading.SetBounds(24, 18, 552, 24);
        Controls.Add(heading);
        var path = new TextBox { Text = output, ReadOnly = true, Multiline = true,
            WordWrap = true, ScrollBars = ScrollBars.Vertical, TabStop = false };
        path.SetBounds(24, 46, 552, 48);
        Controls.Add(path);
        var details = new Label { Text = "Run this file to apply the registry changes.\nConversion has not applied them." };
        details.SetBounds(24, 106, 552, 44);
        Controls.Add(details);

        autoDismiss.Name = "AutoDismiss";
        autoDismiss.Text = "dismiss prompt after 5 seconds";
        autoDismiss.SetBounds(24, 270, 430, 28);
        autoDismiss.Checked = preference.AutoDismiss;
        autoDismiss.CheckedChanged += delegate
        {
            preferenceStatus.Text = preference.TrySet(autoDismiss.Checked)
                ? "Setting saved for future confirmations."
                : "Could not save setting; it applies to this session only.";
            ConfigureCountdown();
        };
        Controls.Add(autoDismiss);
        preferenceStatus.Name = "PreferenceStatus";
        preferenceStatus.Text = "Your selection is remembered for future confirmations.";
        preferenceStatus.SetBounds(24, 164, 552, 24);
        Controls.Add(preferenceStatus);
        countdownStatus.Name = "CountdownStatus";
        countdownStatus.SetBounds(24, 198, 552, 24);
        Controls.Add(countdownStatus);
        var ok = new Button { Name = "Confirm", Text = "OK", DialogResult = DialogResult.OK };
        ok.SetBounds(476, 226, 100, 34);
        Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = ok;
        timer.Interval = 100;
        timer.Tick += delegate
        {
            double remaining = 5 - elapsed.Elapsed.TotalSeconds;
            if (remaining <= 0)
            {
                timer.Stop();
                DialogResult = DialogResult.OK;
                Close();
            }
            else countdownStatus.Text = "Closes automatically in " + Math.Ceiling(remaining) + " seconds.";
        };
        ConfigureCountdown();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ConfigureCountdown();
    }

    private void ConfigureCountdown()
    {
        timer.Stop();
        elapsed.Reset();
        countdownStatus.Text = autoDismiss.Checked
            ? "Closes automatically in 5 seconds."
            : "Automatic dismissal is off.";
        if (autoDismiss.Checked && Visible)
        {
            elapsed.Start();
            timer.Start();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class IconPreference
{
    private readonly string path;
    internal string Mode = "Built-in";
    internal string CustomPath = "";
    internal byte[] CustomBytes;

    internal IconPreference(string path)
    {
        this.path = path;
        try
        {
            string[] lines = File.ReadAllLines(path);
            if (lines.Length != 3) return;
            if (lines[0] != "Built-in" && lines[0] != "Custom" && lines[0] != "None") return;
            string customPath = Encoding.UTF8.GetString(Convert.FromBase64String(lines[1]));
            byte[] bytes = Convert.FromBase64String(lines[2]);
            if (bytes.Length > 0) Validate(bytes);
            if (lines[0] == "Custom" && bytes.Length == 0) return;
            Mode = lines[0]; CustomPath = customPath; CustomBytes = bytes.Length == 0 ? null : bytes;
        }
        catch (Exception) { /* A missing or invalid setting uses the built-in icon. */ }
    }

    internal static IconPreference ForCurrentUser()
    {
        return new IconPreference(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SilentReg", "icon.ini"));
    }

    private static void Validate(byte[] bytes)
    {
        using (var stream = new MemoryStream(bytes))
        using (var icon = new Icon(stream)) { if (icon.Width <= 0) throw new ArgumentException("Invalid icon file."); }
    }

    internal void SelectCustom(string file)
    {
        if (!Path.GetExtension(file).Equals(".ico", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a Windows .ico file.");
        byte[] bytes = File.ReadAllBytes(file);
        Validate(bytes);
        CustomBytes = bytes; CustomPath = Path.GetFullPath(file); Mode = "Custom";
    }

    internal bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, new string[] { Mode, Convert.ToBase64String(Encoding.UTF8.GetBytes(CustomPath)),
                Convert.ToBase64String(CustomBytes ?? new byte[0]) }, new UTF8Encoding(false));
            return true;
        }
        catch (Exception) { return false; }
    }
}

internal sealed class ConverterForm : Form
{
    private readonly TextBox input = new TextBox();
    private readonly CheckBox admin = new CheckBox();
    private readonly ComboBox view = new ComboBox();
    private readonly Label status = new Label();
    private readonly Button create = new Button();
    private readonly ConfirmationPreference confirmationPreference = ConfirmationPreference.ForCurrentUser();
    private readonly IconPreference iconPreference;
    private readonly ComboBox iconMode = new ComboBox();
    private readonly TextBox iconPath = new TextBox();
    private readonly Button iconBrowse = new Button();
    private bool updatingIcon;

    internal ConverterForm() : this(IconPreference.ForCurrentUser()) { }

    internal ConverterForm(IconPreference preference)
    {
        iconPreference = preference;
        Text = "SilentReg - Silent registry converter";
        AppArtwork.Apply(this);
        ClientSize = new Size(650, 490);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AllowDrop = true;

        AddLabel("Create a silent registry import", 24, 20, 600, 32, 16F);
        AddLabel("Choose a .reg file or drop it here. Your original file stays unchanged.", 24, 64, 600, 26, 10F);
        input.SetBounds(24, 105, 490, 30);
        input.ReadOnly = true;
        Controls.Add(input);
        var browse = new Button { Text = "Browse..." };
        browse.SetBounds(526, 103, 100, 32);
        browse.Click += delegate
        {
            using (var dialog = new OpenFileDialog { Filter = "Registry files (*.reg)|*.reg", Title = "Choose a registry file" })
                if (dialog.ShowDialog(this) == DialogResult.OK) SelectInput(dialog.FileName);
        };
        Controls.Add(browse);
        admin.Text = "Request administrator access (Windows may show UAC)";
        admin.SetBounds(24, 151, 602, 30);
        Controls.Add(admin);
        AddLabel("Registry view:", 24, 195, 115, 28, 10F);
        view.DropDownStyle = ComboBoxStyle.DropDownList;
        view.Items.AddRange(new object[] { "Native", "64", "32" });
        view.SelectedIndex = 0;
        view.SetBounds(145, 191, 110, 30);
        Controls.Add(view);
        AddLabel("Native uses the operating system's default view.", 270, 195, 360, 28, 9F);
        AddLabel("Output icon:", 24, 237, 110, 28, 10F);
        iconMode.Name = "IconMode";
        iconMode.DropDownStyle = ComboBoxStyle.DropDownList;
        iconMode.Items.AddRange(new object[] { "Built-in", "Custom", "None" });
        iconMode.SetBounds(145, 233, 160, 30);
        iconMode.SelectedItem = iconPreference.Mode;
        Controls.Add(iconMode);
        AddLabel("None uses the generic executable icon.", 318, 237, 310, 28, 9F);
        iconPath.Name = "IconPath";
        iconPath.ReadOnly = true;
        iconPath.SetBounds(24, 276, 490, 30);
        Controls.Add(iconPath);
        iconBrowse.Text = "Icon...";
        iconBrowse.SetBounds(526, 274, 100, 32);
        iconBrowse.Click += delegate { BrowseIcon(); };
        Controls.Add(iconBrowse);
        iconMode.SelectedIndexChanged += delegate
        {
            if (updatingIcon) return;
            if (iconMode.SelectedItem.ToString() == "Custom" && iconPreference.CustomBytes == null)
            {
                if (!BrowseIcon()) RefreshIconFields();
                return;
            }
            iconPreference.Mode = iconMode.SelectedItem.ToString();
            SaveIconSetting();
        };
        RefreshIconFields();
        AddLabel("Choose an .ico file above or drop it anywhere in this window.", 24, 314, 600, 26, 9F);
        AddLabel("The generated .exe applies changes without dialogs or log files.\nAdministrator access still requires Windows approval.", 24, 350, 600, 52, 10F);
        create.Text = "Create silent .exe...";
        create.SetBounds(24, 410, 205, 38);
        create.Enabled = false;
        create.Click += CreateOutput;
        Controls.Add(create);
        status.SetBounds(24, 457, 602, 26);
        status.AutoEllipsis = true;
        Controls.Add(status);
        DragEventHandler onDragEnter = delegate(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        };
        DragEventHandler onDragDrop = delegate(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length == 1)
            {
                if (Path.GetExtension(files[0]).Equals(".ico", StringComparison.OrdinalIgnoreCase)) SelectIcon(files[0]);
                else SelectInput(files[0]);
            }
        };
        DragEnter += onDragEnter;
        DragDrop += onDragDrop;
        foreach (Control control in Controls)
        {
            control.AllowDrop = true;
            control.DragEnter += onDragEnter;
            control.DragDrop += onDragDrop;
        }
    }

    private bool BrowseIcon()
    {
        using (var dialog = new OpenFileDialog { Filter = "Windows icons (*.ico)|*.ico", Title = "Choose output icon" })
        {
            // The saved path also covers icons selected by drag-and-drop or in an earlier session.
            try
            {
                string directory = Path.GetDirectoryName(iconPreference.CustomPath);
                if (Directory.Exists(directory)) dialog.InitialDirectory = directory;
            }
            catch (ArgumentException) { /* Fall back to the normal picker location. */ }
            catch (NotSupportedException) { /* Ignore an unavailable saved path. */ }
            return dialog.ShowDialog(this) == DialogResult.OK && SelectIcon(dialog.FileName);
        }
    }

    private bool SelectIcon(string file)
    {
        try { iconPreference.SelectCustom(file); SaveIconSetting(); return true; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cannot use this icon", MessageBoxButtons.OK, MessageBoxIcon.Error); return false; }
    }

    private void RefreshIconFields()
    {
        updatingIcon = true;
        iconMode.SelectedItem = iconPreference.Mode;
        iconPath.Text = iconPreference.Mode == "Custom" ? iconPreference.CustomPath :
            (iconPreference.Mode == "None" ? "Generic executable icon" : "SilentReg built-in icon");
        // Keep the filename visible when icons share a long directory prefix.
        iconPath.SelectionStart = iconPath.TextLength;
        iconPath.SelectionLength = 0;
        iconPath.ScrollToCaret();
        updatingIcon = false;
    }

    private void SaveIconSetting()
    {
        bool saved = iconPreference.Save();
        RefreshIconFields();
        status.Text = saved ? "Icon setting saved for future sessions." : "Could not save icon setting; it applies to this session only.";
    }

    private void AddLabel(string text, int x, int y, int width, int height, float size)
    {
        var label = new Label { Text = text, Font = new Font("Segoe UI", size) };
        label.SetBounds(x, y, width, height);
        Controls.Add(label);
    }

    private void SelectInput(string path)
    {
        try
        {
            bool suggested = Builder.SuggestAdmin(path);
            input.Text = Path.GetFullPath(path);
            admin.Checked = suggested;
            create.Enabled = true;
            status.Text = suggested ? "Administrator access suggested for this file. Review before creating." : "Ready. Review the registry file before creating.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cannot use this file", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void CreateOutput(object sender, EventArgs e)
    {
        using (var dialog = new SaveFileDialog { Filter = "Windows executable (*.exe)|*.exe", DefaultExt = "exe", AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(input.Text) + ".silent.exe", InitialDirectory = Path.GetDirectoryName(input.Text), Title = "Save silent importer", OverwritePrompt = true })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            create.Enabled = false;
            UseWaitCursor = true;
            status.Text = "Creating...";
            status.Refresh();
            try
            {
                Builder.Build(input.Text, dialog.FileName, admin.Checked, view.SelectedItem.ToString(), true,
                    iconPreference.Mode == "Custom" ? iconPreference.CustomBytes : null, iconPreference.Mode == "Built-in");
                status.Text = "Created: " + dialog.FileName;
                UseWaitCursor = false;
                using (var confirmation = new CreationConfirmation(dialog.FileName, confirmationPreference))
                    confirmation.ShowDialog(this);
            }
            catch (Exception ex)
            {
                status.Text = "Creation failed.";
                MessageBox.Show(this, ex.Message, "Creation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { create.Enabled = true; UseWaitCursor = false; }
        }
    }
}
