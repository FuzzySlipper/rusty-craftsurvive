using System.Reflection;

// A reflection pass over the installed Engine assembly. The SDK package ships no
// documentation file, so the only reliable way to ask what a type actually
// exposes - rather than what a string search suggests - is to load it and look.

if (args.Length == 0)
{
    Console.WriteLine("usage: EngineSurface <path-to-Rusty.Engine.dll> [name-filter]");
    return;
}

Assembly assembly = Assembly.LoadFrom(args[0]);
string filter = args.Length > 1 ? args[1] : "Navigation";
string[] wantedProperties = ["Origin", "CellSize", "Extent", "CellCount", "Bounds", "WalkableCellCount"];

Console.WriteLine($"=== types declaring a navigation-shaped property (filter: {filter}) ===");
foreach (Type type in assembly.GetExportedTypes().OrderBy(candidate => candidate.FullName))
{
    try
    {
        PropertyInfo[] hits = [.. type.GetProperties().Where(property => wantedProperties.Contains(property.Name))];
        if (hits.Length == 0)
        {
            continue;
        }

        Console.WriteLine($"{type.FullName}");
        Console.WriteLine($"    {string.Join(", ", hits.Select(property => $"{property.PropertyType.Name} {property.Name}"))}");
    }
    catch (Exception exception)
    {
        Console.WriteLine($"{type.FullName}: <unreadable: {exception.GetType().Name}>");
    }
}

Console.WriteLine($"=== constructors and properties of {filter} types ===");
foreach (Type type in assembly.GetExportedTypes()
    .Where(candidate => candidate.Name.Contains(filter, StringComparison.Ordinal))
    .OrderBy(candidate => candidate.FullName))
{
    try
    {
        Console.WriteLine($"{type.FullName}");
        foreach (ConstructorInfo constructor in type.GetConstructors())
        {
            Console.WriteLine($"    ctor({string.Join(", ", constructor.GetParameters().Select(p => $"{p.ParameterType} {p.Name}"))})");
        }

        PropertyInfo[] properties = type.GetProperties();
        if (properties.Length > 0)
        {
            Console.WriteLine($"    props: {string.Join(", ", properties.Select(p => $"{p.PropertyType.Name} {p.Name}"))}");
        }

        MethodInfo[] methods = [.. type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(method => !method.IsSpecialName && method.DeclaringType == type)];
        if (methods.Length > 0)
        {
            Console.WriteLine($"    methods: {string.Join(", ", methods.Select(method => $"{method.ReturnType.Name} {method.Name}({string.Join(",", method.GetParameters().Select(p => p.ParameterType.ToString()))})"))}");
        }
    }
    catch (Exception exception)
    {
        Console.WriteLine($"{type.FullName}: <unreadable: {exception.GetType().Name}>");
    }
}
