using System;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace FleetOps.Experimental;

public enum FleetLanguage { ZhCN, En }

public static class FleetLocalization
{
    private static Font? chineseFont;
    private static bool fontAttempted;
    public static string Choose(FleetLanguage language, string zh, string en) => language == FleetLanguage.En ? en : zh;
    public static string ActionLabel(FleetLanguage language, FleetAction action) => action switch
    {
        FleetAction.Fire => Choose(language, "火炮齐射", "Gun volley"),
        FleetAction.Aim => Choose(language, "火炮静态设点", "Static gun aim"),
        FleetAction.Follow => Choose(language, "火炮跟随鼠标", "Gun mouse follow"),
        FleetAction.TorpedoFire => Choose(language, "鱼雷发射", "Launch torpedoes"),
        FleetAction.TorpedoFollow => Choose(language, "鱼雷跟随鼠标", "Torpedo mouse follow"),
        FleetAction.Restore => Choose(language, "恢复选中舰原版控制", "Restore selected ships"),
        FleetAction.Recommendation => Choose(language, "射击建议开关", "Toggle firing advice"),
        FleetAction.Settings => Choose(language, "设置窗口开关", "Toggle settings"),
        _ => action.ToString()
    };

    public static Font? AcquireFont(FleetLanguage language)
    {
        // The English settings UI still contains a Chinese-language selector.
        // CJK fonts also cover Latin, so retain the same available font in either language.
        if (fontAttempted) return chineseFont;
        fontAttempted = true;
        foreach (var name in new[] { "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "Noto Sans CJK SC", "Arial Unicode MS", "Arial" })
        {
            try
            {
                // The generated string overload is an unstripping throw stub. The array
                // overload references a missing constructor. Allocate the IL2CPP object
                // and use the generated, directly bound native creation icall instead.
                RuntimeHelpers.RunClassConstructor(typeof(Font).TypeHandle);
                var nativeClass = Il2CppClassPointerStore<Font>.NativeClassPtr;
                if (nativeClass == IntPtr.Zero) continue;
                var nativeObject = IL2CPP.il2cpp_object_new(nativeClass);
                if (nativeObject == IntPtr.Zero) continue;
                var candidate = new Font(nativeObject);
                Font.Internal_CreateDynamicFont(candidate, new Il2CppStringArray(new[] { name }), 16);
                if (candidate != null && candidate.HasCharacter('舰') && candidate.HasCharacter('设'))
                { chineseFont = candidate; break; }
            }
            catch { /* A missing OS font must not break the game's GUI. */ }
        }
        return chineseFont;
    }

    public static IDisposable UseFont(FleetLanguage language) => new FontScope(AcquireFont(language));
    private sealed class FontScope : IDisposable
    {
        private GUISkin? skin;
        private Font? previous;
        public FontScope(Font? font)
        {
            try
            {
                if (font == null) return;
                skin = GUI.skin; previous = skin.font; skin.font = font;
            }
            catch { skin = null; }
        }
        public void Dispose()
        {
            if (skin == null) return;
            try { skin.font = previous; } catch { }
            skin = null;
        }
    }
}
