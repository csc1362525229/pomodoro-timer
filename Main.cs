/*
 * 番茄钟 — WebView2 桌面应用
 * 编译: C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /target:winexe /out:番茄钟.exe /r:Microsoft.Web.WebView2.Core.dll /r:Microsoft.Web.WebView2.WinForms.dll Main.cs
 */

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

public class Program
{
    [STAThread]
    static void Main()
    {
        // 高 DPI 感知（清单优先，P/Invoke 兜底）
        try { SetProcessDPIAware(); } catch { }

        bool createdNew;
        var mutex = new System.Threading.Mutex(true, @"Global\PomodoroApp_S", out createdNew);
        if (!createdNew) { mutex.Close(); return; }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new PomodoroForm());
        GC.KeepAlive(mutex);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();
}

public class PomodoroForm : Form
{
    private WebView2 _webView;
    private NotifyIcon _tray;
    private ContextMenuStrip _trayMenu;
    private bool _alwaysOnTop;
    private bool _isQuitting;
    private bool _closeHintShown;
    private Button _btnTop, _btnMin, _btnClose;  // 标题栏按钮（Resize 时重定位）

    public PomodoroForm()
    {
        InitForm();
        InitTray();
        InitWebView();
    }

    void InitForm()
    {
        Text = "番茄钟";
        Size = new Size(520, 940);
        MinimumSize = new Size(420, 760);
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.FromArgb(26, 29, 34);  // Coastal 深灰底色
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;

        // 程序图标 — 暖杏橙色
        var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var br = new SolidBrush(Color.FromArgb(240, 131, 106)))
                g.FillEllipse(br, 4, 4, 56, 56);
        }
        Icon = Icon.FromHandle(bmp.GetHicon());

        // 窗口拖拽 — 标题栏
        var titleBar = new Panel { Height = 40, Dock = DockStyle.Top, BackColor = Color.FromArgb(26, 29, 34), Cursor = Cursors.SizeAll };
        titleBar.MouseDown += (s, e) => {
            if (e.Button == MouseButtons.Left) { NativeMethods.ReleaseCapture(); NativeMethods.SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); }
        };

        // 标题文字（垂直居中于 40px 标题栏）
        var titleLbl = new Label { Text = "  🍅 番茄钟", ForeColor = Color.FromArgb(149, 155, 178), Font = new Font("Microsoft YaHei", 9f), Location = new Point(0, 10), AutoSize = true };
        titleBar.Controls.Add(titleLbl);

        // 右侧按钮容器（停靠右边缘，不受窗口宽度影响）
        var rightPanel = new Panel { Dock = DockStyle.Right, Width = 124, Height = 40, BackColor = Color.FromArgb(26, 29, 34) };
        // 让拖拽事件穿透此面板
        rightPanel.MouseDown += (s, e) => {
            if (e.Button == MouseButtons.Left) { NativeMethods.ReleaseCapture(); NativeMethods.SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); }
        };

        // 置顶按钮 — 面板内左侧 (y=6 使 28px 按钮居中于 40px 面板)
        _btnTop = new Button { Text = "📌", FlatStyle = FlatStyle.Flat, Size = new Size(32, 28), Location = new Point(6, 6), ForeColor = Color.FromArgb(149, 155, 178), Font = new Font("Microsoft YaHei", 9f), Cursor = Cursors.Hand };
        _btnTop.FlatAppearance.BorderSize = 0;
        _btnTop.Click += (s, e) => { _alwaysOnTop = !_alwaysOnTop; TopMost = _alwaysOnTop; _btnTop.ForeColor = _alwaysOnTop ? Color.FromArgb(240, 131, 106) : Color.FromArgb(149, 155, 178); };

        // 最小化按钮 — 面板内中间
        _btnMin = new Button { Text = "─", FlatStyle = FlatStyle.Flat, Size = new Size(32, 28), Location = new Point(46, 6), ForeColor = Color.FromArgb(149, 155, 178), Font = new Font("Microsoft YaHei", 10f), Cursor = Cursors.Hand };
        _btnMin.FlatAppearance.BorderSize = 0;
        _btnMin.Click += (s, e) => WindowState = FormWindowState.Minimized;

        // 关闭按钮 — 面板内右侧
        _btnClose = new Button { Text = "✕", FlatStyle = FlatStyle.Flat, Size = new Size(32, 28), Location = new Point(84, 6), ForeColor = Color.FromArgb(149, 155, 178), Font = new Font("Microsoft YaHei", 10f), Cursor = Cursors.Hand };
        _btnClose.FlatAppearance.BorderSize = 0;
        _btnClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(240, 131, 106);
        _btnClose.Click += (s, e) => CloseWindow();

        rightPanel.Controls.Add(_btnClose);
        rightPanel.Controls.Add(_btnMin);
        rightPanel.Controls.Add(_btnTop);
        titleBar.Controls.Add(rightPanel);

        // 标题栏底部分隔线
        titleBar.Paint += (s, e) => { using (var pen = new Pen(Color.FromArgb(42, 46, 56), 1)) e.Graphics.DrawLine(pen, 0, 39, titleBar.Width, 39); };

        Controls.Add(titleBar);
    }

    void InitTray()
    {
        _trayMenu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("显示窗口");
        showItem.Click += (s, e) => ShowFromTray();
        _trayMenu.Items.Add(showItem);

        _trayMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (s, e) => { _isQuitting = true; _tray.Visible = false; Application.Exit(); };
        _trayMenu.Items.Add(exitItem);

        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var br = new SolidBrush(Color.FromArgb(240, 131, 106)))
                g.FillEllipse(br, 2, 2, 28, 28);
        }
        _tray = new NotifyIcon { Icon = Icon.FromHandle(bmp.GetHicon()), Text = "番茄钟", ContextMenuStrip = _trayMenu, Visible = true };
        _tray.DoubleClick += (s, e) => ShowFromTray();
        Resize += (s, e) => { if (WindowState == FormWindowState.Minimized) HideToTray(); };
    }

    async void InitWebView()
    {
        // 容器面板 — 隔离 WebView2 原生 HWND，防止其覆盖标题栏
        var container = new Panel { Dock = DockStyle.Fill, BackColor = System.Drawing.Color.FromArgb(26, 29, 34) };
        Controls.Add(container);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(26, 29, 34)
        };
        container.Controls.Add(_webView);

        var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
            null, Path.GetTempPath());

        await _webView.EnsureCoreWebView2Async(env);

        // 前台消息：JS → C#
        _webView.CoreWebView2.WebMessageReceived += (s, e) =>
        {
            string msg = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(msg)) return;

            if (msg.StartsWith("tray:"))
                _tray.Text = "番茄钟 - " + msg.Substring(5);
            else if (msg == "top-on")
                { _alwaysOnTop = true; TopMost = true; }
            else if (msg == "top-off")
                { _alwaysOnTop = false; TopMost = false; }
            else if (msg == "hide-tray")
                HideToTray();
            else if (msg == "minimize")
                WindowState = FormWindowState.Minimized;
            else if (msg == "bell")
                { try { Console.Beep(600, 100); } catch { } }
            else if (msg.StartsWith("notify:"))
            {
                var parts = msg.Substring(7).Split(new[] { '|' }, 2);
                if (parts.Length == 2)
                    _tray.ShowBalloonTip(3000, parts[0], parts[1], ToolTipIcon.Info);
            }
        };

        // WebView2 就绪后才加载 HTML
        LoadHtml();
    }

    void LoadHtml()
    {
        // 优先读取外部 AppPage.html（方便后续更新 UI 无需重编译）
        string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AppPage.html");
        if (File.Exists(htmlPath))
        {
            string html = File.ReadAllText(htmlPath, System.Text.Encoding.UTF8);
            _webView.CoreWebView2.NavigateToString(html);
            return;
        }
        // 兜底：内嵌精简版（外部文件缺失时）
        _webView.CoreWebView2.NavigateToString(GetEmbeddedHtml());
    }

    void CloseWindow()
    {
        if (!_closeHintShown)
        {
            _closeHintShown = true;
            var result = ShowCloseHintDialog();
            if (result)
            {
                _isQuitting = true;
                _tray.Visible = false;
                Application.Exit();
                return;
            }
        }
        HideToTray();
    }

    bool ShowCloseHintDialog()
    {
        var dlg = new Form
        {
            Text = "番茄钟",
            Size = new Size(400, 230),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Color.FromArgb(33, 37, 44),
            MaximizeBox = false,
            MinimizeBox = false
        };

        var icon = new Label
        {
            Text = "🍅", Font = new Font("Microsoft YaHei", 28f),
            Location = new Point(20, 20), Size = new Size(50, 50),
            TextAlign = ContentAlignment.MiddleCenter
        };

        var msg = new Label
        {
            Text = "关闭窗口后，番茄钟会最小化到\n右下角系统托盘继续运行。",
            ForeColor = Color.FromArgb(230, 232, 238),
            Font = new Font("Microsoft YaHei", 10.5f),
            Location = new Point(82, 22), AutoSize = true
        };

        var btnTray = new Button
        {
            Text = "最小化到托盘", Size = new Size(150, 38),
            Location = new Point(35, 115), FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White, BackColor = Color.FromArgb(240, 131, 106),
            Font = new Font("Microsoft YaHei", 10f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnTray.FlatAppearance.BorderSize = 0;
        btnTray.Click += (s, e) => { dlg.DialogResult = DialogResult.OK; dlg.Close(); };

        var btnExit = new Button
        {
            Text = "退出程序", Size = new Size(150, 38),
            Location = new Point(210, 115), FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(180, 180, 200),
            BackColor = Color.FromArgb(60, 60, 80),
            Font = new Font("Microsoft YaHei", 10f),
            Cursor = Cursors.Hand
        };
        btnExit.FlatAppearance.BorderSize = 0;
        btnExit.Click += (s, e) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };

        dlg.Controls.Add(icon); dlg.Controls.Add(msg);
        dlg.Controls.Add(btnTray); dlg.Controls.Add(btnExit);

        dlg.AcceptButton = btnTray;
        return dlg.ShowDialog(this) == DialogResult.Cancel;
    }

    void HideToTray() { Hide(); }
    void ShowFromTray() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_isQuitting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            CloseWindow();
        }
    }

    // 嵌入的 HTML（从外部文件读取，如果不存在则用默认版本）
    string GetEmbeddedHtml()
    {
        return @"<!DOCTYPE html><html lang=""zh-CN""><head><meta charset=""UTF-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1.0""><title>番茄钟</title><style>:root{--bg:#1a1d22;--surface:#21252c;--fg:#e6e8ee;--sec:#959bb2;--work:#f0836a;--short:#6bbfa4;--long:#8098d8;--ring:#2a2e38}*{margin:0;padding:0;box-sizing:border-box}body{font-family:""Microsoft YaHei"",""PingFang SC"",sans-serif;background:var(--bg);color:var(--fg);display:flex;justify-content:center;align-items:center;min-height:100vh;user-select:none;-webkit-user-select:none;overflow:hidden}.app{display:flex;flex-direction:column;align-items:center;gap:24px;padding:20px 24px;width:100%;max-width:500px}.tabs{display:flex;gap:4px;background:var(--surface);border-radius:24px;padding:4px}.tab{padding:10px 20px;border-radius:22px;border:none;background:transparent;color:var(--sec);font-size:14px;cursor:pointer;transition:.25s;font-family:inherit}.tab.active{background:var(--ring);color:var(--fg)}.tab:hover:not(.active){color:var(--fg)}.ring-wrap{position:relative;width:280px;height:280px;display:flex;align-items:center;justify-content:center}.ring-svg{position:absolute;width:100%;height:100%;transform:rotate(-90deg)}.ring-bg{fill:none;stroke:var(--ring);stroke-width:8}.ring-bar{fill:none;stroke:var(--work);stroke-width:8;stroke-linecap:round;transition:stroke-dashoffset 1s linear,stroke .3s}.time{font-size:64px;font-weight:700;font-variant-numeric:tabular-nums;z-index:1;font-family:""Consolas"",""Courier New"",monospace}.time.paused{opacity:.55}.status{font-size:13px;color:var(--sec);margin-top:-12px}.dots{display:flex;gap:10px;align-items:center;margin-top:-8px}.dot{width:12px;height:12px;border-radius:50%;background:var(--ring);transition:.3s}.dot.done{background:var(--work);box-shadow:0 0 8px rgba(240,131,106,.35)}.dot.cur{background:var(--sec)}.btn-row{display:flex;gap:12px;align-items:center}.btn{border:none;cursor:pointer;border-radius:50%;display:flex;align-items:center;justify-content:center;transition:.25s;font-family:inherit;outline:none}.btn-play{width:72px;height:72px;background:var(--work);color:#fff;font-size:22px;box-shadow:0 4px 20px rgba(240,131,106,.3)}.btn-play:hover{transform:scale(1.06)}.btn-play:active{transform:scale(.94)}.btn-sm{width:44px;height:44px;background:var(--ring);color:var(--sec);font-size:16px}.btn-sm:hover{background:#2d2b3d;color:var(--fg)}.btn-sm:active{transform:scale(.92)}.overlay{position:fixed;inset:0;background:rgba(0,0,0,.65);backdrop-filter:blur(4px);display:flex;align-items:center;justify-content:center;z-index:100;opacity:0;pointer-events:none;transition:opacity .3s}.overlay.open{opacity:1;pointer-events:auto}.panel{background:var(--surface);border-radius:16px;padding:28px;width:340px;max-width:92vw;display:flex;flex-direction:column;gap:20px}.panel h3{font-size:17px;font-weight:600}.row{display:flex;align-items:center;justify-content:space-between}.row label{font-size:14px;color:var(--sec)}.row input[type=range]{width:110px;accent-color:var(--work)}.row .val{font-size:13px;font-weight:600;min-width:42px;text-align:right}.switch{position:relative;width:44px;height:24px}.switch input{opacity:0;width:0;height:0}.switch-slider{position:absolute;inset:0;background:var(--ring);border-radius:24px;cursor:pointer;transition:.3s}.switch-slider::before{content:'';position:absolute;width:18px;height:18px;left:3px;bottom:3px;background:var(--sec);border-radius:50%;transition:.3s}.switch input:checked+.switch-slider{background:var(--work)}.switch input:checked+.switch-slider::before{background:#fff;transform:translateX(20px)}.hint{font-size:12px;color:var(--sec);text-align:center;line-height:1.5}.nav{display:flex;gap:4px;width:100%;justify-content:center;margin-bottom:-8px}.nav-btn{padding:6px 14px;border:none;border-radius:14px;background:transparent;color:var(--sec);font-size:12px;cursor:pointer;font-family:inherit;transition:.25s}.nav-btn.active{background:var(--surface);color:var(--fg)}.page{display:none;flex-direction:column;align-items:center;gap:20px;width:100%}.page.show{display:flex}.task-card{background:var(--surface);border-radius:12px;padding:14px;width:100%;display:flex;justify-content:space-between;align-items:center}.input-row{display:flex;gap:8px;width:100%}.input-row input{flex:1;padding:8px 12px;border-radius:8px;border:1px solid var(--ring);background:var(--bg);color:var(--fg);font-family:inherit;font-size:14px;outline:none}.input-row select{padding:8px;border-radius:8px;border:1px solid var(--ring);background:var(--bg);color:var(--fg);font-family:inherit;font-size:13px}.input-row button{padding:8px 16px;border-radius:8px;border:none;background:var(--work);color:#fff;cursor:pointer;font-family:inherit;font-size:13px;font-weight:600}@keyframes pulse{0%,100%{transform:scale(1)}50%{transform:scale(1.06)}}.ring-wrap.finish{animation:pulse .6s ease-in-out 3}</style></head><body><div class=""app""><div class=""nav""><button class=""nav-btn active"" data-page=""timer"">计时</button><button class=""nav-btn"" data-page=""tasks"">任务</button><button class=""nav-btn"" data-page=""stats"">统计</button><button class=""nav-btn"" data-page=""settings"">设置</button></div><div class=""page show"" id=""page-timer""><div class=""tabs""><button class=""tab active"" data-mode=""work"">专注</button><button class=""tab"" data-mode=""short"">短休</button><button class=""tab"" data-mode=""long"">长休</button></div><div class=""ring-wrap"" id=""ring-wrap""><svg class=""ring-svg"" viewBox=""0 0 200 200""><circle class=""ring-bg"" cx=""100"" cy=""100"" r=""90""/><circle class=""ring-bar"" id=""ring-bar"" cx=""100"" cy=""100"" r=""90"" stroke-dasharray=""565.486"" stroke-dashoffset=""0""/></svg><div class=""time"" id=""time"">25:00</div></div><div class=""status"" id=""status"">准备开始</div><div class=""dots"">今日&nbsp;<span class=""dot"" id=""dot0""></span><span class=""dot"" id=""dot1""></span><span class=""dot"" id=""dot2""></span><span class=""dot"" id=""dot3""></span>&nbsp;<span id=""pomo-count"" style=""color:var(--sec);font-size:13px"">0</span></div><div class=""btn-row""><button class=""btn btn-sm"" id=""btn-reset"" title=""重置(R)"">↺</button><button class=""btn btn-play"" id=""btn-toggle"" title=""开始/暂停(空格)"">▶</button><button class=""btn btn-sm"" id=""btn-skip"" title=""跳过(→)"">⏭</button></div></div><div class=""page"" id=""page-tasks""><div class=""input-row""><input id=""task-input"" placeholder=""输入任务名称..."" maxlength=""40""><select id=""task-tag""><option>工作</option><option>学习</option><option>编程</option><option>阅读</option><option>运动</option></select><button id=""task-add"">添加</button></div><div id=""task-list"" style=""width:100%;display:flex;flex-direction:column;gap:8px""></div></div><div class=""page"" id=""page-stats""><div style=""display:flex;gap:8px;width:100%;flex-wrap:wrap;justify-content:center""><div class=""task-card"" style=""flex:1;min-width:80px;flex-direction:column;align-items:center;gap:4px""><span style=""font-size:24px"" id=""stat-pomos"">0</span><span style=""color:var(--sec);font-size:11px"">完成番茄</span></div><div class=""task-card"" style=""flex:1;min-width:80px;flex-direction:column;align-items:center;gap:4px""><span style=""font-size:24px"" id=""stat-minutes"">0</span><span style=""color:var(--sec);font-size:11px"">专注分钟</span></div><div class=""task-card"" style=""flex:1;min-width:80px;flex-direction:column;align-items:center;gap:4px""><span style=""font-size:24px"" id=""stat-streak"">0</span><span style=""color:var(--sec);font-size:11px"">连续天数</span></div></div><div id=""history-list"" style=""width:100%;display:flex;flex-direction:column;gap:6px;max-height:550px;overflow-y:auto""></div></div><div class=""page"" id=""page-settings""><div class=""row""><label>专注时长</label><input type=""range"" id=""s-work"" min=""5"" max=""60"" value=""25"" step=""1""><span class=""val"" id=""sv-work"">25分</span></div><div class=""row""><label>短休时长</label><input type=""range"" id=""s-short"" min=""1"" max=""15"" value=""5"" step=""1""><span class=""val"" id=""sv-short"">5分</span></div><div class=""row""><label>长休时长</label><input type=""range"" id=""s-long"" min=""5"" max=""30"" value=""15"" step=""1""><span class=""val"" id=""sv-long"">15分</span></div><div class=""row""><label>长休间隔</label><input type=""range"" id=""s-interval"" min=""2"" max=""10"" value=""4"" step=""1""><span class=""val"" id=""sv-interval"">4轮</span></div><div class=""row""><label>窗口置顶</label><label class=""switch""><input type=""checkbox"" id=""chk-top""><span class=""switch-slider""></span></label></div><p class=""hint"">空格 开始/暂停 | R 重置 | → 跳过</p></div></div><div class=""overlay"" id=""overlay""><div class=""panel""><h3>🎉 计时结束!</h3><p style=""text-align:center;color:var(--sec)"" id=""finish-msg""></p><button style=""padding:12px;border-radius:10px;border:none;background:var(--work);color:#fff;font-size:16px;cursor:pointer;font-weight:600;font-family:inherit"" id=""btn-confirm"">知道了</button></div></div><script>" + GetEmbeddedJs() + "</script></body></html>";
    }

    string GetEmbeddedJs()
    {
        return @"(function(){var $=s=>document.querySelector(s);var $$=s=>document.querySelectorAll(s);var C=2*Math.PI*90;var cfg={work:25*60,short:5*60,long:15*60,interval:4,topmost:false};var state={mode:'work',remain:cfg.work,total:cfg.work,running:false,timerId:null,pomos:0};var records=[];var tasks=[];var elTime=$('#time'),elRing=$('#ring-bar'),elWrap=$('#ring-wrap'),elStatus=$('#status'),elToggle=$('#btn-toggle'),elResetBtn=$('#btn-reset'),elSkip=$('#btn-skip');var overlay=$('#overlay'),finishMsg=$('#finish-msg'),btnConfirm=$('#btn-confirm');function api(m){try{window.chrome.webview.postMessage(m)}catch(e){console.log(m)}}function load(){try{tasks=JSON.parse(localStorage.getItem('pm-tasks')||'[]')}catch(e){tasks=[]}try{records=JSON.parse(localStorage.getItem('pm-records')||'[]')}catch(e){records=[]}try{var c=JSON.parse(localStorage.getItem('pm-config')||'{}');if(c.work)cfg.work=c.work;if(c.short)cfg.short=c.short;if(c.long)cfg.long=c.long;if(c.interval)cfg.interval=c.interval;if(c.topmost!=null)cfg.topmost=c.topmost}catch(e){}var t=localStorage.getItem('pm-today');if(t){var p=t.split('|');var today=new Date().toISOString().slice(0,10);if(p[0]===today)state.pomos=parseInt(p[1])||0}if(cfg.topmost)api('top-on');}function save(){try{localStorage.setItem('pm-config',JSON.stringify(cfg))}catch(e){}try{localStorage.setItem('pm-records',JSON.stringify(records))}catch(e){}try{localStorage.setItem('pm-tasks',JSON.stringify(tasks))}catch(e){}var today=new Date().toISOString().slice(0,10);try{localStorage.setItem('pm-today',today+'|'+state.pomos)}catch(e){}}function fmt(s){var m=Math.floor(s/60),sec=s%60;return (m<10?'0':'')+m+':'+(sec<10?'0':'')+sec}function accent(){return state.mode==='work'?'var(--work)':state.mode==='short'?'var(--short)':'var(--long)'}function render(){elTime.textContent=fmt(state.remain);elTime.classList.toggle('paused',!state.running);elRing.style.stroke=accent();var p=state.remain/state.total*C;elRing.setAttribute('stroke-dashoffset',C-p);var names={work:'专注中',short:'短休息',long:'长休息'};var rd=state.pomos+1;elStatus.textContent=state.running?names[state.mode]+' · 第'+rd+'轮':(state.remain===state.total?'准备开始':'已暂停');$$('.tab').forEach(function(t){t.classList.toggle('active',t.dataset.mode===state.mode)});elToggle.style.background=accent();elToggle.textContent=state.running?'⏸':'▶';for(var i=0;i<4;i++){var d=$('#dot'+i);d.classList.remove('done','cur');var cy=state.pomos%cfg.interval;if(i<cy)d.classList.add('done');else if(i===cy&&state.mode==='work')d.classList.add('cur')}$('#pomo-count').textContent=state.pomos;var title=fmt(state.remain)+' '+(state.running?'▶':'⏸')+' '+names[state.mode];document.title=title;api('tray:'+title);$('#chk-top').checked=cfg.topmost;$('#s-work').value=cfg.work/60;$('#s-short').value=cfg.short/60;$('#s-long').value=cfg.long/60;$('#s-interval').value=cfg.interval;updateSettingLabels();}function updateSettingLabels(){$('#sv-work').textContent=$('#s-work').value+'分';$('#sv-short').textContent=$('#s-short').value+'分';$('#sv-long').textContent=$('#s-long').value+'分';$('#sv-interval').textContent=$('#s-interval').value+'轮'}function tick(){if(state.remain>0){state.remain--;render();return}finish()}function start(){if(state.running)return;api('bell');if(state.remain<=0){state.total=state.mode==='work'?cfg.work:(state.mode==='short'?cfg.short:cfg.long);state.remain=state.total}state.running=true;state.timerId=setInterval(tick,1000);render()}function pause(){if(!state.running)return;state.running=false;clearInterval(state.timerId);state.timerId=null;render()}function reset(){api('bell');pause();state.total=state.mode==='work'?cfg.work:(state.mode==='short'?cfg.short:cfg.long);state.remain=state.total;render()}function skip(){api('bell');pause();state.remain=0;tick()}function switchMode(m){if(state.mode===m)return;state.mode=m;state.total=m==='work'?cfg.work:(m==='short'?cfg.short:cfg.long);state.remain=state.total;state.running=false;clearInterval(state.timerId);state.timerId=null;render()}function finish(){clearInterval(state.timerId);state.timerId=null;state.running=false;state.remain=0;render();elWrap.classList.add('finish');setTimeout(function(){elWrap.classList.remove('finish')},2000);if(state.mode==='work'){state.pomos++;records.push({time:new Date().toISOString(),mode:'专注',completed:true});api('notify:番茄完成！|已完成 '+state.pomos+' 个番茄，休息吧~');finishMsg.textContent='已完成 '+state.pomos+' 个番茄，休息一下吧~';var next=state.pomos%cfg.interval===0?'long':'short';switchMode(next)}else{var n=state.mode==='long'?'长休息':'短休息';finishMsg.textContent=n+'结束，开始新的番茄吧！';api('notify:休息结束|'+n+'已完成');switchMode('work')}overlay.classList.add('open');try{(new Audio('data:audio/wav;base64,UklGRnoGAABXQVZFZm10IBAAAAABAAEAQB8AAEAfAAABAAgAZGF0YQoGAACAf39/f4B/f3+Af39/gH9/f4CAf39/gIB/f3+Af39/gIB/f3+Af39/gIB/f39/gH9/f4B/f3+Af39/gH9/f4B/f3+AgH9/f4B/f39/f39/f39/f39/f39/f39/gH9/f4B/f3+Af39/gH9/f4B/f3+AgH9/f4B/f3+Af39/gH9/f4CAf39/gIB/f39/f39/f39/f39/f39/gH9/f4B/f3+Af39/gH9/f4CAf39/gH9/f39/f39/f39/f3+Af39/gH9/f4B/f3+Af39/gH9/f4CAf39/gH9/f4B/f3+Af39/gH9/f4B/f3+Af39/gIB/f3+Af39/gIB/f39/f3+Af39/gIB/f3+Af39/f39/gIB/f39/f39/gIB/f3+Af39/gH9/f39/f39/f3+Af39/gH9/f39/f39/f3+Af39/gH9/f39/f39/f39/gH9/f39/f39/f3+Af39/f39/f39/f3+Af39/f39/f39/f3+Af39/f39/f4B/f3+Af39/gH9/f4B/f3+Af39/f39/gH9/f39/f3+Af39/f39/f3+Af39/gH9/f4B/f3+Af39/gH9/f39/f3+Af39/gH9/f4B/f3+Af39/f4B/f39/f39/f4B/f39/f4B/f3+Af39/f39/f39/f39/f3+Af39/gH9/f4B/f3+Af39/f39/f39/f3+Af39/f39/f3+Af39/f39/f3+Af39/gH9/f4B/f3+Af39/gIB/f3+Af39/gH9/f4B/f3+Af39/gIB/f3+Af39/f39/f39/f39/f39/f39/f4B/f3+Af39/f39/f39/f3+Af39/f39/f39/f3+Af39/gH9/f4B/f3+Af39/f39/f39/f3+Af39/f39/f4B/f3+Af39/f4B/f39/f39/f3+Af39/f39/f3+Af39/f39/f39/gH9/f39/f39/f3+Af39/f39/f4B/f3+Af39/f4B/f3+Af39/f39/f39/f3+Af39/f39/f4B/f39/f3+Af39/gH9/f39/f39/f39/gH9/f39/f39/f4B/f3+Af39/f39/f39/f3+Af39/gH9/f39/f39/f3+Af39/gH9/f39/f4B/f39/f3+Af39/f4B/f39/f3+Af39/f4B/f39/f3+Af39/f39/f39/f39/f3+Af39/f39/f39/f39/f3+Af39/f4B/f39/f3+Af39/f39/gH9/gIB/f3+Af39/f3+Af3+Af39/gIB/f3+Af39/gH9/f4B/f39/f39/gH9/f4B/f3+Af39/f4B/f39/f3+Af39/gH9/f4B/f3+Af39/f4B/f3+Af39/gH9/f4B/f3+Af39/gH9/f39/f39/f39/f39/gH9/f39/f39/f39/f39/f39/gH9/f39/f39/f39/f3+Af39/f39/f4B/f3+Af39/gH9/f4CAgICAgICAgICAAAA//8=')).play()}catch(e){}}function applySettings(){cfg.work=parseInt($('#s-work').value)*60;cfg.short=parseInt($('#s-short').value)*60;cfg.long=parseInt($('#s-long').value)*60;cfg.interval=parseInt($('#s-interval').value);cfg.topmost=$('#chk-top').checked;save();api(cfg.topmost?'top-on':'top-off');if(!state.running){state.total=state.mode==='work'?cfg.work:(state.mode==='short'?cfg.short:cfg.long);state.remain=state.total}render()}function renderTasks(){var list=$('#task-list');list.innerHTML='';tasks.forEach(function(t,i){var card=document.createElement('div');card.className='task-card';card.innerHTML='<div><div style=""font-weight:600"">'+t.name+'</div><div style=""color:var(--sec);font-size:12px"">'+t.tag+' · 番茄:'+t.pomos+' · '+t.mins+'分钟</div></div><div style=""display:flex;gap:4px""><button class=""del-btn"" data-idx=""'+i+'"">✕</button></div>';list.appendChild(card)});document.querySelectorAll('.del-btn').forEach(function(b){b.addEventListener('click',function(){var i=parseInt(this.dataset.idx);tasks.splice(i,1);save();renderTasks()})})}function renderStats(){$('#stat-pomos').textContent=state.pomos;$('#stat-minutes').textContent=state.pomos*cfg.work/60;var s=0;for(var i=0;i<365;i++){var d=new Date();d.setDate(d.getDate()-i);var ds=d.toISOString().slice(0,10);var found=false;records.forEach(function(r){if(r.time&&r.time.slice(0,10)===ds&&r.completed)found=true});if(found)s++;else break}$('#stat-streak').textContent=s;var hl=$('#history-list');hl.innerHTML='';var recs=records.slice().reverse().slice(0,30);recs.forEach(function(r){var div=document.createElement('div');div.className='task-card';div.innerHTML='<span>'+r.mode+'</span><span style=""color:var(--sec);font-size:12px"">'+(r.time||'').slice(0,16)+'</span>';hl.appendChild(div)})}elToggle.addEventListener('click',function(){state.running?pause():start()});elResetBtn.addEventListener('click',reset);elSkip.addEventListener('click',skip);btnConfirm.addEventListener('click',function(){overlay.classList.remove('open')});$$('.tab').forEach(function(t){t.addEventListener('click',function(){switchMode(t.dataset.mode)})});$$('.nav-btn').forEach(function(n){n.addEventListener('click',function(){$$('.nav-btn').forEach(function(x){x.classList.remove('active')});n.classList.add('active');$$('.page').forEach(function(p){p.classList.remove('show')});var pageId='page-'+n.dataset.page;$('#'+pageId).classList.add('show');if(n.dataset.page==='tasks')renderTasks();if(n.dataset.page==='stats')renderStats()})});document.addEventListener('keydown',function(e){if(e.target.tagName==='INPUT'||e.target.tagName==='SELECT')return;switch(e.key){case' ':e.preventDefault();state.running?pause():start();break;case'r':case'R':reset();break;case'ArrowRight':skip();break}});$('#s-work').addEventListener('input',function(){cfg.work=parseInt(this.value)*60;save();updateSettingLabels();if(!state.running){state.total=cfg.work;state.remain=cfg.work;render()}});$('#s-short').addEventListener('input',function(){cfg.short=parseInt(this.value)*60;save();updateSettingLabels()});$('#s-long').addEventListener('input',function(){cfg.long=parseInt(this.value)*60;save();updateSettingLabels()});$('#s-interval').addEventListener('input',function(){cfg.interval=parseInt(this.value);save();updateSettingLabels()});$('#chk-top').addEventListener('change',function(){cfg.topmost=this.checked;save();api(this.checked?'top-on':'top-off')});$('#task-add').addEventListener('click',function(){var name=$('#task-input').value.trim();if(!name)return;var tag=$('#task-tag').value;tasks.push({name:name,tag:tag,pomos:0,mins:0});$('#task-input').value='';save();renderTasks()});load();render();})();";
    }
}

// 窗口拖拽辅助
internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
}
