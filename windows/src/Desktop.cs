using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DesktopDance
{
    /// <summary>一个参加跳舞的图标。只读取图标和名字，绝不改动文件本身。</summary>
    public sealed class DancerItem
    {
        public string Name = "";
        public string Key = "";           // 外壳里的解析名（路径或 ::{CLSID}），只用来比对
        public bool IsGuest;              // true = 不在桌面上、借来显示的应用图标
        public bool HasPos;               // 读到了真实位置
        public double PxX, PxY;           // 真实图标中心（屏幕像素）
        public Vec Home;                  // 原位（舞台坐标）
        public BitmapSource? Icon;
    }

    /// <summary>主显示器的尺寸信息，全部是物理像素。</summary>
    public sealed class ScreenInfo
    {
        public int MonX, MonY, MonW = 1920, MonH = 1080;
        public int WorkX, WorkY, WorkW = 1920, WorkH = 1040;
        public int VirtX, VirtY, VirtW = 1920, VirtH = 1080;
        public double Scale = 1;          // 缩放比例（100% = 1，150% = 1.5）

        public static ScreenInfo Read()
        {
            var s = new ScreenInfo();
            try
            {
                IntPtr mon = Native.MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1 /* 主显示器 */);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (mon != IntPtr.Zero && Native.GetMonitorInfo(mon, ref mi))
                {
                    s.MonX = mi.rcMonitor.Left; s.MonY = mi.rcMonitor.Top;
                    s.MonW = mi.rcMonitor.Right - mi.rcMonitor.Left; s.MonH = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                    s.WorkX = mi.rcWork.Left; s.WorkY = mi.rcWork.Top;
                    s.WorkW = mi.rcWork.Right - mi.rcWork.Left; s.WorkH = mi.rcWork.Bottom - mi.rcWork.Top;
                    if (Native.GetDpiForMonitor(mon, 0, out uint dx, out uint _) == 0 && dx >= 48) s.Scale = dx / 96.0;
                }
                s.VirtX = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
                s.VirtY = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
                s.VirtW = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
                s.VirtH = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
                if (s.VirtW <= 0 || s.VirtH <= 0) { s.VirtX = s.MonX; s.VirtY = s.MonY; s.VirtW = s.MonW; s.VirtH = s.MonH; }
            }
            catch (Exception ex) { Log.Write("读屏幕信息失败：" + ex.Message); }
            return s;
        }

        public override string ToString() =>
            "主屏 " + MonW + "x" + MonH + " @(" + MonX + "," + MonY + ")，工作区 " + WorkW + "x" + WorkH + " @(" + WorkX + "," + WorkY +
            ")，缩放 " + (Scale * 100).ToString("0") + "%，全部屏幕 " + VirtW + "x" + VirtH + " @(" + VirtX + "," + VirtY + ")";
    }

    public sealed class LoadResult
    {
        public List<DancerItem> Items = new List<DancerItem>();
        public int DesktopCount;
        public ScreenInfo Screen = new ScreenInfo();
        public double IconDip = 48, CellW = 76, CellH = 100;
        public bool PositionsFromShell;
        public int OffScreenCount;                  // 在别的显示器上、没参加的桌面图标
        public BitmapSource? Curtain;               // 铺好的壁纸，大小正好是主屏像素
        public Color CurtainColor = Color.FromRgb(20, 20, 20);
        public string CurtainNote = "";             // 写日志用，中文
        public Msg CurtainProblem = Msg.None;       // 壁纸铺不上、只能用纯色盖住时给用户看的话；正常时是空的
        public Dictionary<string, POINT> Snapshot = new Dictionary<string, POINT>();   // 开始前每个图标的位置（结束后只读复核）
        public List<string> Diag = new List<string>();
    }

    public sealed class NameCatalog
    {
        public List<string> Desktop = new List<string>();
        public List<string> Guests = new List<string>();
    }

    /// <summary>
    /// 读桌面：图标的名字、图片、真实位置。全部是只读操作。
    /// 位置和名字问的是资源管理器里「桌面」这个视图本身（IFolderView），所以个人桌面、公共桌面、
    /// 「此电脑」「回收站」这类图标都在里面，和你眼睛看到的一致。问不到时退回去直接读两个桌面文件夹。
    /// 这些函数要在一个单独的 STA 线程里调用（见 StaWorker）。
    /// </summary>
    public static class DesktopLoader
    {
        public const int MaxDesktopItems = 140;

        sealed class ViewItem
        {
            public string Name = "", Key = "";
            public IntPtr Pidl;
            public POINT Pos;
        }

        sealed class DesktopView
        {
            public List<ViewItem> Items = new List<ViewItem>();
            public POINT Spacing;
            public RECT ListRect;
            public bool HasList;
            public bool IconsVisible = true;

            public void FreePidls()
            {
                foreach (var it in Items)
                {
                    if (it.Pidl != IntPtr.Zero) { Marshal.FreeCoTaskMem(it.Pidl); it.Pidl = IntPtr.Zero; }
                }
            }
        }

        sealed class GuestEntry
        {
            public string Name = "";
            public IShellItem? Item;
            public string Path = "";
        }

        // ---------- 对外 ----------

        public static LoadResult Load(int target, string ballName)
        {
            var r = new LoadResult();
            r.Screen = ScreenInfo.Read();
            ScreenInfo sc = r.Screen;
            double scale = sc.Scale;
            r.Diag.Add(sc.ToString());

            int regIcon = ReadDesktopIconSize();
            r.IconDip = M.Clamp(regIcon > 0 ? regIcon : 48, 24, 128);
            int iconPx = (int)Math.Round(r.IconDip * scale);
            int bmpPx = Math.Min(256, Math.Max(32, (int)Math.Ceiling(iconPx * 1.5)));
            r.Diag.Add("桌面图标大小：注册表 " + (regIcon > 0 ? regIcon.ToString() : "读不到，按 48") + "，屏幕上约 " + iconPx + " 像素");
            r.CellW = r.IconDip + 28;
            r.CellH = r.IconDip + 52;

            var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            DesktopView? view = ReadDesktopView(r.Diag);
            try
            {
                if (view != null && view.IconsVisible)
                {
                    r.PositionsFromShell = true;
                    if (view.Spacing.X > 8 && view.Spacing.Y > 8) { r.CellW = view.Spacing.X / scale; r.CellH = view.Spacing.Y / scale; }
                    int ox = view.HasList ? view.ListRect.Left : sc.VirtX;
                    int oy = view.HasList ? view.ListRect.Top : sc.VirtY;
                    r.Diag.Add("桌面视图：" + view.Items.Count + " 个图标，格子 " + view.Spacing.X + "x" + view.Spacing.Y + "，列表窗口原点 (" + ox + "," + oy + ")");
                    foreach (var vi in view.Items)
                    {
                        if (vi.Key.Length > 0) r.Snapshot[vi.Key] = vi.Pos;
                        double cx = ox + vi.Pos.X + iconPx / 2.0, cy = oy + vi.Pos.Y + iconPx / 2.0;
                        bool onPrimary = cx >= sc.MonX && cx < sc.MonX + sc.MonW && cy >= sc.MonY && cy < sc.MonY + sc.MonH;
                        if (!onPrimary) { r.OffScreenCount++; continue; }
                        if (r.Items.Count >= MaxDesktopItems) continue;
                        var item = new DancerItem { Name = vi.Name, Key = vi.Key, IsGuest = false, HasPos = true, PxX = cx, PxY = cy };
                        item.Home = new Vec((cx - sc.MonX) / scale, (sc.MonY + sc.MonH - cy) / scale);
                        IShellItem? si = null;
                        try
                        {
                            Guid iid = typeof(IShellItem).GUID;
                            if (Native.SHCreateItemFromIDList(vi.Pidl, ref iid, out si) == 0 && si != null)
                                item.Icon = LoadImage(si, bmpPx, false);
                        }
                        catch (Exception ex) { r.Diag.Add("取图标失败 " + vi.Name + "：" + ex.Message); }
                        finally { Release(si); }
                        r.Items.Add(item);
                        names.Add(item.Name);
                    }
                    if (r.OffScreenCount > 0) r.Diag.Add("有 " + r.OffScreenCount + " 个桌面图标在别的显示器上，不参加");
                }
                else if (view != null)
                {
                    r.Diag.Add("桌面图标被设成了不显示（右键桌面 → 查看 → 显示桌面图标 没勾），按空桌面处理");
                }
                else
                {
                    // 退路：直接读两个桌面文件夹。拿不到位置，副本从默认网格出发。
                    r.Diag.Add("问不到桌面视图，改为直接读桌面文件夹（没有真实位置）");
                    foreach (string path in DesktopFiles())
                    {
                        if (r.Items.Count >= MaxDesktopItems) break;
                        IShellItem? si = ItemFromPath(path);
                        if (si == null) continue;
                        try
                        {
                            var item = new DancerItem { Name = DisplayName(si), Key = path, IsGuest = false, HasPos = false };
                            if (item.Name.Length == 0) item.Name = System.IO.Path.GetFileNameWithoutExtension(path);
                            item.Icon = LoadImage(si, bmpPx, false);
                            r.Items.Add(item);
                            names.Add(item.Name);
                        }
                        finally { Release(si); }
                    }
                }
            }
            finally { view?.FreePidls(); }
            r.DesktopCount = r.Items.Count;

            // 补位：桌面图标不够 target 个时，借已安装应用的图标（只显示，不建任何文件）
            int need = Math.Max(0, target - r.DesktopCount);
            string ballKey = (ballName ?? "").Trim();
            bool hasBall = ballKey.Length == 0 || r.Items.Exists(it => Matches(it.Name, ballKey));
            if (need > 0 || !hasBall)
            {
                List<GuestEntry> guests = ListGuests(r.Diag);
                try
                {
                    int added = 0;
                    foreach (var g in guests)
                    {
                        if (added >= need) break;
                        if (names.Contains(g.Name)) continue;
                        var item = MakeGuest(g, bmpPx);
                        if (item == null) continue;
                        r.Items.Add(item);
                        names.Add(item.Name);
                        added++;
                    }
                    // 选来当篮球的那个不在这批图标里时，把它单独补进来
                    if (ballKey.Length > 0 && !r.Items.Exists(it => Matches(it.Name, ballKey)))
                    {
                        GuestEntry? g = guests.Find(x => Matches(x.Name, ballKey));
                        DancerItem? item = g != null ? MakeGuest(g, bmpPx) : null;
                        if (item != null) r.Items.Add(item);
                        else r.Diag.Add("没找到名字里带「" + ballKey + "」的图标，篮球改用第一个图标");
                    }
                    r.Diag.Add("可借用的应用图标 " + guests.Count + " 个，用了 " + (r.Items.Count - r.DesktopCount) + " 个");
                }
                finally
                {
                    foreach (var g in guests) { Release(g.Item); g.Item = null; }
                }
            }

            int noIcon = r.Items.FindAll(it => it.Icon == null).Count;
            if (noIcon > 0) r.Diag.Add("有 " + noIcon + " 个图标没取到图片，用灰色方块代替");

            // 遮罩用的壁纸
            try
            {
                Wallpaper.Compose(r);
            }
            catch (Exception ex)
            {
                r.Curtain = null;
                r.CurtainNote = "壁纸处理失败，用纯色盖住真实图标";
                r.CurtainProblem = new Msg(r.CurtainNote, "Could not process the wallpaper; a plain color covers the real icons");
                r.Diag.Add("壁纸处理失败：" + ex.GetType().Name + " " + ex.Message);
            }
            return r;
        }

        /// <summary>只要名字（给「篮球」下拉菜单用）。</summary>
        public static NameCatalog ListNames()
        {
            var cat = new NameCatalog();
            var diag = new List<string>();
            DesktopView? view = ReadDesktopView(diag);
            try
            {
                if (view != null && view.IconsVisible)
                {
                    foreach (var vi in view.Items) if (vi.Name.Length > 0) cat.Desktop.Add(vi.Name);
                }
                else if (view == null)
                {
                    foreach (string path in DesktopFiles())
                    {
                        IShellItem? si = ItemFromPath(path);
                        if (si == null) continue;
                        try { string n = DisplayName(si); if (n.Length > 0) cat.Desktop.Add(n); }
                        finally { Release(si); }
                    }
                }
            }
            finally { view?.FreePidls(); }
            List<GuestEntry> guests = ListGuests(diag);
            var seen = new HashSet<string>(cat.Desktop, StringComparer.CurrentCultureIgnoreCase);
            foreach (var g in guests)
            {
                if (seen.Add(g.Name)) cat.Guests.Add(g.Name);
                Release(g.Item);
                g.Item = null;
            }
            foreach (string d in diag) Log.Write("  " + d);
            return cat;
        }

        /// <summary>只读：现在每个桌面图标在哪。问不到返回 null。</summary>
        public static Dictionary<string, POINT>? ReadPositions()
        {
            var diag = new List<string>();
            DesktopView? view = ReadDesktopView(diag);
            if (view == null) return null;
            try
            {
                var d = new Dictionary<string, POINT>();
                foreach (var vi in view.Items) if (vi.Key.Length > 0) d[vi.Key] = vi.Pos;
                return d;
            }
            finally { view.FreePidls(); }
        }

        public static bool Matches(string name, string key) =>
            key.Length > 0 && name.IndexOf(key, StringComparison.CurrentCultureIgnoreCase) >= 0;

        // ---------- 桌面视图 ----------

        static DesktopView? ReadDesktopView(List<string> diag)
        {
            object? swObj = null, disp = null, browserObj = null, viewObj = null;
            try
            {
                Type? t = Type.GetTypeFromCLSID(ShellGuids.CLSID_ShellWindows, false);
                if (t == null) { diag.Add("桌面视图：系统里没有 ShellWindows"); return null; }
                swObj = Activator.CreateInstance(t);
                var sw = (IShellWindows)swObj!;
                object? loc = 0;                     // CSIDL_DESKTOP
                int hwnd;
                try
                {
                    object? unused = new object();
                    disp = sw.FindWindowSW(ref loc, ref unused, ShellGuids.SWC_DESKTOP, out hwnd, ShellGuids.SWFO_NEEDDISPATCH);
                }
                catch (Exception ex1)
                {
                    diag.Add("桌面视图：第一次查找失败（" + ex1.GetType().Name + " " + ex1.Message + "），换一种参数再试");
                    object? none = null;
                    loc = 0;
                    disp = sw.FindWindowSW(ref loc, ref none, ShellGuids.SWC_DESKTOP, out hwnd, ShellGuids.SWFO_NEEDDISPATCH);
                }
                if (disp == null) { diag.Add("桌面视图：没找到桌面窗口（资源管理器没在运行？）"); return null; }

                var sp = (IComServiceProvider)disp;
                Guid sid = ShellGuids.SID_STopLevelBrowser;
                Guid iidBrowser = typeof(IShellBrowser).GUID;
                int hr = sp.QueryService(ref sid, ref iidBrowser, out browserObj);
                if (hr != 0 || browserObj == null) { diag.Add("桌面视图：QueryService 失败 0x" + hr.ToString("X8")); return null; }
                var browser = (IShellBrowser)browserObj;
                hr = browser.QueryActiveShellView(out viewObj);
                if (hr != 0 || viewObj == null) { diag.Add("桌面视图：QueryActiveShellView 失败 0x" + hr.ToString("X8")); return null; }

                var dv = new DesktopView();
                IntPtr defView = IntPtr.Zero;
                try { ((IShellView)viewObj).GetWindow(out defView); } catch { }
                if (defView != IntPtr.Zero)
                {
                    IntPtr lv = Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
                    if (lv != IntPtr.Zero)
                    {
                        dv.HasList = Native.GetWindowRect(lv, out dv.ListRect);
                        dv.IconsVisible = Native.IsWindowVisible(lv);
                    }
                }

                var fv = (IFolderView)viewObj;
                hr = fv.ItemCount(ShellGuids.SVGIO_ALLVIEW, out int count);
                if (hr != 0) { diag.Add("桌面视图：ItemCount 失败 0x" + hr.ToString("X8")); return null; }
                fv.GetSpacing(out dv.Spacing);
                for (int i = 0; i < count && i < 2000; i++)
                {
                    if (fv.Item(i, out IntPtr pidl) != 0 || pidl == IntPtr.Zero) continue;
                    var vi = new ViewItem { Pidl = pidl };
                    if (fv.GetItemPosition(pidl, out vi.Pos) != 0) { Marshal.FreeCoTaskMem(pidl); continue; }
                    vi.Name = NameFromPidl(pidl, ShellGuids.SIGDN_NORMALDISPLAY);
                    vi.Key = NameFromPidl(pidl, ShellGuids.SIGDN_DESKTOPABSOLUTEPARSING);
                    if (vi.Key.Length == 0) vi.Key = "名字:" + vi.Name;
                    dv.Items.Add(vi);
                }
                return dv;
            }
            catch (Exception ex)
            {
                diag.Add("桌面视图：出错 " + ex.GetType().Name + " " + ex.Message);
                return null;
            }
            finally
            {
                Release(viewObj); Release(browserObj); Release(disp); Release(swObj);
            }
        }

        static string NameFromPidl(IntPtr pidl, uint sigdn)
        {
            try
            {
                if (Native.SHGetNameFromIDList(pidl, sigdn, out IntPtr p) != 0 || p == IntPtr.Zero) return "";
                try { return Marshal.PtrToStringUni(p) ?? ""; }
                finally { Marshal.FreeCoTaskMem(p); }
            }
            catch { return ""; }
        }

        static string DisplayName(IShellItem item)
        {
            try
            {
                if (item.GetDisplayName(ShellGuids.SIGDN_NORMALDISPLAY, out IntPtr p) != 0 || p == IntPtr.Zero) return "";
                try { return Marshal.PtrToStringUni(p) ?? ""; }
                finally { Marshal.FreeCoTaskMem(p); }
            }
            catch { return ""; }
        }

        static IShellItem? ItemFromPath(string path)
        {
            try
            {
                Guid iid = typeof(IShellItem).GUID;
                return Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItem? si) == 0 ? si : null;
            }
            catch { return null; }
        }

        static void Release(object? o)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { }
        }

        /// <summary>个人桌面 + 公共桌面里看得见的文件（跳过隐藏和系统文件，比如 desktop.ini）。</summary>
        static List<string> DesktopFiles()
        {
            var list = new List<string>();
            var dirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            };
            foreach (string dir in dirs)
            {
                try
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    foreach (string p in Directory.EnumerateFileSystemEntries(dir))
                    {
                        try
                        {
                            FileAttributes a = File.GetAttributes(p);
                            if ((a & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                            list.Add(p);
                        }
                        catch { }
                    }
                }
                catch { }
            }
            list.Sort(StringComparer.CurrentCultureIgnoreCase);
            return list;
        }

        static int ReadDesktopIconSize()
        {
            try
            {
                using RegistryKey? k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Bags\1\Desktop");
                object? v = k?.GetValue("IconSize");
                if (v is int i && i >= 16 && i <= 256) return i;
            }
            catch { }
            return 0;
        }

        // ---------- 补位的应用图标 ----------

        static readonly string[] SkipWords =
        {
            "uninstall", "卸载", "readme", "read me", "help", "帮助", "manual", "手册", "documentation", "文档",
            "license", "许可", "website", "网站", "release notes", "更新日志", "反馈", "feedback", "修复", "repair",
        };

        static bool GoodGuestName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 60) return false;
            string lower = name.ToLowerInvariant();
            foreach (string w in SkipWords) if (lower.Contains(w)) return false;
            if (lower.Contains("http") || lower.EndsWith(".txt") || lower.EndsWith(".chm") || lower.EndsWith(".html") || lower.EndsWith(".url")) return false;
            return true;
        }

        /// <summary>
        /// 已安装的应用。先问系统的「应用」虚拟文件夹（开始菜单「所有应用」那一份，含商店应用）；
        /// 问不到或太少时再扫开始菜单里的快捷方式。只读，不创建任何东西。
        /// </summary>
        static List<GuestEntry> ListGuests(List<string> diag)
        {
            var list = new List<GuestEntry>();
            var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            IShellItem? folder = null;
            object? enObj = null;
            try
            {
                folder = ItemFromPath("shell:AppsFolder");
                if (folder != null)
                {
                    Guid bhid = ShellGuids.BHID_EnumItems;
                    Guid iidEnum = typeof(IEnumShellItems).GUID;
                    int hr = folder.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out enObj);
                    if (hr == 0 && enObj != null)
                    {
                        var en = (IEnumShellItems)enObj;
                        for (int guard = 0; guard < 4000; guard++)
                        {
                            hr = en.Next(1, out IShellItem? it, out uint fetched);
                            if (hr != 0 || fetched == 0 || it == null) break;
                            string name = DisplayName(it);
                            if (GoodGuestName(name) && seen.Add(name)) list.Add(new GuestEntry { Name = name, Item = it });
                            else Release(it);
                        }
                    }
                    else diag.Add("应用列表：枚举失败 0x" + hr.ToString("X8"));
                }
                else diag.Add("应用列表：打不开 shell:AppsFolder");
            }
            catch (Exception ex) { diag.Add("应用列表：出错 " + ex.GetType().Name + " " + ex.Message); }
            finally { Release(enObj); Release(folder); }

            if (list.Count < 8)
            {
                int before = list.Count;
                var dirs = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                };
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (string dir in dirs)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                        foreach (string p in Directory.EnumerateFiles(dir, "*.lnk", opts))
                        {
                            string name = System.IO.Path.GetFileNameWithoutExtension(p);
                            if (GoodGuestName(name) && seen.Add(name)) list.Add(new GuestEntry { Name = name, Path = p });
                        }
                    }
                    catch { }
                }
                diag.Add("应用列表：从开始菜单快捷方式补了 " + (list.Count - before) + " 个");
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }

        static DancerItem? MakeGuest(GuestEntry g, int bmpPx)
        {
            IShellItem? si = g.Item;
            bool own = false;
            if (si == null && g.Path.Length > 0) { si = ItemFromPath(g.Path); own = true; }
            if (si == null) return null;
            try
            {
                BitmapSource? icon = LoadImage(si, bmpPx, true);
                if (icon == null) return null;
                return new DancerItem { Name = g.Name, Key = "guest:" + g.Name, IsGuest = true, HasPos = false, Icon = icon };
            }
            catch { return null; }
            finally { if (own) Release(si); }
        }

        // ---------- 图标图片 ----------

        /// <summary>向系统要一张图标图片。iconOnly=false 时和资源管理器一样：图片、视频会给内容缩略图。</summary>
        static BitmapSource? LoadImage(IShellItem item, int px, bool iconOnly)
        {
            IShellItemImageFactory factory;
            try { factory = (IShellItemImageFactory)item; }
            catch { return null; }
            var size = new SIZE { CX = px, CY = px };
            IntPtr hbm = IntPtr.Zero;
            int hr = -1;
            if (!iconOnly)
            {
                try { hr = factory.GetImage(size, ShellGuids.SIIGBF_RESIZETOFIT, out hbm); } catch { hr = -1; hbm = IntPtr.Zero; }
            }
            if (hr != 0 || hbm == IntPtr.Zero)
            {
                try { hr = factory.GetImage(size, ShellGuids.SIIGBF_ICONONLY, out hbm); } catch { hr = -1; hbm = IntPtr.Zero; }
            }
            if (hr != 0 || hbm == IntPtr.Zero) return null;
            try { return FromHBitmap(hbm); }
            finally { Native.DeleteObject(hbm); }
        }

        /// <summary>把系统给的位图拷成 WPF 能用的图片，保留透明度。</summary>
        static BitmapSource? FromHBitmap(IntPtr hbm)
        {
            if (Native.GetObject(hbm, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0) return null;
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;
            var bi = new BITMAPINFO
            {
                biSize = 40, biWidth = w, biHeight = -h,      // 负高度 = 从上往下的行序
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            };
            var px = new byte[w * h * 4];
            IntPtr hdc = Native.GetDC(IntPtr.Zero);
            int got;
            try { got = Native.GetDIBits(hdc, hbm, 0, (uint)h, px, ref bi, 0); }
            finally { Native.ReleaseDC(IntPtr.Zero, hdc); }
            if (got == 0) return null;

            // 透明度：有的位图完全没有透明信息（全 0），当成不透明；
            // 颜色比透明度还亮的说明不是「预乘」格式，按普通格式处理。
            bool anyAlpha = false, straight = false;
            for (int i = 0; i < px.Length; i += 4)
            {
                byte a = px[i + 3];
                if (a != 0) anyAlpha = true;
                if (px[i] > a + 1 || px[i + 1] > a + 1 || px[i + 2] > a + 1) straight = true;
            }
            PixelFormat fmt;
            if (!anyAlpha)
            {
                for (int i = 3; i < px.Length; i += 4) px[i] = 255;
                fmt = PixelFormats.Bgr32;
            }
            else fmt = straight ? PixelFormats.Bgra32 : PixelFormats.Pbgra32;
            BitmapSource bs = BitmapSource.Create(w, h, 96, 96, fmt, null, px, w * 4);
            bs.Freeze();
            return bs;
        }
    }

    /// <summary>
    /// 遮罩用的壁纸。只读：从系统设置里读壁纸文件路径和铺法（填充/适应/拉伸/平铺/居中/跨区），
    /// 自己照样铺一张和主屏一样大的图。从不调用任何「设置壁纸」的接口。
    /// </summary>
    public static class Wallpaper
    {
        public static void Compose(LoadResult r)
        {
            ScreenInfo sc = r.Screen;
            // 背景色
            Color bg = Color.FromRgb(0, 0, 0);
            try
            {
                using RegistryKey? k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
                string? v = k?.GetValue("Background") as string;
                string[] parts = (v ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && byte.TryParse(parts[0], out byte cr) && byte.TryParse(parts[1], out byte cg) && byte.TryParse(parts[2], out byte cb))
                    bg = Color.FromRgb(cr, cg, cb);
                else
                {
                    uint c = Native.GetSysColor(Native.COLOR_DESKTOP);
                    bg = Color.FromRgb((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
                }
            }
            catch { }
            r.CurtainColor = bg;

            // 壁纸文件和铺法
            string path = "";
            int style = 10;
            bool tile = false;
            try
            {
                var sb = new System.Text.StringBuilder(1024);
                if (Native.SystemParametersInfo(Native.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0)) path = sb.ToString();
            }
            catch { }
            try
            {
                using RegistryKey? k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                if (k != null)
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) path = (k.GetValue("WallPaper") as string) ?? path;
                    if (int.TryParse(k.GetValue("WallpaperStyle") as string, out int st)) style = st;
                    tile = (k.GetValue("TileWallpaper") as string) == "1";
                }
            }
            catch { }
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                string transcoded = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                                           @"Microsoft\Windows\Themes\TranscodedWallpaper");
                if (string.IsNullOrEmpty(path) && !File.Exists(transcoded))
                {
                    r.Curtain = null;
                    r.CurtainNote = "遮罩：纯色背景（系统没有设壁纸图片）";
                    r.Diag.Add("壁纸：没有图片，背景色 " + bg);
                    return;
                }
                if (File.Exists(transcoded)) path = transcoded;
            }
            if (!File.Exists(path))
            {
                r.Curtain = null;
                r.CurtainNote = "遮罩：找不到壁纸文件，用纯色盖住真实图标";
                r.CurtainProblem = new Msg(r.CurtainNote, "No wallpaper file found; a plain color covers the real icons");
                r.Diag.Add("壁纸：文件不存在 " + path);
                return;
            }

            BitmapSource img;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                img = dec.Frames[0];
                img.Freeze();
            }
            double iw = img.PixelWidth, ih = img.PixelHeight;
            if (iw < 1 || ih < 1) throw new InvalidDataException("壁纸尺寸为 0");
            double mw = sc.MonW, mh = sc.MonH;

            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
            string how;
            using (DrawingContext dc = dv.RenderOpen())
            {
                var bgBrush = new SolidColorBrush(bg);
                var full = new System.Windows.Rect(0, 0, mw, mh);
                dc.DrawRectangle(bgBrush, null, full);
                dc.PushClip(new RectangleGeometry(full));
                if (tile)
                {
                    how = "平铺";
                    var brush = new ImageBrush(img)
                    {
                        TileMode = TileMode.Tile, Stretch = Stretch.Fill,
                        ViewportUnits = BrushMappingMode.Absolute,
                        Viewport = new System.Windows.Rect(sc.VirtX - sc.MonX, sc.VirtY - sc.MonY, iw, ih),
                    };
                    dc.DrawRectangle(brush, null, full);
                }
                else if (style == 22)
                {
                    how = "跨区";
                    double k = Math.Max(sc.VirtW / iw, sc.VirtH / ih);
                    double dw = iw * k, dh = ih * k;
                    double x = (sc.VirtW - dw) / 2 + (sc.VirtX - sc.MonX), y = (sc.VirtH - dh) / 2 + (sc.VirtY - sc.MonY);
                    dc.DrawImage(img, new System.Windows.Rect(x, y, dw, dh));
                }
                else if (style == 2)
                {
                    how = "拉伸";
                    dc.DrawImage(img, full);
                }
                else if (style == 0)
                {
                    how = "居中";
                    dc.DrawImage(img, new System.Windows.Rect((mw - iw) / 2, (mh - ih) / 2, iw, ih));
                }
                else
                {
                    bool fit = style == 6;
                    how = fit ? "适应" : "填充";
                    double k = fit ? Math.Min(mw / iw, mh / ih) : Math.Max(mw / iw, mh / ih);
                    double dw = iw * k, dh = ih * k;
                    dc.DrawImage(img, new System.Windows.Rect((mw - dw) / 2, (mh - dh) / 2, dw, dh));
                }
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(sc.MonW, sc.MonH, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            r.Curtain = rtb;
            r.CurtainNote = "遮罩：壁纸文件（" + how + "）";
            r.Diag.Add("壁纸：" + path + "，" + (int)iw + "x" + (int)ih + "，铺法 " + how + "（WallpaperStyle=" + style + "，Tile=" + (tile ? 1 : 0) + "）");
        }
    }
}
