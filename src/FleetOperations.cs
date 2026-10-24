using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

[assembly: MelonInfo(typeof(FleetOps.Experimental.FleetOperations), "Fleet Operations ISOLATED EXPERIMENT", "0.0.8.1-export-r1", "Local experiment")]
[assembly: MelonPriority(300)]
[assembly: HarmonyDontPatchAll]

namespace FleetOps.Experimental;

public sealed class FleetOperations : MelonMod
{
    private static FleetOperations? instance;
    private readonly FleetOrderBook orders = new();
    private readonly GunVolleyBook gunVolleys = new();
    private readonly FleetOrderBook torpedoAim = new();
    private readonly TorpedoVolleyBook torpedoVolleys = new();
    private readonly HashSet<long> pointFailures = new();
    private readonly HashSet<long> torpedoFailures = new();
    [ThreadStatic] private static bool insideTorpedoTick;
    private bool ready, fresh, wasBattle, visible = true;
    private double clock;
    private DateTime nextSnapshot, nextRequest, nextBindingCheck, nextPublishWarning, nextFollowUpdate, nextDispersionUpdate;
    private string prefix = "", noticeZh = "先选择己方舰船，再设置瞄点。设置按钮可改键和语言。", noticeEn = "Select your ships, then set an aim point. Settings controls shortcuts and language.";
    private string[] keyConflicts = Array.Empty<string>();
    private FleetSettings? settings;
    private bool showRecommendation;
    private Ship? recommendationTarget;
    private readonly List<FleetTorpedoOverlay.Snapshot> torpedoPreviews = new();
    private readonly List<FleetFireSolution.Snapshot> recommendations = new();
    private DateTime nextTacticalUpdate;
    private string followCursorState = "OFF";
    private object? lastFollowProjection;
    private object? lastTorpedoFollowProjection;
    private bool showDispersion = true, dispersionAllGuns, dispersionFailureReported;
    private string dispersionState = "Set a gun point with K/O to see spread.";
    private readonly List<FleetDispersionOverlay.Contour> dispersionContours = new();
    private readonly List<object> dispersionPreview = new();
    private const float MaximumPointDistance = 50000;
    private readonly Rect panel = new(20, 155, 730, 400);
    private string L(string zh, string en) => FleetLocalization.Choose(settings?.Language ?? FleetLanguage.ZhCN, zh, en);
    private string Key(FleetAction action) => settings?.Bindings.TryGetValue(action, out var binding) == true ? binding.ToString() : "—";
    private void Notice(string zh, string en) { noticeZh = zh; noticeEn = en; }
    private sealed class TickLease
    {
        internal Ship Ship = null!;
        internal Ship.ShootMode Main, Secondary, Torpedo;
        internal IDisposable? PointScope, CompletedGunScope;
        internal int ShotsBefore;
        internal FleetOrderMode Mode;
        internal bool Released, Settled, HasGunOrder, RunTorpedoAim;
        internal void Release()
        {
            if (Released) return;
            Released = true;
            try { PointScope?.Dispose(); }
            finally
            {
                try { CompletedGunScope?.Dispose(); }
                finally
                {
                    if (Ship != null && Ship)
                    { Ship.gunMode = Main; Ship.gunSecMode = Secondary; Ship.torpedoMode = Torpedo; }
                }
            }
        }
    }

    public override void OnInitializeMelon()
    {
        instance = this;
        try
        {
            prefix = RequireExperiment();
            settings = new FleetSettings(prefix);
            Patch(typeof(BattleManager), nameof(BattleManager.PrepareBattle), Type.EmptyTypes, nameof(NewBattle));
            Patch(typeof(BattleManager), nameof(BattleManager.PrepareBattleFromSave), new[] { typeof(GameManager.RealBattleSave) }, nameof(LoadedBattle));
            HarmonyInstance.Patch(AccessTools.Method(typeof(Ship), nameof(Ship.AimAndShoot), new[] { typeof(bool) }),
                prefix: new HarmonyMethod(typeof(FleetOperations), nameof(AimPrefix)),
                postfix: new HarmonyMethod(typeof(FleetOperations), nameof(AimPostfix)),
                finalizer: new HarmonyMethod(typeof(FleetOperations), nameof(AimFinalizer)));
            FleetDamageEvidence.Install(HarmonyInstance, Emit);
            FleetPointBridge.Install(HarmonyInstance, Emit);
            ready = true;
            Emit("EXPERIMENT_READY", new { processId = Process.GetCurrentProcess().Id, prefix,
                bindings = settings.Bindings.ToDictionary(k => k.Key.ToString(), v => v.Value.ToString()),
                language = settings.Language.ToString(), runtimeAcceptance = "USER_REVIEW_PENDING" });
        }
        catch (Exception e) { LoggerInstance.Error("FLEET_OPS_DISABLED " + e); ready = false; }
    }

    private void Patch(Type owner, string name, Type[] args, string before) => HarmonyInstance.Patch(
        AccessTools.Method(owner, name, args) ?? throw new MissingMethodException(owner.Name, name), prefix: new HarmonyMethod(typeof(FleetOperations), before));
    private static void NewBattle() { if (instance != null) { instance.Reset(); instance.fresh = true; instance.Emit("FRESH_BATTLE", new { }); } }
    private static void LoadedBattle() { if (instance != null) { instance.Reset(); instance.fresh = false; instance.Emit("LOADED_BATTLE_DISABLED", new { }); } }
    private void Reset() { orders.Clear(); gunVolleys.Clear(); torpedoAim.Clear(); torpedoVolleys.Clear(); pointFailures.Clear(); torpedoFailures.Clear(); clock = 0; followCursorState = "OFF"; lastFollowProjection = lastTorpedoFollowProjection = null; dispersionContours.Clear(); dispersionPreview.Clear(); torpedoPreviews.Clear(); recommendations.Clear(); recommendationTarget = null; dispersionFailureReported = false; dispersionState = ""; nextDispersionUpdate = nextTacticalUpdate = DateTime.MinValue; FleetPointBridge.Reset(); }
    private bool BattleReady => ready && fresh && GameManager.IsBattle && GameManager.IsCustomBattle;
    private static bool OwnedAlive(Ship s) => s != null && s && s.player != null && s.player.isMain && s.state == Ship.State.Alive && s.modelState == Ship.ModelState.Battle;

