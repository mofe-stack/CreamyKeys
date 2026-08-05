// CreamyKeys - system-wide keyboard sounds using the Opera GX "Creamy Keyboard" mod samples.
// Build: run build.cmd (embeds the sounds and icons into the exe as resources).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// ---------------------------------------------------------------------------
// Audio + keyboard hook engine
// ---------------------------------------------------------------------------
static class Engine
{
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern int mciSendString(string cmd, StringBuilder ret, int retLen, IntPtr hwnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int cch);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int VK_BACK = 0x08, VK_RETURN = 0x0D, VK_SPACE = 0x20;

    // Samples are ~300ms but you can type far faster than that, and one MCI
    // device plays one thing at a time - so each sample gets a pool of voices
    // to round-robin through.
    private const int POOL = 6;
    private const int PLAY = 1, RELOAD = 2;

    private static readonly string[] LETTERS = { "letter_1", "letter_2", "letter_3" };

    private sealed class Job { public int Kind; public string Category; public int Volume; }

    private static IntPtr _hook = IntPtr.Zero;
    private static HookProc _proc;            // must stay rooted or the GC eats the callback
    private static readonly HashSet<int> _down = new HashSet<int>();
    private static readonly Dictionary<string, List<string[]>> _voices =
        new Dictionary<string, List<string[]>>();
    private static readonly Queue<Job> _queue = new Queue<Job>();
    private static readonly object _gate = new object();
    private static readonly ManualResetEvent _ready = new ManualResetEvent(false);
    private static readonly Random _rng = new Random();
    private static Thread _worker;
    private static string _srcDir, _dstDir, _startError;
    private static bool _running;
    private static int _cursor;

    public static bool Muted;
    public static bool PlayOnRepeat;
    public static long KeyCount, PlayedOk;
    public static string LastMciError;

    // Every mciSendString below runs on the worker thread. MCI aliases are
    // thread-affine: a device opened on one thread returns error 263 to any
    // other, so the thread that opens the voices must also play and close them.
    private static string Mci(string cmd)
    {
        var buf = new StringBuilder(256);
        mciSendString(cmd, buf, buf.Capacity, IntPtr.Zero);
        return buf.ToString();
    }

    private static int MciRc(string cmd)
    {
        var buf = new StringBuilder(256);
        return mciSendString(cmd, buf, buf.Capacity, IntPtr.Zero);
    }

    // MCI's command parser splits on spaces, so prefer the 8.3 path and fall
    // back to quoting if short-name generation is disabled on the volume.
    private static string MciPath(string path)
    {
        var buf = new StringBuilder(400);
        int n = GetShortPathName(path, buf, buf.Capacity);
        if (n > 0 && n < buf.Capacity && buf.ToString().IndexOf(' ') < 0) return buf.ToString();
        return "\"" + path + "\"";
    }

