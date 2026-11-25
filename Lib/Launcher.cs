// 元素面板.exe 的源码
// 元素面板.exe 是一个窗口: 窗口里用 WebView2 显示 Page.html, 项目文件由 元素面板.exe 自己读写
// 没有后台服务: 窗口开着就是在运行, 窗口关掉就是退出
// WebView2 = Windows 自带的 Edge 内核; Lib\WebView2 里是调用 Edge 内核用的三个库文件
// 重新生成 元素面板.exe ( 在 B_ElementPanel 目录里运行 ):
// C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /codepage:65001 /target:winexe /platform:x64 /win32icon:Lib\Icon.ico /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Lib\WebView2\Microsoft.Web.WebView2.Core.dll /reference:Lib\WebView2\Microsoft.Web.WebView2.WinForms.dll /out:元素面板.exe Lib\Launcher.cs
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

static class Launcher
{
    public static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

    public static void Setup()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) => { string f = Path.Combine(Dir, "Lib", "WebView2", new AssemblyName(e.Name).Name + ".dll"); return File.Exists(f) ? Assembly.LoadFrom(f) : null; };   // WebView2 的 dll 放在 Lib\WebView2 里
        SetProcessDPIAware();   // 不声明的话, 屏幕缩放不是 100% 时窗口里的字是糊的
        Application.EnableVisualStyles();
    }

    [STAThread]
    static void Main()
    {
        bool first;
        using (new Mutex(true, "ElementPanel_Window", out first))
        {
            if (first) { Setup(); Run(); return; }
            // 元素面板已经开着: 不开第二个窗口, 把已经开着的窗口拿到最前面
            int me = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
            {
                IntPtr h = p.MainWindowHandle;
                if (p.Id == me || h == IntPtr.Zero) continue;
                if (IsIconic(h)) ShowWindow(h, 9);   // 9 = 从最小化恢复
                SetForegroundWindow(h);
            }
        }
    }

    // 单独一个函数: Setup 告诉 .NET 去哪里找 WebView2 的 dll 之后, 才能碰到用了 WebView2 的 Panel
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run()
    {
        try { Application.Run(new Panel()); }
        catch (Exception e) { MessageBox.Show("没有启动: " + e.Message, "元素面板"); }
    }
}

