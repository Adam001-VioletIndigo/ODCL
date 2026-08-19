using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.UI;

namespace ODCL;

public sealed partial class MainWindow : Window
{
    private DbService _db;
    private readonly List<SessionItem> _sessions = new();
    private readonly Dictionary<string, NodeData> _contentIndex = new();
    private DbStats _stats = new();
    private bool _busy;

    private sealed record NodeData(string Kind, string Json);

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
    private bool _desc = true;
    private bool _allSel;

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
        if (_desc)
            list.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
        else
            list.Sort((a, b) => a.TotalBytes.CompareTo(b.TotalBytes));
        foreach (var it in list)
        {
            it.Sub = $"{DateTimeOffset.FromUnixTimeMilliseconds(it.Created).ToLocalTime():yyyy-MM-dd HH:mm} · "
                   + ($"事件 {it.EventCount:N0} · {Fmt(it.EventBytes)} · 消息数据 {Fmt(it.RelatedBytes)}");
        }
        if (_db.OrphanEvents > 0)
        {
            list.Add(new SessionItem
            {
                Id = "__orphan__",
                Title = $"〔孤立事件 {_db.OrphanEvents:N0} 条，不属于任何会话〕",
                Sub = $"{Fmt(_db.OrphanBytes)}，可直接删除",
                EventBytes = _db.OrphanBytes,
                RelatedBytes = 0,
            });
        }
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

    private async void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_busy) return;
        if (SessionList.SelectedItem is not SessionItem si) return;
        try
        {
            await LoadSession(si);
        }
        catch (Exception ex)
        {
            DetailText.Text = "加载会话失败：" + ex.Message + "\n" + ex;
        }
    }

    private async Task LoadSession(SessionItem si)
    {
        MsgTree.RootNodes.Clear();
        _contentIndex.Clear();
        DetailText.Text = $"会话：{si.Title}\nID：{si.Id}\n{si.Sub}\n加载中…";
        if (si.Id == "__orphan__")
        {
            DetailText.Text = "这些 event 不属于当前任何会话（历史残留），可在列表中选中后删除。";
            return;
        }
        var items = await Task.Run(() =>
        {
            var ms = _db.GetMessages(si.Id);
            var pt = _db.GetParts(si.Id).GroupBy(p => p.MessageId).ToDictionary(g => g.Key, g => g.ToList());
            return (ms, pt);
        });
        var root = new TreeViewNode { Content = $"会话消息（{items.ms.Count} 条）" };
        int shown = 0;
        foreach (var m in items.ms)
        {
            var text = SummarizeMessage(m.Json);
            var mn = new TreeViewNode { Content = text };
            _contentIndex[text] = new NodeData("message", m.Json);
            if (items.pt.TryGetValue(m.Id, out var parts))
                foreach (var p in parts)
                {
                    var ptext = SummarizePart(p.Json);
                    var pn = new TreeViewNode { Content = ptext };
                    _contentIndex[ptext] = new NodeData("part", p.Json);
                    mn.Children.Add(pn);
                }
            root.Children.Add(mn);
            mn.IsExpanded = true;
            shown++;
        }
        MsgTree.RootNodes.Add(root);
        root.IsExpanded = true;
        DetailText.Text = $"会话：{si.Title} · 共 {shown} 条消息，点击节点查看内容。";
    }

    private void MsgTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode n && n.Content is string cs && _contentIndex.TryGetValue(cs, out var d))
            DetailText.Text = Materialize(d);
        else if (args.InvokedItem is string s && _contentIndex.TryGetValue(s, out var d2))
            DetailText.Text = Materialize(d2);
    }

    private static string Materialize(NodeData d)
    {
        var sb = new System.Text.StringBuilder();
        if (d.Kind == "part" && d.Json.Contains("\"tool\":", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(d.Json);
                var r = doc.RootElement;
                var tool = Str(ref r, "tool") ?? "?";
                if (r.TryGetProperty("state", out var st))
                {
                    sb.Append($"工具: {tool}   状态: {Str(ref st, "status")}\n");
                    if (st.TryGetProperty("input", out var inp) && inp.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String)
                        sb.Append($"命令: {cmd.GetString()}\n");
                }
                sb.AppendLine();
            }
            catch { }
        }
        sb.Append(Pretty(d.Json));
        return sb.ToString();
    }

    private static string Pretty(string json, int max = 400_000)
    {
        try
        {
            json = JsonNode.Parse(json)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? json;
        }
        catch { }
        return json.Length <= max ? json : json[..max] + $"\n…（超长截断，共 {json.Length:N0} 字符）";
    }

    private static string? Str(ref JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string SummarizeMessage(string json)
    {
        using var d = JsonDocument.Parse(json);
        var r = d.RootElement;
        var parts = new List<string> { Str(ref r, "role") ?? "?" };
        if (Str(ref r, "modelID") is string m && m.Length > 0) parts.Add(m);
        if (Str(ref r, "finish") is string f && f.Length > 0) parts.Add(f);
        if (r.TryGetProperty("time", out var tm) && tm.TryGetProperty("created", out var tc) && tc.ValueKind == JsonValueKind.Number)
            parts.Add(DateTimeOffset.FromUnixTimeMilliseconds(tc.GetInt64()).ToLocalTime().ToString("MM-dd HH:mm"));
        return string.Join(" | ", parts);
    }

    private static string SummarizePart(string json)
    {
        using var d = JsonDocument.Parse(json);
        var r = d.RootElement;
        var type = Str(ref r, "type") ?? "?";
        return type switch
        {
            "text" => "文本: " + Trunc(Str(ref r, "text"), 90),
            "reasoning" => "推理: " + Trunc(Str(ref r, "text"), 90),
            "tool" => $"工具: {Str(ref r, "tool")} · {State(ref r)}",
            "file" => "文件: " + Trunc(Str(ref r, "path"), 90),
            "step-start" => "步骤开始",
            "step-finish" => "步骤结束",
            _ => type + (Str(ref r, "text") is string s ? ": " + Trunc(s, 60) : ""),
        };
    }

    private static string State(ref JsonElement r)
        => r.TryGetProperty("state", out var st) && st.TryGetProperty("status", out var st_) && st_.ValueKind == JsonValueKind.String
            ? st_.GetString()!
            : "?";

    private static string Trunc(string? s, int n)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s.Replace('\n', ' ') : s[..n].Replace('\n', ' ') + "…");

    // ---------- 删除 ----------

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        var sel = SessionList.SelectedItems.Cast<SessionItem>().ToList();
        if (sel.Count == 0) { await Dialog("请先选中要删除的会话。"); return; }
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
        if (SessionList.ItemsSource == null) return;
        if (_allSel) SessionList.SelectedItems.Clear();
        else SessionList.SelectAll();
        _allSel = !_allSel;
        SelectAllBtn.Content = _allSel ? "全不选" : "全选";
    }

    private void TopNBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.ItemsSource is not List<SessionItem> list || list.Count == 0) return;
        int n = int.TryParse(SelN.Text, out var v) && v > 0 ? v : 5;
        var top = list.OrderByDescending(x => x.TotalBytes).Take(n).ToList();
        SessionList.SelectedItems.Clear();
        foreach (var it in top)
            SessionList.SelectedItems.Add(it);
        SessionList.ScrollIntoView(top[^1]);
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void SortBtn_Click(object sender, RoutedEventArgs e)
    {
        _desc = !_desc;
        SortBtn.Content = _desc ? "按占用降序" : "按占用升序";
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