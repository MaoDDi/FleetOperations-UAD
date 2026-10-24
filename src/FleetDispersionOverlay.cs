using System;
using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace FleetOps.Experimental;

/// <summary>Read-only tactical screen overlay of caller-supplied world dispersion.
/// Radii must come from the same evaluator as live shot sampling. No probability
/// level is implied. This IMGUI outline is not depth-occluded by ship geometry.</summary>
internal static class FleetDispersionOverlay
{
    internal sealed class Contour
    {
        public string Label { get; init; } = "";
        public Vector3 WorldCenter { get; init; }
        public Vector3 Along { get; init; }
        public Vector3 Across { get; init; }
        public float AlongRadius { get; init; }
        public float AcrossRadius { get; init; }
        public Color Color { get; init; } = Color.white;
    }

    private const int Segments = 64;

    internal static void Draw(IReadOnlyList<Contour> contours, int maximum = 16)
    {
        // Never sample native randomness, resolve ships/targets, or acquire GUI input.
        try
        {
            if (Event.current == null || Event.current.type != EventType.Repaint
                || contours == null || contours.Count == 0) return;
            // Use the same battle camera as cursor projection; tagged Camera.main
            // can refer to a different camera during UI/transitions.
            Camera camera = G.cam?.cameraComp!;
            if (camera == null) return;
            float near = camera.nearClipPlane, height = Screen.height;
            Rect viewport = camera.pixelRect;
            var clip = new NVector4(viewport.x, viewport.y, viewport.width, viewport.height);
            Matrix4x4 previousMatrix = GUI.matrix;
            Color previousColor = GUI.color;
            int previousDepth = GUI.depth;
            try
            {
                GUI.matrix = Matrix4x4.identity;
                GUI.depth = -9000;
                for (int i = 0; i < Math.Min(Math.Clamp(maximum, 0, 128), contours.Count); i++)
                {
                    Contour contour = contours[i];
                    if (contour == null || !Finite(contour.Color)) continue;
                    DrawContour(contour, camera, near, height, clip);
                }
            }
            finally
            {
                // Attempt every restore even if a native GUI setter fails.
                Restore(() => GUI.matrix = previousMatrix);
                Restore(() => GUI.color = previousColor);
                Restore(() => GUI.depth = previousDepth);
            }
        }
        catch { /* A failed preview must never interrupt native GUI/gameplay. */ }
    }

    internal static void DrawWorldLine(Vector3 start, Vector3 end, Color color, float thickness = 1.5f)
    {
        if (Event.current?.type != EventType.Repaint || !Finite(color)) return;
        Matrix4x4 previousMatrix = GUI.matrix; Color previousColor = GUI.color; int previousDepth = GUI.depth;
        try
        {
            Camera camera = G.cam?.cameraComp!;
            if (camera == null) return;
            Rect viewport = camera.pixelRect;
            if (!FleetDispersionOverlayGeometry.TryScreenSegment(N(camera.WorldToScreenPoint(start)),
                N(camera.WorldToScreenPoint(end)), camera.nearClipPlane, Screen.height,
                new NVector4(viewport.x, viewport.y, viewport.width, viewport.height), out var a, out var b)) return;
            GUI.matrix = Matrix4x4.identity; GUI.depth = -9000; GUI.color = color;
            DrawLine(a, b, thickness);
        }
        catch { }
        finally { Restore(() => GUI.matrix = previousMatrix); Restore(() => GUI.color = previousColor); Restore(() => GUI.depth = previousDepth); }
    }

