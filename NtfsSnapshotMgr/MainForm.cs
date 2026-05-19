using NtfsSnapshotMgr.Services;
using NtfsSnapshotMgr.Utils;
using System.Data;
using System.Diagnostics;

namespace NtfsSnapshotMgr;

public partial class MainForm : Form
{
    private readonly VssService _vss = new();

    // ── Controls ─────────────────────────────────────────────────
    private readonly ComboBox   _cmbVolume      = new();
    private readonly Button     _btnRefresh     = new();
    private readonly Button     _btnCreate      = new();
    private readonly Button     _btnDelete      = new();
    private readonly DataGridView _gridSnapshots = new();
    private readonly ContextMenuStrip _ctxSnapshot = new();
    private readonly GroupBox   _grpStorage     = new();
    private readonly Label      _lblMaxSpace    = new();
    private readonly TrackBar   _tbMaxSpace     = new();
    private readonly Label      _lblMaxValue    = new();  // clickable number
    private readonly ComboBox   _cmbUnit        = new();  // MB / GB / TB
    private readonly Label      _lblPercent     = new();  // "(32.5%)"
    private readonly TextBox    _txtMaxValue    = new();  // hidden, for direct numeric input
    private readonly Label      _lblUsedSpace   = new();
    private readonly Label      _lblDiskInfo    = new();  // total / free
    private readonly Button     _btnApplyStorage = new();
    private readonly StatusStrip _statusBar     = new();
    private readonly ToolStripStatusLabel _statusLabel = new();

    // ── Slider state ────────────────────────────────────────────
    private long _committedMaxBytes;          // last committed value (-1 = unlimited)
    private long _currentVolumeTotalBytes;    // total size of selected volume
    private bool _suppressSliderEvents;       // prevent recursive ValueChanged loop

    // ── Device / focus refresh ──────────────────────────────────
    private const int  DEVICE_REFRESH_MIN_MS  = 3000;
    private const int  FOCUS_REFRESH_SECONDS  = 5;
    private const bool AUTO_REFRESH_ON_FOCUS  = true;
#pragma warning disable CS0162
    private DateTime   _lastDeviceRefresh     = DateTime.MinValue;
    private DateTime   _lastLostFocus         = DateTime.MinValue;

    // ── Input safety ────────────────────────────────────────────
    private const double MAX_USER_INPUT = 1_000_000;  // max value in the selected unit

    // Win32 constants for WM_DEVICECHANGE
    private const int WM_DEVICECHANGE         = 0x0219;
    private const int DBT_DEVICEARRIVAL       = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    public MainForm()
    {
        InitializeComponent();
        BuildLayout();
        this.Load       += MainForm_Load;
        this.Activated  += MainForm_Activated;
        this.Deactivate += MainForm_Deactivate;
        Logger.Info("Application started.");
    }

    // ═══════════════════════════════════════════════════════════
    //  Layout
    // ═══════════════════════════════════════════════════════════

