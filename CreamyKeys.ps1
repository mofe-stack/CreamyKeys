# CreamyKeys - system-wide keyboard sounds using the Opera GX "Creamy Keyboard" mod samples.
# Tray icon: left-click toggles mute, right-click for the menu.
param(
    [ValidateRange(1, 100)] [int] $Volume = 60,
    [switch] $PlayOnKeyRepeat
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

$root      = Split-Path -Parent $PSCommandPath
$soundDir  = Join-Path $root 'sounds'   # pristine samples from the mod
$renderDir = Join-Path $root 'render'   # volume-baked copies that MCI actually opens
$iconPath  = Join-Path $root 'icon.png'

# Single instance only - a second hook would double every click.
$mutex = New-Object System.Threading.Mutex($false, 'Global\CreamyKeys_SingleInstance')
if (-not $mutex.WaitOne(0, $false)) {
    [System.Windows.Forms.MessageBox]::Show('CreamyKeys is already running.', 'CreamyKeys') | Out-Null
    return
}

$engine = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class CreamyKeys
{
    // --- win32 ---------------------------------------------------------
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

    private sealed class Job
    {
        public int Kind;
        public string Category;
        public int Volume;
    }

    private static IntPtr _hook = IntPtr.Zero;
    private static HookProc _proc;            // must stay rooted or the GC eats the callback
    private static readonly HashSet<int> _down = new HashSet<int>();
    private static readonly Dictionary<string, List<string[]>> _voices =
        new Dictionary<string, List<string[]>>();
    private static readonly Queue<Job> _queue = new Queue<Job>();
    private static readonly object _gate = new object();
    private static readonly ManualResetEvent _ready = new ManualResetEvent(false);
    private static readonly Random _rng = new Random(20260805);
    private static Thread _worker;
    private static string _srcDir, _dstDir, _startError;
    private static bool _running;
    private static int _cursor;

    public static bool Muted;
    public static bool PlayOnRepeat;

    // Diagnostics. Only the worker thread may talk to MCI, so it reports its
    // own results here rather than the caller querying device state directly.
    public static long KeyCount;      // keystrokes the hook has seen
    public static long PlayedOk;      // plays MCI accepted
    public static string LastMciError;
    public static string LastPlayMode;
    public static bool Diagnose;

    // --- audio ---------------------------------------------------------
    // Every mciSendString below runs on the worker thread. MCI aliases are
    // thread-affine: a device opened on one thread returns error 263 to any
    // other, so the thread that opens the voices must also be the one that
    // plays and closes them.
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
                if (Diagnose) LastPlayMode = alias + "=" + Mci("status " + alias + " mode");
            }
            else if (job.Kind == RELOAD)
            {
                CloseVoices();                       // release handles before rewriting
                Render(_srcDir, _dstDir, job.Volume);
                OpenVoices();
            }
        }

        CloseVoices();
    }

    // --- hook ----------------------------------------------------------
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

    // --- lifecycle -----------------------------------------------------
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
'@

Add-Type -TypeDefinition $engine -Language CSharp

[CreamyKeys]::Start($soundDir, $renderDir, $Volume, [bool]$PlayOnKeyRepeat)

# --- tray icon ---------------------------------------------------------
try {
    $bmp  = [System.Drawing.Bitmap]::FromFile($iconPath)
    $icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
} catch {
    $icon = [System.Drawing.SystemIcons]::Application
}

$tray = New-Object System.Windows.Forms.NotifyIcon
$tray.Icon    = $icon
$tray.Text    = 'CreamyKeys'
$tray.Visible = $true

$menu     = New-Object System.Windows.Forms.ContextMenuStrip
$muteItem = $menu.Items.Add('Mute')
$muteItem.add_Click({
    $m = -not [CreamyKeys]::Muted
    [CreamyKeys]::Muted = $m
    $muteItem.Text = if ($m) { 'Unmute' } else { 'Mute' }
    $tray.Text     = if ($m) { 'CreamyKeys (muted)' } else { 'CreamyKeys' }
})

$volMenu = New-Object System.Windows.Forms.ToolStripMenuItem('Volume')
foreach ($v in 20, 40, 60, 80, 100) {
    $item = $volMenu.DropDownItems.Add("$v%")
    $item.Tag = $v
    $item.add_Click({ [CreamyKeys]::SetVolume([int]$this.Tag) })
}
$menu.Items.Add($volMenu) | Out-Null

$menu.Items.Add('-') | Out-Null
$menu.Items.Add('Exit').add_Click({
    $tray.Visible = $false
    [CreamyKeys]::Stop()
    [System.Windows.Forms.Application]::ExitThread()
})

$tray.ContextMenuStrip = $menu
$tray.add_MouseClick({ if ($_.Button -eq 'Left') { $muteItem.PerformClick() } })

try {
    # Pumps the message loop the low-level hook depends on.
    [System.Windows.Forms.Application]::Run()
} finally {
    $tray.Visible = $false
    $tray.Dispose()
    [CreamyKeys]::Stop()
    $mutex.ReleaseMutex()
}