    internal static void DrawWorldMarker(Vector3 point, string label, Color color, float size = 6f)
    {
        if (Event.current?.type != EventType.Repaint || !Finite(color)) return;
        Matrix4x4 previousMatrix = GUI.matrix; Color previousColor = GUI.color; int previousDepth = GUI.depth;
        try
        {
            Camera camera = G.cam?.cameraComp!;
            if (camera == null) return;
            Vector3 screen = camera.WorldToScreenPoint(point);
            if (!float.IsFinite(screen.x) || !float.IsFinite(screen.y) || !float.IsFinite(screen.z)
                || screen.z <= camera.nearClipPlane || !camera.pixelRect.Contains(new Vector2(screen.x, screen.y))) return;
            float x = screen.x, y = Screen.height - screen.y;
            GUI.matrix = Matrix4x4.identity; GUI.depth = -9000; GUI.color = color;
            float r = Math.Clamp(size, 2, 20);
            GUI.DrawTexture(new Rect(x-r, y-.75f, r*2, 1.5f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(x-.75f, y-r, 1.5f, r*2), Texture2D.whiteTexture);
            if (!string.IsNullOrEmpty(label)) GUI.Label(new Rect(x+r+3, y-10, 360, 42), label);
        }
        catch { }
        finally { Restore(() => GUI.matrix = previousMatrix); Restore(() => GUI.color = previousColor); Restore(() => GUI.depth = previousDepth); }
    }

    private static void DrawContour(Contour contour, Camera camera, float near, float height, NVector4 clip)
    {
        NVector3 center = N(contour.WorldCenter), along = N(contour.Along), across = N(contour.Across);
        if (!FleetDispersionOverlayGeometry.TryEllipsePoint(center, along, across,
            contour.AlongRadius, contour.AcrossRadius, 0, out NVector3 first)) return;
        GUI.color = contour.Color;
        Vector3 previous = camera.WorldToScreenPoint(U(first));
        for (int i = 1; i <= Segments; i++)
        {
            if (!FleetDispersionOverlayGeometry.TryEllipsePoint(center, along, across,
                contour.AlongRadius, contour.AcrossRadius, i * (Math.PI * 2 / Segments), out NVector3 world)) return;
            Vector3 current = camera.WorldToScreenPoint(U(world));
            if (FleetDispersionOverlayGeometry.TryScreenSegment(N(previous), N(current), near, height, clip,
                out NVector2 a, out NVector2 b)) DrawLine(a, b);
            previous = current;
        }
        // Optional source label, with no minimum ellipse radius/area inflation.
        Vector3 screen = camera.WorldToScreenPoint(contour.WorldCenter);
        if (!string.IsNullOrEmpty(contour.Label) && float.IsFinite(screen.x) && float.IsFinite(screen.y)
            && float.IsFinite(screen.z) && screen.z > near && screen.x >= clip.X
            && screen.x <= clip.X + clip.Z && screen.y >= clip.Y && screen.y <= clip.Y + clip.W)
        {
            string label = contour.Label.Length <= 64 ? contour.Label : contour.Label.Substring(0, 64);
            GUI.Label(new Rect(screen.x + 5, height - screen.y + 5, 220, 20), label);
        }
    }

    private static void DrawLine(NVector2 a, NVector2 b, float thickness = 1.5f)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float length = (float)Math.Sqrt((double)dx * dx + (double)dy * dy);
        if (!float.IsFinite(length) || length <= 0) return;
        Matrix4x4 previousMatrix = GUI.matrix;
        try
        {
            GUIUtility.RotateAroundPivot((float)(Math.Atan2(dy, dx) * 180 / Math.PI), new Vector2(a.X, a.Y));
            thickness = Math.Clamp(thickness, .5f, 6f);
            GUI.DrawTexture(new Rect(a.X, a.Y - thickness / 2f, length, thickness), Texture2D.whiteTexture);
        }
        finally { Restore(() => GUI.matrix = previousMatrix); }
    }

    private static void Restore(Action restore) { try { restore(); } catch { } }
    private static bool Finite(Color c) => float.IsFinite(c.r) && float.IsFinite(c.g) && float.IsFinite(c.b) && float.IsFinite(c.a);
    private static NVector3 N(Vector3 p) => new(p.x, p.y, p.z);
    private static Vector3 U(NVector3 p) => new(p.X, p.Y, p.Z);
}
