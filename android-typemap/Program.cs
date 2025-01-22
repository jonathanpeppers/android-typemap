using System.Text;
using Java.Interop.Tools.JavaCallableWrappers;
using Mono.Cecil;

var top = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location)!, "..", "..", "..", ".."));
var inputDirectory = Path.Combine(top, "assemblies");
var outputFile = Path.Combine(top, "Output.cs");

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(inputDirectory);

var javaTypes = new Dictionary<string, TypeDefinition>();
foreach (var path in Directory.GetFiles(inputDirectory, "*dll"))
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
}

using var writer = File.CreateText(outputFile);
writer.WriteLine("Dictionary<string, Type> typeMappings = new () {");
foreach (var pair in javaTypes)
{
    writer.WriteLine($"\t[\"{pair.Key}\"] = typeof ({pair.Value.FullName}))]");
}
writer.WriteLine('}');

static void AddJavaTypes(Dictionary<string, TypeDefinition> javaTypes, TypeDefinition type)
{
    if (HasJavaPeer(type))
    {
        var key = ToJniName(type) ?? throw new InvalidOperationException("ToJniName returned null");
        javaTypes.TryAdd(key, type);
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

static IEnumerable<TypeDefinition> GetBaseTypes(TypeDefinition type)
{
    TypeDefinition? t = type;

    while ((t = GetBaseType(t)) != null)
    {
        yield return t;
    }
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

static string? ToJniName(TypeDefinition type)
{
    if (type == null)
        throw new ArgumentNullException("type");

    if (type.IsValueType)
        return GetPrimitiveClass(type);

    if (type.FullName == "System.String")
        return "java/lang/String";

    return ToJniNameGeneric(
            type: type,
            decl: t => t.DeclaringType,
            name: t => t.Name,
            ns: t => GetPackageName(t),
            overrideName: t => ToJniNameFromAttributes(t),
            shouldUpdateName: t => IsNonStaticInnerClass(t as TypeDefinition)
    );
}

static string ToJniNameGeneric<T>(T type, Func<T, T> decl, Func<T, string> name, Func<T, string> ns, Func<T, string?> overrideName, Func<T, bool> shouldUpdateName)
    where T : class
{
    var nameParts = new List<string>();
    var typeName = (string?)null;
    var nsType = type;

    for (var declType = type; declType != null; declType = decl(declType))
    {
        nsType = declType;
        typeName = overrideName(declType);
        if (typeName != null)
        {
            break;
        }
        var n = name(declType).Replace('`', '_');
        if (shouldUpdateName(declType))
        {
            n = "$" + name(decl(declType)) + "_" + n;
        }
        nameParts.Add(n);
    }

    if (nameParts.Count == 0 && typeName != null)
        return typeName;

    nameParts.Reverse();

    var nestedSuffix = string.Join("_", nameParts.ToArray()).Replace("_$", "$");
    if (typeName != null)
        return (typeName + "_" + nestedSuffix).Replace("_$", "$"); ;

    // Results in namespace/parts/OuterType_InnerType
    // We do this to simplify monodroid type generation
    typeName = nestedSuffix;
    var _ns = ToLowerCase(ns(nsType));
    return string.IsNullOrEmpty(_ns)
        ? typeName
        : _ns.Replace('.', '/') + "/" + typeName;
}

static bool IsNonStaticInnerClass(TypeDefinition? type)
{
    if (type == null)
        return false;
    if (!type.IsNested)
        return false;

    if (!HasJavaPeer(type.DeclaringType))
        return false;

    foreach (var baseType in GetBaseTypes(type))
    {
        if (baseType == null)
            continue;
        if (!HasTypeRegistrationAttribute(baseType))
            continue;

        foreach (var method in baseType.Methods)
        {
            if (!method.IsConstructor || method.IsStatic)
                continue;
            if (method.Parameters.Any(p => p.Name == "__self"))
                return true;
        }

        // Stop at the first base type with [Register]
        break;
    }

    return false;
}

static string? ToJniNameFromAttributes(TypeDefinition type)
{
    if (!type.HasCustomAttributes)
        return null;
    foreach (var attr in type.CustomAttributes)
    {
        if (!IsIJniNameProviderAttribute(attr))
            continue;
        var ap = attr.HasProperties ? attr.Properties.FirstOrDefault(p => p.Name == "Name") : default;
        string? name = null;
        if (ap.Name == null)
        {
            var ca = attr.ConstructorArguments.FirstOrDefault();
            if (ca.Type == null || ca.Type.FullName != "System.String")
                continue;
            name = (string)ca.Value;
        }
        else
            name = (string)ap.Argument.Value;
        if (!string.IsNullOrEmpty(name))
            return name.Replace('.', '/');
    }
    return null;
}

static bool IsIJniNameProviderAttribute(CustomAttribute attr)
{
    var attributeType = attr.AttributeType.Resolve();
    if (!attributeType.HasInterfaces)
        return false;
    return attributeType.Interfaces.Any(it => it.InterfaceType.FullName == "Java.Interop.IJniNameProviderAttribute");
}

static string? GetPrimitiveClass(TypeDefinition type)
{
    if (type.IsEnum)
        return GetPrimitiveClass(type.Fields.First(f => f.IsSpecialName).FieldType.Resolve());
    if (type.FullName == "System.Byte")
        return "B";
    if (type.FullName == "System.Char")
        return "C";
    if (type.FullName == "System.Double")
        return "D";
    if (type.FullName == "System.Single")
        return "F";
    if (type.FullName == "System.Int32")
        return "I";
    if (type.FullName == "System.Int64")
        return "J";
    if (type.FullName == "System.Int16")
        return "S";
    if (type.FullName == "System.Boolean")
        return "Z";
    return null;
}

static string ToLowerCase(string value)
{
    if (string.IsNullOrEmpty(value))
        return value;
    string[] parts = value.Split('.');
    for (int i = 0; i < parts.Length; ++i)
    {
        parts[i] = parts[i].ToLowerInvariant();
    }
    return string.Join(".", parts);
}

static bool IsPackageNamePreservedForAssembly(string assemblyName)
{
    return assemblyName == "Mono.Android";
}

const string CRC_PREFIX = "crc64";

static string GetPackageName(TypeDefinition type)
{
    if (IsPackageNamePreservedForAssembly(type.Module.Assembly.Name.Name))
        return type.Namespace.ToLowerInvariant();
    return CRC_PREFIX + ToCrc64(type.Namespace + ":" + type.Module.Assembly.Name.Name);
}

static string ToCrc64(string value)
{
    var data = Encoding.UTF8.GetBytes(value);
    var hash = Crc64Helper.Compute(data);
    var buf = new StringBuilder(hash.Length * 2);
    foreach (var b in hash)
        buf.AppendFormat("{0:x2}", b);
    return buf.ToString();
}

static bool HasTypeRegistrationAttribute(TypeDefinition type)
{
    if (!type.HasCustomAttributes)
        return false;
    return AnyCustomAttributes(type, "Android.Runtime.RegisterAttribute") ||
        AnyCustomAttributes(type, "Java.Interop.JniTypeSignatureAttribute");
}

static bool AnyCustomAttributes(ICustomAttributeProvider item, string attribute_fullname)
{
    foreach (CustomAttribute custom_attribute in item.CustomAttributes)
    {
        if (custom_attribute.Constructor.DeclaringType.FullName == attribute_fullname)
            return true;
    }
    return false;
}