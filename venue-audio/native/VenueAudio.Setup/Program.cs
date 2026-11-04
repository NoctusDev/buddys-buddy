using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace VenueAudio.Setup;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        var form = new Form { Text = "Install Venue Audio", ClientSize = new Size(650, 420), StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false };
        var title = new Label { Text = "Venue Audio — in-game test build", Font = new Font("Segoe UI", 17, FontStyle.Bold), AutoSize = true, Location = new Point(22, 20) };
        var description = new Label { Text = "Keep FFXIV open. Prepare the files here, then load the plugin in-game.\nThis does not edit your running game's settings.", AutoSize = true, Location = new Point(24, 68) };
        var profile = new TextBox { Text = FindProfile(), Location = new Point(24, 126), Width = 462 };
        var browse = new Button { Text = "Browse…", Location = new Point(496, 124), Width = 90 };
        var status = new Label { Text = "Choose your XIVLauncher profile folder, then click Prepare files.", Location = new Point(24, 170), Size = new Size(600, 100) };
        var pluginPath = new TextBox { ReadOnly = true, Location = new Point(24, 285), Width = 600, PlaceholderText = "Plugin path appears here and is copied for you" };
        var install = new Button { Text = "Prepare files & copy path", Location = new Point(24, 350), Size = new Size(220, 40) };
        var close = new Button { Text = "Close", Location = new Point(520, 350), Size = new Size(106, 40) };
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "Select your XIVLauncher profile folder", UseDescriptionForTitle = true }; if (dialog.ShowDialog(form) == DialogResult.OK) profile.Text = dialog.SelectedPath; };
        close.Click += (_, _) => form.Close();
        install.Click += (_, _) =>
        {
            try
            {
                string root = Path.GetFullPath(profile.Text.Trim());
                string configPath = Path.Combine(root, "dalamudConfig.json");
                if (!File.Exists(configPath)) throw new IOException("That folder does not contain dalamudConfig.json. Select the XIVLauncher profile you use.");
                using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("plugin.zip")!;
                string build = Convert.ToHexStringLower(SHA256.HashData(resource))[..10];
                resource.Position = 0;
                string destination = Path.Combine(root, "venue-audio", "0.1.0-" + build);
                Directory.CreateDirectory(destination);
                using var archive = new ZipArchive(resource);
                string complete = Path.Combine(destination, ".complete");
                if (!File.Exists(complete)) { archive.ExtractToDirectory(destination, true); File.WriteAllText(complete, build); }
                // Dalamud packages may have a containing directory; locate the actual entry assembly.
                string dll = Directory.GetFiles(destination, "VenueAudio.dll", SearchOption.AllDirectories).Single();
                pluginPath.Text = dll;
                Clipboard.SetText(dll);
                status.Text = "Files ready; plugin path copied. Keep the game running.\n1. Dalamud Settings > Experimental > Dev Plugin Locations: paste, +, Save.\n2. Plugin Installer > Dev Tools > Scan Dev Plugins (if needed).\n3. Find Venue Audio and click Load. Its controls open immediately.";
                install.Text = "Copy plugin path again";
            }
            catch (Exception e) { status.Text = e.Message; }
        };
        form.Controls.AddRange([title, description, profile, browse, status, pluginPath, install, close]);
        Application.Run(form);
    }
    static string FindProfile()
    {
        const string known = @"D:\Multibox\Glumishxiv3";
        return File.Exists(Path.Combine(known, "dalamudConfig.json")) ? known : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher");
    }
}
