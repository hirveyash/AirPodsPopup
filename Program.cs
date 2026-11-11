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
            0x14 => "AirPods Pro 2", 0x24 => "AirPods Pro 2", 0x0A => "AirPods Max", _ => "AirPods"
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

    static readonly Guid[] Services = { new("0000110b-0000-1000-8000-00805f9b34fb"), new("0000111e-0000-1000-8000-00805f9b34fb") }; // A2DP sink, Handsfree

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
    public static async Task<bool> ConnectAsync(ulong addr)
    {
        var info = new DEVICE_INFO { dwSize = (uint)Marshal.SizeOf<DEVICE_INFO>(), Address = addr, Name = "" };
        foreach (var g in Services) { var s = g; BluetoothSetServiceState(IntPtr.Zero, ref info, ref s, 0); }
        foreach (var g in Services) { var s = g; BluetoothSetServiceState(IntPtr.Zero, ref info, ref s, 1); }
        for (int i = 0; i < 16; i++)
        {
            await Task.Delay(500);
            using var dev = await BluetoothDevice.FromBluetoothAddressAsync(addr);
            if (dev?.ConnectionStatus == BluetoothConnectionStatus.Connected) return true;
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
        }
        hide.Stop(); hide.Start();
    }

    async Task RefreshStatus()
    {
        btn.Enabled = false; btn.Text = "Checking...";
        try
        {
            var f = await Bt.FindAsync();
            if (f is null) { btn.Text = "Pair AirPods in Windows first"; return; }
            addr = f.Value.addr;
            btn.Text = f.Value.connected ? "Connected" : "Connect"; btn.Enabled = !f.Value.connected;
        }
        catch { btn.Text = "Bluetooth unavailable"; }
    }

    async Task ConnectAsync()
    {
        busy = true; btn.Enabled = false; btn.Text = "Connecting...";
        try { var ok = await Bt.ConnectAsync(addr); btn.Text = ok ? "Connected" : "Couldn't connect - retry"; btn.Enabled = !ok; }
        catch { btn.Text = "Couldn't connect - retry"; btn.Enabled = true; }
        busy = false;
    }
}

static class Program
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", AppName = "AirPodsPopup";

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

        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
        watcher.AdvertisementFilter.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData { CompanyId = 0x004C });
        watcher.Received += (_, a) =>
        {
            foreach (var m in a.Advertisement.ManufacturerData)
            {
                var d = new byte[m.Data.Length]; DataReader.FromBuffer(m.Data).ReadBytes(d);
                if (Parser.Parse(d) is not { } p) continue;
                var near = a.RawSignalStrengthInDBm >= -70; // ignore someone else's AirPods across the room
                var fresh = DateTime.UtcNow - last > TimeSpan.FromSeconds(15);
                if (near) last = DateTime.UtcNow;
                if (near && (fresh || popup.Visible)) ui.Post(_ => popup.ShowPods(p), null);
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

        Application.Run();
        watcher.Stop(); tray.Visible = false;
        return 0;
    }

    // Pro, not flipped (0x20 set) -> left = low nibble 0xA(100%)... see expected values below.
    static int SelfTest()
    {
        var p = Parser.Parse(new byte[] { 0x07, 0x19, 0x01, 0x0E, 0x20, 0x20, 0x8A, 0x05, 0, 0 });
        var ok = p == new Pods("AirPods Pro", 100, 80, 50, false) && Parser.Parse(new byte[] { 1, 2 }) == null;
        Console.Error.WriteLine(ok ? "selftest ok" : $"selftest FAILED: {p}");
        return ok ? 0 : 1;
    }
}