    private void BuildLayout()
    {
        // ── Form ──────────────────────────────────────────────
        this.Text           = "NTFS Snapshot Manager";
        this.ClientSize     = new Size(900, 620);
        this.StartPosition  = FormStartPosition.CenterScreen;
        this.MinimumSize    = new Size(700, 480);

        // ── Top toolbar panel ─────────────────────────────────
        var topBar = new Panel
        {
            Dock    = DockStyle.Top,
            Height  = 36,
            Padding = new Padding(8, 4, 8, 4),
        };

        var lblVol = new Label { Text = "卷:", TextAlign = ContentAlignment.MiddleRight, Width = 30, Top = 6 };
        _cmbVolume.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbVolume.Bounds = new Rectangle(40, 5, 100, 22);

        _btnRefresh.Text  = "刷新";
        _btnRefresh.Bounds = new Rectangle(148, 4, 60, 26);

        _btnCreate.Text  = "创建快照";
        _btnCreate.Bounds = new Rectangle(220, 4, 80, 26);

        _btnDelete.Text  = "删除快照";
        _btnDelete.Bounds = new Rectangle(308, 4, 80, 26);

        topBar.Controls.AddRange(new Control[] { lblVol, _cmbVolume, _btnRefresh, _btnCreate, _btnDelete });

        // ── DataGridView (center) ─────────────────────────────
        _gridSnapshots.Dock           = DockStyle.Fill;
        _gridSnapshots.AllowUserToAddRows    = false;
        _gridSnapshots.AllowUserToDeleteRows = false;
        _gridSnapshots.ReadOnly       = true;
        _gridSnapshots.SelectionMode  = DataGridViewSelectionMode.FullRowSelect;
        _gridSnapshots.MultiSelect    = false;
        _gridSnapshots.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _gridSnapshots.RowHeadersVisible = false;
        _gridSnapshots.AllowUserToResizeRows = false;
        _gridSnapshots.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _gridSnapshots.ContextMenuStrip  = _ctxSnapshot;

        // Context menu
        var mnuOpen = new ToolStripMenuItem("在资源管理器中打开");
        mnuOpen.Click += (_, _) => OpenSelectedSnapshot();
        _ctxSnapshot.Items.Add(mnuOpen);

        // ── Right panel: storage settings ─────────────────────
        var rightPanel = new Panel { Dock = DockStyle.Right, Width = 250, Padding = new Padding(8) };

        _grpStorage.Text    = "存储设置";
        _grpStorage.Dock    = DockStyle.Fill;

        _lblMaxSpace.Text   = "最大空间:";
        _lblMaxSpace.Location = new Point(12, 28);
        _lblMaxSpace.AutoSize = true;

        // Slider 0..1000 → 0% .. 100% of total disk space
        _tbMaxSpace.Location      = new Point(12, 50);
        _tbMaxSpace.Width         = 210;
        _tbMaxSpace.Minimum       = 0;
        _tbMaxSpace.Maximum       = 1000;
        _tbMaxSpace.TickFrequency = 100;
        _tbMaxSpace.SmallChange   = 1;    // 0.1% per arrow key
        _tbMaxSpace.LargeChange   = 10;   // 1% per PageUp/PageDown
        _tbMaxSpace.TickStyle     = TickStyle.None;

        // Row below slider: [number] [unit ▼] (percent)
        _lblMaxValue.Text      = "--";
        _lblMaxValue.Location  = new Point(12, 100);
        _lblMaxValue.AutoSize  = false;
        _lblMaxValue.Size      = new Size(58, 18);
        _lblMaxValue.TextAlign = ContentAlignment.MiddleRight;
        _lblMaxValue.Cursor    = Cursors.Hand;
        _lblMaxValue.ForeColor = Color.DarkBlue;
        _lblMaxValue.Font      = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);

        _cmbUnit.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbUnit.Location      = new Point(74, 98);
        _cmbUnit.Width         = 52;
        _cmbUnit.Items.AddRange(["MB", "GB", "TB"]);

        _lblPercent.Text      = "";
        _lblPercent.Location  = new Point(132, 100);
        _lblPercent.AutoSize  = true;
        _lblPercent.ForeColor = Color.DimGray;
        _lblPercent.Font      = new Font("Microsoft YaHei UI", 8.5f);

        // Hidden TextBox for direct editing
        _txtMaxValue.Location  = new Point(12, 100);
        _txtMaxValue.Width     = 58;
        _txtMaxValue.Visible   = false;
        _txtMaxValue.TextAlign = HorizontalAlignment.Right;

        _lblUsedSpace.Text    = "快照占用: --";
        _lblUsedSpace.Location = new Point(12, 128);
        _lblUsedSpace.AutoSize = true;

        _lblDiskInfo.Text      = "磁盘: --";
        _lblDiskInfo.Location  = new Point(12, 148);
        _lblDiskInfo.AutoSize  = true;
        _lblDiskInfo.ForeColor = Color.DimGray;
        _lblDiskInfo.Font      = new Font("Microsoft YaHei UI", 8f);

        _btnApplyStorage.Text     = "应用";
        _btnApplyStorage.Location = new Point(12, 170);
        _btnApplyStorage.Width    = 80;

        var lblHint = new Label
        {
            Text       = "拖动滑条调整上限(% 磁盘总空间)，\n点击蓝色数字修改，下拉选择单位。",
            Location   = new Point(12, 200),
            AutoSize   = true,
            ForeColor  = Color.Gray,
            Font       = new Font("Microsoft YaHei UI", 7.5f),
        };

