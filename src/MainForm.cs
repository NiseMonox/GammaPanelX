using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GammaPanelX
{
    public class MainForm : Form
    {
        // ---------- 状态 ----------
        private List<DisplayMonitor> _monitors = new List<DisplayMonitor>();
        private AppSettings _settings;
        private bool _loadingUi;          // 抑制程序写 UI 时触发提交
        private bool _exitRequested;
        private bool _restoreNeutralOnExit;
        private int _channelIdx;          // 0=R 1=G 2=B（rdoAll 模式下忽略）
        private readonly List<Profile> _hotkeyOrder = new List<Profile>();
        private static readonly uint ShowWindowMsg = NativeMethods.RegisterWindowMessage("GammaPanelX_ShowWindow");
        private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string RunKeyName = "GammaPanelX";

        // ---------- 控件 ----------
        private ListBox lstMonitors;
        private Button btnIdentify, btnRefresh;
        private CheckBox chkApplyAll, chkKeepApplied, chkTrayOnClose, chkAutoStart;

        private GroupBox grpGamma;
        private RadioButton rdoAll, rdoR, rdoG, rdoB;
        private TrackBar tbBright, tbContrast, tbGamma;
        private NumericUpDown numBright, numContrast, numGamma;
        private Button btnResetMonitor, btnResetAll;
        private PictureBox picCurve;
        private Label lblGammaStatus;
        private LinkLabel lnkUnlock;

        private GroupBox grpDdc;
        private Label lblDdcStatus;
        private TrackBar tbDdcBright, tbDdcContrast, tbDdcSat;
        private Label lblDdcBrightVal, lblDdcContrastVal, lblDdcSatVal;
        private Button btnDdcRefresh;

        private GroupBox grpProfiles;
        private ComboBox cboProfiles;
        private Button btnApplyProfile, btnSaveProfile, btnSaveAsProfile, btnDeleteProfile, btnClearHotkey;
        private TextBox txtHotkey;

        private StatusStrip statusStrip;
        private ToolStripStatusLabel lblStatus;

        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;

        private System.Windows.Forms.Timer _saveTimer;          // 设置延迟保存
        private System.Windows.Forms.Timer _keepTimer;          // 定时重新应用
        private System.Windows.Forms.Timer _ddcSetTimer;        // DDC 写入去抖
        private System.Windows.Forms.Timer _displayChangeTimer; // 显示器变更去抖

        // 待发送的 DDC 写入及其目标显示器 (滑块拖动时捕获, 避免去抖期间切换选中项导致写错屏)
        private readonly Dictionary<byte, uint> _pendingDdcWrites = new Dictionary<byte, uint>();
        private readonly List<DisplayMonitor> _pendingDdcTargets = new List<DisplayMonitor>();

        // DDC 写入批次按入队顺序由单个工作线程依次发送, 保证后发的值不会被先发的覆盖
        private readonly object _ddcQueueLock = new object();
        private readonly Queue<KeyValuePair<List<DisplayMonitor>, Dictionary<byte, uint>>> _ddcQueue =
            new Queue<KeyValuePair<List<DisplayMonitor>, Dictionary<byte, uint>>>();
        private bool _ddcWorkerRunning;

        public MainForm()
        {
            _settings = SettingsStore.Load();
            BuildUi();
            BuildTray();

            _saveTimer = MakeTimer(1000, delegate { _saveTimer.Stop(); SettingsStore.Save(_settings); });
            _keepTimer = MakeTimer(5000, delegate { ReapplyNonNeutral(); });
            _ddcSetTimer = MakeTimer(400, delegate { _ddcSetTimer.Stop(); FlushDdcWrites(); });
            _displayChangeTimer = MakeTimer(1200, delegate { _displayChangeTimer.Stop(); RefreshMonitors(true); });

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;

            Load += delegate
            {
                _loadingUi = true;
                chkKeepApplied.Checked = _settings.KeepApplied;
                chkTrayOnClose.Checked = _settings.TrayOnClose;
                chkAutoStart.Checked = ReadAutoStart();
                _loadingUi = false;

                if (_settings.KeepApplied) _keepTimer.Start();

                RefreshMonitors(false);
                RefreshProfileCombo();
                ReRegisterHotkeys();

                if (_settings.ApplyOnStartup)
                {
                    int n = ReapplyNonNeutral();
                    if (n > 0) SetStatus(string.Format("已恢复 {0} 台显示器的上次设置", n));
                }
            };
        }

        private System.Windows.Forms.Timer MakeTimer(int interval, EventHandler tick)
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = interval;
            t.Tick += tick;
            return t;
        }

        // ====================================================================
        //  UI 构建
        // ====================================================================

        private void BuildUi()
        {
            Text = "GammaPanel X — 多显示器独立调节";
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(898, 604);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = CreateAppIcon();

            // ---------- 左列：显示器列表与选项 ----------
            Label lblMon = NewLabel("显示器", 12, 12);
            Controls.Add(lblMon);

            lstMonitors = new ListBox();
            lstMonitors.SetBounds(12, 34, 252, 290);
            lstMonitors.IntegralHeight = false;
            lstMonitors.SelectedIndexChanged += delegate { LoadMonitorToUi(); };
            Controls.Add(lstMonitors);

            btnIdentify = NewButton("识别显示器", 12, 332, 122, 28, delegate { IdentifyForm.ShowAll(_monitors); });
            btnRefresh = NewButton("刷新列表", 142, 332, 122, 28, delegate { RefreshMonitors(true); });
            Controls.Add(btnIdentify);
            Controls.Add(btnRefresh);

            chkApplyAll = NewCheck("同步调节所有显示器", 12, 372);
            chkKeepApplied = NewCheck("定时重新应用 (每5秒, 防游戏重置)", 12, 398);
            chkTrayOnClose = NewCheck("点击关闭按钮时最小化到托盘", 12, 424);
            chkAutoStart = NewCheck("开机自动启动", 12, 450);
            chkKeepApplied.CheckedChanged += delegate
            {
                if (_loadingUi) return;
                _settings.KeepApplied = chkKeepApplied.Checked;
                if (_settings.KeepApplied) _keepTimer.Start(); else _keepTimer.Stop();
                ScheduleSave();
            };
            chkTrayOnClose.CheckedChanged += delegate
            {
                if (_loadingUi) return;
                _settings.TrayOnClose = chkTrayOnClose.Checked;
                ScheduleSave();
            };
            chkAutoStart.CheckedChanged += delegate
            {
                if (_loadingUi) return;
                WriteAutoStart(chkAutoStart.Checked);
            };
            Controls.Add(chkApplyAll);
            Controls.Add(chkKeepApplied);
            Controls.Add(chkTrayOnClose);
            Controls.Add(chkAutoStart);

            // ---------- 右上：软件 Gamma ----------
            grpGamma = new GroupBox();
            grpGamma.Text = "软件调节 (Gamma 查找表) — 对该显示器单独生效";
            grpGamma.SetBounds(276, 8, 610, 320);
            Controls.Add(grpGamma);

            rdoAll = NewRadio("RGB 联动", 14, 26, true);
            rdoR = NewRadio("红", 110, 26, false);
            rdoG = NewRadio("绿", 165, 26, false);
            rdoB = NewRadio("蓝", 220, 26, false);
            rdoR.ForeColor = Color.Firebrick;
            rdoG.ForeColor = Color.ForestGreen;
            rdoB.ForeColor = Color.RoyalBlue;
            EventHandler chSwitch = delegate
            {
                if (_loadingUi) return;
                _channelIdx = rdoG.Checked ? 1 : (rdoB.Checked ? 2 : 0);
                LoadChannelToSliders();
            };
            rdoAll.CheckedChanged += chSwitch;
            rdoR.CheckedChanged += chSwitch;
            rdoG.CheckedChanged += chSwitch;
            rdoB.CheckedChanged += chSwitch;
            grpGamma.Controls.Add(rdoAll);
            grpGamma.Controls.Add(rdoR);
            grpGamma.Controls.Add(rdoG);
            grpGamma.Controls.Add(rdoB);

            tbBright = NewTrack(-100, 100, 25, 70, 56, 240);
            numBright = NewNum(-100, 100, 0, 318, 60);
            AddRow(grpGamma, "亮度", 56, tbBright, numBright);

            tbContrast = NewTrack(-100, 100, 25, 70, 104, 240);
            numContrast = NewNum(-100, 100, 0, 318, 108);
            AddRow(grpGamma, "对比度", 104, tbContrast, numContrast);

            tbGamma = NewTrack(30, 300, 30, 70, 152, 240);
            tbGamma.Value = 100;
            numGamma = new NumericUpDown();
            numGamma.SetBounds(318, 156, 62, 24);
            numGamma.Minimum = 0.30m;
            numGamma.Maximum = 3.00m;
            numGamma.DecimalPlaces = 2;
            numGamma.Increment = 0.05m;
            numGamma.Value = 1.00m;
            AddRow(grpGamma, "伽马", 152, tbGamma, numGamma);

            // 滑块 <-> 数字框 双向同步，并提交修改
            WireSlider(tbBright, numBright, 1);
            WireSlider(tbContrast, numContrast, 1);
            WireSlider(tbGamma, numGamma, 100);

            btnResetMonitor = NewButton("重置此显示器", 70, 206, 120, 28, delegate { ResetMonitor(); });
            btnResetAll = NewButton("重置所有显示器", 200, 206, 120, 28, delegate { ResetAllMonitors(); });
            grpGamma.Controls.Add(btnResetMonitor);
            grpGamma.Controls.Add(btnResetAll);

            picCurve = new PictureBox();
            picCurve.SetBounds(396, 28, 200, 206);
            picCurve.BorderStyle = BorderStyle.FixedSingle;
            picCurve.BackColor = Color.FromArgb(28, 28, 30);
            picCurve.Paint += PaintCurve;
            grpGamma.Controls.Add(picCurve);

            lblGammaStatus = NewLabel("", 14, 248);
            lblGammaStatus.AutoSize = false;
            lblGammaStatus.SetBounds(14, 248, 580, 20);
            lblGammaStatus.ForeColor = Color.Firebrick;
            grpGamma.Controls.Add(lblGammaStatus);

            lnkUnlock = new LinkLabel();
            lnkUnlock.Text = "极端数值被系统拒绝？点此解除 Windows Gamma 范围限制 (需管理员, 注销后生效)";
            lnkUnlock.SetBounds(14, 274, 580, 20);
            lnkUnlock.LinkClicked += delegate { UnlockGammaRange(); };
            grpGamma.Controls.Add(lnkUnlock);

            // ---------- 右中：DDC/CI ----------
            grpDdc = new GroupBox();
            grpDdc.Text = "硬件调节 (DDC/CI) — 直接控制显示器面板, 含饱和度";
            grpDdc.SetBounds(276, 336, 610, 152);
            Controls.Add(grpDdc);

            lblDdcStatus = NewLabel("正在检测…", 14, 22);
            lblDdcStatus.AutoSize = false;
            lblDdcStatus.SetBounds(14, 22, 470, 18);
            grpDdc.Controls.Add(lblDdcStatus);

            btnDdcRefresh = NewButton("重新读取", 495, 17, 100, 26, delegate { LoadDdcToUi(true); });
            grpDdc.Controls.Add(btnDdcRefresh);

            tbDdcBright = NewTrack(0, 100, 10, 70, 44, 420);
            lblDdcBrightVal = NewLabel("--", 500, 50);
            AddRow(grpDdc, "亮度", 44, tbDdcBright, null);
            grpDdc.Controls.Add(lblDdcBrightVal);

            tbDdcContrast = NewTrack(0, 100, 10, 70, 78, 420);
            lblDdcContrastVal = NewLabel("--", 500, 84);
            AddRow(grpDdc, "对比度", 78, tbDdcContrast, null);
            grpDdc.Controls.Add(lblDdcContrastVal);

            tbDdcSat = NewTrack(0, 100, 10, 70, 112, 420);
            lblDdcSatVal = NewLabel("--", 500, 118);
            AddRow(grpDdc, "饱和度", 112, tbDdcSat, null);
            grpDdc.Controls.Add(lblDdcSatVal);

            WireDdcSlider(tbDdcBright, lblDdcBrightVal, VcpCodes.Brightness);
            WireDdcSlider(tbDdcContrast, lblDdcContrastVal, VcpCodes.Contrast);
            WireDdcSlider(tbDdcSat, lblDdcSatVal, VcpCodes.Saturation);
            SetDdcEnabled(false, false, false);

            // ---------- 底部：配置文件 ----------
            grpProfiles = new GroupBox();
            grpProfiles.Text = "配置文件 (可绑定全局热键一键切换)";
            grpProfiles.SetBounds(12, 496, 874, 78);
            Controls.Add(grpProfiles);

            cboProfiles = new ComboBox();
            cboProfiles.DropDownStyle = ComboBoxStyle.DropDownList;
            cboProfiles.SetBounds(14, 30, 200, 26);
            cboProfiles.SelectedIndexChanged += delegate { ShowProfileHotkey(); };
            grpProfiles.Controls.Add(cboProfiles);

            btnApplyProfile = NewButton("应用", 222, 29, 64, 27, delegate { ApplySelectedProfile(); });
            btnSaveProfile = NewButton("保存当前", 292, 29, 84, 27, delegate { SaveToSelectedProfile(); });
            btnSaveAsProfile = NewButton("另存为…", 382, 29, 84, 27, delegate { SaveProfileAs(); });
            btnDeleteProfile = NewButton("删除", 472, 29, 64, 27, delegate { DeleteSelectedProfile(); });
            grpProfiles.Controls.Add(btnApplyProfile);
            grpProfiles.Controls.Add(btnSaveProfile);
            grpProfiles.Controls.Add(btnSaveAsProfile);
            grpProfiles.Controls.Add(btnDeleteProfile);

            Label lblHot = NewLabel("热键:", 566, 34);
            grpProfiles.Controls.Add(lblHot);

            txtHotkey = new TextBox();
            txtHotkey.ReadOnly = true;
            txtHotkey.SetBounds(606, 30, 170, 26);
            txtHotkey.Text = "点击后按组合键";
            txtHotkey.KeyDown += HotkeyCapture;
            grpProfiles.Controls.Add(txtHotkey);

            btnClearHotkey = NewButton("清除", 782, 29, 60, 27, delegate { ClearHotkey(); });
            grpProfiles.Controls.Add(btnClearHotkey);

            // ---------- 状态栏 ----------
            statusStrip = new StatusStrip();
            lblStatus = new ToolStripStatusLabel("就绪");
            statusStrip.Items.Add(lblStatus);
            Controls.Add(statusStrip);
        }

        private Label NewLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Location = new Point(x, y);
            return l;
        }

        private Button NewButton(string text, int x, int y, int w, int h, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, h);
            b.Click += onClick;
            return b;
        }

        private CheckBox NewCheck(string text, int x, int y)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.Location = new Point(x, y);
            return c;
        }

        private RadioButton NewRadio(string text, int x, int y, bool isChecked)
        {
            RadioButton r = new RadioButton();
            r.Text = text;
            r.AutoSize = true;
            r.Location = new Point(x, y);
            r.Checked = isChecked;
            return r;
        }

        private TrackBar NewTrack(int min, int max, int tick, int x, int y, int w)
        {
            TrackBar t = new TrackBar();
            t.Minimum = min;
            t.Maximum = max;
            t.TickFrequency = tick;
            t.AutoSize = false;          // 默认自适应高度约 45px, 会与下一行重叠
            t.SetBounds(x, y, w, 32);
            t.Value = Math.Max(min, Math.Min(max, 0));
            return t;
        }

        private NumericUpDown NewNum(int min, int max, int val, int x, int y)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Value = val;
            n.SetBounds(x, y, 62, 24);
            return n;
        }

        private void AddRow(GroupBox grp, string caption, int trackY, TrackBar tb, Control num)
        {
            Label l = NewLabel(caption, 14, trackY + 6);
            grp.Controls.Add(l);
            grp.Controls.Add(tb);
            if (num != null) grp.Controls.Add(num);
        }

        /// <summary>软件 Gamma 滑块与数字框双向绑定；scale 是 TrackBar 值相对数字值的倍率。</summary>
        private void WireSlider(TrackBar tb, NumericUpDown num, int scale)
        {
            tb.Scroll += delegate
            {
                if (_loadingUi) return;
                _loadingUi = true;
                decimal v = (decimal)tb.Value / scale;
                if (v < num.Minimum) v = num.Minimum;
                if (v > num.Maximum) v = num.Maximum;
                num.Value = v;
                _loadingUi = false;
                CommitSliderChange();
            };
            num.ValueChanged += delegate
            {
                if (_loadingUi) return;
                _loadingUi = true;
                int tv = (int)Math.Round(num.Value * scale);
                if (tv < tb.Minimum) tv = tb.Minimum;
                if (tv > tb.Maximum) tv = tb.Maximum;
                tb.Value = tv;
                _loadingUi = false;
                CommitSliderChange();
            };
        }

        private void WireDdcSlider(TrackBar tb, Label valLabel, byte code)
        {
            tb.Scroll += delegate
            {
                if (_loadingUi) return;
                DisplayMonitor m = SelectedMonitor();
                if (m == null) return;
                valLabel.Text = tb.Value + "/" + tb.Maximum;

                // 目标显示器在拖动时确定, 而不是 400ms 去抖到期时 — 期间切换选中项不会写错屏
                List<DisplayMonitor> newTargets = new List<DisplayMonitor>();
                if (chkApplyAll.Checked) newTargets.AddRange(_monitors);
                else newTargets.Add(m);

                // 去抖窗口内目标变了 (切换了显示器): 先把旧批次按旧目标冲刷出去, 不能混入新目标
                bool flushOld;
                lock (_pendingDdcWrites)
                {
                    flushOld = _pendingDdcWrites.Count > 0 && !TargetsEqual(_pendingDdcTargets, newTargets);
                }
                if (flushOld) FlushDdcWrites();

                lock (_pendingDdcWrites)
                {
                    _pendingDdcWrites[code] = (uint)tb.Value;
                    _pendingDdcTargets.Clear();
                    _pendingDdcTargets.AddRange(newTargets);
                }
                _ddcSetTimer.Stop();
                _ddcSetTimer.Start();
            };
        }

        private static bool TargetsEqual(List<DisplayMonitor> a, List<DisplayMonitor> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private Icon CreateAppIcon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(24, 24, 28));
                using (Pen p = new Pen(Color.FromArgb(80, 200, 120), 3f))
                {
                    Point[] pts = new Point[16];
                    for (int i = 0; i < 16; i++)
                    {
                        double x = i / 15.0;
                        double y = Math.Pow(x, 0.45);
                        pts[i] = new Point(2 + (int)(x * 28), 30 - (int)(y * 28));
                    }
                    g.DrawLines(p, pts);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        // ====================================================================
        //  托盘
        // ====================================================================

        private void BuildTray()
        {
            trayMenu = new ContextMenuStrip();
            RebuildTrayMenu();

            trayIcon = new NotifyIcon();
            trayIcon.Icon = Icon;
            trayIcon.Text = "GammaPanel X";
            trayIcon.Visible = true;
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.DoubleClick += delegate { ShowMainWindow(); };
        }

        private void RebuildTrayMenu()
        {
            trayMenu.Items.Clear();
            ToolStripMenuItem show = new ToolStripMenuItem("显示主窗口");
            show.Font = new Font(trayMenu.Font, FontStyle.Bold);
            show.Click += delegate { ShowMainWindow(); };
            trayMenu.Items.Add(show);
            trayMenu.Items.Add(new ToolStripSeparator());

            if (_settings.Profiles.Count > 0)
            {
                foreach (Profile p in _settings.Profiles)
                {
                    Profile captured = p;
                    string text = p.Name;
                    if (p.HotKey != 0) text += "  (" + FormatHotkey(p.HotKey) + ")";
                    ToolStripMenuItem item = new ToolStripMenuItem(text);
                    item.Click += delegate { ApplyProfile(captured); };
                    trayMenu.Items.Add(item);
                }
                trayMenu.Items.Add(new ToolStripSeparator());
            }

            ToolStripMenuItem reset = new ToolStripMenuItem("重置所有显示器");
            reset.Click += delegate { ResetAllMonitors(); };
            trayMenu.Items.Add(reset);
            trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exitKeep = new ToolStripMenuItem("退出 (保留当前色彩)");
            exitKeep.Click += delegate { ExitApp(false); };
            trayMenu.Items.Add(exitKeep);

            ToolStripMenuItem exitRestore = new ToolStripMenuItem("退出并恢复默认色彩");
            exitRestore.Click += delegate { ExitApp(true); };
            trayMenu.Items.Add(exitRestore);
        }

        private void ShowMainWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Activate();
        }

        // ====================================================================
        //  显示器枚举与选择
        // ====================================================================

        private void RefreshMonitors(bool reapply)
        {
            string prevKey = null;
            DisplayMonitor prev = SelectedMonitor();
            if (prev != null) prevKey = prev.StableKey;

            MonitorService.ReleaseAll(_monitors);
            _monitors = MonitorService.Enumerate();
            ResolveBaselines();   // 必须在重新应用之前读取硬件 LUT

            _loadingUi = true;
            lstMonitors.Items.Clear();
            foreach (DisplayMonitor m in _monitors)
                lstMonitors.Items.Add(m);

            int sel = 0;
            for (int i = 0; i < _monitors.Count; i++)
            {
                if (prevKey != null && _monitors[i].StableKey == prevKey) { sel = i; break; }
                if (prevKey == null && _monitors[i].IsPrimary) sel = i;
            }
            if (lstMonitors.Items.Count > 0)
                lstMonitors.SelectedIndex = sel;
            _loadingUi = false;

            if (reapply)
                ReapplyNonNeutral();

            LoadMonitorToUi();

            // 后台探测所有显示器的 DDC/CI 支持情况
            List<DisplayMonitor> snapshot = new List<DisplayMonitor>(_monitors);
            ThreadPool.QueueUserWorkItem(delegate
            {
                foreach (DisplayMonitor m in snapshot)
                {
                    try { MonitorService.ProbeDdc(m); }
                    catch (Exception) { }
                }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        // 若探测期间列表已被新一轮刷新替换, 释放被淘汰对象上重新获取的句柄, 防止泄漏
                        foreach (DisplayMonitor m in snapshot)
                        {
                            if (!_monitors.Contains(m))
                                MonitorService.ReleaseDdc(m);
                        }
                        DisplayMonitor cur = SelectedMonitor();
                        if (cur != null && snapshot.Contains(cur))
                            LoadDdcToUi(false);
                    });
                }
                catch (Exception) { }
            });

            SetStatus(string.Format("检测到 {0} 台显示器", _monitors.Count));
        }

        private DisplayMonitor SelectedMonitor()
        {
            return lstMonitors.SelectedItem as DisplayMonitor;
        }

        private MonitorConfig GetConfig(DisplayMonitor m)
        {
            MonitorConfig cfg;
            if (!_settings.Current.TryGetValue(m.StableKey, out cfg))
            {
                cfg = new MonitorConfig();
                _settings.Current[m.StableKey] = cfg;
            }
            return cfg;
        }

        // ---------- 基线 LUT (保留系统 ICC 校准) ----------

        /// <summary>为每台显示器确定基线 LUT：首次见到时捕获当前硬件曲线；
        /// 若硬件曲线与"基线+已存配置"的预期不符 (说明外部已重新校准或注销重置), 则以当前硬件曲线为新基线。</summary>
        private void ResolveBaselines()
        {
            foreach (DisplayMonitor m in _monitors)
            {
                ushort[] hw = MonitorService.GetGammaRamp(m.DeviceName);
                if (hw == null) continue;

                List<int> stored;
                if (!_settings.Baselines.TryGetValue(m.StableKey, out stored) || stored == null || stored.Count != 768)
                {
                    _settings.Baselines[m.StableKey] = ToIntList(hw);
                    ScheduleSave();
                    continue;
                }

                MonitorConfig cfg;
                if (!_settings.Current.TryGetValue(m.StableKey, out cfg)) cfg = new MonitorConfig();
                ushort[] expected = GammaMath.BuildRamp(cfg, ToRamp(stored));
                if (MaxAbsDiff(hw, expected) > 1500)   // 容忍驱动量化误差, 但能识别 vcgt/夜间模式级别的变化
                {
                    _settings.Baselines[m.StableKey] = ToIntList(hw);
                    ScheduleSave();
                }
            }
        }

        private ushort[] GetBaseline(DisplayMonitor m)
        {
            List<int> stored;
            if (_settings.Baselines.TryGetValue(m.StableKey, out stored) && stored != null && stored.Count == 768)
                return ToRamp(stored);
            return null;
        }

        private static List<int> ToIntList(ushort[] ramp)
        {
            List<int> list = new List<int>(768);
            for (int i = 0; i < 768; i++) list.Add(ramp[i]);
            return list;
        }

        private static ushort[] ToRamp(List<int> list)
        {
            ushort[] ramp = new ushort[768];
            for (int i = 0; i < 768; i++)
            {
                int v = list[i];
                if (v < 0) v = 0;
                if (v > 65535) v = 65535;
                ramp[i] = (ushort)v;
            }
            return ramp;
        }

        private static int MaxAbsDiff(ushort[] a, ushort[] b)
        {
            int max = 0;
            for (int i = 0; i < 768; i++)
            {
                int d = Math.Abs(a[i] - b[i]);
                if (d > max) max = d;
            }
            return max;
        }

        /// <summary>统一的 Gamma 应用入口：在基线之上合成配置。</summary>
        private bool ApplyConfig(DisplayMonitor m, MonitorConfig cfg)
        {
            return MonitorService.ApplyGamma(m.DeviceName, cfg, GetBaseline(m));
        }

        /// <summary>把选中显示器的配置载入右侧面板。</summary>
        private void LoadMonitorToUi()
        {
            if (_loadingUi) return;
            DisplayMonitor m = SelectedMonitor();
            if (m == null) return;

            MonitorConfig cfg = GetConfig(m);
            SyncRadioToConfig(cfg);

            LoadChannelToSliders();
            picCurve.Invalidate();
            LoadDdcToUi(false);
        }

        /// <summary>让通道单选按钮如实反映配置：联动配置显示"RGB 联动",
        /// 分通道配置切到单通道视图 — 否则滑块显示红通道值却按联动提交, 会悄悄毁掉绿/蓝通道。</summary>
        private void SyncRadioToConfig(MonitorConfig cfg)
        {
            _loadingUi = true;
            if (cfg.Linked)
            {
                rdoAll.Checked = true;
            }
            else if (rdoAll.Checked)
            {
                if (_channelIdx == 1) rdoG.Checked = true;
                else if (_channelIdx == 2) rdoB.Checked = true;
                else rdoR.Checked = true;
            }
            _loadingUi = false;
        }

        private void LoadChannelToSliders()
        {
            DisplayMonitor m = SelectedMonitor();
            if (m == null) return;
            MonitorConfig cfg = GetConfig(m);
            ChannelSettings cs = rdoAll.Checked ? cfg.Red : cfg.GetChannel(_channelIdx);

            _loadingUi = true;
            tbBright.Value = Clamp(cs.Brightness, tbBright.Minimum, tbBright.Maximum);
            numBright.Value = tbBright.Value;
            tbContrast.Value = Clamp(cs.Contrast, tbContrast.Minimum, tbContrast.Maximum);
            numContrast.Value = tbContrast.Value;
            int g = (int)Math.Round(cs.Gamma * 100.0);
            tbGamma.Value = Clamp(g, tbGamma.Minimum, tbGamma.Maximum);
            numGamma.Value = (decimal)tbGamma.Value / 100;
            _loadingUi = false;
        }

        private static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        // ====================================================================
        //  软件 Gamma 提交
        // ====================================================================

        /// <summary>滑块变动 → 写入配置并立即应用。</summary>
        private void CommitSliderChange()
        {
            DisplayMonitor m = SelectedMonitor();
            if (m == null) return;
            MonitorConfig cfg = GetConfig(m);

            int bright = (int)numBright.Value;
            int contrast = (int)numContrast.Value;
            double gamma = (double)numGamma.Value;

            if (rdoAll.Checked)
            {
                SetChannel(cfg.Red, bright, contrast, gamma);
                SetChannel(cfg.Green, bright, contrast, gamma);
                SetChannel(cfg.Blue, bright, contrast, gamma);
                cfg.Linked = true;
            }
            else
            {
                SetChannel(cfg.GetChannel(_channelIdx), bright, contrast, gamma);
                cfg.Linked = false;
            }

            if (chkApplyAll.Checked)
            {
                foreach (DisplayMonitor other in _monitors)
                {
                    if (other != m)
                        _settings.Current[other.StableKey] = cfg.Clone();
                }
                ApplyToMonitors(_monitors);
            }
            else
            {
                ApplyToMonitors(new List<DisplayMonitor> { m });
            }

            picCurve.Invalidate();
            ScheduleSave();
        }

        private static void SetChannel(ChannelSettings cs, int bright, int contrast, double gamma)
        {
            cs.Brightness = bright;
            cs.Contrast = contrast;
            cs.Gamma = gamma;
        }

        private void ApplyToMonitors(List<DisplayMonitor> targets)
        {
            bool anyRejected = false;
            foreach (DisplayMonitor m in targets)
            {
                MonitorConfig cfg = GetConfig(m);
                if (!ApplyConfig(m, cfg))
                    anyRejected = true;
            }
            ShowRampRejected(anyRejected);
        }

        private void ShowRampRejected(bool rejected)
        {
            lblGammaStatus.Text = rejected
                ? "部分数值被系统拒绝 (超出默认允许范围), 屏幕可能未变化 — 见下方解除限制链接"
                : "";
        }

        /// <summary>重新应用所有已连接且非默认值的显示器配置。返回应用台数。</summary>
        private int ReapplyNonNeutral()
        {
            int n = 0;
            bool anyRejected = false;
            foreach (DisplayMonitor m in _monitors)
            {
                MonitorConfig cfg;
                if (_settings.Current.TryGetValue(m.StableKey, out cfg) && !cfg.IsNeutral())
                {
                    if (!ApplyConfig(m, cfg))
                        anyRejected = true;
                    n++;
                }
            }
            if (anyRejected)
                ShowRampRejected(true);
            return n;
        }

        private void ResetMonitor()
        {
            DisplayMonitor m = SelectedMonitor();
            if (m == null) return;
            MonitorConfig cfg = new MonitorConfig();
            _settings.Current[m.StableKey] = cfg;
            ApplyConfig(m, cfg);   // 中性配置 = 精确还原基线 (含系统 ICC 校准)
            SyncRadioToConfig(cfg);
            LoadChannelToSliders();
            picCurve.Invalidate();
            ScheduleSave();
            SetStatus(m.ToString() + " 已重置");
        }

        private void ResetAllMonitors()
        {
            MonitorConfig last = null;
            foreach (DisplayMonitor m in _monitors)
            {
                MonitorConfig cfg = new MonitorConfig();
                _settings.Current[m.StableKey] = cfg;
                ApplyConfig(m, cfg);
                last = cfg;
            }
            if (last != null) SyncRadioToConfig(last);
            LoadChannelToSliders();
            picCurve.Invalidate();
            ScheduleSave();
            SetStatus("所有显示器已重置为默认");
        }

        // ====================================================================
        //  曲线预览
        // ====================================================================

        private void PaintCurve(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int w = picCurve.ClientSize.Width;
            int h = picCurve.ClientSize.Height;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (Pen grid = new Pen(Color.FromArgb(55, 55, 60)))
            {
                for (int i = 1; i < 4; i++)
                {
                    g.DrawLine(grid, w * i / 4, 0, w * i / 4, h);
                    g.DrawLine(grid, 0, h * i / 4, w, h * i / 4);
                }
            }
            using (Pen diag = new Pen(Color.FromArgb(90, 90, 95)))
            {
                diag.DashStyle = DashStyle.Dash;
                g.DrawLine(diag, 0, h - 1, w - 1, 0);
            }

            DisplayMonitor m = SelectedMonitor();
            if (m == null) return;
            MonitorConfig cfg = GetConfig(m);

            bool same = ChannelsEqual(cfg.Red, cfg.Green) && ChannelsEqual(cfg.Green, cfg.Blue);
            if (same)
            {
                DrawChannelCurve(g, cfg.Red, Color.Gainsboro, w, h);
            }
            else
            {
                DrawChannelCurve(g, cfg.Red, Color.FromArgb(235, 80, 80), w, h);
                DrawChannelCurve(g, cfg.Green, Color.FromArgb(90, 220, 110), w, h);
                DrawChannelCurve(g, cfg.Blue, Color.FromArgb(100, 140, 250), w, h);
            }
        }

        private static bool ChannelsEqual(ChannelSettings a, ChannelSettings b)
        {
            return a.Brightness == b.Brightness && a.Contrast == b.Contrast && Math.Abs(a.Gamma - b.Gamma) < 0.001;
        }

        private void DrawChannelCurve(Graphics g, ChannelSettings cs, Color color, int w, int h)
        {
            PointF[] pts = new PointF[64];
            for (int i = 0; i < 64; i++)
            {
                int idx = i * 255 / 63;
                double v = GammaMath.Evaluate(cs, idx);
                pts[i] = new PointF((float)(idx / 255.0 * (w - 1)), (float)((1.0 - v) * (h - 1)));
            }
            using (Pen p = new Pen(color, 1.6f))
            {
                g.DrawLines(p, pts);
            }
        }

        // ====================================================================
        //  DDC/CI
        // ====================================================================

        private void SetDdcEnabled(bool bright, bool contrast, bool sat)
        {
            tbDdcBright.Enabled = bright;
            tbDdcContrast.Enabled = contrast;
            tbDdcSat.Enabled = sat;
            if (!bright) lblDdcBrightVal.Text = "--";
            if (!contrast) lblDdcContrastVal.Text = "--";
            if (!sat) lblDdcSatVal.Text = "--";
        }

        /// <summary>后台读取选中显示器的 DDC 数值并刷新硬件调节区。</summary>
        private void LoadDdcToUi(bool forceReprobe)
        {
            DisplayMonitor m = SelectedMonitor();
            if (m == null) return;

            SetDdcEnabled(false, false, false);
            lblDdcStatus.Text = "正在读取显示器硬件参数…";

            DisplayMonitor target = m;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (forceReprobe || !target.DdcProbed)
                        MonitorService.ProbeDdc(target);

                    uint bCur = 0, bMax = 0, cCur = 0, cMax = 0, sCur = 0, sMax = 0;
                    bool bOk = target.DdcBrightnessOk && MonitorService.DdcGet(target, VcpCodes.Brightness, out bCur, out bMax);
                    bool cOk = target.DdcContrastOk && MonitorService.DdcGet(target, VcpCodes.Contrast, out cCur, out cMax);
                    bool sOk = target.DdcSaturationOk && MonitorService.DdcGet(target, VcpCodes.Saturation, out sCur, out sMax);

                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (!_monitors.Contains(target))
                        {
                            MonitorService.ReleaseDdc(target);     // 对象已被刷新淘汰, 回收句柄防泄漏
                            return;
                        }
                        if (SelectedMonitor() != target) return;   // 选择已变, 丢弃过期结果

                        _loadingUi = true;
                        if (bOk && bMax > 0)
                        {
                            tbDdcBright.Maximum = (int)bMax;
                            tbDdcBright.Value = Clamp((int)bCur, 0, (int)bMax);
                            lblDdcBrightVal.Text = bCur + "/" + bMax;
                        }
                        if (cOk && cMax > 0)
                        {
                            tbDdcContrast.Maximum = (int)cMax;
                            tbDdcContrast.Value = Clamp((int)cCur, 0, (int)cMax);
                            lblDdcContrastVal.Text = cCur + "/" + cMax;
                        }
                        if (sOk && sMax > 0)
                        {
                            tbDdcSat.Maximum = (int)sMax;
                            tbDdcSat.Value = Clamp((int)sCur, 0, (int)sMax);
                            lblDdcSatVal.Text = sCur + "/" + sMax;
                        }
                        _loadingUi = false;

                        SetDdcEnabled(bOk, cOk, sOk);
                        if (!bOk && !cOk && !sOk)
                            lblDdcStatus.Text = "此显示器不支持 DDC/CI (或被显示器菜单关闭)";
                        else if (!sOk)
                            lblDdcStatus.Text = "已连接 — 此显示器不提供饱和度(VCP 0x8A), 可调亮度/对比度";
                        else
                            lblDdcStatus.Text = "已连接 — 参数直接写入显示器硬件";
                    });
                }
                catch (Exception)
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            lblDdcStatus.Text = "DDC/CI 通信失败";
                        });
                    }
                    catch (Exception) { }
                }
            });
        }

        /// <summary>把积压的 DDC 写入按顺序排队发送（去抖后调用）。</summary>
        private void FlushDdcWrites()
        {
            Dictionary<byte, uint> writes;
            List<DisplayMonitor> targets;
            lock (_pendingDdcWrites)
            {
                if (_pendingDdcWrites.Count == 0 || _pendingDdcTargets.Count == 0) return;
                writes = new Dictionary<byte, uint>(_pendingDdcWrites);
                targets = new List<DisplayMonitor>(_pendingDdcTargets);
                _pendingDdcWrites.Clear();
                _pendingDdcTargets.Clear();
            }

            lock (_ddcQueueLock)
            {
                _ddcQueue.Enqueue(new KeyValuePair<List<DisplayMonitor>, Dictionary<byte, uint>>(targets, writes));
                if (_ddcWorkerRunning) return;   // 已有工作线程在按序发送
                _ddcWorkerRunning = true;
            }
            ThreadPool.QueueUserWorkItem(delegate { DrainDdcQueue(); });
        }

        private void DrainDdcQueue()
        {
            while (true)
            {
                KeyValuePair<List<DisplayMonitor>, Dictionary<byte, uint>> batch;
                lock (_ddcQueueLock)
                {
                    if (_ddcQueue.Count == 0)
                    {
                        _ddcWorkerRunning = false;
                        return;
                    }
                    batch = _ddcQueue.Dequeue();
                }
                foreach (DisplayMonitor t in batch.Key)
                {
                    foreach (KeyValuePair<byte, uint> kv in batch.Value)
                    {
                        bool supported =
                            (kv.Key == VcpCodes.Brightness && t.DdcBrightnessOk) ||
                            (kv.Key == VcpCodes.Contrast && t.DdcContrastOk) ||
                            (kv.Key == VcpCodes.Saturation && t.DdcSaturationOk);
                        if (supported)
                        {
                            try { MonitorService.DdcSet(t, kv.Key, kv.Value); }
                            catch (Exception) { }
                        }
                    }
                }
            }
        }

        // ====================================================================
        //  配置文件
        // ====================================================================

        private void RefreshProfileCombo()
        {
            string prev = cboProfiles.SelectedItem as string;
            cboProfiles.Items.Clear();
            foreach (Profile p in _settings.Profiles)
                cboProfiles.Items.Add(p.Name);
            if (prev != null && cboProfiles.Items.Contains(prev))
                cboProfiles.SelectedItem = prev;
            else if (cboProfiles.Items.Count > 0)
                cboProfiles.SelectedIndex = 0;
            RebuildTrayMenu();
            ShowProfileHotkey();
        }

        private Profile SelectedProfile()
        {
            string name = cboProfiles.SelectedItem as string;
            if (name == null) return null;
            foreach (Profile p in _settings.Profiles)
                if (p.Name == name) return p;
            return null;
        }

        private Dictionary<string, MonitorConfig> SnapshotCurrent()
        {
            Dictionary<string, MonitorConfig> snap = new Dictionary<string, MonitorConfig>();
            foreach (KeyValuePair<string, MonitorConfig> kv in _settings.Current)
                snap[kv.Key] = kv.Value.Clone();
            return snap;
        }

        private void SaveToSelectedProfile()
        {
            Profile p = SelectedProfile();
            if (p == null) { SaveProfileAs(); return; }
            p.Monitors = SnapshotCurrent();
            ScheduleSave();
            SetStatus(string.Format("配置文件「{0}」已更新", p.Name));
        }

        private void SaveProfileAs()
        {
            string name = PromptText("另存为配置文件", "输入配置文件名称:");
            if (string.IsNullOrEmpty(name)) return;

            Profile existing = null;
            foreach (Profile p in _settings.Profiles)
                if (p.Name == name) { existing = p; break; }

            if (existing != null)
            {
                existing.Monitors = SnapshotCurrent();
            }
            else
            {
                Profile p = new Profile();
                p.Name = name;
                p.Monitors = SnapshotCurrent();
                _settings.Profiles.Add(p);
            }
            ScheduleSave();
            RefreshProfileCombo();
            cboProfiles.SelectedItem = name;
            SetStatus(string.Format("配置文件「{0}」已保存", name));
        }

        private void DeleteSelectedProfile()
        {
            Profile p = SelectedProfile();
            if (p == null) return;
            if (MessageBox.Show(this, string.Format("确定删除配置文件「{0}」?", p.Name),
                    "GammaPanel X", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            _settings.Profiles.Remove(p);
            ScheduleSave();
            RefreshProfileCombo();
            ReRegisterHotkeys();
        }

        private void ApplySelectedProfile()
        {
            Profile p = SelectedProfile();
            if (p == null) return;
            ApplyProfile(p);
        }

        private void ApplyProfile(Profile p)
        {
            if (p.Monitors != null)
            {
                foreach (KeyValuePair<string, MonitorConfig> kv in p.Monitors)
                    _settings.Current[kv.Key] = kv.Value.Clone();
            }
            bool anyRejected = false;
            foreach (DisplayMonitor m in _monitors)
            {
                MonitorConfig cfg;
                if (_settings.Current.TryGetValue(m.StableKey, out cfg))
                {
                    if (!ApplyConfig(m, cfg))
                        anyRejected = true;
                }
            }
            ShowRampRejected(anyRejected);
            _settings.LastProfile = p.Name;
            ScheduleSave();
            DisplayMonitor sel = SelectedMonitor();
            if (sel != null) SyncRadioToConfig(GetConfig(sel));
            LoadChannelToSliders();
            picCurve.Invalidate();
            if (anyRejected)
                SetStatus(string.Format("已应用「{0}」, 但部分数值被系统拒绝", p.Name));
            else
                SetStatus(string.Format("已应用配置文件「{0}」", p.Name));
            if (trayIcon != null && !Visible)
                trayIcon.ShowBalloonTip(1200, "GammaPanel X", string.Format("已应用「{0}」", p.Name), ToolTipIcon.Info);
        }

        private string PromptText(string title, string caption)
        {
            using (Form f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ClientSize = new Size(320, 120);
                f.StartPosition = FormStartPosition.CenterParent;
                f.Font = Font;

                Label l = NewLabel(caption, 12, 12);
                TextBox t = new TextBox();
                t.SetBounds(12, 38, 296, 26);
                Button ok = new Button();
                ok.Text = "确定";
                ok.DialogResult = DialogResult.OK;
                ok.SetBounds(142, 78, 80, 28);
                Button cancel = new Button();
                cancel.Text = "取消";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.SetBounds(228, 78, 80, 28);

                f.Controls.Add(l);
                f.Controls.Add(t);
                f.Controls.Add(ok);
                f.Controls.Add(cancel);
                f.AcceptButton = ok;
                f.CancelButton = cancel;

                if (f.ShowDialog(this) == DialogResult.OK)
                    return t.Text.Trim();
                return null;
            }
        }

        // ====================================================================
        //  热键
        // ====================================================================

        private void ShowProfileHotkey()
        {
            Profile p = SelectedProfile();
            if (p == null || p.HotKey == 0)
                txtHotkey.Text = "点击后按组合键";
            else
                txtHotkey.Text = FormatHotkey(p.HotKey);
        }

        private void HotkeyCapture(object sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;

            Keys key = e.KeyCode;
            if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu ||
                key == Keys.LWin || key == Keys.RWin || key == Keys.None)
                return;

            Profile p = SelectedProfile();
            if (p == null)
            {
                SetStatus("请先创建并选中一个配置文件, 再设置热键");
                return;
            }
            if (key == Keys.Escape || key == Keys.Back || key == Keys.Delete)
            {
                ClearHotkey();
                return;
            }

            // 裸键会从所有程序手里抢走该按键, 只允许 Ctrl/Alt 组合或 F 功能键
            bool hasMod = (e.KeyData & (Keys.Control | Keys.Alt)) != 0;
            bool isFKey = key >= Keys.F1 && key <= Keys.F24;
            if (!hasMod && !isFKey)
            {
                SetStatus("请使用 Ctrl/Alt 组合键 (如 Ctrl+Alt+1) 或 F 功能键作为热键");
                return;
            }

            foreach (Profile other in _settings.Profiles)
            {
                if (other != p && other.HotKey == (int)e.KeyData)
                {
                    SetStatus(string.Format("该热键已被配置文件「{0}」占用", other.Name));
                    return;
                }
            }

            p.HotKey = (int)e.KeyData;
            txtHotkey.Text = FormatHotkey(p.HotKey);
            ScheduleSave();
            ReRegisterHotkeys();
            RebuildTrayMenu();
        }

        private void ClearHotkey()
        {
            Profile p = SelectedProfile();
            if (p == null) return;
            p.HotKey = 0;
            ShowProfileHotkey();
            ScheduleSave();
            ReRegisterHotkeys();
            RebuildTrayMenu();
        }

        private static string FormatHotkey(int keyData)
        {
            if (keyData == 0) return "";
            Keys kd = (Keys)keyData;
            string s = "";
            if ((kd & Keys.Control) == Keys.Control) s += "Ctrl+";
            if ((kd & Keys.Shift) == Keys.Shift) s += "Shift+";
            if ((kd & Keys.Alt) == Keys.Alt) s += "Alt+";
            s += (kd & Keys.KeyCode).ToString();
            return s;
        }

        private void ReRegisterHotkeys()
        {
            for (int i = 0; i < _hotkeyOrder.Count; i++)
                NativeMethods.UnregisterHotKey(Handle, 0x100 + i);
            _hotkeyOrder.Clear();

            foreach (Profile p in _settings.Profiles)
            {
                if (p.HotKey == 0) continue;
                Keys kd = (Keys)p.HotKey;
                uint mods = 0;
                if ((kd & Keys.Control) == Keys.Control) mods |= NativeMethods.MOD_CONTROL;
                if ((kd & Keys.Shift) == Keys.Shift) mods |= NativeMethods.MOD_SHIFT;
                if ((kd & Keys.Alt) == Keys.Alt) mods |= NativeMethods.MOD_ALT;
                uint vk = (uint)(kd & Keys.KeyCode);

                int id = 0x100 + _hotkeyOrder.Count;
                if (NativeMethods.RegisterHotKey(Handle, id, mods, vk))
                    _hotkeyOrder.Add(p);
                else
                    SetStatus(string.Format("热键 {0} 注册失败 (可能已被其他程序占用)", FormatHotkey(p.HotKey)));
            }
        }

        // ====================================================================
        //  系统集成
        // ====================================================================

        protected override void WndProc(ref Message msg)
        {
            if (msg.Msg == NativeMethods.WM_HOTKEY)
            {
                int idx = msg.WParam.ToInt32() - 0x100;
                if (idx >= 0 && idx < _hotkeyOrder.Count)
                    ApplyProfile(_hotkeyOrder[idx]);
            }
            else if (msg.Msg == NativeMethods.WM_DISPLAYCHANGE)
            {
                _displayChangeTimer.Stop();
                _displayChangeTimer.Start();
            }
            else if (ShowWindowMsg != 0 && msg.Msg == (int)ShowWindowMsg)
            {
                ShowMainWindow();
            }
            base.WndProc(ref msg);
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                _displayChangeTimer.Stop();
                _displayChangeTimer.Start();   // 唤醒后驱动常重置 LUT, 延迟刷新+重应用
            }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock)
                ReapplyNonNeutral();
        }

        private bool ReadAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    return k != null && k.GetValue(RunKeyName) != null;
                }
            }
            catch (Exception) { return false; }
        }

        private void WriteAutoStart(bool enable)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (enable)
                        k.SetValue(RunKeyName, "\"" + Application.ExecutablePath + "\"");
                    else
                        k.DeleteValue(RunKeyName, false);
                }
            }
            catch (Exception)
            {
                SetStatus("写入开机启动项失败");
            }
        }

        private void UnlockGammaRange()
        {
            try
            {
                object val = Registry.GetValue(
                    "HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ICM",
                    "GdiIcmGammaRange", null);
                if (val is int && (int)val == 256)
                {
                    MessageBox.Show(this, "限制已经解除。如果还未生效, 请注销或重启一次。",
                        "GammaPanel X", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "reg";
                psi.Arguments = "add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ICM\" /v GdiIcmGammaRange /t REG_DWORD /d 256 /f";
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process proc = Process.Start(psi);
                proc.WaitForExit(10000);

                object after = Registry.GetValue(
                    "HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ICM",
                    "GdiIcmGammaRange", null);
                if (after is int && (int)after == 256)
                    MessageBox.Show(this, "已写入注册表。注销或重启后, 系统将允许更大的 Gamma 调节范围。",
                        "GammaPanel X", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(this, "注册表写入未生效, 请确认在弹出的 UAC 窗口中点击了\"是\"。",
                        "GammaPanel X", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception)
            {
                // 用户取消了 UAC 提示
            }
        }

        // ====================================================================
        //  退出
        // ====================================================================

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exitRequested && e.CloseReason == CloseReason.UserClosing && _settings.TrayOnClose)
            {
                e.Cancel = true;
                Hide();
                trayIcon.ShowBalloonTip(1500, "GammaPanel X", "仍在后台运行, 双击托盘图标可重新打开", ToolTipIcon.Info);
                return;
            }

            if (_restoreNeutralOnExit)
            {
                // 中性配置在基线之上合成 = 精确还原系统默认/ICC 校准曲线
                foreach (DisplayMonitor m in _monitors)
                    ApplyConfig(m, new MonitorConfig());
            }

            SettingsStore.Save(_settings);
            for (int i = 0; i < _hotkeyOrder.Count; i++)
                NativeMethods.UnregisterHotKey(Handle, 0x100 + i);
            MonitorService.ReleaseAll(_monitors);
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            trayIcon.Visible = false;
            trayIcon.Dispose();
            base.OnFormClosing(e);
        }

        private void ExitApp(bool restoreNeutral)
        {
            _exitRequested = true;
            _restoreNeutralOnExit = restoreNeutral;
            Close();
        }

        private void ScheduleSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void SetStatus(string text)
        {
            lblStatus.Text = text;
        }
    }
}
