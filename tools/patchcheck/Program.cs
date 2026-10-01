using Mono.Cecil;
// Verifies every [HarmonyPatch] in the plugin resolves against the game's assemblies, and that
// prefix/postfix parameter names (other than __injected ones) exist on the target method.
var managed = args[0];
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(managed);
var rp = new ReaderParameters { AssemblyResolver = resolver };
var plugin = AssemblyDefinition.ReadAssembly(args[1], rp);
int bad = 0, ok = 0;
foreach (var type in plugin.MainModule.Types)
foreach (var m in type.Methods)
{
    var attrs = m.CustomAttributes.Where(a => a.AttributeType.Name == "HarmonyPatch").ToList();
    if (attrs.Count == 0) continue;
    var a = attrs[0];
    var targetType = (TypeReference)a.ConstructorArguments[0].Value;
    string name = a.ConstructorArguments.Count > 1 ? (string)a.ConstructorArguments[1].Value : null;
    TypeReference[] argTypes = a.ConstructorArguments.Count > 2 ? ((CustomAttributeArgument[])a.ConstructorArguments[2].Value).Select(x => (TypeReference)x.Value).ToArray() : null;
    var td = targetType.Resolve();
    var candidates = td.Methods.Where(x => x.Name == name).ToList();
    if (argTypes != null) candidates = candidates.Where(x => x.Parameters.Count == argTypes.Length && x.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(argTypes.Select(t => t.FullName))).ToList();
    if (candidates.Count != 1) { Console.WriteLine($"BAD  {m.Name}: {td.FullName}.{name} matches {candidates.Count}"); bad++; continue; }
    var target = candidates[0];
    foreach (var p in m.Parameters)
    {
        if (p.Name.StartsWith("__")) continue;
        var tp = target.Parameters.FirstOrDefault(x => x.Name == p.Name);
        if (tp == null) { Console.WriteLine($"BAD  {m.Name}: param '{p.Name}' not on {td.Name}.{name}({string.Join(",", target.Parameters.Select(x=>x.Name))})"); bad++; }
        else if (tp.ParameterType.FullName != p.ParameterType.FullName.TrimEnd('&') && tp.ParameterType.FullName != p.ParameterType.FullName) { Console.WriteLine($"BAD  {m.Name}: param '{p.Name}' type {p.ParameterType.FullName} vs {tp.ParameterType.FullName}"); bad++; }
    }
    if (m.Parameters.Any(p => p.Name == "__instance") && target.IsStatic) { Console.WriteLine($"BAD  {m.Name}: __instance on static"); bad++; }
    if (m.ReturnType.FullName == "System.Boolean" || m.ReturnType.FullName == "System.Void") ok++;
    Console.WriteLine($"ok   {td.Name}.{name}  <- {m.Name}");
}
Console.WriteLine($"{ok} patches, {bad} problems");
return bad == 0 ? 0 : 1;
