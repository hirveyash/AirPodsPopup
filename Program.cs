using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

record Pods(string Model, int? Left, int? Right, int? Case, bool CaseCharging);

static class Parser
{
    // Apple "proximity pairing" ad (reverse-engineered, unofficial): 07 19 01 <model> 20 <status> <pods> <case/charge> <lid> <color> ...
    public static Pods? Parse(byte[] d)
    {
        if (d.Length < 10 || d[0] != 0x07 || d[1] != 0x19) return null;
        string model = d[3] switch
        {
            0x02 => "AirPods", 0x0F => "AirPods 2", 0x13 => "AirPods 3", 0x0E => "AirPods Pro",
            0x14 => "AirPods Pro 2", 0x24 => "AirPods Pro 2", 0x27 => "AirPods Pro 3", 0x0A => "AirPods Max", _ => "AirPods"
        };
        bool flip = (d[5] & 0x20) == 0;
        int hi = d[6] >> 4, lo = d[6] & 0xF;
        return new Pods(model, Pct(flip ? hi : lo), Pct(flip ? lo : hi), Pct(d[7] & 0xF), (d[7] & 0x40) != 0);
    }
    static int? Pct(int n) => n <= 10 ? n * 10 : null; // 15 = unknown
}

static class Bt
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVICE_INFO
    {
        public uint dwSize; public ulong Address; public uint Class, Connected, Remembered, Authenticated;
        public long LastSeen1, LastSeen2, LastUsed1, LastUsed2;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
    }
    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern uint BluetoothSetServiceState(IntPtr radio, ref DEVICE_INFO info, ref Guid service, uint flags);

    // A2DP sink only. Enabling Handsfree too made Windows pick the low-quality, quiet call profile as the output.
    static readonly Guid[] Services = { new("0000110b-0000-1000-8000-00805f9b34fb") };

    // Paired device whose name contains "AirPods"; prefers one that is already connected.
    public static async Task<(ulong addr, bool connected)?> FindAsync()
    {
        var infos = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
        (ulong, bool)? best = null;
        foreach (var i in infos.Where(i => i.Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase)))
        {
            using var dev = await BluetoothDevice.FromIdAsync(i.Id);
            if (dev == null) continue;
            var c = dev.ConnectionStatus == BluetoothConnectionStatus.Connected;
            if (best == null || c) best = (dev.BluetoothAddress, c);
            if (c) break;
        }
        return best;
    }

    // Windows has no "connect" API; toggling the audio services makes it (re)connect the paired device.
    public static async Task<bool> IsConnectedAsync(ulong addr)
    {
        using var dev = await BluetoothDevice.FromBluetoothAddressAsync(addr);
        return dev?.ConnectionStatus == BluetoothConnectionStatus.Connected;
    }

    public static async Task<bool> ConnectAsync(ulong addr)
    {
        var info = new DEVICE_INFO { dwSize = (uint)Marshal.SizeOf<DEVICE_INFO>(), Address = addr, Name = "" };
        void Set(uint flag) { foreach (var g in Services) { var s = g; BluetoothSetServiceState(IntPtr.Zero, ref info, ref s, flag); } }
        Set(1); // fast path: just enable
        for (int i = 0; i < 40; i++)
        {
            if (i == 12) { Set(0); Set(1); } // slow path after ~3 s: toggle to force a reconnect
            await Task.Delay(250);
            if (await IsConnectedAsync(addr)) return true;
        }
        return false;
    }
}

class Popup : Form
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    readonly Label title = new(), batt = new();
    readonly Button btn = new();
    readonly System.Windows.Forms.Timer hide = new() { Interval = 8000 };
    ulong addr; bool busy;

    public Popup()
    {
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None; TopMost = true; ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual; ClientSize = new Size(340, 130);
        BackColor = Color.FromArgb(32, 32, 36); ForeColor = Color.White; Font = new Font("Segoe UI", 10);
        title.SetBounds(16, 12, 308, 30); title.Font = new Font("Segoe UI Semibold", 13);
        batt.SetBounds(16, 44, 308, 28); batt.ForeColor = Color.Gainsboro;
        btn.SetBounds(16, 80, 308, 36); btn.FlatStyle = FlatStyle.Flat; btn.FlatAppearance.BorderSize = 0;
        btn.BackColor = Color.FromArgb(10, 132, 255); btn.Click += async (_, _) => await ConnectAsync();
        Controls.AddRange(new Control[] { title, batt, btn });
        hide.Tick += (_, _) => { if (!Bounds.Contains(Cursor.Position) && !busy) { hide.Stop(); Hide(); } };
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; } } // NOACTIVATE | TOOLWINDOW
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); int r = 2; DwmSetWindowAttribute(Handle, 33, ref r, 4); } // rounded corners (Win11)

    static string P(int? n) => n is int v ? $"{v}%" : "-";

    public void ShowPods(Pods p)
    {
        title.Text = p.Model;
        batt.Text = $"L {P(p.Left)}    R {P(p.Right)}    Case {P(p.Case)}{(p.CaseCharging ? " (charging)" : "")}";
        if (!Visible)
        {
            var wa = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
            Show();
            _ = RefreshStatus();
            hide.Stop(); hide.Start(); // timer starts only on first show; later ads just refresh the text
        }
    }

    // Polls while the popup is up: Windows' connection state lags a few seconds behind the case lid.
    async Task RefreshStatus()
    {
        btn.Enabled = false; btn.Text = "Checking...";
        while (Visible)
        {
            try
            {
                if (busy) { await Task.Delay(1000); continue; }
                if (addr == 0)
                {
                    var f = await Bt.FindAsync();
                    if (f is null) { btn.Text = "Pair AirPods in Windows first"; return; }
                    addr = f.Value.addr;
                }
                var c = await Bt.IsConnectedAsync(addr);
                if (!busy) { btn.Text = c ? "Connected" : "Connect"; btn.Enabled = !c; }
            }
            catch { btn.Text = "Bluetooth unavailable"; return; }
            await Task.Delay(1000);
        }
    }

    async Task ConnectAsync()
    {
        busy = true; hide.Stop(); btn.Enabled = false; btn.Text = "Connecting...";
        try { var ok = await Bt.ConnectAsync(addr); btn.Text = ok ? "Connected" : "Couldn't connect - retry"; btn.Enabled = !ok; }
        catch { btn.Text = "Couldn't connect - retry"; btn.Enabled = true; }
        busy = false;
        hide.Interval = 4000; hide.Start(); // linger briefly so the result is seen
    }

    // Reset to the default delay for the next showing.
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) hide.Interval = 8000;
    }
}

