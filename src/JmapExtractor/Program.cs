using System.Text.Json;
using UAssetRegistry;

namespace Solstice.Tools.JmapExtractor;

/// <summary>
/// 把 trumank/jmap 的活对象转储与 AssetRegistry.bin 合成两份表。
///
/// <para>用法：<c>JmapExtractor &lt;jmap.json&gt; &lt;outDir&gt; [AssetRegistry.bin]</c></para>
///
/// <para>产出：</para>
/// <list type="bullet">
///   <item><c>objects.json</c> —— 对象表。jmap 提供 /Script，AssetRegistry 提供 /Game；</item>
///   <item><c>objects.kards.json</c> —— 只保留 4 个 Kards 包，用于缓存；</item>
///   <item><c>functions.json</c> —— 以对象完整路径为 key，函数嵌在对象里；</item>
///   <item><c>functions.kards.json</c> —— 同上，只保留 Kards 包。</item>
/// </list>
/// </summary>
public static class Program
{
    private static readonly string[] KardsPackages =
    [
        "/Script/kards",
        "/Script/KardsCore",
        "/Script/KardsSubsystems",
        "/Script/KardsWidgets"
    ];

    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: JmapExtractor <jmap.json> <outDir> [AssetRegistry.bin]");
            return 2;
        }

        var inputPath = args[0];
        var outDir = args[1];
        var registryPath = args.Length > 2 ? args[2] : null;

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"找不到输入文件: {inputPath}");
            return 2;
        }

        if (registryPath is not null && !File.Exists(registryPath))
        {
            Console.Error.WriteLine($"找不到 AssetRegistry.bin: {registryPath}");
            return 2;
        }

        Directory.CreateDirectory(outDir);
        Console.WriteLine($"[读入] {inputPath} ({new FileInfo(inputPath).Length / 1024.0 / 1024.0:F1} MB)");

        using var doc = JsonDocument.Parse(
            File.ReadAllBytes(inputPath),
            new JsonDocumentOptions { MaxDepth = 128 });

        var root = doc.RootElement;
        var metadata = root.TryGetProperty("metadata", out var md) ? md : default;
        var objectsElement = root.GetProperty("objects");

        var objects = new Dictionary<string, ObjInfo>(StringComparer.Ordinal);
        var functions = new Dictionary<string, List<FuncInfo>>(StringComparer.Ordinal);

        foreach (var prop in objectsElement.EnumerateObject())
        {
            var path = prop.Name;
            var o = prop.Value;

            var type = GetString(o, "type");
            objects[path] = new ObjInfo(type, GetString(o, "outer"), GetString(o, "class"), GetString(o, "super"), "jmap");

            if (type == "Function" && objects[path].Outer is { } owner)
            {
                var shortName = path.Contains(':') ? path[(path.LastIndexOf(':') + 1)..] : path;
                if (!functions.TryGetValue(owner, out var list))
                {
                    list = [];
                    functions[owner] = list;
                }

                list.Add(new FuncInfo(shortName, GetString(o, "function_flags"), ReadParams(o)));
            }
        }

        var jmapCount = objects.Count;

        // ---- 并入 AssetRegistry.bin 的 /Game 对象 ----
        var gameCount = 0;
        var packageCount = 0;

        if (registryPath is not null)
        {
            Console.WriteLine($"[读入] {registryPath}");

            var state = AssetRegistryLoader.LoadFromFile(registryPath);
            var packages = new HashSet<string>(StringComparer.Ordinal);

            foreach (var a in state.PreallocatedAssetDataBuffers)
            {
                var objectPath = a.ObjectPath;
                if (objects.ContainsKey(objectPath))
                {
                    continue;
                }

                var packagePath = a.PackagePath.ToString();
                var classPath = $"{a.ClassPackage}.{a.AssetClass}";

                objects[objectPath] = new ObjInfo("Asset", packagePath, classPath, null, "registry");
                gameCount++;

                // 合成包节点，让 outer 链可解析
                var p = packagePath;
                while (!string.IsNullOrEmpty(p) && packages.Add(p))
                {
                    p = ParentPath(p);
                }
            }

            foreach (var pkg in packages)
            {
                if (objects.ContainsKey(pkg))
                {
                    continue;
                }

                objects[pkg] = new ObjInfo(
                    "Package",
                    ParentPath(pkg),
                    "/Script/CoreUObject.Package",
                    null,
                    "registry");
                packageCount++;
            }
        }

        var generatedAt = DateTimeOffset.UtcNow.ToString("O");
        var counts = new
        {
            objects = objects.Count,
            fromJmap = jmapCount,
            fromRegistry = gameCount + packageCount,
            gameAssets = gameCount,
            gamePackages = packageCount,
            functionOwners = functions.Count,
            functions = functions.Sum(kv => kv.Value.Count)
        };

        WriteObjects(Path.Combine(outDir, "objects.json"), objects, metadata, generatedAt, counts, null);
        WriteObjects(Path.Combine(outDir, "objects.kards.json"), objects, metadata, generatedAt, counts, IsKards);
        WriteFunctions(Path.Combine(outDir, "functions.json"), objects, functions, metadata, generatedAt, counts, null);
        WriteFunctions(Path.Combine(outDir, "functions.kards.json"), objects, functions, metadata, generatedAt, counts, IsKards);

        Console.WriteLine($"[完成] 对象 {counts.objects:N0}（jmap {jmapCount:N0} + /Game 资产 {gameCount:N0} + 合成包 {packageCount:N0}）");
        Console.WriteLine($"[完成] 函数宿主 {counts.functionOwners:N0}，函数 {counts.functions:N0}");
        Console.WriteLine($"[输出] {outDir}");

        foreach (var f in new[] { "objects.json", "objects.kards.json", "functions.json", "functions.kards.json" })
        {
            var fi = new FileInfo(Path.Combine(outDir, f));
            Console.WriteLine($"   {f,-22} {fi.Length / 1024.0 / 1024.0:F2} MB");
        }

        return 0;
    }

    /// <summary><c>/Game/A/B</c> 的父路径是 <c>/Game/A</c>；<c>/Game</c> 的父路径是空。</summary>
    private static string? ParentPath(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx <= 0 ? null : path[..idx];
    }

    private static bool IsKards(string path)
    {
        foreach (var pkg in KardsPackages)
        {
            if (path.Equals(pkg, StringComparison.Ordinal) ||
                path.StartsWith(pkg + ".", StringComparison.Ordinal) ||
                path.StartsWith(pkg + ":", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetString(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<ParamInfo> ReadParams(JsonElement o)
    {
        var list = new List<ParamInfo>();

        if (!o.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var p in props.EnumerateArray())
        {
            var flags = GetString(p, "flags") ?? string.Empty;

            var dir = flags.Contains("CPF_ReturnParm", StringComparison.Ordinal) ? "return"
                : flags.Contains("CPF_OutParm", StringComparison.Ordinal) ? "out"
                : "in";

            list.Add(ReadParam(p, dir));
        }

        return list;
    }

    /// <summary>
    /// 读一条属性（参数）的完整类型。
    ///
    /// <para>
    /// 结构体属性没有 <c>property_class</c>，结构路径在 <c>struct</c> 字段里；
    /// 容器属性把元素类型放在 <c>inner</c> / <c>key_prop</c> / <c>value_prop</c> 里，所以这里递归。
    /// 少了这些，下游就只知道「是个结构体 / 是个数组」，不知道具体类型。
    /// </para>
    /// </summary>
    private static ParamInfo ReadParam(JsonElement p, string? dir = null)
        => new(
            Name: GetString(p, "name") ?? string.Empty,
            Type: GetString(p, "type") ?? string.Empty,
            Dir: dir ?? "in",
            Cls: GetString(p, "property_class") ?? GetString(p, "struct"),
            Enum: GetString(p, "enum"),
            Size: p.TryGetProperty("size", out var s) && s.TryGetInt32(out var sv) ? sv : 0,
            Offset: p.TryGetProperty("offset", out var off) && off.TryGetInt32(out var ov) ? ov : 0,
            Inner: ReadNested(p, "inner"),
            Key: ReadNested(p, "key_prop"),
            Value: ReadNested(p, "value_prop"));

    private static ParamInfo? ReadNested(JsonElement p, string name)
        => p.TryGetProperty(name, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? ReadParam(nested)
            : null;

    /// <summary>
    /// 写一条参数。容器把元素类型嵌在 <c>inner</c> / <c>key</c> / <c>value</c> 里，
    /// 老字段（name / type / dir / class / enum / offset / size）保持不变，只多这几个。
    /// </summary>
    private static void WriteParam(Utf8JsonWriter w, ParamInfo p, bool includeDir)
    {
        w.WriteStartObject();
        w.WriteString("name", p.Name);
        w.WriteString("type", p.Type);

        if (includeDir)
        {
            w.WriteString("dir", p.Dir);
        }

        WriteNullable(w, "class", p.Cls);
        WriteNullable(w, "enum", p.Enum);
        w.WriteNumber("offset", p.Offset);
        w.WriteNumber("size", p.Size);

        if (p.Inner is not null)
        {
            w.WritePropertyName("inner");
            WriteParam(w, p.Inner, includeDir: false);
        }

        if (p.Key is not null)
        {
            w.WritePropertyName("key");
            WriteParam(w, p.Key, includeDir: false);
        }

        if (p.Value is not null)
        {
            w.WritePropertyName("value");
            WriteParam(w, p.Value, includeDir: false);
        }

        w.WriteEndObject();
    }

    private static void WriteObjects(
        string path,
        Dictionary<string, ObjInfo> objects,
        JsonElement metadata,
        string generatedAt,
        object counts,
        Func<string, bool>? filter)
    {
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream);

        w.WriteStartObject();
        WriteMetadata(w, metadata, generatedAt, counts);

        w.WriteStartObject("objects");
        foreach (var (key, value) in objects)
        {
            if (filter is not null && !filter(key))
            {
                continue;
            }

            w.WriteStartObject(key);
            w.WriteString("type", value.Type);
            WriteNullable(w, "outer", value.Outer);
            WriteNullable(w, "class", value.Class);
            WriteNullable(w, "super", value.Super);
            w.WriteString("source", value.Source);
            w.WriteEndObject();
        }

        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteFunctions(
        string path,
        Dictionary<string, ObjInfo> objects,
        Dictionary<string, List<FuncInfo>> functions,
        JsonElement metadata,
        string generatedAt,
        object counts,
        Func<string, bool>? filter)
    {
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream);

        w.WriteStartObject();
        WriteMetadata(w, metadata, generatedAt, counts);

        w.WriteStartObject("objects");
        foreach (var (owner, funcs) in functions)
        {
            if (filter is not null && !filter(owner))
            {
                continue;
            }

            objects.TryGetValue(owner, out var info);

            w.WriteStartObject(owner);
            w.WriteString("type", info?.Type ?? "Object");
            WriteNullable(w, "super", info?.Super);
            w.WriteString("source", info?.Source ?? "jmap");

            w.WriteStartObject("functions");
            foreach (var f in funcs)
            {
                w.WriteStartObject(f.Name);
                WriteNullable(w, "flags", f.Flags);

                w.WriteStartArray("params");
                foreach (var p in f.Params)
                {
                    WriteParam(w, p, includeDir: true);
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndObject();
            w.WriteEndObject();
        }

        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteMetadata(Utf8JsonWriter w, JsonElement metadata, string generatedAt, object counts)
    {
        w.WriteStartObject("metadata");
        w.WriteString("extractor", "Solstice.Tools.JmapExtractor");
        w.WriteString("generated_at", generatedAt);

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            if (metadata.TryGetProperty("source", out var src))
            {
                w.WriteString("source", src.GetString());
            }

            if (metadata.TryGetProperty("build_change_list", out var bcl))
            {
                w.WriteString("build", bcl.GetString());
            }

            if (metadata.TryGetProperty("engine_version", out var ev) && ev.ValueKind == JsonValueKind.Object)
            {
                var major = ev.TryGetProperty("major", out var mj) ? mj.GetInt32() : 0;
                var minor = ev.TryGetProperty("minor", out var mn) ? mn.GetInt32() : 0;
                w.WriteString("engine", $"{major}.{minor}");
            }
        }

        w.WritePropertyName("counts");
        JsonSerializer.Serialize(w, counts);
        w.WriteEndObject();
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, value);
        }
    }

    private sealed record ObjInfo(string? Type, string? Outer, string? Class, string? Super, string Source);

    private sealed record FuncInfo(string Name, string? Flags, List<ParamInfo> Params);

    private sealed record ParamInfo(
        string Name,
        string Type,
        string Dir,
        string? Cls,
        string? Enum,
        int Size,
        int Offset,
        ParamInfo? Inner = null,
        ParamInfo? Key = null,
        ParamInfo? Value = null);
}
