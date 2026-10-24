using System;
using System.Numerics;
using G = FleetOps.Experimental.FleetGunDispersionGeometry;

int passed = 0, failed = 0;
void Check(string name, Func<bool> test)
{
    try { if (!test()) throw new Exception("assertion failed"); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e.Message); failed++; }
}
bool Near(float a, float b) => MathF.Abs(a-b) < .001f;
var stock = new G.Settings(30,150,10000);
Check("zero range retains native close radius", () => G.TryProfile(Vector3.Zero,Vector3.Zero,stock,out var p) && p.Range==0 && p.Radius==30);
Check("reference range reaches native far radius", () => G.TryProfile(Vector3.Zero,new(10000,0,0),stock,out var p) && p.Range==10000 && p.Radius==150);
Check("middle distance interpolates the same curve", () => G.TryProfile(Vector3.Zero,new(5000,0,0),stock,out var p) && p.Radius==90);
Check("beyond far range extrapolates without capping", () => G.TryProfile(Vector3.Zero,new(20000,0,0),stock,out var p) && p.Radius==270);
Check("runtime settings are used rather than stock constants", () => G.TryProfile(Vector3.Zero,new(49000,0,0),new(4,555,49000),out var p) && p.Radius==555);
Check("decreasing far radius fails closed even before extrapolation", () => {
    foreach(float range in new[]{0f,100f,1000f,10000f})
        if(G.TryProfile(Vector3.Zero,new(range,0,0),new(30,10,10000),out _))return false;
    return true;
});
Check("range uses 3D aim while impact center is on sea", () => G.TryProfile(Vector3.Zero,new(3,4,0),stock,out var p) && p.Range==5 && Near(p.Radius,30.06f) && p.AimCenter.Y==4 && p.ImpactCenter==new Vector3(3,0,0));
Check("negative nonfinite and zero-reference settings fail closed", () => {
    foreach(var s in new[]{new G.Settings(-1,150,10000),new G.Settings(30,-1,10000),new G.Settings(30,150,0),new G.Settings(30,150,-1),new G.Settings(float.NaN,150,10000),new G.Settings(30,float.PositiveInfinity,10000),new G.Settings(30,150,float.NaN)})
        if(G.TryProfile(Vector3.Zero,new(1000,0,0),s,out _))return false;
    return true;
});
Check("nonfinite or overflowing geometry fails closed", () => !G.TryProfile(new(float.NaN,0,0),Vector3.Zero,stock,out _) && !G.TryProfile(Vector3.Zero,new(float.PositiveInfinity,0,0),stock,out _) && !G.TryProfile(new(float.MaxValue,0,0),new(-float.MaxValue,0,0),stock,out _));
Check("zero native draw freezes center with exact sea Y", () => G.TryProfile(new(1,9,3),new(400,25,600),stock,out var p) && G.TryFreeze(p,Vector3.Zero,out var f) && f.ImpactPoint==new Vector3(400,0,600) && f.Profile.AimCenter.Y==25);
Check("disk boundary maps to the displayed radius", () => G.TryProfile(Vector3.Zero,new(1000,0,2000),stock,out var p) && G.TryFreeze(p,new(.6f,0,.8f),out var f) && Near(Vector3.Distance(f.ImpactPoint,p.ImpactCenter),p.Radius) && f.ImpactPoint.Y==0);
Check("invalid native draws cannot escape the circle", () => {
    if(!G.TryProfile(Vector3.Zero,new(1000,0,0),stock,out var p))return false;
    foreach(var n in new[]{new Vector3(1.001f,0,0),new Vector3(0,.01f,0),new Vector3(float.NaN,0,0),new Vector3(0,0,float.PositiveInfinity)}) if(G.TryFreeze(p,n,out _))return false;
    return true;
});
Check("freeze rejects fabricated invalid profiles", () => G.TryProfile(Vector3.Zero,new(1000,0,0),stock,out var p) && !G.TryFreeze(p with {Radius=-1},Vector3.Zero,out _) && !G.TryFreeze(p with {Range=0},Vector3.Zero,out _) && !G.TryFreeze(p with {ImpactCenter=new(1000,10,0)},Vector3.Zero,out _));
Check("frozen shot does not follow a later aim profile", () => {
    if(!G.TryProfile(Vector3.Zero,new(1000,20,0),stock,out var p) || !G.TryFreeze(p,new(.5f,0,0),out var f))return false;
    var point=f.ImpactPoint;
    return G.TryProfile(Vector3.Zero,new(6000,80,9000),stock,out _) && f.ImpactPoint==point && f.Profile.AimCenter==p.AimCenter;
});
Check("zero-radius settings keep a finite exact center", () => G.TryProfile(Vector3.Zero,new(20,50,30),new(0,0,10000),out var p) && G.TryFreeze(p,new(.6f,0,.8f),out var f) && f.ImpactPoint==new Vector3(20,0,30));
Check("negative extrapolated radius and overflowing endpoints fail closed", () => !G.TryProfile(Vector3.Zero,new(30000,0,0),new(30,10,10000),out _) && G.TryProfile(new(float.MaxValue,0,0),new(float.MaxValue,0,0),new(float.MaxValue,float.MaxValue,1),out var p) && !G.TryFreeze(p,Vector3.UnitX,out _));
Check("three-disk averages remain bounded and center concentrated after freezing", () => {
    if(!G.TryProfile(Vector3.Zero,Vector3.Zero,new(100,100,1),out var p))return false;
    var random=new Random(78125);double sumSquare=0;
    for(int i=0;i<20000;i++) {
        Vector3 average=Vector3.Zero;
        for(int j=0;j<3;j++) { double angle=2*Math.PI*random.NextDouble(),r=Math.Sqrt(random.NextDouble()); average+=new Vector3((float)(r*Math.Cos(angle)),0,(float)(r*Math.Sin(angle))); }
        average/=3;
        if(!G.TryFreeze(p,average,out var f))return false;
        double square=f.ImpactPoint.LengthSquared()/10000d;
        if(square>1.000001 || f.ImpactPoint.Y!=0)return false;
        sumSquare+=square;
    }
    return sumSquare/20000>.15 && sumSquare/20000<.18;
});
Console.WriteLine($"TOTAL {passed+failed}; PASSED {passed}; FAILED {failed}");
Environment.ExitCode=failed==0?0:1;
