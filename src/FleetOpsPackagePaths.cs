using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;

namespace FleetOps.Export;

internal static class FleetOpsPackagePaths
{
    internal sealed record Layout(string Root, string Game, string Userdata, string Designs, string Marker);
    internal static Layout Resolve(string exeDirectory)
    {
        if (!TryStructuralGameRoot(exeDirectory, out string root)) throw Fail("Executable must be packageRoot/lab/fleet-operations/game.");
        string game = Path.Combine(root, "lab", "fleet-operations", "game");
        string data = Path.Combine(root, "lab", "fleet-operations", "userdata");
        var layout = new Layout(root, game, data, Path.Combine(data, "Designs"), Path.Combine(root, "fleetops-package.json"));
        RequireDirectories(layout);
        RequireMarker(layout.Marker);
        return layout;
    }

    // Fixed chains only: safe to call each frame. Descendant traversal is startup-only below.
    internal static void RequireDirectories(Layout layout)
    {
        foreach (string path in new[] { layout.Root, Path.Combine(layout.Root, "lab"), Path.Combine(layout.Root, "lab", "fleet-operations"), layout.Game })
            RequireDirectory(path, true);
        RequireDirectory(layout.Userdata, false);
        RequireDirectory(layout.Designs, false);
    }

    internal static void RequireSaveTree(Layout layout)
    {
        RequireDirectories(layout);
        RequireDirectory(layout.Userdata, true);
        RequireDirectory(layout.Designs, true);
        var pending = new Stack<string>();
        pending.Push(layout.Userdata);
        while (pending.Count != 0)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw Fail("Save tree must not contain links: " + path);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
            }
        }
    }

    // Pure lexical authority for terminating ONLY this current process. A broken marker
    // must not allow a structurally packaged executable to continue with default saves.
    internal static bool TryStructuralGameRoot(string exeDirectory, out string root)
    {
        root = "";
        try
        {
            if (string.IsNullOrWhiteSpace(exeDirectory) || !Path.IsPathFullyQualified(exeDirectory)) return false;
            foreach (string segment in exeDirectory.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                if (segment == "." || segment == "..") return false;
            string full = Path.GetFullPath(exeDirectory).TrimEnd('\\', '/');
            var game = new DirectoryInfo(full);
            var fleet = game.Parent; var lab = fleet?.Parent; var package = lab?.Parent;
            if (package == null || !EqualName(game.Name, "game") || !EqualName(fleet!.Name, "fleet-operations") || !EqualName(lab!.Name, "lab")) return false;
            if (!SamePath(full, Path.Combine(package.FullName, "lab", "fleet-operations", "game"))) return false;
            root = package.FullName;
            return true;
        }
        catch (Exception error) when (error is ArgumentException || error is IOException || error is NotSupportedException || error is UnauthorizedAccessException) { return false; }
    }

    // A prior validation may document authority but never expands it to another
    // executable or a noncanonical path. This also works before marker validation.
    internal static bool HasExitAuthority(string exeDirectory, string? validatedGame = null) =>
        TryStructuralGameRoot(exeDirectory, out _);

    internal static bool SamePath(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException || error is IOException || error is NotSupportedException) { return false; }
    }

    private static bool EqualName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static InvalidOperationException Fail(string message) => new(message);
    private static void RequireDirectory(string path, bool required)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception error) when (error is FileNotFoundException || error is DirectoryNotFoundException)
        { if (!required) return; throw Fail("Required package directory missing: " + path); }
        if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
            throw Fail("Package directory must be a real directory, never a link/file: " + path);
    }

    private static void RequireMarker(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw Fail("Package marker must be a regular file.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 65536) throw Fail("Package marker too large.");
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Fail("Package marker must be an object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? packageId = null, version = null; int? schema = null;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Name != "PackageId" && property.Name != "SchemaVersion" && property.Name != "ExportVersion") continue;
                if (!seen.Add(property.Name)) throw Fail("Duplicate package marker authority field.");
                if (property.Name == "SchemaVersion")
                {
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out int value)) throw Fail("Invalid marker schema type.");
                    schema = value;
                }
                else
                {
                    if (property.Value.ValueKind != JsonValueKind.String) throw Fail("Invalid marker string type.");
                    if (property.Name == "PackageId") packageId = property.Value.GetString(); else version = property.Value.GetString();
                }
            }
            if (packageId != "uad-fleet-operations" || schema != 1 || version != "0.0.8.1-export-r1") throw Fail("Package marker identity/schema/export version mismatch.");
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException)
        { throw new InvalidOperationException("Required package marker is unavailable or malformed.", error); }
    }
}
