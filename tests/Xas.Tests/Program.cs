using System.Reflection;

if (args.Length > 0 && args[0] == "--echo-args")
{
    Console.Write(string.Join('|', args.Skip(1)));
    return;
}
if (args.Length > 0 && args[0] == "--echo-stdin")
{
    await Console.OpenStandardInput().CopyToAsync(Console.OpenStandardOutput());
    return;
}

var suites = Assembly.GetExecutingAssembly().GetTypes()
    .Where(type => type.IsClass && type.Name.EndsWith("Tests", StringComparison.Ordinal))
    .Select(type => (Type: type, Method: type.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Static)))
    .Where(suite => suite.Method is not null)
    .OrderBy(suite => suite.Type.Name)
    .ToArray();

foreach (var suite in suites)
{
    Console.WriteLine($"Running {suite.Type.Name}...");
    await (Task)suite.Method!.Invoke(null, null)!;
    Console.WriteLine($"Passed {suite.Type.Name}");
}

Console.WriteLine($"Passed {suites.Length} test suites.");
