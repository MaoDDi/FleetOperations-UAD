using System;
using System.Collections.Generic;
using System.Numerics;

namespace FleetOps.Experimental;

internal static class FleetNativeSideGeometry
{
    internal readonly record struct Spec(int Id, Vector3 Center, Vector3 Size,
        Matrix4x4 BoxLocalToWorld, Matrix4x4 ShipWorldToLocal, float ClampLo, float ClampHi);
    internal readonly record struct Bounds(Vector3 Min, Vector3 Max);
    internal readonly record struct PlaneD(double X, double Y, double Z, double D)
    {
        internal double Dot(Vector3 p) => X*p.X + Y*p.Y + Z*p.Z;
    }
    internal sealed class Envelope
    {
        internal readonly int Id;
        internal readonly PlaneD[] Planes;
        internal readonly Matrix4x4 WorldToRecoveredShip;
        internal readonly float ClampLo, ClampHi;
        internal float AlphaMax { get; }
        internal IReadOnlyList<Vector3> WorldCorners { get; }
        internal Envelope(Spec spec, PlaneD[] planes, Matrix4x4 worldToRecoveredShip,
            float alpha, Vector3[] corners)
        { Id=spec.Id; Planes=planes; WorldToRecoveredShip=worldToRecoveredShip;
            ClampLo=spec.ClampLo; ClampHi=spec.ClampHi; AlphaMax=alpha; WorldCorners=corners; }
    }

