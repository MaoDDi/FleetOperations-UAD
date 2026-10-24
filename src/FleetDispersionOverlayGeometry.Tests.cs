using System;
using System.Numerics;
using G = FleetOps.Experimental.FleetDispersionOverlayGeometry;

internal static class Program
{
    private static int passed, failed;
    private static void Main()
    {
        Test("world radii stay in metres", () => {
            Require(Point(2, 3, 0) == new Vector3(12, 4, 20));
            Require(Point(20, 3, 0) == new Vector3(30, 4, 20));
            Near(Point(2, 3, Math.PI / 2), new Vector3(10, 4, 23));
        });
        Test("tiny ellipse is never enlarged", () => Near(Point(.01f, .02f, 0), new Vector3(10.01f, 4, 20)));
        Test("rotated basis retains axes", () => {
            Require(G.TryEllipsePoint(new Vector3(10, 4, 20), Vector3.UnitZ, -Vector3.UnitX, 5, 2,
                Math.PI / 2, out var p)); Near(p, new Vector3(8, 4, 20));
        });
        Test("nonfinite geometry rejected", () => {
            Require(!G.TryEllipsePoint(new Vector3(float.NaN,0,0), Vector3.UnitX, Vector3.UnitZ, 2,3,0,out _));
            Require(!G.TryEllipsePoint(Vector3.Zero,Vector3.UnitX,Vector3.UnitZ,float.PositiveInfinity,3,0,out _));
            Require(!G.TryEllipsePoint(Vector3.Zero,Vector3.UnitX,Vector3.UnitZ,2,3,double.NaN,out _));
        });
        Test("invalid bases rejected rather than silently rescaled", () => {
            Require(!G.TryEllipsePoint(Vector3.Zero,Vector3.UnitX*2,Vector3.UnitZ,2,3,0,out _));
            Require(!G.TryEllipsePoint(Vector3.Zero,Vector3.UnitX,Vector3.UnitX,2,3,0,out _));
            Require(!G.TryEllipsePoint(Vector3.Zero,Vector3.UnitY,Vector3.UnitZ,2,3,0,out _));
        });
        Test("negative and zero-only radii rejected", () => {
            Require(!G.TryEllipsePoint(Vector3.Zero,Vector3.UnitX,Vector3.UnitZ,-1,3,0,out _));
            Require(!G.TryEllipsePoint(Vector3.Zero,Vector3.UnitX,Vector3.UnitZ,0,0,0,out _));
        });
        Test("screen Y is flipped once", () => {
            Require(Screen(new(100, 50, 2),new(200,100,2),out var a,out var b));
            Require(a==new Vector2(100,550) && b==new Vector2(200,500));
        });
        Test("behind or near-crossing segment is rejected", () => {
            Require(!Screen(new(100,50,-2),new(200,100,2),out _,out _));
            Require(!Screen(new(100,50,.1f),new(200,100,2),out _,out _));
            Require(!Screen(new(100,50,2),new(200,100,.3f),out _,out _));
        });
        Test("viewport clipping produces true segment intersection", () => {
            Require(Screen(new(-100,300,2),new(200,300,2),out var a,out var b));
            Require(a==new Vector2(0,300) && b==new Vector2(200,300));
            Require(!Screen(new(-100,300,2),new(-1,400,2),out _,out _));
        });
        Test("camera subrect is respected", () => {
            Require(G.TryScreenSegment(new(50,150,2),new(300,150,2),.3f,600,new(100,100,400,300),out var a,out var b));
            Require(a==new Vector2(100,450) && b==new Vector2(300,450));
        });
        Test("nonfinite projection and invalid viewport rejected", () => {
            Require(!Screen(new(float.PositiveInfinity,100,2),new(200,100,2),out _,out _));
            Require(!G.TryScreenSegment(new(100,100,2),new(200,100,2),.3f,600,new(0,0,0,600),out _,out _));
        });
        Test("pathologically long projected lines rejected", () => Require(!Screen(new(-100000,300,2),new(100000,300,2),out _,out _)));
        Test("closed 64 segment ellipse keeps original centre", () => {
            var sum=Vector3.Zero;
            for(int i=0;i<64;i++) sum+=Point(7,2,i*Math.PI/32);
            Near(sum/64,new Vector3(10,4,20)); Near(Point(7,2,0),Point(7,2,Math.PI*2));
        });
        Console.WriteLine($"{passed} passed, {failed} failed");
        Environment.ExitCode = failed == 0 ? 0 : 1;
    }
    private static Vector3 Point(float a,float b,double t) {
        Require(G.TryEllipsePoint(new(10,4,20),Vector3.UnitX,Vector3.UnitZ,a,b,t,out var p)); return p;
    }
    private static bool Screen(Vector3 a,Vector3 b,out Vector2 p,out Vector2 q) => G.TryScreenSegment(a,b,.3f,600,new(0,0,800,600),out p,out q);
    private static void Near(Vector3 a, Vector3 b) => Require(Vector3.Distance(a,b)<.0001f);
    private static void Require(bool ok) { if(!ok) throw new Exception("assertion failed"); }
    private static void Test(string name,Action test) {
        try { test(); passed++; Console.WriteLine("PASS "+name); }
        catch(Exception ex) { failed++; Console.WriteLine("FAIL "+name+": "+ex.Message); }
    }
}
