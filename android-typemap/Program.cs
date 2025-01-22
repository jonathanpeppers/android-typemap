using Mono.Cecil;

var folder = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location)!, "..", "..", "..", "..", "assemblies");
folder = Path.GetFullPath(folder);

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(folder);

var javaTypes = new List<TypeDefinition>();
foreach (var path in Directory.GetFiles(folder, "*dll"))
{
    Console.WriteLine(path);

    var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver });
    foreach (ModuleDefinition md in assembly.Modules)
    {
        foreach (TypeDefinition td in md.Types)
        {
            AddJavaTypes(javaTypes, td);
        }
    }

    foreach (var type in javaTypes)
    {
        Console.WriteLine(type.FullName);
    }
}

static void AddJavaTypes(List<TypeDefinition> javaTypes, TypeDefinition type)
{
    if (HasJavaPeer(type))
    {
        javaTypes.Add(type);
    }

    if (!type.HasNestedTypes)
        return;

    foreach (TypeDefinition nested in type.NestedTypes)
        AddJavaTypes(javaTypes, nested);
}

static bool HasJavaPeer(TypeDefinition type)
{
    if (type.IsInterface && ImplementsInterface(type, "Java.Interop.IJavaPeerable"))
        return true;

    foreach (var t in GetTypeAndBaseTypes(type))
    {
        switch (t.FullName)
        {
            case "Java.Lang.Object":
            case "Java.Lang.Throwable":
            case "Java.Interop.JavaObject":
            case "Java.Interop.JavaException":
                return true;
            default:
                break;
        }
    }
    return false;
}

static bool ImplementsInterface(TypeDefinition type, string interfaceName)
{
    foreach (var t in GetTypeAndBaseTypes(type))
    {
        foreach (var i in t.Interfaces)
        {
            if (i.InterfaceType.FullName == interfaceName)
            {
                return true;
            }
        }
    }
    return false;
}

static TypeDefinition? GetBaseType(TypeDefinition type)
{
    var bt = type.BaseType;
    if (bt == null)
        return null;
    return bt.Resolve();
}

static IEnumerable<TypeDefinition> GetTypeAndBaseTypes(TypeDefinition type)
{
    TypeDefinition? t = type;

    while (t != null)
    {
        yield return t;
        t = GetBaseType(t);
    }
}
