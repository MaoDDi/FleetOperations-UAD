using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using UnityEngine;

namespace FleetOps.Experimental;

public enum FleetAction { Fire, Aim, Follow, TorpedoFire, TorpedoFollow, Restore, Recommendation, Settings }
public sealed record FleetShortcut(KeyCode Key, bool Ctrl = false, bool Alt = false, bool Shift = false)
{
    public override string ToString() => Key == KeyCode.None ? "—" : (Ctrl ? "Ctrl+" : "") + (Alt ? "Alt+" : "") + (Shift ? "Shift+" : "") + Key;
}
public sealed record FleetNativeShortcut(string Name, KeyCode Key, bool? Ctrl = null, bool? Alt = null, bool? Shift = null);
public sealed record FleetBindingIssue(FleetAction? Action, string Code, string Detail, FleetAction? OtherAction = null);

/// <summary>Player settings only; no native battle state, aim orders or commands are touched.</summary>
public sealed class FleetSettings
{
    private static readonly FleetAction[] actions = Enum.GetValues<FleetAction>();
    private readonly string path;
    private Dictionary<FleetAction, FleetShortcut> bindings = Defaults();
    private FleetNativeShortcut[] native = Array.Empty<FleetNativeShortcut>();
    private IReadOnlyDictionary<FleetAction, FleetShortcut> bindingView = null!;
    private IReadOnlyList<FleetBindingIssue> issues = Array.Empty<FleetBindingIssue>();
    private string? loadProblem;
    private FleetAction? capture;
    private int page;
    private string statusZh = "", statusEn = "", statusDetail = "";
    public FleetLanguage Language { get; private set; } = FleetLanguage.ZhCN;
    public bool IsOpen { get; private set; }
    public bool InputBlocked => IsOpen || capture.HasValue;
    public Rect CurrentRect { get; private set; } = new(40, 60, 760, 600);
    public IReadOnlyDictionary<FleetAction, FleetShortcut> Bindings => bindingView;
    public IReadOnlyList<FleetBindingIssue> Issues => issues;

    public FleetSettings(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A settings directory is required.", nameof(directory));
        path = Path.Combine(Path.GetFullPath(directory), "fleet_ops_keys.json");
        Load(); Publish();
    }
    private static Dictionary<FleetAction, FleetShortcut> Defaults() => new()
    {
        [FleetAction.Fire] = new(KeyCode.I, true), [FleetAction.Aim] = new(KeyCode.K, true),
        [FleetAction.Follow] = new(KeyCode.O, true), [FleetAction.TorpedoFire] = new(KeyCode.U, true),
        [FleetAction.TorpedoFollow] = new(KeyCode.Y, true), [FleetAction.Restore] = new(KeyCode.L, true),
        [FleetAction.Recommendation] = new(KeyCode.B, true), [FleetAction.Settings] = new(KeyCode.N, true)
    };
    private string L(string zh, string en) => FleetLocalization.Choose(Language, zh, en);
    public string Display(FleetAction action) => bindings[action].Key == KeyCode.None ? L("未绑定", "Unbound") : bindings[action].ToString();
    public void ToggleOpen() { IsOpen = !IsOpen; capture = null; }
    public void Close() { IsOpen = false; capture = null; }
    public bool CaptureMouse(Vector3 screenPosition) => IsOpen && CurrentRect.Contains(new Vector2(screenPosition.x, Screen.height - screenPosition.y));

