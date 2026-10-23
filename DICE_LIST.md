# Invokyr —— 骰子清单与「禁用混沌骰子」Mod 说明（v1.2.0）

## 一、骰子名单（2026-10-23 完整日志 dump，按运行时物品名）

骰子分为三套完全独立的系统。本 Mod 只处理**功能骰子**，棋盘点数/诅咒/祝福骰子一律不动。

### 1. 混沌 / 多效果组合骰子（默认封禁 → 从发放池移除）
| 运行时名字 | 效果构成 | 判定方式 |
|---|---|---|
| `Data-Dice_Chaos_1` | 混沌（多面混合） | 名字含 `chaos` |
| `Data-Dice_Chaos_2` | 混沌（多面混合） | 名字含 `chaos` |
| `Data-Dice_TeleRez` | 传送 + 复活 | 组合检测：tele+rez |
| `Data-Dice_BoomHeal` | 爆炸 + 治疗 | 组合检测：boom+heal |
| `Data-Dice_CocoonFire` | 茧 + 火焰 | 组合检测：cocoon+fire |

> 这类骰子混合了多种效果，很难滚出自己想要的那一面，就是你最初说的「混沌骰子」。

### 2. 单效果功能骰子（保留）
| 运行时名字 | 说明 |
|---|---|
| `Data-Dice_Heal` | 治疗 |
| `Data-Dice_Boom` | 爆炸 |
| `Data-Dice_Fire` | 火焰 |
| `Data-Dice_Freeze` | 冻结 |
| `Data-Dice_Lightning` | 闪电 |
| `Data-Dice_Teleport` | 传送 |
| `Data-Dice_Revive` | 复活 |
| `Data-Dice_Cocoon` | 茧 |
| `Data-Dice_Gather` | 收集 |
| `Data-Dice_Scanner` | 扫描 |
| `Data-Dice_Light` | 光 |
| `Data-Dice_Pelote` | 抛掷 |
| `Data-Dice_Fireworks` | 烟花 |
| `Data-Dice_AntiGravity` | 反重力 |

> ⚠️ 每个功能骰子本身都可能带一两个 debuff 面，所以**判断混沌绝不数面**，只按骰子身份（名字/别名）判。

### 3. 棋盘点数 / 诅咒 / 祝福骰子（另一套系统，保留不动）
`Dice_Normal_16/_46`、`Dice_Blessed_6/_46`、`Dice_Cursed_1/_13`、
`Dice_Blursed_16/_36`、`NormalDice_13/_16` —— 棋盘上只显示点数 / 给诅咒 / 给祝福（全员加血）。

### 4. 非骰子工具（不属于封禁对象）
`Data-PlayCube`、`Data-IceCube`。

## 二、混沌判定规则（v1.2.0）

按身份判断，顺序如下，任一命中即封禁：
1. `Allow` 白名单命中 → 放行（最高优先）。
2. 名字/别名含 `chaos` → 封禁（覆盖 `Chaos_1/2`）。
3. `ExtraBlock` 配置子串命中 → 封禁（可扩展）。
4. **组合检测**（硬编码，不依赖配置）：把名字按 camelCase 拆词，数出命中的不同「效果词」数量，
   ≥2 即为混沌。效果词表：`heal / boom / fire / cocoon / teleport / revive / lightning / light /
   freeze / gather / scan / pelote / fireworks / antigravity / tele / rez / explosion`。
   整词匹配（`Light` ≠ `Lightning`），不会误伤单效果骰。
   - `TeleRez → [Tele, Rez] = 2 → 封禁`
   - `BoomHeal → [Boom, Heal] = 2 → 封禁`
   - `CocoonFire → [Cocoon, Fire] = 2 → 封禁`
   - `Boom → [Boom] = 1 → 保留`，`Teleport → [Teleport] = 1 → 保留`
5. `BlockMultiEffectDice`：名字在 `Dice_` 后带 ≥2 个数字段（如 `Dice_1_2`）→ 封禁。

> 组合检测是硬编码在 Mod 里的，**不依赖 cfg**，所以即使配置文件被清空，`TeleRez/BoomHeal/CocoonFire` 也一定被封。