    public override void OnUpdate()
    {
        if (!ready) return;
        try
        {
            RequireExperiment();
            if (settings != null && Application.isFocused && (settings.IsOpen || !Util.FocusIsInInputField())
                && settings.IsPressed(FleetAction.Settings)) settings.ToggleOpen();
            if (DateTime.UtcNow >= nextBindingCheck)
            {
                nextBindingCheck = DateTime.UtcNow.AddSeconds(1);
                var native = NativeBindings().Where(b => b.Mode != "Campaign" && b.Mode != "Constructor")
                    .Where(b => Enum.TryParse<KeyCode>(b.Code, out _))
                    .Select(b => new FleetNativeShortcut(b.Name, Enum.Parse<KeyCode>(b.Code))).ToArray();
                settings?.RefreshNativeBindings(native);
                var conflicts = settings?.Issues.Select(i => i.ToString()).ToArray() ?? Array.Empty<string>();
                if (!conflicts.SequenceEqual(keyConflicts))
                {
                    keyConflicts = conflicts;
                    Emit("KEY_CONFLICT_CHECK", new { conflicts });
                    if (conflicts.Length > 0) Notice("存在快捷键冲突或配置问题，请打开设置修正。", "Shortcut/configuration issue: open Settings to resolve it.");
                }
            }
            if (GameManager.IsBattle)
            {
                wasBattle = true;
                if (!TimeControl.IsPaused()) clock += Math.Clamp(Time.deltaTime, 0, .25f);
                FleetPointBridge.Tick();
            }
            else if (wasBattle) { Reset(); wasBattle = false; fresh = false; }
            if (BattleReady) { CaptureRecommendationTarget(); UpdateMouseFollow(); }
            if (BattleReady && InputAllowed())
            {
                try
                {
                    if (settings?.IsPressed(FleetAction.Restore) == true) Restore("hotkey");
                    else if (settings?.IsPressed(FleetAction.TorpedoFollow) == true) ToggleTorpedoFollow();
                    else if (settings?.IsPressed(FleetAction.TorpedoFire) == true) FireTorpedoes("hotkey");
                    else if (settings?.IsPressed(FleetAction.Follow) == true) ToggleMouseFollow();
                    else if (settings?.IsPressed(FleetAction.Aim) == true) AimFromCursor();
                    else if (settings?.IsPressed(FleetAction.Fire) == true) Fire("hotkey");
                    else if (settings?.IsPressed(FleetAction.Recommendation) == true) ToggleRecommendations();
                }
                catch (InvalidOperationException e) { Notice("指令未执行：" + e.Message, "Command rejected: " + e.Message); Emit("COMMAND_REJECTED", new { reason = e.Message }); }
            }
            if (DateTime.UtcNow >= nextRequest)
            { nextRequest = DateTime.UtcNow.AddMilliseconds(100); ReadRequest(); }
            if (BattleReady) { UpdateDispersionPreview(); UpdateTacticalPreview(); }
            if (BattleReady && DateTime.UtcNow >= nextSnapshot)
            {
                nextSnapshot = DateTime.UtcNow.AddSeconds(1);
                WriteSnapshot();
            }
        }
        catch (Exception e) { Emit("UPDATE_ERROR", new { error = e.ToString() }); }
    }

    private bool InputAllowed() => settings?.InputBlocked != true && Application.isFocused && GameManager.CanHandleKeyboardInput() && !Util.FocusIsInInputField() && GUIUtility.hotControl == 0;
    private sealed record NativeBinding(string Name, string Code, string Mode);
    private static NativeBinding[] NativeBindings()
    {
        var bindings = G.settings?.Bindings;
        if (bindings == null) return Array.Empty<NativeBinding>();
        return typeof(Settings.KeyBindings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(Settings.KeyBinding) && p.GetIndexParameters().Length == 0)
            .Select(p => new { Property = p.Name, Value = p.GetValue(bindings) as Settings.KeyBinding })
            .Where(x => x.Value != null).Select(x => new NativeBinding(x.Property, x.Value!.Code.ToString(), x.Value.ControlMode.ToString())).ToArray();
    }
    private static object InputSnapshot() => new { focused = Application.isFocused, keyboardAllowed = GameManager.CanHandleKeyboardInput(),
        mouseAllowed = GameManager.CanHandleMouseInput(), inputFieldFocused = Util.FocusIsInInputField(), hotControl = GUIUtility.hotControl,
        paused = TimeControl.IsPaused(), mouse = V(Input.mousePosition), screen = new[] { Screen.width, Screen.height } };
    private static bool ControlHeld() => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