    public void RefreshNativeBindings(IEnumerable<FleetNativeShortcut> shortcuts)
    {
        if (shortcuts == null) throw new ArgumentNullException(nameof(shortcuts));
        var materialized = shortcuts.ToArray();
        if (materialized.Any(x => x == null)) throw new ArgumentException("Null native binding.", nameof(shortcuts));
        native = materialized; Publish();
    }
    public bool IsPressed(FleetAction action)
    {
        if (!Enum.IsDefined(typeof(FleetAction), action) || capture.HasValue || loadProblem != null || IsOpen && action != FleetAction.Settings) return false;
        if (issues.Any(x => x.Action == action)) return false;
        var b = bindings[action];
        return b.Key != KeyCode.None && Input.GetKeyDown(b.Key)
            && b.Ctrl == (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
            && b.Alt == (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
            && b.Shift == (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
    }

    public bool TryRebind(FleetAction action, FleetShortcut shortcut, out string error)
    {
        error = "";
        if (!Enum.IsDefined(typeof(FleetAction), action) || shortcut == null) { error = L("无效动作或快捷键。", "Invalid action or shortcut."); return false; }
        shortcut = shortcut.Key == KeyCode.None ? new(KeyCode.None) : shortcut;
        var next = new Dictionary<FleetAction, FleetShortcut>(bindings) { [action] = shortcut };
        var problem = Validate(next).FirstOrDefault(x => x.Action == action);
        if (problem != null) { error = Describe(problem); return false; }
        return Commit(next, Language, out error);
    }
    public bool TrySetLanguage(FleetLanguage language, out string error)
    {
        if (!Enum.IsDefined(typeof(FleetLanguage), language)) { error = "Invalid language."; return false; }
        return Commit(new(bindings), language, out error);
    }
    public bool TrySave(out string error)
    {
        var problem = Validate(bindings).FirstOrDefault();
        if (problem != null) { error = Describe(problem); return false; }
        return Commit(new(bindings), Language, out error);
    }
    private bool Commit(Dictionary<FleetAction, FleetShortcut> next, FleetLanguage language, out string error)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            var document = new { SchemaVersion = 2, Language = language == FleetLanguage.En ? "en" : "zh-CN",
                Bindings = next.ToDictionary(x => x.Key.ToString(), x => new { Key = x.Value.Key.ToString(), x.Value.Ctrl, x.Value.Alt, x.Value.Shift }) };
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, document, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak-" + Guid.NewGuid().ToString("N"));
            else File.Move(temporary, path);
            temporary = null;
            bindings = next; Language = language; loadProblem = null; Publish(); error = ""; return true;
        }
        catch (Exception ex) { error = L("无法保存设置：", "Could not save settings: ") + ex.Message; return false; }
        finally { if (temporary != null) try { File.Delete(temporary); } catch { } }
    }
    private void Load()
    {
        if (!File.Exists(path)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Expected a JSON object.");
            var next = Defaults();
            var language = FleetLanguage.ZhCN;
            if (root.TryGetProperty("SchemaVersion", out var version))
            {
                if (version.GetInt32() != 2) throw new FormatException("Unsupported settings schema.");
                if (root.TryGetProperty("Language", out var lang))
                    language = lang.GetString() switch { "zh-CN" => FleetLanguage.ZhCN, "en" => FleetLanguage.En, _ => throw new FormatException("Unknown language.") };
                if (!root.TryGetProperty("Bindings", out var rows) || rows.ValueKind != JsonValueKind.Object) throw new FormatException("Missing bindings object.");
                foreach (var action in actions)
                {
                    if (!rows.TryGetProperty(action.ToString(), out var row) || row.ValueKind == JsonValueKind.Null) continue;
                    var key = ParseKey(row.GetProperty("Key").GetString());
                    next[action] = key == KeyCode.None ? new(key) : new(key, ReadBool(row, "Ctrl"), ReadBool(row, "Alt"), ReadBool(row, "Shift"));
                }
            }
            else
            {
                // Legacy six-field format: letter strings had an implicit Ctrl modifier.
                foreach (var action in actions.Where(x => x != FleetAction.Settings && x != FleetAction.Recommendation))
                    if (root.TryGetProperty(action.ToString(), out var row) && row.ValueKind != JsonValueKind.Null)
                        next[action] = new(ParseKey(row.GetString()), true);
            }
            bindings = next; Language = language;
        }
        catch (Exception ex) { loadProblem = ex.Message; }
    }
    private static bool ReadBool(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.GetBoolean();
    private static KeyCode ParseKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Enum.TryParse<KeyCode>(value, true, out var key) || !Enum.IsDefined(typeof(KeyCode), key)) throw new FormatException("Invalid key: " + value);
        return key;
    }
    private static bool Allowed(KeyCode key)
    {
        if (key == KeyCode.None) return true;
        var n = (int)key;
        return Enum.IsDefined(typeof(KeyCode), key) && n >= (int)KeyCode.Space && n < (int)KeyCode.Mouse0
            && key != KeyCode.Escape && key != KeyCode.LeftControl && key != KeyCode.RightControl
            && key != KeyCode.LeftAlt && key != KeyCode.RightAlt && key != KeyCode.LeftShift && key != KeyCode.RightShift
            && key != KeyCode.LeftCommand && key != KeyCode.RightCommand && key != KeyCode.LeftWindows && key != KeyCode.RightWindows
            && key != KeyCode.AltGr && key != KeyCode.J && !(n >= (int)KeyCode.F1 && n <= (int)KeyCode.F10);
    }
    private List<FleetBindingIssue> Validate(IReadOnlyDictionary<FleetAction, FleetShortcut> values)
    {
        var found = new List<FleetBindingIssue>();
        foreach (var action in actions)
        {
            var b = values[action];
            if (!Allowed(b.Key)) found.Add(new(action, "Reserved", b.Key.ToString()));
            if (b.Key == KeyCode.None) continue;
            foreach (var other in actions.Where(x => x != action && values[x] == b)) found.Add(new(action, "Duplicate", "", other));
            foreach (var n in native.Where(n => n.Key == b.Key && (!n.Ctrl.HasValue || n.Ctrl == b.Ctrl) && (!n.Alt.HasValue || n.Alt == b.Alt) && (!n.Shift.HasValue || n.Shift == b.Shift)))
                found.Add(new(action, "Native", n.Name));
        }
        return found;
    }
    private void Publish()
    {
        bindingView = new ReadOnlyDictionary<FleetAction, FleetShortcut>(new Dictionary<FleetAction, FleetShortcut>(bindings));
        var list = Validate(bindings);
        if (loadProblem != null) list.Insert(0, new(null, "Load", loadProblem));
        issues = Array.AsReadOnly(list.ToArray());
    }
    private string Describe(FleetBindingIssue issue) => issue.Code switch
    {
        "Load" => L("配置读取失败；快捷键已禁用。请修正或保存默认设置：", "Settings could not be loaded; shortcuts disabled. Repair or save defaults: ") + issue.Detail,
        "Reserved" => L("该按键为保留键或不支持：", "Reserved or unsupported key: ") + issue.Detail,
        "Duplicate" => L("与此动作重复：", "Duplicates: ") + FleetLocalization.ActionLabel(Language, issue.OtherAction!.Value),
        "Native" => L("与原版快捷键冲突：", "Conflicts with native shortcut: ") + issue.Detail,
        _ => issue.Detail
    };
    private void Result(bool success, string error)
    {
        statusZh = success ? "设置已保存。" : "设置未保存。"; statusEn = success ? "Settings saved." : "Settings were not saved.";
        statusDetail = error;
    }

