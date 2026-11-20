using System.Diagnostics;
using System.Text;
using Streamy.Core.Adb;
using Streamy.Services;

namespace Streamy.UI;

/// <summary>
/// For developers: the phone's log (logcat) as it happens, with a level and text filter, pause and save; and a
/// shell on the phone that keeps its state (current folder, variables) between commands.
/// </summary>
internal sealed class ConsoleWindow : Form
{
    private const int MaxLines = 50_000;
    private static readonly Font Mono = new("Cascadia Mono", 9f);

    private readonly string _serial;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };

    // Logcat
    private readonly ListView _log = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        VirtualMode = true,
        FullRowSelect = true,
        HideSelection = false,
        BorderStyle = BorderStyle.None,
        Font = Mono,
    };
    private readonly TextBox _filter = new() { Width = 260, Margin = new Padding(3, 5, 3, 3), PlaceholderText = "Filter by tag, text or process id" };
    private readonly ComboBox _level = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Margin = new Padding(3, 5, 3, 3) };
    private readonly Button _pause = new() { Text = "Pause", AutoSize = true };
    private readonly Label _logStatus = new() { AutoSize = true, Margin = new Padding(8, 9, 3, 3), ForeColor = SystemColors.GrayText };
    private readonly List<LogcatLine> _lines = [];
    private readonly List<int> _shown = [];
    private readonly Queue<LogcatLine> _incoming = new();
    private readonly System.Windows.Forms.Timer _flush = new() { Interval = 200 };
    private Process? _logcat;
    private bool _paused;

    // Shell
    private readonly RichTextBox _output = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, Font = Mono, BackColor = Color.FromArgb(0x1E, 0x1E, 0x1E), ForeColor = Color.FromArgb(0xDD, 0xDD, 0xDD), WordWrap = false };
    private readonly TextBox _input = new() { Dock = DockStyle.Bottom, Font = Mono, BorderStyle = BorderStyle.FixedSingle };
    private readonly List<string> _history = [];
    private int _historyAt;
    private Process? _shell;
    private readonly StringBuilder _pendingOutput = new();

    public ConsoleWindow(string serial, string deviceName)
    {
        _serial = serial;
        Text = $"{deviceName} log and shell - Streamy";
        Icon = AppIcon.Load();
        ClientSize = new Size(1100, 640);
        MinimumSize = new Size(640, 360);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLogTab();
        BuildShellTab();
        Controls.Add(_tabs);
        Shown += (_, _) => { StartLogcat(); StartShell(); };
        _flush.Tick += (_, _) => Flush();
        _flush.Start();
    }

    // ---- Logcat -------------------------------------------------------------------------------------------

    private void BuildLogTab()
    {
        var page = new TabPage("Log");
        _log.Columns.Add("Time", 150);
        _log.Columns.Add("PID", 60, HorizontalAlignment.Right);
        _log.Columns.Add("", 26);
        _log.Columns.Add("Tag", 180);
        _log.Columns.Add("Message", 900);
        _log.RetrieveVirtualItem += (_, e) =>
        {
            var l = _lines[_shown[e.ItemIndex]];
            var item = new ListViewItem(l.Time) { ForeColor = ColorOf(l.Level) };
            item.SubItems.Add(l.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            item.SubItems.Add(l.Level.ToString());
            item.SubItems.Add(l.Tag);
            item.SubItems.Add(l.Message);
            e.Item = item;
        };
        _log.KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.C) { CopySelected(); e.Handled = true; } };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy", null, (_, _) => CopySelected());
        menu.Items.Add("Show only this tag", null, (_, _) => { if (SelectedLines().FirstOrDefault() is { } l) _filter.Text = l.Tag; });
        menu.Items.Add("Show only this process", null, (_, _) => { if (SelectedLines().FirstOrDefault() is { } l) _filter.Text = l.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture); });
        _log.ContextMenuStrip = menu;

        _level.Items.AddRange(["Verbose", "Debug", "Info", "Warning", "Error"]);
        _level.SelectedIndex = 0;
        _level.SelectedIndexChanged += (_, _) => Refilter();
        _filter.TextChanged += (_, _) => Refilter();
        Glyph.Apply(_pause, Glyph.Pause);
        _pause.Click += (_, _) =>
        {
            _paused = !_paused;
            _pause.Text = _paused ? "Resume" : "Pause";
            Glyph.Apply(_pause, _paused ? Glyph.Play : Glyph.Pause);
        };
        var clear = new Button { Text = "Clear", AutoSize = true };
        Glyph.Apply(clear, Glyph.Erase);
        clear.Click += (_, _) =>
        {
            lock (_incoming) _incoming.Clear();
            _lines.Clear();
            Refilter();
        };
        var save = new Button { Text = "Save", AutoSize = true };
        Glyph.Apply(save, Glyph.Save);
        save.Click += (_, _) => SaveLog();

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = false };
        bar.Controls.AddRange([_filter, _level, _pause, clear, save, _logStatus]);
        page.Controls.Add(_log);
        page.Controls.Add(bar);
        _tabs.TabPages.Add(page);
    }

    private static Color ColorOf(char level) => level switch
    {
        'E' or 'F' or 'A' => Color.FromArgb(0xD0, 0x30, 0x30),
        'W' => Color.FromArgb(0xB0, 0x70, 0x00),
        'I' => SystemColors.WindowText,
        'D' => Color.FromArgb(0x30, 0x60, 0xA0),
        _ => SystemColors.GrayText,
    };

    private void StartLogcat()
    {
        _logcat = AdbClient.Default.Start(LogcatLine.Command(5000), _serial);
        _logcat.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null || LogcatLine.Parse(e.Data) is not { } line) return;
            lock (_incoming) _incoming.Enqueue(line);
        };
        _logcat.ErrorDataReceived += (_, _) => { };
        _logcat.Exited += (_, _) => BeginInvokeSafe(() => _logStatus.Text = "The log stopped (the phone disconnected?)");
        _logcat.BeginOutputReadLine();
        _logcat.BeginErrorReadLine();
    }

    /// <summary>Moves new lines into the list five times a second; keeps following the end unless scrolled up.</summary>
    private void Flush()
    {
        if (_paused) { UpdateLogStatus(); FlushShell(); return; }
        List<LogcatLine> batch;
        lock (_incoming)
        {
            if (_incoming.Count == 0) { FlushShell(); return; }
            batch = [.. _incoming];
            _incoming.Clear();
        }
        bool atEnd = _shown.Count == 0 || IsAtEnd();
        int dropped = Math.Max(0, _lines.Count + batch.Count - MaxLines);
        if (dropped > 0)
        {
            _lines.RemoveRange(0, Math.Min(dropped, _lines.Count));
            _lines.AddRange(batch);
            Refilter();
        }
        else
        {
            int start = _lines.Count;
            _lines.AddRange(batch);
            int min = _level.SelectedIndex, i = start;
            var filter = _filter.Text.Trim();
            foreach (var l in batch)
            {
                if (l.Severity >= min && l.Matches(filter)) _shown.Add(i);
                i++;
            }
            _log.VirtualListSize = _shown.Count;
        }
        if (atEnd && _shown.Count > 0) _log.EnsureVisible(_shown.Count - 1);
        UpdateLogStatus();
        FlushShell();
    }

    private bool IsAtEnd()
    {
        if (_log.VirtualListSize == 0) return true;
        var top = _log.TopItem;
        if (top is null) return true;
        int perPage = Math.Max(1, _log.ClientSize.Height / Math.Max(1, _log.GetItemRect(0).Height));
        return top.Index + perPage >= _log.VirtualListSize - 2;
    }

    private void Refilter()
    {
        _shown.Clear();
        int min = _level.SelectedIndex;
        var filter = _filter.Text.Trim();
        for (int i = 0; i < _lines.Count; i++)
            if (_lines[i].Severity >= min && _lines[i].Matches(filter)) _shown.Add(i);
        _log.VirtualListSize = _shown.Count;
        _log.Invalidate();
        if (_shown.Count > 0) _log.EnsureVisible(_shown.Count - 1);
        UpdateLogStatus();
    }

    private void UpdateLogStatus() =>
        _logStatus.Text = $"{_shown.Count:N0} of {_lines.Count:N0} lines" + (_paused ? ", paused" : "");

    private IEnumerable<LogcatLine> SelectedLines() =>
        _log.SelectedIndices.Cast<int>().OrderBy(i => i).Select(i => _lines[_shown[i]]);

    private void CopySelected()
    {
        var text = string.Join(Environment.NewLine, SelectedLines());
        if (text.Length > 0) Clipboard.SetText(text);
    }

    private void SaveLog()
    {
        using var dlg = new SaveFileDialog { Filter = "Text|*.txt", FileName = $"logcat {DateTime.Now:yyyy-MM-dd HH-mm-ss}.txt" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllLines(dlg.FileName, _shown.Select(i => _lines[i].ToString()));
        _logStatus.Text = $"Saved {_shown.Count:N0} lines";
    }

    // ---- Shell --------------------------------------------------------------------------------------------

    private void BuildShellTab()
    {
        var page = new TabPage("Shell");
        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Padding = new Padding(6, 4, 0, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Commands run on the phone as the shell user. Up and Down go through earlier commands. Programs that wait for input or need a terminal (top, vi) are not supported.",
        };
        var restart = new Button { Text = "Restart shell", AutoSize = true, Dock = DockStyle.Right };
        Glyph.Apply(restart, Glyph.Refresh);
        restart.Click += (_, _) => { StopShell(); Append("\n[shell restarted]\n"); StartShell(); };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = _input.PreferredHeight + 8, Padding = new Padding(4) };
        bottom.Controls.Add(_input);
        bottom.Controls.Add(restart);
        _input.Dock = DockStyle.Fill;
        _input.KeyDown += OnInputKey;
        page.Controls.Add(_output);
        page.Controls.Add(bottom);
        page.Controls.Add(hint);
        _tabs.TabPages.Add(page);
        _tabs.SelectedIndexChanged += (_, _) => { if (_tabs.SelectedIndex == 1) _input.Focus(); };
    }

    private void StartShell()
    {
        // -T: no terminal on the phone; the shell reads commands from the pipe and keeps its state between them.
        _shell = AdbClient.Default.StartInteractive(["shell", "-T"], _serial);
        _shell.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_pendingOutput) _pendingOutput.Append(e.Data).Append('\n'); };
        _shell.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_pendingOutput) _pendingOutput.Append(e.Data).Append('\n'); };
        _shell.Exited += (_, _) => BeginInvokeSafe(() => Append("[the shell ended]\n"));
        _shell.BeginOutputReadLine();
        _shell.BeginErrorReadLine();
    }

    private void StopShell()
    {
        try { if (_shell is { HasExited: false }) _shell.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        _shell?.Dispose();
        _shell = null;
    }

    private void OnInputKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            var cmd = _input.Text;
            _input.Clear();
            if (cmd.Trim().Length > 0 && (_history.Count == 0 || _history[^1] != cmd)) _history.Add(cmd);
            _historyAt = _history.Count;
            Append("$ " + cmd + "\n");
            if (_shell is null or { HasExited: true }) StartShell();
            try
            {
                _shell!.StandardInput.WriteLine(cmd);
                _shell.StandardInput.Flush();
            }
            catch (IOException ex)
            {
                Append($"[could not send: {ex.Message}]\n");
            }
        }
        else if (e.KeyCode == Keys.Up && _history.Count > 0)
        {
            _historyAt = Math.Max(0, _historyAt - 1);
            _input.Text = _history[_historyAt];
            _input.SelectionStart = _input.TextLength;
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Down && _history.Count > 0)
        {
            _historyAt = Math.Min(_history.Count, _historyAt + 1);
            _input.Text = _historyAt < _history.Count ? _history[_historyAt] : "";
            _input.SelectionStart = _input.TextLength;
            e.Handled = true;
        }
    }

    private void FlushShell()
    {
        string text;
        lock (_pendingOutput)
        {
            if (_pendingOutput.Length == 0) return;
            text = _pendingOutput.ToString();
            _pendingOutput.Clear();
        }
        Append(text);
    }

    private void Append(string text)
    {
        // Keep the box from growing without end.
        if (_output.TextLength > 2_000_000) _output.Text = _output.Text[^1_000_000..];
        _output.AppendText(text);
        _output.ScrollToCaret();
    }

    private void BeginInvokeSafe(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    /// <summary>Text the shell printed so far (for the developer switch).</summary>
    public string ShellText => _output.Text;

    public int LogLines => _lines.Count;

    /// <summary>Runs a command as if typed (for the developer switch).</summary>
    public void Type(string command)
    {
        _input.Text = command;
        OnInputKey(_input, new KeyEventArgs(Keys.Enter));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _flush.Dispose();
        try { if (_logcat is { HasExited: false }) _logcat.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        _logcat?.Dispose();
        StopShell();
        base.OnFormClosed(e);
    }
}