    /// <summary>A conservative native visual-side subset, not a steel mesh or a widened whole-ship box.
    /// ClampLo/Hi are already the native centerZ+minZ / centerZ+maxZ; never add another center here.</summary>
    internal static bool TryBuild(Spec spec, out Envelope? envelope, out Bounds bounds)
    {
        envelope=null; bounds=default;
        if (!Finite(spec.Center) || !Finite(spec.Size) || spec.Size.X<=0 || spec.Size.Y<=0 || spec.Size.Z<=0
            || !float.IsFinite(spec.ClampLo) || !float.IsFinite(spec.ClampHi) || spec.ClampLo>=spec.ClampHi
            || !Affine(spec.BoxLocalToWorld) || !Affine(spec.ShipWorldToLocal)
            || !Matrix4x4.Invert(spec.BoxLocalToWorld,out var inverseBox) || !Finite(inverseBox)
            || !Matrix4x4.Invert(spec.ShipWorldToLocal,out var shipToWorld) || !Finite(shipToWorld)) return false;
        float alpha=(float)Math.Min(1d,1.5d*spec.Size.Y);
        if (!float.IsFinite(alpha) || alpha<=0) return false;
        // System.Numerics uses row vectors: world -> ship -> scale native Z -> world.
        Matrix4x4 forward=spec.ShipWorldToLocal*Matrix4x4.CreateScale(1,1,1.07f)*shipToWorld;
        if (!Affine(forward) || !Matrix4x4.Invert(forward,out var undo) || !Finite(undo)) return false;
        Vector3 origin=Vector3.Transform(spec.Center+new Vector3(0,1.25f,0),spec.BoxLocalToWorld);
        Vector3 uAxis=Vector3.TransformNormal(Vector3.UnitX,spec.BoxLocalToWorld);
        Vector3 vAxis=Vector3.TransformNormal(Vector3.UnitZ,spec.BoxLocalToWorld);
        if (!Finite(origin) || !Finite(uAxis) || !Finite(vAxis)) return false;
        double det=(double)uAxis.X*vAxis.Z-(double)uAxis.Z*vAxis.X;
        double scale=Math.Sqrt(((double)uAxis.X*uAxis.X+(double)uAxis.Y*uAxis.Y+(double)uAxis.Z*uAxis.Z)
            *((double)vAxis.X*vAxis.X+(double)vAxis.Y*vAxis.Y+(double)vAxis.Z*vAxis.Z));
        if (!double.IsFinite(det) || !double.IsFinite(scale) || scale<=0 || Math.Abs(det)<=scale*1e-6) return false;
        double ux=vAxis.Z/det, uz=-vAxis.X/det, u0=-ux*origin.X-uz*origin.Z;
        double vx=-uAxis.Z/det, vz=uAxis.X/det, v0=-vx*origin.X-vz*origin.Z;
        double hx=spec.Size.X*.5d, hz=spec.Size.Z*.5d;
        var prePlanes=new[] {
            new PlaneD(ux,0,uz,hx-u0), new PlaneD(-ux,0,-uz,hx+u0),
            new PlaneD(vx,0,vz,hz-v0), new PlaneD(-vx,0,-vz,hz+v0),
            new PlaneD(0,-1,0,0),
            new PlaneD(-alpha*(uAxis.Y*ux+vAxis.Y*vx),1,-alpha*(uAxis.Y*uz+vAxis.Y*vz),
                alpha*(origin.Y+uAxis.Y*u0+vAxis.Y*v0))
        };
        var planes=new List<PlaneD>();
        foreach (PlaneD p in prePlanes)
        {
            // p(undo(world)) <= D. This also transports normals by F inverse-transpose.
            var mapped=new PlaneD(undo.M11*p.X+undo.M12*p.Y+undo.M13*p.Z,
                undo.M21*p.X+undo.M22*p.Y+undo.M23*p.Z,
                undo.M31*p.X+undo.M32*p.Y+undo.M33*p.Z,
                p.D-undo.M41*p.X-undo.M42*p.Y-undo.M43*p.Z);
            if (!TryNormalize(mapped,out var normalized)) return false;
            planes.Add(normalized);
        }
        var corners=new Vector3[8]; int index=0; float maximumTop=float.NegativeInfinity;
        Vector3 min=new(float.PositiveInfinity),max=new(float.NegativeInfinity);
        for (int x=-1;x<=1;x+=2) for (int z=-1;z<=1;z+=2)
        {
            Vector3 top=Vector3.Transform(spec.Center+new Vector3((float)(hx*x),1.25f,(float)(hz*z)),spec.BoxLocalToWorld);
            maximumTop=MathF.Max(maximumTop,top.Y);
            for (int upper=0;upper<=1;upper++)
            {
                Vector3 pre=new(top.X,upper==0?0:alpha*top.Y,top.Z);
                Vector3 world=Vector3.Transform(pre,forward);
                if (!Finite(world)) return false;
                corners[index++]=world; min=Vector3.Min(min,world);max=Vector3.Max(max,world);
            }
        }
        if (!(maximumTop>0)) return false;
        // Additional conservative guard: the actual 8-corner OBB's world-XZ convex footprint.
        // It is distinct from the four-corner native parameter surface and is never a world AABB hit.
        var physicalCorners=new List<Vector2>(); Vector3 half=spec.Size*.5f;
        for(int x=-1;x<=1;x+=2) for(int y=-1;y<=1;y+=2) for(int z=-1;z<=1;z+=2)
        {
            Vector3 p=Vector3.Transform(spec.Center+half*new Vector3(x,y,z),spec.BoxLocalToWorld);
            if(!Finite(p))return false;physicalCorners.Add(new Vector2(p.X,p.Z));
        }
        List<Vector2> hull=Hull(physicalCorners);
        if(hull.Count<3)return false;
        for(int i=0;i<hull.Count;i++)
        {
            Vector2 a=hull[i],b=hull[(i+1)%hull.Count];
            double nx=(double)b.Y-a.Y,nz=(double)a.X-b.X;
            if(!TryNormalize(new PlaneD(nx,0,nz,nx*a.X+nz*a.Y),out var plane))return false;
            planes.Add(plane);
        }
        Matrix4x4 recoveredShip=undo*spec.ShipWorldToLocal;
        if(!Finite(recoveredShip))return false;
        envelope=new Envelope(spec,planes.ToArray(),recoveredShip,alpha,corners);
        bounds=new Bounds(min,max); return true;
    }

