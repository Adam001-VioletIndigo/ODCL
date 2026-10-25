using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace ODCL;

public sealed class ChatData
{
    public List<ChatRound> Rounds = new();
    public int MessageCount;
    public bool HasToc => MessageCount > 50;
}

public static class ChatLimits
{
    public const int Bubble = 4000;
    public const int Step = 6000;
}

public sealed class ChatRound : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string UserId = "";
    public long Time;
    public string Model = "";
    public string UserText = "";
    public string AssistantText = "";
    public List<ChatStep> Steps = new();
    public int StepCount;
    public bool HasFailure;

    public string TimeText => DateTimeOffset.FromUnixTimeMilliseconds(Time).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string AssistMeta => string.IsNullOrEmpty(Model) ? TimeText : $"{Model} · {TimeText}";
    public string ExecHeader => $"执行过程 · {StepCount} 步" + (HasFailure ? "  ·  有失败" : "");
    public string TocPreview => OneLine(UserText, 140);
    public string TocMeta => $"{StepCount} 步" + (HasFailure ? " · 有失败" : "");

    private bool _userExpanded;
    public bool UserExpanded
    {
        get => _userExpanded;
        set { if (_userExpanded != value) { _userExpanded = value; Raise(); Raise(nameof(UserDisplay)); Raise(nameof(UserExpandVis)); } }
    }

    private bool _assistExpanded;
    public bool AssistExpanded
    {
        get => _assistExpanded;
        set { if (_assistExpanded != value) { _assistExpanded = value; Raise(); Raise(nameof(AssistDisplay)); Raise(nameof(AssistExpandVis)); } }
    }

    private bool _highlighted;
    public bool IsHighlighted
    {
        get => _highlighted;
        set { if (_highlighted != value) { _highlighted = value; Raise(); } }
    }

    public string UserDisplay => Fit(UserText, UserExpanded, ChatLimits.Bubble);
    public string AssistDisplay => Fit(AssistantText, AssistExpanded, ChatLimits.Bubble);

    public Visibility UserVis => UserText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility AssistVis => AssistantText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility StepsVis => StepCount == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility UserExpandVis => !UserExpanded && UserText.Length > ChatLimits.Bubble ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AssistExpandVis => !AssistExpanded && AssistantText.Length > ChatLimits.Bubble ? Visibility.Visible : Visibility.Collapsed;

    internal static string Fit(string s, bool expanded, int limit)
        => expanded || s.Length <= limit ? s : s[..limit] + $"\n…（已截断，共 {s.Length:N0} 字符）";

    internal static string OneLine(string s, int max)
    {
        var t = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }
}

public sealed class ChatStep : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Kind = "";
    public string Tool = "";
    public string Status = "";
    public string Detail = "";
    public string Output = "";

    public bool Failed => Status is "error" or "failed";
    public string Header => Kind == "reasoning" ? "思考" : $"{Tool} · {Status}";

    private bool _expanded;
    public bool Expanded
    {
        get => _expanded;
        set { if (_expanded != value) { _expanded = value; Raise(); Raise(nameof(Body)); Raise(nameof(ExpandVis)); } }
    }

    private string Combined => string.IsNullOrEmpty(Detail) ? Output
        : string.IsNullOrEmpty(Output) ? Detail
        : Detail + "\n\n" + Output;

    public string Body => ChatRound.Fit(Combined, Expanded, ChatLimits.Step);
    public Visibility ExpandVis => !Expanded && Combined.Length > ChatLimits.Step ? Visibility.Visible : Visibility.Collapsed;
}

public static class ChatLoader
{
    private static readonly List<MsgPart> Empty = new();

