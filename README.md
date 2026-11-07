# solstice-jmap

把 [trumank/jmap](https://github.com/trumank/jmap) 的活对象转储（`.jmap`）与 `AssetRegistry.bin`
合成两份可直接查询的表。

产出用于「按函数签名生成蓝图字节码」：知道函数有哪些参数、**参数方向**、以及是不是 `FUNC_Static`，
才能在节点编辑器里正确构造插销和选指令。

## 用法

```bash
JmapExtractor <jmap.json> <outDir> [AssetRegistry.bin]
```

| 参数 | 说明 |
|---|---|
| `jmap.json` | jmap 导出的活对象转储（通常几十 MB） |
| `outDir` | 输出目录 |
| `AssetRegistry.bin` | 可选。给了才会并入 `/Game` 资产；不给只处理 `/Script` |

## 产出

| 文件 | 内容 |
|---|---|
| `objects.json` | 对象表。jmap 提供 `/Script`，AssetRegistry 提供 `/Game`、`/Engine` 与插件根 |
| `objects.kards.json` | 只保留 Kards 相关包，用于缓存 |
| `functions.json` | 以**对象完整路径**为 key，函数嵌在对象里，含 `flags` 与 `params` |
| `functions.kards.json` | 同上，只保留 Kards 包（体积约为完整版的 1/10） |

`metadata` 段会记录计数与来源，便于核对：

```json
{
  "metadata": {
    "extractor": "Solstice.Tools.JmapExtractor",
    "source": "kards-Win64-Shipping.exe",
    "build": "++UE5+Release-5.6-Fork-CL-0",
    "engine": "5.6",
    "counts": { "objects": 47311, "fromJmap": 29675, "fromRegistry": 17636 }
  },
  "objects": { ... }
}
```

### 函数条目

```json
"/Script/Engine.KismetSystemLibrary": {
  "functions": {
    "IsValid": {
      "flags": "FUNC_Final | FUNC_Native | FUNC_Static | FUNC_Public | FUNC_BlueprintCallable",
      "params": [
        { "name": "Object",      "type": "ObjectProperty", "dir": "in",     "ref": false, "const": false,
          "class": "/Script/CoreUObject.Object", "enum": "", "offset": 0, "size": 8 },
        { "name": "ReturnValue", "type": "BoolProperty",   "dir": "return", "ref": false, "const": false,
          "class": "", "enum": "", "offset": 8, "size": 1 }
      ]
    }
  }
}
```

**两层 key，别搞混**：

| 层 | key 形态 |
|---|---|
| 对象层（`objects` 的 key） | 对象完整路径，如 `/Script/Engine.KismetSystemLibrary` |
| 函数层（`functions` 的 key） | **短名**，取自对象路径最后一个 `:` 之后 |

**例外**：outer 是 **Package** 的对象，其路径用 `.` 分隔、没有 `:`，短名提取会退化成完整路径。
实测 47311 条对象里有 447 个这样的函数 key，**全部是 `__DelegateSignature`**：

```json
"/Script/AIModule": {
  "type": "Package",
  "functions": {
    "/Script/AIModule.AIMoveCompletedSignature__DelegateSignature": { ... }
  }
}
```

所以按短名索引时**要额外按后缀匹配一遍**，否则会静默漏掉所有 delegate 签名。

### 参数方向：`dir` + `ref` + `const`

每个**顶层参数**除 `dir` 外还带两个布尔位，直接转录 `CPF_ReferenceParm` / `CPF_ConstParm`。
方向由三者唯一确定：

| `dir` | `ref` | `const` | 含义 | C# |
|---|---|---|---|---|
| `return` | — | — | 返回值 | `return` |
| `out` | `false` | — | 真输出，调用方写回 | `out` |
| `out` | `true` | `false` | 可读写引用 | `ref` |
| `out` | `true` | `true` | 按引用只读入参（`UPARAM(ref)` 的 const 引用） | `in` |
| `in` | `false` | — | 值传递 | 值 |
| `in` | `true` | `true` | const 引用入参 | `in` |

**只看 `dir` 会错。** 它是 `CPF_OutParm` 的折叠视图，而 `CPF_OutParm` 也会被 `UPARAM(ref)` 的
按引用入参置上。实测 `Array_Add`：

| 参数 | `dir` | `ref` | `const` | 真实方向 |
|---|---|---|---|---|
| `TargetArray` | `out` | `true` | `true` | 入参 |
| `NewItem` | `out` | `true` | `true` | 入参 |
| `ReturnValue` | `return` | `false` | `false` | 返回值 |

对比 `Array_Get`：`Item` 是 `dir=out, ref=false` —— 这个才是真输出。

**全量分布**（KARDS 5.6，26811 个顶层参数）：

| 方向 | 数量 |
|---|---|
| 值传递 / 只读入参 | 14894 |
| 返回值 | 6674 |
| 按引用只读入参（`in`） | 2766 |
| 真输出（`out`） | 2271 |
| 可读写引用（`ref`） | 206 |

> **一份 jmap 足以判定参数方向**，不需要回游戏或回资产补信息。
> `ref` / `const` 只在顶层参数上写；`inner` / `key` / `value` 这类元素类型没有传参方向。

### `flags` 与调用形态

`FUNC_Static` 只是**必要条件之一**。引擎在 `EX_FinalFunction` / `EX_LocalFinalFunction` /
`EX_CallMath` / `EX_VirtualFunction` / `EX_Context` 之间选哪个，是六个条件的合取，
其中一个（调用目标兼容性）在 jmap 里不存在，**无法完整复现**。
所以 `flags` 不足以决定调用形态，详见 `solstice-kismet` 里 `CallShape` 的注释。

## 已知边界

- **`super` 恒为 null**：jmap 转储本身不提供继承关系（实测 47311/47311 全 null）。
  **继承链要查 usmap**，别指望沿 `super` 走。
- **没有 Property 条目**：`objects` 里只有 `Package` / `Enum` / `Class` / `Function` / `Object` /
  `ScriptStruct` / `Asset` 七种类型。**类属性与结构成员都不在这里** —— 属性查 usmap，
  或读资产自身的 `LoadedProperties`。
- **`StructProperty` 的 `class` 是有值的**（取自转储的 `struct` 字段），形如
  `"class": "/Script/Engine.LatentActionInfo"`，与 `offset` / `size` 一起给出。
  但**结构成员**仍要查 usmap。
- 产出的 JSON 只含**元数据**（路径、类型、偏移），不含任何游戏资源内容
- jmap 需要注入运行中的游戏进程才能导出，本仓库不涉及那部分

## 许可

MIT