    // Bake the requested gain into a copy of each sample; MCI waveaudio here
    // rejects "setaudio volume", so this is how volume gets applied.
    public static void Render(string srcDir, string dstDir, int volume)
    {
        Directory.CreateDirectory(dstDir);
        var names = new List<string>(LETTERS) { "space", "enter", "backspace" };

        foreach (string name in names)
        {
            byte[] b = File.ReadAllBytes(Path.Combine(srcDir, name + ".wav"));

            int pos = 12, dataOff = -1, dataLen = 0;
            while (pos + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, pos, 4);
                int sz = BitConverter.ToInt32(b, pos + 4);
                if (id == "data") { dataOff = pos + 8; dataLen = Math.Min(sz, b.Length - dataOff); break; }
                pos += 8 + sz + (sz & 1);
            }

            if (dataOff > 0 && volume < 100)
            {
                double g = volume / 100.0;
                for (int i = dataOff; i + 1 < dataOff + dataLen; i += 2)
                {
                    short s = (short)(b[i] | (b[i + 1] << 8));
                    int v = (int)Math.Round(s * g);
                    if (v > 32767) v = 32767;
                    if (v < -32768) v = -32768;
                    b[i] = (byte)(v & 0xff);
                    b[i + 1] = (byte)((v >> 8) & 0xff);
                }
            }

            File.WriteAllBytes(Path.Combine(dstDir, name + ".wav"), b);
        }
    }

    private static void OpenVoices()
    {
        LoadCategory("letter",    LETTERS);
        LoadCategory("space",     new[] { "space" });
        LoadCategory("enter",     new[] { "enter" });
        LoadCategory("backspace", new[] { "backspace" });
    }

    private static void LoadCategory(string category, string[] names)
    {
        var pools = new List<string[]>();
        for (int f = 0; f < names.Length; f++)
        {
            var aliases = new string[POOL];
            for (int i = 0; i < POOL; i++)
            {
                string alias = "ck_" + category + "_" + f + "_" + i;
                Mci("open " + MciPath(Path.Combine(_dstDir, names[f] + ".wav")) +
                    " type waveaudio alias " + alias);
                aliases[i] = alias;
            }
            pools.Add(aliases);
        }
        _voices[category] = pools;
    }

    private static void CloseVoices()
    {
        foreach (var pools in _voices.Values)
            foreach (var pool in pools)
                foreach (var alias in pool)
                    Mci("close " + alias);
        _voices.Clear();
    }

    private static void Pump()
    {
        try
        {
            OpenVoices();
            // Opening the waveout device costs ~100ms on first play; burn that
            // now on a 1ms slice so the first real keystroke isn't late.
            Mci("play ck_letter_0_0 from 0 to 1");
        }
        catch (Exception ex) { _startError = ex.Message; }
        finally { _ready.Set(); }

        while (true)
        {
            Job job;
            lock (_gate)
            {
                while (_running && _queue.Count == 0) Monitor.Wait(_gate);
                if (!_running) break;
                job = _queue.Dequeue();
            }

            if (job.Kind == PLAY)
            {
                List<string[]> pools;
                if (!_voices.TryGetValue(job.Category, out pools) || pools.Count == 0) continue;
                string[] pool = pools[pools.Count == 1 ? 0 : _rng.Next(pools.Count)];
                string alias = pool[(_cursor++ & 0x7fffffff) % pool.Length];

                int rc = MciRc("play " + alias + " from 0");
                if (rc == 0) Interlocked.Increment(ref PlayedOk);
                else LastMciError = "rc=" + rc + " playing " + alias;
            }
            else if (job.Kind == RELOAD)
            {
                CloseVoices();                        // release handles before rewriting
                Render(_srcDir, _dstDir, job.Volume);
                OpenVoices();
            }
        }

        CloseVoices();
    }

    private static IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Runs on the input path: Windows silently drops the hook if this takes
        // longer than LowLevelHooksTimeout (~300ms). Enqueue and get out.
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            int vk = Marshal.ReadInt32(lParam);

            if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
            {
                lock (_gate) _down.Remove(vk);
            }
            else if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                bool fresh;
                lock (_gate) fresh = _down.Add(vk);
                Interlocked.Increment(ref KeyCount);

                if (!Muted && (fresh || PlayOnRepeat))
                {
                    string category =
                        vk == VK_BACK   ? "backspace" :
                        vk == VK_RETURN ? "enter"     :
                        vk == VK_SPACE  ? "space"     : "letter";

                    lock (_gate)
                    {
                        if (_queue.Count < 24)
                            _queue.Enqueue(new Job { Kind = PLAY, Category = category });
                        Monitor.Pulse(_gate);
                    }
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public static void Start(string srcDir, string dstDir, int volume, bool playOnRepeat)
    {
        PlayOnRepeat = playOnRepeat;
        _srcDir = srcDir;
        _dstDir = dstDir;
        Render(srcDir, dstDir, volume);

        _running = true;
        _worker = new Thread(Pump);
        _worker.IsBackground = true;
        _worker.Priority = ThreadPriority.AboveNormal;
        _worker.Start();

        if (!_ready.WaitOne(10000)) throw new Exception("audio thread failed to start");
        if (_startError != null) throw new Exception("audio init failed: " + _startError);

        _proc = OnKey;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new Exception("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
    }

    // Windows silently detaches low-level hooks across sleep, lock and fast
    // user switching, and gives no notification when it does - the process
    // keeps running while every keystroke stops arriving. Re-arming is cheap,
    // so just do it rather than trying to detect the dead state.
    public static bool HookAlive { get { return _hook != IntPtr.Zero; } }

    public static void ReinstallHook()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        _proc = OnKey;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    public static void SetVolume(int volume)
    {
        lock (_gate)
        {
            _queue.Enqueue(new Job { Kind = RELOAD, Volume = volume });
            Monitor.Pulse(_gate);
        }
    }

    public static void Stop()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        lock (_gate) { _running = false; Monitor.PulseAll(_gate); }
        if (_worker != null) { _worker.Join(3000); _worker = null; }   // let it close its own voices
    }
}

// ---------------------------------------------------------------------------
// Tray application
// ---------------------------------------------------------------------------
static class Program
{
    private const string RUN_KEY  = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RUN_NAME = "CreamyKeys";

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static NotifyIcon _tray;
    private static ToolStripMenuItem _muteItem, _volMenu, _startupItem;
    private static string _dir, _settings;
    private static int _volume = 100;
    private static volatile bool _needsRehook;
    private static bool _promoted;

