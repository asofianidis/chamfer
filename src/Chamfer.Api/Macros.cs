using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Chamfer.Document;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Chamfer.Api;

/// <summary>The variables a macro sees.</summary>
public sealed class MacroGlobals(ChamferApi api)
{
    public ChamferApi Api { get; } = api;

    public PartDocument Part => Api.Part;

    public void Print(object? value) => Api.Print(value);
}

/// <summary>Outcome of running a macro.</summary>
public sealed record MacroResult(bool Succeeded, IReadOnlyList<string> Output, string? Error, object? ReturnValue);

/// <summary>Runs C# macros (Roslyn scripts) and records user actions as macros.</summary>
public static class Macros
{
    private static readonly Lazy<ScriptOptions> Options = new(() => ScriptOptions.Default
        .AddReferences(
            typeof(ChamferApi).Assembly,
            typeof(PartDocument).Assembly,
            typeof(Geometry.Vec3).Assembly,
            typeof(Kernel.Occt.Shape).Assembly,
            typeof(Sketching.Sketch).Assembly,
            typeof(Assembly.AssemblyDocument).Assembly,
            typeof(Enumerable).Assembly)
        .AddImports(
            "System", "System.Linq", "System.Collections.Generic",
            "Chamfer.Api", "Chamfer.Document", "Chamfer.Document.Features",
            "Chamfer.Geometry", "Chamfer.Kernel.Occt", "Chamfer.Sketching"));

    public static async Task<MacroResult> RunAsync(string code, ChamferApi api, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(api);
        try
        {
            var state = await CSharpScript.RunAsync(code, Options.Value, new MacroGlobals(api), typeof(MacroGlobals), cancel).ConfigureAwait(false);
            return new MacroResult(true, [.. api.Output], null, state.ReturnValue);
        }
        catch (CompilationErrorException ex)
        {
            return new MacroResult(false, [.. api.Output], string.Join(Environment.NewLine, ex.Diagnostics.Select(d => d.ToString())), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new MacroResult(false, [.. api.Output], $"{ex.GetType().Name}: {ex.Message}", null);
        }
    }

    /// <summary>
    /// A macro that replays the features added between two versions of a part
    /// (feature by feature, as JSON), plus any variables that changed.
    /// </summary>
    public static string Record(PartDocument before, PartDocument after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var sb = new StringBuilder("// Recorded by Chamfer. Each line adds one feature; edit freely.\n");
        foreach (var v in after.Variables.Where(v => !before.Variables.Contains(v)))
        {
            sb.Append($"Api.Variable({Literal(v.Name)}, {Literal(v.Expression)});\n");
        }

        var known = before.Features.Select(f => f.Id).ToHashSet();
        foreach (var f in after.Features.Where(f => !known.Contains(f.Id)))
        {
            sb.Append($"Api.AddFeatureJson({Verbatim(PartDocument.SerializeFeature(f))});\n");
        }

        return sb.ToString();
    }

    private static string Literal(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Verbatim(string s) => "@\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

/// <summary>A plugin: a .NET assembly in the plugins folder with a type implementing this.</summary>
public interface IChamferPlugin
{
    string Name { get; }

    void Initialize(IPluginHost host);
}

/// <summary>What a plugin can do to the application.</summary>
public interface IPluginHost
{
    /// <summary>Adds a command button; <paramref name="run"/> gets the API for the open part.</summary>
    void AddCommand(string label, string tooltip, Action<ChamferApi> run);
}

/// <summary>Loads plugins, each in its own load context so they can carry their own dependencies.</summary>
public static class PluginLoader
{
    public static IReadOnlyList<IChamferPlugin> Load(string folder, Action<string>? onError = null)
    {
        var plugins = new List<IChamferPlugin>();
        if (!Directory.Exists(folder))
        {
            return plugins;
        }

        foreach (var dll in Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories))
        {
            try
            {
                var context = new PluginLoadContext(dll);
                var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(dll));
                foreach (var type in assembly.GetExportedTypes().Where(t => typeof(IChamferPlugin).IsAssignableFrom(t) && !t.IsAbstract))
                {
                    if (Activator.CreateInstance(type) is IChamferPlugin plugin)
                    {
                        plugins.Add(plugin);
                    }
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ReflectionTypeLoadException or TypeLoadException or MissingMethodException or TargetInvocationException)
            {
                onError?.Invoke($"{Path.GetFileName(dll)}: {ex.Message}");
            }
        }

        return plugins;
    }

    private sealed class PluginLoadContext(string path) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);

        protected override System.Reflection.Assembly? Load(AssemblyName name)
        {
            // Share Chamfer's own assemblies with the host so plugin types match.
            if (Default.Assemblies.Any(a => a.GetName().Name == name.Name))
            {
                return null;
            }

            return _resolver.ResolveAssemblyToPath(name) is { } p ? LoadFromAssemblyPath(p) : null;
        }
    }
}
