using System;
using Microsoft.Win32;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
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
            // One instance only. Two would fight over the headset's control
            // channel - it allows a single session - and would both claim the
            // same global shortcuts, with the second one silently getting none.
            bool firstInstance;
            using (var instanceLock = new Mutex(true, MainForm.SingleInstanceMutexName, out firstInstance))
            {
                if (!firstInstance)
                {
                    Log("Already running");
                    MainForm.AskRunningInstanceToShow();
                    return;
                }
                Run();
                GC.KeepAlive(instanceLock);
            }
        }

        private static void Run()
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
        // Comfortably longer than the debounce above, so the window between the
        // last slider move and the write leaving is covered too.
        private const int EqEditOwnsSlidersMs = 2000;
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
        // Sound Connect reads the meter at about 1 Hz, and the streaming run
        // below keeps that cadence without paying to reopen the control channel
        // for each reading.
        private const int MeterStreamIntervalMs = 800;
        // Only the wait for each reading; the backend budgets the connect
        // separately, which is the slow part.
        private const int MeterStreamTimeoutMs = 1500;
        // Long enough that the stream is limited by the supervisor rather than
        // by running out of samples: a day at the interval above.
        private const int MeterStreamSamples = 108000;
        // The stream also asks for battery, on its first reading and then this
        // many readings apart - about once a minute at the rate it actually
        // runs. Battery moves slowly, and each ask is a few tens of
        // milliseconds on a channel the stream already holds.
        private const int MeterStreamBatteryEvery = 60;
        // A battery reading older than this carries its age in the tooltip.
        // Comfortably longer than the stream's own interval, so it only shows
        // when nothing is refreshing battery at all.
        private const int BatteryAgeShownAfterMinutes = 5;
        // Whether Safe Listening is on is asked the same way, this many
        // readings apart. Switched off, the headset keeps answering the meter
        // with one frozen level marked valid, so the readings alone cannot show
        // it. Often enough that switching it back on shows within a few seconds.
        private const int MeterStreamSafeListeningEvery = 5;
        // Below these the tray digits turn yellow, then red. Judged on the lower
        // earbud, not the case: a flat case does not cut listening short.
        // Yellow rather than amber because amber and red differ mostly in hue,
        // which red/green colour blindness hides; yellow is far brighter than
        // red, and a difference in lightness survives.
        // Red matches where the WF-1000XM6's Auto Power Save starts switching
        // features off (below 20%, per Sony), and yellow comes early enough to
        // warn before that - the same 30% at which the case light starts
        // flashing orange for a low earbud.
        private const int BatteryLowPercent = 30;
        private const int BatteryCriticalPercent = 20;
        private const int MeterSupervisorIntervalMs = 1000;
        // How long a reading may be overdue before the display says so. It has
        // to clear the longest gap that ordinary operation produces, which is a
        // command borrowing the channel and the stream reconnecting behind it:
        // the state batch alone is 3.1 s, and reconnecting costs about 1.8 s
        // while a phone is streaming to the headset, measured at gaps of 4.4 to
        // 5.9 s in that case against 2.4 to 3.2 s when the PC is the source.
        // Worst case is the batch plus a full connect timeout, near 8 s.
        //
        // Set below that, the icon dims on every state refresh and flickers
        // several times a minute for no reason. Set here, the only thing that
        // waits is a stream that is alive but has gone quiet; a stream that
        // actually dies exits, and that path marks it stale at once whatever
        // this says.
        private const int MeterStaleAfterMs = 10000;
        private const int MeterRestartDelayMs = 2000;
        private const int MeterStopWaitMs = 1500;
        private const int BackendCommandPaceMs = 450;
        // 36 02 is the paired-device list, and it is the one read here that lives
        // on DATA_MDR_NO2 rather than DATA_MDR.
        private const string StateBatchTail = "D6 D1;D6 D2;52 00;56 00;5A 00;E6 01;E6 00;F6 02;F6 01;26 05;mdr2:36 02\"";

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

        // The battery queries worth asking on the meter stream. Earbuds skip the
        // single level from 22 00, which the per-bud reading already supersedes,
        // so the stream holds the channel no longer than it has to.
        private static string MeterBatteryQueries(DeviceProfile profile)
        {
            return profile != null && profile.HasEarbudBatteries ? "22 01;22 02" : "22 00";
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
        private readonly System.Windows.Forms.Timer meterSupervisorTimer;

        private DeviceProfile currentProfile;
        private DeviceProfile lastStateRefreshProfile;
        private DeviceProfile soundPressureProbedProfile;
        private DateTime lastStateRefreshAt = DateTime.MinValue;
        private bool exiting;
        private bool minimizeToTray = true;
        private bool startMinimizedToTray;
        private bool hasShownOnce;
        private bool trayNotifications = true;
        // The balloon explains where the window went the first time it
        // disappears. After that the user knows, and repeating it on every
        // close is noise, so it is shown once and remembered across restarts.
        private bool trayNoticeShown;
        private bool trayCleanupStarted;
        private bool commandBusy;
        private bool controlInUse;
        private Process meterProcess;
        private bool meterGateHeld;
        private bool meterStale;
        private bool safeListeningOff;
        private DateTime lastMeterReadingAt = DateTime.MinValue;
        private DateTime meterStreamFailedAt = DateTime.MinValue;
        private bool soundPressureUnsupported;
        // Off by default on purpose: it is the only thing here that holds the
        // headset's control channel while the window is hidden, which keeps the
        // phone app off it, and that is not a cost to impose on someone who
        // never asked for the reading.
        private bool trayMeterEnabled;
        private bool trayMeterPaused;
        private readonly SoundLevelHistory soundLevelHistory = new SoundLevelHistory();
        private SoundLevelHistoryForm soundLevelHistoryForm;
        private CardPanel soundPressureBlock;
        private Label soundPressureChevron;
        private bool soundPressureBlockHover;
        private ToolStripMenuItem soundLevelHistoryItem;
        private int soundLevelReference = DefaultSoundLevelReference;
        private int soundLevelSpanMinutes = DefaultSoundLevelSpanMinutes;
        private bool sessionLocked;
        // Held so it can be unhooked: SystemEvents keeps a static strong
        // reference, which would otherwise outlive the form and call back into
        // a disposed one.
        private SessionSwitchEventHandler sessionSwitchHandler;
        private ToolStripMenuItem trayMeterPauseItem;
        // The generated icon currently on the NotifyIcon, if any. Replacing it
        // without disposing the old one leaks four GDI objects per update,
        // which at this cadence exhausts the per-process limit within the hour.
        private Icon trayMeterIcon;
        private string trayMeterDigits;
        private bool trayMeterDigitsDim;
        // What is actually on the icon now, so a reading that repeats - which
        // most of them do - does not redraw it.
        private string trayIconTextDrawn;
        private bool trayIconDimDrawn;
        private bool trayIconPausedDrawn;
        private bool trayIconApplied;
        private string trayTooltipApplied;
        // The last battery levels, kept in two parts because the meter stream
        // reports the buds and the case on separate lines, and with the time
        // they were read so the tray can say when they have gone old.
        private string batteryLevelsText;
        private string batteryCaseText;
        private DateTime batteryReadAt;
        // The lowest level that limits listening - the lower bud, or the one
        // level an over-ear model reports - or null before any reading.
        private int? batteryListeningLevel;
        // Each bud and the case, for colouring each part of the window's row on
        // its own. A docked bud is null, since it warns of nothing.
        private int? batteryLeftLevel;
        private int? batteryRightLevel;
        private int? batteryCaseLevel;
        private Color trayIconColorDrawn;
        private bool autoDetectRunning;
        // Whether the last scan found the headset connected. Distinct from
        // lastStateRefreshConnected, which says whether the last state batch
        // got through: the scan is a local enumeration costing 64 ms and no
        // control channel, and it runs whether or not the window is visible.
        private bool lastScanConnected;
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
        private string codecText;
        // The codec shown is the last one reported, and it has since stopped
        // being reported: a bud went into the case.
        private bool codecStale;
        private string activeDeviceText;
        private readonly Dictionary<int, string> deviceSlotNames = new Dictionary<int, string>();
        private Label soundPressureLabel;
        private Label soundPressureCaption;
        private PillButton lowLatencyButton;
        private bool lowLatencyActive;
        private const string LowLatencyHint = "Use Sound Connect to turn Low Latency on and off.";
        // An em dash, not "0 dB": the headset reports no reading at all when
        // nothing is playing, and for a few seconds after playback starts.
        private const string NoSoundPressureText = "\u2014";
        private const string SafeListeningOffText = "Off";
        private const string SoundPressureHint =
            "Level of the audio playing through the headset, ignoring noise cancelling.\r\n" +
            "Shows \u2014 while nothing is playing, and for a few seconds after playback starts.\r\n" +
            "Shows Off while Safe Listening is switched off in Sound Connect.\r\n" +
            "Click for the last hour.";
        // Where the history graph draws its reference line until the user picks
        // another: the level workplace hearing guidance starts from.
        private const int DefaultSoundLevelReference = 85;
        // Which span the history window opens on until the user picks another.
        // The longest one, which shows everything that is kept.
        private const int DefaultSoundLevelSpanMinutes = 60;
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
        private DateTime lastEqEditAt = DateTime.MinValue;
        private int eqFetchGeneration;
        private int settingWriteCount;

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

        // A second launch asks the running instance to show itself and then
        // exits; see Program.Main. The message id is derived from the name, so
        // both processes arrive at the same number without sharing anything.
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string lpString);

        // SendNotifyMessage, not PostMessage: a posted broadcast reports success
        // and is never delivered to the hidden window this has to reach. This one
        // still does not wait for another process to handle it.
        [DllImport("user32.dll")]
        private static extern bool SendNotifyMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        internal const string SingleInstanceMutexName = @"Local\XmControl.SingleInstance";
        internal const string ShowWindowMessageName = "XmControl.ShowWindow";
        private static readonly IntPtr HwndBroadcast = new IntPtr(0xffff);

        internal static void AskRunningInstanceToShow()
        {
            int message = RegisterWindowMessage(ShowWindowMessageName);
            if (message != 0) SendNotifyMessage(HwndBroadcast, message, IntPtr.Zero, IntPtr.Zero);
        }

        private readonly int showWindowMessage = RegisterWindowMessage(ShowWindowMessageName);

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
            // Raised on a SystemEvents thread, so it is marshalled rather than
            // touched directly; PostToUi drops it if the handle has gone.
            sessionSwitchHandler = (s, e) =>
            {
                if (e.Reason != SessionSwitchReason.SessionLock &&
                    e.Reason != SessionSwitchReason.SessionUnlock) return;
                bool locked = e.Reason == SessionSwitchReason.SessionLock;
                PostToUi(() => SetSessionLocked(locked));
            };
            SystemEvents.SessionSwitch += sessionSwitchHandler;
            // The plain glyph and the bare title are what is showing now; both
            // caches record that so the first real reading is the first update
            // actually sent to the shell.
            trayTooltipApplied = trayIcon.Text;
            trayIconApplied = true;

            autoDetectTimer = new System.Windows.Forms.Timer { Interval = AutoDetectIntervalMs };
            autoDetectTimer.Tick += async (s, e) => await RunUiTaskAsync(AutoDetectTickAsync);
            meterSupervisorTimer = new System.Windows.Forms.Timer { Interval = MeterSupervisorIntervalMs };
            meterSupervisorTimer.Tick += (s, e) => MeterSupervisorTick();
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
                    NotifyHiddenInTray("Still running in the tray.");
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
                if (!IsClosing) meterSupervisorTimer.Start();
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

            // Someone started the app again. Treat it as "show me the window",
            // which is what a second launch is nearly always for: the window can
            // be hidden in the tray, and the tray icon itself can be buried in
            // the overflow, so the app looks like it never started.
            if (m.Msg != 0 && m.Msg == showWindowMessage)
            {
                ShowWindow();
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

        private bool WindowIsShowing
        {
            get { return Visible && WindowState != FormWindowState.Minimized; }
        }

        // Either window showing a live reading keeps the meter running. The
        // history window counts for the same reason the main one does: someone
        // opened it to watch the level.
        private bool MeterIsWatched
        {
            get
            {
                return WindowIsShowing ||
                       (soundLevelHistoryForm != null && soundLevelHistoryForm.IsShowing);
            }
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

            // The live meter goes in the header rather than in a Settings row: it
            // is the one value on screen that moves second to second, and every
            // card below is already packed to the bottom of the window.
            //
            // The reading is also the way into the sound level history, so the
            // caption, the number and a chevron share one block that lights up
            // on hover. A separate button beside the gear was tried and did not
            // sit well with it. Hiding the block on a headset without a meter
            // takes the way in away with the reading.
            soundPressureBlock = new CardPanel
            {
                BackColor = page,
                BorderColor = page,
                Radius = 8,
                Size = new Size(158, 54),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            header.Controls.Add(soundPressureBlock);

            soundPressureCaption = new Label
            {
                Text = "Sound pressure",
                ForeColor = Color.FromArgb(190, 198, 207),
                Font = new Font("Segoe UI Semibold", 9.2f),
                AutoSize = false,
                Size = new Size(126, 18),
                Location = new Point(6, 3),
                TextAlign = ContentAlignment.MiddleRight
            };
            soundPressureBlock.Controls.Add(soundPressureCaption);

            soundPressureLabel = new Label
            {
                Text = NoSoundPressureText,
                ForeColor = ink,
                Font = new Font("Segoe UI Semibold", 15f),
                AutoSize = false,
                Size = new Size(126, 30),
                Location = new Point(6, 21),
                TextAlign = ContentAlignment.MiddleRight
            };
            soundPressureBlock.Controls.Add(soundPressureLabel);

            // Heavier and a little larger than the number's weight would give, or it
            // reads as a stray mark; raised to sit on the digits' midline.
            soundPressureChevron = new Label
            {
                Text = "›",
                ForeColor = Color.FromArgb(154, 162, 172),
                Font = new Font("Segoe UI Semibold", 17f),
                AutoSize = false,
                Size = new Size(20, 30),
                Location = new Point(132, 19),
                TextAlign = ContentAlignment.MiddleCenter
            };
            soundPressureBlock.Controls.Add(soundPressureChevron);

            foreach (Control part in new Control[] { soundPressureBlock, soundPressureCaption, soundPressureLabel, soundPressureChevron })
            {
                part.Cursor = Cursors.Hand;
                part.Click += (s, e) => ShowSoundLevelHistory();
                part.MouseEnter += (s, e) => SetSoundPressureBlockHover(true);
                part.MouseLeave += (s, e) => SetSoundPressureBlockHover(SoundPressureBlockUnderCursor());
                if (part != soundPressureBlock) toolTip.SetToolTip(part, SoundPressureHint);
            }

            Action layoutHeader = () =>
            {
                if (appSettingsButton != null && !appSettingsButton.IsDisposed)
                {
                    appSettingsButton.Location = new Point(header.Width - appSettingsButton.Width - 22, 16);
                }

                int meterRight = header.Width - 22 - (appSettingsButton != null ? appSettingsButton.Width + 12 : 0);
                if (soundPressureBlock != null && !soundPressureBlock.IsDisposed)
                {
                    soundPressureBlock.Location = new Point(meterRight - soundPressureBlock.Width, 3);
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
            batteryLabel.Paint += PaintBatteryRow;
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
            // The sound level items sit at the bottom, not up with the window
            // items: from a taskbar at the bottom of the screen this menu opens
            // upwards, so the bottom is the end nearest the pointer.
            //
            // The headset allows one control session at a time, so a running
            // tray meter is why the phone app cannot connect. That has to be
            // undoable from the tray itself, without opening the window and
            // taking the channel all over again to do it.
            // Named for exactly when it takes effect. Pausing has no effect
            // while the window is open, because the state refresh is holding
            // the control channel every fifteen seconds regardless and the
            // meter stopping would free nothing.
            trayMeterPauseItem = new ToolStripMenuItem("Pause tray sound level when minimized");
            trayMeterPauseItem.CheckOnClick = false;
            trayMeterPauseItem.Click += (s, e) => ToggleTrayMeterPaused();
            trayMeterPauseItem.Available = trayMeterEnabled;
            menu.Items.Add(trayMeterPauseItem);
            soundLevelHistoryItem = new ToolStripMenuItem("Sound level history...");
            soundLevelHistoryItem.Click += (s, e) => ShowSoundLevelHistory();
            soundLevelHistoryItem.Available = !soundPressureUnsupported;
            menu.Items.Add(soundLevelHistoryItem);
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
            if (TrayMeterPausedNow)
            {
                title += " - paused";
            }
            else if (trayMeterEnabled && controlInUse)
            {
                // Said instead of a reading: any number left on the icon is
                // old, and "not updating" would not say what to do about it.
                title += " - headset in use by another app";
            }
            else if (trayMeterEnabled && trayMeterDigits != null)
            {
                title += trayMeterDigits == NoSoundPressureText ? " - nothing playing"
                    // Not a level and not a fault: the headset stops measuring
                    // until Safe Listening is switched back on in Sound Connect,
                    // which the icon cannot say in three characters.
                    : trayMeterDigits == SafeListeningOffText ? " - Safe Listening is off"
                    : " - " + trayMeterDigits + " dB";
                // The icon says this by dimming; the tooltip has room to say it
                // in words, and a number nobody flagged as stopped is the one
                // thing here that could be read as current when it is not.
                if (trayMeterDigitsDim) title += " (not updating)";
            }

            // Battery is the thing people otherwise open the window to check.
            // It is refreshed by the state batch while the window is open and
            // by the meter stream while the tray meter runs; with neither, the
            // last reading stays, and says how old it is rather than passing
            // for current.
            // Dots rather than bare spaces between the parts: "R docked Case 74%"
            // runs the words together.
            string battery = BatteryText(" · ");
            if (battery != null)
            {
                TimeSpan age = DateTime.UtcNow - batteryReadAt;
                string suffix = age >= TimeSpan.FromMinutes(BatteryAgeShownAfterMinutes)
                    ? " (" + FormatAge(age) + " ago)"
                    : "";
                // 63 characters is the Shell_NotifyIcon limit for a tooltip.
                // When the full line does not fit, the case level goes before
                // the age does: an old reading without its age would pass for
                // a current one.
                string line = title + "\n" + battery + suffix;
                if (line.Length > 63) line = title + "\n" + batteryLevelsText.Replace("   ", " · ") + suffix;
                if (line.Length <= 63) title = line;
            }
            return title.Length <= 63 ? title : title.Substring(0, 63);
        }

        private static string FormatAge(TimeSpan age)
        {
            if (age.TotalHours >= 1) return (int)age.TotalHours + " h";
            return (int)age.TotalMinutes + " min";
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
            trayNoticeShown = false;
            trayMeterEnabled = false;
            soundLevelReference = DefaultSoundLevelReference;
            soundLevelSpanMinutes = DefaultSoundLevelSpanMinutes;
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
                    else if (string.Equals(key, "TrayNoticeShown", StringComparison.OrdinalIgnoreCase))
                    {
                        trayNoticeShown = ParseBool(value);
                    }
                    else if (string.Equals(key, "ShowSoundLevelInTray", StringComparison.OrdinalIgnoreCase))
                    {
                        trayMeterEnabled = ParseBool(value);
                    }
                    else if (string.Equals(key, "SoundLevelReference", StringComparison.OrdinalIgnoreCase))
                    {
                        int reference;
                        if (int.TryParse(value, out reference) && reference >= 40 && reference <= 110) soundLevelReference = reference;
                    }
                    else if (string.Equals(key, "SoundLevelSpanMinutes", StringComparison.OrdinalIgnoreCase))
                    {
                        int minutes;
                        // One of the buttons only, so a hand-edited file
                        // cannot leave the window with no span selected.
                        if (int.TryParse(value, out minutes) && SoundLevelHistoryForm.HasSpan(minutes)) soundLevelSpanMinutes = minutes;
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
                    "MinimizeToTrayOnClose=" + (minimizeToTray ? "true" : "false"),
                    "TrayNoticeShown=" + (trayNoticeShown ? "true" : "false"),
                    "ShowSoundLevelInTray=" + (trayMeterEnabled ? "true" : "false"),
                    "SoundLevelReference=" + soundLevelReference,
                    "SoundLevelSpanMinutes=" + soundLevelSpanMinutes
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
            using (var dialog = new AppSettingsDialog(startAtBoot, startMinimizedToTray, minimizeToTray, trayMeterEnabled, page, card, cardSoft, line, ink, subdued, blue, bluePressed))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (!SetStartAtBoot(dialog.StartAtBoot))
                {
                    SetStatus("Could not update startup setting", red);
                    MessageBox.Show("Windows did not allow the startup shortcut to be updated.", "App settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Turning the setting on is the moment the close button starts
                // behaving differently, so the notice is owed again even if it
                // has been shown before.
                if (dialog.MinimizeToTrayOnClose && !minimizeToTray) trayNoticeShown = false;
                startMinimizedToTray = dialog.StartMinimizedInTray;
                minimizeToTray = dialog.MinimizeToTrayOnClose;
                SetTrayMeterEnabled(dialog.ShowSoundLevelInTray);
                SaveAppPreferences();
                if (lastActionLabel != null && !lastActionLabel.IsDisposed) lastActionLabel.Text = "App settings saved";
                SetStatus("App settings saved", subdued);
            }
        }

        // The block's labels each raise their own enter and leave, and moving
        // between them passes through a leave, so a leave only counts once the
        // pointer is really outside the block.
        private bool SoundPressureBlockUnderCursor()
        {
            if (soundPressureBlock == null || soundPressureBlock.IsDisposed) return false;
            return soundPressureBlock.ClientRectangle.Contains(soundPressureBlock.PointToClient(Cursor.Position));
        }

        private void SetSoundPressureBlockHover(bool hover)
        {
            if (soundPressureBlockHover == hover || soundPressureBlock == null || soundPressureBlock.IsDisposed) return;
            soundPressureBlockHover = hover;
            // The same fill the gear uses on hover. The chevron brightens too,
            // a change of lightness rather than of colour.
            Color fill = hover ? Color.FromArgb(44, 48, 54) : page;
            soundPressureBlock.BackColor = fill;
            soundPressureBlock.BorderColor = fill;
            soundPressureChevron.ForeColor = hover ? ink : Color.FromArgb(154, 162, 172);
            soundPressureBlock.Invalidate(true);
        }

        // A window of its own rather than a section of the main one, which has
        // no room left. It can stay open while the main window is in the tray.
        private void ShowSoundLevelHistory()
        {
            if (IsClosing) return;
            if (soundLevelHistoryForm == null || soundLevelHistoryForm.IsDisposed)
            {
                var form = new SoundLevelHistoryForm(soundLevelHistory, soundLevelReference, soundLevelSpanMinutes, windowIcon, page, card, line, ink, subdued, blue, bluePressed);
                form.ReferenceChanged += (s, e) =>
                {
                    soundLevelReference = form.Reference;
                    SaveAppPreferences();
                };
                form.SpanChanged += (s, e) =>
                {
                    soundLevelSpanMinutes = form.SpanMinutes;
                    SaveAppPreferences();
                };
                form.FormClosed += (s, e) =>
                {
                    if (ReferenceEquals(soundLevelHistoryForm, form)) soundLevelHistoryForm = null;
                    // Hand the channel back at once if nothing else is watching.
                    PostToUi(MeterSupervisorTick);
                };
                // Minimizing it can change whether the tray should look paused.
                form.Resize += (s, e) => PostToUi(RefreshTrayMeter);
                soundLevelHistoryForm = form;
            }
            soundLevelHistoryForm.Show();
            if (soundLevelHistoryForm.WindowState == FormWindowState.Minimized) soundLevelHistoryForm.WindowState = FormWindowState.Normal;
            soundLevelHistoryForm.Activate();
            // Start the meter now rather than at the next tick.
            MeterSupervisorTick();
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
            // Both belong to the headset that answered, not to this app.
            if (changed) { codecText = null; activeDeviceText = null; codecStale = false; }
            // Another headset's battery is not this one's.
            if (changed)
            {
                batteryLevelsText = null;
                batteryCaseText = null;
                batteryListeningLevel = null;
                batteryLeftLevel = null;
                batteryRightLevel = null;
                batteryCaseLevel = null;
            }
            if (IsClosing) return changed;

            Text = AppTitle();
            if (titleLabel != null) titleLabel.Text = currentProfile.DisplayName;
            ApplyTrayTooltip();
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
                var attempt = await DetectProfileAsync(true);
                if (IsClosing) return;
                // A scan that never ran says nothing about whether the headset
                // is still there, so the last known state has to stand. Marking
                // it disconnected here would make every command that happens to
                // overlap the tick look like the headset going away.
                if (!attempt.Scanned) return;

                var detection = attempt.Detection;
                if (detection == null || !detection.Connected)
                {
                    lastStateRefreshConnected = false;
                    return;
                }

                // Nothing outside the window shows device state: every tray menu
                // entry is a fire-and-forget command and the tray tooltip is just
                // the model name. Refreshing while the window is hidden spends
                // three seconds on the headset's control channel to update
                // controls nobody can see, and only one program can hold that
                // channel at a time, so it also keeps the phone app off it. The
                // window asks for a refresh when it comes back.
                if (!WindowIsShowing) return;

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

        private async Task<DetectAttempt> DetectProfileAsync(bool quiet)
        {
            var output = quiet ? await RunBackendQuietAsync("scan") : await RunBackendAsync("scan", "Detecting device");
            if (IsClosing) return DetectAttempt.NotScanned;
            // The quiet runner returns null when a command already holds the
            // gate, which means no scan happened at all. That is not the same
            // as a scan that came back without the headset.
            if (output == null) return DetectAttempt.NotScanned;

            var detection = FindDetectedProfile(output);
            if (detection == null)
            {
                lastScanConnected = false;
                SetStatus("No supported device", amber);
                if (connectionLabel != null) connectionLabel.Text = "No supported device";
                return DetectAttempt.Missing;
            }

            // Recorded here, where the scan says it, rather than after the state
            // batch has run. The batch is skipped while the window is hidden,
            // so a flag only it can set stays false for good once the headset
            // has dropped there - which is exactly when the tray meter is the
            // only thing that wants to know.
            lastScanConnected = detection.Connected;
            bool changed = ApplyProfile(detection.Profile);
            if (connectionLabel != null) connectionLabel.Text = detection.Connected ? "Connected" : "Not connected";
            if (lastActionLabel != null && changed) lastActionLabel.Text = "Detected " + detection.Profile.DisplayName;
            if (detection.Connected && controlInUse) SetStatus("Connected - controls in use by another app", amber);
            else SetStatus(detection.Connected ? "Connected" : "Not connected", detection.Connected ? green : amber);
            return new DetectAttempt(detection);
        }

        // Detection has three outcomes and the caller has to tell the last two
        // apart: a device was found, a scan ran and did not find one, or no scan
        // ran because a command held the command gate.
        private sealed class DetectAttempt
        {
            public static readonly DetectAttempt NotScanned = new DetectAttempt(null, false);
            public static readonly DetectAttempt Missing = new DetectAttempt(null, true);

            public DetectAttempt(DeviceDetection detection) : this(detection, true)
            {
            }

            private DetectAttempt(DeviceDetection detection, bool scanned)
            {
                Detection = detection;
                Scanned = scanned;
            }

            public DeviceDetection Detection { get; private set; }
            public bool Scanned { get; private set; }
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
            var detection = (await DetectProfileAsync(false)).Detection;
            if (IsClosing) return;
            if (detection == null) return;
            int writesBefore = settingWriteCount;
            var output = await RunBackendAsync(WithDevice(BuildStateBatchCommand(detection.Profile ?? currentProfile)), "Updating");
            if (IsClosing) return;
            if (string.IsNullOrWhiteSpace(output)) return;
            bool overtaken = settingWriteCount != writesBefore;
            if (!output.Contains("Could not open"))
            {
                connectionLabel.Text = "Connected";
                if (!overtaken) MarkStateRefreshed(detection);
            }
            ParseBattery(output);
            ParseCodec(output);
            ParseActiveDevice(output);
            if (overtaken) return;
            ParseMode(output);
            ParseExtraSettings(output);
        }

        private async Task RefreshCurrentStateQuietAsync(DeviceDetection detection)
        {
            int writesBefore = settingWriteCount;
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
            ParseActiveDevice(output);
            // Left unmarked, so the next detect tick reads the settings again
            // once the write has gone out.
            if (settingWriteCount != writesBefore) return;
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

        // The meter answers on DATA_MDR_NO2, so it cannot ride along in the
        // state batch, which is DATA_MDR. It also cannot be polled by running
        // the backend once per reading: opening the control channel costs about
        // 280 ms when this PC is the only link to the headset, but around 1.8 s
        // when a phone is connected as well, which is longer than the interval
        // being asked for. So one backend run streams readings for as long as
        // the meter is wanted, and pays that cost once.
        //
        // Only one program can hold the control channel at a time, so the
        // stream holds the command gate for its lifetime and every other
        // command stops it first. The supervisor tick below restarts it once
        // that command is done.
        private void MeterSupervisorTick()
        {
            if (IsClosing) return;

            // The battery age in the tooltip, and whether a low battery may
            // still colour the digits and the window's row, move with the clock rather than with any
            // reading, so something has to look at them. Neither reaches the
            // shell unless the result actually changed.
            ApplyTrayIcon();
            ApplyTrayTooltip();
            if (batteryLabel != null) batteryLabel.Invalidate();

            // Support is a protocol capability, not a form factor, so it is
            // probed rather than inferred from the model name.
            if (!ReferenceEquals(soundPressureProbedProfile, currentProfile))
            {
                soundPressureProbedProfile = currentProfile;
                SetSoundPressureSupported(true);
            }

            // A reading every interval is expected; going quiet for several
            // means the number on screen has stopped tracking the headset, and
            // that has to look different from a headset reporting silence.
            //
            // Judged on the age of the last reading alone, rather than on
            // whether a stream happens to be running this instant. Every
            // command borrows the channel for a few seconds and the state batch
            // does it every fifteen, so keying off the stream made the icon
            // flicker several times a minute while the window was open, on a
            // reading that was only a moment old. The clock restarts with each
            // stream, so a reconnection gets the same grace.
            if (DateTime.UtcNow - lastMeterReadingAt > TimeSpan.FromMilliseconds(MeterStaleAfterMs))
            {
                SetMeterStale(true);
            }

            if (!ShouldRunMeter())
            {
                if (meterProcess != null) StopMeterStream();
                // Locking, pausing or unplugging all stop the readings without
                // producing one, so the icon is told here rather than waiting
                // for a line that is not coming.
                RefreshTrayMeter();
                return;
            }

            if (meterProcess != null) return;

            // Do not fight a command for the gate; the next tick will retry.
            if (commandBusy) return;
            if (DateTime.UtcNow - meterStreamFailedAt < TimeSpan.FromMilliseconds(MeterRestartDelayMs)) return;
            StartMeterStream();
        }

        private bool ShouldRunMeter()
        {
            if (IsClosing || soundPressureUnsupported) return false;
            // The scan's answer, not the state batch's: see lastScanConnected.
            if (!lastScanConnected) return false;
            // Checked before the window, because a locked desktop hides the
            // window just as thoroughly as it hides the tray icon.
            if (sessionLocked) return false;
            if (MeterIsWatched) return true;
            // Nothing on the window to read while it is in the tray, and holding
            // the control channel there would keep the phone app off it for a
            // number nobody can see. The tray icon is the exception: it shows
            // the reading precisely while the window is hidden, so it earns the
            // channel - but only while it is switched on and not paused from
            // the tray menu.
            return trayMeterEnabled && !trayMeterPaused;
        }

        // Locking is the one unambiguous "I am not here" signal available: the
        // desktop is gone, so neither the tray icon nor the window has a reader
        // and the control channel is better off back with whatever else wants
        // it. Unlocking is all it takes to resume - the supervisor picks the
        // stream back up on its next tick, the same way it does after a command
        // has borrowed the channel.
        //
        // Deliberately not inferred from keyboard and mouse idleness. Sitting
        // still is what watching a film looks like, so backing off there would
        // switch the meter off in the middle of the case it exists for.
        //
        // The flag starts false and only ever moves on an event that actually
        // arrives, so a notification that never comes leaves the meter running
        // rather than never starting. Running too much is visible in the tray
        // and fixable from its menu; never running is indistinguishable from a
        // feature that does not work.
        private void SetSessionLocked(bool locked)
        {
            if (sessionLocked == locked) return;
            sessionLocked = locked;
            // Hand the channel back now rather than at the next tick, so the
            // phone can have it as soon as the screen locks.
            if (locked) StopMeterStream();
            RefreshTrayMeter();
        }

        private void StartMeterStream()
        {
            if (meterProcess != null || !File.Exists(backendPath)) return;
            // The gate is what keeps this and the ordinary commands off the
            // control channel at the same time.
            if (!commandGate.Wait(0)) return;

            Process process = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = backendPath,
                    Arguments = WithDevice("soundpressure --samples " + MeterStreamSamples +
                                           " --interval " + MeterStreamIntervalMs +
                                           " --timeout " + MeterStreamTimeoutMs +
                                           " --battery \"" + MeterBatteryQueries(currentProfile) + "\"" +
                                           " --battery-every " + MeterStreamBatteryEvery +
                                           " --safe-listening-every " + MeterStreamSafeListeningEvery),
                    WorkingDirectory = Path.GetDirectoryName(backendPath),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null) PostToUi(() => OnMeterLine(e.Data));
                };
                // The window is usually hidden while this runs, so its status
                // line cannot say why the stream will not start; the tooltip can.
                process.ErrorDataReceived += (s, e) =>
                {
                    if (IsControlInUse(e.Data)) PostToUi(() => { controlInUse = true; RefreshTrayMeter(); });
                };
                process.Exited += (s, e) => PostToUi(OnMeterStreamExited);
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                meterProcess = process;
                meterGateHeld = true;
                lastMeterReadingAt = DateTime.UtcNow;
            }
            catch
            {
                if (process != null) process.Dispose();
                meterProcess = null;
                meterStreamFailedAt = DateTime.UtcNow;
                meterGateHeld = false;
                commandGate.Release();
            }
            // Whether the stream is running is what decides whether the tray
            // digits are dimmed, so both ends of its life say so.
            RefreshTrayMeter();
        }

        // Safe to call when nothing is running, and safe to call twice.
        private void StopMeterStream()
        {
            var process = meterProcess;
            meterProcess = null;
            if (process != null)
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                }
                catch
                {
                }
                try
                {
                    process.WaitForExit(MeterStopWaitMs);
                }
                catch
                {
                }
                try
                {
                    process.Dispose();
                }
                catch
                {
                }
            }
            if (meterGateHeld)
            {
                meterGateHeld = false;
                commandGate.Release();
            }
            RefreshTrayMeter();
        }

        private void OnMeterStreamExited()
        {
            // The backend gives up when the link drops. Let go of the gate and
            // let the supervisor try again after a pause; the reading on screen
            // goes stale rather than blank, because a lost link says nothing
            // about whether anything is playing.
            if (meterProcess == null) return;
            StopMeterStream();
            // Only an unplanned exit earns a pause before reconnecting. A stop
            // made to let a command through should be picked straight back up.
            meterStreamFailedAt = DateTime.UtcNow;
            SetMeterStale(true);
        }

        private void OnMeterLine(string line)
        {
            if (IsClosing || string.IsNullOrEmpty(line)) return;

            // Battery rides along on the same stream; see MeterStreamBatteryEvery.
            if (Regex.IsMatch(line, @"^(case )?battery:", RegexOptions.IgnoreCase))
            {
                ParseBattery(line);
                return;
            }

            // The headset pushes its device list whenever the playing device
            // changes, unasked, on the same channel the meter is holding. Taking
            // it here is what makes the codec row follow a source change at once
            // instead of at the next refresh.
            if (line.StartsWith("device", StringComparison.OrdinalIgnoreCase))
            {
                ParseDeviceLine(line);
                return;
            }

            var safeListening = Regex.Match(line, @"^safe listening:\s*(on|off)\s*$", RegexOptions.IgnoreCase);
            if (safeListening.Success)
            {
                safeListeningOff = safeListening.Groups[1].Value.Equals("off", StringComparison.OrdinalIgnoreCase);
                if (safeListeningOff)
                {
                    SetSoundPressureText(SafeListeningOffText);
                    // The tray has to say it too, or the icon keeps showing the
                    // last live number as if it were still being measured. The
                    // next reading puts the digits back when it is switched on.
                    trayMeterDigits = SafeListeningOffText;
                    RefreshTrayMeter();
                }
                return;
            }

            var match = Regex.Match(line, @"^sound pressure:\s*(.+?)\s*$", RegexOptions.IgnoreCase);
            if (!match.Success) return;

            lastMeterReadingAt = DateTime.UtcNow;
            controlInUse = false;
            SetMeterStale(false);

            // Still a sign the stream is alive, but not a level: with Safe
            // Listening off the headset repeats one frozen value as if valid.
            if (safeListeningOff) return;

            // "none" is a real answer, not a failure: the headset has no level
            // to report while nothing is playing.
            var level = Regex.Match(match.Groups[1].Value, @"^(\d+)\s*dB$", RegexOptions.IgnoreCase);
            SetSoundPressureText(level.Success ? level.Groups[1].Value + " dB" : NoSoundPressureText);
            soundLevelHistory.Record(DateTime.UtcNow, level.Success ? int.Parse(level.Groups[1].Value) : -1);
            trayMeterDigits = level.Success ? level.Groups[1].Value : NoSoundPressureText;
            RefreshTrayMeter();
        }

        private void SetMeterStale(bool stale)
        {
            if (meterStale == stale) return;
            meterStale = stale;
            if (soundPressureLabel != null && !soundPressureLabel.IsDisposed)
            {
                soundPressureLabel.ForeColor = stale ? subdued : ink;
            }
            RefreshTrayMeter();
        }

        // The one place that decides what the tray icon says. Everything that
        // can change the answer - a reading, a stalled stream, the setting, the
        // pause item, the headset going away - comes through here.
        private void RefreshTrayMeter()
        {
            // Dimmed digits mean one thing only: this number is no longer being
            // refreshed and something is wrong. Pausing is not dimmed, because
            // it is not a fault - it is the user having asked for exactly this,
            // and it gets its own shape below so the two cannot be confused.
            //
            // Staleness is the general test, a reading being overdue. Locking
            // is named outright because it is known before the clock proves it.
            //
            // Digits are left on screen rather than blanked. The last known
            // level still says more than nothing, and blanking would claim the
            // audio had stopped, which is not what any of this knows.
            trayMeterDigitsDim = meterStale || sessionLocked;
            ApplyTrayIcon();
            ApplyTrayTooltip();
        }

        // Setting NotifyIcon.Text talks to the shell whether or not the string
        // changed, and this runs on every reading, so only real changes go out.
        private void ApplyTrayTooltip()
        {
            if (trayIcon == null || trayCleanupStarted) return;
            string title = TrayTitle();
            if (title == trayTooltipApplied) return;
            trayTooltipApplied = title;
            trayIcon.Text = title;
        }

        private void ApplyTrayIcon()
        {
            if (trayIcon == null || trayCleanupStarted) return;

            bool paused = TrayMeterPausedNow;
            string text = trayMeterEnabled ? trayMeterDigits : null;
            bool dim = trayMeterDigitsDim;
            Color digits = TrayDigitsColor(dim);
            if (trayIconApplied && paused == trayIconPausedDrawn && text == trayIconTextDrawn &&
                (text == null || (dim == trayIconDimDrawn && digits == trayIconColorDrawn))) return;

            if (text == null && !paused)
            {
                trayIcon.Icon = notificationIcon;
                DisposeTrayMeterIcon();
            }
            else
            {
                Icon rendered;
                try
                {
                    // Paused draws at full strength: it is a state the user
                    // chose, not a reading that has gone bad, and keeping it
                    // bright is a second thing separating it from stale digits.
                    rendered = paused
                        ? RenderPausedIcon(Color.FromArgb(226, 230, 235))
                        : RenderDigitsIcon(text, digits,
                            digits == TrayBatteryLowColor || digits == TrayBatteryCriticalColor);
                }
                catch
                {
                    return;
                }
                // The shell copies the icon when it is handed over, so the one
                // it was using before can go as soon as the new one is set.
                // Leaving it instead leaks four GDI objects an update.
                Icon previous = trayMeterIcon;
                trayMeterIcon = rendered;
                trayIcon.Icon = rendered;
                if (previous != null) previous.Dispose();
            }

            trayIconTextDrawn = text;
            trayIconDimDrawn = dim;
            trayIconColorDrawn = digits;
            trayIconPausedDrawn = paused;
            trayIconApplied = true;
        }

        // Dimmed keeps its one meaning, that the sound level has stopped
        // updating, and wins over everything else. Otherwise the digits warn of
        // a low battery, but only on a reading recent enough to trust: a colour
        // carries no age the way the tooltip does, so an old level going yellow
        // would claim something nobody currently knows.
        private Color TrayDigitsColor(bool dim)
        {
            if (dim) return TrayDigitsDimColor;
            return BatteryWarningColor(batteryListeningLevel) ?? TrayDigitsColorNormal;
        }

        // The warning colour for one battery level, or null when it needs none,
        // shared by the tray digits and the window's row so the two agree.
        private Color? BatteryWarningColor(int? level)
        {
            if (!level.HasValue ||
                DateTime.UtcNow - batteryReadAt >= TimeSpan.FromMinutes(BatteryAgeShownAfterMinutes)) return null;
            if (level.Value < BatteryCriticalPercent) return TrayBatteryCriticalColor;
            if (level.Value < BatteryLowPercent) return TrayBatteryLowColor;
            return null;
        }

        private static readonly Color TrayDigitsColorNormal = Color.FromArgb(226, 230, 235);
        private static readonly Color TrayDigitsDimColor = Color.FromArgb(128, 134, 142);
        private static readonly Color TrayBatteryLowColor = Color.FromArgb(255, 222, 0);
        private static readonly Color TrayBatteryCriticalColor = Color.FromArgb(255, 99, 88);

        // Pausing only takes effect once the window is out of the way, so this
        // is also the only time the icon should claim to be paused. With the
        // window open the reading is live and saying otherwise would put the
        // tray and the window in disagreement about the same number.
        private bool TrayMeterPausedNow
        {
            get { return trayMeterEnabled && trayMeterPaused && !MeterIsWatched; }
        }

        private void DisposeTrayMeterIcon()
        {
            Icon previous = trayMeterIcon;
            trayMeterIcon = null;
            if (previous != null) previous.Dispose();
        }

        private void SetTrayMeterEnabled(bool enabled)
        {
            trayMeterEnabled = enabled;
            if (!enabled) trayMeterPaused = false;
            if (trayMeterPauseItem != null)
            {
                trayMeterPauseItem.Available = enabled;
                trayMeterPauseItem.Checked = trayMeterPaused;
            }
            RefreshTrayMeter();
        }

        private void ToggleTrayMeterPaused()
        {
            trayMeterPaused = !trayMeterPaused;
            if (trayMeterPauseItem != null) trayMeterPauseItem.Checked = trayMeterPaused;
            // Give the channel back at once rather than at the next tick, so
            // the phone app can have it the moment the user asks for it.
            if (trayMeterPaused && !MeterIsWatched) StopMeterStream();
            RefreshTrayMeter();
        }

        private void SetSoundPressureSupported(bool supported)
        {
            soundPressureUnsupported = !supported;
            if (soundPressureCaption != null && !soundPressureCaption.IsDisposed)
            {
                soundPressureCaption.Visible = supported;
            }
            if (soundPressureLabel != null && !soundPressureLabel.IsDisposed)
            {
                soundPressureLabel.Visible = supported;
            }
            if (soundPressureBlock != null && !soundPressureBlock.IsDisposed)
            {
                soundPressureBlock.Visible = supported;
            }
            if (soundLevelHistoryItem != null) soundLevelHistoryItem.Available = supported;
            if (!supported) SetSoundPressureText(NoSoundPressureText);
        }

        private void SetSoundPressureText(string text)
        {
            if (soundPressureLabel == null || soundPressureLabel.IsDisposed) return;
            if (soundPressureLabel.Text != text) soundPressureLabel.Text = text;
        }

        private void ParseCodec(string output)
        {
            if (codecLabel == null) return;
            var match = Regex.Match(output, @"(?m)^codec:\s*(.+?)\s*$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string value = match.Groups[1].Value;
                codecText = value.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? "Unknown" : value;
                codecStale = false;
            }
            // The WF-1000XM6 leaves the device information request unanswered
            // for as long as either bud sits in the case (verified with each
            // bud, in both connection quality settings), while still answering
            // the battery request in the same batch with that bud at 0%. Any
            // other missing reply is left to look like one.
            else if (Regex.IsMatch(output, @"(?m)^battery:\s*left\s*(0%|.*right\s*0%)", RegexOptions.IgnoreCase))
            {
                codecStale = true;
            }
            else
            {
                return;
            }
            ShowCodecRow();
        }

        // The headset lists every device it is paired with, each in a connection
        // slot, and names the slot of the one playing. Slot 0 means the device is
        // not connected, so it can never be the active one.
        private void ParseActiveDevice(string output)
        {
            foreach (var line in output.Split('\n')) ParseDeviceLine(line);
        }

        // Fed a line at a time, because the same lines arrive two ways: in a
        // state batch, and unasked down the meter stream when the headset
        // changes source. The records come first and the active slot last, so
        // slots are collected until that line resolves them.
        private void ParseDeviceLine(string line)
        {
            var device = Regex.Match(line,
                @"^device:\s*slot\s*(\d+);\s*addr\s*\S+;\s*(connected|not connected)(;\s*this pc)?;\s*name\s*(.*?)\s*$",
                RegexOptions.IgnoreCase);
            if (device.Success)
            {
                int slot;
                if (!int.TryParse(device.Groups[1].Value, out slot) || slot == 0) return;
                string name = device.Groups[4].Value;
                if (name.Length == 0) name = "Unnamed device";
                // Saying "this PC" is worth the width: the name the headset
                // holds is whatever the machine was called when it was paired.
                deviceSlotNames[slot] = device.Groups[3].Success ? name + " (this PC)" : name;
                return;
            }

            var active = Regex.Match(line, @"^device list:\s*\d+\s*known;\s*active slot\s*(\d+)\s*$", RegexOptions.IgnoreCase);
            if (!active.Success) return;

            int activeSlot;
            if (!int.TryParse(active.Groups[1].Value, out activeSlot)) return;
            string activeName;
            activeDeviceText = activeSlot > 0 && deviceSlotNames.TryGetValue(activeSlot, out activeName) ? activeName : null;
            // Slots are only as good as the list they came with; a device can
            // move between them, so do not carry them into the next one.
            deviceSlotNames.Clear();
            ShowCodecRow();
        }

        // The codec follows whichever device is playing, so the two belong on one
        // line: "AAC from dave-p10p" says what the codec alone cannot. The device
        // is left off when the headset does not name one, rather than shown as
        // unknown, because the codec on its own is still the truth.
        private void ShowCodecRow()
        {
            if (codecLabel == null) return;
            if (codecText == null)
            {
                // Nothing reported yet, and nothing will be until the bud comes
                // out, which "Waiting" would not say.
                if (codecStale) codecLabel.Text = "Not reported while a bud is docked";
                return;
            }
            codecLabel.Text = activeDeviceText == null ? codecText : codecText + " from " + activeDeviceText;
            // Dimmed, as the meter is, for a value no longer being refreshed:
            // the source can still change while the bud is docked.
            codecLabel.ForeColor = codecStale ? faint : subdued;
        }

        // Takes either a whole state batch or a single line from the meter
        // stream, which reports the buds and the case separately. Each part
        // keeps its last value until a new one arrives, so a line carrying only
        // one of them does not blank the other.
        private void ParseBattery(string output)
        {
            // Earbuds report the two buds under inquired type 0x01 and the case
            // under 0x02. Over-ear models answer neither, so fall back to the
            // single level from type 0x00.
            var buds = Regex.Match(output, @"(?m)^battery:\s*left\s*(\d+)%.*?right\s*(\d+)%", RegexOptions.IgnoreCase);
            var cradle = Regex.Match(output, @"(?m)^case battery:\s*(\d+)%", RegexOptions.IgnoreCase);
            // Only a level, never a hex dump of a battery frame this build does
            // not decode, or that dump would become the label.
            var single = Regex.Match(output, @"(?m)^battery:\s*(\d+%.*)$");

            if (buds.Success)
            {
                // A bud in the case reads 0%, not-charging, on the WF-1000XM6,
                // and goes straight back to its real level when taken out. It
                // is not being listened on, so it does not count towards the
                // warning, and it is shown as docked rather than as flat; a
                // bud that really was flat would have switched itself off.
                // "Docked" rather than "in case", which would sit next to the
                // "Case" level that follows.
                int left = int.Parse(buds.Groups[1].Value);
                int right = int.Parse(buds.Groups[2].Value);
                batteryLevelsText = "L " + BudLevelText(left) + "   R " + BudLevelText(right);
                batteryLeftLevel = left == 0 ? (int?)null : left;
                batteryRightLevel = right == 0 ? (int?)null : right;
                batteryListeningLevel = left == 0 ? (right == 0 ? (int?)null : right)
                    : right == 0 ? left
                    : Math.Min(left, right);
            }
            else if (single.Success && !(currentProfile != null && currentProfile.HasEarbudBatteries))
            {
                // Once a model reports its buds separately, a single level is a
                // coarser answer to the same question and does not replace them.
                batteryLevelsText = FormatBatteryText(single.Groups[1].Value);
                batteryCaseText = null;
                batteryCaseLevel = null;
                batteryListeningLevel = int.Parse(Regex.Match(single.Groups[1].Value, @"\d+").Value);
            }
            else if (!cradle.Success)
            {
                return;
            }
            if (cradle.Success)
            {
                batteryCaseText = "Case " + cradle.Groups[1].Value + "%";
                batteryCaseLevel = int.Parse(cradle.Groups[1].Value);
            }

            batteryReadAt = DateTime.UtcNow;
            if (batteryLabel != null && batteryLevelsText != null)
            {
                // The row is drawn by PaintBatteryRow, part by part, so the
                // label's own text is kept empty and only names it.
                batteryLabel.Text = "";
                batteryLabel.AccessibleName = BatteryText(", ");
                batteryLabel.Invalidate();
            }
            ApplyTrayIcon();
            ApplyTrayTooltip();
        }

        // Each bud and the case get their own colour, so the one running low
        // stands out from the ones that are fine, with the tray's thresholds.
        private void PaintBatteryRow(object sender, PaintEventArgs e)
        {
            var label = (Label)sender;
            if (batteryLevelsText == null) return;

            var parts = new List<KeyValuePair<string, int?>>();
            string[] levels = batteryLevelsText.Split(new[] { "   " }, StringSplitOptions.None);
            if (levels.Length == 2)
            {
                parts.Add(new KeyValuePair<string, int?>(levels[0], batteryLeftLevel));
                parts.Add(new KeyValuePair<string, int?>(levels[1], batteryRightLevel));
            }
            else
            {
                parts.Add(new KeyValuePair<string, int?>(batteryLevelsText, batteryListeningLevel));
            }
            if (batteryCaseText != null) parts.Add(new KeyValuePair<string, int?>(batteryCaseText, batteryCaseLevel));
            // An old reading says how old, as the tooltip does, since it has
            // also lost any warning colour and would otherwise pass for fresh.
            TimeSpan age = DateTime.UtcNow - batteryReadAt;
            if (age >= TimeSpan.FromMinutes(BatteryAgeShownAfterMinutes))
            {
                parts.Add(new KeyValuePair<string, int?>("(" + FormatAge(age) + " ago)", null));
            }

            // Where the label would have put its first character, and each
            // part's advance measured against a trailing mark, since a run
            // ending in spaces does not measure its spaces.
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            int x = (TextRenderer.MeasureText(e.Graphics, "x", label.Font).Width -
                     TextRenderer.MeasureText(e.Graphics, "x", label.Font, Size.Empty, flags).Width) / 2;
            int mark = TextRenderer.MeasureText(e.Graphics, ".", label.Font, Size.Empty, flags).Width;
            foreach (var part in parts)
            {
                Color color = BatteryWarningColor(part.Value) ?? label.ForeColor;
                TextRenderer.DrawText(e.Graphics, part.Key, label.Font, new Point(x, 0), color, flags);
                x += TextRenderer.MeasureText(e.Graphics, part.Key + "   .", label.Font, Size.Empty, flags).Width - mark;
            }
        }

        private static string BudLevelText(int level)
        {
            return level == 0 ? "docked" : level + "%";
        }

        private string BatteryText(string gap)
        {
            if (batteryLevelsText == null) return null;
            string text = batteryLevelsText.Replace("   ", gap);
            return batteryCaseText == null ? text : text + gap + batteryCaseText;
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
                if (HasEqValues(eqValues) && !EqualizerEditInFlight())
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
            ParseMode(await RunSettingWriteAsync(WithDevice("ncasm-set --mode anc"), "Setting ANC"));
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
            string args = "ncasm-set --mode ambient --level " + clamped + " --voice " + (voice ? "on" : "off");
            // Report what the headset applied rather than what was asked for.
            // ParseMode refreshes these controls from the reply, and the two can
            // differ because the headset clamps values it will not take. When it
            // reports nothing the command did not land, so say that instead of
            // presenting the requested value as a confirmation.
            if (ParseMode(await RunSettingWriteAsync(WithDevice(args), "Setting ambient")))
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
            ParseMode(await RunSettingWriteAsync(WithDevice(args), "Setting auto ambient"));
        }

        private async Task SetAmbientSensitivityAsync(string sensitivity)
        {
            if (autoAmbientSupported == false)
            {
                lastActionLabel.Text = "Auto ambient not supported";
                return;
            }
            lastActionLabel.Text = "Auto ambient sensitivity " + sensitivity;
            string args = "ncasm-set --auto on --sensitivity " + sensitivity + "";
            ParseMode(await RunSettingWriteAsync(WithDevice(args), "Setting sensitivity"));
        }

        private async Task SetOffAsync()
        {
            bigModeLabel.Text = "Off";
            bigDetailLabel.Text = "Noise control disabled";
            SetModeButtonState("Off");
            lastActionLabel.Text = "Noise control off";
            ParseMode(await RunSettingWriteAsync(WithDevice("ncasm-set --mode off"), "Turning off"));
        }

        private async Task SetDseeAsync(bool enabled)
        {
            SetDseeState(enabled);
            lastActionLabel.Text = "DSEE Extreme " + (enabled ? "Auto" : "Off");
            await RunSettingWriteAsync(WithDevice("raw \"E8 01 " + (enabled ? "01" : "00") + "\" --ack-only"), "Setting DSEE");
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
            var output = await RunBackendSilentAsync(WithDevice("batch \"" + payload + "\""));
            if (IsClosing) return;
            if (output.Contains("Could not open"))
            {
                SetStatus(UnreachableStatus(output), red);
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
            var output = await RunBackendSilentAsync(WithDevice("raw \"" + payload + "\" --ack-only"));
            if (IsClosing) return;
            if (output.Contains("Could not open"))
            {
                SetStatus(UnreachableStatus(output), red);
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
            var output = await RunBackendSilentAsync(WithDevice("batch \"56 00\""));
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
                var output = await RunBackendQuietAsync(WithDevice("raw \"" + payload + "\" --ack-only"));
                if (output == null)
                {
                    liveEqSendPending = true;
                    lastActionLabel.Text = "Equalizer waiting";
                    return;
                }
                if (output.Contains("Could not open"))
                {
                    SetStatus(UnreachableStatus(output), red);
                    lastActionLabel.Text = "Equalizer not sent";
                    return;
                }
                lastActionLabel.Text = "Equalizer updated";
                return;
            }

            await RunSettingWriteAsync(WithDevice("raw \"" + payload + "\" --ack-only"), "Setting equalizer");
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
            await RunSettingWriteAsync(WithDevice("raw \"" + payload + "\" --ack-only"), "Setting equalizer");
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
            lastEqEditAt = DateTime.UtcNow;

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

        // A state refresh reads the equalizer before it returns, so its values
        // predate anything the user has done since. Applying them drags the
        // slider back under the user, and the resend that follows then writes
        // that reverted value to the headset, so the edit is lost rather than
        // merely redrawn. An edit owns the sliders until it has been sent:
        // while a write is in flight or queued, and for a moment after the last
        // move, so that the gap before the debounce fires is covered as well.
        private bool EqualizerEditInFlight()
        {
            if (liveEqSendRunning || liveEqSendPending) return true;
            return DateTime.UtcNow - lastEqEditAt < TimeSpan.FromMilliseconds(EqEditOwnsSlidersMs);
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
            await RunSettingWriteAsync(WithDevice("raw \"E8 00 " + (prioritizeSound ? "00" : "01") + "\" --ack-only"), "Setting connection quality");
        }

        private async Task SetSpeakToChatAsync(bool enabled)
        {
            string value = enabled ? "00" : "01";
            SetSpeakToChatState(enabled);
            lastActionLabel.Text = "Speak-to-Chat " + (enabled ? "on" : "off");
            await RunSettingWriteAsync(WithDevice("raw \"F8 02 " + value + " " + value + "\" --ack-only"), "Setting Speak-to-Chat");
        }

        private async Task SetWearPauseAsync(bool enabled)
        {
            SetWearPauseState(enabled);
            lastActionLabel.Text = "Pause when removed " + (enabled ? "on" : "off");
            await RunSettingWriteAsync(WithDevice("raw \"F8 01 " + (enabled ? "00" : "01") + "\" --ack-only"), "Setting wearing sensor");
        }

        private async Task SetTouchPanelAsync(bool enabled)
        {
            SetTouchPanelState(enabled);
            lastActionLabel.Text = "Touch panel " + (enabled ? "on" : "off");
            await RunSettingWriteAsync(WithDevice("raw \"D8 D1 00 " + (enabled ? "00" : "01") + "\" --ack-only"), "Setting touch panel");
        }

        private async Task SetMultipointAsync(bool enabled)
        {
            SetMultipointState(enabled);
            lastActionLabel.Text = "Multipoint " + (enabled ? "on" : "off");
            await RunSettingWriteAsync(WithDevice("raw \"D8 D2 00 " + (enabled ? "00" : "01") + "\" --ack-only"), "Setting multipoint");
        }

        private async Task SetAutoPowerRemovedAsync()
        {
            SetAutoPowerState("removed");
            lastActionLabel.Text = "Automatic power off set to on";
            await RunSettingWriteAsync(WithDevice("raw \"28 05 10 00\" --ack-only"), "Setting auto power");
        }

        private async Task SetAutoPowerDisabledAsync()
        {
            SetAutoPowerState("disabled");
            lastActionLabel.Text = "Automatic power off set to off";
            await RunSettingWriteAsync(WithDevice("raw \"28 05 11 00\" --ack-only"), "Setting auto power");
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

        // A setting control shows its new value at once, but the write waits
        // for the command gate, and a state refresh already holding the gate
        // read the headset before the click. Applying that reading flips the
        // control back to the old value, and the write only reaches the headset
        // after that, so the display stays wrong until the next refresh. The
        // count is taken synchronously on the click, before the gate is awaited,
        // so a refresh can tell that a write was asked for while it ran.
        private Task<string> RunSettingWriteAsync(string args, string busyText)
        {
            settingWriteCount++;
            return RunBackendAsync(args, busyText);
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
            if (channel)
            {
                StopMeterStream();
                await commandGate.WaitAsync();
            }
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

                if (output.Contains("Could not open")) SetStatus(UnreachableStatus(output), red);
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
                // The try and catch have already set the outcome; a blanket
                // "Ready" here used to wipe "Device not reachable" at once.
                commandBusy = false;
                if (channel) commandGate.Release();
            }
        }

        private async Task<string> RunBackendQuietAsync(string args)
        {
            if (IsClosing) return "";
            if (!File.Exists(backendPath)) return "";
            // A scan never touches the control channel, so it must not wait on
            // the gate or cut the meter stream short; it would otherwise be
            // starved by the stream that holds the gate.
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

            StopMeterStream();
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

            StopMeterStream();
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
                string output = await Task.Run(() => RunBackendProcess(args));
                // A scan never touches the control channel, so it cannot say
                // whether another app still holds it.
                if (!IsScanCommand(args)) controlInUse = IsControlInUse(output);
                return output;
            }
            finally
            {
                if (shouldPace) lastBackendCommandAtUtc = DateTime.UtcNow;
            }
        }

        private static bool IsScanCommand(string args)
        {
            return args != null && (args == "scan" || args.StartsWith("scan ", StringComparison.Ordinal));
        }

        // The backend's wording when the connect is refused as already in use:
        // the headset is there, but Sound Connect or another utility has its
        // control session. Windows still reports it connected, which is why
        // "Device not reachable" sent people looking in the wrong place.
        private static bool IsControlInUse(string output)
        {
            return output != null && output.Contains("in use by another app");
        }

        private static string UnreachableStatus(string output)
        {
            return IsControlInUse(output) ? "Headset in use by another app" : "Device not reachable";
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

        // Both routes into the tray - the close button and minimizing - say the
        // same thing, so one notice covers them: whichever happens first
        // explains it, and neither says it again.
        private void NotifyHiddenInTray(string message)
        {
            if (trayNoticeShown) return;
            trayNoticeShown = true;
            SaveAppPreferences();
            Notify(message);
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
            if (meterSupervisorTimer != null)
            {
                meterSupervisorTimer.Stop();
            }
            StopMeterStream();
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

            // First, because it is the only thing here that outlives the
            // process. Exiting from the tray menu reaches this without going
            // through OnFormClosing, so this is the one place that covers both
            // routes out; miss it and the backend keeps running with no owner,
            // holding the headset's control channel until someone notices and
            // kills it by hand.
            StopMeterStream();

            if (soundLevelHistoryForm != null && !soundLevelHistoryForm.IsDisposed)
            {
                soundLevelHistoryForm.Close();
                soundLevelHistoryForm = null;
            }

            if (sessionSwitchHandler != null)
            {
                SystemEvents.SessionSwitch -= sessionSwitchHandler;
                sessionSwitchHandler = null;
            }

            if (autoDetectTimer != null) autoDetectTimer.Dispose();
            if (liveEqTimer != null) liveEqTimer.Dispose();
            if (meterSupervisorTimer != null) meterSupervisorTimer.Dispose();
            if (trayIcon != null)
            {
                trayIcon.ContextMenuStrip = null;
                trayIcon.Icon = null;
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
            DisposeTrayMeterIcon();
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
            NotifyHiddenInTray("Quick controls are available in the tray.");
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

        // Windows scales this 32x32 bitmap to whatever size the tray asks for,
        // so the digits are fitted to the box rather than drawn at a fixed
        // point size: the app is DPI-unaware, which puts the icon at about
        // 28 px on a 175% display, and anything smaller than the box allows
        // stops being readable there. Two digits is the design size, since
        // readings run from the low thirties to the low nineties; a third digit
        // only turns up at volumes nobody listens at, and is left to shrink
        // rather than be truncated into a number that would simply be wrong.
        //
        // A low battery also puts a bar under the digits. Colour alone did not
        // carry it: at tray size, yellow digits on a dark taskbar can pass for
        // white to a red/green colour blind user, so the warning needs a shape that
        // is there or not whatever colour it is seen as. The digits give up
        // the bar's height only while it is shown.
        private static Icon RenderDigitsIcon(string text, Color color, bool batteryBar)
        {
            var bitmap = new Bitmap(32, 32);
            try
            {
                var box = batteryBar ? new RectangleF(1f, 0f, 30f, 24f) : new RectangleF(1f, 1f, 30f, 30f);
                using (var g = Graphics.FromImage(bitmap))
                using (var brush = new SolidBrush(color))
                using (var format = new StringFormat(StringFormat.GenericTypographic))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    if (batteryBar) FillRound(g, brush, new Rectangle(2, 25, 28, 6), 2);
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    using (var font = FitFont(g, text, box.Width, box.Height, format))
                    {
                        g.DrawString(text, font, brush, box, format);
                    }
                }
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    return (Icon)Icon.FromHandle(handle).Clone();
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
            finally
            {
                bitmap.Dispose();
            }
        }

        // Two bars, drawn rather than typed: a pause glyph from a font would
        // depend on coverage that may not be there, and at the size this lands
        // on screen a pair of rectangles is crisper than any glyph would be.
        private static Icon RenderPausedIcon(Color color)
        {
            var bitmap = new Bitmap(32, 32);
            try
            {
                using (var g = Graphics.FromImage(bitmap))
                using (var brush = new SolidBrush(color))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    FillRound(g, brush, new Rectangle(7, 6, 7, 20), 3);
                    FillRound(g, brush, new Rectangle(18, 6, 7, 20), 3);
                }
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    return (Icon)Icon.FromHandle(handle).Clone();
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
            finally
            {
                bitmap.Dispose();
            }
        }

        private static Font FitFont(Graphics g, string text, float boxWidth, float boxHeight, StringFormat format)
        {
            const float reference = 24f;
            using (var probe = new Font("Segoe UI", reference, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                SizeF size = g.MeasureString(text, probe, PointF.Empty, format);
                float scale = Math.Min(boxWidth / Math.Max(size.Width, 1f), boxHeight / Math.Max(size.Height, 1f));
                return new Font("Segoe UI", Math.Max(6f, reference * scale), FontStyle.Bold, GraphicsUnit.Pixel);
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

    // One meter reading. A negative level is the headset saying nothing is
    // playing, which is an answer, not a missing reading.
    internal struct SoundLevelSample
    {
        public DateTime At;
        public int Level;
    }

    internal sealed class SoundLevelStats
    {
        public int Peak = -1;
        public DateTime PeakAt;
        public double? Average;
        public double AboveSeconds;
        public double CoveredFraction;
    }

    // The last hour of meter readings, kept in memory only. Nothing here asks
    // the headset for anything: it keeps what the meter stream already reads.
    internal sealed class SoundLevelHistory
    {
        public static readonly TimeSpan Kept = TimeSpan.FromMinutes(60);
        // A reading overdue by longer than this is a gap, not a slow reading.
        // The same allowance the tray uses before dimming its digits: it clears
        // a command borrowing the channel and the stream reconnecting, so the
        // state refresh every fifteen seconds does not riddle the graph with
        // holes that are not really missing audio.
        public static readonly TimeSpan Overdue = TimeSpan.FromSeconds(10);
        // How long a reading is taken to stand for when nothing follows it
        // closely enough to say: about the stream's own interval.
        public static readonly TimeSpan Nominal = TimeSpan.FromSeconds(1);

        private readonly List<SoundLevelSample> samples = new List<SoundLevelSample>();

        public List<SoundLevelSample> Samples
        {
            get { return samples; }
        }

        public void Record(DateTime at, int level)
        {
            samples.Add(new SoundLevelSample { At = at, Level = level });
            // A little over the hour, so the left edge of the longest view
            // still knows whether the reading before it ran on into view.
            DateTime cutoff = at - Kept - TimeSpan.FromMinutes(1);
            int stale = 0;
            while (stale < samples.Count && samples[stale].At < cutoff) stale++;
            if (stale > 0) samples.RemoveRange(0, stale);
        }

        // When the reading at index i stops counting: at the next reading if
        // that came soon enough, otherwise one nominal interval on.
        public DateTime EndOf(int i, DateTime now)
        {
            DateTime at = samples[i].At;
            DateTime end = i + 1 < samples.Count && samples[i + 1].At - at <= Overdue
                ? samples[i + 1].At
                : at + Nominal;
            return end > now ? (now > at ? now : at) : end;
        }

        public SoundLevelStats Measure(DateTime from, DateTime to, int reference, DateTime now)
        {
            var stats = new SoundLevelStats();
            double covered = 0, levelTime = 0, energy = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                SoundLevelSample sample = samples[i];
                if (sample.At > to) break;
                DateTime end = EndOf(i, now);
                if (end < from) continue;

                if (sample.Level >= 0 && sample.At >= from && sample.Level > stats.Peak)
                {
                    stats.Peak = sample.Level;
                    stats.PeakAt = sample.At;
                }

                DateTime start = sample.At < from ? from : sample.At;
                if (end > to) end = to;
                double seconds = (end - start).TotalSeconds;
                if (seconds <= 0) continue;
                covered += seconds;
                if (sample.Level < 0) continue;
                levelTime += seconds;
                // An energy average, the way exposure is judged: ten minutes at
                // 90 dB and ten at 70 average to 87, not to 80.
                energy += seconds * Math.Pow(10, sample.Level / 10.0);
                if (sample.Level > reference) stats.AboveSeconds += seconds;
            }
            double span = (to - from).TotalSeconds;
            stats.CoveredFraction = span > 0 ? Math.Min(1, covered / span) : 0;
            if (levelTime > 0) stats.Average = 10 * Math.Log10(energy / levelTime);
            return stats;
        }
    }

    internal sealed class SoundLevelGraph : Control
    {
        private const int PlotLeft = 44;
        private const int PlotRight = 16;
        private const int PlotTop = 26;
        private const int PlotBottom = 26;
        private const int MinimumDragPixels = 6;

        private readonly SoundLevelHistory history;
        private readonly Color ink;
        private readonly Color subdued;
        private readonly Color gridColor;
        private readonly Color lineColor;
        private readonly Color hatchColor;

        private TimeSpan span = TimeSpan.FromMinutes(60);
        private int reference = 85;
        private DateTime? selectionFrom;
        private DateTime? selectionTo;
        private DateTime? dragFrom;
        private DateTime? dragTo;
        private int? hoverX;
        private int dragStartX;

        public event EventHandler SelectionChanged;

        public SoundLevelGraph(SoundLevelHistory history, Color back, Color ink, Color subdued, Color gridColor, Color lineColor)
        {
            this.history = history;
            this.ink = ink;
            this.subdued = subdued;
            this.gridColor = gridColor;
            this.lineColor = lineColor;
            hatchColor = Color.FromArgb(78, 78, 78);
            BackColor = back;
            Font = new Font("Segoe UI", 8.6f);
            Cursor = Cursors.Cross;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public TimeSpan Span
        {
            get { return span; }
            set { span = value; ClearSelection(); Invalidate(); }
        }

        public int Reference
        {
            get { return reference; }
            set { reference = value; Invalidate(); }
        }

        public bool HasSelection
        {
            get { return selectionFrom.HasValue; }
        }

        public DateTime SelectionFrom
        {
            get { return selectionFrom.Value; }
        }

        public DateTime SelectionTo
        {
            get { return selectionTo.Value; }
        }

        public void ClearSelection()
        {
            bool had = selectionFrom.HasValue;
            selectionFrom = null;
            selectionTo = null;
            Invalidate();
            if (had && SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        // The span the figures describe: the selection if there is one,
        // otherwise everything on screen.
        public void GetScope(DateTime now, out DateTime from, out DateTime to)
        {
            if (selectionFrom.HasValue)
            {
                from = selectionFrom.Value;
                to = selectionTo.Value > now ? now : selectionTo.Value;
            }
            else
            {
                from = now - span;
                to = now;
            }
        }

        private Rectangle Plot
        {
            get
            {
                return new Rectangle(PlotLeft, PlotTop,
                    Math.Max(10, Width - PlotLeft - PlotRight),
                    Math.Max(10, Height - PlotTop - PlotBottom));
            }
        }

        private float XOf(DateTime at, DateTime viewStart, Rectangle plot)
        {
            return plot.Left + (float)((at - viewStart).TotalSeconds / span.TotalSeconds * plot.Width);
        }

        private DateTime TimeAt(int x, DateTime now)
        {
            Rectangle plot = Plot;
            double fraction = Math.Max(0, Math.Min(1, (x - plot.Left) / (double)plot.Width));
            return now - span + TimeSpan.FromSeconds(fraction * span.TotalSeconds);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            DateTime now = DateTime.UtcNow;
            dragStartX = e.X;
            dragFrom = TimeAt(e.X, now);
            dragTo = dragFrom;
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            hoverX = e.X;
            if (dragFrom.HasValue) dragTo = TimeAt(e.X, DateTime.UtcNow);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragFrom.HasValue) return;
            Capture = false;
            DateTime a = dragFrom.Value;
            DateTime b = TimeAt(e.X, DateTime.UtcNow);
            dragFrom = null;
            dragTo = null;
            // A click rather than a drag lets go of the selection.
            if (Math.Abs(e.X - dragStartX) < MinimumDragPixels)
            {
                selectionFrom = null;
                selectionTo = null;
            }
            else
            {
                selectionFrom = a < b ? a : b;
                selectionTo = a < b ? b : a;
            }
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverX = null;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            DateTime now = DateTime.UtcNow;
            DateTime viewStart = now - span;
            Rectangle plot = Plot;
            List<SoundLevelSample> samples = history.Samples;

            // The scale fits the usual listening range and only grows to take
            // in a reading or a reference line outside it.
            int low = 50, high = 100;
            for (int i = 0; i < samples.Count; i++)
            {
                SoundLevelSample s = samples[i];
                if (s.Level < 0 || s.At < viewStart) continue;
                low = Math.Min(low, s.Level / 10 * 10);
                high = Math.Max(high, (s.Level + 9) / 10 * 10);
            }
            low = Math.Min(low, reference / 10 * 10);
            high = Math.Max(high, (reference + 9) / 10 * 10);
            Func<double, float> yOf = level => plot.Bottom - (float)((level - low) / (double)(high - low) * plot.Height);

            DrawGrid(g, plot, low, high, yOf);
            DrawGaps(g, plot, samples, viewStart, now);
            DrawSelection(g, plot, viewStart);
            DrawLevels(g, plot, samples, viewStart, now, yOf);
            DrawReference(g, plot, yOf);
            DrawTimeAxis(g, plot, viewStart, now);
            DrawPeak(g, plot, viewStart, now, yOf);
            DrawHover(g, plot, samples, now);
        }

        private void DrawGrid(Graphics g, Rectangle plot, int low, int high, Func<double, float> yOf)
        {
            int step = high - low > 60 ? 20 : 10;
            using (var pen = new Pen(gridColor))
            {
                for (int level = low; level <= high; level += step)
                {
                    float y = yOf(level);
                    g.DrawLine(pen, plot.Left, y, plot.Right, y);
                    var box = new Rectangle(0, (int)y - 9, plot.Left - 8, 18);
                    TextRenderer.DrawText(g, level.ToString(), Font, box, subdued, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }
        }

        // Missing readings are hatched rather than left looking like silence,
        // and the line is broken across them rather than joined, so a stretch
        // nobody measured can never pass for a quiet one.
        private void DrawGaps(Graphics g, Rectangle plot, List<SoundLevelSample> samples, DateTime viewStart, DateTime now)
        {
            var gaps = new List<KeyValuePair<DateTime, DateTime>>();
            DateTime covered = viewStart;
            for (int i = 0; i < samples.Count; i++)
            {
                DateTime end = history.EndOf(i, now);
                if (end < viewStart) continue;
                if (samples[i].At - covered >= SoundLevelHistory.Overdue)
                {
                    gaps.Add(new KeyValuePair<DateTime, DateTime>(covered, samples[i].At));
                }
                if (end > covered) covered = end;
            }
            if (now - covered >= SoundLevelHistory.Overdue)
            {
                gaps.Add(new KeyValuePair<DateTime, DateTime>(covered, now));
            }

            using (var pen = new Pen(hatchColor, 1.4f))
            {
                foreach (var gap in gaps)
                {
                    float left = Math.Max(plot.Left, XOf(gap.Key, viewStart, plot));
                    float right = Math.Min(plot.Right, XOf(gap.Value, viewStart, plot));
                    if (right - left < 1) continue;
                    var area = new RectangleF(left, plot.Top, right - left, plot.Height);
                    GraphicsState state = g.Save();
                    g.SetClip(area);
                    for (float x = left - plot.Height; x < right; x += 8)
                    {
                        g.DrawLine(pen, x, plot.Bottom, x + plot.Height, plot.Top);
                    }
                    g.Restore(state);
                    if (right - left >= 90)
                    {
                        var box = new Rectangle((int)left, plot.Top + 4, (int)(right - left), 18);
                        using (var back = new SolidBrush(BackColor))
                        {
                            Size text = TextRenderer.MeasureText("No readings", Font);
                            g.FillRectangle(back, box.Left + (box.Width - text.Width) / 2 - 4, box.Top, text.Width + 8, box.Height);
                        }
                        TextRenderer.DrawText(g, "No readings", Font, box, Color.FromArgb(190, 198, 207), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    }
                }
            }
        }

        private void DrawSelection(Graphics g, Rectangle plot, DateTime viewStart)
        {
            DateTime? a = dragFrom.HasValue ? dragFrom : selectionFrom;
            DateTime? b = dragFrom.HasValue ? dragTo : selectionTo;
            if (!a.HasValue || !b.HasValue) return;
            float x1 = XOf(a.Value < b.Value ? a.Value : b.Value, viewStart, plot);
            float x2 = XOf(a.Value < b.Value ? b.Value : a.Value, viewStart, plot);
            x1 = Math.Max(plot.Left, x1);
            x2 = Math.Min(plot.Right, x2);
            if (x2 <= x1) return;
            // Lighter, not a different hue: the selection has to read without
            // relying on colour.
            using (var wash = new SolidBrush(Color.FromArgb(24, 255, 255, 255)))
            using (var edge = new Pen(Color.FromArgb(150, ink)))
            {
                g.FillRectangle(wash, x1, plot.Top, x2 - x1, plot.Height);
                g.DrawLine(edge, x1, plot.Top, x1, plot.Bottom);
                g.DrawLine(edge, x2, plot.Top, x2, plot.Bottom);
            }
        }

        // Readings arrive about a second apart, so on the shorter spans a pixel
        // column is worth barely more than one of them: there each reading gets
        // its own point at its own time, and a dip between tracks keeps its
        // full depth. Only where a column really holds several readings are
        // they summarised, with a faint band for their range around the line
        // through their mean, so a short loud moment is not averaged out of
        // sight on the longer spans.
        //
        // Either way the columns are pinned to the clock, not to the left edge
        // of the view. Anchored to the view they re-formed on every repaint as
        // it scrolled: the same readings averaged together differently each
        // second, so a dip flickered between its own depth and its neighbours'
        // mean, and the line stretched and squeezed instead of scrolling.
        private void DrawLevels(Graphics g, Rectangle plot, List<SoundLevelSample> samples, DateTime viewStart, DateTime now, Func<double, float> yOf)
        {
            long columnTicks = Math.Max(1, span.Ticks / plot.Width);
            bool band = columnTicks > 2 * SoundLevelHistory.Nominal.Ticks;
            // One column per reading, in other words, since no two share a tick.
            if (!band) columnTicks = 1;

            var line = new List<PointF>();
            var top = new List<PointF>();
            var bottom = new List<PointF>();
            long column = long.MinValue;
            DateTime columnStartedAt = DateTime.MinValue;
            long offsetTicks = 0;
            int min = 0, max = 0, count = 0;
            double sum = 0;
            DateTime lastAt = DateTime.MinValue;

            using (var linePen = new Pen(lineColor, 2f) { LineJoin = LineJoin.Round })
            using (var bandBrush = new SolidBrush(Color.FromArgb(60, lineColor)))
            using (var dotBrush = new SolidBrush(lineColor))
            {
                Action flushColumn = () =>
                {
                    if (count == 0) return;
                    // Placed at the mean of the readings' own times, so the
                    // point sits where they are and slides with the view by
                    // fractions of a pixel rather than snapping to a whole one.
                    // The times are summed as offsets from the first in the
                    // column, because a sum of absolute ticks would overflow.
                    float x = XOf(columnStartedAt.AddTicks(offsetTicks / count), viewStart, plot);
                    line.Add(new PointF(x, yOf(sum / count)));
                    top.Add(new PointF(x, yOf(max)));
                    bottom.Add(new PointF(x, yOf(min)));
                    count = 0;
                };
                Action flushSegment = () =>
                {
                    flushColumn();
                    if (line.Count == 1)
                    {
                        g.FillEllipse(dotBrush, line[0].X - 1.5f, line[0].Y - 1.5f, 3, 3);
                    }
                    else if (line.Count > 1)
                    {
                        if (band)
                        {
                            var outline = new List<PointF>(top);
                            for (int i = bottom.Count - 1; i >= 0; i--) outline.Add(bottom[i]);
                            g.FillPolygon(bandBrush, outline.ToArray());
                        }
                        g.DrawLines(linePen, line.ToArray());
                    }
                    line.Clear();
                    top.Clear();
                    bottom.Clear();
                };

                for (int i = 0; i < samples.Count; i++)
                {
                    SoundLevelSample s = samples[i];
                    if (s.At < viewStart) { lastAt = s.At; continue; }
                    bool gap = lastAt != DateTime.MinValue && s.At - lastAt > SoundLevelHistory.Overdue;
                    lastAt = s.At;
                    if (s.Level < 0 || gap) flushSegment();
                    if (s.Level < 0) continue;

                    long here = s.At.Ticks / columnTicks;
                    if (here != column)
                    {
                        flushColumn();
                        column = here;
                    }
                    if (count == 0)
                    {
                        min = s.Level;
                        max = s.Level;
                        sum = 0;
                        columnStartedAt = s.At;
                        offsetTicks = 0;
                    }
                    else
                    {
                        offsetTicks += (s.At - columnStartedAt).Ticks;
                    }
                    min = Math.Min(min, s.Level);
                    max = Math.Max(max, s.Level);
                    sum += s.Level;
                    count++;
                }
                flushSegment();
            }
        }

        private void DrawReference(Graphics g, Rectangle plot, Func<double, float> yOf)
        {
            float y = yOf(reference);
            using (var pen = new Pen(ink, 1.3f) { DashPattern = new float[] { 5, 4 } })
            {
                g.DrawLine(pen, plot.Left, y, plot.Right, y);
            }
            string text = reference + " dB";
            Size size = TextRenderer.MeasureText(text, Font);
            var box = new Rectangle(plot.Right - size.Width - 6, (int)y - size.Height - 2, size.Width + 4, size.Height);
            using (var back = new SolidBrush(BackColor))
            {
                g.FillRectangle(back, box);
            }
            TextRenderer.DrawText(g, text, Font, box, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // Ticks on round clock times, so the labels do not shift every second,
        // with "now" pinned to the right edge.
        private void DrawTimeAxis(Graphics g, Rectangle plot, DateTime viewStart, DateTime now)
        {
            int minutes = span.TotalMinutes <= 5 ? 1 : span.TotalMinutes <= 15 ? 3 : span.TotalMinutes <= 30 ? 5 : 10;
            var box = new Rectangle(0, plot.Bottom + 6, 0, 16);
            DateTime localStart = viewStart.ToLocalTime();
            DateTime tick = new DateTime(localStart.Year, localStart.Month, localStart.Day, localStart.Hour, 0, 0, DateTimeKind.Local);
            while (tick < localStart) tick = tick.AddMinutes(minutes);
            float nowX = plot.Right;
            for (; tick.ToUniversalTime() < now; tick = tick.AddMinutes(minutes))
            {
                float x = XOf(tick.ToUniversalTime(), viewStart, plot);
                if (x - plot.Left < 18 || nowX - x < 44) continue;
                box.X = (int)x - 30;
                box.Width = 60;
                TextRenderer.DrawText(g, tick.ToString("HH:mm"), Font, box, subdued, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            box.X = plot.Right - 60;
            box.Width = 60;
            TextRenderer.DrawText(g, "now", Font, box, subdued, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawPeak(Graphics g, Rectangle plot, DateTime viewStart, DateTime now, Func<double, float> yOf)
        {
            if (dragFrom.HasValue) return;
            DateTime from, to;
            GetScope(now, out from, out to);
            if (from < viewStart) from = viewStart;
            SoundLevelStats stats = history.Measure(from, to, reference, now);
            if (stats.Peak < 0) return;

            float x = XOf(stats.PeakAt, viewStart, plot);
            float y = yOf(stats.Peak);
            using (var brush = new SolidBrush(ink))
            {
                g.FillPolygon(brush, new[] { new PointF(x - 5, y - 13), new PointF(x + 5, y - 13), new PointF(x, y - 5) });
            }
            string text = "Peak " + stats.Peak;
            using (var bold = new Font("Segoe UI Semibold", 9f))
            {
                Size size = TextRenderer.MeasureText(text, bold);
                int left = (int)x - size.Width / 2;
                left = Math.Max(plot.Left, Math.Min(plot.Right - size.Width, left));
                int topY = Math.Max(0, (int)y - 15 - size.Height);
                TextRenderer.DrawText(g, text, bold, new Point(left, topY), ink, TextFormatFlags.NoPadding);
            }
        }

        private void DrawHover(Graphics g, Rectangle plot, List<SoundLevelSample> samples, DateTime now)
        {
            if (!hoverX.HasValue || dragFrom.HasValue) return;
            int hx = hoverX.Value;
            if (hx < plot.Left || hx > plot.Right) return;

            DateTime at = TimeAt(hx, now);
            string reading = "no reading";
            TimeSpan best = SoundLevelHistory.Overdue;
            for (int i = 0; i < samples.Count; i++)
            {
                if (at < samples[i].At - best) break;
                TimeSpan distance = samples[i].At > at ? samples[i].At - at : at - samples[i].At;
                if (distance <= best)
                {
                    best = distance;
                    reading = samples[i].Level < 0 ? "nothing playing" : samples[i].Level + " dB";
                }
            }

            using (var pen = new Pen(Color.FromArgb(190, 198, 207)) { DashPattern = new float[] { 2, 3 } })
            {
                g.DrawLine(pen, hx, plot.Top, hx, plot.Bottom);
            }
            string text = at.ToLocalTime().ToString("HH:mm:ss") + "   " + reading;
            using (var bold = new Font("Segoe UI Semibold", 9f))
            {
                Size size = TextRenderer.MeasureText(text, bold);
                var box = new Rectangle(hx + 10, plot.Bottom - size.Height - 14, size.Width + 16, size.Height + 8);
                if (box.Right > plot.Right) box.X = hx - 10 - box.Width;
                g.SmoothingMode = SmoothingMode.None;
                using (var back = new SolidBrush(Color.FromArgb(43, 43, 43)))
                using (var border = new Pen(gridColor))
                {
                    g.FillRectangle(back, box);
                    g.DrawRectangle(border, box);
                }
                TextRenderer.DrawText(g, text, bold, box, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }

    internal sealed class SoundLevelHistoryForm : Form
    {
        private static readonly int[] SpanChoices = { 5, 15, 30, 60 };
        private const int DefaultSpanMinutes = 60;
        private static readonly int[] ReferenceLevels = { 50, 55, 60, 65, 70, 75, 80, 85, 90, 95, 100 };

        private readonly SoundLevelHistory history;
        private readonly Color page;
        private readonly Color card;
        private readonly Color ink;
        private readonly Color subdued;
        private readonly Color blue;
        private readonly Color bluePressed;
        private readonly Color inactive = Color.FromArgb(61, 67, 76);
        private readonly Color inactivePressed = Color.FromArgb(50, 55, 63);
        private readonly System.Windows.Forms.Timer tick;

        private SoundLevelGraph graph;
        private Label nowLabel;
        private Label scopeLabel;
        private PillButton clearButton;
        private ChoiceDropdown referenceBox;
        private readonly PillButton[] spanButtons = new PillButton[SpanChoices.Length];
        private readonly Label[] tileValues = new Label[4];
        private readonly Label[] tileDetails = new Label[4];
        private readonly Label[] tileCaptions = new Label[4];
        private readonly CardPanel[] tiles = new CardPanel[4];
        private CardPanel graphCard;
        private Label referenceCaption;

        public event EventHandler ReferenceChanged;
        public event EventHandler SpanChanged;

        public int Reference { get; private set; }

        public int SpanMinutes { get; private set; }

        public static bool HasSpan(int minutes)
        {
            return Array.IndexOf(SpanChoices, minutes) >= 0;
        }

        public SoundLevelHistoryForm(SoundLevelHistory history, int reference, int spanMinutes, Icon icon, Color page, Color card, Color line, Color ink, Color subdued, Color blue, Color bluePressed)
        {
            this.history = history;
            this.page = page;
            this.card = card;
            this.ink = ink;
            this.subdued = subdued;
            this.blue = blue;
            this.bluePressed = bluePressed;
            Reference = reference;
            SpanMinutes = HasSpan(spanMinutes) ? spanMinutes : DefaultSpanMinutes;

            Text = "Sound level history";
            Icon = icon;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            ClientSize = new Size(760, 540);
            MinimumSize = new Size(560, 460);
            BackColor = page;
            ForeColor = ink;
            Font = new Font("Segoe UI", 10f);

            Build(line);

            tick = new System.Windows.Forms.Timer { Interval = 1000 };
            tick.Tick += (s, e) => RefreshView();
        }

        // Showing depends on whether this window is actually visible, which
        // is what earns the meter the control channel; see ShouldRunMeter.
        public bool IsShowing
        {
            get { return !IsDisposed && Visible && WindowState != FormWindowState.Minimized; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int enabled = 1;
                DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int));
                DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));
                int caption = ColorTranslator.ToWin32(page);
                int captionText = ColorTranslator.ToWin32(ink);
                DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
                DwmSetWindowAttribute(Handle, 36, ref captionText, sizeof(int));
                DwmSetWindowAttribute(Handle, 34, ref caption, sizeof(int));
            }
            catch
            {
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            tick.Start();
            RefreshView();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            tick.Stop();
            tick.Dispose();
            base.OnFormClosed(e);
        }

        private void Build(Color line)
        {
            var nowCaption = new Label
            {
                Text = "Now",
                ForeColor = Color.FromArgb(190, 198, 207),
                Font = new Font("Segoe UI Semibold", 9.2f),
                AutoSize = true,
                Location = new Point(22, 14)
            };
            Controls.Add(nowCaption);

            nowLabel = new Label
            {
                Text = "—",
                ForeColor = ink,
                Font = new Font("Segoe UI Semibold", 17f),
                AutoSize = true,
                Location = new Point(18, 32)
            };
            Controls.Add(nowLabel);

            for (int i = 0; i < SpanChoices.Length; i++)
            {
                int minutes = SpanChoices[i];
                var button = new PillButton(minutes == 60 ? "1 hour" : minutes + " min", inactive, inactivePressed)
                {
                    Size = new Size(76, 32),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right
                };
                button.Click += (s, e) => SetSpan(minutes);
                spanButtons[i] = button;
                Controls.Add(button);
            }

            graphCard = new CardPanel
            {
                BackColor = card,
                BorderColor = line,
                Radius = 8,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            Controls.Add(graphCard);

            graph = new SoundLevelGraph(history, card, ink, subdued, line, blue)
            {
                Reference = Reference,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            graph.SelectionChanged += (s, e) => RefreshView();
            graphCard.Controls.Add(graph);

            scopeLabel = new Label
            {
                ForeColor = Color.FromArgb(190, 198, 207),
                Font = new Font("Segoe UI", 9.2f),
                AutoSize = false,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            Controls.Add(scopeLabel);

            clearButton = new PillButton("Clear selection", inactive, inactivePressed)
            {
                Size = new Size(124, 28),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                Visible = false
            };
            clearButton.Click += (s, e) => graph.ClearSelection();
            Controls.Add(clearButton);

            string[] captions = { "Peak", "Average", "Above " + Reference + " dB", "Readings" };
            for (int i = 0; i < tiles.Length; i++)
            {
                var tile = new CardPanel { BackColor = card, BorderColor = card, Radius = 8, Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
                tileCaptions[i] = new Label { Text = captions[i], ForeColor = subdued, BackColor = card, Font = new Font("Segoe UI", 9f), AutoSize = true, Location = new Point(12, 8) };
                tileValues[i] = new Label { Text = "—", ForeColor = ink, BackColor = card, Font = new Font("Segoe UI Semibold", 14f), AutoSize = true, Location = new Point(10, 26) };
                tileDetails[i] = new Label { ForeColor = Color.FromArgb(190, 198, 207), BackColor = card, Font = new Font("Segoe UI", 8.6f), AutoSize = true, Location = new Point(12, 56) };
                tile.Controls.Add(tileCaptions[i]);
                tile.Controls.Add(tileValues[i]);
                tile.Controls.Add(tileDetails[i]);
                tiles[i] = tile;
                Controls.Add(tile);
            }

            referenceCaption = new Label
            {
                Text = "Reference line",
                ForeColor = Color.FromArgb(190, 198, 207),
                Font = new Font("Segoe UI Semibold", 9.2f),
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom
            };
            Controls.Add(referenceCaption);

            referenceBox = new ChoiceDropdown
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(43, 43, 43),
                ForeColor = ink,
                FlatStyle = FlatStyle.Flat,
                Width = 104,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom
            };
            int selected = 0;
            for (int i = 0; i < ReferenceLevels.Length; i++)
            {
                referenceBox.Items.Add(ReferenceLevels[i] + " dB");
                if (Math.Abs(ReferenceLevels[i] - Reference) < Math.Abs(ReferenceLevels[selected] - Reference)) selected = i;
            }
            referenceBox.SelectedIndex = selected;
            referenceBox.SelectedIndexChanged += (s, e) =>
            {
                if (referenceBox.SelectedIndex < 0) return;
                Reference = ReferenceLevels[referenceBox.SelectedIndex];
                graph.Reference = Reference;
                tileCaptions[2].Text = "Above " + Reference + " dB";
                RefreshView();
                if (ReferenceChanged != null) ReferenceChanged(this, EventArgs.Empty);
            };
            Controls.Add(referenceBox);

            Resize += (s, e) => LayoutControls();
            LayoutControls();
            SetSpan(SpanMinutes);
        }

        private void LayoutControls()
        {
            int margin = 18;
            int width = ClientSize.Width;
            int height = ClientSize.Height;

            int right = width - margin;
            for (int i = spanButtons.Length - 1; i >= 0; i--)
            {
                spanButtons[i].Location = new Point(right - spanButtons[i].Width, 26);
                right = spanButtons[i].Left - 6;
            }

            int footerTop = height - margin - 30;
            referenceCaption.Location = new Point(margin + 2, footerTop + 6);
            referenceBox.Location = new Point(referenceCaption.Right + 10, footerTop);
            referenceBox.Height = 30;

            int tileHeight = 80;
            int tileTop = footerTop - 14 - tileHeight;
            int gapBetween = 10;
            int tileWidth = (width - margin * 2 - gapBetween * (tiles.Length - 1)) / tiles.Length;
            for (int i = 0; i < tiles.Length; i++)
            {
                tiles[i].Bounds = new Rectangle(margin + i * (tileWidth + gapBetween), tileTop, tileWidth, tileHeight);
            }

            int scopeTop = tileTop - 10 - 28;
            clearButton.Location = new Point(width - margin - clearButton.Width, scopeTop);
            scopeLabel.Bounds = new Rectangle(margin + 2, scopeTop, width - margin * 2 - clearButton.Width - 12, 28);

            graphCard.Bounds = new Rectangle(margin, 76, width - margin * 2, Math.Max(80, scopeTop - 8 - 76));
            graph.Bounds = new Rectangle(4, 6, graphCard.Width - 8, graphCard.Height - 10);
        }

        private void SetSpan(int minutes)
        {
            bool changed = SpanMinutes != minutes;
            SpanMinutes = minutes;
            for (int i = 0; i < SpanChoices.Length; i++)
            {
                bool on = SpanChoices[i] == minutes;
                spanButtons[i].SetPalette(on ? blue : inactive, on ? bluePressed : inactivePressed);
            }
            graph.Span = TimeSpan.FromMinutes(minutes);
            RefreshView();
            // Not on the call from Build, which only restores what was saved.
            if (changed && SpanChanged != null) SpanChanged(this, EventArgs.Empty);
        }

        private void RefreshView()
        {
            if (IsDisposed) return;
            DateTime now = DateTime.UtcNow;
            List<SoundLevelSample> samples = history.Samples;

            // Dimmed means the number has stopped being refreshed, the same as
            // the tray and the main window.
            if (samples.Count == 0)
            {
                SetText(nowLabel, "—");
                nowLabel.ForeColor = subdued;
            }
            else
            {
                SoundLevelSample last = samples[samples.Count - 1];
                SetText(nowLabel, last.Level < 0 ? "—" : last.Level + " dB");
                nowLabel.ForeColor = now - last.At > SoundLevelHistory.Overdue ? subdued : ink;
            }

            DateTime from, to;
            graph.GetScope(now, out from, out to);
            SoundLevelStats stats = history.Measure(from, to, Reference, now);

            if (graph.HasSelection)
            {
                double minutes = (to - from).TotalMinutes;
                string length = minutes < 1 ? Math.Round((to - from).TotalSeconds) + " s" : Math.Round(minutes) + " min";
                SetText(scopeLabel, "Selection " + from.ToLocalTime().ToString("HH:mm:ss") + " – " + to.ToLocalTime().ToString("HH:mm:ss") + " (" + length + ")");
            }
            else
            {
                int spanMinutes = (int)graph.Span.TotalMinutes;
                SetText(scopeLabel, (spanMinutes == 60 ? "Last hour" : "Last " + spanMinutes + " minutes") + " · drag across the graph to measure part of it");
            }
            clearButton.Visible = graph.HasSelection;

            if (stats.Peak >= 0)
            {
                SetText(tileValues[0], stats.Peak + " dB");
                SetText(tileDetails[0], "at " + stats.PeakAt.ToLocalTime().ToString("HH:mm:ss"));
            }
            else
            {
                SetText(tileValues[0], "—");
                SetText(tileDetails[0], "");
            }

            if (stats.Average.HasValue)
            {
                int average = (int)Math.Round(stats.Average.Value);
                SetText(tileValues[1], average + " dB");
                int over = average - Reference;
                // Said in words, not by colouring the number.
                SetText(tileDetails[1], over > 0 ? over + " dB over the " + Reference + " dB line"
                    : over == 0 ? "at the " + Reference + " dB line"
                    : -over + " dB under the " + Reference + " dB line");
            }
            else
            {
                SetText(tileValues[1], "—");
                SetText(tileDetails[1], "while playing");
            }

            SetText(tileValues[2], FormatDuration(stats.AboveSeconds));
            SetText(tileDetails[2], stats.AboveSeconds > 0 ? "while playing" : "");

            SetText(tileValues[3], (int)Math.Round(stats.CoveredFraction * 100) + "%");
            SetText(tileDetails[3], graph.HasSelection ? "of the selection" : "of this span");

            graph.Invalidate();
        }

        private static string FormatDuration(double seconds)
        {
            if (seconds < 60) return Math.Round(seconds) + " s";
            double minutes = seconds / 60;
            return (minutes < 10 ? minutes.ToString("0.0") : Math.Round(minutes).ToString()) + " min";
        }

        private static void SetText(Label label, string text)
        {
            if (label.Text != text) label.Text = text;
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
        private PillButton trayMeterOnButton;
        private PillButton trayMeterOffButton;

        public bool StartAtBoot { get; private set; }
        public bool StartMinimizedInTray { get; private set; }
        public bool MinimizeToTrayOnClose { get; private set; }
        public bool ShowSoundLevelInTray { get; private set; }

        public AppSettingsDialog(bool startAtBoot, bool startMinimizedInTray, bool minimizeToTrayOnClose, bool showSoundLevelInTray, Color page, Color card, Color cardSoft, Color line, Color ink, Color subdued, Color blue, Color bluePressed)
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
            ShowSoundLevelInTray = showSoundLevelInTray;

            Text = "App settings";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(560, 498);
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
                Size = new Size(512, 314),
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

            AddDivider(panel, 238);

            trayMeterOnButton = NewSettingButton("On");
            trayMeterOffButton = NewSettingButton("Off");
            AddSettingRow(panel, "Show the sound level in the tray", "Holds the control channel while hidden.", 260, trayMeterOnButton, trayMeterOffButton);
            trayMeterOnButton.Click += (s, e) => SetShowSoundLevelInTray(true);
            trayMeterOffButton.Click += (s, e) => SetShowSoundLevelInTray(false);

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
            SetShowSoundLevelInTray(ShowSoundLevelInTray);
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

        private void SetShowSoundLevelInTray(bool enabled)
        {
            ShowSoundLevelInTray = enabled;
            SetPair(trayMeterOnButton, trayMeterOffButton, enabled);
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
