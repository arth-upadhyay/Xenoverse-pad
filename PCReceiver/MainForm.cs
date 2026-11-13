using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PCReceiver;

public sealed class MainForm : Form
{
    private readonly Button _btnStart;
    private readonly Button _btnStop;
    private readonly Label _lblStatus;
    private readonly RichTextBox _txtLog;
    private readonly Panel _topPanel;
    private readonly FlowLayoutPanel _effectPanel;

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private CancellationTokenSource? _receiverCts;
    private Task? _receiverTask;
    private readonly string[] _startupArgs;

    public MainForm(string[] args)
    {
        _startupArgs = args;

        Text = "XenoversePad PC Receiver — Virtual Xbox 360 Controller";
        Size = new Size(820, 600);
        MinimumSize = new Size(680, 480);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 18, 22);
        ForeColor = Color.FromArgb(235, 235, 242);
        Font = new Font("Segoe UI", 10f, FontStyle.Regular);

        // ── Top Control & Status Panel ──────────────────────────────────
        _topPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 200,
            BackColor = Color.FromArgb(24, 24, 30),
            Padding = new Padding(20, 16, 20, 12)
        };

        _lblStatus = new Label
        {
            Text = "Status: Waiting to Start",
            Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 185, 0),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 38
        };

        var buttonContainer = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 84,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0, 8, 0, 8)
        };
        buttonContainer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68f));
        buttonContainer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32f));

        // Large green START RECEIVER button
        _btnStart = new Button
        {
            Text = "▶  START RECEIVER",
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            BackColor = Color.FromArgb(16, 124, 16), // Xbox Standard Green (#107C10)
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 10, 0),
            Cursor = Cursors.Hand
        };
        _btnStart.FlatAppearance.BorderColor = Color.FromArgb(68, 255, 136);
        _btnStart.FlatAppearance.BorderSize = 1;
        _btnStart.Click += OnStartClicked;

        // Red STOP button (disabled by default)
        _btnStop = new Button
        {
            Text = "■  STOP",
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            BackColor = Color.FromArgb(70, 30, 34),
            ForeColor = Color.FromArgb(160, 160, 160),
            FlatStyle = FlatStyle.Flat,
            Dock = DockStyle.Fill,
            Margin = new Padding(10, 0, 0, 0),
            Enabled = false,
            Cursor = Cursors.Hand
        };
        _btnStop.FlatAppearance.BorderColor = Color.FromArgb(120, 40, 45);
        _btnStop.FlatAppearance.BorderSize = 1;
        _btnStop.Click += OnStopClicked;

        buttonContainer.Controls.Add(_btnStart, 0, 0);
        buttonContainer.Controls.Add(_btnStop, 1, 0);

        // Quick Haptic Vocabulary Test Bar (Phase 7 XVE1 Effects 1..4)
        _effectPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0)
        };

        var lblEffects = new Label
        {
            Text = "Test Haptics (XVE1):",
            AutoSize = true,
            ForeColor = Color.FromArgb(170, 170, 185),
            Margin = new Padding(0, 8, 10, 0)
        };
        _effectPanel.Controls.Add(lblEffects);

        _effectPanel.Controls.Add(CreateEffectButton("1: Kamehameha Charge", 1));
        _effectPanel.Controls.Add(CreateEffectButton("2: Kamehameha Release", 2));
        _effectPanel.Controls.Add(CreateEffectButton("3: Heavy Hit", 3));
        _effectPanel.Controls.Add(CreateEffectButton("4: Transform / Awaken", 4));

        _topPanel.Controls.Add(_effectPanel);
        _topPanel.Controls.Add(buttonContainer);
        _topPanel.Controls.Add(_lblStatus);

        // ── Bottom Live Log Console (Read-Only RichTextBox) ─────────────
        var logContainer = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 8, 20, 20),
            BackColor = Color.FromArgb(18, 18, 22)
        };

        _txtLog = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(10, 10, 14),
            ForeColor = Color.FromArgb(180, 245, 190),
            Font = new Font("Consolas", 10f, FontStyle.Regular),
            BorderStyle = BorderStyle.None,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            WordWrap = false
        };

        logContainer.Controls.Add(_txtLog);

        Controls.Add(logContainer);
        Controls.Add(_topPanel);

        // Redirect Console.Out and Console.Error to the RichTextBox log console
        var uiWriter = new UiLogTextWriter(AppendLog);
        Console.SetOut(uiWriter);
        Console.SetError(uiWriter);

        AppendLog("XenoversePad PC Receiver UI ready. Click 'START RECEIVER' to begin.");
    }

    private Button CreateEffectButton(string label, byte effectId)
    {
        var btn = new Button
        {
            Text = label,
            AutoSize = true,
            Height = 32,
            BackColor = Color.FromArgb(38, 38, 48),
            ForeColor = Color.FromArgb(220, 220, 235),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Margin = new Padding(4, 0, 4, 0),
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderColor = Color.FromArgb(75, 75, 92);
        btn.Click += (_, _) =>
        {
            Task.Run(() =>
            {
                Console.WriteLine($"[effect] UI Button {effectId} → triggering XVE1 id={effectId} intensity=255");
                Program.SendEffect(effectId, 255);
            });
        };
        return btn;
    }

    // ── Serialized Start / Stop Lifecycle Handlers ──────────────────────

    private async void OnStartClicked(object? sender, EventArgs e)
    {
        // Prevent rapid multi-clicks from queueing overlapping Start operations
        if (!_lifecycleLock.Wait(0))
        {
            return;
        }

        try
        {
            // Ensure any previous task is completely finished before starting a new session
            if (_receiverTask is not null)
            {
                if (!_receiverTask.IsCompleted)
                {
                    return;
                }
                _receiverTask = null;
            }

            SetButtonsRunningState();
            UpdateStatus("Status: Starting Receiver...", Color.FromArgb(255, 214, 0));

            _receiverCts?.Dispose();
            var cts = new CancellationTokenSource();
            _receiverCts = cts;
            CancellationToken token = cts.Token;

            var task = Task.Run(() => Program.StartReceiverAsync(_startupArgs, UpdateStatus, token));
            _receiverTask = task;

            // Monitor completion asynchronously so if the receiver stops on error, UI resets cleanly
            _ = task.ContinueWith(_ =>
            {
                SafeInvoke(() =>
                {
                    if (_receiverTask == task)
                    {
                        SetButtonsStoppedState();
                        UpdateStatus("Status: Waiting to Start (Stopped)", Color.FromArgb(255, 185, 0));
                    }
                });
            }, TaskScheduler.Default);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async void OnStopClicked(object? sender, EventArgs e)
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            _btnStart.Enabled = false;
            _btnStop.Enabled = false;
            UpdateStatus("Status: Stopping Receiver...", Color.FromArgb(255, 120, 80));

            await StopReceiverInternalAsync();

            SetButtonsStoppedState();
            UpdateStatus("Status: Waiting to Start (Stopped)", Color.FromArgb(255, 185, 0));
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StopReceiverInternalAsync()
    {
        var task = _receiverTask;
        var cts = _receiverCts;

        try
        {
            // 1. Signal cancellation token first
            cts?.Cancel();

            // 2. Wake blocking socket calls (ReceiveFrom / AcceptTcpClient) without touching ViGEm from UI thread
            Program.WakeSocketsForStop();

            // 3. Wait for the background thread to finish its loop and release sockets cleanly
            if (task is not null)
            {
                await Task.WhenAny(task, Task.Delay(2500));
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[warn] Error during stop: {ex.Message}");
        }
        finally
        {
            _receiverTask = null;
            // Do not dispose cts while background sub-tasks might still inspect token; let GC collect it safely
            _receiverCts = null;
        }
    }

    private void SetButtonsRunningState()
    {
        _btnStart.Enabled = false;
        _btnStart.BackColor = Color.FromArgb(30, 65, 30);
        _btnStart.ForeColor = Color.FromArgb(150, 175, 150);

        _btnStop.Enabled = true;
        _btnStop.BackColor = Color.FromArgb(232, 17, 35); // Xbox Red (#E81123)
        _btnStop.ForeColor = Color.White;
    }

    private void SetButtonsStoppedState()
    {
        _btnStart.Enabled = true;
        _btnStart.BackColor = Color.FromArgb(16, 124, 16); // Xbox Green (#107C10)
        _btnStart.ForeColor = Color.White;

        _btnStop.Enabled = false;
        _btnStop.BackColor = Color.FromArgb(70, 30, 34);
        _btnStop.ForeColor = Color.FromArgb(160, 160, 160);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try
        {
            _receiverCts?.Cancel();
            Program.WakeSocketsForStop();
            Program.ShutdownViGEmOnExit();
        }
        catch { }
        base.OnFormClosing(e);
    }

    // ── Thread-Safe UI Status & Log Helpers ─────────────────────────────

    public void UpdateStatus(string statusText, Color color)
    {
        SafeInvoke(() =>
        {
            _lblStatus.Text = statusText;
            _lblStatus.ForeColor = color;
        });
    }

    public void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        string timestamped = $"[{DateTime.Now:HH:mm:ss.fff}] {message.TrimEnd('\r', '\n')}{Environment.NewLine}";

        SafeInvoke(() =>
        {
            if (_txtLog.IsDisposed) return;

            if (_txtLog.Lines.Length > 600)
            {
                int cutIndex = _txtLog.GetFirstCharIndexFromLine(200);
                if (cutIndex > 0)
                {
                    _txtLog.Select(0, cutIndex);
                    _txtLog.ReadOnly = false;
                    _txtLog.SelectedText = string.Empty;
                    _txtLog.ReadOnly = true;
                }
            }

            _txtLog.AppendText(timestamped);
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.ScrollToCaret();
        });
    }

    private void SafeInvoke(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(action);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }
        else
        {
            action();
        }
    }

    private sealed class UiLogTextWriter : TextWriter
    {
        private readonly Action<string> _writeLineCallback;
        private readonly StringBuilder _buffer = new();
        private readonly object _lock = new();

        public UiLogTextWriter(Action<string> writeLineCallback)
        {
            _writeLineCallback = writeLineCallback;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_lock)
            {
                if (value == '\n')
                {
                    string line = _buffer.ToString();
                    _buffer.Clear();
                    _writeLineCallback(line);
                }
                else if (value != '\r')
                {
                    _buffer.Append(value);
                }
            }
        }

        public override void WriteLine(string? value)
        {
            if (value is not null)
            {
                _writeLineCallback(value);
            }
        }
    }
}
