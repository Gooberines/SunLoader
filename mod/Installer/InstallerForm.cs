using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SunLoaderInstaller
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallerForm());
        }
    }

    // The installer window, styled like the mod's menus: black, hot pink, bold.
    class InstallerForm : Form
    {
        static readonly Color Bg = Color.FromArgb(10, 10, 14), Panel = Color.FromArgb(24, 24, 32), Pink = Color.FromArgb(255, 42, 115);
        static readonly Color PinkHover = Color.FromArgb(255, 90, 150), Dim = Color.FromArgb(150, 150, 170), Green = Color.FromArgb(80, 230, 130);

        readonly TextBox pathBox = new TextBox();
        readonly Label pathStatus = new Label();
        readonly CheckBox noIntro = new CheckBox(), exampleMod = new CheckBox(), getLatest = new CheckBox();
        readonly Button installBtn = new Button(), uninstallBtn = new Button(), browseBtn = new Button();
        readonly ProgressBar progress = new ProgressBar();
        readonly TextBox log = new TextBox();
        bool busy;

        public InstallerForm()
        {
            Text = "Sun Loader Installer";
            ClientSize = new Size(640, 560);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10f);
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var header = new HeaderPanel { Bounds = new Rectangle(0, 0, 640, 120) };
            Controls.Add(header);

            Controls.Add(MakeLabel("GAME FOLDER", 30, 140, new Font("Segoe UI", 9f, FontStyle.Bold), Dim));
            pathBox.SetBounds(30, 164, 470, 28);
            pathBox.BackColor = Panel; pathBox.ForeColor = Color.White; pathBox.BorderStyle = BorderStyle.FixedSingle;
            pathBox.TextChanged += (s, e) => RefreshState();
            Controls.Add(pathBox);
            Style(browseBtn, "BROWSE", Panel, Color.FromArgb(45, 45, 60));
            browseBtn.SetBounds(510, 162, 100, 30);
            browseBtn.Click += (s, e) => Browse();
            Controls.Add(browseBtn);
            pathStatus.SetBounds(30, 196, 580, 22);
            Controls.Add(pathStatus);

            Controls.Add(MakeLabel("EXTRAS", 30, 230, new Font("Segoe UI", 9f, FontStyle.Bold), Dim));
            SetupCheck(noIntro, "No Intro  -  skip the intro video when the game starts", 30, 254, true);
            SetupCheck(exampleMod, "Example Mod  -  a sample mod for people making their own", 30, 282, false);
            SetupCheck(getLatest, "Get the newest Sun Loader from GitHub (needs internet)", 30, 310, true);

            Style(installBtn, "INSTALL", Pink, PinkHover);
            installBtn.SetBounds(30, 346, 400, 52);
            installBtn.Font = new Font("Segoe UI Black", 16f);
            installBtn.Click += (s, e) => Run(install: true);
            Controls.Add(installBtn);
            Style(uninstallBtn, "UNINSTALL", Panel, Color.FromArgb(90, 25, 40));
            uninstallBtn.SetBounds(440, 346, 170, 52);
            uninstallBtn.Click += (s, e) => Run(install: false);
            Controls.Add(uninstallBtn);

            progress.SetBounds(30, 410, 580, 10);
            Controls.Add(progress);

            log.SetBounds(30, 430, 580, 110);
            log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical;
            log.BackColor = Panel; log.ForeColor = Color.Gainsboro; log.BorderStyle = BorderStyle.None;
            log.Font = new Font("Consolas", 9f);
            Controls.Add(log);

            var found = Installer.FindGame();
            pathBox.Text = found ?? "";
            Say(found != null ? "Found Just Shapes & Beats in your Steam library." : "Couldn't find the game automatically. Press Browse and pick its folder.");
            if (!File.Exists(Path.Combine(Installer.FilesDir, "SunLoader.dll")) || !File.Exists(Path.Combine(Installer.FilesDir, "BepInEx.zip")))
                Say("WARNING: the 'files' folder is missing. Unzip the whole download and run the installer from there.");
            RefreshState();
        }

        static Label MakeLabel(string text, int x, int y, Font font, Color color) =>
            new Label { Text = text, Location = new Point(x, y), AutoSize = true, Font = font, ForeColor = color, BackColor = Color.Transparent };

        void SetupCheck(CheckBox box, string text, int x, int y, bool on)
        {
            box.Text = text; box.Checked = on; box.AutoSize = true; box.Location = new Point(x, y);
            box.ForeColor = Color.White; box.FlatStyle = FlatStyle.Flat; box.FlatAppearance.CheckedBackColor = Pink;
            Controls.Add(box);
        }

        static void Style(Button b, string text, Color color, Color hover)
        {
            b.Text = text; b.BackColor = color; b.ForeColor = Color.White;
            b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = hover;
            b.Font = new Font("Segoe UI", 10f, FontStyle.Bold); b.Cursor = Cursors.Hand;
        }

        void Browse()
        {
            using (var dialog = new FolderBrowserDialog { Description = "Pick the Just Shapes & Beats folder (the one with JSB.exe)" })
            {
                if (Directory.Exists(pathBox.Text)) dialog.SelectedPath = pathBox.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) pathBox.Text = dialog.SelectedPath;
            }
        }

        void RefreshState()
        {
            var ok = Installer.IsGameFolder(pathBox.Text);
            pathStatus.ForeColor = ok ? Green : Color.FromArgb(255, 160, 60);
            pathStatus.Text = !ok ? "This folder doesn't have JSB.exe in it."
                : Installer.IsInstalled(pathBox.Text) ? "Sun Loader is installed here. Install again to update or repair it."
                : "Ready to install.";
            installBtn.Enabled = uninstallBtn.Enabled = ok && !busy;
            installBtn.Text = ok && Installer.IsInstalled(pathBox.Text) ? "REINSTALL / UPDATE" : "INSTALL";
        }

        void Say(string line)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => Say(line))); return; }
            log.AppendText(line + Environment.NewLine);
        }

        void SetProgress(int value)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetProgress(value))); return; }
            progress.Value = Math.Max(0, Math.Min(100, value));
        }

        async void Run(bool install)
        {
            if (busy) return;
            if (!install && MessageBox.Show(this, "Turn off Sun Loader and BepInEx? Your bosses, music and mods are kept.",
                    "Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            busy = true; RefreshState(); progress.Value = 0;
            var game = pathBox.Text.Trim();
            bool wantIntro = noIntro.Checked, wantExample = exampleMod.Checked, wantLatest = getLatest.Checked;
            var installer = new Installer { Log = Say, Progress = SetProgress };
            try
            {
                await Task.Run(() => { if (install) installer.Install(game, wantIntro, wantExample, wantLatest); else installer.Uninstall(game); });
                if (install)
                    MessageBox.Show(this,
                        "Sun Loader is installed!\n\n" +
                        "The first time you start the game it takes a minute or two longer while BepInEx sets itself up. That only happens once.\n\n" +
                        "In the game: click BOSSES on the main menu (or press F4) to make your own bosses, MODS (or F2) for the mod menu, and press F1 in a level for the boss editor.",
                        "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception e)
            {
                var msg = e is InstallError ? e.Message
                    : e is UnauthorizedAccessException ? "Windows blocked writing to the game folder. Close this, right-click the installer and choose 'Run as administrator'."
                    : "Something went wrong: " + e.Message;
                Say("ERROR: " + msg);
                MessageBox.Show(this, msg, "Couldn't finish", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { busy = false; RefreshState(); }
        }
    }

    // The header: drifting pink stripes behind the title, like the game's menus.
    class HeaderPanel : Control
    {
        readonly Timer timer = new Timer { Interval = 33 };
        float offset;

        public HeaderPanel()
        {
            DoubleBuffered = true;
            timer.Tick += (s, e) => { offset = (offset + 1.2f) % 90; Invalidate(); };
            timer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(10, 10, 14));
            using (var stripe = new SolidBrush(Color.FromArgb(28, 255, 42, 115)))
                for (float x = -Height + offset; x < Width; x += 90)
                    g.FillPolygon(stripe, new[] { new PointF(x, Height), new PointF(x + 34, Height), new PointF(x + 34 + Height, 0), new PointF(x + Height, 0) });
            using (var pink = new SolidBrush(Color.FromArgb(255, 42, 115)))
            {
                DrawSun(g, new RectangleF(30, 26, 64, 64), pink);
                g.FillRectangle(pink, 0, Height - 4, Width, 4);
                using (var title = new Font("Segoe UI Black", 30f))
                {
                    g.DrawString("SUN LOADER", title, new SolidBrush(Color.FromArgb(120, 0, 0, 0)), 113, 21);
                    g.DrawString("SUN LOADER", title, Brushes.White, 110, 18);
                }
            }
            using (var sub = new Font("Segoe UI", 10f, FontStyle.Bold))
            using (var dim = new SolidBrush(Color.FromArgb(200, 200, 215)))
                g.DrawString("Mod loader + boss editor for Just Shapes & Beats  ·  ALPHA", sub, dim, 115, 72);
        }

        // A simple sun: a disc with eight rays.
        static void DrawSun(Graphics g, RectangleF r, Brush brush)
        {
            var c = new PointF(r.X + r.Width / 2, r.Y + r.Height / 2);
            for (int i = 0; i < 8; i++)
            {
                var a = i * Math.PI / 4;
                PointF P(double ang, float rad) => new PointF(c.X + (float)Math.Cos(ang) * rad, c.Y + (float)Math.Sin(ang) * rad);
                g.FillPolygon(brush, new[] { P(a - 0.22, r.Width * 0.34f), P(a, r.Width * 0.5f), P(a + 0.22, r.Width * 0.34f) });
            }
            g.FillEllipse(brush, c.X - r.Width * 0.27f, c.Y - r.Width * 0.27f, r.Width * 0.54f, r.Width * 0.54f);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