    internal static bool TryIntersectSegment(Vector3 start, Vector3 end, Envelope envelope, out FleetGunImpactGeometry.Hit hit)
    {
        hit=default;
        if(envelope is null || !Finite(start) || !Finite(end))return false;
        double dx=(double)end.X-start.X,dy=(double)end.Y-start.Y,dz=(double)end.Z-start.Z;
        double length=Math.Sqrt(dx*dx+dy*dy+dz*dz);
        if(!double.IsFinite(length)||length<=1e-8)return false;
        double enter=0,exit=1; PlaneD? entry=null;
        foreach(PlaneD p in envelope.Planes)
        {
            double distance=p.D-p.Dot(start),denominator=p.X*dx+p.Y*dy+p.Z*dz;
            if(Math.Abs(denominator)<=1e-12*length)
            { if(distance<0)return false;continue; }
            double t=distance/denominator;
            if(denominator<0) {if(t>enter){enter=t;entry=p;}}
            else exit=Math.Min(exit,t);
            if(enter>exit || exit<0 || enter>1)return false;
        }
        // A later sweep may start inside after a prior segment's contact failed the Z guard.
        // Without a real entry plane, accepting fraction zero would indirectly invent a guard end cap.
        if(!entry.HasValue || enter<0 || enter>1 || !double.IsFinite(enter))return false;
        var point=new Vector3((float)(start.X+enter*dx),(float)(start.Y+enter*dy),(float)(start.Z+enter*dz));
        // Guard the already found contact. Never advance it to a clamp plane and invent an end cap.
        Matrix4x4 q=envelope.WorldToRecoveredShip;
        double localZ=(start.X+enter*dx)*q.M13+(start.Y+enter*dy)*q.M23+(start.Z+enter*dz)*q.M33+q.M43;
        if(!double.IsFinite(localZ) || localZ<=envelope.ClampLo || localZ>=envelope.ClampHi)return false;
        Vector3 normal=new((float)entry.Value.X,(float)entry.Value.Y,(float)entry.Value.Z);
        if(!Finite(point)||!Finite(normal)||normal.LengthSquared()<=1e-12f)return false;
        hit=new FleetGunImpactGeometry.Hit(envelope.Id,(float)enter,point,Vector3.Normalize(normal));return true;
    }

    private static bool TryNormalize(PlaneD p,out PlaneD normalized)
    {
        normalized=default;double length=Math.Sqrt(p.X*p.X+p.Y*p.Y+p.Z*p.Z);
        if(!double.IsFinite(length)||length<=0||!double.IsFinite(p.D))return false;
        normalized=new(p.X/length,p.Y/length,p.Z/length,p.D/length);return true;
    }
    private static List<Vector2> Hull(List<Vector2> points)
    {
        points.Sort((a,b)=>a.X!=b.X?a.X.CompareTo(b.X):a.Y.CompareTo(b.Y));
        var unique=new List<Vector2>();foreach(var p in points)if(unique.Count==0||p!=unique[^1])unique.Add(p);
        if(unique.Count<3)return unique;
        var hull=new List<Vector2>();
        foreach(var p in unique){while(hull.Count>=2&&Cross(hull[^2],hull[^1],p)<=0)hull.RemoveAt(hull.Count-1);hull.Add(p);}
        int lower=hull.Count;
        for(int i=unique.Count-2;i>=0;i--){var p=unique[i];while(hull.Count>lower&&Cross(hull[^2],hull[^1],p)<=0)hull.RemoveAt(hull.Count-1);hull.Add(p);}
        hull.RemoveAt(hull.Count-1);return hull;
    }
    private static double Cross(Vector2 a,Vector2 b,Vector2 c)=>((double)b.X-a.X)*((double)c.Y-a.Y)-((double)b.Y-a.Y)*((double)c.X-a.X);
    private static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
    private static bool Affine(Matrix4x4 m)=>Finite(m)&&MathF.Abs(m.M14)<=1e-6f&&MathF.Abs(m.M24)<=1e-6f&&MathF.Abs(m.M34)<=1e-6f&&MathF.Abs(m.M44-1)<=1e-6f;
    private static bool Finite(Matrix4x4 m)=>
        float.IsFinite(m.M11)&&float.IsFinite(m.M12)&&float.IsFinite(m.M13)&&float.IsFinite(m.M14)
        &&float.IsFinite(m.M21)&&float.IsFinite(m.M22)&&float.IsFinite(m.M23)&&float.IsFinite(m.M24)
        &&float.IsFinite(m.M31)&&float.IsFinite(m.M32)&&float.IsFinite(m.M33)&&float.IsFinite(m.M34)
        &&float.IsFinite(m.M41)&&float.IsFinite(m.M42)&&float.IsFinite(m.M43)&&float.IsFinite(m.M44);
}
