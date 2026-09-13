# OverlayPluginAddon

一款 [OverlayPlugin](https://github.com/OverlayPlugin/OverlayPlugin) Addon 插件，在本機即時計算每個玩家的 **GCD 運轉率**，並把結果注入 ACT 的 CombatData，讓 mopimopi 等既有懸浮窗直接多出欄位。

這是 [RdpsOverlay](https://github.com/Unnbird/RdpsOverlay) 的 GCD 部分獨立出來的版本：**只有 GCD，沒有 rDPS／buff 拆帳／FFLogs 解析器**。`GcdTracker`、`ActionCategories`、`ActionData` 與 `data/actions.json` 逐字沿用，事件來源與診斷只保留餵 GCD 所需的部分。

不需要帳號、不需要上傳器、不需要網路。

## 運作原理

```
ACT log 讀取迴圈
  └─ BeforeLogLineRead ──→ StatusTracker   誰身上掛著什麼狀態（只為加速狀態與玩家判定；03 / 26 / 30 / 25 / 04 / 01 / 02 行）
                        ──→ GcdTracker      20 行（詠唱開始）與 21/22 行（技能落地）記一次 GCD；23 行撤銷
                                  ↓
                    CombatantData.ExportVariables（gcdUptime / gcdCount / gcdClip / gcdOccupied / gcdRecast）
                                  ↓
                 OverlayPlugin 的 CombatData 事件
                                  ↓
                    mopimopi / 任何懸浮窗
```

hook 掛在 ACT 的 log 讀取迴圈上（而不是 `FFXIVRepository.RegisterLogLineHandler`），因為那是同一條執行緒、嚴格照 log 順序，能保證「按鍵入帳時，它之前的加速狀態行都已經套用完畢」。

## 匯出的欄位

| key | 說明 |
|---|---|
| `gcdUptime` | GCD 運轉率（%）：第一次按鍵到最新一次按鍵之間，GCD 在轉的比例。**已經除好的值，懸浮窗直接顯示、不要自己再除**：它在每次打出 GCD 的當下量一次 |
| `gcdCount` | GCD 次數（只計戰技與魔法） |
| `gcdClip` | GCD 之間空窗損失的總秒數 |
| `gcdOccupied` | 已結束的 GCD 佔用的總秒數（`gcdUptime` 的分子）。分母是該玩家**自己的按鍵區間**，不是戰鬥時長；拿它去除 `DURATION` 會得到另一個數字 |
| `gcdRecast` | 該玩家 2.5 秒基準的 GCD，由推估出的速度屬性算出 |

除錯用的自訂事件 `onGcdUpdate` 每秒推一次，內含每人的統計與速度屬性；用 `getGcdData` 也可以主動拉。

同一個 ACT 裡若同時載入 RdpsOverlay，兩邊註冊的是同一組 key，先註冊的那一方負責，後者靜默跳過。

## GCD 運轉率

演算法沿用 [xivanalysis](https://github.com/xivanalysis/xivanalysis) 的模型（`SpeedStatsAdapterStep` + `AlwaysBeCasting` + `speedStatMapper`）。

```
單次施放佔用 = max(recast, castTime + (castTime ≥ GCD ? 100ms 詠唱稅 : 0))
運轉率       = 1 − Σ 空窗 / (最新一次按鍵 − 第一次按鍵)
空窗         = Σ max(0, 間隔 − 佔用)      （超過 slack 才算）

以上整組在「打出 GCD 的當下」量一次，之後定住；分母是自己的按鍵區間，不是戰鬥時長
```

**每次施放記它完整的 GCD 佔用，不看到下一次的間隔**：log 時間戳有 ~45ms 的批次抖動，一個乾淨的 2.5 秒輪替會量出 2.46～2.54 的間隔；如果拿 `min(recast, 間隔)`，每個「看起來早了 40ms」的按鍵都會被削掉，永遠到不了 100%。

**進行中的那個 GCD 兩邊都不算**：它的 recast 還在轉、後面的間隔還沒關上，所以它在 `gcdCount` 裡，但不在分子也不在分母。

### 一次 GCD 只量一次

運轉率是在打出 GCD 的那一刻量的，量完就定住，直到下一次 GCD。分子分母都只看這個玩家自己的按鍵。

分母不用戰鬥時長的原因在時鐘：ACT 的 `EncounterData.Duration` 是傷害驅動的，有傷害落地才往前走；而 GCD 是按下去就記（20 行），硬詠唱的傷害要一整段詠唱之後才落地。用戰鬥時長當分母，瞬發之後接任何硬詠唱都會讓運轉率鋸齒一次。改成「第一次按鍵到最新一次按鍵」之後，分子分母只會在同一個時刻一起動。

代價：戰鬥開始到第一次按鍵之間不算（晚開的人不會被扣分）；最後一次按鍵之後的閒置要等下一次按鍵才會被算進去，中途倒地的人運轉率停在倒下那一刻。

**空窗（lost）** 對應 xivanalysis 的 downtime windows：`間隔 > recast + 150ms` 才算，150ms 是 100ms 詠唱稅加 50ms 抖動（`GCD_ERROR_OFFSET`）；硬詠唱後再多給 500ms 的滑步窗（`SLIDECAST_OFFSET`）。

**recast 解析照 `CastTime.getAdjustedTime` 逐步走**：基礎 recast 已在 1500ms 地板的完全不動；否則套速度屬性 → 套加速 → 向下取到 10ms → 夾回地板。

### 怎麼分辨 GCD 和 oGCD

`FFXIV_ACT_Plugin.Resource` 內嵌的 `ActionCategoryList` 就是 `技能id | 分類`：分類 2（魔法）與 3（戰技）是 GCD，1（自動攻擊）與 4（能力技）不是。

這份表**不打包進發布檔**，而是在執行期從解析器的組件直接讀：33000 多筆、每個改版都會變，跟著使用者實際在跑的版本走才不會過期。

⚠️ 這個組件是 Costura 內嵌的，而且延遲載入。必須用 `Assembly.Load(name)` 主動要，才會觸發 Costura 把它交出來；載入失敗由每秒的 timer 重試，直到解析器準備好為止。讀不到時 GCD 欄位顯示 0 並在 log 留警告（`GCD uptime unavailable for now: ...`）。

計數走 21/22 行而不是傷害事件，這樣治療的 GCD 也算得到。AoE 一次打八個目標會有八行，用「同一施放者 + 同一技能 id + 同一時間戳」去重。

### 每個技能的 recast 不一樣

舞步 1 秒、忍術 1.5 秒、六合星 5 秒、彩虹點滴 6 秒。xivanalysis 的解法：

1. **每個技能的基礎 recast 是已知資料**（`data/actions.json`，由 [tools/Build-ActionData.js](tools/Build-ActionData.js) 從 xivanalysis 抽出）
2. 觀測到的間隔可以**正規化**：除掉開啟該間隔那個技能的基礎 recast 與當下的加速倍率，整場所有間隔就塌縮到同一個分布
3. 這個分布的眾數就是該玩家 2.5 秒基準的 GCD，反解成單一個技速／詠速屬性值
4. 那一個屬性值再回推他按的每一個技能的真實 recast

**GCD 是按下去就開始轉。** 硬詠唱在 20 行（StartsCasting）收到時就記錄，傷害落地時帶著同一個時間戳會被去重擋掉；23 行（詠唱中斷）把那筆暫記移除。

**詠唱時間只在真的詠唱了才算。** 用 20 行與 21 行配對判斷這一按到底有沒有走詠唱條（連續魔法、即刻詠唱、觸發技都會讓資料裡的 castTime 對這一按失效）。20 行帶的詠唱條長度（已含速度、加速與職業機制）優先於資料表的值。

沒有 `speedAttribute` 的技能（舞步這種固定秒數的）不參與第 2 步，但在第 4 步仍然照它的固定 recast 計入佔用。

速度公式（`attributeMultiplier = 1000 − floor(130 × (stat − 420) / 2780)`）與其反函數都在 [ActionData.cs](OverlayPluginAddon/ActionData.cs)，出處是 Allagan Studies，經 xivanalysis 的 `speedStatMapper` 轉錄。

### 資料來源

```powershell
node tools/Build-ActionData.js [xivanalysis 路徑]
```

抽出 recast 覆蓋（500ms 到 6000ms）與加速狀態（神速咏唱 0.80、內丹 0.85、風雅 0.87、疾走 0.85、靈感 0.75，靈感只作用於指定技能）。超過 10 秒的 recast 一律當成預設值。

### 還沒做到的

- **分母沒有扣掉 downtime。** xivanalysis 的分母是「戰鬥時長 − boss 無敵時間」，這裡沒有 targetability 追蹤，有 phase 轉換的戰鬥每個人的運轉率都會偏低。
- **MNK / NIN 的職業基礎加成沒有單獨套用**（資料檔裡有，但沒接上）。
- **第一次按鍵之前、最後一次按鍵之後都不算**（見上）。

## 安裝

1. 解壓 `OverlayPluginAddon-X.Y.Z.zip` 到任意資料夾
2. ACT → Plugins → Plugin Listing → Browse → 選 `OverlayPluginAddon.dll` → Add/Enable
3. **載入順序必須是**：`FFXIV_ACT_Plugin.dll` → `OverlayPlugin.dll` → `OverlayPluginAddon.dll`
4. `data/actions.json` 必須跟 dll 放在一起

mopimopi 端（[Unnbird/mopimopi](https://github.com/Unnbird/mopimopi)）已經有 `GCD%` / `GCDs` / `Lost` 欄位，在設定畫面勾起來即可。外掛啟動時會註冊 preset **MopiMopiCustom**，在 OverlayPlugin 的「新增懸浮窗」直接選就好。

### 自動更新

每次 ACT 啟動時查一次 `Unnbird/OverlayPluginAddon` 的最新 release tag，比目前版本新才會詢問；按下同意才會下載 `OverlayPluginAddon-<version>.zip`，覆蓋 dll 與 `data/` 之後重啟 ACT。

## 驗證流程

### 0. GCD 數學（不用進遊戲）

```powershell
.\build.ps1 -SkipDeps
.\tools\Test-Gcd.ps1
```

RdpsOverlay `Test-Ledger.ps1` 的 GCD 半部逐字搬過來：乾淨輪轉、技速推估、舞步與忍術的固定 recast、加速窗口（含靈感只作用於指定技能）、長詠唱與詠唱稅、真實空窗、AoE 去重、瞬發 vs 硬詠唱、連續魔法的交替輪替、硬詠唱開始不會讓運轉率飆高、一次 GCD 只量一次、時間戳抖動、150ms 損失 slack、運轉率不超過 100%、樣本不足退回預設。

### 0c. GCD 分類表（不用進遊戲）

```powershell
.\tools\Test-ActionCategories.ps1
```

驗證組件在與不在兩條路徑：不在時要明確報錯而不是給一張空表，在時要解析出 33000 多筆並正確分類。

### 1. 全部是 0？先跑診斷

```js
callOverlayHandler({ call: 'dumpGcdDiagnostics' })
```

在 dll 旁邊產生 `OverlayPluginAddon.diagnostics.txt`（每場戰鬥結束也會自動寫一份）：

| 症狀 | 意義 |
|---|---|
| `event source started : False` | addon 沒載入 / 載入順序錯 |
| `export variables added : False` | `Init()` 沒被呼叫 |
| `total seen : 0` | `BeforeLogLineRead` 沒掛上 |
| `type 21 / 20` 是 0 | 該類型的 log line 沒出現 |
| `players identified : 0` | actor id 的欄位位置錯了 |
| `action categories : ...could not be loaded` | 解析器資源還沒解壓，會自動重試；持續失敗代表載入順序不對 |
| `recorded as GCD : 0` 但 `type 21` 有數字 | 21 行的欄位位置錯了，或分類表沒載到 |

檔案最後會附上幾條原始 log line 並逐欄編號，直接跟 [StatusTracker.cs](OverlayPluginAddon/StatusTracker.cs) 和 [GcdEventSource.cs](OverlayPluginAddon/GcdEventSource.cs) 最上面的欄位常數對照即可。

### 2. 逐 GCD 對帳

每場戰鬥結束會在 dll 旁邊的 `OverlayPluginAddon.status.log` 追加一段：每人一行統計，底下每個 GCD 一行（時間、技能 id、硬詠唱與否、記入的 recast、到下一次的間隔、佔用、損失）。「GCD 邏輯錯了」的爭論在這裡收尾。

## 建置

```powershell
.\build.ps1
```

輸出在 `..\out\OverlayPluginAddon-<version>.zip`；中間產物在 `..\out\Release\OverlayPluginAddon\`，與 RdpsOverlay 的 `..\out\Release\addons\` 分開。

專案假設的 workspace 佈局（與 RdpsOverlay 相同）：

```
<workspace>/OverlayPlugin       <- OverlayPlugin 原始碼，build.ps1 會自動 clone
<workspace>/OverlayPluginAddon  <- 本專案
<workspace>/out                 <- 建置輸出
```

`build.ps1` 會在跑 `fetch_deps.ps1` 之前把 OverlayPlugin `DEPS.json` 裡失效的 `FFXIV_ACT_Plugin_SDK_2.0.6.1.zip` 改成 `3.0.2.8`（詳見腳本內註解），所以直接 `.\build.ps1` 就好。
