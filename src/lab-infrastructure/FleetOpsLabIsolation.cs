using System.Diagnostics;
using System.Reflection;
using MelonLoader;

[assembly: MelonInfo(typeof(FleetOperationsLab.FleetOpsLabIsolation), "Fleet Operations Lab Isolation", "0.1.0-export-r1", "Local isolated experiment")]
[assembly: MelonPriority(int.MinValue)]

namespace FleetOperationsLab;

public sealed class FleetOpsLabIsolation : MelonMod
{
    private const string RelativeData = @"lab\fleet-operations\userdata";
    private string _saveRoot = "";
    private string _validatedGame = "";
    private PropertyInfo? _prefix, _cache, _designs, _designCache;
    private bool _ready;

    public override void OnInitializeMelon()
    {
        try
        {
            string root = RequireExecutableRoot();
            _saveRoot = Path.Combine(root, RelativeData) + Path.DirectorySeparatorChar;
            RequireDirectories();
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp")
                ?? Assembly.Load("Assembly-CSharp");
            Type storage = assembly.GetType("Il2Cpp.Storage", throwOnError: true)!;
            _prefix = RequireProperty(storage, "prefix", false);
            _cache = RequireProperty(storage, "cachedPrefix", true);
            _designs = RequireProperty(storage, "designsPrefix", false);
            _designCache = RequireProperty(storage, "designsPrefixCache", true);
            Directory.CreateDirectory(_saveRoot);
            Directory.CreateDirectory(Path.Combine(_saveRoot, "Designs"));
            RequireDirectories();
            FleetOps.Export.FleetOpsPackagePaths.RequireSaveTree(FleetOps.Export.FleetOpsPackagePaths.Resolve(CurrentExecutableDirectory()));
            _cache.SetValue(null, _saveRoot);
            _designCache.SetValue(null, Path.Combine(_saveRoot, "Designs") + Path.DirectorySeparatorChar);
            RequireIsolation();
            _ready = true;
            LoggerInstance.Msg("FLEETOPS_ISOLATION_READY prefix=" + Read(_prefix) + " designsPrefix=" + Read(_designs));
            File.WriteAllText(Path.Combine(_saveRoot, "fleetops-isolation-ready.txt"),
                "UTC=" + DateTime.UtcNow.ToString("O") + Environment.NewLine +
                "Process=" + Process.GetCurrentProcess().MainModule?.FileName + Environment.NewLine +
                "Storage.prefix=" + Read(_prefix) + Environment.NewLine +
                "Storage.designsPrefix=" + Read(_designs) + Environment.NewLine);
        }
        catch (Exception error) { Abort(error); }
    }

    public override void OnUpdate()
    {
        if (!_ready) return;
        try { RequireIsolation(); }
        catch (Exception error) { Abort(error); }
    }

    private void RequireIsolation()
    {
        string root = RequireExecutableRoot();
        if (!SamePath(_saveRoot, Path.Combine(root, RelativeData))) throw new InvalidOperationException("Save-root authority changed.");
        RequireDirectories();
        string designs = Path.Combine(_saveRoot, "Designs");
        if (!SamePath(Read(_cache), _saveRoot) || !SamePath(Read(_prefix), _saveRoot) ||
            !SamePath(Read(_designCache), designs) || !SamePath(Read(_designs), designs))
            throw new InvalidOperationException("Storage cache/getter escaped the isolated fleet-operations userdata.");
    }

    private static string CurrentExecutableDirectory() => Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName ?? "") ?? "";

    private string RequireExecutableRoot()
    {
        var paths = FleetOps.Export.FleetOpsPackagePaths.Resolve(CurrentExecutableDirectory());
        if (_validatedGame.Length != 0 && !SamePath(_validatedGame, paths.Game)) throw new InvalidOperationException("Validated package game changed.");
        _validatedGame = paths.Game;
        return paths.Root;
    }

    private static void RequireDirectories()
    {
        FleetOps.Export.FleetOpsPackagePaths.Resolve(CurrentExecutableDirectory());
    }

    private static PropertyInfo RequireProperty(Type storage, string name, bool writable)
    {
        PropertyInfo? property = storage.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (property?.PropertyType != typeof(string) || !property.CanRead || (writable && !property.CanWrite))
            throw new MissingMemberException("Native Storage string property unavailable: " + name);
        return property;
    }

    private static string Read(PropertyInfo? property) => (string?)property!.GetValue(null) ?? "";
    private static bool SamePath(string actual, string expected) => FleetOps.Export.FleetOpsPackagePaths.SamePath(actual, expected);

    private void Abort(Exception error)
    {
        _ready = false;
        LoggerInstance.Error("FLEETOPS_ISOLATION_FAILED " + error);
        string directory = CurrentExecutableDirectory();
        // Never stop another process, including the original P0 lab or production game.
        if (FleetOps.Export.FleetOpsPackagePaths.HasExitAuthority(directory, _validatedGame))
            Environment.Exit(89);
        LoggerInstance.Error("Outside the experiment: plugin disabled; no other executable is terminated.");
    }
}
