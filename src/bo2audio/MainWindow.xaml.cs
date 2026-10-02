using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static Native;

// BO2 Audio window: waits for the game and lists the output devices to pick from.
// The switch shortcut and its options live on a separate settings view.

public partial class MainWindow : Window
{
    const int HotkeyId = 1;

    class Row
    {
        public Device Device;
        public RadioButton Radio;
        public TextBlock Name, Tag, Check;
    }

    readonly Settings settings = Settings.Load();
    readonly DispatcherTimer pollTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer levelTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    readonly List<Row> rows = [];

    List<Device> devices = [];
    Session session;
    IntPtr hwnd;
    string game;        // process name of the running game, or null
    string problem;     // shown in red until the next successful switch
    bool hotkeyOk;
    bool capturing;     // waiting for a new shortcut
    bool syncing;       // true while code sets the radio buttons

    public MainWindow()
    {
        InitializeComponent();
        pollTimer.Tick += (_, _) => Poll();
        levelTimer.Tick += (_, _) => UpdateLevel();
        PreviewKeyDown += KeysKeyDown;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => session?.Dispose();
    }

    // We create and use Core Audio objects only on thread-pool (MTA) threads. The capture
    // thread shares them, and COM cannot marshal them into the UI's STA.
    static T Mta<T>(Func<T> f) => Task.Run(f).GetAwaiter().GetResult();
    static void Mta(Action a) => Task.Run(a).GetAwaiter().GetResult();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        ShortcutOn.IsChecked = settings.HotkeyEnabled;
        BeepOn.IsChecked = settings.Beep;
        ApplyHotkey();
        try { Mta(MuteState.Recover); } catch { }   // a device left muted by a killed run
        Poll();
        pollTimer.Start();
        levelTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        pollTimer.Stop();
        levelTimer.Stop();
        UnregisterHotKey(hwnd, HotkeyId);
        Detach();
        base.OnClosed(e);
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)WM_HOTKEY && session != null) NextDevice();
        return IntPtr.Zero;
    }

    void Detach()
    {
        if (session == null) return;
        var s = session;
        session = null;
        Mta(s.Dispose);
    }

    // Tracks the game starting and exiting, and output devices being added and removed.
    void Poll()
    {
        try
        {
            var (pid, process) = Games.Find();
            game = process;
            bool changed = rows.Count == 0;
            if (session != null && session.Pid != pid) { Detach(); changed = true; }

            var list = Mta(Audio.RenderDevices);
            changed |= !list.Select(d => d.Id).SequenceEqual(devices.Select(d => d.Id));
            if (session == null && pid != 0)
            {
                session = Mta(() => Audio.SourceDevice(list, pid) is { } source ? new Session(pid, source) : null);
                changed |= session != null;
            }
            if (session?.Target is { } target && list.All(d => d.Id != target.Id))
            {
                Mta(() => session.SwitchTo(null));
                problem = $"{target.Name} was unplugged. Sound is back on {session.Source.Name}.";
            }
            if (changed) { devices = list; BuildRows(); }
        }
        catch (Exception e)
        {
            problem = e.Message;
        }
        UpdateTexts();
    }

    // "Headphones (USB Audio Device)" -> "Headphones", "USB Audio Device"
    static (string Name, string Detail) Split(string name)
    {
        int open = name.IndexOf(" (");
        return open > 0 && name.EndsWith(')') ? (name[..open], name[(open + 2)..^1]) : (name, null);
    }

    static TextBlock Secondary(string text = null)
    {
        var t = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    void BuildRows()
    {
        Rows.Children.Clear();
        CycleRows.Children.Clear();
        rows.Clear();
        foreach (var d in devices)
        {
            bool own = session != null && d.Id == session.Source.Id;
            var (name, detail) = Split(d.Name);

            // Main view: the device as one big radio row.
            var row = new Row
            {
                Device = d,
                Name = new TextBlock { Text = name, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis },
                Tag = Secondary(),
                Check = new TextBlock
                {
                    Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                },
            };
            row.Check.SetResourceReference(TextBlock.ForegroundProperty, "AccentFillColorDefaultBrush");
            row.Tag.Margin = new Thickness(12, 0, 0, 0);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(row.Name);
            if (detail != null) names.Children.Add(Secondary(detail));
            var content = new DockPanel();
            DockPanel.SetDock(row.Check, Dock.Right);
            DockPanel.SetDock(row.Tag, Dock.Right);
            content.Children.Add(row.Check);
            content.Children.Add(row.Tag);
            content.Children.Add(names);
            row.Radio = new RadioButton
            {
                Style = (Style)FindResource("DeviceRow"), GroupName = "output", IsEnabled = session != null, Content = content,
            };
            AutomationProperties.SetName(row.Radio, d.Name);
            row.Radio.Checked += (_, _) => { if (!syncing) Switch(d, beep: false); };
            rows.Add(row);
            Rows.Children.Add(row.Radio);

            // Settings view: whether the shortcut includes the device.
            var inCycle = new CheckBox
            {
                Content = name, IsChecked = own || !settings.SkipInCycle.Contains(d.Id), IsEnabled = !own, ToolTip = d.Name,
            };
            inCycle.Click += (_, _) =>
            {
                settings.SkipInCycle.Remove(d.Id);
                if (inCycle.IsChecked != true) settings.SkipInCycle.Add(d.Id);
                settings.Save();
            };
            var cycleRow = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            if (own)
            {
                var always = Secondary("Chosen in the game, always included");
                DockPanel.SetDock(always, Dock.Right);
                cycleRow.Children.Add(always);
            }
            cycleRow.Children.Add(inCycle);
            CycleRows.Children.Add(cycleRow);
        }
        SyncRows();
    }

    void SyncRows()
    {
        string currentId = session == null ? null : (session.Target ?? session.Source).Id;
        syncing = true;
        foreach (var row in rows)
        {
            bool current = row.Device.Id == currentId, own = session != null && row.Device.Id == session.Source.Id;
            row.Radio.IsChecked = current;
            row.Name.FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal;
            row.Check.Visibility = current ? Visibility.Visible : Visibility.Collapsed;
            row.Tag.Text = own ? (current ? "Chosen in the game" : "Chosen in the game, muted") : current ? "Playing here" : "";
        }
        syncing = false;
    }

    void Switch(Device device, bool beep)
    {
        if (session == null) return;
        try
        {
            Mta(() => session.SwitchTo(device));
            problem = null;
            if (beep) Audio.Beep(device);
        }
        catch (Exception e)
        {
            problem = $"Can't switch to {device.Name}: {e.Message}";
        }
        SyncRows();
        UpdateTexts();
    }

    // Runs when the shortcut is pressed. The cycle is the game's device first, then the ticked ones.
    void NextDevice()
    {
        var cycle = devices.Where(d => d.Id == session.Source.Id || !settings.SkipInCycle.Contains(d.Id))
            .OrderBy(d => d.Id == session.Source.Id ? 0 : 1).ToList();
        if (cycle.Count == 0) return;
        string currentId = (session.Target ?? session.Source).Id;
        Switch(cycle[(cycle.FindIndex(d => d.Id == currentId) + 1) % cycle.Count], beep: settings.Beep);
    }

    void UpdateTexts()
    {
        string dot, hint = null;
        if (session != null)
        {
            dot = "SystemFillColorSuccessBrush";
            Status.Text = $"{Games.Title(game)} is running";
            Heading.Text = "Where should the game's sound play?";
        }
        else if (game != null)
        {
            dot = "SystemFillColorCautionBrush";
            Status.Text = $"{Games.Title(game)} is running";
            Heading.Text = "Waiting for the game to play sound";
            hint = "You can pick a device as soon as the game makes sound.";
        }
        else
        {
            dot = "SystemFillColorNeutralBrush";
            Status.Text = "Black Ops 2 is not running";
            Heading.Text = "Start the game to pick a device";
            hint = "Works with Steam and Plutonium. Keep this window open.";
        }
        Dot.Fill = TryFindResource(dot) as Brush ?? Brushes.Gray;
        Level.Visibility = session != null ? Visibility.Visible : Visibility.Hidden;
        Hint.Text = hint;
        Hint.Visibility = hint != null ? Visibility.Visible : Visibility.Collapsed;
        Problem.Text = problem;
        Problem.Visibility = problem != null ? Visibility.Visible : Visibility.Collapsed;

        string keys = settings.Hotkey.Replace("+", " + ");
        bool taken = settings.HotkeyEnabled && !hotkeyOk;
        ShortcutLine.Text = !settings.HotkeyEnabled ? "Shortcut is off"
            : taken ? $"Shortcut {keys} is used by another app"
            : $"Shortcut for the next device: {keys}";
        KeysButton.Content = capturing ? "Press keys…" : keys;
        KeysButton.IsEnabled = settings.HotkeyEnabled;
        KeysHelp.Text = capturing ? "Esc cancels" : taken ? "Used by another app. Choose different keys." : "Click, then press the new keys";
        if (taken && !capturing) KeysHelp.Foreground = TryFindResource("SystemFillColorCriticalBrush") as Brush ?? Brushes.Firebrick;
        else KeysHelp.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
    }

    void UpdateLevel()
    {
        if (session == null) { Level.Value = 0; return; }
        float peak = session.Forwarder.CapturePeak;
        session.Forwarder.CapturePeak = 0;
        Level.Value = Math.Max(Math.Sqrt(peak), Level.Value * 0.8);   // rises at once, falls slowly
    }

    void OpenSettings(object sender, RoutedEventArgs e) => ShowSettings(true);
    void CloseSettings(object sender, RoutedEventArgs e) => ShowSettings(false);

    void ShowSettings(bool show)
    {
        capturing = false;
        MainView.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        SettingsView.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UpdateTexts();
    }

    void ShortcutOnClick(object sender, RoutedEventArgs e)
    {
        settings.HotkeyEnabled = ShortcutOn.IsChecked == true;
        settings.Save();
        ApplyHotkey();
    }

    void BeepOnClick(object sender, RoutedEventArgs e)
    {
        settings.Beep = BeepOn.IsChecked == true;
        settings.Save();
    }

    void KeysClick(object sender, RoutedEventArgs e)
    {
        capturing = !capturing;
        UpdateTexts();
    }

    void KeysLostFocus(object sender, RoutedEventArgs e)
    {
        capturing = false;
        UpdateTexts();
    }

    void KeysKeyDown(object sender, KeyEventArgs e)
    {
        if (!capturing) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key == Key.F4 && mods == ModifierKeys.Alt) return;
        e.Handled = true;
        if (key == Key.Escape) { capturing = false; UpdateTexts(); return; }
        bool fkey = key is >= Key.F1 and <= Key.F24;
        string name = fkey || key is >= Key.A and <= Key.Z ? key.ToString()
            : key is >= Key.D0 and <= Key.D9 ? ((char)('0' + (key - Key.D0))).ToString() : null;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), alt = mods.HasFlag(ModifierKeys.Alt);
        if (name == null || !fkey && !ctrl && !alt) return;   // modifier alone, or a plain typing key
        capturing = false;
        settings.Hotkey = (ctrl ? "Ctrl+" : "") + (alt ? "Alt+" : "") + (mods.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + name;
        ApplyHotkey();
        if (hotkeyOk) settings.Save();
    }

    // Registers settings.Hotkey, or nothing while the shortcut is switched off.
    void ApplyHotkey()
    {
        UnregisterHotKey(hwnd, HotkeyId);
        hotkeyOk = false;
        if (settings.HotkeyEnabled)
        {
            try
            {
                var (mods, vk) = Hotkey.Parse(settings.Hotkey);
                hotkeyOk = RegisterHotKey(hwnd, HotkeyId, mods, vk);
            }
            catch (ArgumentException) { }
        }
        UpdateTexts();
    }
}