static class Program
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", AppName = "AirPodsPopup";
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr h, nint min, nint max);

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest();
        using var mutex = new Mutex(true, "AirPodsPopup.Single", out var first);
        if (!first) return 0;

        Application.EnableVisualStyles();
        var ui = new WindowsFormsSynchronizationContext();
        var popup = new Popup(); _ = popup.Handle; // create handle so we can marshal onto the UI thread
        DateTime last = DateTime.MinValue;
        var debug = args.Contains("--debug"); // logs raw ads to %TEMP%\airpods-debug.log

        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
        watcher.AdvertisementFilter.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData { CompanyId = 0x004C });
        watcher.Received += (_, a) =>
        {
            foreach (var m in a.Advertisement.ManufacturerData)
            {
                var d = new byte[m.Data.Length]; DataReader.FromBuffer(m.Data).ReadBytes(d);
                if (debug) File.AppendAllText(Path.Combine(Path.GetTempPath(), "airpods-debug.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} addr={a.BluetoothAddress:X12} rssi={a.RawSignalStrengthInDBm} {Convert.ToHexString(d)}\n");
                if (Parser.Parse(d) is not { } p) continue;
                // shortcut: signal strength is the only "is it mine" test (AirPods rotate their address every few seconds); neighbours measured <= -60 dBm, mine -22..-38. Upgrade if a 2nd pair sits within ~30 cm.
                if (a.RawSignalStrengthInDBm < -50) continue;
                var fresh = DateTime.UtcNow - last > TimeSpan.FromSeconds(15);
                last = DateTime.UtcNow;
                // Case battery reads "unknown" when the buds are out of the case (in ears), so only a case-open event can trigger.
                if (popup.Visible || (fresh && p.Case != null)) ui.Post(_ => popup.ShowPods(p), null);
            }
        };
        watcher.Start();

        var menu = new ContextMenuStrip();
        var auto = new ToolStripMenuItem("Start with Windows") { Checked = Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(AppName) != null, CheckOnClick = true };
        auto.CheckedChanged += (_, _) =>
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true)!;
            if (auto.Checked) k.SetValue(AppName, $"\"{Environment.ProcessPath}\""); else k.DeleteValue(AppName, false);
        };
        menu.Items.Add(auto);
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());
        using var tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "AirPods Popup", ContextMenuStrip = menu, Visible = true };

        // Notify when the paired AirPods disconnect (e.g. put back in the case). Retries until a paired pair exists.
        BluetoothDevice? watched = null;
        _ = Task.Run(async () =>
        {
            while (watched == null)
            {
                try
                {
                    if (await Bt.FindAsync() is { } f && await BluetoothDevice.FromBluetoothAddressAsync(f.addr) is { } dev)
                    {
                        dev.ConnectionStatusChanged += (s, _) =>
                        {
                            if (s.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                                ui.Post(_ => tray.ShowBalloonTip(3000, "AirPods", $"{s.Name} disconnected", ToolTipIcon.None), null);
                        };
                        watched = dev; // keep a reference so the event keeps firing
                        break;
                    }
                }
                catch { }
                await Task.Delay(30000);
            }
        });

        // Idle tray app: periodically give back resident pages so Task Manager shows a small number. Pages fault back in on demand.
        using var trim = new System.Windows.Forms.Timer { Interval = 30000 };
        trim.Tick += (_, _) => SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
        trim.Start();

        Application.Run();
        watcher.Stop(); tray.Visible = false; watched?.Dispose();
        return 0;
    }

    // Pro, not flipped (0x20 set) -> left = low nibble 0xA(100%)... see expected values below.
    static int SelfTest()
    {
        var p = Parser.Parse(new byte[] { 0x07, 0x19, 0x01, 0x0E, 0x20, 0x20, 0x8A, 0x05, 0, 0 });
        var real = Parser.Parse(Convert.FromHexString("071901272024A7DA5100")); // captured from a real AirPods Pro 3
        var ok = p == new Pods("AirPods Pro", 100, 80, 50, false) && real == new Pods("AirPods Pro 3", 70, 100, 100, true)
            && Parser.Parse(new byte[] { 1, 2 }) == null;
        Console.Error.WriteLine(ok ? "selftest ok" : $"selftest FAILED: {p}");
        return ok ? 0 : 1;
    }
}
