using System;
using System.Collections.Generic;
using System.Numerics;
using FleetOps.Experimental;
using G = FleetOps.Experimental.FleetNativeSideGeometry;

static class NativeSideTests
{
    private static int passed, failed;
    private static readonly G.Spec Basic = new(17, new(0,2,0), new(4,1,8), Matrix4x4.Identity, Matrix4x4.Identity, -100,100);
    public static int Main()
    {
        Test("actual side under native top has measured first contact", () => {
            var e = Build(Basic); True(Hit(new(-3,1,0),new(3,1,0),e,out var h));
            Near(1f/6,h.Fraction); Near(new(-2,1,0),h.Point); Near(-Vector3.UnitX,h.Normal); Equal(17,h.Id);
        });
        Test("outside side footprint and above top miss", () => {
            var e=Build(Basic); False(Hit(new(-3,4,0),new(3,4,0),e,out _));
            False(Hit(new(-3,1,5),new(3,1,5),e,out _));
        });
        Test("native alpha limits vertical support", () => {
            var e=Build(Basic with {Size=new(4,.1f,8)});
            False(Hit(new(-3,1,0),new(3,1,0),e,out _)); True(Hit(new(-3,.2f,0),new(3,.2f,0),e,out _));
        });
        Test("zero height or wholly submerged top fails closed", () => {
            False(G.TryBuild(Basic with {Size=new(4,0,8)},out _,out _));
            False(G.TryBuild(Basic with {Center=new(0,-3,0)},out _,out _));
        });
        Test("nonzero box center and translated ship are counted once", () => {
            var ship=Matrix4x4.CreateTranslation(10,0,30); Matrix4x4.Invert(ship,out var inverse);
            var e=Build(Basic with {Center=new(1,2,2),BoxLocalToWorld=ship,ShipWorldToLocal=inverse});
            True(Hit(new(8,1,32),new(14,1,32),e,out var h)); Near(1f/6,h.Fraction); Near(9,h.Point.X);
        });
        Test("pitched nonuniformly scaled box retains real footprint", () => {
            var box=Matrix4x4.CreateScale(2,1,.5f)*Matrix4x4.CreateRotationX(.25f)*Matrix4x4.CreateTranslation(10,0,20);
            var e=Build(Basic with {BoxLocalToWorld=box});
            var top=Vector3.Transform(new(0,3.25f,0),box);
            True(Hit(new(4,top.Y*.5f,top.Z),new(16,top.Y*.5f,top.Z),e,out var h)); Near(6,h.Point.X);
        });
        Test("sloping top is not replaced with global max height", () => {
            var e=Build(Basic with {BoxLocalToWorld=Matrix4x4.CreateRotationZ(MathF.PI/6)});
            False(Hit(new(0,3.8f,-1),new(0,3.8f,1),e,out _));
            True(Hit(new(0,3.6f,-6),new(0,3.6f,6),e,out _));
        });
        Test("native final Z scale keeps original segment fraction", () => {
            var e=Build(Basic); True(Hit(new(-3,1,3.21f),new(3,1,3.21f),e,out var h));
            Near(1f/6,h.Fraction); Near(3.21f,h.Point.Z);
        });
        Test("clamp interval is a guard and does not manufacture end caps", () => {
            var e=Build(Basic with {ClampLo=-1,ClampHi=1});
            False(Hit(new(-3,1,2),new(3,1,2),e,out _));
            False(Hit(new(0,1,-3),new(0,1,3),e,out _));
            True(Hit(new(-3,1,0),new(3,1,0),e,out _));
        });
        Test("later sweep subsegment inside guard cannot manufacture a previously rejected contact", () => {
            var e=Build(Basic with {ClampLo=-1,ClampHi=1});
            False(Hit(new(0,1,-3),new(0,1,-.5f),e,out _));
            False(Hit(new(0,1,-.5f),new(0,1,0),e,out _));
        });
        Test("pitched rolled final map transforms contact and normal", () => {
            var ship=Matrix4x4.CreateFromYawPitchRoll(.2f,.5f,.15f)*Matrix4x4.CreateTranslation(2,1,1);
            Matrix4x4.Invert(ship,out var inverse); var forward=inverse*Matrix4x4.CreateScale(1,1,1.07f)*ship;
            Matrix4x4.Invert(forward,out var undo);
            var e=Build(Basic with {ShipWorldToLocal=inverse});
            var a=Vector3.Transform(new(0,-1,0),forward);var b=Vector3.Transform(new(0,1,0),forward);
            True(Hit(a,b,e,out var h)); Near(.5f,h.Fraction); Near(Vector3.Transform(Vector3.Zero,forward),h.Point);
            Near(Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitY,Matrix4x4.Transpose(undo))),h.Normal);
        });
        Test("actual box worldXZ hull narrows expanded final map", () => {
            var e=Build(Basic);
            False(Hit(new(-3,1,4.1f),new(3,1,4.1f),e,out _));
        });
        Test("parallel boundary and exact segment endpoint are supported without padding", () => {
            var e=Build(Basic); True(Hit(new(-3,1,0),new(-2,1,0),e,out var h)); Near(1,h.Fraction);
            False(Hit(new(-3,1,0),new(-2.01f,1,0),e,out _));
        });
        Test("singular or nonaffine matrices fail closed", () => {
            False(G.TryBuild(Basic with {BoxLocalToWorld=Matrix4x4.CreateScale(0,1,1)},out _,out _));
            False(G.TryBuild(Basic with {ShipWorldToLocal=default},out _,out _));
            var malformed=Matrix4x4.Identity;malformed.M14=.5f;
            False(G.TryBuild(Basic with {BoxLocalToWorld=malformed},out _,out _));
        });
        Test("near vertical XZ parameterization fails closed", () => {
            False(G.TryBuild(Basic with {BoxLocalToWorld=Matrix4x4.CreateRotationX(MathF.PI/2)},out _,out _));
        });
        Test("nonfinite vectors matrices guards and segments fail closed", () => {
            False(G.TryBuild(Basic with {Center=new(float.NaN,0,0)},out _,out _));
            False(G.TryBuild(Basic with {Size=new(4,float.PositiveInfinity,8)},out _,out _));
            False(G.TryBuild(Basic with {ClampLo=float.NaN},out _,out _));
            False(G.TryBuild(Basic with {ClampHi=-100},out _,out _));
            var m=Matrix4x4.Identity;m.M33=float.NaN;False(G.TryBuild(Basic with {ShipWorldToLocal=m},out _,out _));
            False(Hit(new(float.NaN,0,0),Vector3.One,Build(Basic),out _));
        });
        Console.WriteLine($"TOTAL {passed+failed}; PASSED {passed}; FAILED {failed}"); return failed==0?0:1;
    }
    private static G.Envelope Build(G.Spec s) { True(G.TryBuild(s,out var e,out var b)); True(Finite(b.Min)&&Finite(b.Max)); return e!; }
    private static bool Hit(Vector3 a,Vector3 b,G.Envelope e,out FleetGunImpactGeometry.Hit h)=>G.TryIntersectSegment(a,b,e,out h);
    private static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
    private static void Test(string name,Action action) { try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception ex){failed++;Console.WriteLine("FAIL "+name+": "+ex.Message);} }
    private static void True(bool b){if(!b)throw new Exception("expected true");}
    private static void False(bool b){if(b)throw new Exception("expected false");}
    private static void Near(float e,float a){if(!float.IsFinite(a)||MathF.Abs(e-a)>1e-4f)throw new Exception($"expected {e}, actual {a}");}
    private static void Near(Vector3 e,Vector3 a){Near(e.X,a.X);Near(e.Y,a.Y);Near(e.Z,a.Z);}
    private static void Equal(int e,int a){if(e!=a)throw new Exception($"expected {e}, actual {a}");}
}