    private Ship[] Selected()
    {
        if (!BattleReady || G.ui?.selectedShips == null) return Array.Empty<Ship>();
        var all = new Dictionary<long, Ship>(); var candidates = new List<FleetCandidate>();
        foreach (Ship s in G.ui.selectedShips)
        {
            if (s == null || !s) continue;
            long p = s.Pointer.ToInt64();
            if (!all.ContainsKey(p)) all[p] = s;
            candidates.Add(new(p, s.id.ToString(), s.player != null && s.player.isMain, s.state == Ship.State.Alive, s.modelState == Ship.ModelState.Battle));
        }
        return SelectionRules.Capture(candidates).Select(x => all[x.Pointer]).ToArray();
    }
    private Ship[] RequireSelection()
    {
        if (!BattleReady) throw new InvalidOperationException(L("需要新建独立自定义战斗。", "New isolated custom battle required."));
        Ship[] ships = Selected();
        if (ships.Length == 0) throw new InvalidOperationException(L("未选择存活的己方舰船。", "No living player ships selected."));
        return ships;
    }
    private (NVector3 Point, object Evidence) ProjectCursor(bool preferGunSurface = true)
    {
        if (settings?.InputBlocked == true || !GameManager.CanHandleMouseInput()) throw new InvalidOperationException(L("鼠标正在操作界面。", "Mouse input is currently captured."));
        Vector3 mouse = Input.mousePosition;
        var guiMouse = new Vector2(mouse.x, Screen.height - mouse.y);
        if (mouse.x < 0 || mouse.y < 0 || mouse.x >= Screen.width || mouse.y >= Screen.height || panel.Contains(guiMouse)
            || settings?.IsOpen == true && settings.CurrentRect.Contains(guiMouse))
            throw new InvalidOperationException(L("请将鼠标移到战斗海面视口。", "Mouse must be over the battle sea viewport."));
        Camera camera = G.cam?.cameraComp ?? throw new InvalidOperationException(L("战斗相机不可用。", "Battle camera unavailable."));
        Ray ray = camera.ScreenPointToRay(mouse);
        // A sea-plane-only ray can land hundreds of metres behind the visible upper hull.
        // Read only currently visible native combat surfaces. Fogged ships remain sea points.
        if (preferGunSurface && FleetGunImpactBridge.TryProjectCursorRay(ray.origin, ray.direction, MaximumPointDistance, out Vector3 surface, out string? body))
            return (N(surface), new { mouse = V(mouse), rayOrigin = V(ray.origin), rayDirection = V(ray.direction), point = V(surface), surfaceKind = "VISIBLE_SHIP", body });
        if (!SeaProjection.TryProject(N(ray.origin), N(ray.direction), MaximumPointDistance, out var point)) throw new InvalidOperationException(L("鼠标射线没有有效的近处海面交点。", "Mouse ray has no nearby forward sea intersection."));
        return (point, new { mouse = V(mouse), rayOrigin = V(ray.origin), rayDirection = V(ray.direction), point = new[] { point.X, point.Y, point.Z }, surfaceKind = "SEA", body = (string?)null });
    }
    private void AimFromCursor()
    {
        var projected = ProjectCursor();
        var point = projected.Point;
        Ship[] ships = RequireSelection();
        orders.Aim(ships.Select(x => x.Pointer.ToInt64()), point, clock); pointFailures.Clear();
        gunVolleys.Clear(ships.Select(x => x.Pointer.ToInt64()));
        Notice($"已为 {ships.Length} 艘舰设点；{Key(FleetAction.Fire)} 向该点齐射。", $"POINT AIM: {ships.Length} ships; {Key(FleetAction.Fire)} to fire there.");
        Emit("AIM_COMMAND", new { trigger = "hotkey", projection = projected.Evidence, selection = Ids(ships) });
        WriteSnapshot();
    }
    private void ToggleMouseFollow()
    {
        Ship[] ships = RequireSelection();
        long[] ids = ships.Select(x => x.Pointer.ToInt64()).ToArray();
        bool disabling = ids.All(orders.IsFollowing);
        NVector3 point;
        object? projection = null;
        if (disabling) point = orders.Get(ids[0], clock)?.Point ?? ForwardPoint(ships[0]);
        else { var projected = ProjectCursor(); point = projected.Point; projection = projected.Evidence; }
        bool enabled = orders.ToggleFollow(ids, point, clock);
        if (enabled) gunVolleys.Clear(ids);
        pointFailures.Clear();
        followCursorState = enabled ? "TRACKING" : orders.FollowingIds.Count + torpedoAim.FollowingIds.Count == 0 ? "OFF" : "TRACKING_OTHER_SHIPS";
        Notice(enabled ? $"火炮跟随已开：{ships.Length} 艘舰；{Key(FleetAction.Follow)} 停止，{Key(FleetAction.Fire)} 开火。" : $"火炮跟随已关：{ships.Length} 艘舰保持最后瞄点。",
            enabled ? $"GUN FOLLOW ON: {ships.Length} ships; {Key(FleetAction.Follow)} stops, {Key(FleetAction.Fire)} fires." : $"GUN FOLLOW OFF: {ships.Length} ships hold their last point.");
        Emit("FOLLOW_COMMAND", new { trigger = "hotkey", enabled, selection = Ids(ships), projection, following = orders.FollowingIds });
        WriteSnapshot();
    }
    private void UpdateMouseFollow()
    {
        if (orders.FollowingIds.Count == 0 && torpedoAim.FollowingIds.Count == 0) { followCursorState = "OFF"; return; }
        if (DateTime.UtcNow < nextFollowUpdate) return;
        nextFollowUpdate = DateTime.UtcNow.AddMilliseconds(33);
        // An open menu, another window, an input field, or a sky/UI cursor holds the last valid point.
        if (!InputAllowed()) { followCursorState = "HOLD_INPUT_CAPTURED"; return; }
        bool tracking = false, held = false;
        if (orders.FollowingIds.Count > 0)
        {
            try
            {
                var projected = ProjectCursor(); orders.UpdateFollow(projected.Point, clock);
                lastFollowProjection = projected.Evidence; tracking = true;
            }
            catch (InvalidOperationException) { held = true; }
        }
        if (torpedoAim.FollowingIds.Count > 0)
        {
            try
            {
                var projected = ProjectCursor(false); torpedoAim.UpdateFollow(projected.Point, clock);
                lastTorpedoFollowProjection = projected.Evidence; tracking = true;
            }
            catch (InvalidOperationException) { held = true; }
        }
        followCursorState = tracking ? held ? "TRACKING_PARTIAL_HOLD" : "TRACKING" : "HOLD_INVALID_CURSOR";
    }
    private static bool HasTorpedoLaunchers(Ship ship)
    {
        if (ship.parts != null) foreach (Part p in ship.parts) if (p?.data?.isTorpedo == true) return true;
        return false;
    }
    private Ship[] RequireTorpedoSelection()
    {
        Ship[] ships = RequireSelection().Where(HasTorpedoLaunchers).ToArray();
        if (ships.Length == 0) throw new InvalidOperationException(L("选中存活舰船中没有鱼雷发射器。", "No selected living ship has torpedo launchers."));
        return ships;
    }
    private void ToggleTorpedoFollow()
    {
        Ship[] ships = RequireTorpedoSelection();
        long[] ids = ships.Select(x => x.Pointer.ToInt64()).ToArray();
        bool disabling = ids.All(torpedoAim.IsFollowing);
        NVector3 point;
        object? projection = null;
        if (disabling) point = torpedoAim.Get(ids[0], clock)?.Point ?? ForwardPoint(ships[0]);
        else { var projected = ProjectCursor(false); point = projected.Point; projection = projected.Evidence; }
        bool enabled = torpedoAim.ToggleFollow(ids, point, clock);
        torpedoFailures.Clear();
        followCursorState = orders.FollowingIds.Count + torpedoAim.FollowingIds.Count == 0 ? "OFF" : "TRACKING";
        Notice(enabled ? $"鱼雷跟随已开：{ships.Length} 艘舰；{Key(FleetAction.TorpedoFollow)} 停止，{Key(FleetAction.TorpedoFire)} 发射。" : $"鱼雷跟随已关：{ships.Length} 艘舰保持最后鱼雷瞄点。",
            enabled ? $"TORPEDO FOLLOW ON: {ships.Length} ships; {Key(FleetAction.TorpedoFollow)} stops, {Key(FleetAction.TorpedoFire)} launches." : $"TORPEDO FOLLOW OFF: {ships.Length} ships hold their last torpedo point.");
        Emit("TORPEDO_FOLLOW_COMMAND", new { trigger = "hotkey", enabled, selection = Ids(ships), projection, following = torpedoAim.FollowingIds });
        WriteSnapshot();
    }
    private void FireTorpedoes(string trigger)
    {
        Ship[] ships = RequireTorpedoSelection();
        var fallbacks = new List<object>();
        foreach (Ship ship in ships)
        {
            long id = ship.Pointer.ToInt64();
            if (torpedoAim.Get(id, clock)?.Point.HasValue == true) continue;
            NVector3 point;
            string reason;
            try { point = ProjectCursor(false).Point; reason = "CURRENT_MOUSE_SEA_POINT"; }
            catch (InvalidOperationException) { point = ForwardPoint(ship, 500f); reason = "NO_TORPEDO_POINT_FORWARD_500M_FALLBACK"; }
            torpedoAim.Aim(new[] { id }, point, clock);
            fallbacks.Add(new { ship = ship.id.ToString(), point = new[] { point.X, point.Y, point.Z }, reason });
        }
        torpedoVolleys.Request(ships.Select(x => x.Pointer.ToInt64()), clock);
        torpedoFailures.Clear();
        Notice($"已为 {ships.Length} 艘舰提交鱼雷发射；火炮指令不变。", $"TORPEDO VOLLEY QUEUED: {ships.Length} ships; gun orders unchanged.");
        Emit("TORPEDO_FIRE_COMMAND", new { trigger, selection = Ids(ships), fallbacks, before = ships.Select(SnapshotShip).ToArray() });
        WriteSnapshot();
    }
    private static NVector3 ForwardPoint(Ship ship, float distance = 3000f)
    {
        Vector3 p = ship.transform.position, d = ship.transform.forward;
        d.y = 0;
        if (d.sqrMagnitude < .0001f) d = Vector3.forward;
        d = d.normalized;
        return new NVector3(p.x + d.x * distance, 0, p.z + d.z * distance);
    }
    private static bool HasVisibleGunTarget(Ship ship)
    {
        bool Valid(Ship? t) => t != null && t && t.state == Ship.State.Alive && ship.IsEnemyFor(t) && t.IsVisibleFor(ship);
        if (ship.enemies != null) foreach (var t in ship.enemies) if (t.Key?.isGun == true && Valid(t.Value?.target)) return true;
        if (ship.enemiesLeftSide != null) foreach (var t in ship.enemiesLeftSide) if (t.Key?.isGun == true && Valid(t.Value?.target)) return true;
        return false;
    }
    private void Fire(string trigger)
    {
        Ship[] ships = RequireSelection().Where(s => GunShots(s).Length > 0).ToArray();
        if (ships.Length == 0) throw new InvalidOperationException(L("选中存活舰船中没有火炮。", "No selected living ship has guns."));
        var fallbacks = new List<object>();
        foreach (Ship ship in ships)
        {
            long id = ship.Pointer.ToInt64();
            if (orders.Get(id, clock)?.Point.HasValue == true || HasVisibleGunTarget(ship)) continue;
            NVector3 point = ForwardPoint(ship);
            orders.Aim(new[] { id }, point, clock);
            fallbacks.Add(new { ship = ship.id.ToString(), point = new[] { point.X, point.Y, point.Z }, reason = "NO_VISIBLE_NATIVE_TARGET_FORWARD_SEA_POINT" });
        }
        foreach (Ship ship in ships) gunVolleys.Request(ship.Pointer.ToInt64(), GunShots(ship));
        FleetSalvoOverlay.BeginRequest(ships.Select(s => s.Pointer.ToInt64()));
        orders.Fire(ships.Select(x => x.Pointer.ToInt64()), clock); pointFailures.Clear();
        Notice($"已为 {ships.Length} 艘舰提交逐炮塔齐射；保留弹药、装填和射界限制。", $"FIRE QUEUED: {ships.Length} ships. One volley per turret; ammo/reload/arcs retained.");
        Emit("FIRE_COMMAND", new { trigger, selection = Ids(ships), fallbacks, before = ships.Select(SnapshotShip).ToArray() });
        WriteSnapshot();
    }
    private void Restore(string trigger)
    {
        Ship[] ships = RequireSelection();
        orders.Restore(ships.Select(x => x.Pointer.ToInt64())); pointFailures.Clear();
        gunVolleys.Clear(ships.Select(x => x.Pointer.ToInt64()));
        torpedoAim.Restore(ships.Select(x => x.Pointer.ToInt64())); torpedoVolleys.Clear(ships.Select(x => x.Pointer.ToInt64())); torpedoFailures.Clear();
        Notice($"已恢复 {ships.Length} 艘选中舰的原版瞄准。", $"RESTORED native aim for {ships.Length} selected ships.");
        Emit("RESTORE_COMMAND", new { trigger, selection = Ids(ships) }); WriteSnapshot();
    }
    private void Hold()
    {
        Ship[] ships = RequireSelection();
        orders.Restore(ships.Select(x => x.Pointer.ToInt64()));
        gunVolleys.Clear(ships.Select(x => x.Pointer.ToInt64()));
        torpedoAim.Restore(ships.Select(x => x.Pointer.ToInt64())); torpedoVolleys.Clear(ships.Select(x => x.Pointer.ToInt64()));
        foreach (Ship s in ships) { s.gunMode = Ship.ShootMode.Off; s.gunSecMode = Ship.ShootMode.Off; s.torpedoMode = Ship.ShootMode.Off; }
        Emit("TEST_HOLD_SELECTED", new { selection = Ids(ships) }); WriteSnapshot();
    }

