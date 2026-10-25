using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.UI;

namespace ODCL;

public sealed partial class MainWindow : Window
{
    private DbService _db;
    private readonly List<SessionItem> _sessions = new();
    private DbStats _stats = new();
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        Title = "ODCL — Opencode 数据库清理器";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        _sized = false;
        Activated += (_, _) => TryResize();
        TryResize();
        _db = new DbService(DbPaths.DefaultDbPath());
        VacuumBtn.IsEnabled = false;
        _ = RefreshAsync();
    }

    private bool _sized;
    private bool _allSel;
    private SessionItem? _currentItem;
    private SessionItem? _orphan;

    private void LayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = e.NewSize.Width;
        bool v = w < 1080;
        double total = w >= 1800 ? 144 : w >= 1500 ? 132 : w >= 1200 ? 120 : w >= 1080 ? 108 : 96;
        double mini = Math.Clamp(total * 0.5, 48, 72);
        CardTotal.RingSize = total;
        CardEvent.RingSize = mini;
        CardRel.RingSize = mini;
        CardFree.RingSize = mini;
        CardDisk.RingSize = mini;
        CardTotal.Vertical = v;
        CardEvent.Vertical = v;
        CardRel.Vertical = v;
        CardFree.Vertical = v;
        CardDisk.Vertical = v;
    }

    private void TryResize()
    {
        if (_sized) return;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = 96;
            double s = dpi / 96.0;
            int w = (int)Math.Round(1440 * s), h = (int)Math.Round(830 * s);
            NativeRect wa = default;
            if (SystemParametersInfoW(0x0030, 0, ref wa, 0))
            {
                w = Math.Min(w, wa.Right - wa.Left);
                h = Math.Min(h, wa.Bottom - wa.Top);
            }
            AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
            _sized = true;
        }
        catch { }
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SystemParametersInfoW(uint action, uint y, ref NativeRect rect, uint flags);

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }

    // ---------- 刷新 / 统计 ----------

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            CardEvent.Value = "读取中…";
            (DbStats stats, List<SessionItem> list) = await Task.Run(() =>
            {
                var s = _db.GetStats();
                var l = _db.GetSessions();
                return (s, l);
            });
            _stats = stats;
            _sessions.Clear();
            _sessions.AddRange(list);
            BuildList();
            UpdateStatCards();

            long est = DiskStrategy.EstimateFinal(stats.EventBytes, stats.RelatedBytes);
            VacuumBtn.IsEnabled = DiskStrategy.CanRebuild(stats.FreeDisk, est);
            DiskHint.Text = DiskStrategy.CanRebuild(stats.FreeDisk, est)
                ? ""
                : $"⚠ 磁盘剩余 {Fmt(stats.FreeDisk)}，重建需约 {Fmt(est)}；删除将自动分批、重建暂不可用";
        }
        catch (Exception ex)
        {
            CardEvent.Value = "读取失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private void BuildList()
    {
        if (!_db.Exists())
        {
            SessionList.ItemsSource = null;
            return;
        }
        var list = new List<SessionItem>(_sessions);
        SessionSorter.Sort(list, SortCombo.SelectedIndex);
        foreach (var it in list)
        {
            it.Sub = $"{DateTimeOffset.FromUnixTimeMilliseconds(it.Created).ToLocalTime():yyyy-MM-dd HH:mm} · "
                   + ($"事件 {it.EventCount:N0} · {Fmt(it.EventBytes)} · 消息数据 {Fmt(it.RelatedBytes)}");
        }
        if (_db.OrphanEvents > 0)
        {
            _orphan ??= new SessionItem { Id = "__orphan__" };
            _orphan.Title = $"〔孤立事件 {_db.OrphanEvents:N0} 条，不属于任何会话〕";
            _orphan.Sub = $"{Fmt(_db.OrphanBytes)}，可直接删除";
            _orphan.EventBytes = _db.OrphanBytes;
            list.Add(_orphan);
        }
        else
        {
            _orphan = null;
        }
        foreach (var it in _sessions) it.IsCurrent = false;
        if (_orphan != null) _orphan.IsCurrent = false;
        _currentItem = null;
        SessionList.ItemsSource = list;
        _allSel = false;
        SelectAllBtn.Content = "全选";
    }

    private void UpdateStatCards()
    {
        var ev = Color.FromArgb(255, 79, 156, 249);
        var re = Color.FromArgb(255, 60, 197, 143);
        var fl = Color.FromArgb(255, 245, 166, 35);

        long total = Math.Max(1, _stats.TotalFileSize);
        CardTotal.SetData(Fmt(_stats.TotalFileSize), "总大小",
            (Math.Max(0, _stats.EventBytes), ev),
            (Math.Max(0, _stats.RelatedBytes), re),
            (Math.Max(0, _stats.FreelistBytes), fl));
        CardTotal.SetLegend(new[]
        {
            ("event 事件", Math.Max(0, _stats.EventBytes), ev),
            ("关联数据", Math.Max(0, _stats.RelatedBytes), re),
            ("freelist", Math.Max(0, _stats.FreelistBytes), fl),
            ("其他", Math.Max(0, total - _stats.EventBytes - _stats.RelatedBytes - _stats.FreelistBytes), Color.FromArgb(255, 118, 118, 132)),
        });

        CardEvent.SetPercent(Pct(_stats.EventBytes, total));
        CardEvent.Value = Fmt(_stats.EventBytes);
        CardRel.SetPercent(Pct(_stats.RelatedBytes, total));
        CardRel.Value = Fmt(_stats.RelatedBytes);
        CardFree.SetPercent(Pct(_stats.FreelistBytes, total));
        CardFree.Value = Fmt(_stats.FreelistBytes);
        CardFree.Extra = _stats.FreelistBytes > 0 ? "重建数据库即可释放" : "";
        CardDisk.SetPercent(Pct(_stats.FreeDisk, Math.Max(1, _stats.TotalDisk)));
        CardDisk.Value = Fmt(_stats.FreeDisk);
    }

    private static string Pct(long v, long total)
        => total > 0 ? (Math.Clamp((long)(v * 100.0 / total), 0, 100)) + "%" : "0%";

    // ---------- 会话内容 ----------

    private void CopyCmd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string id) return;
        if (string.IsNullOrEmpty(id) || id == "__orphan__") return;
        var dp = new DataPackage();
        dp.SetText($"opencode -s {id}");
        Clipboard.SetContent(dp);
    }

    private async void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionItem si })
            await ShowPreview(si);
    }

    private void Cb_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private async Task ShowPreview(SessionItem si)
    {
        if (_busy || si == _currentItem) return;
        if (SessionList.ItemsSource is not List<SessionItem> list) return;
        _currentItem = si;
        foreach (var it in list) it.IsCurrent = it == si;
        await LoadSession(si);
    }

    private int _loadSeq;
    private List<ChatRound> _rounds = new();

    private async Task LoadSession(SessionItem si)
    {
        int seq = ++_loadSeq;
        _rounds = new();
        ChatList.ItemsSource = null;
        TocBtn.Visibility = Visibility.Collapsed;
        ChatHeader.Text = $"会话：{si.Title}";
        ChatLoading.IsActive = true;

        if (si.Id == "__orphan__")
        {
            ChatLoading.IsActive = false;
            ChatHeader.Text = "孤立事件（不属于任何会话）";
            return;
        }

        ChatData data;
        try
        {
            data = await Task.Run(() => _db.GetChat(si.Id));
        }
        catch (Exception ex)
        {
            if (seq == _loadSeq) { ChatLoading.IsActive = false; ChatHeader.Text = "加载失败：" + ex.Message; }
            return;
        }
        if (seq != _loadSeq) return;

        _rounds = data.Rounds;
        ChatList.ItemsSource = _rounds;
        TocBtn.Visibility = data.HasToc ? Visibility.Visible : Visibility.Collapsed;
        ChatHeader.Text = $"会话：{si.Title} · {data.Rounds.Count} 轮 · {data.MessageCount} 条消息";
        ChatLoading.IsActive = false;
        if (_rounds.Count > 0) ChatList.ScrollIntoView(_rounds[0]);
    }

    private void ExpandUser_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatRound r }) r.UserExpanded = true;
    }

    private void ExpandAssist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatRound r }) r.AssistExpanded = true;
    }

    private void ExpandStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatStep s }) s.Expanded = true;
    }

    private void Exec_Expanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (sender.Tag is ChatRound r && sender.Content is ListView list && list.ItemsSource == null)
            list.ItemsSource = r.Steps;
    }

    private async void TocBtn_Click(object sender, RoutedEventArgs e)
    {
        var items = _rounds.Where(r => !string.IsNullOrEmpty(r.UserText)).ToList();
        if (items.Count == 0) return;
        ChatRound? jump = null;
        var lv = new ListView
        {
            ItemsSource = items,
            ItemTemplate = (DataTemplate)LayoutRoot.Resources["TocItemTemplate"],
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
            Width = 560,
            Height = 460,
        };
        var dlg = new ContentDialog
        {
            Title = "对话目录（点击跳转）",
            Content = lv,
            CloseButtonText = "关闭",
            XamlRoot = Content.XamlRoot,
        };
        lv.ItemClick += (_, args) => { jump = args.ClickedItem as ChatRound; dlg.Hide(); };
        await dlg.ShowAsync();
        if (jump != null) JumpToRound(jump);
    }

    private void JumpToRound(ChatRound r)
    {
        foreach (var x in _rounds) x.IsHighlighted = x == r;
        ChatList.ScrollIntoView(r, ScrollIntoViewAlignment.Leading);
    }

    // ---------- 删除 ----------

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        var sel = _sessions.Where(x => x.IsSelected).ToList();
        if (_orphan is { IsSelected: true }) sel.Add(_orphan);
        if (sel.Count == 0) { await Dialog("请先勾选要删除的会话。"); return; }
        var st = await Task.Run(() => _db.GetStats());
        long delEvent = sel.Sum(x => x.EventBytes);
        long delRel = sel.Sum(x => x.RelatedBytes);
        long delCount = sel.Sum(x => x.EventCount);
        long keptEvent = Math.Max(0, st.EventBytes - delEvent);
        long keptRel = Math.Max(0, st.RelatedBytes - delRel);
        long estFinal = DiskStrategy.EstimateFinal(keptEvent, keptRel);
        bool batch = DiskStrategy.NeedBatch(st.FreeDisk, estFinal);
        long avg = delCount > 0 ? delEvent / delCount : 0;

        if (!await Confirm(
            $"删除 {sel.Count} 个会话（约 {Fmt(delEvent + delRel)}）。\n" +
            $"执行方式：{(batch ? "分批删除（磁盘剩余不足重建所需）" : "快速删除")}。\n确定？"))
            return;

        DeleteBtn.IsEnabled = false;
        VacuumBtn.IsEnabled = false;
        ShowProgress(sel.Count + 1);
        try
        {
            int batchRows = batch ? DiskStrategy.BatchRows(st.FreeDisk, (int)st.PageSize, avg) : 0;
            await Task.Run(() =>
            {
                foreach (var it in sel)
                {
                    if (it.Id == "__orphan__")
                        _db.DeleteOrphans(batchRows, null);
                    else
                        _db.DeleteSession(it.Id, batchRows, null);
                    _busyStage++;
                    _ = DispatcherQueue.TryEnqueue(() => Progress.Value = _busyStage);
                }
            });
            await RefreshAsync();
            await Dialog($"已释放约 {Fmt(delEvent + delRel)} 空间。");
        }
        catch (Exception ex)
        {
            await Dialog("删除失败：" + ex.Message + "\n（若提示 locked/busy，可稍后重试或先退出 opencode）");
        }
        finally
        {
            DeleteBtn.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
            _busy = false;
        }
    }

    private int _busyStage;

    private void ShowProgress(int max)
    {
        Progress.IsIndeterminate = false;
        Progress.Maximum = max;
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;
    }

    // ---------- VACUUM ----------

    private async void VacuumBtn_Click(object sender, RoutedEventArgs e)
    {
        var st = await Task.Run(() => _db.GetStats());
        long estFinal = DiskStrategy.EstimateFinal(st.EventBytes, st.RelatedBytes);
        if (!DiskStrategy.CanRebuild(st.FreeDisk, estFinal))
        {
            await Dialog($"磁盘剩余不足：重建需临时空间约 {Fmt(estFinal)}，当前剩余 {Fmt(st.FreeDisk)}。先删除更多会话再重建。");
            return;
        }
        if (!await Confirm($"重建数据库？\n临时空间约需 {Fmt(estFinal)}，将回收 freelist 空间，期间请勿写入。确定？"))
            return;
        VacuumBtn.IsEnabled = false;
        DeleteBtn.IsEnabled = false;
        Progress.IsIndeterminate = true;
        if (Progress.Visibility != Visibility.Visible) Progress.Visibility = Visibility.Visible;
        try
        {
            await Task.Run(() => _db.Vacuum());
            await RefreshAsync();
            await Dialog("VACUUM 完成。");
        }
        catch (Exception ex)
        {
            await Dialog("重建失败：" + ex.Message + "\n（若提示 locked/busy，请先完全退出 opencode 后再试）");
        }
        finally
        {
            VacuumBtn.IsEnabled = true;
            DeleteBtn.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- 转移数据库 ----------

    private async void MoveBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow(picker, this);
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        string target = folder.Path;
        string source = _db.DbDirectory;
        string srcFull = Path.GetFullPath(source).TrimEnd('\\', '/');
        string dstFull = Path.GetFullPath(target).TrimEnd('\\', '/');
        if (string.Equals(srcFull, dstFull, StringComparison.OrdinalIgnoreCase))
        { await Dialog("目标不能等于原文件夹。"); return; }
        if (dstFull.StartsWith(srcFull + "\\", StringComparison.OrdinalIgnoreCase) || dstFull.StartsWith(srcFull + "/", StringComparison.OrdinalIgnoreCase))
        { await Dialog("目标不能位于原文件夹内部。"); return; }

        string warn = "";
        try
        {
            if (Process.GetProcessesByName("opencode").Length > 0)
                warn = "\n⚠ 检测到 opencode 进程正在运行，强烈建议先退出，否则可能复制失败或数据不一致。";
        }
        catch { }

        if (!await Confirm(
            $"将把整个 [opencode] 数据文件夹整体移动：\n  原位置: {source}\n  目标位置: {dstFull}\n移动后删除原文件夹并创建目录符号链接 mklink /d。{warn}\n确定？"))
            return;
        MoveBtn.IsEnabled = false;
        ShowProgress(0);
        try
        {
            await Task.Run(() => DbService.MoveDirectory(source, dstFull, null));
            var movedDb = Path.Combine(dstFull, "opencode.db");
            string? check = await Task.Run(() => new DbService(movedDb).Integrity());
            if (check != "ok")
            {
                await Dialog($"目标副本校验失败（integrity_check={check}）。原数据未删除，请人工核对目标文件夹。");
                return;
            }
            Directory.Delete(source, true);
            var (ok, err) = RunMklink(source, dstFull);
            if (!ok)
            {
                await Dialog($"符号链接创建失败：{err}\n数据已在 {dstFull}。请以管理员身份手动执行：\n\ncmd /c mklink /d \"{source}\" \"{dstFull}\"");
                return;
            }
            _db = new DbService(Path.Combine(source, "opencode.db"));
            await RefreshAsync();
            await Dialog("转移完成！数据已移至目标盘，原路径符号链接已就绪，opencode 无需改路径。");
        }
        catch (Exception ex)
        {
            await Dialog("转移失败：" + ex.Message);
        }
        finally
        {
            MoveBtn.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private static (bool ok, string err) RunMklink(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /d \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        if (p.ExitCode == 0) return (true, outp.Trim());
        if (outp.Contains("权限", StringComparison.OrdinalIgnoreCase) ||
            outp.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            outp.Contains("require", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var psi2 = new ProcessStartInfo("cmd.exe", $"/c mklink /d \"{link}\" \"{target}\"")
                {
                    Verb = "runas",
                    UseShellExecute = true,
                };
                using var p2 = Process.Start(psi2)!;
                p2.WaitForExit(60000);
                if (p2.ExitCode == 0) return (true, "");
            }
            catch (Exception ex)
            {
                return (false, outp.Trim() + "\n[提权失败] " + ex.Message);
            }
        }
        return (false, outp.Trim());
    }

    // ---------- 其他 ----------

    private void SelN_BeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
        => args.Cancel = !string.IsNullOrEmpty(args.NewText) && !args.NewText.All(static c => c is >= '0' and <= '9');

    private void SelectAllBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.ItemsSource is not List<SessionItem> list || list.Count == 0) return;
        foreach (var it in list) it.IsSelected = !_allSel;
        _allSel = !_allSel;
        SelectAllBtn.Content = _allSel ? "全不选" : "全选";
    }

    private void TopNBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.ItemsSource is not List<SessionItem> list || list.Count == 0) return;
        int n = int.TryParse(SelN.Text, out var v) && v > 0 ? v : 5;
        var top = list.Where(x => x.Id != "__orphan__").OrderByDescending(x => x.TotalBytes).Take(n).ToList();
        if (top.Count == 0) return;
        foreach (var it in list) it.IsSelected = false;
        foreach (var it in top) it.IsSelected = true;
        _allSel = false;
        SelectAllBtn.Content = "全选";
        SessionList.ScrollIntoView(top[^1]);
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_db is null) return;
        try { BuildList(); } catch { }
    }

    private async void ChangeDbBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add(".db");
        try
        {
            InitializeWithWindow(picker, this);
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            _db = new DbService(file.Path);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await Dialog("打开数据库失败：" + ex.Message);
        }
    }

    private static void InitializeWithWindow(object picker, Window window)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    private async Task Dialog(string text)
    {
        var d = new ContentDialog
        {
            Title = "ODCL",
            Content = text,
            CloseButtonText = "确定",
            XamlRoot = Content.XamlRoot,
        };
        await d.ShowAsync();
    }

    private async Task<bool> Confirm(string text, string? title = null)
    {
        var d = new ContentDialog
        {
            Title = title ?? "请确认",
            Content = text,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }

    private static string Fmt(long b)
        => b >= 1073741824 ? $"{b / 1073741824.0:F1} GB"
         : b >= 1048576 ? $"{b / 1048576.0:F1} MB"
         : b >= 1024 ? $"{b / 1024.0:F1} KB"
         : $"{b} B";
}

// ponytail: 主题切换时不会自动重算已渲染项的画笔；对单窗口工具可接受，升级路径是监听 RequestedTheme 后重刷 ItemsSource。
public sealed class BoolToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool on = value is true;
        var keys = (parameter as string ?? "").Split('|');
        string key = on && keys.Length > 0
            ? keys[0]
            : keys.Length > 1 ? keys[1] : "CardStrokeColorDefaultBrush";
        if (key == "Transparent") return new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        return Application.Current.Resources.TryGetValue(key, out var b) && b is Brush br
            ? br
            : new SolidColorBrush(on ? Color.FromArgb(255, 79, 156, 249) : Color.FromArgb(255, 118, 118, 132));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
