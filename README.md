# Invokyr Mods

Invokyr 的三个独立 BepInEx mod，**都只需房主（Host）安装**，服务端生效后自动同步给所有客户端。

| Mod | 说明 | 文档 |
|---|---|---|
| 总是打折 | 商店所有商品打折（原为随机部分打折） | [`mods/总是打折.md`](mods/总是打折.md) |
| 不再混沌 | 多效果混合的功能骰子（混沌）不再出现 | [`mods/不再混沌.md`](mods/不再混沌.md) |
| 可控祝福 | 棋盘点数骰的祝福面 / 诅咒面可开关 | [`mods/可控祝福.md`](mods/可控祝福.md) |

## 安装（三个一起）

确认无 Invokyr 进程在运行后，把三个 dll 各放入一个中文文件夹（**已替你放好**）：

```
Steam\steamapps\common\Invokyr\BepInEx\plugins\
├── 总是打折\InvokyrAllDiscount.dll
├── 不再混沌\InvokyrNoChaosDice.dll
└── 可控祝福\InvokyrBlessControl.dll
```

每个文件夹里还附有一份同名 `.md` 说明。

## 本机环境

- 游戏：`C:\Program Files (x86)\Steam\steamapps\common\Invokyr`（Unity 6000.3.14f1, IL2CPP, Mirror 联机）
- Mod 加载器：BepInEx `6.0.0-be.790`（Unity 6 兼容版）
- 日志：`BepInEx\LogOutput.log`
- 源码：`plugin/InvokyrAllDiscount/`、`plugin/InvokyrNoChaosDice/`、`plugin/InvokyrBlessControl/`
- 逆向工具/中间产物：`reverse/`、`reverse-out/`

## 文件清单

```
invokyr-mod/
├── README.md
├── mods/总是打折.md        # 各 mod 独立说明（同文件也已放入对应 plugins 文件夹）
├── mods/不再混沌.md
├── mods/可控祝福.md
├── plugin/InvokyrAllDiscount/
├── plugin/InvokyrNoChaosDice/
├── plugin/InvokyrBlessControl/
└── reverse/  reverse-out/
```