// Page.html 里的 store 就是这个对象: Page.html 调用下面的函数读写项目文件, 每个函数都当场做完再返回
// 一个项目一个文件: Projects\项目名.json
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public class Store
{
    readonly string dir;

    public Store(string dir)
    {
        this.dir = dir;
        Directory.CreateDirectory(Path.Combine(dir, "_Trash"));
    }

    // 项目名 -> 项目文件的路径; 项目名不能当文件名时报错
    string F(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name[0] == '.' || name[0] == '_') throw new ArgumentException("项目名不能用: " + name);
        return Path.Combine(dir, name + ".json");
    }

    // 杀毒软件正在扫描文件的那一瞬间, 读写会失败: 隔 30 毫秒再试, 一共试 10 次
    static T Retry<T>(Func<T> act)
    {
        for (int i = 0; ; i++)
        {
            try { return act(); }
            catch (Exception e) { if (i == 9 || !(e is IOException || e is UnauthorizedAccessException)) throw; Thread.Sleep(30); }
        }
    }

    // 全部项目的名字, 一行一个, 最近保存的排在前面
    public string List()
    {
        return string.Join("\n", new DirectoryInfo(dir).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Select(f => Path.GetFileNameWithoutExtension(f.Name)));
    }

    // 项目的内容; 没有这个项目时是空字符串
    public string Load(string name)
    {
        string f = F(name);
        return File.Exists(f) ? Retry(() => File.ReadAllText(f)) : "";
    }

    // 先写进临时文件, 再用临时文件换掉项目文件: 写到一半断电也不损坏项目文件
    public void Save(string name, string text)
    {
        string f = F(name), tmp = f + ".tmp";
        try { Retry(() => { File.WriteAllText(tmp, text); if (File.Exists(f)) File.Replace(tmp, f, null); else File.Move(tmp, f); return 0; }); }
        catch (Exception e) { MessageBox.Show("没有保存: " + e.Message, "元素面板"); }
    }

    // 已经有同名的项目时不改名, 返回 false
    public bool Rename(string from, string to)
    {
        string f = F(from), t = F(to);
        if (File.Exists(t) && !string.Equals(f, t, StringComparison.OrdinalIgnoreCase)) return false;   // 只改大小写时 t 和 f 是同一个文件, 不算同名
        if (File.Exists(f)) Retry(() => { File.Move(f, t); return 0; });
        return true;
    }

    // 删除: 项目文件搬进 Projects\_Trash, 不真的删掉
    public void Delete(string name)
    {
        string f = F(name);
        if (File.Exists(f)) Retry(() => { File.Move(f, Path.Combine(dir, "_Trash", name + " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".json")); return 0; });
    }
}

static class Host
{
    // 把 web 接好: 页面文件从 dir 读, 项目文件在 dir\Projects, Page.html 发来的消息交给 onMessage
    // profile: Edge 内核存放自己数据的文件夹; args: 额外交给 Edge 内核的启动参数, 只有自测用
    public static async Task Attach(WebView2 web, string dir, string profile, string args, Action<string> onMessage)
    {
        CoreWebView2Environment.SetLoaderDllFolderPath(Path.Combine(dir, "Lib", "WebView2"));
        var env = await CoreWebView2Environment.CreateAsync(null, profile, new CoreWebView2EnvironmentOptions(args));
        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2;
        core.Settings.IsZoomControlEnabled = false;               // Ctrl + 滚轮不缩放整个页面
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;   // 关掉浏览器自己的快捷键: F5 刷新, Ctrl+P 打印, Ctrl+F 查找; 复制 粘贴 撤销 不受影响
        core.Settings.IsNonClientRegionSupportEnabled = true;     // 页面里写了 app-region: drag 的那一条, 按住可以拖动窗口
        core.Settings.IsReputationCheckingRequired = false;       // 页面是本机的文件, 不需要 SmartScreen 联网检查网址; 不关的话, 实测页面每次要多等 2 秒才出来
        core.SetVirtualHostNameToFolderMapping("elementpanel.example", dir, CoreWebView2HostResourceAccessKind.Allow);   // 页面里 https://elementpanel.example/ 开头的地址, 读到的是 dir 里的文件
        core.AddHostObjectToScript("store", new Store(Path.Combine(dir, "Projects")));
        core.WebMessageReceived += (s, m) => onMessage(m.TryGetWebMessageAsString());
        core.Navigate("https://elementpanel.example/Page.html?" + File.GetLastWriteTimeUtc(Path.Combine(dir, "Page.html")).Ticks);   // 问号后面是 Page.html 的修改时间: Page.html 改过之后, Edge 内核不会拿出以前存下的旧页面
    }
}

class Panel : Form
{
    readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill };
    bool full;
    // 没有全屏时窗口的样子: place 是不最大化时的位置和大小, max 是有没有最大化; 关窗口时写进 Window.txt, 下次打开时读出
    Rectangle place;
    bool max = true;
    static readonly string Memo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElementPanel", "Window.txt");

    public Panel()
    {
        Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
        place = Rectangle.Inflate(area, -area.Width / 8, -area.Height / 8);
        try
        {
            int[] m = File.ReadAllText(Memo).Split(',').Select(int.Parse).ToArray();
            var r = new Rectangle(m[0], m[1], m[2], m[3]);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r))) { place = r; max = m[4] == 1; }   // 记下的位置已经不在任何屏幕上时不用
        }
        catch { }   // 第一次打开, 还没有 Window.txt
        FormClosing += (s, e) =>
        {
            if (!full) Remember();
            try { Directory.CreateDirectory(Path.GetDirectoryName(Memo)); File.WriteAllText(Memo, string.Join(",", place.X, place.Y, place.Width, place.Height, max ? 1 : 0)); } catch { }
        };
        Text = "元素面板";
        Icon = new Icon(Path.Combine(Launcher.Dir, "Lib", "Icon.ico"));
        BackColor = Color.FromArgb(0xEC, 0xE8, 0xE1);
        StartPosition = FormStartPosition.Manual;
        Bounds = place;
        KeyPreview = true;
        Controls.Add(web);
        KeyDown += (s, e) => { if (e.KeyCode == Keys.F11) Toggle(); };
        Load += async (s, e) =>
        {
            Full();   // 打开就是全屏
            try
            {
                // Page.html 发来的消息: F11 是光标在页面里时按了 F11; Min Max Quit 是单击了窗口右上角的三个圆按钮
                await Host.Attach(web, Launcher.Dir, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElementPanel"), null, t =>
                {
                    if (t == "F11") Toggle();
                    if (t == "Min") WindowState = FormWindowState.Minimized;
                    if (t == "Max") WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                    if (t == "Quit") BeginInvoke(new Action(Close));
                });
                web.CoreWebView2.NavigationCompleted += (a, b) => Tell();
                web.CoreWebView2.DocumentTitleChanged += (a, b) => Text = web.CoreWebView2.DocumentTitle;   // 任务栏上的标题跟着页面的标题走, 页面的标题里有当前项目的名字
            }
            catch (Exception x) { MessageBox.Show("页面没有打开: " + x.Message, "元素面板"); }
        };
    }

    // 窗口一直没有系统自带的标题栏 ( 标题栏 = WS_CAPTION ): 不是全屏时, Page.html 在右上角画三个圆按钮, 顶上留一条可以拖动窗口; 窗口的边仍然可以拖着改大小
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.Style &= ~0x00C00000; return p; } }

    // 不最大化的普通窗口: 系统在窗口顶上留一条白边 ( 用来拖着改高度的边 ), 这里把窗口里面的区域一直顶到窗口的上沿, 白边就没有了
    // 0x83 = WM_NCCALCSIZE, 系统用这条消息问窗口里面的区域有多大; 左 右 下 三条边照旧, 仍然可以拖着改大小
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x83 && m.WParam != IntPtr.Zero && !IsZoomed(m.HWnd))
        {
            int top = Marshal.ReadInt32(m.LParam, 4);
            base.WndProc(ref m);
            Marshal.WriteInt32(m.LParam, 4, top);
            return;
        }
        base.WndProc(ref m);
    }

    // 告诉 Page.html 现在是不是全屏: 不是全屏时 Page.html 才画出右上角的三个圆按钮
    void Tell() { if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsString(full ? "full" : "window"); }

    // 记下没有全屏时窗口的样子
    void Remember()
    {
        if (WindowState == FormWindowState.Minimized) return;
        max = WindowState == FormWindowState.Maximized;
        place = max ? RestoreBounds : Bounds;
    }

    // 全屏: 没有标题栏, 盖住整个屏幕, 包括任务栏
    void Full()
    {
        full = true;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Normal;
        Bounds = Screen.FromRectangle(place).Bounds;
        Tell();
    }

    // F11: 全屏时回到进全屏之前的样子 ( 原来最大化就还是最大化, 原来是多大的窗口就还是多大 ); 没有全屏时先记下样子再进全屏
    void Toggle()
    {
        if (!full) { Remember(); Full(); return; }
        full = false;
        FormBorderStyle = FormBorderStyle.Sizable;
        Bounds = place;
        if (max) WindowState = FormWindowState.Maximized;
        Tell();
    }
}