        _grpStorage.Controls.AddRange(new Control[] {
            _lblMaxSpace, _tbMaxSpace,
            _lblMaxValue, _cmbUnit, _lblPercent, _txtMaxValue,
            _lblUsedSpace, _lblDiskInfo, _btnApplyStorage, lblHint });

        rightPanel.Controls.Add(_grpStorage);

        // ── StatusStrip (bottom) ──────────────────────────────
        _statusBar.Items.Add(_statusLabel);
        _statusLabel.Text = "就绪";

        // ── Assemble ──────────────────────────────────────────
        var centerPanel = new Panel { Dock = DockStyle.Fill };
        centerPanel.Controls.Add(_gridSnapshots);

        this.Controls.Add(centerPanel);
        this.Controls.Add(rightPanel);
        this.Controls.Add(topBar);
        this.Controls.Add(_statusBar);

        // ── Wire events ───────────────────────────────────────
        _btnRefresh.Click      += (_, _) => RefreshAll();
        _btnCreate.Click       += BtnCreate_Click;
        _btnDelete.Click       += BtnDelete_Click;
        _btnApplyStorage.Click += BtnApplyStorage_Click;
        _cmbVolume.SelectedIndexChanged += (_, _) => RefreshAll();

        _tbMaxSpace.ValueChanged += TbMaxSpace_ValueChanged;
        _tbMaxSpace.MouseUp      += TbMaxSpace_MouseUp;
        _lblMaxValue.Click       += LblMaxValue_Click;
        _txtMaxValue.KeyDown     += TxtMaxValue_KeyDown;
        _txtMaxValue.Leave       += TxtMaxValue_Leave;
        _cmbUnit.SelectedIndexChanged += CmbUnit_SelectedIndexChanged;