    public static ChatData Build(List<MsgPart> messages, List<MsgPart> parts)
    {
        var byMsg = parts.GroupBy(p => p.MessageId)
                         .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Time).ThenBy(p => p.Id).ToList());
        var data = new ChatData { MessageCount = messages.Count };
        ChatRound? cur = null;

        foreach (var m in messages.OrderBy(x => x.Time).ThenBy(x => x.Id))
        {
            string role = "", model = "";
            try
            {
                using var md = JsonDocument.Parse(m.Json);
                role = Str(md.RootElement, "role") ?? "";
                model = Str(md.RootElement, "modelID") ?? "";
            }
            catch { }

            var own = byMsg.TryGetValue(m.Id, out var ps) ? ps : Empty;

            if (role == "user")
            {
                cur = new ChatRound { UserId = m.Id, Time = m.Time, Model = model, UserText = CollectText(own) };
                data.Rounds.Add(cur);
            }
            else if (role == "assistant")
            {
                if (cur == null) { cur = new ChatRound { Time = m.Time, Model = model }; data.Rounds.Add(cur); }
                if (string.IsNullOrEmpty(cur.Model)) cur.Model = model;
                var text = new List<string>();
                foreach (var p in own)
                {
                    try
                    {
                        using var d = JsonDocument.Parse(p.Json);
                        var r = d.RootElement;
                        switch (Str(r, "type"))
                        {
                            case "text":
                                var tx = Str(r, "text");
                                if (!string.IsNullOrWhiteSpace(tx)) text.Add(tx!);
                                break;
                            case "tool":
                            case "reasoning":
                                var step = ParseStep(r);
                                if (step != null) cur.Steps.Add(step);
                                break;
                        }
                    }
                    catch { }
                }
                if (text.Count > 0)
                {
                    var joined = string.Join("\n", text);
                    cur.AssistantText = cur.AssistantText.Length == 0 ? joined : cur.AssistantText + "\n\n" + joined;
                }
            }
            else
            {
                var text = CollectText(own);
                if (cur != null && text.Length > 0)
                    cur.AssistantText = cur.AssistantText.Length == 0 ? text : cur.AssistantText + "\n\n" + text;
            }
        }

        foreach (var r in data.Rounds)
        {
            r.StepCount = r.Steps.Count;
            r.HasFailure = r.Steps.Any(s => s.Failed);
        }
        return data;
    }

    private static string CollectText(List<MsgPart> parts)
    {
        var list = new List<string>();
        foreach (var p in parts)
        {
            try
            {
                using var d = JsonDocument.Parse(p.Json);
                if (Str(d.RootElement, "type") == "text" && Str(d.RootElement, "text") is string t && !string.IsNullOrWhiteSpace(t))
                    list.Add(t);
            }
            catch { }
        }
        return string.Join("\n", list);
    }

    private static ChatStep? ParseStep(JsonElement r)
    {
        var type = Str(r, "type");
        if (type == "reasoning")
            return new ChatStep { Kind = "reasoning", Output = Str(r, "text") ?? "" };
        if (type != "tool")
            return null;

        var st = new ChatStep { Kind = "tool", Tool = Str(r, "tool") ?? "?" };
        if (r.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object)
        {
            st.Status = Str(state, "status") ?? "?";
            if (state.TryGetProperty("input", out var input)) st.Detail = InputSummary(input);
            if (state.TryGetProperty("output", out var o) && o.ValueKind == JsonValueKind.String) st.Output = o.GetString() ?? "";
            if (st.Output.Length == 0 && state.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String) st.Output = er.GetString() ?? "";
        }
        return st;
    }

    private static string InputSummary(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return "";
        if (Str(input, "command") is string cmd) return "命令: " + cmd;
        if (Str(input, "filePath") is string fp) return "文件: " + fp;
        if (Str(input, "pattern") is string pat) return "模式: " + pat;
        if (Str(input, "description") is string desc) return desc;
        foreach (var p in input.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String)
                return $"{p.Name}: {p.Value.GetString()}";
        return "";
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static void SelfCheck()
    {
        void Check(bool c, string what) { if (!c) throw new InvalidOperationException("ChatLoader: " + what); }
        var msgs = new List<MsgPart>
        {
            new() { Id = "u1", Time = 1, Json = "{\"role\":\"user\"}" },
            new() { Id = "a1", Time = 2, Json = "{\"role\":\"assistant\",\"modelID\":\"m\"}" },
            new() { Id = "a2", Time = 3, Json = "{\"role\":\"assistant\",\"modelID\":\"m\"}" },
            new() { Id = "u2", Time = 4, Json = "{\"role\":\"user\"}" },
        };
        var parts = new List<MsgPart>
        {
            new() { Id = "p1", MessageId = "u1", Time = 1, Json = "{\"type\":\"text\",\"text\":\"hello\"}" },
            new() { Id = "p2", MessageId = "a1", Time = 2, Json = "{\"type\":\"text\",\"text\":\"answer1\"}" },
            new() { Id = "p3", MessageId = "a1", Time = 2, Json = "{\"type\":\"tool\",\"tool\":\"read\",\"state\":{\"status\":\"error\",\"input\":{\"filePath\":\"/x\"},\"output\":\"boom\"}}" },
            new() { Id = "p4", MessageId = "a2", Time = 3, Json = "{\"type\":\"reasoning\",\"text\":\"think\"}" },
            new() { Id = "p5", MessageId = "u2", Time = 4, Json = "{\"type\":\"text\",\"text\":\"again\"}" },
        };
        var d = Build(msgs, parts);
        Check(d.MessageCount == 4, "message count");
        Check(d.Rounds.Count == 2, "round count");
        Check(d.Rounds[0].UserText == "hello", "user text");
        Check(d.Rounds[0].AssistantText.Contains("answer1"), "assistant text");
        Check(d.Rounds[0].StepCount == 2, "step count");
        Check(d.Rounds[0].HasFailure, "failure flag");
        Check(d.Rounds[1].UserText == "again", "second round");
        d.MessageCount = 50;
        Check(!d.HasToc, "toc lower bound");
        d.MessageCount = 51;
        Check(d.HasToc, "toc threshold");

        var longRound = new ChatRound { UserText = new string('x', ChatLimits.Bubble + 10) };
        Check(longRound.UserDisplay.Length > ChatLimits.Bubble && longRound.UserDisplay.Contains("截断"), "truncate");
    }
}
