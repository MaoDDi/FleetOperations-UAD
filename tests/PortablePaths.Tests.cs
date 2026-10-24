using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using FleetOps.Export;

internal static class PortablePathsTests
{
    private const string Manifest = "{\"PackageId\":\"uad-fleet-operations\",\"SchemaVersion\":1,\"ExportVersion\":\"0.0.8.1-export-r1\"}";
    private static int passed, failed;
    private static readonly string Sandbox = Path.Combine(Path.GetTempPath(), "FleetOps-PortablePaths-Tests-" + Guid.NewGuid().ToString("N"));
    private static readonly List<string> Junctions = new();
    private static int serial;
    private sealed record Fixture(string Root, string Game, string Data, string Designs, string Marker);
    private static Fixture New(bool saves = true)
    {
        string root = Path.Combine(Sandbox, "移动 包 " + ++serial);
        string game = Path.Combine(root, "lab", "fleet-operations", "game");
        string data = Path.Combine(root, "lab", "fleet-operations", "userdata");
        string designs = Path.Combine(data, "Designs");
        Directory.CreateDirectory(game);
        if (saves) Directory.CreateDirectory(designs);
        string marker = Path.Combine(root, "fleetops-package.json");
        File.WriteAllText(marker, Manifest);
        return new(root, game, data, designs, marker);
    }
    private static void Check(bool ok) { if (!ok) throw new Exception("Assertion failed."); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected fail-closed InvalidOperationException.");
    }
    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    private static void Link(string path, string target)
    {
        RequireOwned(path); RequireOwned(target);
        if (Directory.Exists(path)) Directory.Delete(path, false);
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "/c", "mklink", "/J", path, target }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception("Test junction creation failed: " + output);
        Junctions.Add(path);
    }
    private static void RequireOwned(string path)
    {
        string prefix = Path.GetFullPath(Sandbox).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new Exception("Test path escaped its sandbox.");
    }
    public static int Main()
    {
        Directory.CreateDirectory(Sandbox);
        try
        {
            Test("valid moved Unicode package fixes all paths", () => { var f = New(); var p = FleetOpsPackagePaths.Resolve(f.Game); Check(p.Root == f.Root && p.Game == f.Game && p.Userdata == f.Data && p.Designs == f.Designs && p.Marker == f.Marker); });
            Test("trailing separator remains canonical", () => { var f = New(); Check(FleetOpsPackagePaths.Resolve(f.Game + Path.DirectorySeparatorChar).Game == f.Game); });
            Test("absent saves accepted without creating directories", () => { var f = New(false); FleetOpsPackagePaths.Resolve(f.Game); Check(!Directory.Exists(f.Data)); });
            foreach (var item in new[] {
                ("missing marker", (string?)null), ("malformed marker", "{"), ("nonobject marker", "[]"),
                ("wrong PackageId", Manifest.Replace("uad-fleet-operations", "other")),
                ("wrong SchemaVersion", Manifest.Replace(":1", ":2")),
                ("schema wrong type", Manifest.Replace(":1", ":\"1\"")),
                ("wrong ExportVersion", Manifest.Replace("0.0.8.1-export-r1", "0.0.8")),
                ("duplicate required property", Manifest.Replace("{", "{\"SchemaVersion\":1,")),
                ("missing required property", "{\"PackageId\":\"uad-fleet-operations\",\"SchemaVersion\":1}") })
                Test(item.Item1, () => { var f = New(); if (item.Item2 == null) File.Delete(f.Marker); else File.WriteAllText(f.Marker, item.Item2); Reject(() => FleetOpsPackagePaths.Resolve(f.Game)); });
            Test("marker path override is ignored", () => { var f = New(); File.WriteAllText(f.Marker, Manifest.Replace("}", ",\"UserdataPath\":\"C:/outside\"}")); Check(FleetOpsPackagePaths.Resolve(f.Game).Userdata == f.Data); });
            Test("marker directory rejected", () => { var f = New(); File.Delete(f.Marker); Directory.CreateDirectory(f.Marker); Reject(() => FleetOpsPackagePaths.Resolve(f.Game)); });
            Test("missing game rejected", () => { var f = New(); Directory.Delete(f.Game); Reject(() => FleetOpsPackagePaths.Resolve(f.Game)); });
            Test("save file rejected", () => { var f = New(false); File.WriteAllText(f.Data, "file"); Reject(() => FleetOpsPackagePaths.Resolve(f.Game)); });
            Test("relative path rejected", () => Reject(() => FleetOpsPackagePaths.Resolve(@"lab\fleet-operations\game")));
            Test("dot traversal rejected", () => { var f = New(); Reject(() => FleetOpsPackagePaths.Resolve(Path.Combine(f.Game, "..", "game"))); });
            Test("wrong tail rejected", () => { var f = New(); Reject(() => FleetOpsPackagePaths.Resolve(Path.Combine(f.Root, "lab", "wrong", "game"))); });
            Test("structural root returns exact ancestor", () => { var f = New(); Check(FleetOpsPackagePaths.TryStructuralGameRoot(f.Game, out string root) && root == f.Root); });
            Test("missing marker retains only current structural exit authority", () => { var f = New(); File.Delete(f.Marker); Check(FleetOpsPackagePaths.HasExitAuthority(f.Game)); });
            Test("malformed marker retains structural exit authority", () => { var f = New(); File.WriteAllText(f.Marker, "{"); Check(FleetOpsPackagePaths.HasExitAuthority(f.Game)); });
            Test("ordinary Steam has no exit authority", () => Check(!FleetOpsPackagePaths.HasExitAuthority(@"C:\Steam\steamapps\common\Ultimate Admiral Dreadnoughts")));
            Test("relative path has no exit authority", () => Check(!FleetOpsPackagePaths.HasExitAuthority(@"lab\fleet-operations\game")));
            Test("changed current executable cannot use cached authority", () => { var f = New(); Check(!FleetOpsPackagePaths.HasExitAuthority(@"C:\Steam\game", f.Game)); });
            Test("noncanonical path cannot borrow cached exit authority", () => { var f = New(); Check(!FleetOpsPackagePaths.HasExitAuthority(Path.Combine(f.Game, "..", "game"), f.Game)); });
            Test("same path normalization", () => { var f = New(); Check(FleetOpsPackagePaths.SamePath(f.Data.ToUpperInvariant() + "\\", f.Data) && !FleetOpsPackagePaths.SamePath("", f.Data)); });
            Test("normal save descendants pass startup tree scan", () => { var f = New(); Directory.CreateDirectory(Path.Combine(f.Designs, "nested")); File.WriteAllText(Path.Combine(f.Data, "save.json"), "{}"); FleetOpsPackagePaths.RequireSaveTree(FleetOpsPackagePaths.Resolve(f.Game)); });
            foreach (string level in new[] { "root", "lab", "fleet", "game", "userdata", "Designs" })
                Test(level + " junction rejected", () => {
                    var f = New(false); string target = Path.Combine(Sandbox, "target-" + ++serial); Directory.CreateDirectory(target);
                    string path = level switch { "root" => f.Root, "lab" => Path.Combine(f.Root, "lab"), "fleet" => Path.Combine(f.Root, "lab", "fleet-operations"), "game" => f.Game, "userdata" => f.Data, _ => f.Designs };
                    // Move only within this test's verified sandbox; preserve the valid layout behind the link.
                    RequireOwned(path); RequireOwned(target);
                    if (Directory.Exists(path)) { Directory.Delete(target); Directory.Move(path, target); }
                    if (level == "Designs") Directory.CreateDirectory(f.Data);
                    Link(path, target); Reject(() => FleetOpsPackagePaths.Resolve(f.Game));
                });
            Test("nested save junction rejected only by startup tree scan", () => { var f = New(); string target = Path.Combine(Sandbox, "target-" + ++serial); Directory.CreateDirectory(target); Link(Path.Combine(f.Designs, "nested-link"), target); var p = FleetOpsPackagePaths.Resolve(f.Game); Reject(() => FleetOpsPackagePaths.RequireSaveTree(p)); });
            Test("fixed chain rechecked after replacement", () => { var f = New(); var p = FleetOpsPackagePaths.Resolve(f.Game); Directory.Delete(f.Designs); File.WriteAllText(f.Designs, "file"); Reject(() => FleetOpsPackagePaths.RequireDirectories(p)); });
        }
        finally
        {
            foreach (string link in Junctions) if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) Directory.Delete(link, false);
            string full = Path.GetFullPath(Sandbox); string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\', '/');
            if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("FleetOps-PortablePaths-Tests-", StringComparison.Ordinal)) throw new Exception("Unsafe test cleanup path.");
            Directory.Delete(full, true);
        }
        Console.WriteLine($"TOTAL {passed + failed}; PASSED {passed}; FAILED {failed}");
        return failed == 0 ? 0 : 1;
    }
}
