# solstice-jmap

把 [trumank/jmap](https://github.com/trumank/jmap) 的活对象转储（`.jmap`）与 `AssetRegistry.bin`
合成两份可直接查询的表。

产出用于「按函数签名生成蓝图字节码」：知道函数有哪些参数、参数方向（in / out / return）、
以及是不是 `FUNC_Static`，才能在节点编辑器里正确构造插销和选指令。

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
| `objects.json` | 对象表。jmap 提供 `/Script`，AssetRegistry 提供 `/Game` |
| `objects.kards.json` | 只保留 Kards 相关包，用于缓存 |
| `functions.json` | 以对象完整路径为 key，函数嵌在对象里，含 `flags` 与 `params` |
| `functions.kards.json` | 同上，只保留 Kards 包 |

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
        { "name": "Object",      "type": "ObjectProperty", "dir": "in",     "class": "/Script/CoreUObject.Object", "enum": "", "offset": 0, "size": 8 },
        { "name": "ReturnValue", "type": "BoolProperty",   "dir": "return", "class": "", "enum": "", "offset": 8, "size": 1 }
      ]
    }
  }
}
```

`dir` 取值 `in` / `out` / `return`；`flags` 里的 `FUNC_Static` 决定调用形态
（静态 → `EX_FinalFunction`，实例 → 需要 `EX_Context`）。

## 构建

```bash
git clone --recursive https://github.com/CCB-TEAM/solstice-jmap.git
dotnet build src/JmapExtractor
```

依赖 [CCB-TEAM/solstice-assetregistry](https://github.com/CCB-TEAM/solstice-assetregistry)
（作为子模块引入，它自己又带两个子模块，所以必须 `--recursive`）。

## 注意

- 产出的 JSON 只含**元数据**（路径、类型、偏移），不含任何游戏资源内容
- `functions.kards.json` 是过滤版，体积约为完整版的 1/10
- jmap 需要注入运行中的游戏进程才能导出，本仓库不涉及那部分

## 许可

MIT