    public void Draw()
    {
        if (!IsOpen) return;
        // Keep the Chinese language selector readable even while English is selected.
        using var font = FleetLocalization.UseFont(FleetLanguage.ZhCN);
        var previousDepth = GUI.depth;
        try { GUI.depth = -10000; DrawWindow(); }
        finally { GUI.depth = previousDepth; }
    }
    private void DrawWindow()
    {
        var width = Math.Min(760f, Math.Max(320f, Screen.width - 20f));
        var height = Math.Min(600f, Math.Max(300f, Screen.height - 20f));
        CurrentRect = new Rect(Math.Max(0, Math.Min(40, Screen.width - width)), Math.Max(0, Math.Min(60, Screen.height - height)), width, height);
        var r = CurrentRect;
        var wrapped = new GUIStyle { wordWrap = true, font = GUI.skin.font, fontSize = GUI.skin.label.fontSize };
        wrapped.normal.textColor = GUI.skin.label.normal.textColor;
        GUI.Box(r, L("舰队手动瞄准设置", "Fleet manual aim settings"));
        GUI.Label(new Rect(r.x + 16, r.y + 30, width - 32, 25), L("语言 / Language", "Language / 语言"));
        if (GUI.Button(new Rect(r.x + 180, r.y + 28, 120, 26), "简体中文")) Result(TrySetLanguage(FleetLanguage.ZhCN, out var error), error);
        if (GUI.Button(new Rect(r.x + 310, r.y + 28, 120, 26), "English")) Result(TrySetLanguage(FleetLanguage.En, out var error), error);
        GUI.Label(new Rect(r.x + 16, r.y + 60, width - 32, 44), L("更改后自动保存。点击快捷键后按组合键；Esc 取消。F1–F10 / J 保留；原版冲突键不可用。", "Changes save immediately. Click a shortcut and press a combination; Esc cancels. F1–F10 / J are reserved; native conflicts are blocked."), wrapped);
        // Generated BeginScrollView/EndScrollView overloads are throw stubs in this
        // game's IL2CPP assemblies. Explicit pages keep all eight actions accessible.
        int rows = Math.Clamp((int)((height - 278) / 76), 1, 4);
        int pages = (actions.Length + rows - 1) / rows;
        page = Math.Clamp(page, 0, pages - 1);
        var rowWidth = width - 36;
        float rowHeight = Math.Min(76, Math.Max(28, (height - 278) / rows));
        for (var i = page * rows; i < Math.Min(actions.Length, (page + 1) * rows); i++)
        {
            var action = actions[i]; var y = r.y + 110 + (i - page * rows) * rowHeight;
            GUI.Label(new Rect(r.x + 16, y, rowWidth * .46f, 26), FleetLocalization.ActionLabel(Language, action));
            if (GUI.Button(new Rect(r.x + 16 + rowWidth * .46f, y, rowWidth * .34f, 27), capture == action ? L("请按组合键…", "Press shortcut…") : Display(action))) capture = action;
            if (GUI.Button(new Rect(r.x + 16 + rowWidth * .82f, y, rowWidth * .17f, 27), L("清除", "Clear"))) { capture = null; Result(TryRebind(action, new(KeyCode.None), out var error), error); }
            var issue = issues.FirstOrDefault(x => x.Action == action);
            if (issue != null && rowHeight > 29) GUI.Label(new Rect(r.x + 16, y + 29, rowWidth, rowHeight - 29), Describe(issue), wrapped);
        }
        if (GUI.Button(new Rect(r.x + 16, r.y + height - 158, 110, 25), L("上一页", "Previous"))) { capture = null; page = (page + pages - 1) % pages; }
        GUI.Label(new Rect(r.x + 135, r.y + height - 156, width - 270, 25), L($"第 {page + 1} / {pages} 页", $"Page {page + 1} / {pages}"));
        if (GUI.Button(new Rect(r.x + width - 126, r.y + height - 158, 110, 25), L("下一页", "Next"))) { capture = null; page = (page + 1) % pages; }
        if (loadProblem != null) GUI.Label(new Rect(r.x + 16, r.y + height - 130, width - 32, 48), Describe(issues.First(x => x.Code == "Load")), wrapped);
        else GUI.Label(new Rect(r.x + 16, r.y + height - 130, width - 32, 48), L(statusZh, statusEn) + " " + statusDetail, wrapped);
        var buttonWidth = (width - 48) / 3;
        if (GUI.Button(new Rect(r.x + 12, r.y + height - 70, buttonWidth, 30), L("保存", "Save"))) Result(TrySave(out var error), error);
        if (GUI.Button(new Rect(r.x + 24 + buttonWidth, r.y + height - 70, buttonWidth, 30), L("重置快捷键", "Reset shortcuts"))) { capture = null; Result(Commit(Defaults(), Language, out var error), error); }
        if (GUI.Button(new Rect(r.x + 36 + buttonWidth * 2, r.y + height - 70, buttonWidth, 30), L("关闭", "Close"))) Close();
        GUI.Label(new Rect(r.x + 16, r.y + height - 34, width - 32, 25), L("设置窗口打开时，模组战斗快捷键暂停。", "Mod battle shortcuts pause while settings are open."));
        var e = Event.current;
        if (capture.HasValue && e != null && e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.Escape) { capture = null; e.Use(); }
            else if (e.keyCode != KeyCode.None && !IsModifier(e.keyCode))
            {
                var action = capture.Value;
                var success = TryRebind(action, new(e.keyCode, e.control, e.alt, e.shift), out var error);
                Result(success, error); if (success) capture = null; e.Use();
            }
        }
    }
    private static bool IsModifier(KeyCode key) => key == KeyCode.LeftControl || key == KeyCode.RightControl || key == KeyCode.LeftAlt || key == KeyCode.RightAlt || key == KeyCode.LeftShift || key == KeyCode.RightShift || key == KeyCode.LeftCommand || key == KeyCode.RightCommand || key == KeyCode.LeftWindows || key == KeyCode.RightWindows || key == KeyCode.AltGr;
}