## 三、实现方式（数据源移除）

目标：**新出现的骰子不再有混沌类；存档里已有的混沌骰子无所谓，保留不动**。

- 在**房主打开棋盘时**（`DiceBox.OnStartServer`，早于任何骰子发放）和**每次骰子实体生成时**
  （`DiceTool.OnStartServer`）清一次发放池。
- 清理的池子：
  1. `ItemManager.AvailableTools`（所有可随机发放的工具，含骰子）—— 这是 `GetSpawnableRandomItem()`
     随机发放的真正源头。移除混沌骰子后，新发放选不到混沌。
  2. `DiceSet` 骰子池、`ItemSet` 发放列表（若运行时可达）。
- 因为跑在房主/服务端且清的是发放池，**所有客户端同步生效，只有房主需要装 Mod**（与打折 Mod 同思路）。
- **不替换已有骰子实体**：存档/背包里已经存在的混沌骰子原样保留（你已确认「已有的无所谓」）。

## 四、配置文件（`BepInEx\config\dev.doubao.invokyr.nochaosdice.cfg`）
| 配置项 | 默认 | 说明 |
|---|---|---|
| `Enable` | `true` | 总开关 |
| `BlockMultiEffectDice` | `true` | 封禁 `Dice_1_2` 这类多数字段骰子 |
| `ExtraBlock` | `revivetp,teleRez` | 额外封禁的名字/别名子串（可补，如 `boomheal,cooconfire`） |
| `Allow` | 空 | 强制保留的名字/别名 |
| `LogAllDice` | `true` | 打印每个骰子名字/别名/面，确认后建议关掉 |

> 注意：旧版本生成过一份 cfg，里面 `ExtraBlock` 可能是空。若想额外封更多组合骰子，把名字填进
> `ExtraBlock`（如 `boomheal,cocoonfire`）再重启即可；核心三个组合骰子已由硬编码覆盖。

## 五、使用步骤
1. 确认无游戏进程后，把 `InvokyrNoChaosDice.dll` 放进 `Invokyr\BepInEx\plugins\`。
2. 房主启动游戏 → 开一局 → 进棋盘（打开棋盘、进地图），看新发放的骰子是否还会出现混沌。
3. 打开 `Invokyr\BepInEx\LogOutput.log`，找 `[NoChaosDice]` 日志：
   - 应看到 `DiceBox opened (host) -> purging chaos dice from spawn pools.`
   - 以及 `ItemManager pool die='Data-Dice_TeleRez' ... -> BLOCK` 等清理记录。
4. 若需调整，改 `Allow` / `ExtraBlock` 再重启。

## 六、已知边界
- 组队界面/背包里**已经加载出来的混沌骰子**不会被移除（符合「已有无所谓」）。
- 清理发生在棋盘打开和骰子生成时；同一局内若混沌骰子已从某池发放过，理论上一次全量清理后后续发放均正常。

## 七、棋盘点数骰子（祝福面 / 诅咒面）→ 独立 Mod「可控祝福」
棋盘上决定前进点数的骰子（`Dice_Blessed_*` / `Dice_Cursed_*` / `Dice_Blursed_*` 等）的面是
`BoardGame.Elements.SOFaceResultCellEffect`（`digitResult` 点数 + `alterationSubType` 祝福/诅咒）。
这是另一套系统，**不再混沌 mod 不碰**；由独立 mod `InvokyrBlessControl`（可控祝福）在房主端拦截面效果执行：
- `BoardDice:BlessHealAmount`（`dev.doubao.invokyr.blesscontrol.cfg`，默认 `50`）设 `0` → 祝福效果移除（不给全员加血），骰子照常出点数。
- `BoardDice:CurseEnabled`（默认 `false`）→ 诅咒效果移除，骰子照常出点数。
- 纯点数面（`Normal`）不受影响。

> 祝福加血数值（非 0）当前保留游戏原生；真正的「自定义加血量数值」（如改 30）需拦截玩家加血 API，属后续版。