        _gridSnapshots.CellMouseClick  += Grid_CellMouseClick;
        _gridSnapshots.CellDoubleClick += (_, _) => OpenSelectedSnapshot();
        _gridSnapshots.KeyDown         += Grid_KeyDown;
    }

    // ═══════════════════════════════════════════════════════════
    //  Events
    // ═══════════════════════════════════════════════════════════

    private void MainForm_Load(object? sender, EventArgs e)
    {
        try
        {
            var volumes = _vss.GetLocalNtfsVolumes();
            _cmbVolume.DataSource = volumes;
            _cmbVolume.DisplayMember = nameof(VolumeInfo.DriveLetter);

            if (_cmbVolume.Items.Count > 0)
                _cmbVolume.SelectedIndex = 0;
            else
                _statusLabel.Text = "未找到本地 NTFS 卷。";

            Logger.Info($"Loaded {volumes.Count} NTFS volume(s).");
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"加载卷列表失败: {ex.Message}";
            Logger.Error("Failed to load volumes", ex);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Device change monitoring (WM_DEVICECHANGE)
    // ═══════════════════════════════════════════════════════════

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DEVICECHANGE)
        {
            int wParam = m.WParam.ToInt32();
            if (wParam == DBT_DEVICEARRIVAL || wParam == DBT_DEVICEREMOVECOMPLETE)
            {
                var now = DateTime.Now;
                if ((now - _lastDeviceRefresh).TotalMilliseconds >= DEVICE_REFRESH_MIN_MS)
                {
                    _lastDeviceRefresh = now;
                    var action = wParam == DBT_DEVICEARRIVAL ? "arrival" : "removal";
                    Logger.Info($"Device {action} detected, scheduling refresh.");

                    // Defer to UI thread (WndProc is already UI thread, but invoke for safety)
                    BeginInvoke(() => RefreshVolumesAndSnapshots());
                }
            }
        }
        base.WndProc(ref m);
    }

    private void RefreshVolumesAndSnapshots()
    {
        if (this.IsDisposed || this.Disposing) return;

        try
        {
            var volumes = _vss.GetLocalNtfsVolumes();
            var previous = _cmbVolume.SelectedItem as VolumeInfo;
            var previousLetter = previous?.DriveLetter;

            _cmbVolume.DataSource = volumes;
            _cmbVolume.DisplayMember = nameof(VolumeInfo.DriveLetter);

            // Try to re-select the same volume
            if (previousLetter != null)
            {
                for (int i = 0; i < _cmbVolume.Items.Count; i++)
                {
                    if (_cmbVolume.Items[i] is VolumeInfo vi &&
                        vi.DriveLetter == previousLetter)
                    {
                        _cmbVolume.SelectedIndex = i;
                        return;
                    }
                }
            }

            if (_cmbVolume.Items.Count > 0)
                _cmbVolume.SelectedIndex = 0;

            Logger.Info($"Device refresh: {volumes.Count} volume(s) found.");
        }
        catch (Exception ex)
        {
            Logger.Error("Device refresh failed", ex);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Focus-based auto-refresh
    // ═══════════════════════════════════════════════════════════

    private void MainForm_Deactivate(object? sender, EventArgs e)
    {
        _lastLostFocus = DateTime.Now;
    }

    private void MainForm_Activated(object? sender, EventArgs e)
    {
        if (!AUTO_REFRESH_ON_FOCUS) return;
#pragma warning restore CS0162

        var elapsed = DateTime.Now - _lastLostFocus;
        if (_lastLostFocus != DateTime.MinValue && elapsed.TotalSeconds >= FOCUS_REFRESH_SECONDS)
        {
            Logger.Info($"Focus returned after {elapsed.TotalSeconds:F0}s, auto-refreshing.");
            RefreshAll();
        }
    }

    private void BtnCreate_Click(object? sender, EventArgs e)
    {
        if (_cmbVolume.SelectedItem is not VolumeInfo vol) return;

        try
        {
            _statusLabel.Text = $"正在 {vol.DriveLetter} 上创建快照...";
            _vss.CreateSnapshot(vol.DriveLetter + "\\");
            _statusLabel.Text = $"已在 {vol.DriveLetter} 上创建快照。";
            Logger.Info($"Snapshot created on {vol.DriveLetter}.");
            RefreshAll();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"创建快照失败: {ex.Message}";
            Logger.Error($"Failed to create snapshot on {vol.DriveLetter}", ex);
        }
    }

    private void BtnDelete_Click(object? sender, EventArgs e)
    {
        if (_gridSnapshots.SelectedRows.Count == 0) return;

        var row  = _gridSnapshots.SelectedRows[0];
        var id   = row.Cells["colId"].Value?.ToString();
        var time = row.Cells["colTime"].Value?.ToString();

        if (id is null) return;

        var result = MessageBox.Show(
            $"确定要删除此快照吗？\n\n时间: {time}\nID: {id}",
            "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (result != DialogResult.Yes) return;

        try
        {
            _vss.DeleteSnapshot(id);
            _statusLabel.Text = $"已删除快照 {id}。";
            Logger.Info($"Snapshot deleted: {id}");
            RefreshAll();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"删除快照失败: {ex.Message}";
            Logger.Error($"Failed to delete snapshot {id}", ex);
        }
    }

    private void BtnApplyStorage_Click(object? sender, EventArgs e)
    {
        if (_cmbVolume.SelectedItem is not VolumeInfo vol) return;

        // Commit whatever value is currently shown on the slider
        CommitMaxStorage(vol);
    }

    // ═══════════════════════════════════════════════════════════
    //  Snapshot explorer open
    // ═══════════════════════════════════════════════════════════

    private void Grid_CellMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
        {
            _gridSnapshots.ClearSelection();
            _gridSnapshots.Rows[e.RowIndex].Selected = true;
        }
    }

    private void Grid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            OpenSelectedSnapshot();
        }
    }

    private void OpenSelectedSnapshot()
    {
        if (_gridSnapshots.SelectedRows.Count == 0) return;
        if (_cmbVolume.SelectedItem is not VolumeInfo vol) return;

        var row = _gridSnapshots.SelectedRows[0];
        var snapshotId = row.Cells["colId"].Value?.ToString();
        if (snapshotId is null) return;

        // Find the snapshot's InstallDate
        var snaps = _vss.GetSnapshots(vol.DriveLetter + "\\");
        var snap = snaps.FirstOrDefault(s => s.ID == snapshotId);
        if (snap is null || snap.InstallDate == DateTime.MinValue) return;

        // Build path: \\localhost\E$\@GMT-2026.05.19-09.07.23\
        var drive = vol.DriveLetter.TrimEnd(':');
        // @GMT- prefix expects UTC.
        // ManagementDateTimeConverter may return Local/Unspecified — force to local, then to UTC.
        var installDate = snap.InstallDate;
        if (installDate.Kind == DateTimeKind.Unspecified)
            installDate = DateTime.SpecifyKind(installDate, DateTimeKind.Local);
        var gmt = installDate.ToUniversalTime().ToString("yyyy.MM.dd-HH.mm.ss");
        var path = $"\\\\localhost\\{drive}$\\@GMT-{gmt}\\";

        try
        {
            Process.Start("explorer.exe", path);
            _statusLabel.Text = $"已打开快照: {path}";
            Logger.Info($"Opened snapshot in explorer: {path}");
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"打开失败: {ex.Message}";
            Logger.Error($"Failed to open snapshot {snapshotId}", ex);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Data refresh
    // ═══════════════════════════════════════════════════════════

    private void RefreshAll()
    {
        if (_cmbVolume.SelectedItem is not VolumeInfo vol) return;

        try
        {
            RefreshSnapshots(vol);
            RefreshStorage(vol);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"刷新失败: {ex.Message}";
        }
    }

    private void RefreshSnapshots(VolumeInfo vol)
    {
        var snaps = _vss.GetSnapshots(vol.DriveLetter + "\\");
        var storage = _vss.GetSnapshotStorage(vol.DriveLetter + "\\");

        _gridSnapshots.DataSource = null;
        _gridSnapshots.Columns.Clear();

        // Manually populate so we can include storage info
        var dt = new DataTable();
        dt.Columns.Add("colVolume", typeof(string));
        dt.Columns.Add("colTime",   typeof(string));
        dt.Columns.Add("colId",     typeof(string));
        dt.Columns.Add("colSpace",  typeof(string));

        string spaceTotal = storage is not null
            ? BytesFormatter.Format(storage.UsedSpace)
            : "--";

        foreach (var s in snaps)
        {
            dt.Rows.Add(
                vol.DriveLetter,
                s.InstallDate == DateTime.MinValue ? "N/A" : s.InstallDate.ToString("yyyy-MM-dd HH:mm:ss"),
                s.ID,
                spaceTotal
            );
        }

        _gridSnapshots.DataSource = dt;

        // Column headers
        _gridSnapshots.Columns["colVolume"].HeaderText = "卷";
        _gridSnapshots.Columns["colTime"].HeaderText   = "创建时间";
        _gridSnapshots.Columns["colId"].HeaderText     = "快照 ID";
        _gridSnapshots.Columns["colSpace"].HeaderText  = "已用空间 (总)";

        // Adjust column widths
        _gridSnapshots.Columns["colVolume"].FillWeight = 10;
        _gridSnapshots.Columns["colTime"].FillWeight   = 30;
        _gridSnapshots.Columns["colId"].FillWeight     = 40;
        _gridSnapshots.Columns["colSpace"].FillWeight  = 20;
    }

    private void RefreshStorage(VolumeInfo vol)
    {
        _currentVolumeTotalBytes = vol.TotalSize > long.MaxValue
            ? long.MaxValue
            : (long)vol.TotalSize;

        _lblDiskInfo.Text = $"磁盘: {BytesFormatter.Format(_currentVolumeTotalBytes)}  │  剩余: {BytesFormatter.Format((long)vol.FreeSpace)}";

        var storage = _vss.GetSnapshotStorage(vol.DriveLetter + "\\");

        if (storage is null)
        {
            _lblUsedSpace.Text    = "快照占用: 未配置";
            _lblMaxValue.Text     = "--";
            _lblPercent.Text      = "";
            _cmbUnit.SelectedIndex = -1;
            _committedMaxBytes    = 0;
            _tbMaxSpace.Value     = 0;
            _tbMaxSpace.Enabled   = false;
            return;
        }

        _tbMaxSpace.Enabled = true;
        _lblUsedSpace.Text  = $"快照占用: {BytesFormatter.Format(storage.UsedSpace)}";
        _committedMaxBytes  = storage.MaxSpace;

        // Update slider from committed value
        UpdateSliderFromBytes(storage.MaxSpace);
    }

    // ═══════════════════════════════════════════════════════════
    //  Slider & label editing
    // ═══════════════════════════════════════════════════════════

    /// <summary>Slider dragging: preview only, no commit.</summary>
    private void TbMaxSpace_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressSliderEvents) return;
        var (number, unit) = SliderToNumberAndUnit(_tbMaxSpace.Value);
        _lblMaxValue.Text = number;
        AutoSelectUnit(unit);
        UpdatePercentLabel(_tbMaxSpace.Value);
    }

    /// <summary>Slider released → commit.</summary>
    private void TbMaxSpace_MouseUp(object? sender, MouseEventArgs e)
    {
        _committedMaxBytes = SliderToBytes(_tbMaxSpace.Value);
        UpdateSliderFromBytes(_committedMaxBytes);

        if (_cmbVolume.SelectedItem is VolumeInfo vol)
            CommitMaxStorage(vol);
    }

    /// <summary>Unit dropdown changed → recalculate slider from current displayed value.</summary>
    private void CmbUnit_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_suppressSliderEvents) return;
        if (!double.TryParse(_lblMaxValue.Text, out double number)) return;
        if (!TrySafeMultiply(number, out long newBytes)) return;

        newBytes = Math.Clamp(newBytes, 0, _currentVolumeTotalBytes);

        _committedMaxBytes = newBytes;
        UpdateSliderFromBytes(newBytes);

        if (_cmbVolume.SelectedItem is VolumeInfo vol)
            CommitMaxStorage(vol);
    }

    /// <summary>Click the value label → switch to edit mode (number only).</summary>
    private void LblMaxValue_Click(object? sender, EventArgs e)
    {
        _txtMaxValue.Text = _lblMaxValue.Text;
        _txtMaxValue.Tag  = _txtMaxValue.Text;   // remember for revert

        _lblMaxValue.Visible = false;
        _txtMaxValue.Visible = true;
        _txtMaxValue.Focus();
        _txtMaxValue.SelectAll();
    }

    /// <summary>TextBox key handling: Enter → commit with current unit, Esc → revert.</summary>
    private void TxtMaxValue_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;

            if (double.TryParse(_txtMaxValue.Text.Trim(), out double number) && number >= 0)
            {
                if (!TrySafeMultiply(number, out long newBytes))
                {
                    EndLabelEdit();
                    return;
                }

                newBytes = Math.Clamp(newBytes, 0, _currentVolumeTotalBytes);

                _committedMaxBytes = newBytes;
                UpdateSliderFromBytes(newBytes);

                if (_cmbVolume.SelectedItem is VolumeInfo vol)
                    CommitMaxStorage(vol);
            }
            else
            {
                _statusLabel.Text = "请输入有效的非负数。";
            }

            EndLabelEdit();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            EndLabelEdit();
        }
    }

    /// <summary>TextBox loses focus without Enter → revert.</summary>
    private void TxtMaxValue_Leave(object? sender, EventArgs e)
    {
        EndLabelEdit();
    }

    private void EndLabelEdit()
    {
        _txtMaxValue.Visible = false;
        _lblMaxValue.Visible = true;
        this.ActiveControl = null;
    }

    private void CommitMaxStorage(VolumeInfo vol)
    {
        try
        {
            _vss.SetMaxStorage(vol.DriveLetter + "\\", _committedMaxBytes);
            _statusLabel.Text = _committedMaxBytes == -1
                ? $"已将 {vol.DriveLetter} 的存储上限设为无限制。"
                : $"已将 {vol.DriveLetter} 的存储上限设为 {BytesFormatter.Format(_committedMaxBytes)}。";
            Logger.Info($"Storage max set on {vol.DriveLetter}: {BytesFormatter.Format(_committedMaxBytes)}");
            RefreshAll();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"设置存储上限失败: {ex.Message}";
            Logger.Error($"Failed to set storage max on {vol.DriveLetter}", ex);
        }
    }

    // ── Slider ↔ display helpers ───────────────────────────────

    private const long MIN_STORAGE_BYTES = 320L * 1024 * 1024;

    private void UpdateSliderFromBytes(long maxBytes)
    {
        _suppressSliderEvents = true;

        int pos;
        if (maxBytes == -1 || maxBytes >= _currentVolumeTotalBytes)
        {
            pos = 1000;
        }
        else if (_currentVolumeTotalBytes <= MIN_STORAGE_BYTES)
        {
            pos = 0;
        }
        else
        {
            long usableRange = _currentVolumeTotalBytes - MIN_STORAGE_BYTES;
            long clamped = Math.Max(MIN_STORAGE_BYTES, maxBytes);
            double pct = (double)(clamped - MIN_STORAGE_BYTES) / usableRange;
            pos = (int)Math.Round(pct * 1000);
            pos = Math.Clamp(pos, 0, 999);
        }

        _tbMaxSpace.Value = pos;
        _suppressSliderEvents = false;

        var (number, unit) = SliderToNumberAndUnit(pos);
        _lblMaxValue.Text = number;
        AutoSelectUnit(unit);
        UpdatePercentLabel(pos);
    }

    /// <summary>Slider position → (numberString, unitKey).</summary>
    private (string number, string unit) SliderToNumberAndUnit(int sliderPos)
    {
        if (_currentVolumeTotalBytes <= 0)
            return ("--", "GB");

        if (sliderPos >= 1000)
            return ("无限制", "");

        long bytes = SliderToBytes(sliderPos);

        // Auto-pick best unit
        if (bytes >= 1_099_511_627_776)       // >= 1 TB
            return ($"{bytes / 1_099_511_627_776.0:F2}", "TB");
        else if (bytes >= 1_073_741_824)       // >= 1 GB
            return ($"{bytes / 1_073_741_824.0:F2}", "GB");
        else
            return ($"{bytes / 1_048_576.0:F0}", "MB");
    }

    /// <summary>Set combobox to the given unit without triggering events.</summary>
    private void AutoSelectUnit(string unit)
    {
        _suppressSliderEvents = true;
        _cmbUnit.SelectedItem = unit;
        _suppressSliderEvents = false;
    }

    /// <summary>Returns the byte multiplier for the currently selected unit.</summary>
    private long GetUnitMultiplier()
    {
        return _cmbUnit.SelectedItem?.ToString() switch
        {
            "TB" => 1_099_511_627_776L,
            "GB" => 1_073_741_824L,
            _    => 1_048_576L,   // MB default
        };
    }

    /// <summary>
    /// Safely multiply user input by the current unit multiplier.
    /// Returns false if the value overflows or exceeds MAX_USER_INPUT.
    /// </summary>
    private bool TrySafeMultiply(double number, out long resultBytes)
    {
        resultBytes = 0;

        if (number > MAX_USER_INPUT)
        {
            _statusLabel.Text = $"数值过大，最大允许 {MAX_USER_INPUT:N0}（当前单位）。";
            return false;
        }

        long multiplier = GetUnitMultiplier();
        try
        {
            resultBytes = checked((long)(number * multiplier));
            if (resultBytes < 0) resultBytes = 0;  // negative overflow → treat as 0
            return true;
        }
        catch (OverflowException)
        {
            _statusLabel.Text = "数值溢出，请输入更小的值。";
            return false;
        }
    }

    /// <summary>Update the percentage label next to the number.</summary>
    private void UpdatePercentLabel(int sliderPos)
    {
        if (_currentVolumeTotalBytes <= 0)
        {
            _lblPercent.Text = "";
            return;
        }
        if (sliderPos >= 1000)
        {
            _lblPercent.Text = "(100%)";
            return;
        }
        long bytes = SliderToBytes(sliderPos);
        double pct = (double)bytes / _currentVolumeTotalBytes * 100.0;
        _lblPercent.Text = $"({pct:F1}%)";
    }

    private string SliderToLabel(int sliderPos)
    {
        var (number, unit) = SliderToNumberAndUnit(sliderPos);
        return unit.Length > 0 ? $"{number} {unit}" : number;
    }

    /// <summary>
    /// Map slider position (0..1000) to bytes.
    /// 0 → 320 MB (minimum), 1000 → unlimited (-1).
    /// </summary>
    private long SliderToBytes(int sliderPos)
    {
        if (sliderPos >= 1000 || _currentVolumeTotalBytes <= 0)
            return -1;

        double safeTotal = Math.Min(_currentVolumeTotalBytes, long.MaxValue / 2.0);
        long usableRange = (long)safeTotal - MIN_STORAGE_BYTES;

        if (usableRange <= 0)
            return -1;

        double pct = sliderPos / 1000.0;
        long bytes = MIN_STORAGE_BYTES + (long)(pct * usableRange);

        if (bytes < MIN_STORAGE_BYTES) bytes = MIN_STORAGE_BYTES;
        if (bytes >= (long)safeTotal) return -1;

        return bytes;
    }
}
