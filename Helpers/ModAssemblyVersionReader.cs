using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace GHPC_Mod_Manager.Helpers;

/// <summary>
/// 从Mod DLL读取版本号（只读元数据，不加载程序集）
/// </summary>
public static class ModAssemblyVersionReader
{
    /// <summary>
    /// 读取Mod版本：优先MelonInfo特性，其次AssemblyInformationalVersion，最后程序集版本
    /// </summary>
    /// <param name="dllPath">DLL完整路径</param>
    /// <returns>版本字符串，读不到时返回null</returns>
    public static string? TryReadVersion(string dllPath)
    {
        try
        {
            using var stream = File.OpenRead(dllPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
                return null;

            var reader = peReader.GetMetadataReader();
            if (!reader.IsAssembly)
                return null;

            var assembly = reader.GetAssemblyDefinition();
            string? informationalVersion = null;

            foreach (var handle in assembly.GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(handle);
                var attributeName = GetAttributeTypeName(reader, attribute);

                if (attributeName == "MelonInfoAttribute")
                {
                    var melonVersion = ReadMelonInfoVersion(attribute);
                    if (!string.IsNullOrWhiteSpace(melonVersion))
                        return CleanVersion(melonVersion);
                }
                else if (attributeName == "AssemblyInformationalVersionAttribute")
                {
                    var value = attribute.DecodeValue(new SimpleAttributeTypeProvider());
                    if (value.FixedArguments.Length > 0 && value.FixedArguments[0].Value is string s)
                        informationalVersion = s;
                }
            }

            if (!string.IsNullOrWhiteSpace(informationalVersion))
                return CleanVersion(informationalVersion);

            var assemblyVersion = assembly.Version;
            if (assemblyVersion == null || assemblyVersion == new Version(0, 0, 0, 0))
                return null;

            return CleanVersion(assemblyVersion.ToString());
        }
        catch
        {
            // 不是托管DLL或文件被占用，按读不到处理
            return null;
        }
    }

    // MelonInfo构造参数有两种：
    // (Type, name, "1.2.3", author, link)
    // (Type, name, major, minor, revision, [identifier], author, link)
    private static string? ReadMelonInfoVersion(CustomAttribute attribute)
    {
        var value = attribute.DecodeValue(new SimpleAttributeTypeProvider());
        var args = value.FixedArguments;
        if (args.Length < 3)
            return null;

        if (args[2].Value is string versionText)
            return versionText;

        if (args.Length >= 5 && args[2].Value is int major && args[3].Value is int minor && args[4].Value is int revision)
        {
            var version = $"{major}.{minor}.{revision}";
            if (args.Length >= 7 && args[5].Value is string identifier && !string.IsNullOrWhiteSpace(identifier))
                version += $"-{identifier}";
            return version;
        }

        return null;
    }

    private static string? GetAttributeTypeName(MetadataReader reader, CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var memberRef = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (memberRef.Parent.Kind == HandleKind.TypeReference)
                    return reader.GetString(reader.GetTypeReference((TypeReferenceHandle)memberRef.Parent).Name);
                return null;
            case HandleKind.MethodDefinition:
                var methodDef = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                return reader.GetString(reader.GetTypeDefinition(methodDef.GetDeclaringType()).Name);
            default:
                return null;
        }
    }

    // "1.2.3+abc123" -> "1.2.3"，"1.2.3.0" -> "1.2.3"
    private static string CleanVersion(string version)
    {
        var cleaned = version.Trim();
        var plusIndex = cleaned.IndexOf('+');
        if (plusIndex > 0)
            cleaned = cleaned.Substring(0, plusIndex);

        var parts = cleaned.Split('.');
        if (parts.Length == 4 && parts[3] == "0" && parts.All(p => int.TryParse(p, out _)))
            cleaned = string.Join('.', parts.Take(3));

        // 版本号会用作备份目录名，去掉非法字符
        foreach (var c in Path.GetInvalidFileNameChars())
            cleaned = cleaned.Replace(c, '_');

        return cleaned;
    }

    // 只需解出字符串和整数参数，类型参数统一按字符串处理
    private sealed class SimpleAttributeTypeProvider : ICustomAttributeTypeProvider<string>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeDefinition(handle).Name);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeReference(handle).Name);
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type == "System.Type" || type == "Type";
    }
}
