using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Rhino.PlugIns;

[assembly: Guid("ed923aa8-899c-4e72-a707-6a75995f197b")]

// Custom Rhino UserData requires a RhinoCommon plug-in identity. The fixture
// registers one in the isolated host process; it does not install a package.
public sealed class FixtureUserDataPlugin : PlugIn { }

internal static class FixtureUserData
{
    internal static void EnsureRegistered()
    {
        var assembly = typeof(FixtureUserDataPlugin).Assembly;
        if (PlugIn.Find(assembly) != null) return;
        var create = typeof(PlugIn).GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Rhino cannot create an isolated UserData fixture plugin.");
        var plugin = create.Invoke(null, new object?[] { typeof(FixtureUserDataPlugin), "Panel+ UserData fixture", "1.0", false, assembly }) as PlugIn
            ?? throw new InvalidOperationException("Rhino could not create the isolated UserData fixture plugin.");
        // The ordinary Rhino loader adds the created plugin to this process-local
        // list. The isolated harness has no installed plugin for that loader to add.
        if (PlugIn.Find(assembly) == null)
        {
            var plugins = typeof(PlugIn).GetField("m_plugins", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as ICollection<PlugIn>
                ?? throw new InvalidOperationException("Rhino's in-memory plugin list is unavailable.");
            plugins.Add(plugin);
        }
        if (PlugIn.Find(assembly) == null)
            throw new InvalidOperationException("Rhino did not register the isolated UserData fixture plugin.");
    }
}