    // Test preparation only: real hotkeys always read the native selectedShips list.
    private void SelectTestPair()
    {
        if (!BattleReady) throw new InvalidOperationException("New isolated custom battle required.");
        if (ControlHeld() || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)
            || Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) throw new InvalidOperationException("Release modifiers before test selection.");
        var eligible = new List<Ship>();
        foreach (Ship s in Ship.AllShips) if (OwnedAlive(s) && s.division != null) eligible.Add(s);
        var divisions = eligible.GroupBy(s => s.division.Pointer.ToInt64()).Take(3).ToArray();
        if (divisions.Length < 3) throw new InvalidOperationException("Test selection needs at least 3 friendly divisions; one stays unselected.");
        long[] divisionIds = divisions.Take(2).Select(g => g.Key).ToArray();
        var pair = new Dictionary<long, Ship>();
        foreach (var group in divisions.Take(2))
            foreach (Ship s in group.First().division.ships)
                if (OwnedAlive(s)) pair.TryAdd(s.Pointer.ToInt64(), s);
        var native = new Il2CppSystem.Collections.Generic.List<Ship>();
        foreach (Ship s in pair.Values) native.Add(s);
        G.ui.SelectShips(native, pair.Values.First(), false, false);
        Ship[] actual = Selected();
        if (!actual.Select(s => s.Pointer.ToInt64()).OrderBy(x => x).SequenceEqual(pair.Keys.OrderBy(x => x)))
            throw new InvalidOperationException("Native multi-selection did not match the test preparation list.");
        Emit("TEST_SELECTION_PREPARED", new { preparation = "native Ui.SelectShips API; not a mouse drag acceptance", divisionIds, submitted = Ids(pair.Values), actual = Ids(actual) });
        WriteSnapshot();
    }

    private static bool AimPrefix(Ship __instance, bool isPaused, out TickLease? __state)
    {
        __state = null; var app = instance;
        if (insideTorpedoTick || app?.BattleReady != true || isPaused || !OwnedAlive(__instance)) return true;
        FleetOrder? order = app.orders.Get(__instance.Pointer.ToInt64(), app.clock);
        if (order == null || order.Mode == FleetOrderMode.AimPoint) app.gunVolleys.Clear(new[] { __instance.Pointer.ToInt64() });
        bool runTorpedoes = app.torpedoAim.Get(__instance.Pointer.ToInt64(), app.clock)?.Point.HasValue == true;
        if (order == null && !runTorpedoes) return true;
        var lease = new TickLease { Ship = __instance, Main = __instance.gunMode, Secondary = __instance.gunSecMode, Torpedo = __instance.torpedoMode,
            ShotsBefore = CountShots(__instance), Mode = order?.Mode ?? FleetOrderMode.AimPoint, HasGunOrder = order != null, RunTorpedoAim = runTorpedoes };
        __state = lease;
        try
        {
            // A point scope must not redirect native torpedoes to the gun point. With no
            // gun point or separate torpedo pass, retain ordinary native torpedo firing.
            if (order?.Point.HasValue == true || runTorpedoes) __instance.torpedoMode = Ship.ShootMode.Off;
            if (order == null) return true;
            if (order.Mode != FleetOrderMode.AimPoint)
            {
                var completed = app.gunVolleys.CompletedPartIds(__instance.Pointer.ToInt64()).ToHashSet();
                var timers = new List<IntPtr>();
                if (__instance.parts != null) foreach (Part p in __instance.parts)
                    if (p?.data?.isGun == true && completed.Contains(p.Pointer.ToInt64()) && p._reloading_k__BackingField != null)
                        timers.Add(p._reloading_k__BackingField.Pointer);
                lease.CompletedGunScope = FleetPointBridge.EnterCompletedGunGate(timers);
            }
            bool point = order.Point.HasValue;
            if (point)
            {
                NVector3 n = order.Point!.Value;
                lease.PointScope = FleetPointBridge.Enter(__instance, new Vector3(n.X, n.Y, n.Z), order.Mode == FleetOrderMode.FirePoint);
            }
            var mode = order.Mode == FleetOrderMode.AimPoint ? Ship.ShootMode.Normal : Ship.ShootMode.Aggressive;
            __instance.gunMode = mode; __instance.gunSecMode = mode;
            return true;
        }
        catch (Exception e)
        {
            lease.Release();
            if (app.pointFailures.Add(__instance.Pointer.ToInt64())) app.Emit("ORDER_BLOCKED", new { ship = __instance.id.ToString(), mode = order?.Mode.ToString() ?? "NativeGuns", error = e.ToString() });
            return false; // Never revert an unsuccessful point order to a shot at its real anchor.
        }
    }
    private static void Settle(TickLease lease)
    {
        if (lease.Settled) return;
        lease.Settled = true;
        try
        {
            int after = CountShots(lease.Ship);
            if (lease.HasGunOrder && lease.Mode != FleetOrderMode.AimPoint)
            {
                var app = instance!;
                long id = lease.Ship.Pointer.ToInt64();
                var observed = app.gunVolleys.Observe(id, GunShots(lease.Ship));
                if (observed.NewlyCompletedIds.Count > 0)
                    app.Emit("GUN_PART_VOLLEYS_OBSERVED", new { ship = lease.Ship.id.ToString(), mode = lease.Mode.ToString(), observed.NewlyCompletedIds, observed.CompletedIds, observed.AllCompleted, shotsBefore = lease.ShotsBefore, shotsAfter = after });
                if (observed.AllCompleted)
                {
                    app.orders.MarkFired(id); app.gunVolleys.Clear(new[] { id });
                    app.Emit("VOLLEY_OBSERVED", new { ship = lease.Ship.id.ToString(), mode = lease.Mode.ToString(), completedParts = observed.CompletedIds, shotsBefore = lease.ShotsBefore, shotsAfter = after });
                }
            }
        }
        catch (Exception e) { instance?.Emit("VOLLEY_READ_ERROR", new { error = e.Message }); }
    }
    private static void AimPostfix(TickLease? __state)
    {
        if (__state == null || __state.Released) return;
        try { Settle(__state); }
        finally { __state.Release(); }
        if (__state.RunTorpedoAim) instance?.AimTorpedoes(__state.Ship);
    }
    private static Exception? AimFinalizer(Exception? __exception, TickLease? __state)
    {
        if (__state != null)
        {
            try { Settle(__state); }
            finally { __state.Release(); }
        }
        if (__exception != null) instance?.Emit("NATIVE_AIM_EXCEPTION", new { error = __exception.ToString() });
        return __exception;
    }
    private static int CountShots(Ship ship)
    { int total = 0; if (ship.parts != null) foreach (Part p in ship.parts) if (p?.data?.isGun == true) total += p._statistics_k__BackingField?.shots ?? 0; return total; }
    private static GunPartShots[] GunShots(Ship ship)
    {
        var result = new List<GunPartShots>();
        if (ship.parts != null) foreach (Part p in ship.parts)
            if (p?.data?.isGun == true) result.Add(new(p.Pointer.ToInt64(), p._statistics_k__BackingField?.shots ?? 0));
        return result.ToArray();
    }
    private void UpdateDispersionPreview()
    {
        if (DateTime.UtcNow < nextDispersionUpdate) return;
        nextDispersionUpdate = DateTime.UtcNow.AddMilliseconds(100);
        dispersionContours.Clear(); dispersionPreview.Clear();
        if (!showDispersion) { dispersionState = L("散布显示已关，实际散布仍生效。", "Spread display off; shot dispersion stays on."); return; }
        int groups = 0, unavailable = 0, fallbackProfiles = 0;
        try
        {
            foreach (Ship ship in Selected())
            {
                FleetOrder? order = orders.Get(ship.Pointer.ToInt64(), clock);
                if (order?.Point is not NVector3 point) continue;
                var guns = new List<Part>();
                if (ship.parts != null) foreach (Part p in ship.parts)
                    if (p?.data?.isGun == true) guns.Add(p);
                if (guns.Count == 0) continue;
                float largest = guns.Max(p => p.data.caliber);
                foreach (var group in guns.Where(p => dispersionAllGuns || p.data.caliber == largest).GroupBy(p => p.data.caliber))
                {
                    groups++;
                    if (dispersionContours.Count >= 16) continue;
                    FleetGunDispersionGeometry.Profile? outer = null;
                    object? parameters = null;
                    var partIds = new List<long>();
                    foreach (Part gun in group)
                    {
                        // Same provider, native range origin and aim XYZ as the Shell.Init sampler.
                        // No GetCalcHitPoint, DistribNorm or random delegate is called by preview.
                        if (!FleetGunDispersion.TryProfile(gun, FleetGunDispersion.RangeOrigin(gun),
                            new Vector3(point.X, point.Y, point.Z), out var profile, out object evidence))
                        {
                            unavailable++;
                            dispersionPreview.Add(new { ship = ship.id.ToString(), caliber = group.Key,
                                gunPointer = gun.Pointer.ToInt64(), valid = false, parameters = evidence });
                            continue;
                        }
                        if (profile.CalibrationMethod != FleetGunDispersionGeometry.NativeReferenceCalibration) fallbackProfiles++;
                        partIds.Add(gun.Pointer.ToInt64());
                        if (!outer.HasValue || profile.Radius > outer.Value.Radius) { outer = profile; parameters = evidence; }
                    }
                    if (!outer.HasValue) continue;
                    var chosen = outer.Value;
                    int index = dispersionContours.Count;
                    Color color = index % 3 == 0 ? new Color(.2f, 1f, .75f, .8f)
                        : index % 3 == 1 ? new Color(1f, .85f, .2f, .75f) : new Color(.35f, .7f, 1f, .75f);
                    string scope = dispersionAllGuns ? L("火炮", "GUN") : L("主炮", "MAIN");
                    dispersionContours.Add(new FleetDispersionOverlay.Contour {
                        Label = $"{scope} {group.Key:F0}mm  R{chosen.Radius:F0}m · " +
                            (chosen.CalibrationMethod == FleetGunDispersionGeometry.NativeReferenceCalibration
                                ? $"{L("20m靶", "20m target")} {chosen.NativeChance:P1}"
                                : L("距离散布后备", "DISTANCE FALLBACK")), WorldCenter = new Vector3(chosen.ImpactCenter.X, 0, chosen.ImpactCenter.Z),
                        Along = Vector3.forward, Across = Vector3.right, AlongRadius = chosen.Radius, AcrossRadius = chosen.Radius, Color = color });
                    dispersionPreview.Add(new { ship = ship.id.ToString(), caliber = group.Key, scope,
                        sourceGunPointers = partIds, aimCenter = new[] { point.X, point.Y, point.Z },
                        impactCenter = new[] { chosen.ImpactCenter.X, chosen.ImpactCenter.Y, chosen.ImpactCenter.Z },
                        outerRadius = chosen.Radius, parameters, nativeRandomCalls = 0,
                        meaning = "Maximum support radius across the displayed ship/caliber group; not a probability circle." });
                }
            }
            dispersionState = groups == 0 ? L("设置火炮瞄点后显示实时散布。", "Set a gun point to see live spread.")
                : L($"实时散布 {dispersionContours.Count}/{groups} 圈；海面外边界", $"Live spread {dispersionContours.Count}/{groups} circles; sea-surface outer bound");
            if (unavailable > 0) dispersionState += L($"；{unavailable} 座原版精度尚未就绪", $"; {unavailable} native gun profiles unavailable");
            if (fallbackProfiles > 0) dispersionState += L($"；{fallbackProfiles} 座使用距离散布后备", $"; {fallbackProfiles} guns use distance fallback");
            if (FleetGunDispersion.Faulted) dispersionState = L("精度引用恢复失败；请正常退出并重启本实验。", "Precision restore failed; close and restart this experiment.");
            if (FleetPointBridge.Faulted) dispersionState = L("武器控制桥发生故障，指令未生效；请正常退出并重启。", "Weapon bridge fault: commands are blocked. Close and restart normally.");
        }
        catch (Exception e)
        {
            dispersionContours.Clear(); dispersionPreview.Clear(); dispersionState = L("散布预览暂不可用。", "Spread preview unavailable.");
            if (!dispersionFailureReported) { dispersionFailureReported = true; Emit("DISPERSION_PREVIEW_ERROR", new { error = e.ToString() }); }
        }
    }

    private void CaptureRecommendationTarget()
    {
        // Cache identity only after explicit native selection. Never treat hovering or a weapon's
        // auto-target as an explicit enemy selection, and never read a fogged target's motion.
        bool Candidate(Ship? s) => s != null && s && s.player != null && !s.player.isMain
            && s.state == Ship.State.Alive && s.modelState == Ship.ModelState.Battle
            && s.IsVisibleFor(PlayerController.Instance);
        Ship? main = G.ui?.selectedShipMain;
        if (Candidate(main)) { recommendationTarget = main; return; }
        if (G.ui?.selectedShips != null)
            foreach (Ship s in G.ui.selectedShips) if (Candidate(s)) { recommendationTarget = s; return; }
    }

    private bool RecommendationTargetVisible() => recommendationTarget != null && recommendationTarget
        && recommendationTarget.state == Ship.State.Alive && recommendationTarget.modelState == Ship.ModelState.Battle
        && recommendationTarget.IsVisibleFor(PlayerController.Instance);

    private void ToggleRecommendations()
    {
        showRecommendation = !showRecommendation; nextTacticalUpdate = DateTime.MinValue;
        if (!showRecommendation) recommendations.Clear();
        Notice(showRecommendation ? "射击建议已开：先选择可见敌舰，再选己方发射舰。" : "射击建议已关。",
            showRecommendation ? "Recommendations ON: select a visible enemy, then your firing ships." : "Recommendations OFF.");
    }

    private void UpdateTacticalPreview()
    {
        if (DateTime.UtcNow < nextTacticalUpdate) return;
        nextTacticalUpdate = DateTime.UtcNow.AddMilliseconds(100);
        torpedoPreviews.Clear(); recommendations.Clear();
        string language = settings?.Language == FleetLanguage.En ? "en" : "zh-CN";
        foreach (Ship ship in Selected().Take(16))
        {
            long id = ship.Pointer.ToInt64();
            if (torpedoAim.Get(id, clock)?.Point is NVector3 point)
            {
                try { torpedoPreviews.Add(FleetTorpedoOverlay.Build(ship, new Vector3(point.X, 0, point.Z), language)); }
                catch (Exception ex) { Emit("TORPEDO_PREVIEW_ERROR", new { error = ex.Message }); }
            }
            if (showRecommendation && RecommendationTargetVisible())
            {
                try { recommendations.Add(FleetFireSolution.Build(ship, recommendationTarget!, language)); }
                catch (Exception ex) { Emit("FIRE_SOLUTION_ERROR", new { error = ex.Message }); }
            }
        }
    }

    private string FollowLabel() => followCursorState switch {
        "OFF" => L("关闭", "OFF"), "TRACKING" => L("跟随中", "TRACKING"),
        "TRACKING_OTHER_SHIPS" => L("其他舰仍在跟随", "OTHER SHIPS TRACKING"),
        "TRACKING_PARTIAL_HOLD" => L("部分保持最后瞄点", "PARTIAL HOLD"),
        "HOLD_INPUT_CAPTURED" => L("界面操作，保持瞄点", "HOLD: UI INPUT"),
        _ => L("鼠标无效，保持瞄点", "HOLD: INVALID CURSOR") };
    private static int CountTorpedoShots(Ship ship)
    { int total = 0; if (ship.parts != null) foreach (Part p in ship.parts) if (p?.data?.isTorpedo == true) total += p._statistics_k__BackingField?.shots ?? 0; return total; }
    private static int CountTorpedoAmmo(Ship ship)
    { int total = 0; if (ship._ammo_k__BackingField != null) foreach (var a in ship._ammo_k__BackingField) if (a.Key?.isTorpedo == true) total += a.Value.ap + a.Value.he; return total; }
    private void AimTorpedoes(Ship ship)
    {
        if (insideTorpedoTick || !BattleReady || !OwnedAlive(ship) || TimeControl.IsPaused()) return;
        long id = ship.Pointer.ToInt64();
        FleetOrder? aim = torpedoAim.Get(id, clock);
        if (aim?.Point is not NVector3 point) return;
        bool launch = torpedoVolleys.IsPending(id, clock);
        var main = ship.gunMode; var secondary = ship.gunSecMode; var torpedo = ship.torpedoMode;
        int shotsBefore = CountTorpedoShots(ship), ammoBefore = CountTorpedoAmmo(ship);
        IDisposable? scope = null;
        try
        {
            ship.gunMode = Ship.ShootMode.Off; ship.gunSecMode = Ship.ShootMode.Off;
            ship.torpedoMode = launch ? Ship.ShootMode.Aggressive : Ship.ShootMode.Normal;
            scope = FleetPointBridge.Enter(ship, new Vector3(point.X, point.Y, point.Z), launch, true);
            insideTorpedoTick = true;
            ship.AimAndShoot(false);
        }
        catch (Exception e)
        {
            if (torpedoFailures.Add(id)) Emit("TORPEDO_ORDER_BLOCKED", new { ship = ship.id.ToString(), launch, error = e.ToString() });
        }
        finally
        {
            try
            {
                int shotsAfter = CountTorpedoShots(ship), ammoAfter = CountTorpedoAmmo(ship);
                if (launch && (shotsAfter > shotsBefore || ammoAfter < ammoBefore))
                {
                    torpedoVolleys.WasFired(id);
                    Emit("TORPEDO_VOLLEY_OBSERVED", new { ship = ship.id.ToString(), shotsBefore, shotsAfter, ammoBefore, ammoAfter,
                        point = new[] { point.X, point.Y, point.Z }, boundary = "Native counters/ammo observed; correlate POINT_TORPEDO_CREATE for actual projectiles." });
                }
            }
            catch (Exception e) { Emit("TORPEDO_VOLLEY_READ_ERROR", new { error = e.Message }); }
            finally
            {
                try { scope?.Dispose(); }
                finally
                {
                    insideTorpedoTick = false;
                    if (ship != null && ship) { ship.gunMode = main; ship.gunSecMode = secondary; ship.torpedoMode = torpedo; }
                }
            }
        }
    }

    private void ReadRequest()
    {
        string file = Path.Combine(prefix, "fleet_ops.request");
        if (!File.Exists(file) || File.Exists(file + ".processing")) return;
        File.Move(file, file + ".processing");
        try
        {
            string command = File.ReadAllText(file + ".processing").Trim();
            if (command == "inspect") WriteSnapshot();
            else if (command == "select-test-pair") SelectTestPair();
            else if (command == "hold-selected") Hold();
            else if (command == "fire-selected") Fire("test_queue");
            else if (command == "restore-selected") Restore("test_queue");
            else throw new InvalidOperationException("Commands: inspect, select-test-pair, hold-selected, fire-selected, restore-selected. Point command must use the actual cursor hotkey.");
            File.WriteAllText(file + ".done", JsonSerializer.Serialize(new { command, processId = Process.GetCurrentProcess().Id, at = DateTime.UtcNow.ToString("O") }));
        }
        catch (Exception e) { Emit("REQUEST_REJECTED", new { error = e.Message }); }
        finally { File.Move(file + ".processing", file + ".processed", true); }
    }
    private void WriteSnapshot()
    {
        var ships = new List<Ship>();
        if (Ship.AllShips != null) foreach (Ship s in Ship.AllShips) if (s != null && s && s.modelState == Ship.ModelState.Battle) ships.Add(s);
        var value = new { observedUtc = DateTime.UtcNow.ToString("O"), processId = Process.GetCurrentProcess().Id, frame = Time.frameCount, clock,
            game = new { GameManager.IsBattle, GameManager.IsBattleCombat, GameManager.IsCustomBattle, fresh },
            selected = Ids(Selected()), orders = orders.Entries.Select(x => new { pointer = x.Key, mode = x.Value.Mode.ToString(),
                point = x.Value.Point.HasValue ? new[] { x.Value.Point.Value.X, x.Value.Point.Value.Y, x.Value.Point.Value.Z } : null }),
            gunVolleys = gunVolleys.Entries.Select(x => new { pointer = x.Key, parts = x.Value.Values }),
            torpedoes = new { orders = torpedoAim.Entries.Select(x => new { pointer = x.Key, point = x.Value.Point.HasValue ? new[] { x.Value.Point.Value.X, x.Value.Point.Value.Y, x.Value.Point.Value.Z } : null }),
                following = torpedoAim.FollowingIds, projection = lastTorpedoFollowProjection, pending = torpedoVolleys.Entries.Select(x => new { pointer = x.Key, expiresAt = x.Value.ExpiresAt }) },
            follow = new { ids = orders.FollowingIds, state = followCursorState, projection = lastFollowProjection },
            dispersion = new { enabled = showDispersion, allGuns = dispersionAllGuns, state = dispersionState, preview = dispersionPreview,
                firedSalvos = FleetSalvoOverlay.Snapshot() },
            tacticalUi = new { recommendationEnabled = showRecommendation, selectedVisibleEnemy = RecommendationTargetVisible(),
                torpedoPreview = torpedoPreviews.Select(p => new { p.Summary, p.Evidence }).ToArray(),
                recommendations = recommendations.Select(p => new { p.Summary, p.Evidence }).ToArray() },
            configuration = new { language = settings?.Language.ToString(), bindings = settings?.Bindings.ToDictionary(k => k.Key.ToString(), v => v.Value.ToString()) },
            input = InputSnapshot(), keyConflicts, nativeBindings = NativeBindings(), ships = ships.Select(SnapshotShip).ToArray(), bridge = FleetPointBridge.Snapshot(), damageEvidence = FleetDamageEvidence.Status() };
        string json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(prefix, "fleet_ops_status.json.tmp"), json);
        try { File.Move(Path.Combine(prefix, "fleet_ops_status.json.tmp"), Path.Combine(prefix, "fleet_ops_status.json"), true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A reader without FILE_SHARE_DELETE can briefly prevent an atomic replacement on Windows.
            // Keep the last complete status; the next sample retries. Append-only evidence still records this sample.
            if (DateTime.UtcNow >= nextPublishWarning)
            { nextPublishWarning = DateTime.UtcNow.AddSeconds(10); Emit("SNAPSHOT_PUBLISH_DEFERRED", new { reason = e.Message }); }
        }
        File.AppendAllText(Path.Combine(prefix, "fleet_ops_samples.jsonl"), JsonSerializer.Serialize(value) + Environment.NewLine);
    }
    private static object SnapshotShip(Ship ship)
    {
        var guns = new List<object>();
        var torpedoLaunchers = new List<object>();
        if (ship.parts != null) foreach (Part p in ship.parts)
        {
            if (p?.data?.isTorpedo == true) torpedoLaunchers.Add(new { id = p.Id.ToString(), pointer = p.Pointer.ToInt64(), data = p.data.name,
                shots = p._statistics_k__BackingField?.shots, reloadRunning = p._reloading_k__BackingField?.isRunning,
                reloadTimer = p._reloading_k__BackingField?.Pointer.ToInt64(),
                position = V(p.transform.position), rotation = V(p.transform.eulerAngles) });
            if (p?.data?.isGun != true) continue;
            var barrels = new List<object>();
            if (p._gunBarrels_k__BackingField != null) foreach (GameObject b in p._gunBarrels_k__BackingField)
                if (b != null) barrels.Add(new { name = b.name, position = V(b.transform.position), forward = V(b.transform.forward), localEuler = V(b.transform.localEulerAngles) });
            guns.Add(new { id = p.Id.ToString(), pointer = p.Pointer.ToInt64(), data = p.data.name, caliber = p.data.caliber, nativeBarrelCount = p.data.barrels, shots = p._statistics_k__BackingField?.shots,
                reloadRunning = p._reloading_k__BackingField?.isRunning, reloadTimer = p._reloading_k__BackingField?.Pointer.ToInt64(), position = V(p.transform.position), rotation = V(p.transform.eulerAngles), barrels });
        }
        var ammo = new List<object>();
        if (ship._ammo_k__BackingField != null) foreach (var a in ship._ammo_k__BackingField) ammo.Add(new { data = a.Key.name, a.Value.ap, a.Value.he });
        var targets = new List<object>();
        var friendlyVisibility = new List<object>();
        if (Ship.AllShips != null) foreach (Ship s in Ship.AllShips)
            if (OwnedAlive(s)) friendlyVisibility.Add(new { source = s.id.ToString(), visible = ship.IsVisibleFor(s) });
        if (ship.enemies != null) foreach (var t in ship.enemies) if (t.Key?.isGun == true) targets.Add(new { gun = t.Key.name, side = "right", target = t.Value?.target?.id.ToString() });
        if (ship.enemiesLeftSide != null) foreach (var t in ship.enemiesLeftSide) if (t.Key?.isGun == true) targets.Add(new { gun = t.Key.name, side = "left", target = t.Value?.target?.id.ToString() });
        return new { id = ship.id.ToString(), pointer = ship.Pointer.ToInt64(), name = ship.vesselName, owned = ship.player?.isMain, country = ship.player?.data?.name,
            state = ship.state.ToString(), position = V(ship.transform.position), forward = V(ship.transform.forward),
            nativeDamage = FleetDamageEvidence.ReadShipDamage(ship),
            visibility = new { ship.isShownForOthers, ship.RevealedUntil, ship.positionWasRevealed, ship.IsPositionRevealed, unityTime = Time.time,
                visibleToPlayer = ship.IsVisibleFor(PlayerController.Instance),
                fromFriendlyShips = friendlyVisibility },
            nativeAimMaps = new { right = ship.enemies?.Pointer.ToInt64(), left = ship.enemiesLeftSide?.Pointer.ToInt64() },
            main = ship.gunMode.ToString(), secondary = ship.gunSecMode.ToString(),
            shots = CountShots(ship), torpedoShots = CountTorpedoShots(ship), torpedoAmmo = CountTorpedoAmmo(ship),
            torpedoMode = ship.torpedoMode.ToString(), ammo, targets, guns, torpedoLaunchers };
    }
    private static object[] Ids(IEnumerable<Ship> ships) => ships.Select(s => (object)new { id = s.id.ToString(), pointer = s.Pointer.ToInt64(), name = s.vesselName, division = s.division?.leader?.id.ToString() }).ToArray();
    private void Emit(string kind, object detail)
    {
        // Telemetry must never throw through a native hook or replace its exception.
        try
        {
            string line = JsonSerializer.Serialize(new { kind, observedUtc = DateTime.UtcNow.ToString("O"), detail });
            try { LoggerInstance.Msg("FLEET_OPS_" + line); } catch { }
            if (!string.IsNullOrEmpty(prefix)) File.AppendAllText(Path.Combine(prefix, "fleet_ops_events.jsonl"), line + Environment.NewLine);
        }
        catch { }
    }
    public override void OnGUI()
    {
        if (settings == null || !visible) return;
        using (FleetLocalization.UseFont(settings.Language))
        {
            if (settings.IsOpen) { settings.Draw(); return; }
            if (GameManager.IsBattle)
            {
                if (BattleReady && showDispersion)
                {
                    FleetDispersionOverlay.Draw(dispersionContours);
                    FleetSalvoOverlay.Draw(L);
                }
                if (BattleReady) foreach (var preview in torpedoPreviews) FleetTorpedoOverlay.Draw(preview, showDispersion);
                if (BattleReady && showRecommendation)
                    foreach (var solution in recommendations) FleetFireSolution.Draw(solution);
                GUI.Box(panel, L("舰队手动瞄准 0.0.8.1 · 独立实验", "FLEET MANUAL AIM 0.0.8.1 · ISOLATED"));
                GUI.Label(new Rect(30, 182, 710, 24), L($"火炮  {Key(FleetAction.Aim)} 设点 / {Key(FleetAction.Follow)} 跟随 / {Key(FleetAction.Fire)} 齐射",
                    $"GUNS  {Key(FleetAction.Aim)} aim / {Key(FleetAction.Follow)} follow / {Key(FleetAction.Fire)} volley"));
                GUI.Label(new Rect(30, 207, 710, 24), L($"鱼雷  {Key(FleetAction.TorpedoFollow)} 独立跟随 / {Key(FleetAction.TorpedoFire)} 发射   {Key(FleetAction.Restore)} 恢复全部",
                    $"TORPEDOES  {Key(FleetAction.TorpedoFollow)} follow / {Key(FleetAction.TorpedoFire)} launch   {Key(FleetAction.Restore)} restore all"));
                GUI.Label(new Rect(30, 234, 710, 42), L(noticeZh, noticeEn));
                GUI.Label(new Rect(30, 278, 710, 24), L($"选中 {Selected().Length} 艘 | 火炮跟随 {orders.FollowingIds.Count} | 鱼雷跟随 {torpedoAim.FollowingIds.Count} | {FollowLabel()}",
                    $"Selected {Selected().Length} | Gun follow {orders.FollowingIds.Count} | Torpedo follow {torpedoAim.FollowingIds.Count} | {FollowLabel()}"));
                GUI.Label(new Rect(30, 303, 710, 24), L($"橙色：{FleetSalvoOverlay.ActiveVolleys} 轮已发齐射 / {FleetSalvoOverlay.ActiveShells} 发在飞；落弹结束即清除",
                    $"ORANGE: {FleetSalvoOverlay.ActiveVolleys} fired volleys / {FleetSalvoOverlay.ActiveShells} shells airborne; clears on flight end"));
                if (GUI.Button(new Rect(30, 331, 125, 25), showDispersion ? L("散布显示：开", "SPREAD ON") : L("散布显示：关", "SPREAD OFF")))
                { showDispersion = !showDispersion; nextDispersionUpdate = nextTacticalUpdate = DateTime.MinValue; }
                if (GUI.Button(new Rect(162, 331, 125, 25), dispersionAllGuns ? L("全部口径", "ALL CALIBERS") : L("仅主炮", "MAIN BATTERY")))
                { dispersionAllGuns = !dispersionAllGuns; nextDispersionUpdate = DateTime.MinValue; }
                if (GUI.Button(new Rect(294, 331, 230, 25), L($"射击建议 {Key(FleetAction.Recommendation)}：{(showRecommendation ? "开" : "关")}",
                    $"LEAD {Key(FleetAction.Recommendation)}: {(showRecommendation ? "ON" : "OFF")}"))) ToggleRecommendations();
                if (GUI.Button(new Rect(531, 331, 205, 25), L($"设置 / 改键 {Key(FleetAction.Settings)}", $"SETTINGS {Key(FleetAction.Settings)}"))) settings.ToggleOpen();
                GUI.Label(new Rect(30, 361, 710, 22), dispersionState);
                GUI.Label(new Rect(30, 384, 710, 42), L("实时圈：当前瞄点；橙圈：已发射冻结区域。读取原版最终设计精度，20m靶校准；异常时明示距离后备。",
                    "Live: current aim. Orange: frozen fired area. Native design precision, 20m calibration; explicit distance fallback if unavailable."));
                GUI.Label(new Rect(30, 428, 710, 42), showRecommendation
                    ? !RecommendationTargetVisible() ? L("射击建议：先点选可见敌舰，再选己方舰船；不读取迷雾目标。", "LEAD: select a visible enemy, then your ships. Fogged targets are excluded.")
                        : L("蓝/紫标记：火炮 / 鱼雷建议；按恒速运动估计，不会自动瞄准或发射。", "BLUE/PURPLE: gun / torpedo lead; constant-course estimate, no automatic aim or fire.")
                    : L("鱼雷线始终指向当前鱼雷瞄点；发射后不留历史扇面。", "Torpedo lines use the current aim point; no historical launch fan is retained."));
                if (keyConflicts.Length > 0) GUI.Label(new Rect(30, 471, 710, 22), L("部分快捷键不可用，请在设置中处理冲突。", "Some shortcuts are unavailable. Resolve conflicts in Settings."));
                if (torpedoPreviews.Count > 0) GUI.Label(new Rect(30, 496, 710, 24), torpedoPreviews[0].Summary);
                if (showRecommendation && recommendations.Count > 0) GUI.Label(new Rect(30, 522, 710, 25), recommendations[0].Summary);
            }
            else if (GUI.Button(new Rect(20, 120, 250, 30), L("舰队瞄准设置 / 中英文", "Fleet aim settings / Language"))) settings.ToggleOpen();
            settings.Draw();
        }
    }
    private static NVector3 N(Vector3 v) => new(v.x, v.y, v.z);
    private static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };
    private static string RequireExperiment()
    {
        string exe = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName ?? "") ?? "";
        var paths = FleetOps.Export.FleetOpsPackagePaths.Resolve(exe);
        string save = paths.Userdata + Path.DirectorySeparatorChar;
        if (!SamePath(Storage.prefix, save) || !SamePath(Storage.designsPrefix, paths.Designs)) throw new InvalidOperationException("Independent native save paths not active.");
        return save;
    }
    private static bool SamePath(string a, string b) => FleetOps.Export.FleetOpsPackagePaths.SamePath(a, b);
}