    [STAThread]
    static void Main()
    {
        _dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        _settings = Path.Combine(_dir, "settings.txt");

        bool fresh;
        // Held for the process lifetime - keeps a second copy from double-clicking.
        var mutex = new Mutex(true, @"Global\CreamyKeys_SingleInstance", out fresh);
        var ping = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\CreamyKeys_Ping");

        if (!fresh)
        {
            ping.Set();     // tell the running copy to say hello, then step aside
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // The sounds and icons are embedded in the exe so a download is one
        // file; unpack them next to the exe (or LocalAppData if that fails,
        // e.g. Program Files) so users can still swap in their own wavs.
        try { ExtractAssets(_dir); }
        catch
        {
            _dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CreamyKeys");
            Directory.CreateDirectory(_dir);
            ExtractAssets(_dir);
        }
        _settings = Path.Combine(_dir, "settings.txt");

        LoadSettings();

        try
        {
            Engine.Start(Path.Combine(_dir, "sounds"), Path.Combine(_dir, "render"), _volume, false);
        }
        catch (Exception ex)
        {
            MessageBox.Show("CreamyKeys could not start:\n\n" + ex.Message,
                "CreamyKeys", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        BuildTray();
        _promoted = PromoteTrayIcon();

        // Re-arm the hook on the events that kill it, plus a slow safety net.
        SystemEvents.PowerModeChanged += delegate (object s, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) _needsRehook = true;
        };
        SystemEvents.SessionSwitch += delegate (object s, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock ||
                e.Reason == SessionSwitchReason.ConsoleConnect ||
                e.Reason == SessionSwitchReason.SessionLogon) _needsRehook = true;
        };

        int ticks = 0;
        var revive = new System.Windows.Forms.Timer { Interval = 5000 };
        revive.Tick += delegate
        {
            ticks++;
            if (_needsRehook) { _needsRehook = false; Engine.ReinstallHook(); }
            else if (ticks % 12 == 0) Engine.ReinstallHook();   // every 60s

            // On the very first run Explorer only creates this icon's registry
            // entry after the icon appears, so keep trying until it sticks.
            if (!_promoted && PromoteTrayIcon())
            {
                _promoted = true;
                _tray.Visible = false;   // re-register so it moves out of the overflow now
                _tray.Visible = true;
            }

            // Heartbeat so a dead hook can be told apart from dead audio.
            try
            {
                File.WriteAllText(Path.Combine(_dir, "status.txt"),
                    "hook=" + (Engine.HookAlive ? "alive" : "DEAD") +
                    "\r\nkeys=" + Engine.KeyCount +
                    "\r\nplayed=" + Engine.PlayedOk +
                    "\r\nmuted=" + Engine.Muted +
                    "\r\nlastMciError=" + (Engine.LastMciError ?? "none") + "\r\n");
            }
            catch { }
        };
        revive.Start();

        // A hidden form gives us a WinForms sync context to marshal the
        // "already running" ping back onto the UI thread.
        var anchor = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized };
        anchor.Load += (s, e) => anchor.Visible = false;
        var ctx = new System.Windows.Forms.Timer { Interval = 1 };  // forces handle creation
        anchor.CreateControl();
        var sync = SynchronizationContext.Current;

        var pinger = new Thread(() =>
        {
            while (true)
            {
                ping.WaitOne();
                if (sync != null) sync.Post(_ => Announce(), null);
            }
        });
        pinger.IsBackground = true;
        pinger.Start();

        try { Application.Run(); }
        finally
        {
            _tray.Visible = false;
            _tray.Dispose();
            Engine.Stop();
            GC.KeepAlive(mutex);
        }
    }

