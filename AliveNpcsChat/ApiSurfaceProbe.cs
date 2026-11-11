using System.Reflection;
using System.Text;
using StardewModdingAPI;

namespace AliveNpcsChat;

/// <summary>
/// Dumps the public API surface of the installed AliveNpcs.dll to chat-api-report.txt:
/// every public type in its Api-related namespaces, with methods and properties. This is
/// reconnaissance for wiring a dynamic prompt block (which needs AliveNpcs' own types at
/// compile time), so the code can be written against the exact signatures installed here.
/// Read-only: nothing in AliveNpcs is called or changed.
/// </summary>
internal static class ApiSurfaceProbe
{
    public static void Write(IAliveNpcsApi api, IModHelper helper, IMonitor monitor)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AliveNpcs Chat addon — API surface report");
        sb.AppendLine($"AliveNpcs assembly: {api.GetType().Assembly.FullName}");
        sb.AppendLine();

        Type[] types;
        try
        {
            types = api.GetType().Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types may fail to load (missing game refs); the rest are still useful.
            types = ex.Types.Where(t => t is not null).Select(t => t!).ToArray();
            sb.AppendLine($"(partial: {ex.Types.Count(t => t is null)} types failed to load)");
            sb.AppendLine();
        }
        catch (Exception ex)
        {
            monitor.Log($"Couldn't list AliveNpcs' types: {ex}", LogLevel.Error);
            return;
        }

        // Everything in an Api-ish namespace, plus registration/hook types wherever they live.
        var interesting = types
            .Where(t => t.IsPublic)
            .Where(t =>
                (t.Namespace?.Contains("Api", StringComparison.Ordinal) ?? false)
                || t.Name.Contains("Registration", StringComparison.Ordinal)
                || t.Name.Contains("PromptBlock", StringComparison.Ordinal)
                || t.Name.Contains("StoryArc", StringComparison.Ordinal)
                || t.Name.Contains("Metadata", StringComparison.Ordinal))
            .OrderBy(t => t.Namespace)
            .ThenBy(t => t.Name)
            .ToList();

        sb.AppendLine($"{interesting.Count} matching public types:");
        sb.AppendLine();

        foreach (Type t in interesting)
        {
            sb.AppendLine($"== {t.FullName}");
            sb.AppendLine($"   kind: {(t.IsInterface ? "interface" : t.IsEnum ? "enum" : typeof(Delegate).IsAssignableFrom(t) ? "delegate" : "class")}");

            try
            {
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    sb.AppendLine($"   property {TypeName(p.PropertyType)} {p.Name} {{ get; set; }}");

                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                             .Where(m => !m.IsSpecialName))
                {
                    var parms = string.Join(", ", m.GetParameters().Select(p => TypeName(p.ParameterType) + " " + p.Name));
                    sb.AppendLine($"   method {TypeName(m.ReturnType)} {m.Name}({parms})");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"   (couldn't read members: {ex.Message})");
            }

            sb.AppendLine();
        }

        string outPath = Path.Combine(helper.DirectoryPath, "chat-api-report.txt");
        try
        {
            File.WriteAllText(outPath, sb.ToString());
            monitor.Log(
                $"API surface report written to:\n{outPath}\n"
                + "Paste its contents to your assistant so the prompt block can be wired up exactly.",
                LogLevel.Info);
        }
        catch (Exception ex)
        {
            monitor.Log($"Couldn't write the API report: {ex}", LogLevel.Error);
        }
    }

    /// <summary>A readable type name, with generic arguments spelled out.</summary>
    private static string TypeName(Type t)
    {
        if (t.IsGenericType)
        {
            string name = t.Name.Split('`')[0];
            return name + "<" + string.Join(", ", t.GetGenericArguments().Select(TypeName)) + ">";
        }
        return t.Name;
    }
}
