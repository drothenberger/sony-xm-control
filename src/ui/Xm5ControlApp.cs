using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xm5ControlUi
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            try
            {
                Log("Starting");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => ReportException(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportException(e.ExceptionObject);
                Application.Run(new MainForm());
                Log("Exited");
            }
            catch (Exception ex)
            {
                ReportException(ex);
            }
        }

        private static void ReportException(object exception)
        {
            string text = exception == null ? "Unknown error" : exception.ToString();
            Log(text);
            MessageBox.Show(text, "Sony Headphones Control", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private static void Log(string message)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "xm5ui-startup.log");
                File.AppendAllText(path, DateTime.Now.ToString("s") + " " + message + Environment.NewLine + Environment.NewLine);
            }
            catch
            {
            }
        }
    }

    internal sealed class DeviceProfile
    {
        private readonly string[] aliases;

        public string DisplayName { get; private set; }
        public string NameFilter { get; private set; }
        public string AssetPath { get; private set; }

        /// <summary>
        /// True for the WF earbuds, which report a level per earbud plus one for
        /// the case. The WH headphones answer neither of those inquired types, so
        /// asking them costs a timeout per refresh for nothing.
        /// </summary>
        public bool HasEarbudBatteries
        {
            get { return NameFilter.StartsWith("WF", StringComparison.OrdinalIgnoreCase); }
        }

        public DeviceProfile(string displayName, string nameFilter, string assetPath, params string[] aliases)
        {
            DisplayName = displayName;
            NameFilter = nameFilter;
            AssetPath = assetPath;
            this.aliases = aliases ?? new string[0];
        }

        public bool MatchesText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text.IndexOf(NameFilter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            for (int i = 0; i < aliases.Length; i++)
            {
                if (text.IndexOf(aliases[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    internal sealed class DeviceDetection
    {
        public DeviceProfile Profile { get; private set; }
        public bool Connected { get; private set; }
        public bool Remembered { get; private set; }
        public int Score { get; private set; }

        public DeviceDetection(DeviceProfile profile, bool connected, bool remembered, int score)
        {
            Profile = profile;
            Connected = connected;
            Remembered = remembered;
            Score = score;
        }
    }

    internal sealed class ShortcutAction
    {
        public string Id { get; private set; }
        public string Label { get; private set; }

        public ShortcutAction(string id, string label)
        {
            Id = id;
            Label = label;
        }
    }

    internal sealed class ShortcutBinding
    {
        public bool Ctrl { get; private set; }
        public bool Alt { get; private set; }
        public bool Shift { get; private set; }
        public bool Win { get; private set; }
        public Keys Key { get; private set; }

        public ShortcutBinding(bool ctrl, bool alt, bool shift, bool win, Keys key)
        {
            Ctrl = ctrl;
            Alt = alt;
            Shift = shift;
            Win = win;
            Key = key;
        }

        public bool IsAssigned
        {
            get { return Key != Keys.None; }
        }

        public bool IsGlobalSafe
        {
            get { return IsAssigned && (Ctrl || Alt || Shift || Win || IsFunctionKey(Key)); }
        }

        public uint Modifiers
        {
            get
            {
                uint modifiers = 0x4000; // MOD_NOREPEAT
                if (Alt) modifiers |= 0x0001;
                if (Ctrl) modifiers |= 0x0002;
                if (Shift) modifiers |= 0x0004;
                if (Win) modifiers |= 0x0008;
                return modifiers;
            }
        }

        public string Signature
        {
            get { return ToConfigString().ToUpperInvariant(); }
        }

        public static ShortcutBinding None()
        {
            return new ShortcutBinding(false, false, false, false, Keys.None);
        }

        public ShortcutBinding Clone()
        {
            return new ShortcutBinding(Ctrl, Alt, Shift, Win, Key);
        }

        public override string ToString()
        {
            if (!IsAssigned) return "None";
            var parts = new List<string>();
            if (Ctrl) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            if (Win) parts.Add("Win");
            parts.Add(new KeysConverter().ConvertToString(Key));
            return string.Join(" + ", parts.ToArray());
        }

        public string ToConfigString()
        {
            if (!IsAssigned) return "None";
            var parts = new List<string>();
            if (Ctrl) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            if (Win) parts.Add("Win");
            parts.Add(Key.ToString());
            return string.Join("+", parts.ToArray());
        }

        public static ShortcutBinding Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                return None();
            }

            bool ctrl = false;
            bool alt = false;
            bool shift = false;
            bool win = false;
            Keys key = Keys.None;
            string[] parts = text.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
                {
                    ctrl = true;
                }
                else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                {
                    alt = true;
                }
                else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                {
                    shift = true;
                }
                else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                {
                    win = true;
                }
                else
                {
                    Keys parsed;
                    if (Enum.TryParse(part, true, out parsed)) key = parsed;
                    else
                    {
                        try
                        {
                            object converted = new KeysConverter().ConvertFromString(part);
                            if (converted is Keys) key = (Keys)converted;
                        }
                        catch
                        {
                        }
                    }
                }
            }

            return key == Keys.None ? None() : new ShortcutBinding(ctrl, alt, shift, win, key);
        }

        public static bool IsFunctionKey(Keys key)
        {
            return key >= Keys.F1 && key <= Keys.F24;
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly Color page = Color.FromArgb(18, 18, 18);
        private readonly Color card = Color.FromArgb(31, 31, 31);
        private readonly Color cardSoft = Color.FromArgb(43, 43, 43);
        private readonly Color line = Color.FromArgb(58, 58, 58);
        private readonly Color ink = Color.FromArgb(245, 245, 245);
        private readonly Color subdued = Color.FromArgb(169, 169, 169);
        // A value row's text once it is no longer being refreshed. Darker than
        // subdued, the colour such text normally has, rather than a new hue.
        private readonly Color faint = Color.FromArgb(112, 112, 112);
        private readonly Color blue = Color.FromArgb(0x10, 0x9d, 0xf5);
        private readonly Color bluePressed = Color.FromArgb(0x0c, 0x86, 0xd4);
        private readonly Color green = Color.FromArgb(0, 166, 125);
        private readonly Color red = Color.FromArgb(255, 88, 88);
        private readonly Color amber = Color.FromArgb(214, 150, 45);

        private const int CardInset = 36;
        private const int HeroModeOpticalOffset = 0;
        private const int HeroLastActionTop = 100;
        private const int AutoDetectIntervalMs = 4000;
        private const int AutoStateRefreshIntervalMs = 15000;
        private const int LiveEqDebounceMs = 260;
        // Window layout. Each left-hand row holds a card plus its margin on
        // both sides; the equalizer row takes whatever height is left.
        private const int RootPadding = 22;
        private const int HeaderRowHeight = 72;
        private const int CardMargin = 7;
        private const int HeroRowHeight = 152;
        private const int NoiseControlRowHeight = 344;
        // Where the settings card is wide enough for the Connection quality
        // caption beside its three buttons; any narrower and they overlap.
        private const int ContentMinimumWidth = 1260;
        // The keyboard shortcuts row sits at the foot of the settings card, but
        // never higher than just below the last setting.
        private const int ShortcutsRowMinimumTop = 730;
        private const int ShortcutsRowAllowance = 92;
        // The bands stand side by side, so the card is the same height for six
        // of them as for ten; only their width is shared out. A taller window
        // gives the faders the extra height, which is finer control.
        private const int EqPresetRowTop = 78;
        private const int EqBandTop = 124;
        private const int EqBandLabelHeight = 22;
        private const int EqFaderMinHeight = 150;
        private const int EqCardBottomInset = 18;
        private const int BackendCommandPaceMs = 450;
        private const string StateBatchTail = "D6 D1;D6 D2;52 00;56 00;5A 00;E6 01;E6 00;F6 02;F6 01;26 05\" --timeout 1800";

        // 66 19 is the WF-1000XM6 noise control state; 66 17 covers older models.
        // Which one a model answers is a protocol capability rather than a form
        // factor, so unlike the per-earbud battery levels it cannot be inferred
        // from the device name: both go out until one of them replies. Only the
        // answering type is asked for after that, because an inquired type a
        // model does not implement is acked and then never answered, so leaving
        // both in costs a full --timeout on every refresh, forever.
        private string BuildStateBatchCommand(DeviceProfile profile)
        {
            string battery = profile != null && profile.HasEarbudBatteries
                ? "22 00;22 01;22 02;"
                : "22 00;";
            string ncasm = ncasmTypeSeen == 0x19 ? "66 19;"
                : ncasmTypeSeen == 0x17 ? "66 17;"
                : "66 19;66 17;";
            return "batch \"" + battery + "12 00;" + ncasm + StateBatchTail;
        }

        private readonly string backendPath;
        private readonly DeviceProfile[] profiles;
        private readonly ShortcutAction[] shortcutActions;
        private readonly Dictionary<string, ShortcutBinding> shortcutBindings = new Dictionary<string, ShortcutBinding>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, ShortcutAction> registeredShortcuts = new Dictionary<int, ShortcutAction>();
        private readonly Dictionary<int, int[]> eqBandCache = new Dictionary<int, int[]>();
        private readonly NotifyIcon trayIcon;
        private readonly ContextMenuStrip trayMenu;
        private readonly Icon windowIcon;
        private readonly Icon notificationIcon;
        private readonly ToolTip toolTip = new ToolTip();
        private readonly SemaphoreSlim commandGate = new SemaphoreSlim(1, 1);
        private readonly System.Windows.Forms.Timer autoDetectTimer;
        private readonly System.Windows.Forms.Timer liveEqTimer;

        private DeviceProfile currentProfile;
        private DeviceProfile lastStateRefreshProfile;
        private DateTime lastStateRefreshAt = DateTime.MinValue;
        private bool exiting;
        private bool minimizeToTray = true;
        private bool startMinimizedToTray;
        private bool hasShownOnce;
        private bool trayNotifications = true;
        private bool trayCleanupStarted;
        private bool commandBusy;
        private bool autoDetectRunning;
        private bool lastStateRefreshConnected;
        private bool immediateExitQueued;
        private DateTime lastBackendCommandAtUtc = DateTime.MinValue;

        private Label titleLabel;
        private Label statusLabel;
        private Label connectionLabel;
        private Label batteryLabel;
        private Label multipointLabel;
        private Label lastActionLabel;
        private Label bigModeLabel;
        private Label bigDetailLabel;
        private Label levelLabel;
        private Label eqCardSummaryLabel;
        private Label dseeLabel;
        private Label connectionQualityLabel;
        private Label codecLabel;
        // Whether the codec shown came from the headset, as opposed to the
        // row's "Waiting" or a note that it is not being reported.
        private bool codecReported;
        private PillButton lowLatencyButton;
        private bool lowLatencyActive;
        private const string LowLatencyHint = "Use Sound Connect to turn Low Latency on and off.";
        private Label speakToChatLabel;
        private Label wearPauseLabel;
        private Label touchPanelLabel;
        private Label autoPowerLabel;
        private Label shortcutsCaptionLabel;
        private Label shortcutsDescriptionLabel;
        private Panel shortcutsDivider;
        private PillButton multipointOnButton;
        private PillButton multipointOffButton;
        private PillButton soundQualityButton;
        private PillButton stableButton;
        private PillButton dseeAutoButton;
        private PillButton dseeOffButton;
        private PillButton speakOnButton;
        private PillButton speakOffButton;
        private PillButton pauseOnButton;
        private PillButton pauseOffButton;
        private PillButton touchOnButton;
        private PillButton touchOffButton;
        private PillButton autoPowerRemovedButton;
        private PillButton autoPowerDisableButton;
        private PillButton configureShortcutsButton;
        private GearButton appSettingsButton;
        private SliderControl levelSlider;
        private ChoiceDropdown ambientKindBox;
        private ChoiceDropdown sensitivityBox;
        private Label autoAmbientLabel;
        private PillButton autoAmbientOnButton;
        private PillButton autoAmbientOffButton;
        private bool? autoAmbientSupported;
        private bool updatingNoiseUi;
        private int ncasmTypeSeen;
        private PillButton ancButton;
        private PillButton ambientButton;
        private PillButton offButton;
        private ChoiceDropdown eqPresetBox;
        private SliderControl[] eqSliders;
        private Label[] eqValueLabels;
        private Label[] eqNameLabels;
        private CardPanel eqCard;
        private PillButton eqFlatButton;
        private PillButton eqCopyPresetButton;
        private EqProfile eqProfile = EqProfile.Legacy6Band();
        private bool eqLayoutKnown;
        private PictureBox heroImageBox;
        private int currentEqPreset = 0xA0;
        private bool updatingEqUi;
        private bool liveEqSendRunning;
        private bool liveEqSendPending;
        private int eqFetchGeneration;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int WmHotKey = 0x0312;
        private const int WmClose = 0x0010;
        private const int ShortcutHotkeyBaseId = 0x5300;
        private const int SwHide = 0;

        public MainForm()
        {
            backendPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "c", "xm5ctl.exe"));
            profiles = new[]
            {
                new DeviceProfile("WH-1000XM5", "WH-1000XM5", @"assets\wh1000xm5.png", "LE_WH-1000XM5", "WH1000XM5"),
                new DeviceProfile("WF-1000XM5", "WF-1000XM5", @"assets\wf1000xm5.png", "LE_WF-1000XM5", "WF1000XM5"),
                new DeviceProfile("WH-1000XM6", "WH-1000XM6", @"assets\wh1000xm6.png", "LE_WH-1000XM6", "WH1000XM6"),
                new DeviceProfile("WF-1000XM6", "WF-1000XM6", @"assets\wf1000xm6.png", "LE_WF-1000XM6", "WF1000XM6")
            };
            currentProfile = profiles[0];
            shortcutActions = CreateShortcutActions();
            LoadShortcutBindings();
            LoadAppPreferences();

            Text = AppTitle();
            // The layout has a smallest size below which cards would overlap, but
            // the window is not held to it: past that point the content scrolls
            // instead, so every control stays reachable in a small window or on
            // a small screen. The window opens at that size or the screen's,
            // whichever is smaller.
            MinimumSize = new Size(480, 360);
            AutoScroll = true;
            AutoScrollMinSize = new Size(ContentMinimumWidth, ContentMinimumHeight);
            ClientSize = new Size(1364, ContentMinimumHeight);
            PlaceOnScreen();
            if (startMinimizedToTray)
            {
                WindowState = FormWindowState.Minimized;
                ShowInTaskbar = false;
            }
            BackColor = page;
            ForeColor = ink;
            Font = new Font("Segoe UI", 10f);
            windowIcon = CreateWindowIcon();
            notificationIcon = CreateTrayIcon();
            Icon = windowIcon;
            DoubleBuffered = true;

            BuildUi();

            trayMenu = BuildTrayMenu();
            trayIcon = new NotifyIcon
            {
                Text = TrayTitle(),
                Icon = notificationIcon,
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            trayIcon.DoubleClick += (s, e) => ShowWindow();

            autoDetectTimer = new System.Windows.Forms.Timer { Interval = AutoDetectIntervalMs };
            autoDetectTimer.Tick += async (s, e) => await RunUiTaskAsync(AutoDetectTickAsync);
            liveEqTimer = new System.Windows.Forms.Timer { Interval = LiveEqDebounceMs };
            liveEqTimer.Tick += async (s, e) => await RunUiTaskAsync(async () =>
            {
                if (IsClosing) return;
                liveEqTimer.Stop();
                await FlushLiveEqualizerAsync();
            });

            Resize += (s, e) =>
            {
                if (!IsClosing && hasShownOnce && WindowState == FormWindowState.Minimized && minimizeToTray)
                {
                    ConcealMainWindow(removeFromTaskbar: true);
                    Notify("Still running in the tray.");
                }
            };
            Activated += async (s, e) =>
            {
                if (hasShownOnce) await RunUiTaskAsync(AutoDetectTickAsync);
            };
            FormClosing += OnFormClosing;
            Shown += async (s, e) =>
            {
                hasShownOnce = true;
                if (startMinimizedToTray)
                {
                    Hide();
                }
                await RunUiTaskAsync(RefreshAllAsync);
                if (!IsClosing) autoDetectTimer.Start();
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TryApplyWindows11Chrome();
            RegisterShortcutHotkeys();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (!RecreatingHandle)
            {
                UnregisterShortcutHotkeys();
            }
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmClose)
            {
                if (!exiting && minimizeToTray)
                {
                    HideToTrayFromClose();
                    return;
                }
                QueueImmediateExit();
                return;
            }

            if (m.Msg == WmHotKey)
            {
                ShortcutAction action;
                if (registeredShortcuts.TryGetValue(m.WParam.ToInt32(), out action))
                {
                    QueueShortcutAction(action.Id);
                    return;
                }
            }
            base.WndProc(ref m);
        }

        private bool IsClosing
        {
            get { return exiting || trayCleanupStarted || IsDisposed || Disposing; }
        }

        private bool CanUpdateUi
        {
            get { return !IsClosing && IsHandleCreated; }
        }

        private async Task RunUiTaskAsync(Func<Task> action)
        {
            if (action == null || IsClosing) return;
            try
            {
                await action();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
                if (!IsClosing) SetStatus("Operation failed", red);
            }
            catch
            {
                if (!IsClosing) SetStatus("Operation failed", red);
            }
        }

        private void PostToUi(Action action)
        {
            if (action == null || !CanUpdateUi) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (!IsClosing) action();
                }));
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(RootPadding),
                BackColor = page,
                ColumnCount = 2,
                RowCount = 2
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, HeaderRowHeight));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);
            // Scrolling moves what is already on screen rather than redrawing
            // it, and on a display Windows is scaling for this DPI-unaware app
            // the moved image lands a pixel off, leaving seams and clipped
            // letters behind. Redraw the lot instead.
            root.Move += (s, e) => root.Invalidate(true);

            var header = new Panel { Dock = DockStyle.Fill, BackColor = page };
            root.SetColumnSpan(header, 2);
            root.Controls.Add(header, 0, 0);

            titleLabel = new Label
            {
                Text = currentProfile.DisplayName,
                ForeColor = ink,
                Font = new Font("Segoe UI Semibold", 23f),
                AutoSize = true,
                Location = new Point(2, 1)
            };
            header.Controls.Add(titleLabel);

            statusLabel = new Label
            {
                Text = File.Exists(backendPath) ? "Ready" : "Backend not found",
                ForeColor = File.Exists(backendPath) ? subdued : red,
                Font = new Font("Segoe UI", 10f),
                AutoSize = true,
                Location = new Point(4, 43)
            };
            header.Controls.Add(statusLabel);

            appSettingsButton = new GearButton(Color.FromArgb(44, 48, 54), Color.FromArgb(61, 67, 76), Color.FromArgb(78, 86, 96))
            {
                Size = new Size(36, 36),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            appSettingsButton.Click += (s, e) => ConfigureAppSettings();
            toolTip.SetToolTip(appSettingsButton, "App settings");
            header.Controls.Add(appSettingsButton);

            Action layoutHeader = () =>
            {
                if (appSettingsButton != null && !appSettingsButton.IsDisposed)
                {
                    appSettingsButton.Location = new Point(header.Width - appSettingsButton.Width - 22, 16);
                }
            };
            header.Resize += (s, e) => layoutHeader();
            layoutHeader();

            var left = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = page,
                RowCount = 3,
                ColumnCount = 1,
                Margin = new Padding(0, 0, 8, 0)
            };
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, HeroRowHeight));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, NoiseControlRowHeight));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(left, 0, 1);

            var right = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = page,
                RowCount = 1,
                ColumnCount = 1,
                Margin = new Padding(8, 0, 0, 0)
            };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(right, 1, 1);

            var hero = CreateCard();
            var controls = CreateCard();
            var equalizer = CreateCard();
            var settingsHub = CreateCard();

            left.Controls.Add(hero, 0, 0);
            left.Controls.Add(controls, 0, 1);
            left.Controls.Add(equalizer, 0, 2);
            right.Controls.Add(settingsHub, 0, 0);

            BuildHero(hero);
            BuildControls(controls);
            BuildEqualizer(equalizer);
            BuildSettingsHub(settingsHub);
        }

        private void BuildHero(CardPanel parent)
        {
            // No "Current mode" caption above the mode: the mode names say what
            // they are, and the card is kept short so the window fits on a
            // 1080p screen.
            bigModeLabel = new Label
            {
                Text = "Unknown",
                ForeColor = ink,
                Font = new Font("Segoe UI Semibold", 29f),
                AutoSize = false,
                Location = new Point(CardInset - HeroModeOpticalOffset, 10),
                Size = new Size(420, 60),
                AutoEllipsis = true,
                UseCompatibleTextRendering = false
            };
            parent.Controls.Add(bigModeLabel);

            bigDetailLabel = new Label
            {
                Text = "Connect a supported headset to read its state",
                ForeColor = subdued,
                Font = new Font("Segoe UI", 10.5f),
                AutoSize = false,
                Location = new Point(CardInset, 70),
                Size = new Size(520, 28),
                AutoEllipsis = true
            };
            parent.Controls.Add(bigDetailLabel);

            lastActionLabel = new Label
            {
                Text = "No changes this session",
                ForeColor = subdued,
                Font = new Font("Segoe UI", 10f),
                AutoSize = false,
                Size = new Size(520, 28),
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Location = new Point(CardInset, HeroLastActionTop),
                AutoEllipsis = true
            };
            lastActionLabel.Height = 22;
            parent.Controls.Add(lastActionLabel);

            heroImageBox = new PictureBox
            {
                BackColor = card,
                Image = LoadImageAsset(currentProfile.AssetPath),
                Location = new Point(520, 23),
                Size = new Size(160, 120),
                SizeMode = PictureBoxSizeMode.Zoom,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            parent.Controls.Add(heroImageBox);

            Action layoutHero = () =>
            {
                bool showImage = heroImageBox.Image != null && parent.Width > 560;
                heroImageBox.Visible = showImage;
                if (showImage)
                {
                    int imageSize = Math.Min(160, Math.Max(96, parent.Height - 20));
                    heroImageBox.Size = new Size(imageSize, imageSize);
                    heroImageBox.Location = new Point(parent.Width - heroImageBox.Width - CardInset, Math.Max(10, (parent.Height - heroImageBox.Height) / 2));
                }
                int textRight = showImage ? heroImageBox.Left - 24 : parent.Width - CardInset;
                int textWidth = Math.Max(260, textRight - CardInset);
                bigModeLabel.Location = new Point(CardInset - HeroModeOpticalOffset, bigModeLabel.Top);
                bigModeLabel.Width = textWidth;
                bigDetailLabel.Width = textWidth;
                lastActionLabel.Width = textWidth;
                lastActionLabel.Location = new Point(CardInset, HeroLastActionTop);
            };
            parent.Resize += (s, e) => layoutHero();
            layoutHero();
        }

        private void BuildControls(CardPanel parent)
        {
            AddTitle(parent, "Noise Control", 20);

            ancButton = new PillButton("Noise cancelling", blue, bluePressed);
            ambientButton = new PillButton("Ambient sound", blue, bluePressed);
            offButton = new PillButton("Off", Color.FromArgb(65, 70, 78), Color.FromArgb(77, 83, 92));

            PlaceRow(parent, 68, 10, 42, ancButton, ambientButton, offButton);
            ancButton.Click += async (s, e) => await SetAncAsync();
            ambientButton.Click += async (s, e) => await SetAmbientAsync(levelSlider.Value, IsVoiceAmbientSelected());
            offButton.Click += async (s, e) => await SetOffAsync();

            AddDivider(parent, 140);

            var autoCaption = new Label
            {
                Text = "Auto ambient sound",
                ForeColor = ink,
                Font = new Font("Segoe UI Semibold", 10f),
                AutoEllipsis = true,
                Location = new Point(CardInset, 158),
                Size = new Size(220, 20)
            };
            parent.Controls.Add(autoCaption);

            autoAmbientLabel = new Label
            {
                Text = "Waiting",
                ForeColor = subdued,
                Font = new Font("Segoe UI", 9.2f),
                AutoEllipsis = true,
                Location = new Point(CardInset, 180),
                Size = new Size(220, 20)
            };
            parent.Controls.Add(autoAmbientLabel);

            autoAmbientOnButton = NewOptionButton("On");
            autoAmbientOffButton = NewOptionButton("Off");
            parent.Controls.Add(autoAmbientOnButton);
            parent.Controls.Add(autoAmbientOffButton);
            autoAmbientOnButton.Click += async (s, e) => await SetAutoAmbientAsync(true);
            autoAmbientOffButton.Click += async (s, e) => await SetAutoAmbientAsync(false);

            sensitivityBox = new ChoiceDropdown
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = cardSoft,
                ForeColor = ink,
                FlatStyle = FlatStyle.Flat,
                Width = 132
            };
            sensitivityBox.Items.AddRange(new object[] { "Low", "Standard", "High" });
            sensitivityBox.SelectedIndex = 1;
            sensitivityBox.SelectedIndexChanged += async (s, e) =>
            {
                if (updatingNoiseUi) return;
                await RunUiTaskAsync(() => SetAmbientSensitivityAsync(SelectedSensitivity()));
            };
            parent.Controls.Add(sensitivityBox);

            Action layoutAutoRow = () =>
            {
                int x = parent.Width - CardInset;
                PillButton[] pills = { autoAmbientOnButton, autoAmbientOffButton };
                for (int i = pills.Length - 1; i >= 0; i--)
                {
                    int width = OptionButtonWidth(pills[i]);
                    x -= width;
                    pills[i].Location = new Point(x, 166);
                    pills[i].Size = new Size(width, 32);
                    x -= 8;
                }

                sensitivityBox.Width = 132;
                x -= sensitivityBox.Width;
                sensitivityBox.Location = new Point(x, 166);

                int textWidth = Math.Max(120, x - CardInset - 12);
                autoCaption.Width = textWidth;
                autoAmbientLabel.Width = textWidth;
            };
            parent.Resize += (s, e) => layoutAutoRow();
            layoutAutoRow();

            AddDivider(parent, 208);

            var ambientLabel = new Label
            {
                Text = "Ambient sound",
                ForeColor = Color.FromArgb(190, 198, 207),
                Font = new Font("Segoe UI Semibold", 9.2f),
                AutoSize = true
            };
            parent.Controls.Add(ambientLabel);

            levelLabel = new Label
            {
                Text = "Level 12",
                ForeColor = Color.FromArgb(190, 198, 207),
                Font = new Font("Segoe UI Semibold", 10f),
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            parent.Controls.Add(levelLabel);

            ambientKindBox = new ChoiceDropdown
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = cardSoft,
                ForeColor = ink,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(CardInset, 188),
                Width = 160
            };
            ambientKindBox.Items.AddRange(new object[] { "Normal", "Voice" });
            ambientKindBox.SelectedIndex = 0;
            parent.Controls.Add(ambientKindBox);

            levelSlider = new SliderControl
            {
                // The headset clamps the ambient level to 1; asking for 0 comes
                // back as 1, so do not offer a value it will not accept.
                Minimum = 1,
                Maximum = 20,
                Value = 12,
                TickFrequency = 2,
                SmallChange = 1,
                LargeChange = 2,
                BackColor = card,
                TrackColor = Color.FromArgb(78, 84, 94),
                FillColor = blue,
                ThumbColor = blue,
                TickColor = Color.FromArgb(91, 98, 108),
                Location = new Point(CardInset, 224),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };
            parent.Controls.Add(levelSlider);

            var apply = new PillButton("Apply", blue, bluePressed);
            apply.Size = new Size(108, 34);
            apply.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            apply.Click += async (s, e) => await SetAmbientAsync(levelSlider.Value, IsVoiceAmbientSelected());
            parent.Controls.Add(apply);

            Action layoutAmbientControls = () =>
            {
                int sliderHeight = 34;
                int sliderVisibleBottom = sliderHeight / 2 + 12;
                int sliderTop = Math.Max(282, parent.Height - 22 - sliderVisibleBottom);
                int inputTop = sliderTop - 36;
                int labelTop = inputTop - 28;
                int right = parent.Width - CardInset;
                ambientLabel.Location = new Point(CardInset, labelTop);
                levelLabel.Location = new Point(right - levelLabel.Width, labelTop);
                apply.Location = new Point(right - apply.Width, inputTop);

                int dropdownRight = apply.Left - 18;
                ambientKindBox.Location = new Point(CardInset, inputTop);
                ambientKindBox.Width = Math.Max(150, Math.Min(220, dropdownRight - CardInset));

                levelSlider.Location = new Point(CardInset - 12, sliderTop);
                levelSlider.Size = new Size(Math.Max(220, parent.Width - (CardInset * 2) + 24), sliderHeight);
            };
            parent.Resize += (s, e) => layoutAmbientControls();
            layoutAmbientControls();
            levelSlider.ValueChanged += (s, e) =>
            {
                levelLabel.Text = "Level " + levelSlider.Value;
                layoutAmbientControls();
            };
        }

        private void BuildEqualizer(CardPanel parent)
        {
            eqCard = parent;
            AddTitle(parent, "Equalizer", 18);

            eqCardSummaryLabel = new Label
            {
                Text = "Waiting",
                ForeColor = subdued,
                Font = new Font("Segoe UI", 10f),
                AutoEllipsis = true,
                Location = new Point(CardInset, 46),
                Size = new Size(320, 22)
            };
            parent.Controls.Add(eqCardSummaryLabel);

            eqPresetBox = new ChoiceDropdown
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = cardSoft,
                ForeColor = ink,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(CardInset, EqPresetRowTop),
                Width = 188
            };
            PopulateEqPresetBox();
            eqPresetBox.SelectedIndexChanged += async (s, e) =>
            {
                if (updatingEqUi) return;
                var choice = eqPresetBox.SelectedItem as EqPresetChoice;
                if (choice == null) return;
                await RunUiTaskAsync(() => SendEqualizerPresetAsync(choice.Value));
            };
            parent.Controls.Add(eqPresetBox);

            var flat = new PillButton("Flat", Color.FromArgb(65, 70, 78), Color.FromArgb(77, 83, 92));
            flat.Click += async (s, e) => await SetEqualizerFlatAsync();
            parent.Controls.Add(flat);

            // Not "Save": every slider move is already written to the headset.
            // This copies the curve on screen into one of the two custom slots.
            var copyPreset = new PillButton("Copy to...", blue, bluePressed);
            copyPreset.Click += (s, e) => ShowEqualizerCopyMenu(copyPreset);
            parent.Controls.Add(copyPreset);

            eqFlatButton = flat;
            eqCopyPresetButton = copyPreset;
            RebuildEqualizerBands();

            parent.Resize += (s, e) => LayoutEqualizer();
        }

        private void PopulateEqPresetBox()
        {
            if (eqPresetBox == null) return;
            updatingEqUi = true;
            try
            {
                eqPresetBox.Items.Clear();
                foreach (var choice in eqProfile.Presets) eqPresetBox.Items.Add(choice);
                if (!SelectEqPreset(currentEqPreset) && eqPresetBox.Items.Count > 0)
                {
                    eqPresetBox.SelectedIndex = 0;
                }
            }
            finally
            {
                updatingEqUi = false;
            }
        }

        // The sliders are rebuilt whenever the device reports a band layout that
        // differs from the one on screen, so the count and the labels always come
        // from the capability reply rather than from a hardcoded six.
        private void RebuildEqualizerBands()
        {
            if (eqCard == null) return;

            if (eqSliders != null)
            {
                for (int i = 0; i < eqSliders.Length; i++)
                {
                    DisposeBandControl(eqSliders[i]);
                    if (eqValueLabels != null && i < eqValueLabels.Length) DisposeBandControl(eqValueLabels[i]);
                    if (eqNameLabels != null && i < eqNameLabels.Length) DisposeBandControl(eqNameLabels[i]);
                }
            }

            int count = eqProfile.BandCount;
            eqSliders = new SliderControl[count];
            eqValueLabels = new Label[count];
            eqNameLabels = new Label[count];

            for (int i = 0; i < count; i++)
            {
                var name = new Label
                {
                    Text = eqProfile.Labels[i],
                    ForeColor = i == 0 && eqProfile.FirstBandIsClearBass ? blue : subdued,
                    Font = new Font("Segoe UI Semibold", 8.8f),
                    AutoEllipsis = true,
                    TextAlign = ContentAlignment.MiddleCenter
                };
                eqCard.Controls.Add(name);
                eqNameLabels[i] = name;

                var value = new Label
                {
                    Text = eqProfile.FormatValue(eqProfile.Neutral),
                    ForeColor = ink,
                    Font = new Font("Segoe UI Semibold", 10f),
                    TextAlign = ContentAlignment.MiddleCenter
                };
                eqCard.Controls.Add(value);
                eqValueLabels[i] = value;

                var slider = new SliderControl
                {
                    Minimum = 0,
                    Maximum = eqProfile.MaxValue,
                    Value = eqProfile.Neutral,
                    TickFrequency = Math.Max(1, eqProfile.MaxValue / 4),
                    SmallChange = 1,
                    LargeChange = 2,
                    BackColor = card,
                    TrackColor = Color.FromArgb(78, 84, 94),
                    FillColor = blue,
                    ThumbColor = blue,
                    TickColor = Color.FromArgb(91, 98, 108),
                    CenteredFill = true,
                    Vertical = true
                };
                slider.ValueChanged += (s, e) => HandleEqualizerSliderChanged((SliderControl)s);
                eqCard.Controls.Add(slider);
                eqSliders[i] = slider;
            }

            UpdateEqControlsEnabled();
            LayoutEqualizer();
        }

        private void DisposeBandControl(Control control)
        {
            if (control == null) return;
            if (eqCard != null) eqCard.Controls.Remove(control);
            control.Dispose();
        }

        private void LayoutEqualizer()
        {
            if (eqCard == null || eqSliders == null) return;
            if (eqFlatButton == null || eqCopyPresetButton == null || eqPresetBox == null) return;

            eqFlatButton.Size = new Size(74, 34);
            eqFlatButton.Location = new Point(eqCard.Width - eqFlatButton.Width - CardInset, EqPresetRowTop);
            eqCopyPresetButton.Size = new Size(98, 34);
            eqCopyPresetButton.Location = new Point(eqFlatButton.Left - eqCopyPresetButton.Width - 10, EqPresetRowTop);
            if (eqCardSummaryLabel != null) eqCardSummaryLabel.Width = Math.Max(260, eqCard.Width - (CardInset * 2));
            eqPresetBox.Location = new Point(CardInset, EqPresetRowTop);
            eqPresetBox.Width = Math.Max(150, Math.Min(220, eqCopyPresetButton.Left - CardInset - 12));

            // One column per band, value above the fader and name below it.
            int count = eqSliders.Length;
            if (count == 0) return;
            int availableWidth = Math.Max(count * 48, eqCard.Width - (CardInset * 2));
            int faderTop = EqBandTop + EqBandLabelHeight;
            int faderHeight = Math.Max(EqFaderMinHeight, eqCard.Height - faderTop - EqBandLabelHeight - EqCardBottomInset);
            for (int i = 0; i < count; i++)
            {
                int left = CardInset + (availableWidth * i / count);
                int width = CardInset + (availableWidth * (i + 1) / count) - left;

                eqValueLabels[i].Location = new Point(left, EqBandTop);
                eqValueLabels[i].Size = new Size(width, EqBandLabelHeight);
                eqSliders[i].Location = new Point(left + (width - 32) / 2, faderTop);
                eqSliders[i].Size = new Size(32, faderHeight);
                eqNameLabels[i].Location = new Point(left, faderTop + faderHeight);
                eqNameLabels[i].Size = new Size(width, EqBandLabelHeight);
            }
        }

        // The smallest client area that shows both columns without clipping;
        // below it the window scrolls.
        private static int ContentMinimumHeight
        {
            get
            {
                int left = HeroRowHeight + NoiseControlRowHeight + EqualizerCardMinimumHeight + (CardMargin * 2);
                int right = ShortcutsRowMinimumTop + ShortcutsRowAllowance + (CardMargin * 2);
                return (RootPadding * 2) + HeaderRowHeight + Math.Max(left, right);
            }
        }

        // Centred on the screen the pointer is on, and shrunk to its working
        // area first if need be, so a small screen never has the title bar or
        // the bottom edge out of reach.
        private void PlaceOnScreen()
        {
            StartPosition = FormStartPosition.Manual;
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            int width = Math.Min(Width, area.Width);
            int height = Math.Min(Height, area.Height);
            Bounds = new Rectangle(
                area.Left + (area.Width - width) / 2,
                area.Top + (area.Height - height) / 2,
                width,
                height);
        }

        // The smallest card that shows every band's fader at its minimum height.
        private static int EqualizerCardMinimumHeight
        {
            get { return EqBandTop + EqBandLabelHeight + EqFaderMinHeight + EqBandLabelHeight + EqCardBottomInset; }
        }

        private void BuildSettingsHub(CardPanel parent)
        {
            AddTitle(parent, "Settings", 24);

            AddEyebrow(parent, "Device", 62);
            connectionLabel = AddActionRow(parent, "Connection", "Waiting", 84);
            batteryLabel = AddActionRow(parent, "Battery", "Waiting", 128);
            codecLabel = AddActionRow(parent, "Codec", "Waiting", 172);
            soundQualityButton = NewOptionButton("Quality");
            stableButton = NewOptionButton("Stability");
            lowLatencyButton = NewOptionButton("Low Latency");
            // Shortened from "Bluetooth connection quality": the third button
            // leaves too little room, and everything in this card is Bluetooth.
            connectionQualityLabel = AddActionRow(parent, "Connection quality", "Waiting", 216, soundQualityButton, stableButton, lowLatencyButton);
            soundQualityButton.Click += async (s, e) => await SetConnectionQualityAsync(true);
            stableButton.Click += async (s, e) => await SetConnectionQualityAsync(false);
            // Shown so the setting is visible, never sent. Low latency is not a
            // headset setting alone: it also needs LE Audio enabled for the
            // headset in the operating system's Bluetooth settings, which this app
            // cannot do. Left enabled because a disabled control gets no mouse
            // events and so could not show the tooltip explaining that.
            lowLatencyButton.Click += (s, e) => lastActionLabel.Text = LowLatencyHint;
            toolTip.SetToolTip(lowLatencyButton, LowLatencyHint);

            multipointOnButton = NewOptionButton("On");
            multipointOffButton = NewOptionButton("Off");
            multipointLabel = AddActionRow(parent, "Multipoint support", "Waiting", 266, multipointOnButton, multipointOffButton);
            multipointOnButton.Click += async (s, e) => await SetMultipointAsync(true);
            multipointOffButton.Click += async (s, e) => await SetMultipointAsync(false);

            AddDivider(parent, 322);
            AddEyebrow(parent, "Sound", 344);

            dseeAutoButton = NewOptionButton("Auto");
            dseeOffButton = NewOptionButton("Off");
            dseeLabel = AddActionRow(parent, "DSEE Extreme", "Waiting", 366, dseeAutoButton, dseeOffButton);
            dseeAutoButton.Click += async (s, e) => await SetDseeAsync(true);
            dseeOffButton.Click += async (s, e) => await SetDseeAsync(false);

            AddDivider(parent, 422);
            AddEyebrow(parent, "Controls", 444);
            speakOnButton = NewOptionButton("On");
            speakOffButton = NewOptionButton("Off");
            speakToChatLabel = AddActionRow(parent, "Speak-to-Chat", "Waiting", 466, speakOnButton, speakOffButton);
            speakOnButton.Click += async (s, e) => await SetSpeakToChatAsync(true);
            speakOffButton.Click += async (s, e) => await SetSpeakToChatAsync(false);

            pauseOnButton = NewOptionButton("On");
            pauseOffButton = NewOptionButton("Off");
            wearPauseLabel = AddActionRow(parent, "Pause when headphones are removed", "Waiting", 514, pauseOnButton, pauseOffButton);
            pauseOnButton.Click += async (s, e) => await SetWearPauseAsync(true);
            pauseOffButton.Click += async (s, e) => await SetWearPauseAsync(false);

            touchOnButton = NewOptionButton("On");
            touchOffButton = NewOptionButton("Off");
            touchPanelLabel = AddActionRow(parent, "Touch sensor control panel", "Waiting", 562, touchOnButton, touchOffButton);
            touchOnButton.Click += async (s, e) => await SetTouchPanelAsync(true);
            touchOffButton.Click += async (s, e) => await SetTouchPanelAsync(false);

            AddDivider(parent, 618);
            AddEyebrow(parent, "Power", 640);
            autoPowerRemovedButton = NewOptionButton("On");
            autoPowerDisableButton = NewOptionButton("Off");
            autoPowerLabel = AddActionRow(parent, "Automatic power off", "Waiting", 662, autoPowerRemovedButton, autoPowerDisableButton);
            autoPowerRemovedButton.Click += async (s, e) => await SetAutoPowerRemovedAsync();
            autoPowerDisableButton.Click += async (s, e) => await SetAutoPowerDisabledAsync();

            shortcutsDivider = new Panel
            {
                BackColor = line,
                Height = 1
            };
            parent.Controls.Add(shortcutsDivider);
            shortcutsCaptionLabel = new Label
            {
                Text = "Keyboard shortcuts",
                ForeColor = ink,
                Font = new Font("Segoe UI Semibold", 10f),
                AutoEllipsis = true
            };
            parent.Controls.Add(shortcutsCaptionLabel);
            shortcutsDescriptionLabel = new Label
            {
                Text = "Configure app shortcuts",
                ForeColor = subdued,
                Font = new Font("Segoe UI", 9.2f),
                AutoEllipsis = true
            };
            parent.Controls.Add(shortcutsDescriptionLabel);
            configureShortcutsButton = NewOptionButton("Shortcuts");
            configureShortcutsButton.Click += (s, e) => ConfigureShortcuts();
            parent.Controls.Add(configureShortcutsButton);
            parent.Resize += (s, e) => LayoutShortcutsRow(parent);
            LayoutShortcutsRow(parent);
            UpdateShortcutsLabel();

            SetModeButtonState("Ambient");
            SetAutoAmbientWaiting();
            SetMultipointState(null);
            SetConnectionQualityState(null);
            SetDseeState(null);
            SetSpeakToChatState(null);
            SetWearPauseState(null);
            SetTouchPanelState(null);
            SetAutoPowerState(null);
        }

        private PillButton NewOptionButton(string caption)
        {
            var button = new PillButton(caption, InactiveButtonColor(), PressedColor(InactiveButtonColor()));
            return button;
        }

        private void LayoutShortcutsRow(Control parent)
        {
            if (parent == null) return;
            int dividerTop = Math.Max(ShortcutsRowMinimumTop, parent.Height - ShortcutsRowAllowance);
            if (shortcutsDivider != null && !shortcutsDivider.IsDisposed)
            {
                shortcutsDivider.Location = new Point(CardInset, dividerTop);
                shortcutsDivider.Width = Math.Max(80, parent.Width - (CardInset * 2));
            }

            int rowTop = dividerTop + 26;
            int buttonWidth = configureShortcutsButton == null ? 108 : Math.Max(108, OptionButtonWidth(configureShortcutsButton));
            int textWidth = Math.Max(140, parent.Width - buttonWidth - (CardInset * 2 + 16));
            if (shortcutsCaptionLabel != null && !shortcutsCaptionLabel.IsDisposed)
            {
                shortcutsCaptionLabel.Location = new Point(CardInset, rowTop);
                shortcutsCaptionLabel.Size = new Size(textWidth, 20);
            }
            if (shortcutsDescriptionLabel != null && !shortcutsDescriptionLabel.IsDisposed)
            {
                shortcutsDescriptionLabel.Location = new Point(CardInset, rowTop + 22);
                shortcutsDescriptionLabel.Size = new Size(textWidth, 20);
            }
            if (configureShortcutsButton != null && !configureShortcutsButton.IsDisposed)
            {
                configureShortcutsButton.Size = new Size(buttonWidth, 32);
                configureShortcutsButton.Location = new Point(parent.Width - buttonWidth - CardInset, rowTop + 8);
            }
        }

        private void SetModeButtonState(string mode)
        {
            bool anc = string.Equals(mode, "ANC", StringComparison.OrdinalIgnoreCase);
            bool ambient = string.Equals(mode, "Ambient", StringComparison.OrdinalIgnoreCase);
            bool off = string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase);
            SetButtonSelected(ancButton, anc, blue);
            SetButtonSelected(ambientButton, ambient, blue);
            SetButtonSelected(offButton, off, blue);
        }

        private void SetMultipointState(bool? enabled)
        {
            if (multipointLabel != null) multipointLabel.Text = enabled.HasValue ? (enabled.Value ? "On" : "Off") : "Waiting";
            SetOptionPair(multipointOnButton, multipointOffButton, enabled, blue, blue);
        }

        private void SetConnectionQualityState(bool? prioritizeSound)
        {
            if (connectionQualityLabel != null) connectionQualityLabel.Text = prioritizeSound.HasValue ? (prioritizeSound.Value ? "Quality" : "Stability") : "Waiting";
            SetOptionPair(soundQualityButton, stableButton, prioritizeSound, blue, blue);
            SetButtonSelected(lowLatencyButton, false, blue);
            lowLatencyActive = false;
            toolTip.SetToolTip(soundQualityButton, null);
            toolTip.SetToolTip(stableButton, null);
        }

        /*
         * Low latency is a third setting that the E6 00 query cannot report: it
         * answers 0 both for prioritised sound quality and for low latency. Only
         * the device information blob distinguishes them. Selecting it in Sony's
         * app requires unpairing and re-pairing the headset, so the buttons are
         * disabled rather than offering a one-click way out of a state that is
         * laborious to get back into.
         */
        private void SetConnectionQualityLowLatency()
        {
            if (connectionQualityLabel != null) connectionQualityLabel.Text = "Low Latency (LE Audio)";
            SetOptionPair(soundQualityButton, stableButton, null, blue, blue);
            SetButtonSelected(lowLatencyButton, true, blue);
            // Sound Connect owns this setting in both directions, so the app will
            // not switch out of low latency either. The buttons stay enabled and
            // are made inert instead: a disabled control gets no mouse events, so
            // clicking one would do nothing without saying why. The tray actions
            // and keyboard shortcuts reach the same guard in SetConnectionQualityAsync.
            lowLatencyActive = true;
            toolTip.SetToolTip(soundQualityButton, LowLatencyHint);
            toolTip.SetToolTip(stableButton, LowLatencyHint);
        }

        private void SetDseeState(bool? auto)
        {
            if (dseeLabel != null) dseeLabel.Text = auto.HasValue ? (auto.Value ? "Auto" : "Off") : "Waiting";
            SetOptionPair(dseeAutoButton, dseeOffButton, auto, blue, blue);
        }

        private void SetSpeakToChatState(bool? enabled)
        {
            if (speakToChatLabel != null) speakToChatLabel.Text = enabled.HasValue ? (enabled.Value ? "On" : "Off") : "Waiting";
            SetOptionPair(speakOnButton, speakOffButton, enabled, blue, blue);
        }

        private void SetWearPauseState(bool? enabled)
        {
            if (wearPauseLabel != null) wearPauseLabel.Text = enabled.HasValue ? (enabled.Value ? "On" : "Off") : "Waiting";
            SetOptionPair(pauseOnButton, pauseOffButton, enabled, blue, blue);
        }

        private void SetTouchPanelState(bool? enabled)
        {
            if (touchPanelLabel != null) touchPanelLabel.Text = enabled.HasValue ? (enabled.Value ? "On" : "Off") : "Waiting";
            SetOptionPair(touchOnButton, touchOffButton, enabled, blue, blue);
        }

        private void SetAutoPowerState(string state)
        {
            bool removed = string.Equals(state, "removed", StringComparison.OrdinalIgnoreCase);
            bool disabled = string.Equals(state, "disabled", StringComparison.OrdinalIgnoreCase);
            if (autoPowerLabel != null)
            {
                if (removed) autoPowerLabel.Text = "On";
                else if (disabled) autoPowerLabel.Text = "Off";
                else autoPowerLabel.Text = "Waiting";
            }
            SetButtonSelected(autoPowerRemovedButton, removed, blue);
            SetButtonSelected(autoPowerDisableButton, disabled, blue);
        }

        private void SetAutoPowerCustomState(string text)
        {
            if (autoPowerLabel != null) autoPowerLabel.Text = text;
            SetButtonSelected(autoPowerRemovedButton, false, blue);
            SetButtonSelected(autoPowerDisableButton, false, blue);
        }

        private void SetOptionPair(PillButton first, PillButton second, bool? firstSelected, Color firstColor, Color secondColor)
        {
            if (!firstSelected.HasValue)
            {
                SetButtonSelected(first, false, firstColor);
                SetButtonSelected(second, false, secondColor);
                return;
            }
            SetButtonSelected(first, firstSelected.Value, firstColor);
            SetButtonSelected(second, !firstSelected.Value, secondColor);
        }

        private void SetButtonSelected(PillButton button, bool selected, Color selectedColor)
        {
            if (button == null) return;
            Color fill = selected ? selectedColor : InactiveButtonColor();
            button.SetPalette(fill, PressedColor(fill));
        }

        private static Color InactiveButtonColor()
        {
            return Color.FromArgb(61, 67, 76);
        }

        private static Color PressedColor(Color color)
        {
            return BlendColor(color, Color.Black, 0.18f);
        }

        private static Color BlendColor(Color a, Color b, float amount)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * amount),
                (int)(a.G + (b.G - a.G) * amount),
                (int)(a.B + (b.B - a.B) * amount));
        }

        private ContextMenuStrip BuildTrayMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open window", null, (s, e) => ShowWindow());
            menu.Items.Add("App settings...", null, (s, e) => ConfigureAppSettings());
            menu.Items.Add(new ToolStripSeparator());
            AddTrayAction(menu.Items, "Noise cancelling", SetAncAsync);
            AddTrayAction(menu.Items, "Ambient sound: 12", () => SetAmbientAsync(12));
            AddTrayAction(menu.Items, "Ambient sound: 20", () => SetAmbientAsync(20));
            AddTrayAction(menu.Items, "Ambient sound: Auto", () => SetAutoAmbientAsync(true));
            AddTrayAction(menu.Items, "Ambient sound: Manual", () => SetAutoAmbientAsync(false));
            AddTrayAction(menu.Items, "Noise control: Off", SetOffAsync);
            menu.Items.Add(new ToolStripSeparator());
            AddTrayAction(menu.Items, "Equalizer: Manual", () => SendEqualizerPresetAsync(0xA0));
            AddTrayAction(menu.Items, "Equalizer: User 1", () => SendEqualizerPresetAsync(0xA1));
            AddTrayAction(menu.Items, "Equalizer: User 2", () => SendEqualizerPresetAsync(0xA2));
            AddTrayAction(menu.Items, "Equalizer: Flat", SetEqualizerFlatAsync);
            AddTrayAction(menu.Items, "Bluetooth connection quality: Quality", () => SetConnectionQualityAsync(true));
            AddTrayAction(menu.Items, "Bluetooth connection quality: Stability", () => SetConnectionQualityAsync(false));
            AddTrayAction(menu.Items, "DSEE Extreme: Auto", () => SetDseeAsync(true));
            AddTrayAction(menu.Items, "DSEE Extreme: Off", () => SetDseeAsync(false));
            AddTrayAction(menu.Items, "Multipoint support: On", () => SetMultipointAsync(true));
            AddTrayAction(menu.Items, "Multipoint support: Off", () => SetMultipointAsync(false));
            menu.Items.Add(new ToolStripSeparator());
            AddTrayAction(menu.Items, "Speak-to-Chat: On", () => SetSpeakToChatAsync(true));
            AddTrayAction(menu.Items, "Speak-to-Chat: Off", () => SetSpeakToChatAsync(false));
            AddTrayAction(menu.Items, "Pause when headphones are removed: On", () => SetWearPauseAsync(true));
            AddTrayAction(menu.Items, "Pause when headphones are removed: Off", () => SetWearPauseAsync(false));
            AddTrayAction(menu.Items, "Touch sensor control panel: On", () => SetTouchPanelAsync(true));
            AddTrayAction(menu.Items, "Touch sensor control panel: Off", () => SetTouchPanelAsync(false));
            AddTrayAction(menu.Items, "Automatic power off: On", SetAutoPowerRemovedAsync);
            AddTrayAction(menu.Items, "Automatic power off: Off", SetAutoPowerDisabledAsync);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Configure shortcuts...", null, (s, e) => ConfigureShortcuts());

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) =>
            {
                PostToUi(QueueImmediateExit);
            });
            return menu;
        }

        private ToolStripMenuItem AddTrayAction(ToolStripItemCollection items, string text, Func<Task> action)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += async (s, e) => await RunUiTaskAsync(action);
            items.Add(item);
            return item;
        }

        private string AppTitle()
        {
            return "XM Control";
        }

        private string TrayTitle()
        {
            string title = AppTitle();
            return title.Length <= 63 ? title : title.Substring(0, 63);
        }

        private string WithDevice(string args)
        {
            return args + " --name \"" + currentProfile.NameFilter.Replace("\"", "") + "\"";
        }

        private static ShortcutAction[] CreateShortcutActions()
        {
            return new[]
            {
                new ShortcutAction("open", "Open window"),
                new ShortcutAction("anc", "Noise cancelling"),
                new ShortcutAction("ambient12", "Ambient sound: 12"),
                new ShortcutAction("ambient20", "Ambient sound: 20"),
                new ShortcutAction("ambientauto", "Ambient sound: Auto"),
                new ShortcutAction("ambientmanual", "Ambient sound: Manual"),
                new ShortcutAction("off", "Noise control: Off"),
                new ShortcutAction("eqmanual", "Equalizer: Manual"),
                new ShortcutAction("equser1", "Equalizer: User 1"),
                new ShortcutAction("equser2", "Equalizer: User 2"),
                new ShortcutAction("eqflat", "Equalizer: Flat"),
                new ShortcutAction("soundquality", "Bluetooth connection quality: Quality"),
                new ShortcutAction("stable", "Bluetooth connection quality: Stability"),
                new ShortcutAction("dseeauto", "DSEE Extreme: Auto"),
                new ShortcutAction("dseeoff", "DSEE Extreme: Off"),
                new ShortcutAction("multipointon", "Multipoint support: On"),
                new ShortcutAction("multipointoff", "Multipoint support: Off"),
                new ShortcutAction("speakon", "Speak-to-Chat: On"),
                new ShortcutAction("speakoff", "Speak-to-Chat: Off"),
                new ShortcutAction("pauseon", "Pause when headphones are removed: On"),
                new ShortcutAction("pauseoff", "Pause when headphones are removed: Off"),
                new ShortcutAction("touchon", "Touch sensor control panel: On"),
                new ShortcutAction("touchoff", "Touch sensor control panel: Off"),
                new ShortcutAction("autopowerremoved", "Automatic power off: On"),
                new ShortcutAction("autopowerdisabled", "Automatic power off: Off")
            };
        }

        private static string AppConfigDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SonyHeadphonesControl");
        }

        private static string ShortcutConfigPath()
        {
            return Path.Combine(AppConfigDirectory(), "shortcuts.txt");
        }

        private static string AppSettingsConfigPath()
        {
            return Path.Combine(AppConfigDirectory(), "app-settings.txt");
        }

        private static string StartupShortcutPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "XM Control.lnk");
        }

        private static string LegacyStartupShortcutPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Sony XM Control.lnk");
        }

        private void LoadAppPreferences()
        {
            minimizeToTray = true;
            startMinimizedToTray = false;
            string path = AppSettingsConfigPath();
            if (!File.Exists(path)) return;

            try
            {
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string lineText = lines[i];
                    if (string.IsNullOrWhiteSpace(lineText)) continue;
                    int equals = lineText.IndexOf('=');
                    if (equals <= 0) continue;
                    string key = lineText.Substring(0, equals).Trim();
                    string value = lineText.Substring(equals + 1).Trim();
                    if (string.Equals(key, "StartMinimizedInTray", StringComparison.OrdinalIgnoreCase))
                    {
                        startMinimizedToTray = ParseBool(value);
                    }
                    else if (string.Equals(key, "MinimizeToTrayOnClose", StringComparison.OrdinalIgnoreCase))
                    {
                        minimizeToTray = ParseBool(value);
                    }
                }
            }
            catch
            {
            }
        }

        private void SaveAppPreferences()
        {
            try
            {
                string path = AppSettingsConfigPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, new[]
                {
                    "StartMinimizedInTray=" + (startMinimizedToTray ? "true" : "false"),
                    "MinimizeToTrayOnClose=" + (minimizeToTray ? "true" : "false")
                });
            }
            catch
            {
                SetStatus("Could not save app settings", red);
            }
        }

        private static bool ParseBool(string value)
        {
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsStartAtBootEnabled()
        {
            return File.Exists(StartupShortcutPath()) || File.Exists(LegacyStartupShortcutPath());
        }

        private bool SetStartAtBoot(bool enabled)
        {
            try
            {
                string shortcutPath = StartupShortcutPath();
                if (!enabled)
                {
                    if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
                    string legacyShortcutPath = LegacyStartupShortcutPath();
                    if (File.Exists(legacyShortcutPath)) File.Delete(legacyShortcutPath);
                    return true;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath));
                string oldShortcutPath = LegacyStartupShortcutPath();
                if (File.Exists(oldShortcutPath)) File.Delete(oldShortcutPath);
                object shell = null;
                object shortcut = null;
                try
                {
                    Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                    if (shellType == null) return false;
                    shell = Activator.CreateInstance(shellType);
                    shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                    Type shortcutType = shortcut.GetType();
                    shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { Application.ExecutablePath });
                    shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { AppDomain.CurrentDomain.BaseDirectory });
                    shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "XM Control" });
                    shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { Application.ExecutablePath + ",0" });
                    shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                    return true;
                }
                finally
                {
                    ReleaseComObject(shortcut);
                    ReleaseComObject(shell);
                }
            }
            catch
            {
                return false;
            }
        }

        private static void ReleaseComObject(object value)
        {
            if (value == null) return;
            try
            {
                if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
            }
            catch
            {
            }
        }

        private void LoadShortcutBindings()
        {
            shortcutBindings.Clear();
            for (int i = 0; i < shortcutActions.Length; i++)
            {
                shortcutBindings[shortcutActions[i].Id] = ShortcutBinding.None();
            }

            string path = ShortcutConfigPath();
            if (!File.Exists(path)) return;

            try
            {
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string lineText = lines[i];
                    if (string.IsNullOrWhiteSpace(lineText)) continue;
                    int equals = lineText.IndexOf('=');
                    if (equals <= 0) continue;
                    string id = lineText.Substring(0, equals).Trim();
                    string value = lineText.Substring(equals + 1).Trim();
                    if (shortcutBindings.ContainsKey(id))
                    {
                        shortcutBindings[id] = ShortcutBinding.Parse(value);
                    }
                }
            }
            catch
            {
            }
        }

        private void SaveShortcutBindings()
        {
            try
            {
                string path = ShortcutConfigPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var lines = new List<string>();
                for (int i = 0; i < shortcutActions.Length; i++)
                {
                    ShortcutBinding binding = ShortcutBinding.None();
                    shortcutBindings.TryGetValue(shortcutActions[i].Id, out binding);
                    lines.Add(shortcutActions[i].Id + "=" + (binding == null ? "None" : binding.ToConfigString()));
                }
                File.WriteAllLines(path, lines.ToArray());
            }
            catch
            {
                SetStatus("Could not save shortcuts", red);
            }
        }

        private void ConfigureAppSettings()
        {
            if (IsClosing) return;
            if (!Visible || WindowState == FormWindowState.Minimized) ShowWindow();

            bool startAtBoot = IsStartAtBootEnabled();
            using (var dialog = new AppSettingsDialog(startAtBoot, startMinimizedToTray, minimizeToTray, page, card, cardSoft, line, ink, subdued, blue, bluePressed))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (!SetStartAtBoot(dialog.StartAtBoot))
                {
                    SetStatus("Could not update startup setting", red);
                    MessageBox.Show("Windows did not allow the startup shortcut to be updated.", "App settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                startMinimizedToTray = dialog.StartMinimizedInTray;
                minimizeToTray = dialog.MinimizeToTrayOnClose;
                SaveAppPreferences();
                if (lastActionLabel != null && !lastActionLabel.IsDisposed) lastActionLabel.Text = "App settings saved";
                SetStatus("App settings saved", subdued);
            }
        }

        private void ConfigureShortcuts()
        {
            if (IsClosing) return;
            if (!Visible || WindowState == FormWindowState.Minimized) ShowWindow();
            using (var dialog = new ShortcutDialog(shortcutActions, shortcutBindings, page, card, cardSoft, line, ink, subdued, blue, bluePressed))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                shortcutBindings.Clear();
                Dictionary<string, ShortcutBinding> next = dialog.Bindings;
                for (int i = 0; i < shortcutActions.Length; i++)
                {
                    ShortcutBinding binding;
                    if (!next.TryGetValue(shortcutActions[i].Id, out binding) || binding == null)
                    {
                        binding = ShortcutBinding.None();
                    }
                    shortcutBindings[shortcutActions[i].Id] = binding.Clone();
                }
            }

            SaveShortcutBindings();
            RegisterShortcutHotkeys();
            UpdateShortcutsLabel();
        }

        private int AssignedShortcutCount()
        {
            int assigned = 0;
            for (int i = 0; i < shortcutActions.Length; i++)
            {
                ShortcutBinding binding;
                if (shortcutBindings.TryGetValue(shortcutActions[i].Id, out binding) && binding != null && binding.IsAssigned)
                {
                    assigned++;
                }
            }
            return assigned;
        }

        private void UpdateShortcutsLabel()
        {
            if (configureShortcutsButton == null || configureShortcutsButton.IsDisposed) return;
            int assigned = AssignedShortcutCount();
            configureShortcutsButton.Text = assigned == 0 ? "Shortcuts" : "Shortcuts (" + assigned + ")";
            LayoutShortcutsRow(configureShortcutsButton.Parent);
        }

        private void RegisterShortcutHotkeys()
        {
            if (!IsHandleCreated) return;
            UnregisterShortcutHotkeys();

            var signatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int failed = 0;
            for (int i = 0; i < shortcutActions.Length; i++)
            {
                ShortcutBinding binding;
                if (!shortcutBindings.TryGetValue(shortcutActions[i].Id, out binding) || binding == null || !binding.IsGlobalSafe) continue;
                if (!signatures.Add(binding.Signature))
                {
                    failed++;
                    continue;
                }

                int id = ShortcutHotkeyBaseId + i;
                if (RegisterHotKey(Handle, id, binding.Modifiers, (uint)binding.Key))
                {
                    registeredShortcuts[id] = shortcutActions[i];
                }
                else
                {
                    failed++;
                }
            }

            UpdateShortcutsLabel();
            if (failed > 0) SetStatus(failed == 1 ? "1 shortcut unavailable" : failed + " shortcuts unavailable", amber);
        }

        private void UnregisterShortcutHotkeys()
        {
            if (!IsHandleCreated || registeredShortcuts.Count == 0)
            {
                registeredShortcuts.Clear();
                return;
            }

            var ids = new List<int>(registeredShortcuts.Keys);
            for (int i = 0; i < ids.Count; i++)
            {
                UnregisterHotKey(Handle, ids[i]);
            }
            registeredShortcuts.Clear();
        }

        private void QueueShortcutAction(string actionId)
        {
            PostToUi(async () => await RunUiTaskAsync(() => ExecuteShortcutActionAsync(actionId)));
        }

        private async Task ExecuteShortcutActionAsync(string actionId)
        {
            if (IsClosing) return;
            if (string.Equals(actionId, "open", StringComparison.OrdinalIgnoreCase))
            {
                ShowWindow();
            }
            else if (string.Equals(actionId, "anc", StringComparison.OrdinalIgnoreCase))
            {
                await SetAncAsync();
            }
            else if (string.Equals(actionId, "ambient12", StringComparison.OrdinalIgnoreCase))
            {
                await SetAmbientAsync(12);
            }
            else if (string.Equals(actionId, "ambient20", StringComparison.OrdinalIgnoreCase))
            {
                await SetAmbientAsync(20);
            }
            else if (string.Equals(actionId, "ambientauto", StringComparison.OrdinalIgnoreCase))
            {
                await SetAutoAmbientAsync(true);
            }
            else if (string.Equals(actionId, "ambientmanual", StringComparison.OrdinalIgnoreCase))
            {
                await SetAutoAmbientAsync(false);
            }
            else if (string.Equals(actionId, "off", StringComparison.OrdinalIgnoreCase))
            {
                await SetOffAsync();
            }
            else if (string.Equals(actionId, "eqflat", StringComparison.OrdinalIgnoreCase))
            {
                await SetEqualizerFlatAsync();
            }
            else if (string.Equals(actionId, "eqmanual", StringComparison.OrdinalIgnoreCase))
            {
                await SendEqualizerPresetAsync(0xA0);
            }
            else if (string.Equals(actionId, "equser1", StringComparison.OrdinalIgnoreCase))
            {
                await SendEqualizerPresetAsync(0xA1);
            }
            else if (string.Equals(actionId, "equser2", StringComparison.OrdinalIgnoreCase))
            {
                await SendEqualizerPresetAsync(0xA2);
            }
            else if (string.Equals(actionId, "soundquality", StringComparison.OrdinalIgnoreCase))
            {
                await SetConnectionQualityAsync(true);
            }
            else if (string.Equals(actionId, "stable", StringComparison.OrdinalIgnoreCase))
            {
                await SetConnectionQualityAsync(false);
            }
            else if (string.Equals(actionId, "dseeauto", StringComparison.OrdinalIgnoreCase))
            {
                await SetDseeAsync(true);
            }
            else if (string.Equals(actionId, "dseeoff", StringComparison.OrdinalIgnoreCase))
            {
                await SetDseeAsync(false);
            }
            else if (string.Equals(actionId, "multipointon", StringComparison.OrdinalIgnoreCase))
            {
                await SetMultipointAsync(true);
            }
            else if (string.Equals(actionId, "multipointoff", StringComparison.OrdinalIgnoreCase))
            {
                await SetMultipointAsync(false);
            }
            else if (string.Equals(actionId, "speakon", StringComparison.OrdinalIgnoreCase))
            {
                await SetSpeakToChatAsync(true);
            }
            else if (string.Equals(actionId, "speakoff", StringComparison.OrdinalIgnoreCase))
            {
                await SetSpeakToChatAsync(false);
            }
            else if (string.Equals(actionId, "pauseon", StringComparison.OrdinalIgnoreCase))
            {
                await SetWearPauseAsync(true);
            }
            else if (string.Equals(actionId, "pauseoff", StringComparison.OrdinalIgnoreCase))
            {
                await SetWearPauseAsync(false);
            }
            else if (string.Equals(actionId, "touchon", StringComparison.OrdinalIgnoreCase))
            {
                await SetTouchPanelAsync(true);
            }
            else if (string.Equals(actionId, "touchoff", StringComparison.OrdinalIgnoreCase))
            {
                await SetTouchPanelAsync(false);
            }
            else if (string.Equals(actionId, "autopowerremoved", StringComparison.OrdinalIgnoreCase))
            {
                await SetAutoPowerRemovedAsync();
            }
            else if (string.Equals(actionId, "autopowerdisabled", StringComparison.OrdinalIgnoreCase))
            {
                await SetAutoPowerDisabledAsync();
            }
        }

        private bool ApplyProfile(DeviceProfile profile)
        {
            if (profile == null) return false;
            bool changed = !ReferenceEquals(currentProfile, profile);
            currentProfile = profile;
            // Which NCASM inquired type answers is a property of the device, so a
            // different one has to prove it again.
            if (changed) ncasmTypeSeen = 0;
            if (IsClosing) return changed;

            Text = AppTitle();
            if (titleLabel != null) titleLabel.Text = currentProfile.DisplayName;
            if (trayIcon != null && !trayCleanupStarted) trayIcon.Text = TrayTitle();
            if (heroImageBox != null && !heroImageBox.IsDisposed && (changed || heroImageBox.Image == null))
            {
                Image previous = heroImageBox.Image;
                heroImageBox.Image = LoadImageAsset(currentProfile.AssetPath);
                if (previous != null) previous.Dispose();
                heroImageBox.Invalidate();
            }

            return changed;
        }

        private async Task AutoDetectTickAsync()
        {
            if (IsClosing || commandBusy || autoDetectRunning) return;
            autoDetectRunning = true;
            try
            {
                var detection = await DetectProfileAsync(true);
                if (IsClosing) return;
                if (detection == null)
                {
                    lastStateRefreshConnected = false;
                    return;
                }

                if (!detection.Connected)
                {
                    lastStateRefreshConnected = false;
                    return;
                }

                bool stateRefreshExpired = DateTime.UtcNow - lastStateRefreshAt >= TimeSpan.FromMilliseconds(AutoStateRefreshIntervalMs);
                if (!ReferenceEquals(lastStateRefreshProfile, detection.Profile) || !lastStateRefreshConnected || stateRefreshExpired)
                {
                    await RefreshCurrentStateQuietAsync(detection);
                }
            }
            catch
            {
                if (!IsClosing) SetStatus("Auto detect failed", amber);
            }
            finally
            {
                autoDetectRunning = false;
            }
        }

        private async Task<DeviceDetection> DetectProfileAsync(bool quiet)
        {
            var output = quiet ? await RunBackendQuietAsync("scan") : await RunBackendAsync("scan", "Detecting device");
            if (IsClosing) return null;
            if (output == null) return null;

            var detection = FindDetectedProfile(output);
            if (detection == null)
            {
                SetStatus("No supported device", amber);
                if (connectionLabel != null) connectionLabel.Text = "No supported device";
                return null;
            }

            bool changed = ApplyProfile(detection.Profile);
            if (connectionLabel != null) connectionLabel.Text = detection.Connected ? "Connected" : "Not connected";
            if (lastActionLabel != null && changed) lastActionLabel.Text = "Detected " + detection.Profile.DisplayName;
            SetStatus(detection.Connected ? "Connected" : "Not connected", detection.Connected ? green : amber);
            return detection;
        }

        private DeviceDetection FindDetectedProfile(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;

            DeviceDetection best = null;
            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string lineText = lines[i];
                for (int j = 0; j < profiles.Length; j++)
                {
                    DeviceProfile profile = profiles[j];
                    if (!profile.MatchesText(lineText)) continue;

                    bool connected = Regex.IsMatch(lineText, @"^\s*yes\s+", RegexOptions.IgnoreCase);
                    bool remembered = Regex.IsMatch(lineText, @"^\s*(yes|no)\s+(yes|no)\s+yes\s+", RegexOptions.IgnoreCase);
                    int score = (connected ? 100 : 0) + (remembered ? 10 : 0) + (ReferenceEquals(profile, currentProfile) ? 1 : 0);
                    if (best == null || score > best.Score)
                    {
                        best = new DeviceDetection(profile, connected, remembered, score);
                    }
                }
            }
            return best;
        }

        private async Task RefreshAllAsync()
        {
            var detection = await DetectProfileAsync(false);
            if (IsClosing) return;
            if (detection == null) return;
            var output = await RunBackendAsync(WithDevice(BuildStateBatchCommand(detection.Profile ?? currentProfile)), "Updating");
            if (IsClosing) return;
            if (string.IsNullOrWhiteSpace(output)) return;
            if (!output.Contains("Could not open"))
            {
                connectionLabel.Text = "Connected";
                MarkStateRefreshed(detection);
            }
            ParseBattery(output);
            ParseCodec(output);
            ParseMode(output);
            ParseExtraSettings(output);
        }

        private async Task RefreshCurrentStateQuietAsync(DeviceDetection detection)
        {
            var output = await RunBackendQuietAsync(WithDevice(BuildStateBatchCommand(
                detection != null && detection.Profile != null ? detection.Profile : currentProfile)));
            if (IsClosing) return;
            if (string.IsNullOrWhiteSpace(output)) return;
            if (output.Contains("Could not open"))
            {
                lastStateRefreshConnected = false;
                return;
            }

            if (connectionLabel != null) connectionLabel.Text = "Connected";
            ParseBattery(output);
            ParseCodec(output);
            ParseMode(output);
            ParseExtraSettings(output);
            MarkStateRefreshed(detection);
            SetStatus("Connected", green);
        }

        private void MarkStateRefreshed(DeviceDetection detection)
        {
            lastStateRefreshProfile = detection != null ? detection.Profile : currentProfile;
            lastStateRefreshConnected = true;
            lastStateRefreshAt = DateTime.UtcNow;
        }

        private void ParseCodec(string output)
        {
            if (codecLabel == null) return;
            var match = Regex.Match(output, @"(?m)^codec:\s*(.+?)\s*$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string value = match.Groups[1].Value;
                // The headset reports the codec of whichever connection is playing,
                // so on a multipoint setup this follows the active device.
                codecLabel.Text = value.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? "Unknown" : value;
                codecLabel.ForeColor = subdued;
                codecReported = true;
                return;
            }

            // The WF-1000XM6 leaves the device information request unanswered
            // for as long as either bud sits in the case (verified with each
            // bud, in both connection quality settings), while still answering
            // the battery request in the same batch with that bud at 0%. Any
            // other missing reply is left to look like one.
            if (!Regex.IsMatch(output, @"(?m)^battery:\s*left\s*(0%|.*right\s*0%)", RegexOptions.IgnoreCase)) return;
            // Dimmed, as the meter is, for a value no longer being refreshed:
            // the source can still change while the bud is docked. With nothing
            // reported yet, say why rather than go on "Waiting".
            if (codecReported) codecLabel.ForeColor = faint;
            else codecLabel.Text = "Not reported while a bud is docked";
        }

        private void ParseBattery(string output)
        {
            if (batteryLabel == null) return;

            // Earbuds report the two buds under inquired type 0x01 and the case
            // under 0x02. Over-ear models answer neither, so fall back to the
            // single level from type 0x00.
            var buds = Regex.Match(output, @"(?m)^battery:\s*left\s*(\d+)%.*?right\s*(\d+)%", RegexOptions.IgnoreCase);
            var cradle = Regex.Match(output, @"(?m)^case battery:\s*(\d+)%", RegexOptions.IgnoreCase);

            if (buds.Success)
            {
                string text = "L " + buds.Groups[1].Value + "%   R " + buds.Groups[2].Value + "%";
                if (cradle.Success) text += "   Case " + cradle.Groups[1].Value + "%";
                batteryLabel.Text = text;
                return;
            }

            var single = Regex.Match(output, @"(?m)^battery:\s*(.+)$");
            if (single.Success) batteryLabel.Text = FormatBatteryText(single.Groups[1].Value);
        }

        private static string FormatBatteryText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "Waiting";
            string value = Regex.Replace(text.Trim(), @"\s*\((?:not-charging|charging|charged|unknown)\)", "", RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"\s+,", ",");
            value = Regex.Replace(value, @"\s{2,}", " ");
            return value.Trim();
        }

        private void ParseExtraSettings(string output)
        {
            var touchPanel = Regex.Match(output, @"payload:\s*D7\s+D1\s+00\s+([0-9A-F]{2})", RegexOptions.IgnoreCase);
            if (touchPanel.Success && touchPanelLabel != null)
            {
                SetTouchPanelState(HexByte(touchPanel.Groups[1].Value) == 0);
            }

            var multipoint = Regex.Match(output, @"payload:\s*D7\s+D2\s+00\s+([0-9A-F]{2})", RegexOptions.IgnoreCase);
            if (multipoint.Success && multipointLabel != null)
            {
                SetMultipointState(HexByte(multipoint.Groups[1].Value) == 0);
            }

            // Band layout first: the state below is interpreted against it.
            ApplyEqualizerCapability(output);

            int eqPreset;
            int[] eqValues;
            if (TryParseEqualizerState(output, out eqPreset, out eqValues))
            {
                AdoptEqLayoutFromState(eqValues);
                if (HasEqValues(eqValues))
                {
                    UpdateEqualizerUi(eqPreset, eqValues);
                }
                else
                {
                    currentEqPreset = eqPreset;
                    SelectEqPresetSilently(eqPreset);
                    if (eqCardSummaryLabel != null) eqCardSummaryLabel.Text = eqProfile.PresetName(eqPreset);
                }
            }
            else
            {
                var equalizer = Regex.Match(output, @"payload:\s*53\s+00\s+([0-9A-F]{2})", RegexOptions.IgnoreCase);
                if (equalizer.Success && eqCardSummaryLabel != null)
                {
                    int state = HexByte(equalizer.Groups[1].Value);
                    string text = state == 0 ? "Enabled, waiting for bands" : "Off";
                    eqCardSummaryLabel.Text = text;
                }
            }

            var dsee = Regex.Match(output, @"payload:\s*E7\s+01\s+([0-9A-F]{2})", RegexOptions.IgnoreCase);
            if (dsee.Success && dseeLabel != null)
            {
                SetDseeState(HexByte(dsee.Groups[1].Value) == 1);
            }

            // Prefer the device information blob: it reports all three settings,
            // where E7 00 collapses low latency and sound quality onto the same
            // value. Fall back to E7 00 for models that do not return the blob.
            var reported = Regex.Match(output, @"(?m)^connection quality:\s*(\S+)\s*$", RegexOptions.IgnoreCase);
            if (reported.Success && connectionQualityLabel != null)
            {
                string mode = reported.Groups[1].Value;
                if (mode.Equals("low-latency", StringComparison.OrdinalIgnoreCase)) SetConnectionQualityLowLatency();
                else if (mode.Equals("quality", StringComparison.OrdinalIgnoreCase)) SetConnectionQualityState(true);
                else if (mode.Equals("stability", StringComparison.OrdinalIgnoreCase)) SetConnectionQualityState(false);
                else SetConnectionQualityState(null);
            }
            else
            {
                var connection = Regex.Match(output, @"payload:\s*E7\s+00\s+([0-9A-F]{2})", RegexOptions.IgnoreCase);
                if (connection.Success && connectionQualityLabel != null)
                {
                    SetConnectionQualityState(HexByte(connection.Groups[1].Value) == 0);
                }
            }

            var speak = Regex.Match(output, @"payload:\s*F7\s+02\s+([0-9A-F]{2})\s+([0-9A-F]{2})", RegexOptions.IgnoreCase);
            if (speak.Success && speakToChatLabel != null)
            {
                SetSpeakToChatState(HexByte(speak.Groups[1].Value) == 0);
            }

            var wear = Regex.Match(output, @"payload:\s*F7\s+01\s+([0-9A-F]{2})", RegexOptions.IgnoreCase);
            if (wear.Success && wearPauseLabel != null)
            {
                SetWearPauseState(HexByte(wear.Groups[1].Value) == 0);
            }

            var power = Regex.Match(output, @"payload:\s*27\s+05\s+([0-9A-F]{2})\s+([0-9A-F]{2})", RegexOptions.IgnoreCase);
            if (power.Success && autoPowerLabel != null)
            {
                int mode = HexByte(power.Groups[1].Value);
                int delay = HexByte(power.Groups[2].Value);
                if (mode == 0x10) SetAutoPowerState("removed");
                else if (mode == 0x11) SetAutoPowerState("disabled");
                else SetAutoPowerCustomState(FormatPowerDelay(mode == 0 ? delay : mode));
            }
        }

        private async Task SetAncAsync()
        {
            bigModeLabel.Text = "ANC";
            bigDetailLabel.Text = "Noise cancelling active";
            SetModeButtonState("ANC");
            lastActionLabel.Text = "ANC selected";
            ParseMode(await RunBackendAsync(WithDevice("ncasm-set --mode anc --timeout 1600"), "Setting ANC"));
        }

        private async Task SetAmbientAsync(int level, bool voice = false)
        {
            int clamped = Math.Max(levelSlider.Minimum, Math.Min(levelSlider.Maximum, level));
            levelSlider.Value = clamped;
            ambientKindBox.SelectedIndex = voice ? 1 : 0;
            string kind = voice ? "Voice" : "Normal";
            bigModeLabel.Text = "Ambient";
            bigDetailLabel.Text = kind + " ambient level " + clamped;
            SetModeButtonState("Ambient");
            lastActionLabel.Text = (voice ? "Voice ambient " : "Ambient ") + clamped;
            string args = "ncasm-set --mode ambient --level " + clamped + " --voice " + (voice ? "on" : "off") + " --timeout 1600";
            // Report what the headset applied rather than what was asked for.
            // ParseMode refreshes these controls from the reply, and the two can
            // differ because the headset clamps values it will not take. When it
            // reports nothing the command did not land, so say that instead of
            // presenting the requested value as a confirmation.
            if (ParseMode(await RunBackendAsync(WithDevice(args), "Setting ambient")))
            {
                lastActionLabel.Text = (IsVoiceAmbientSelected() ? "Voice ambient " : "Ambient ") + levelSlider.Value;
            }
            else
            {
                lastActionLabel.Text = (voice ? "Voice ambient " : "Ambient ") + clamped + " not confirmed";
            }
        }

        private async Task SetAutoAmbientAsync(bool enabled)
        {
            if (autoAmbientSupported == false)
            {
                lastActionLabel.Text = "Auto ambient not supported";
                return;
            }
            lastActionLabel.Text = "Auto ambient " + (enabled ? "on" : "off");
            // Auto and manual are two flavours of ambient sound, so both buttons
            // select ambient and only the auto flag differs. Changing the mode on
            // one and not the other made the pair behave inconsistently from noise
            // cancelling, and left the tray entries named "Ambient sound: Auto" and
            // "Ambient sound: Manual" promising something neither delivered.
            string args = "ncasm-set --mode ambient --auto " + (enabled ? "on" : "off");
            if (enabled) args += " --sensitivity " + SelectedSensitivity();
            ParseMode(await RunBackendAsync(WithDevice(args + " --timeout 1600"), "Setting auto ambient"));
        }

        private async Task SetAmbientSensitivityAsync(string sensitivity)
        {
            if (autoAmbientSupported == false)
            {
                lastActionLabel.Text = "Auto ambient not supported";
                return;
            }
            lastActionLabel.Text = "Auto ambient sensitivity " + sensitivity;
            string args = "ncasm-set --auto on --sensitivity " + sensitivity + " --timeout 1600";
            ParseMode(await RunBackendAsync(WithDevice(args), "Setting sensitivity"));
        }

        private async Task SetOffAsync()
        {
            bigModeLabel.Text = "Off";
            bigDetailLabel.Text = "Noise control disabled";
            SetModeButtonState("Off");
            lastActionLabel.Text = "Noise control off";
            ParseMode(await RunBackendAsync(WithDevice("ncasm-set --mode off --timeout 1600"), "Turning off"));
        }

        private async Task SetDseeAsync(bool enabled)
        {
            SetDseeState(enabled);
            lastActionLabel.Text = "DSEE Extreme " + (enabled ? "Auto" : "Off");
            await RunBackendAsync(WithDevice("raw \"E8 01 " + (enabled ? "01" : "00") + "\" --ack-only --timeout 1200"), "Setting DSEE");
        }

        private async Task SendEqualizerPresetAsync(int preset)
        {
            if (!EqLayoutReady()) return;
            CancelLiveEqualizerApply();
            InvalidateEqualizerFetch();
            int[] cachedValues;
            bool hasCachedValues = TryGetCachedEqValues(preset, out cachedValues);
            currentEqPreset = preset;
            string label = hasCachedValues ? FormatEqSummary(preset, cachedValues) : eqProfile.PresetName(preset);
            if (eqCardSummaryLabel != null) eqCardSummaryLabel.Text = label;
            if (hasCachedValues) UpdateEqualizerUi(preset, cachedValues);
            lastActionLabel.Text = "Equalizer preset updating";

            string payload = FormatEqualizerPresetPayload(preset);
            var output = await RunBackendSilentAsync(WithDevice("batch \"" + payload + "\" --timeout 800"));
            if (IsClosing) return;
            if (output.Contains("Could not open"))
            {
                SetStatus("Device not reachable", red);
                lastActionLabel.Text = "Equalizer preset not sent";
                return;
            }

            int responsePreset;
            int[] responseValues;
            if (TryParseEqualizerState(output, out responsePreset, out responseValues) && responsePreset == preset)
            {
                AdoptEqLayoutFromState(responseValues);
                if (HasEqValues(responseValues))
                {
                    UpdateEqualizerUi(responsePreset, responseValues);
                }
                else
                {
                    await RefreshEqualizerQuietAsync(preset);
                }
            }
            else if (hasCachedValues)
            {
                UpdateEqualizerUi(preset, cachedValues);
            }
            else
            {
                await RefreshEqualizerQuietAsync(preset);
            }
            lastActionLabel.Text = "Equalizer preset updated";
        }

        private async Task CopyEqualizerToPresetAsync(int preset)
        {
            if (!EqLayoutReady()) return;
            if (preset != 0xA1 && preset != 0xA2) return;
            CancelLiveEqualizerApply();
            InvalidateEqualizerFetch();
            int[] values = CurrentEqValues();
            string presetName = eqProfile.PresetName(preset);
            lastActionLabel.Text = "Copying to " + presetName;

            string payload = FormatEqualizerPayload(preset, values);
            var output = await RunBackendSilentAsync(WithDevice("raw \"" + payload + "\" --ack-only --timeout 1200"));
            if (IsClosing) return;
            if (output.Contains("Could not open"))
            {
                SetStatus("Device not reachable", red);
                lastActionLabel.Text = "Not copied to " + presetName;
                return;
            }

            CacheEqValues(preset, values);
            currentEqPreset = preset;
            SelectEqPresetSilently(preset);
            if (eqCardSummaryLabel != null) eqCardSummaryLabel.Text = FormatEqSummary(preset, values);
            lastActionLabel.Text = "Copied to " + presetName;
        }

        private void ShowEqualizerCopyMenu(Control anchor)
        {
            if (anchor == null || anchor.IsDisposed) return;
            var menu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(36, 38, 42),
                ForeColor = ink,
                ShowImageMargin = false,
                Padding = new Padding(4)
            };
            AddEqualizerCopyMenuItem(menu, "Copy to " + eqProfile.PresetName(0xA1), 0xA1);
            AddEqualizerCopyMenuItem(menu, "Copy to " + eqProfile.PresetName(0xA2), 0xA2);
            menu.Closed += (s, e) => DisposeContextMenuLater(menu);
            try
            {
                menu.Show(anchor, new Point(0, anchor.Height + 4));
            }
            catch
            {
                menu.Dispose();
            }
        }

        private void AddEqualizerCopyMenuItem(ContextMenuStrip menu, string text, int preset)
        {
            var item = new ToolStripMenuItem(text)
            {
                BackColor = Color.FromArgb(36, 38, 42),
                ForeColor = ink
            };
            item.Click += async (s, e) => await RunUiTaskAsync(() => CopyEqualizerToPresetAsync(preset));
            menu.Items.Add(item);
        }

        private void DisposeContextMenuLater(ContextMenuStrip menu)
        {
            if (menu == null || menu.IsDisposed) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (!menu.IsDisposed) menu.Dispose();
                }));
            }
            catch (InvalidOperationException)
            {
            }
        }

        private async Task RefreshEqualizerQuietAsync(int expectedPreset)
        {
            int fetchGeneration = ++eqFetchGeneration;
            var output = await RunBackendSilentAsync(WithDevice("batch \"56 00\" --timeout 800"));
            if (IsClosing || string.IsNullOrWhiteSpace(output) || output.Contains("Could not open")) return;

            int preset;
            int[] values;
            if (fetchGeneration != eqFetchGeneration || !TryParseEqualizerState(output, out preset, out values)) return;
            AdoptEqLayoutFromState(values);
            if (expectedPreset >= 0 && preset != expectedPreset) return;
            if (!HasEqValues(values)) return;
            UpdateEqualizerUi(preset, values);
        }

        private async Task SendEqualizerCurrentAsync(bool quiet)
        {
            if (!EqLayoutReady()) return;
            if (eqSliders == null || eqSliders.Length != eqProfile.BandCount) return;
            InvalidateEqualizerFetch();
            var choice = eqPresetBox != null ? eqPresetBox.SelectedItem as EqPresetChoice : null;
            int preset = choice != null ? choice.Value : currentEqPreset;
            int[] values = CurrentEqValues();

            currentEqPreset = preset;
            CacheEqValues(preset, values);
            if (eqCardSummaryLabel != null) eqCardSummaryLabel.Text = FormatEqSummary(preset, values);
            lastActionLabel.Text = quiet ? "Equalizer updating" : "Equalizer applied";

            string payload = FormatEqualizerPayload(preset, values);
            if (quiet)
            {
                var output = await RunBackendQuietAsync(WithDevice("raw \"" + payload + "\" --ack-only --timeout 1200"));
                if (output == null)
                {
                    liveEqSendPending = true;
                    lastActionLabel.Text = "Equalizer waiting";
                    return;
                }
                if (output.Contains("Could not open"))
                {
                    SetStatus("Device not reachable", red);
                    lastActionLabel.Text = "Equalizer not sent";
                    return;
                }
                lastActionLabel.Text = "Equalizer updated";
                return;
            }

            await RunBackendAsync(WithDevice("raw \"" + payload + "\" --ack-only --timeout 1200"), "Setting equalizer");
        }

        // The device stores whatever it is sent without range-checking it, so every
        // value is clamped here rather than relying on the headset to reject it.
        private string FormatEqualizerPayload(int preset, int[] values)
        {
            var text = new StringBuilder();
            text.AppendFormat("58 00 {0:X2} {1:X2}", preset, eqProfile.BandCount);
            for (int i = 0; i < eqProfile.BandCount; i++)
            {
                int value = values != null && i < values.Length ? values[i] : eqProfile.Neutral;
                text.AppendFormat(" {0:X2}", eqProfile.Clamp(value));
            }
            return text.ToString();
        }

        // Selecting a preset without touching the band values. The WF-1000XM6 does
        // not answer the 6-band form (58 00 <preset> 00) at all and uses inquired
        // type 04 instead; sending band values there would force it to Manual.
        private string FormatEqualizerPresetPayload(int preset)
        {
            return string.Format(eqProfile.PresetsUseType04 ? "58 04 {0:X2} 00" : "58 00 {0:X2} 00", preset);
        }

        private void ScheduleLiveEqualizerApply()
        {
            if (updatingEqUi || exiting || trayCleanupStarted || liveEqTimer == null) return;
            liveEqSendPending = true;
            liveEqTimer.Stop();
            liveEqTimer.Start();
        }

        private void CancelLiveEqualizerApply()
        {
            liveEqSendPending = false;
            if (liveEqTimer != null) liveEqTimer.Stop();
        }

        private async Task FlushLiveEqualizerAsync()
        {
            if (updatingEqUi || exiting || trayCleanupStarted) return;
            if (liveEqSendRunning)
            {
                liveEqSendPending = true;
                return;
            }

            liveEqSendRunning = true;
            liveEqSendPending = false;
            try
            {
                await SendEqualizerCurrentAsync(true);
            }
            finally
            {
                liveEqSendRunning = false;
                if (liveEqSendPending && liveEqTimer != null && !exiting && !trayCleanupStarted)
                {
                    liveEqTimer.Stop();
                    liveEqTimer.Start();
                }
            }
        }

        private async Task SetEqualizerFlatAsync()
        {
            if (!EqLayoutReady()) return;
            CancelLiveEqualizerApply();
            // Flattening is an edit like any other, so it lands in the slot that is
            // already selected rather than dragging the user back to Manual.
            int target = EqEditTarget();
            if (eqSliders != null) ResetEqSliders(target);
            lastActionLabel.Text = "Equalizer set to flat";
            string payload = FormatEqualizerPayload(target, FlatEqValues());
            await RunBackendAsync(WithDevice("raw \"" + payload + "\" --ack-only --timeout 1200"), "Setting equalizer");
        }

        private int[] FlatEqValues()
        {
            var values = new int[eqProfile.BandCount];
            for (int i = 0; i < values.Length; i++) values[i] = eqProfile.Neutral;
            return values;
        }

        private void ResetEqSliders(int target)
        {
            if (eqSliders == null) return;
            updatingEqUi = true;
            try
            {
                currentEqPreset = target;
                SelectEqPreset(target);
                for (int i = 0; i < eqSliders.Length; i++)
                {
                    eqSliders[i].Value = eqProfile.Neutral;
                    if (eqValueLabels != null && eqValueLabels[i] != null) eqValueLabels[i].Text = eqProfile.FormatValue(eqProfile.Neutral);
                }
                if (eqCardSummaryLabel != null) eqCardSummaryLabel.Text = eqProfile.PresetName(target) + " / Flat";
                int[] flatValues = FlatEqValues();
                CacheEqValues(target, flatValues);
                lastActionLabel.Text = "Equalizer flattened";
            }
            finally
            {
                updatingEqUi = false;
            }
        }

        private void UpdateEqualizerUi(int preset, int[] values)
        {
            currentEqPreset = preset;
            CacheEqValues(preset, values);
            if (eqCardSummaryLabel != null) eqCardSummaryLabel.Text = FormatEqSummary(preset, values);
            if (eqSliders == null || !HasEqValues(values)) return;

            updatingEqUi = true;
            try
            {
                SelectEqPreset(preset);
                for (int i = 0; i < eqSliders.Length; i++)
                {
                    int value = eqProfile.Clamp(values[i]);
                    eqSliders[i].Value = value;
                    if (eqValueLabels != null && eqValueLabels[i] != null) eqValueLabels[i].Text = eqProfile.FormatValue(value);
                }
            }
            finally
            {
                updatingEqUi = false;
            }
        }

        private bool SelectEqPreset(int preset)
        {
            if (eqPresetBox == null) return false;
            for (int i = 0; i < eqPresetBox.Items.Count; i++)
            {
                var choice = eqPresetBox.Items[i] as EqPresetChoice;
                if (choice != null && choice.Value == preset)
                {
                    eqPresetBox.SelectedIndex = i;
                    return true;
                }
            }
            return false;
        }

        private void SelectEqPresetSilently(int preset)
        {
            updatingEqUi = true;
            try
            {
                SelectEqPreset(preset);
            }
            finally
            {
                updatingEqUi = false;
            }
        }

        private int[] CurrentEqValues()
        {
            int[] values = new int[eqProfile.BandCount];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = eqSliders != null && i < eqSliders.Length && eqSliders[i] != null ? eqSliders[i].Value : eqProfile.Neutral;
            }
            return values;
        }

        private void HandleEqualizerSliderChanged(SliderControl changed)
        {
            int index = eqSliders == null ? -1 : Array.IndexOf(eqSliders, changed);
            if (index >= 0 && eqValueLabels != null && index < eqValueLabels.Length && eqValueLabels[index] != null)
            {
                eqValueLabels[index].Text = eqProfile.FormatValue(changed.Value);
            }
            if (updatingEqUi) return;

            int target = EqEditTarget();
            int[] values = currentEqPreset == target ? CurrentEqValues() : ResolveEqValuesForEdit(currentEqPreset);
            if (index >= 0 && index < values.Length) values[index] = eqProfile.Clamp(changed.Value);

            currentEqPreset = target;
            SelectEqPresetSilently(target);
            CacheEqValues(target, values);
            if (eqCardSummaryLabel != null) eqCardSummaryLabel.Text = FormatEqSummary(target, values);
            lastActionLabel.Text = "Equalizer updating";
            ScheduleLiveEqualizerApply();
        }

        // Manual and the two custom slots hold whatever the user dials in, so an
        // edit stays where it already is. Every other preset is fixed, and editing
        // one moves the curve into Manual instead — matching Sound Connect, and
        // leaving the custom slots actually editable rather than stuck at flat.
        private static bool IsStorableEqPreset(int preset)
        {
            return preset == 0xA0 || preset == 0xA1 || preset == 0xA2;
        }

        private int EqEditTarget()
        {
            return IsStorableEqPreset(currentEqPreset) ? currentEqPreset : 0xA0;
        }

        private int[] ResolveEqValuesForEdit(int preset)
        {
            int[] cachedValues;
            if (TryGetCachedEqValues(preset, out cachedValues)) return cachedValues;
            return CurrentEqValues();
        }

        private const string PayloadPattern = @"payload:\s*((?:[0-9A-F]{2}\s+)*[0-9A-F]{2})";

        private static int[] ParseHexBytes(string text)
        {
            string[] parts = Regex.Split(text.Trim(), @"\s+");
            var bytes = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++) bytes[i] = HexByte(parts[i]);
            return bytes;
        }

        // Reads whatever band count the device reports instead of requiring six.
        // Both inquired types carry the same shape, and a set on type 04 answers
        // with 59 rather than 57, so all four combinations are accepted. A reply
        // with a band count of zero is preset-only and carries no values.
        private bool TryParseEqualizerState(string output, out int preset, out int[] values)
        {
            preset = 0;
            values = null;
            bool found = false;
            bool haveBands = false;

            foreach (Match payload in Regex.Matches(output, PayloadPattern, RegexOptions.IgnoreCase))
            {
                int[] bytes = ParseHexBytes(payload.Groups[1].Value);
                if (bytes.Length < 4) continue;
                if (bytes[0] != 0x57 && bytes[0] != 0x59) continue;
                if (bytes[1] != 0x00 && bytes[1] != 0x04) continue;

                int count = bytes[3];
                if (count == 0)
                {
                    if (!haveBands)
                    {
                        preset = bytes[2];
                        values = null;
                        found = true;
                    }
                    continue;
                }

                if (bytes.Length < 4 + count) continue;
                preset = bytes[2];
                values = new int[count];
                for (int i = 0; i < count; i++) values[i] = bytes[4 + i];
                found = true;
                haveBands = true;
            }

            return found;
        }

        // 5B 00 <band count>, then one 3-byte group per band: a constant 01
        // followed by the centre frequency as a 16-bit big-endian value. There is
        // no range or step field anywhere in the reply.
        private bool TryParseEqualizerCapability(string output, out int bandCount, out int[] frequencies)
        {
            bandCount = 0;
            frequencies = null;

            foreach (Match payload in Regex.Matches(output, PayloadPattern, RegexOptions.IgnoreCase))
            {
                int[] bytes = ParseHexBytes(payload.Groups[1].Value);
                if (bytes.Length < 3 || bytes[0] != 0x5B || bytes[1] != 0x00) continue;

                int count = bytes[2];
                if (count <= 0 || bytes.Length < 3 + (count * 3)) continue;

                var freqs = new int[count];
                for (int i = 0; i < count; i++)
                {
                    int at = 3 + (i * 3);
                    freqs[i] = (bytes[at + 1] << 8) | bytes[at + 2];
                }

                bandCount = count;
                frequencies = freqs;
                return true;
            }

            return false;
        }

        private void ApplyEqualizerCapability(string output)
        {
            int bandCount;
            int[] frequencies;
            if (!TryParseEqualizerCapability(output, out bandCount, out frequencies)) return;
            AdoptEqProfile(EqProfile.FromCapability(bandCount, frequencies));
        }

        // The state reply carries the band count as well, so the layout does not
        // depend on the capability reply alone. Whichever of the two arrives is
        // enough to stop guessing; only the frequency labels need 5A 00.
        private void AdoptEqLayoutFromState(int[] values)
        {
            if (values == null || values.Length <= 0) return;
            AdoptEqProfile(EqProfile.FromCapability(values.Length, null));
        }

        private void AdoptEqProfile(EqProfile profile)
        {
            bool wasKnown = eqLayoutKnown;
            eqLayoutKnown = true;

            if (eqProfile.Matches(profile.BandCount, profile.Labels))
            {
                if (!wasKnown) UpdateEqControlsEnabled();
                return;
            }

            // A band count we already trust is not overwritten by a reply that
            // carries no frequencies: the capability labels are better than the
            // placeholder ones the state reply can produce.
            if (wasKnown && profile.BandCount == eqProfile.BandCount) return;

            // Cached curves belong to the old band layout and cannot be remapped.
            eqProfile = profile;
            eqBandCache.Clear();
            PopulateEqPresetBox();
            RebuildEqualizerBands();
        }

        // Nothing may be written until the real layout is known: the default
        // profile is a guess, and acting on it is what sends a six-band frame to
        // a ten-band device.
        private bool EqLayoutReady()
        {
            if (eqLayoutKnown) return true;
            if (lastActionLabel != null) lastActionLabel.Text = "Equalizer waiting for device";
            return false;
        }

        private void UpdateEqControlsEnabled()
        {
            bool ready = eqLayoutKnown;
            if (eqSliders != null)
            {
                for (int i = 0; i < eqSliders.Length; i++)
                {
                    if (eqSliders[i] != null) eqSliders[i].Enabled = ready;
                }
            }
            if (eqPresetBox != null) eqPresetBox.Enabled = ready;
            if (eqFlatButton != null) eqFlatButton.Enabled = ready;
            if (eqCopyPresetButton != null) eqCopyPresetButton.Enabled = ready;
        }

        private void CacheEqValues(int preset, int[] values)
        {
            if (!HasEqValues(values)) return;
            int[] copy = new int[eqProfile.BandCount];
            for (int i = 0; i < copy.Length; i++) copy[i] = eqProfile.Clamp(values[i]);
            eqBandCache[preset] = copy;
        }

        private bool TryGetCachedEqValues(int preset, out int[] values)
        {
            int[] cached;
            if (eqBandCache.TryGetValue(preset, out cached) && HasEqValues(cached))
            {
                values = new int[eqProfile.BandCount];
                Array.Copy(cached, values, values.Length);
                return true;
            }

            values = null;
            return false;
        }

        private bool HasEqValues(int[] values)
        {
            return values != null && values.Length >= eqProfile.BandCount;
        }

        private void InvalidateEqualizerFetch()
        {
            eqFetchGeneration++;
        }

        private async Task SetConnectionQualityAsync(bool prioritizeSound)
        {
            if (lowLatencyActive)
            {
                lastActionLabel.Text = LowLatencyHint;
                return;
            }
            SetConnectionQualityState(prioritizeSound);
            lastActionLabel.Text = prioritizeSound ? "Connection quality set to quality" : "Connection quality set to stability";
            await RunBackendAsync(WithDevice("raw \"E8 00 " + (prioritizeSound ? "00" : "01") + "\" --ack-only --timeout 1200"), "Setting connection quality");
        }

        private async Task SetSpeakToChatAsync(bool enabled)
        {
            string value = enabled ? "00" : "01";
            SetSpeakToChatState(enabled);
            lastActionLabel.Text = "Speak-to-Chat " + (enabled ? "on" : "off");
            await RunBackendAsync(WithDevice("raw \"F8 02 " + value + " " + value + "\" --ack-only --timeout 1200"), "Setting Speak-to-Chat");
        }

        private async Task SetWearPauseAsync(bool enabled)
        {
            SetWearPauseState(enabled);
            lastActionLabel.Text = "Pause when removed " + (enabled ? "on" : "off");
            await RunBackendAsync(WithDevice("raw \"F8 01 " + (enabled ? "00" : "01") + "\" --ack-only --timeout 1200"), "Setting wearing sensor");
        }

        private async Task SetTouchPanelAsync(bool enabled)
        {
            SetTouchPanelState(enabled);
            lastActionLabel.Text = "Touch panel " + (enabled ? "on" : "off");
            await RunBackendAsync(WithDevice("raw \"D8 D1 00 " + (enabled ? "00" : "01") + "\" --ack-only --timeout 1200"), "Setting touch panel");
        }

        private async Task SetMultipointAsync(bool enabled)
        {
            SetMultipointState(enabled);
            lastActionLabel.Text = "Multipoint " + (enabled ? "on" : "off");
            await RunBackendAsync(WithDevice("raw \"D8 D2 00 " + (enabled ? "00" : "01") + "\" --ack-only --timeout 1200"), "Setting multipoint");
        }

        private async Task SetAutoPowerRemovedAsync()
        {
            SetAutoPowerState("removed");
            lastActionLabel.Text = "Automatic power off set to on";
            await RunBackendAsync(WithDevice("raw \"28 05 10 00\" --ack-only --timeout 1200"), "Setting auto power");
        }

        private async Task SetAutoPowerDisabledAsync()
        {
            SetAutoPowerState("disabled");
            lastActionLabel.Text = "Automatic power off set to off";
            await RunBackendAsync(WithDevice("raw \"28 05 11 00\" --ack-only --timeout 1200"), "Setting auto power");
        }

        private bool ParseMode(string output)
        {
            // The backend prints one line per NCASM inquired type it gets an answer
            // for. Older models only answer type 0x17; the WF-1000XM6 answers both,
            // but only its 0x19 reply carries real values, so prefer whichever line
            // reports the auto ambient fields.
            var matches = Regex.Matches(output,
                @"ncasm:\s*type=(\w+);\s*master\s*(\w+);\s*mode\s*(\w+);\s*voice\s*(\w+);\s*level\s*(\d+)(?:;\s*auto\s*(\w+);\s*sensitivity\s*(\w+))?",
                RegexOptions.IgnoreCase);
            if (matches.Count == 0) return false;

            Match match = null;
            foreach (Match candidate in matches)
            {
                if (candidate.Groups[6].Success) { match = candidate; break; }
                if (match == null) match = candidate;
            }

            // The WF-1000XM6 answers 66 17 as well, with every field zero, which
            // is indistinguishable from an older model reporting noise control
            // off. So once 0x19 has answered for this device, a lone 0x17 line
            // means its reply went missing - report nothing rather than a state
            // the headset is not in.
            int lineType = string.Equals(match.Groups[1].Value, "19", StringComparison.OrdinalIgnoreCase) ? 0x19 : 0x17;
            if (lineType == 0x19) ncasmTypeSeen = 0x19;
            else if (ncasmTypeSeen == 0x19) return false;
            else ncasmTypeSeen = 0x17;

            string master = Capitalize(match.Groups[2].Value);
            string mode = match.Groups[3].Value.Equals("anc", StringComparison.OrdinalIgnoreCase) ? "ANC" : Capitalize(match.Groups[3].Value);
            bool voice = match.Groups[4].Value.Equals("on", StringComparison.OrdinalIgnoreCase);
            string level = match.Groups[5].Value;
            bool hasAuto = match.Groups[6].Success;
            bool auto = hasAuto && match.Groups[6].Value.Equals("on", StringComparison.OrdinalIgnoreCase);
            string sensitivity = hasAuto ? Capitalize(match.Groups[7].Value) : null;

            // The headset keeps the ambient level, voice and auto values while
            // noise cancelling is selected, so describe them only when ambient is
            // the mode actually in effect.
            string detail;
            if (master == "Off") detail = "Noise control disabled";
            else if (mode == "ANC") detail = "Noise cancelling active";
            else if (hasAuto && auto) detail = "Auto ambient (" + sensitivity + ")";
            else detail = (voice ? "Voice" : "Normal") + " ambient level " + level;

            bigModeLabel.Text = master == "Off" ? "Off" : mode;
            bigDetailLabel.Text = detail;
            SetModeButtonState(master == "Off" ? "Off" : mode);

            updatingNoiseUi = true;
            try
            {
                int parsed;
                if (int.TryParse(level, out parsed) && parsed >= levelSlider.Minimum && parsed <= levelSlider.Maximum)
                {
                    levelSlider.Value = parsed;
                }
                ambientKindBox.SelectedIndex = voice ? 1 : 0;
                SetAutoAmbientState(hasAuto ? (bool?)auto : null, sensitivity);
            }
            finally
            {
                updatingNoiseUi = false;
            }
            return true;
        }

        private void SetAutoAmbientWaiting()
        {
            autoAmbientSupported = null;
            if (autoAmbientLabel != null) autoAmbientLabel.Text = "Waiting";
            SetOptionPair(autoAmbientOnButton, autoAmbientOffButton, null, blue, blue);
            if (autoAmbientOnButton != null) autoAmbientOnButton.Enabled = false;
            if (autoAmbientOffButton != null) autoAmbientOffButton.Enabled = false;
            if (sensitivityBox != null) sensitivityBox.Enabled = false;
        }

        private void SetAutoAmbientState(bool? auto, string sensitivity)
        {
            autoAmbientSupported = auto.HasValue;

            if (autoAmbientLabel != null)
            {
                if (!auto.HasValue) autoAmbientLabel.Text = "Not supported";
                else if (auto.Value) autoAmbientLabel.Text = "On · " + (sensitivity ?? "Standard");
                else autoAmbientLabel.Text = "Off";
            }

            SetOptionPair(autoAmbientOnButton, autoAmbientOffButton, auto, blue, blue);
            if (autoAmbientOnButton != null) autoAmbientOnButton.Enabled = auto.HasValue;
            if (autoAmbientOffButton != null) autoAmbientOffButton.Enabled = auto.HasValue;

            if (sensitivityBox != null)
            {
                sensitivityBox.Enabled = auto.HasValue && auto.Value;
                int index = SensitivityIndex(sensitivity);
                if (index >= 0 && sensitivityBox.SelectedIndex != index) sensitivityBox.SelectedIndex = index;
            }
        }

        private static int SensitivityIndex(string sensitivity)
        {
            if (string.IsNullOrEmpty(sensitivity)) return -1;
            if (sensitivity.Equals("Low", StringComparison.OrdinalIgnoreCase)) return 0;
            if (sensitivity.Equals("Standard", StringComparison.OrdinalIgnoreCase)) return 1;
            if (sensitivity.Equals("High", StringComparison.OrdinalIgnoreCase)) return 2;
            return -1;
        }

        private string SelectedSensitivity()
        {
            if (sensitivityBox == null || sensitivityBox.SelectedIndex < 0) return "standard";
            switch (sensitivityBox.SelectedIndex)
            {
                case 0: return "low";
                case 2: return "high";
                default: return "standard";
            }
        }

        private async Task<string> RunBackendAsync(string args, string busyText)
        {
            if (IsClosing) return "";
            if (!File.Exists(backendPath))
            {
                SetStatus("Backend not found", red);
                return "";
            }

            bool channel = UsesControlChannel(args);
            if (channel) await commandGate.WaitAsync();
            if (IsClosing)
            {
                if (channel) commandGate.Release();
                return "";
            }

            SetBusy(true, busyText);
            try
            {
                var output = await RunBackendProcessPacedAsync(args);
                if (IsClosing) return output;

                if (output.Contains("Could not open")) SetStatus("Device not reachable", red);
                else SetStatus("Ready", subdued);
                return output;
            }
            catch
            {
                SetStatus("Command failed", red);
                return "";
            }
            finally
            {
                if (!IsClosing) SetBusy(false, "Ready");
                if (channel) commandGate.Release();
            }
        }

        private async Task<string> RunBackendQuietAsync(string args)
        {
            if (IsClosing) return "";
            if (!File.Exists(backendPath)) return "";
            if (!UsesControlChannel(args))
            {
                try
                {
                    var scanOutput = await RunBackendProcessPacedAsync(args);
                    return IsClosing ? "" : scanOutput;
                }
                catch
                {
                    return "";
                }
            }

            if (!commandGate.Wait(0)) return null;
            try
            {
                var output = await RunBackendProcessPacedAsync(args);
                return IsClosing ? "" : output;
            }
            catch
            {
                return "";
            }
            finally
            {
                commandGate.Release();
            }
        }

        private async Task<string> RunBackendSilentAsync(string args)
        {
            if (IsClosing) return "";
            if (!File.Exists(backendPath)) return "";

            await commandGate.WaitAsync();
            if (IsClosing)
            {
                commandGate.Release();
                return "";
            }

            try
            {
                var output = await RunBackendProcessPacedAsync(args);
                return IsClosing ? "" : output;
            }
            catch
            {
                return "";
            }
            finally
            {
                commandGate.Release();
            }
        }

        // The command gate exists to keep two commands off the headset control
        // channel at once, because the headset allows only one control session.
        // Listing paired devices does not use that channel at all: it is a local
        // Bluetooth enumeration. Making it wait behind the gate meant an
        // equalizer drag, whose writes hold the gate, could starve the device
        // scan and leave the app reporting the headset as disconnected.
        private static bool UsesControlChannel(string args)
        {
            return args == null || !args.StartsWith("scan", StringComparison.OrdinalIgnoreCase);
        }

        private string RunBackendProcess(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = backendPath,
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(backendPath),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (var process = Process.Start(psi))
            {
                if (process == null) return "";
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                return stdout + stderr;
            }
        }

        private async Task<string> RunBackendProcessPacedAsync(string args)
        {
            bool shouldPace = ShouldPaceBackendCommand(args);
            if (shouldPace) await WaitForBackendPaceAsync();
            try
            {
                return await Task.Run(() => RunBackendProcess(args));
            }
            finally
            {
                if (shouldPace) lastBackendCommandAtUtc = DateTime.UtcNow;
            }
        }

        private async Task WaitForBackendPaceAsync()
        {
            if (lastBackendCommandAtUtc == DateTime.MinValue) return;

            double elapsedMs = (DateTime.UtcNow - lastBackendCommandAtUtc).TotalMilliseconds;
            int delayMs = BackendCommandPaceMs - (int)elapsedMs;
            if (delayMs > 0) await Task.Delay(delayMs);
        }

        private static bool ShouldPaceBackendCommand(string args)
        {
            return args != null && args.IndexOf("--ack-only", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void SetBusy(bool value, string label)
        {
            commandBusy = value;
            SetStatus(value ? label + "..." : label, value ? blue : subdued);
        }

        private void SetStatus(string value, Color color)
        {
            if (statusLabel == null || statusLabel.IsDisposed) return;
            statusLabel.Text = value;
            statusLabel.ForeColor = color;
        }

        private bool IsVoiceAmbientSelected()
        {
            return ambientKindBox != null && ambientKindBox.SelectedIndex == 1;
        }

        private void ShowWindow()
        {
            if (IsClosing) return;
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void TryApplyWindows11Chrome()
        {
            try
            {
                int enabled = 1;
                DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int));
                DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));

                int rounded = 2;
                DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));

                int caption = ColorTranslator.ToWin32(page);
                int captionText = ColorTranslator.ToWin32(ink);
                int border = ColorTranslator.ToWin32(page);
                DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
                DwmSetWindowAttribute(Handle, 36, ref captionText, sizeof(int));
                DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));

                // The scrollbars a small window shows, dark to match the rest.
                SetWindowTheme(Handle, "DarkMode_Explorer", null);
            }
            catch
            {
            }
        }

        private void Notify(string message)
        {
            if (!trayNotifications || trayIcon == null || trayCleanupStarted) return;
            try
            {
                trayIcon.ShowBalloonTip(1000, AppTitle(), message, ToolTipIcon.Info);
            }
            catch
            {
            }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!exiting && minimizeToTray && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTrayFromClose();
                return;
            }
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                QueueImmediateExit();
                return;
            }
            if (trayCleanupStarted) return;
            exiting = true;
            trayNotifications = false;
            UnregisterShortcutHotkeys();
            if (autoDetectTimer != null)
            {
                autoDetectTimer.Stop();
            }
            if (liveEqTimer != null)
            {
                liveEqTimer.Stop();
            }
            if (trayMenu != null && trayMenu.Visible)
            {
                trayMenu.Close(ToolStripDropDownCloseReason.CloseCalled);
            }
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            CleanupAfterClosed();
            base.OnFormClosed(e);
        }

        private void CleanupAfterClosed()
        {
            if (trayCleanupStarted) return;
            trayCleanupStarted = true;

            if (autoDetectTimer != null) autoDetectTimer.Dispose();
            if (liveEqTimer != null) liveEqTimer.Dispose();
            if (trayIcon != null)
            {
                trayIcon.ContextMenuStrip = null;
                trayIcon.Icon = null;
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
            if (heroImageBox != null && heroImageBox.Image != null)
            {
                Image image = heroImageBox.Image;
                heroImageBox.Image = null;
                image.Dispose();
            }
            Icon = null;
            notificationIcon.Dispose();
            windowIcon.Dispose();
        }

        private void HideToTrayFromClose()
        {
            ConcealMainWindow(removeFromTaskbar: true);
            Notify("Quick controls are available in the tray.");
        }

        private void ConcealMainWindow(bool removeFromTaskbar)
        {
            try
            {
                if (IsHandleCreated) ShowWindow(Handle, SwHide);
                Hide();
                if (removeFromTaskbar) ShowInTaskbar = false;
            }
            catch
            {
            }
        }

        private void QueueImmediateExit()
        {
            if (immediateExitQueued) return;
            immediateExitQueued = true;
            exiting = true;
            trayNotifications = false;
            ConcealMainWindow(removeFromTaskbar: true);
            try { Opacity = 0; } catch { }

            try
            {
                BeginInvoke(new Action(() =>
                {
                    CleanupAfterClosed();
                    Dispose();
                    Application.ExitThread();
                }));
            }
            catch
            {
                CleanupAfterClosed();
                Dispose();
                Application.ExitThread();
            }
        }

        private CardPanel CreateCard()
        {
            return new CardPanel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(CardMargin),
                Radius = 8,
                BackColor = card,
                BorderColor = line
            };
        }

        private void AddTitle(Control parent, string value, int top)
        {
            parent.Controls.Add(new Label
            {
                Text = value,
                ForeColor = ink,
                Font = new Font("Segoe UI Semibold", 14f),
                AutoSize = true,
                Location = new Point(CardInset, top)
            });
        }

        private void AddEyebrow(Control parent, string value, int top)
        {
            parent.Controls.Add(new Label
            {
                Text = value,
                ForeColor = Color.FromArgb(190, 198, 207),
                Font = new Font("Segoe UI Semibold", 9.2f),
                AutoSize = true,
                Location = new Point(CardInset, top)
            });
        }

        private Label AddActionRow(Control parent, string caption, string value, int top, params PillButton[] buttons)
        {
            var captionLabel = new Label
            {
                Text = caption,
                ForeColor = ink,
                Font = new Font("Segoe UI Semibold", 10f),
                AutoEllipsis = true,
                Location = new Point(CardInset, top),
                Size = new Size(220, 20)
            };
            parent.Controls.Add(captionLabel);

            var valueLabel = new Label
            {
                Text = value,
                ForeColor = subdued,
                Font = new Font("Segoe UI", 9.2f),
                AutoEllipsis = true,
                Location = new Point(CardInset, top + 22),
                Size = new Size(220, 20)
            };
            parent.Controls.Add(valueLabel);

            foreach (var button in buttons) parent.Controls.Add(button);
            Action layout = () =>
            {
                int buttonArea = 0;
                for (int i = 0; i < buttons.Length; i++)
                {
                    buttonArea += OptionButtonWidth(buttons[i]);
                    if (i > 0) buttonArea += 8;
                }
                int textWidth = Math.Max(140, parent.Width - buttonArea - (CardInset * 2 + 10));
                captionLabel.Width = textWidth;
                valueLabel.Width = textWidth;

                int x = parent.Width - CardInset;
                for (int i = buttons.Length - 1; i >= 0; i--)
                {
                    int width = OptionButtonWidth(buttons[i]);
                    x -= width;
                    buttons[i].Location = new Point(x, top + 8);
                    buttons[i].Size = new Size(width, 32);
                    x -= 8;
                }
            };
            parent.Resize += (s, e) => layout();
            layout();
            return valueLabel;
        }

        private static int OptionButtonWidth(PillButton button)
        {
            if (button == null) return 74;
            int textWidth = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            return Math.Max(74, Math.Min(164, textWidth + 34));
        }

        private void AddDivider(Control parent, int top)
        {
            var divider = new Panel
            {
                BackColor = line,
                Height = 1,
                Location = new Point(CardInset, top),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };
            parent.Controls.Add(divider);
            Action layout = () => divider.Width = Math.Max(80, parent.Width - (CardInset * 2));
            parent.Resize += (s, e) => layout();
            layout();
        }

        private void PlaceRow(Control parent, int top, int gap, int height, params PillButton[] buttons)
        {
            Action layout = () =>
            {
                int width = Math.Max(92, (parent.Width - (CardInset * 2) - (buttons.Length - 1) * gap) / buttons.Length);
                for (int i = 0; i < buttons.Length; i++)
                {
                    buttons[i].Location = new Point(CardInset + i * (width + gap), top);
                    buttons[i].Size = new Size(width, height);
                }
            };
            parent.Resize += (s, e) => layout();
            foreach (var button in buttons) parent.Controls.Add(button);
            layout();
        }

        private Image LoadImageAsset(string relativePath)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
                if (!File.Exists(path)) return null;
                using (var stream = new MemoryStream(File.ReadAllBytes(path)))
                using (var source = Image.FromStream(stream))
                {
                    return new Bitmap(source);
                }
            }
            catch
            {
                return null;
            }
        }

        private Icon CreateWindowIcon()
        {
            const int iconSize = 256;
            var bitmap = new Bitmap(iconSize, iconSize);
            using (var g = Graphics.FromImage(bitmap))
            using (var bgBrush = new SolidBrush(Color.FromArgb(22, 27, 34)))
            using (var earBrush = new SolidBrush(blue))
            using (var bandPen = new Pen(blue, 25f))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                bandPen.StartCap = LineCap.Round;
                bandPen.EndCap = LineCap.Round;
                g.Clear(Color.Transparent);
                g.FillEllipse(bgBrush, 10, 10, 236, 236);
                g.DrawArc(bandPen, 54, 48, 148, 140, 205, 130);
                FillRound(g, earBrush, new Rectangle(54, 126, 62, 84), 28);
                FillRound(g, earBrush, new Rectangle(140, 126, 62, 84), 28);
            }
            IntPtr handle = bitmap.GetHicon();
            Icon icon = (Icon)Icon.FromHandle(handle).Clone();
            DestroyIcon(handle);
            bitmap.Dispose();
            return icon;
        }

        private Icon CreateTrayIcon()
        {
            var bitmap = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bitmap))
            using (var glyphBrush = new SolidBrush(Color.FromArgb(226, 230, 235)))
            using (var bandPen = new Pen(Color.FromArgb(226, 230, 235), 3.2f))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                g.DrawArc(bandPen, 5.5f, 4.5f, 21f, 20f, 205, 130);
                FillRound(g, glyphBrush, new Rectangle(6, 17, 8, 10), 4);
                FillRound(g, glyphBrush, new Rectangle(18, 17, 8, 10), 4);
            }
            IntPtr handle = bitmap.GetHicon();
            Icon icon = (Icon)Icon.FromHandle(handle).Clone();
            DestroyIcon(handle);
            bitmap.Dispose();
            return icon;
        }

        private static void FillRound(Graphics g, Brush brush, Rectangle bounds, int radius)
        {
            using (var path = RoundedPath(bounds, radius))
            {
                g.FillPath(brush, path);
            }
        }

        private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static string Capitalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            return char.ToUpperInvariant(value[0]) + value.Substring(1).ToLowerInvariant();
        }

        private static int HexByte(string value)
        {
            return Convert.ToInt32(value, 16);
        }

        private static string FormatPowerDelay(int value)
        {
            switch (value)
            {
                case 0:
                    return "5 minutes";
                case 1:
                    return "30 minutes";
                case 2:
                    return "60 minutes";
                case 3:
                    return "180 minutes";
                case 4:
                    return "15 minutes";
                case 0x11:
                    return "Off";
                default:
                    return "Unknown";
            }
        }

        // The 6-band models lead with Clear Bass, which is worth calling out by
        // name. The 10-band layout has no such band, so the summary names the
        // lowest frequency instead of inventing one.
        // Clear Bass is a shelf control in its own right, so it earns a place in
        // the summary next to the preset name. Where there is no Clear Bass the
        // first band is just the lowest of many and says nothing useful on its
        // own, so the preset name stands alone and the curve and sliders below
        // carry the detail.
        private string FormatEqSummary(int preset, int[] values)
        {
            string presetName = eqProfile.PresetName(preset);
            if (!eqProfile.FirstBandIsClearBass) return presetName;
            if (!HasEqValues(values)) return presetName;
            return presetName + " / Clear Bass " + eqProfile.FormatValue(values[0]);
        }

        private static string FormatGeneralBoolean(int value)
        {
            switch (value)
            {
                case 0:
                    return "On";
                case 1:
                    return "Off";
                default:
                    return "0x" + value.ToString("X2");
            }
        }
    }

    // Everything about the equalizer that varies by model, so the rest of the UI
    // never hardcodes a band count again.
    //
    // The WF-1000XM6 reports ten octave bands (31 Hz to 16 kHz) over a 0-12 range
    // centred on 6. The WH/WF-1000XM5 has Clear Bass plus five bands over 0-20
    // centred on 10. The 5A 00 capability reply carries the band count and the
    // frequencies but no range or step, so the range has to be keyed off the band
    // count: 6 bands keeps the shipped XM5 behaviour, anything else is treated as
    // the newer 0-12 layout.
    internal sealed class EqProfile
    {
        public int BandCount { get; private set; }
        public int MaxValue { get; private set; }
        public bool FirstBandIsClearBass { get; private set; }
        public string[] Labels { get; private set; }
        // The XM6 does not answer the 6-band preset-only form (58 00 <preset> 00)
        // at all; it selects presets on inquired type 04 instead.
        public bool PresetsUseType04 { get; private set; }
        public EqPresetChoice[] Presets { get; private set; }

        public int Neutral { get { return MaxValue / 2; } }

        public int Clamp(int value)
        {
            if (value < 0) return 0;
            return value > MaxValue ? MaxValue : value;
        }

        public string FormatValue(int value)
        {
            int centered = Clamp(value) - Neutral;
            return centered > 0 ? "+" + centered : centered.ToString();
        }

        public string PresetName(int preset)
        {
            foreach (var choice in Presets)
            {
                if (choice.Value == preset) return choice.Text;
            }
            return "0x" + preset.ToString("X2");
        }

        public bool Matches(int bandCount, string[] labels)
        {
            if (BandCount != bandCount || Labels.Length != labels.Length) return false;
            for (int i = 0; i < labels.Length; i++)
            {
                if (!string.Equals(Labels[i], labels[i], StringComparison.Ordinal)) return false;
            }
            return true;
        }

        public static EqProfile Legacy6Band()
        {
            return new EqProfile
            {
                BandCount = 6,
                MaxValue = 20,
                FirstBandIsClearBass = true,
                Labels = new[] { "Clear Bass", "400", "1k", "2.5k", "6.3k", "16k" },
                PresetsUseType04 = false,
                Presets = new[]
                {
                    new EqPresetChoice("Off", 0x00),
                    new EqPresetChoice("Bright", 0x10),
                    new EqPresetChoice("Excited", 0x11),
                    new EqPresetChoice("Mellow", 0x12),
                    new EqPresetChoice("Relaxed", 0x13),
                    new EqPresetChoice("Vocal", 0x14),
                    new EqPresetChoice("Treble", 0x15),
                    new EqPresetChoice("Bass", 0x16),
                    new EqPresetChoice("Speech", 0x17),
                    new EqPresetChoice("Manual", 0xA0),
                    new EqPresetChoice("User 1", 0xA1),
                    new EqPresetChoice("User 2", 0xA2)
                }
            };
        }

        public static EqProfile FromCapability(int bandCount, int[] frequencies)
        {
            if (bandCount == 6) return Legacy6Band();

            var labels = new string[bandCount];
            for (int i = 0; i < bandCount; i++)
            {
                labels[i] = frequencies != null && i < frequencies.Length
                    ? FormatFrequency(frequencies[i])
                    : (i + 1).ToString();
            }

            return new EqProfile
            {
                BandCount = bandCount,
                MaxValue = 12,
                FirstBandIsClearBass = false,
                Labels = labels,
                PresetsUseType04 = true,
                // Every id below was read back from a WF-1000XM6 with that preset
                // selected in Sound Connect. The ids are grouped rather than
                // consecutive — Game sits at 0x20 while the tone presets run
                // 0x30-0x33 — so they are listed explicitly, not generated. The
                // 0x10 block used by the 6-band profile is not this model's
                // vocabulary and draws no response here.
                //
                // Order follows Sound Connect's own list, which shows Game after
                // Soft despite the lower id.
                Presets = new[]
                {
                    new EqPresetChoice("Off", 0x00),
                    new EqPresetChoice("Heavy", 0x30),
                    new EqPresetChoice("Clear", 0x31),
                    new EqPresetChoice("Hard", 0x32),
                    new EqPresetChoice("Soft", 0x33),
                    new EqPresetChoice("Game", 0x20),
                    new EqPresetChoice("Manual", 0xA0),
                    new EqPresetChoice("Custom 1", 0xA1),
                    new EqPresetChoice("Custom 2", 0xA2)
                }
            };
        }

        public static string FormatFrequency(int hz)
        {
            if (hz < 1000) return hz.ToString();
            double k = hz / 1000.0;
            return (k == Math.Floor(k) ? k.ToString("0") : k.ToString("0.#")) + "k";
        }
    }

    internal sealed class EqPresetChoice
    {
        public string Text { get; private set; }
        public int Value { get; private set; }

        public EqPresetChoice(string text, int value)
        {
            Text = text;
            Value = value;
        }

        public override string ToString()
        {
            return Text;
        }
    }

    internal sealed class AppSettingsDialog : Form
    {
        private readonly Color page;
        private readonly Color card;
        private readonly Color line;
        private readonly Color ink;
        private readonly Color subdued;
        private readonly Color blue;
        private readonly Color bluePressed;
        private readonly Color inactive;
        private readonly Color inactivePressed;

        private PillButton startAtBootOnButton;
        private PillButton startAtBootOffButton;
        private PillButton startMinimizedOnButton;
        private PillButton startMinimizedOffButton;
        private PillButton minimizeOnCloseOnButton;
        private PillButton minimizeOnCloseOffButton;

        public bool StartAtBoot { get; private set; }
        public bool StartMinimizedInTray { get; private set; }
        public bool MinimizeToTrayOnClose { get; private set; }

        public AppSettingsDialog(bool startAtBoot, bool startMinimizedInTray, bool minimizeToTrayOnClose, Color page, Color card, Color cardSoft, Color line, Color ink, Color subdued, Color blue, Color bluePressed)
        {
            this.page = page;
            this.card = card;
            this.line = line;
            this.ink = ink;
            this.subdued = subdued;
            this.blue = blue;
            this.bluePressed = bluePressed;
            inactive = Color.FromArgb(61, 67, 76);
            inactivePressed = Color.FromArgb(50, 55, 63);
            StartAtBoot = startAtBoot;
            StartMinimizedInTray = startMinimizedInTray;
            MinimizeToTrayOnClose = minimizeToTrayOnClose;

            Text = "App settings";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(560, 420);
            BackColor = page;
            ForeColor = ink;
            Font = new Font("Segoe UI", 10f);
            Build();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TryApplyWindows11Chrome();
        }

        private void Build()
        {
            var title = new Label
            {
                Text = "App settings",
                ForeColor = ink,
                Font = new Font("Segoe UI Semibold", 17f),
                AutoSize = true,
                Location = new Point(24, 22)
            };
            Controls.Add(title);

            var panel = new Panel
            {
                Location = new Point(24, 70),
                Size = new Size(512, 236),
                BackColor = card,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(panel);

            startAtBootOnButton = NewSettingButton("On");
            startAtBootOffButton = NewSettingButton("Off");
            AddSettingRow(panel, "Start at login", "Open XM Control when you sign in.", 22, startAtBootOnButton, startAtBootOffButton);
            startAtBootOnButton.Click += (s, e) => SetStartAtBoot(true);
            startAtBootOffButton.Click += (s, e) => SetStartAtBoot(false);

            AddDivider(panel, 82);

            startMinimizedOnButton = NewSettingButton("On");
            startMinimizedOffButton = NewSettingButton("Off");
            AddSettingRow(panel, "Start minimized in tray", "Launch quietly and keep quick controls in the tray.", 104, startMinimizedOnButton, startMinimizedOffButton);
            startMinimizedOnButton.Click += (s, e) => SetStartMinimizedInTray(true);
            startMinimizedOffButton.Click += (s, e) => SetStartMinimizedInTray(false);

            AddDivider(panel, 160);

            minimizeOnCloseOnButton = NewSettingButton("On");
            minimizeOnCloseOffButton = NewSettingButton("Off");
            AddSettingRow(panel, "Minimize to tray on close", "The close button hides the app instead of exiting.", 182, minimizeOnCloseOnButton, minimizeOnCloseOffButton);
            minimizeOnCloseOnButton.Click += (s, e) => SetMinimizeToTrayOnClose(true);
            minimizeOnCloseOffButton.Click += (s, e) => SetMinimizeToTrayOnClose(false);

            var save = new PillButton("Save", blue, bluePressed)
            {
                Size = new Size(96, 34),
                Location = new Point(ClientSize.Width - 222, ClientSize.Height - 54),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            save.Click += (s, e) =>
            {
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(save);

            var cancel = new PillButton("Cancel", inactive, inactivePressed)
            {
                Size = new Size(96, 34),
                Location = new Point(ClientSize.Width - 116, ClientSize.Height - 54),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            cancel.Click += (s, e) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };
            Controls.Add(cancel);

            SetStartAtBoot(StartAtBoot);
            SetStartMinimizedInTray(StartMinimizedInTray);
            SetMinimizeToTrayOnClose(MinimizeToTrayOnClose);
        }

        private PillButton NewSettingButton(string text)
        {
            return new PillButton(text, inactive, inactivePressed)
            {
                Size = new Size(74, 32)
            };
        }

        private void AddSettingRow(Control parent, string caption, string description, int top, PillButton onButton, PillButton offButton)
        {
            int buttonBlockWidth = 178;
            int textWidth = Math.Max(190, parent.Width - buttonBlockWidth - 54);
            var captionLabel = new Label
            {
                Text = caption,
                ForeColor = ink,
                Font = new Font("Segoe UI Semibold", 10f),
                Location = new Point(20, top),
                Size = new Size(textWidth, 22),
                AutoEllipsis = true
            };
            parent.Controls.Add(captionLabel);

            var descriptionLabel = new Label
            {
                Text = description,
                ForeColor = subdued,
                Font = new Font("Segoe UI", 9.2f),
                Location = new Point(20, top + 23),
                Size = new Size(textWidth, 22),
                AutoEllipsis = true
            };
            parent.Controls.Add(descriptionLabel);

            offButton.Location = new Point(parent.Width - 94, top + 9);
            offButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            onButton.Location = new Point(offButton.Left - 84, top + 9);
            onButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            parent.Controls.Add(onButton);
            parent.Controls.Add(offButton);
        }

        private void AddDivider(Control parent, int top)
        {
            var divider = new Panel
            {
                BackColor = line,
                Location = new Point(20, top),
                Size = new Size(parent.Width - 40, 1),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            parent.Controls.Add(divider);
        }

        private void SetStartAtBoot(bool enabled)
        {
            StartAtBoot = enabled;
            SetPair(startAtBootOnButton, startAtBootOffButton, enabled);
        }

        private void SetStartMinimizedInTray(bool enabled)
        {
            StartMinimizedInTray = enabled;
            SetPair(startMinimizedOnButton, startMinimizedOffButton, enabled);
        }

        private void SetMinimizeToTrayOnClose(bool enabled)
        {
            MinimizeToTrayOnClose = enabled;
            SetPair(minimizeOnCloseOnButton, minimizeOnCloseOffButton, enabled);
        }

        private void SetPair(PillButton onButton, PillButton offButton, bool enabled)
        {
            if (onButton != null) onButton.SetPalette(enabled ? blue : inactive, enabled ? bluePressed : inactivePressed);
            if (offButton != null) offButton.SetPalette(!enabled ? blue : inactive, !enabled ? bluePressed : inactivePressed);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private void TryApplyWindows11Chrome()
        {
            try
            {
                int enabled = 1;
                DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int));
                DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));

                int rounded = 2;
                DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));

                int caption = ColorTranslator.ToWin32(page);
                int captionText = ColorTranslator.ToWin32(ink);
                int border = ColorTranslator.ToWin32(page);
                DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
                DwmSetWindowAttribute(Handle, 36, ref captionText, sizeof(int));
                DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
            }
            catch
            {
            }
        }
    }

    internal sealed class ShortcutDialog : Form
    {
        private readonly ShortcutAction[] actions;
        private readonly Dictionary<string, HotkeyCaptureBox> boxes = new Dictionary<string, HotkeyCaptureBox>(StringComparer.OrdinalIgnoreCase);
        private readonly Color page;
        private readonly Color card;
        private readonly Color cardSoft;
        private readonly Color line;
        private readonly Color ink;
        private readonly Color subdued;
        private readonly Color blue;
        private readonly Color bluePressed;

        public Dictionary<string, ShortcutBinding> Bindings { get; private set; }

        public ShortcutDialog(ShortcutAction[] actions, Dictionary<string, ShortcutBinding> current, Color page, Color card, Color cardSoft, Color line, Color ink, Color subdued, Color blue, Color bluePressed)
        {
            this.actions = actions;
            this.page = page;
            this.card = card;
            this.cardSoft = cardSoft;
            this.line = line;
            this.ink = ink;
            this.subdued = subdued;
            this.blue = blue;
            this.bluePressed = bluePressed;
            Bindings = new Dictionary<string, ShortcutBinding>(StringComparer.OrdinalIgnoreCase);

            Text = "Keyboard shortcuts";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(640, 600);
            BackColor = page;
            ForeColor = ink;
            Font = new Font("Segoe UI", 10f);
            Build(current);
        }

        private void Build(Dictionary<string, ShortcutBinding> current)
        {
            var title = new Label
            {
                Text = "Keyboard shortcuts",
                ForeColor = ink,
                Font = new Font("Segoe UI Semibold", 17f),
                AutoSize = true,
                Location = new Point(24, 22)
            };
            Controls.Add(title);

            var panel = new Panel
            {
                Location = new Point(24, 68),
                Size = new Size(592, 450),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                AutoScroll = true,
                BackColor = card
            };
            Controls.Add(panel);

            for (int i = 0; i < actions.Length; i++)
            {
                ShortcutAction action = actions[i];
                int y = i * 44 + 12;
                var label = new Label
                {
                    Text = action.Label,
                    ForeColor = ink,
                    Font = new Font("Segoe UI Semibold", 9.5f),
                    Location = new Point(18, y + 7),
                    Size = new Size(250, 24),
                    AutoEllipsis = true
                };
                panel.Controls.Add(label);

                ShortcutBinding binding;
                if (!current.TryGetValue(action.Id, out binding) || binding == null) binding = ShortcutBinding.None();
                var box = new HotkeyCaptureBox(cardSoft, line, ink, subdued)
                {
                    Binding = binding.Clone(),
                    Location = new Point(278, y + 2),
                    Size = new Size(194, 30)
                };
                panel.Controls.Add(box);
                boxes[action.Id] = box;

                var clear = new PillButton("Clear", Color.FromArgb(61, 67, 76), Color.FromArgb(50, 55, 63))
                {
                    Location = new Point(486, y + 1),
                    Size = new Size(70, 31)
                };
                clear.Click += (s, e) => box.Binding = ShortcutBinding.None();
                panel.Controls.Add(clear);
            }

            var save = new PillButton("Save", blue, bluePressed)
            {
                Size = new Size(96, 34),
                Location = new Point(ClientSize.Width - 222, ClientSize.Height - 54),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            save.Click += (s, e) => SaveAndClose();
            Controls.Add(save);

            var cancel = new PillButton("Cancel", Color.FromArgb(61, 67, 76), Color.FromArgb(50, 55, 63))
            {
                Size = new Size(96, 34),
                Location = new Point(ClientSize.Width - 116, ClientSize.Height - 54),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            cancel.Click += (s, e) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };
            Controls.Add(cancel);
        }

        private void SaveAndClose()
        {
            var signatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var next = new Dictionary<string, ShortcutBinding>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < actions.Length; i++)
            {
                HotkeyCaptureBox box = boxes[actions[i].Id];
                ShortcutBinding binding = box.Binding == null ? ShortcutBinding.None() : box.Binding.Clone();
                if (binding.IsAssigned && !binding.IsGlobalSafe)
                {
                    MessageBox.Show("Use a modifier key, or use an F-key.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    box.Focus();
                    return;
                }
                if (binding.IsAssigned && !signatures.Add(binding.Signature))
                {
                    MessageBox.Show("That shortcut is assigned more than once.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    box.Focus();
                    return;
                }
                next[actions[i].Id] = binding;
            }

            Bindings = next;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    internal sealed class HotkeyCaptureBox : TextBox
    {
        private ShortcutBinding binding;
        private readonly Color emptyColor;
        private readonly Color textColor;

        public ShortcutBinding Binding
        {
            get { return binding; }
            set
            {
                binding = value == null ? ShortcutBinding.None() : value;
                UpdateText();
            }
        }

        public HotkeyCaptureBox(Color background, Color border, Color ink, Color subdued)
        {
            binding = ShortcutBinding.None();
            emptyColor = subdued;
            textColor = ink;
            BackColor = background;
            ForeColor = subdued;
            BorderStyle = BorderStyle.FixedSingle;
            ReadOnly = true;
            ShortcutsEnabled = false;
            Font = new Font("Segoe UI", 9.5f);
            TextAlign = HorizontalAlignment.Center;
            TabStop = true;
            UpdateText();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.Tab) return base.ProcessCmdKey(ref msg, keyData);
            if (key == Keys.Back || key == Keys.Delete || key == Keys.Escape)
            {
                Binding = ShortcutBinding.None();
                return true;
            }
            if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu || key == Keys.LWin || key == Keys.RWin)
            {
                return true;
            }

            bool ctrl = (keyData & Keys.Control) == Keys.Control;
            bool alt = (keyData & Keys.Alt) == Keys.Alt;
            bool shift = (keyData & Keys.Shift) == Keys.Shift;
            if (!ctrl && !alt && !shift && !ShortcutBinding.IsFunctionKey(key))
            {
                System.Media.SystemSounds.Beep.Play();
                return true;
            }

            Binding = new ShortcutBinding(ctrl, alt, shift, false, key);
            return true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            base.OnKeyDown(e);
        }

        private void UpdateText()
        {
            Text = binding == null ? "None" : binding.ToString();
            ForeColor = binding != null && binding.IsAssigned ? textColor : emptyColor;
        }
    }

    internal sealed class ChoiceDropdown : Control
    {
        private readonly List<object> items = new List<object>();
        private int selectedIndex = -1;
        private bool hover;
        private bool down;

        public event EventHandler SelectedIndexChanged;

        public ChoiceItemCollection Items { get; private set; }
        public ComboBoxStyle DropDownStyle { get; set; }
        public FlatStyle FlatStyle { get; set; }

        public int SelectedIndex
        {
            get { return selectedIndex; }
            set
            {
                int next = value < -1 ? -1 : value;
                if (next >= items.Count) next = items.Count - 1;
                if (selectedIndex == next) return;
                selectedIndex = next;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public object SelectedItem
        {
            get { return selectedIndex >= 0 && selectedIndex < items.Count ? items[selectedIndex] : null; }
        }

        public ChoiceDropdown()
        {
            Items = new ChoiceItemCollection(this);
            Size = new Size(170, 30);
            Font = new Font("Segoe UI", 9.3f);
            Cursor = Cursors.Hand;
            TabStop = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        }

        internal void AddItem(object item)
        {
            items.Add(item);
            if (selectedIndex == -1) selectedIndex = 0;
            Invalidate();
        }

        internal void ClearItems()
        {
            items.Clear();
            selectedIndex = -1;
            Invalidate();
        }

        internal object GetItem(int index)
        {
            return items[index];
        }

        internal int Count
        {
            get { return items.Count; }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled)
            {
                down = true;
                Focus();
                Invalidate();
                ShowMenu();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            down = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                ShowMenu();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Down && selectedIndex < items.Count - 1)
            {
                SelectedIndex++;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Up && selectedIndex > 0)
            {
                SelectedIndex--;
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = Enabled ? (down ? Blend(BackColor, Color.Black, 0.12f) : (hover ? Blend(BackColor, Color.White, 0.05f) : BackColor)) : Color.FromArgb(48, 51, 57);
            Color border = Focused ? Color.FromArgb(16, 157, 245) : Color.FromArgb(82, 88, 98);
            using (var path = RoundedPath(bounds, 6))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(border))
            {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }

            Rectangle textBounds = new Rectangle(12, 1, Math.Max(10, Width - 38), Height - 2);
            string text = SelectedItem == null ? "" : SelectedItem.ToString();
            TextRenderer.DrawText(e.Graphics, text, Font, textBounds, Enabled ? ForeColor : Color.FromArgb(135, 140, 148), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            Point[] arrow =
            {
                new Point(Width - 21, Height / 2 - 2),
                new Point(Width - 11, Height / 2 - 2),
                new Point(Width - 16, Height / 2 + 4)
            };
            using (var brush = new SolidBrush(Enabled ? Color.FromArgb(210, 216, 224) : Color.FromArgb(120, 126, 134)))
            {
                e.Graphics.FillPolygon(brush, arrow);
            }
        }

        private void ShowMenu()
        {
            if (items.Count == 0 || IsDisposed || !IsHandleCreated) return;
            var menu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(36, 38, 42),
                ForeColor = Color.White,
                ShowImageMargin = false,
                Padding = new Padding(4)
            };
            for (int i = 0; i < items.Count; i++)
            {
                int index = i;
                var item = new ToolStripMenuItem(items[i].ToString())
                {
                    Checked = index == selectedIndex,
                    BackColor = Color.FromArgb(36, 38, 42),
                    ForeColor = Color.White
                };
                item.Click += (s, e) => SelectedIndex = index;
                menu.Items.Add(item);
            }
            menu.Closed += (s, e) =>
            {
                down = false;
                if (!IsDisposed) Invalidate();
                if (!IsDisposed && IsHandleCreated)
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (!menu.IsDisposed) menu.Dispose();
                        }));
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            };
            try
            {
                menu.Show(this, new Point(0, Height + 3));
            }
            catch
            {
                menu.Dispose();
            }
        }

        private static Color Blend(Color a, Color b, float amount)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * amount),
                (int)(a.G + (b.G - a.G) * amount),
                (int)(a.B + (b.B - a.B) * amount));
        }

        private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            radius = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
            if (radius == 0)
            {
                var rectangle = new GraphicsPath();
                rectangle.AddRectangle(bounds);
                rectangle.CloseFigure();
                return rectangle;
            }
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        internal sealed class ChoiceItemCollection
        {
            private readonly ChoiceDropdown owner;

            public ChoiceItemCollection(ChoiceDropdown owner)
            {
                this.owner = owner;
            }

            public int Count
            {
                get { return owner.Count; }
            }

            public object this[int index]
            {
                get { return owner.GetItem(index); }
            }

            public void AddRange(object[] values)
            {
                foreach (object value in values) owner.AddItem(value);
            }

            public void Add(object value)
            {
                owner.AddItem(value);
            }

            public void Clear()
            {
                owner.ClearItems();
            }
        }
    }

    internal sealed class SliderControl : Control
    {
        private int minimum;
        private int maximum = 100;
        private int value;
        private bool dragging;

        public event EventHandler ValueChanged;

        public int Minimum
        {
            get { return minimum; }
            set
            {
                minimum = value;
                if (maximum < minimum) maximum = minimum;
                Value = this.value;
                Invalidate();
            }
        }

        public int Maximum
        {
            get { return maximum; }
            set
            {
                maximum = Math.Max(minimum, value);
                Value = this.value;
                Invalidate();
            }
        }

        public int Value
        {
            get { return value; }
            set
            {
                int next = Math.Max(minimum, Math.Min(maximum, value));
                if (this.value == next) return;
                this.value = next;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        public int TickFrequency { get; set; }
        public int SmallChange { get; set; }
        public int LargeChange { get; set; }
        public bool CenteredFill { get; set; }
        // Minimum at the bottom and maximum at the top, the way a graphic
        // equalizer's faders read.
        public bool Vertical { get; set; }
        public Color TrackColor { get; set; }
        public Color FillColor { get; set; }
        public Color ThumbColor { get; set; }
        public Color TickColor { get; set; }

        public SliderControl()
        {
            Size = new Size(220, 34);
            TickFrequency = 5;
            SmallChange = 1;
            LargeChange = 2;
            TrackColor = Color.FromArgb(78, 84, 94);
            FillColor = Color.FromArgb(16, 157, 245);
            ThumbColor = Color.FromArgb(16, 157, 245);
            TickColor = Color.FromArgb(91, 98, 108);
            Cursor = Cursors.Hand;
            TabStop = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled)
            {
                dragging = true;
                Capture = true;
                Focus();
                SetValueFromPointer(e.Location);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging) SetValueFromPointer(e.Location);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            dragging = false;
            Capture = false;
            base.OnMouseUp(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down)
            {
                Value -= Math.Max(1, SmallChange);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up)
            {
                Value += Math.Max(1, SmallChange);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.PageDown)
            {
                Value -= Math.Max(1, LargeChange);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.PageUp)
            {
                Value += Math.Max(1, LargeChange);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        // The arrow keys move focus between controls unless claimed here, and a
        // vertical slider needs Up and Down as much as a horizontal one needs
        // Left and Right.
        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left:
                case Keys.Right:
                case Keys.Up:
                case Keys.Down:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (Vertical)
            {
                PaintVertical(e.Graphics);
                return;
            }

            Rectangle track = new Rectangle(12, Height / 2 - 3, Math.Max(8, Width - 24), 6);
            float ratio = maximum == minimum ? 0f : (Value - minimum) / (float)(maximum - minimum);
            int thumbX = track.Left + (int)Math.Round(track.Width * ratio);
            int fillStart = CenteredFill ? track.Left + track.Width / 2 : track.Left;
            int fillLeft = Math.Min(fillStart, thumbX);
            int fillRight = Math.Max(fillStart, thumbX);

            using (var trackBrush = new SolidBrush(Enabled ? TrackColor : Color.FromArgb(54, 58, 65)))
            using (var fillBrush = new SolidBrush(Enabled ? FillColor : Color.FromArgb(85, 90, 98)))
            using (var thumbBrush = new SolidBrush(Enabled ? ThumbColor : Color.FromArgb(95, 101, 110)))
            using (var thumbPen = new Pen(Color.FromArgb(26, 28, 31), 2f))
            {
                FillRound(e.Graphics, trackBrush, track, 3);
                if (fillRight > fillLeft)
                {
                    FillRound(e.Graphics, fillBrush, new Rectangle(fillLeft, track.Top, fillRight - fillLeft, track.Height), 3);
                }

                if (TickFrequency > 0 && maximum > minimum)
                {
                    using (var tickPen = new Pen(TickColor, 1f))
                    {
                        for (int tick = minimum; tick <= maximum; tick += TickFrequency)
                        {
                            float tickRatio = (tick - minimum) / (float)(maximum - minimum);
                            int x = track.Left + (int)Math.Round(track.Width * tickRatio);
                            e.Graphics.DrawLine(tickPen, x, track.Bottom + 6, x, track.Bottom + 9);
                        }
                    }
                }

                Rectangle thumb = new Rectangle(thumbX - 7, Height / 2 - 9, 14, 18);
                FillRound(e.Graphics, thumbBrush, thumb, 6);
                using (var path = RoundedPath(thumb, 6))
                {
                    e.Graphics.DrawPath(thumbPen, path);
                }
            }
        }

        private void PaintVertical(Graphics g)
        {
            Rectangle track = new Rectangle(Width / 2 - 3, 12, 6, Math.Max(8, Height - 24));
            float ratio = maximum == minimum ? 0f : (Value - minimum) / (float)(maximum - minimum);
            int thumbY = track.Bottom - (int)Math.Round(track.Height * ratio);
            int fillStart = CenteredFill ? track.Top + track.Height / 2 : track.Bottom;
            int fillTop = Math.Min(fillStart, thumbY);
            int fillBottom = Math.Max(fillStart, thumbY);

            using (var trackBrush = new SolidBrush(Enabled ? TrackColor : Color.FromArgb(54, 58, 65)))
            using (var fillBrush = new SolidBrush(Enabled ? FillColor : Color.FromArgb(85, 90, 98)))
            using (var thumbBrush = new SolidBrush(Enabled ? ThumbColor : Color.FromArgb(95, 101, 110)))
            using (var thumbPen = new Pen(Color.FromArgb(26, 28, 31), 2f))
            {
                FillRound(g, trackBrush, track, 3);
                if (fillBottom > fillTop)
                {
                    FillRound(g, fillBrush, new Rectangle(track.Left, fillTop, track.Width, fillBottom - fillTop), 3);
                }

                if (TickFrequency > 0 && maximum > minimum)
                {
                    using (var tickPen = new Pen(TickColor, 1f))
                    {
                        for (int tick = minimum; tick <= maximum; tick += TickFrequency)
                        {
                            float tickRatio = (tick - minimum) / (float)(maximum - minimum);
                            int y = track.Bottom - (int)Math.Round(track.Height * tickRatio);
                            g.DrawLine(tickPen, track.Right + 6, y, track.Right + 9, y);
                        }
                    }
                }

                Rectangle thumb = new Rectangle(Width / 2 - 9, thumbY - 7, 18, 14);
                FillRound(g, thumbBrush, thumb, 6);
                using (var path = RoundedPath(thumb, 6))
                {
                    g.DrawPath(thumbPen, path);
                }
            }
        }

        private void SetValueFromPointer(Point point)
        {
            int start = 12;
            int length = Math.Max(1, (Vertical ? Height : Width) - 24);
            int position = Vertical ? start + length - (point.Y - start) : point.X;
            float ratio = (Math.Max(start, Math.Min(start + length, position)) - start) / (float)length;
            Value = minimum + (int)Math.Round((maximum - minimum) * ratio);
        }

        private static void FillRound(Graphics g, Brush brush, Rectangle bounds, int radius)
        {
            using (var path = RoundedPath(bounds, radius))
            {
                g.FillPath(brush, path);
            }
        }

        private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            radius = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
            if (radius == 0)
            {
                var rectangle = new GraphicsPath();
                rectangle.AddRectangle(bounds);
                rectangle.CloseFigure();
                return rectangle;
            }
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class DeviceGlyphControl : Control
    {
        public Color BandColor { get; set; }
        public Color AccentColor { get; set; }
        public Color SoftColor { get; set; }
        public Color MutedColor { get; set; }

        public DeviceGlyphControl()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BandColor = Color.FromArgb(16, 157, 245);
            AccentColor = Color.FromArgb(16, 157, 245);
            SoftColor = Color.FromArgb(58, 64, 72);
            MutedColor = Color.FromArgb(169, 169, 169);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle bounds = ClientRectangle;
            bounds.Inflate(-4, -4);
            if (bounds.Width < 80 || bounds.Height < 70) return;

            int centerX = bounds.Left + bounds.Width / 2;
            int top = bounds.Top + 8;
            int cupW = Math.Max(24, bounds.Width / 4);
            int cupH = Math.Max(42, bounds.Height / 2);
            int cupY = bounds.Bottom - cupH - 10;
            var leftCup = new Rectangle(bounds.Left + 14, cupY, cupW, cupH);
            var rightCup = new Rectangle(bounds.Right - cupW - 14, cupY, cupW, cupH);
            var arcRect = new Rectangle(bounds.Left + 24, top, bounds.Width - 48, bounds.Height - 22);

            using (var shadow = new SolidBrush(Color.FromArgb(55, Color.Black)))
            using (var soft = new SolidBrush(SoftColor))
            using (var glow = new SolidBrush(Color.FromArgb(42, AccentColor)))
            using (var band = new Pen(BandColor, 5.2f))
            using (var innerBand = new Pen(Color.FromArgb(120, AccentColor), 2.1f))
            using (var cupEdge = new Pen(Color.FromArgb(95, 104, 116), 1.2f))
            using (var pad = new SolidBrush(Color.FromArgb(66, BandColor)))
            {
                e.Graphics.FillEllipse(glow, centerX - bounds.Width / 3, top + 10, bounds.Width * 2 / 3, bounds.Height - 12);
                e.Graphics.DrawArc(band, arcRect, 205, 130);
                e.Graphics.DrawArc(innerBand, new Rectangle(arcRect.Left + 12, arcRect.Top + 12, arcRect.Width - 24, arcRect.Height - 24), 210, 120);

                FillRound(e.Graphics, shadow, new Rectangle(leftCup.Left + 2, leftCup.Top + 3, leftCup.Width, leftCup.Height), 10);
                FillRound(e.Graphics, shadow, new Rectangle(rightCup.Left + 2, rightCup.Top + 3, rightCup.Width, rightCup.Height), 10);
                FillRound(e.Graphics, soft, leftCup, 10);
                FillRound(e.Graphics, soft, rightCup, 10);
                using (var leftPath = RoundedPath(leftCup, 10))
                using (var rightPath = RoundedPath(rightCup, 10))
                {
                    e.Graphics.DrawPath(cupEdge, leftPath);
                    e.Graphics.DrawPath(cupEdge, rightPath);
                }

                FillRound(e.Graphics, pad, new Rectangle(leftCup.Left + 6, leftCup.Top + 8, leftCup.Width - 12, leftCup.Height - 16), 7);
                FillRound(e.Graphics, pad, new Rectangle(rightCup.Left + 6, rightCup.Top + 8, rightCup.Width - 12, rightCup.Height - 16), 7);
            }
        }

        private static void FillRound(Graphics g, Brush brush, Rectangle bounds, int radius)
        {
            using (var path = RoundedPath(bounds, radius))
            {
                g.FillPath(brush, path);
            }
        }

        private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            radius = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
            if (radius == 0)
            {
                var rectangle = new GraphicsPath();
                rectangle.AddRectangle(bounds);
                rectangle.CloseFigure();
                return rectangle;
            }
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class CardPanel : Panel
    {
        public int Radius { get; set; }
        public Color BorderColor { get; set; }

        public CardPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle bounds = ClientRectangle;
            bounds.Width -= 1;
            bounds.Height -= 1;
            using (var brush = new SolidBrush(BackColor))
            using (var pen = new Pen(BorderColor))
            using (var path = RoundedPath(bounds, Radius))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            UpdateRoundedRegion();
            Invalidate();
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            UpdateRoundedRegion();
        }

        private void UpdateRoundedRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            Rectangle bounds = new Rectangle(0, 0, Width, Height);
            using (var path = RoundedPath(bounds, Radius))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            radius = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
            if (radius == 0)
            {
                var rectangle = new GraphicsPath();
                rectangle.AddRectangle(bounds);
                rectangle.CloseFigure();
                return rectangle;
            }
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class GearButton : Button
    {
        private readonly Color normal;
        private readonly Color hoverFill;
        private readonly Color pressedFill;
        private bool hover;
        private bool down;

        public GearButton(Color normal, Color hoverFill, Color pressedFill)
        {
            this.normal = normal;
            this.hoverFill = hoverFill;
            this.pressedFill = pressedFill;
            FlatStyle = FlatStyle.Flat;
            BackColor = Color.Transparent;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseDownBackColor = Color.Transparent;
            FlatAppearance.MouseOverBackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = false;
            down = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            if (mevent.Button == MouseButtons.Left)
            {
                down = true;
                Invalidate();
            }
            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            down = false;
            Invalidate();
            base.OnMouseUp(mevent);
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            pevent.Graphics.Clear(Parent != null ? Parent.BackColor : Color.Transparent);
            pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            if (hover || down)
            {
                Color fill = down ? pressedFill : hoverFill;
                Rectangle bounds = new Rectangle(1, 1, Width - 3, Height - 3);
                using (var path = RoundedPath(bounds, 8))
                using (var brush = new SolidBrush(fill))
                {
                    pevent.Graphics.FillPath(brush, path);
                }
            }

            DrawGear(
                pevent.Graphics,
                new PointF(Width / 2f, Height / 2f),
                Math.Max(9, Math.Min(Width, Height) * 0.31f),
                Color.FromArgb(154, 162, 172),
                normal,
                Color.FromArgb(0x10, 0x9d, 0xf5));
        }

        private static void DrawGear(Graphics graphics, PointF center, float radius, Color gearColor, Color centerCutoutColor, Color accentColor)
        {
            using (var gearPath = GearPath(center, radius))
            using (var gearBrush = new SolidBrush(gearColor))
            using (var gearPen = new Pen(Color.FromArgb(102, 111, 122), 1.1f))
            {
                graphics.FillPath(gearBrush, gearPath);
                graphics.DrawPath(gearPen, gearPath);
            }

            float cutoutRadius = radius * 0.55f;
            RectangleF cutout = new RectangleF(center.X - cutoutRadius, center.Y - cutoutRadius, cutoutRadius * 2, cutoutRadius * 2);
            using (var cutoutBrush = new SolidBrush(centerCutoutColor))
            {
                graphics.FillEllipse(cutoutBrush, cutout);
            }

            float accentRadius = radius * 0.33f;
            RectangleF accent = new RectangleF(center.X - accentRadius, center.Y - accentRadius, accentRadius * 2, accentRadius * 2);
            using (var accentBrush = new SolidBrush(accentColor))
            using (var accentPen = new Pen(Color.FromArgb(7, 79, 125), 1.0f))
            {
                graphics.FillEllipse(accentBrush, accent);
                graphics.DrawEllipse(accentPen, accent);
            }
        }

        private static GraphicsPath GearPath(PointF center, float radius)
        {
            int teeth = 8;
            double step = (Math.PI * 2) / teeth;
            float rootRadius = radius * 0.78f;
            float outerRadius = radius * 1.12f;
            var path = new GraphicsPath();
            for (int i = 0; i < teeth; i++)
            {
                double angle = -Math.PI / 2 + (i * step);
                PointF[] points =
                {
                    Polar(center, rootRadius, angle - step * 0.47),
                    Polar(center, outerRadius, angle - step * 0.26),
                    Polar(center, outerRadius, angle + step * 0.26),
                    Polar(center, rootRadius, angle + step * 0.47)
                };
                if (i == 0) path.AddLines(points);
                else path.AddLines(points);
            }
            path.CloseFigure();
            return path;
        }

        private static PointF Polar(PointF center, float radius, double angle)
        {
            return new PointF(
                center.X + (float)Math.Cos(angle) * radius,
                center.Y + (float)Math.Sin(angle) * radius);
        }

        private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class PillButton : Button
    {
        private Color normal;
        private Color pressed;
        private bool hover;
        private bool down;

        public PillButton(string caption, Color normal, Color pressed)
        {
            Text = caption;
            this.normal = normal;
            this.pressed = pressed;
            ForeColor = TextColorFor(normal);
            Font = new Font("Segoe UI Semibold", 9.2f);
            FlatStyle = FlatStyle.Flat;
            BackColor = Color.Transparent;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseDownBackColor = Color.Transparent;
            FlatAppearance.MouseOverBackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        }

        public void SetPalette(Color normalColor, Color pressedColor)
        {
            normal = normalColor;
            pressed = pressedColor;
            ForeColor = TextColorFor(normalColor);
            Invalidate();
        }

        public static Color TextColorFor(Color background)
        {
            double luminance = RelativeLuminance(background);
            return luminance > 0.26 ? Color.FromArgb(5, 16, 24) : Color.FromArgb(238, 242, 246);
        }

        private static double RelativeLuminance(Color color)
        {
            double r = LinearChannel(color.R / 255.0);
            double g = LinearChannel(color.G / 255.0);
            double b = LinearChannel(color.B / 255.0);
            return 0.2126 * r + 0.7152 * g + 0.0722 * b;
        }

        private static double LinearChannel(double value)
        {
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = false;
            down = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            down = true;
            Invalidate();
            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            down = false;
            Invalidate();
            base.OnMouseUp(mevent);
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            pevent.Graphics.Clear(Parent != null ? Parent.BackColor : Color.Transparent);
            pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(1, 1, Width - 3, Height - 3);
            Color fill = Enabled ? (down ? pressed : (hover ? Blend(normal, Color.White, 0.08f) : normal)) : Color.FromArgb(50, 50, 50);
            Color border = Enabled ? Blend(fill, Color.Black, 0.06f) : Color.FromArgb(68, 68, 68);
            using (var path = RoundedPath(bounds, 6))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(border))
            {
                pevent.Graphics.FillPath(brush, path);
                pevent.Graphics.DrawPath(pen, path);
            }
            TextRenderer.DrawText(pevent.Graphics, Text, Font, bounds, Enabled ? ForeColor : Color.FromArgb(145, 145, 145), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private static Color Blend(Color a, Color b, float amount)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * amount),
                (int)(a.G + (b.G - a.G) * amount),
                (int)(a.B + (b.B - a.B) * amount));
        }

        private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

}