    private static void ExtractAssets(string dir)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        Directory.CreateDirectory(Path.Combine(dir, "sounds"));
        foreach (string res in asm.GetManifestResourceNames())
        {
            string target = res.StartsWith("sounds.")
                ? Path.Combine(dir, "sounds", res.Substring("sounds.".Length))
                : Path.Combine(dir, res);
            if (File.Exists(target)) continue;   // never clobber user-swapped files
            using (var s = asm.GetManifestResourceStream(res))
            using (var f = File.Create(target))
                s.CopyTo(f);
        }
    }

    private static void Announce()
    {
        _tray.BalloonTipTitle = "CreamyKeys is already running";
        _tray.BalloonTipText  = Engine.Muted
            ? "Currently muted - click the tray icon to unmute."
            : "Volume " + _volume + "%. Click the tray icon to mute.";
        _tray.ShowBalloonTip(3000);
    }

    private static Icon _iconOn, _iconMuted;

    private static Icon Load(string name, Icon fallback)
    {
        try { return new Icon(Path.Combine(_dir, name), SystemInformation.SmallIconSize); }
        catch { return fallback; }
    }

    private static void BuildTray()
    {
        _iconOn    = Load("icon.ico", SystemIcons.Application);
        _iconMuted = Load("icon_muted.ico", _iconOn);

        _tray = new NotifyIcon { Icon = _iconOn, Text = "CreamyKeys", Visible = true };

        var menu = new ContextMenuStrip();

        _muteItem = new ToolStripMenuItem("Mute", null, (s, e) => ToggleMute());
        menu.Items.Add(_muteItem);

        _volMenu = new ToolStripMenuItem("Volume");
        foreach (int v in new[] { 20, 40, 60, 80, 100 })
        {
            int captured = v;
            var item = new ToolStripMenuItem(v + "%", null, (s, e) => ApplyVolume(captured));
            item.Tag = v;
            _volMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(_volMenu);

        menu.Items.Add(new ToolStripSeparator());

        _startupItem = new ToolStripMenuItem("Start with Windows", null, (s, e) => ToggleStartup());
        menu.Items.Add(_startupItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) =>
        {
            _tray.Visible = false;
            Engine.Stop();
            Application.ExitThread();
        }));

        // Shown manually instead of via ContextMenuStrip so it anchors just
        // above the icon rather than wherever the cursor happens to be.
        // SetForegroundWindow makes it dismiss when clicking elsewhere.
        _tray.MouseUp += (s, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                SetForegroundWindow(menu.Handle);
                menu.Show(Cursor.Position, ToolStripDropDownDirection.AboveLeft);
            }
        };
        _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleMute(); };

        RefreshChecks();
    }

    private static void ToggleMute()
    {
        Engine.Muted = !Engine.Muted;
        SaveSettings();
        RefreshChecks();
    }

    private static void ApplyVolume(int v)
    {
        _volume = v;
        Engine.SetVolume(v);
        if (Engine.Muted) { Engine.Muted = false; }   // picking a volume implies you want to hear it
        SaveSettings();
        RefreshChecks();
    }

    private static void ToggleStartup()
    {
        bool on = !IsStartupEnabled();
        using (var k = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
        {
            if (k == null) return;
            if (on) k.SetValue(RUN_NAME, "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue(RUN_NAME, false);
        }
        RefreshChecks();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern int GetLongPathName(string shortPath, StringBuilder longPath, int cch);

    // The exe can be launched via an 8.3 short path while Explorer records the
    // long one - canonicalize before comparing.
    private static string LongPath(string p)
    {
        try
        {
            var b = new StringBuilder(1024);
            int n = GetLongPathName(p, b, b.Capacity);
            return n > 0 && n < b.Capacity ? b.ToString() : p;
        }
        catch { return p; }
    }

    // Windows 11 hides new tray icons behind the overflow arrow. The per-icon
    // "show on taskbar" toggle lives under NotifyIconSettings, keyed by a
    // generated id - find our entry by executable path and flip it.
    private static bool PromoteTrayIcon()
    {
        try
        {
            string self = LongPath(Application.ExecutablePath);
            using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
            {
                if (root == null) return false;
                foreach (string sub in root.GetSubKeyNames())
                {
                    using (var k = root.OpenSubKey(sub, true))
                    {
                        if (k == null) continue;
                        var exe = k.GetValue("ExecutablePath") as string;
                        if (exe != null &&
                            LongPath(exe).Equals(self, StringComparison.OrdinalIgnoreCase))
                        {
                            k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                            return true;
                        }
                    }
                }
            }
        }
        catch { }
        return false;
    }

    private static bool IsStartupEnabled()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RUN_KEY))
            return k != null && k.GetValue(RUN_NAME) != null;
    }

    private static void RefreshChecks()
    {
        _muteItem.Text = Engine.Muted ? "Unmute" : "Mute";
        // Swap the icon too - a tooltip-only cue is invisible, and a stray
        // left-click on the tray icon silently mutes everything.
        _tray.Icon = Engine.Muted ? _iconMuted : _iconOn;
        _tray.Text = Engine.Muted ? "CreamyKeys (MUTED)" : "CreamyKeys - " + _volume + "%";
        foreach (ToolStripMenuItem item in _volMenu.DropDownItems)
            item.Checked = (int)item.Tag == _volume;
        _startupItem.Checked = IsStartupEnabled();
    }

    private static void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settings)) return;
            foreach (string line in File.ReadAllLines(_settings))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                if (key == "volume")
                {
                    int v;
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)
                        && v >= 1 && v <= 100) _volume = v;
                }
                else if (key == "muted")
                {
                    Engine.Muted = (val == "true");
                }
            }
        }
        catch { /* defaults are fine */ }
    }

    private static void SaveSettings()
    {
        try
        {
            File.WriteAllText(_settings,
                "volume=" + _volume.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                "muted=" + (Engine.Muted ? "true" : "false") + Environment.NewLine);
        }
        catch { /* not worth bothering the user about */ }
    }
}
