using System.Reflection;
using DriverGateway.Plugins.Abstractions;

namespace DriverGateway.Host.OpcUa.Runtime;

internal static class DriverPluginLoader
{
    public static IReadOnlyDictionary<string, IDriverPlugin> LoadPlugins(string pluginsDirectory, Action<string>? log)
    {
        var plugins = new Dictionary<string, IDriverPlugin>(StringComparer.OrdinalIgnoreCase);

        LoadFromDirectory(pluginsDirectory, plugins, log);
        LoadFromCurrentAppDomain(plugins, log);

        return plugins;
    }

    private static void LoadFromDirectory(
        string pluginsDirectory,
        IDictionary<string, IDriverPlugin> plugins,
        Action<string>? log)
    {
        if (!Directory.Exists(pluginsDirectory))
        {
            return;
        }

        foreach (var dllPath in Directory.EnumerateFiles(pluginsDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var assembly = Assembly.LoadFrom(dllPath);
                RegisterAssemblyPlugins(assembly, plugins, log);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[PLUGIN] Failed to load '{dllPath}'. {ex.Message}");
            }
        }
    }

    private static void LoadFromCurrentAppDomain(IDictionary<string, IDriverPlugin> plugins, Action<string>? log)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            RegisterAssemblyPlugins(assembly, plugins, log);
        }
    }

    private static void RegisterAssemblyPlugins(
        Assembly assembly,
        IDictionary<string, IDriverPlugin> plugins,
        Action<string>? log)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(static type => type is not null).Cast<Type>().ToArray();
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || !typeof(IDriverPlugin).IsAssignableFrom(type))
            {
                continue;
            }

            if (Activator.CreateInstance(type) is not IDriverPlugin plugin)
            {
                continue;
            }

            if (plugins.TryGetValue(plugin.DriverType, out var existing) &&
                existing.GetType() == plugin.GetType())
            {
                continue;
            }

            plugins[plugin.DriverType] = plugin;
            log?.Invoke($"[PLUGIN] Registered '{plugin.DriverType}' from {assembly.GetName().Name}.");
        }
    }
}
