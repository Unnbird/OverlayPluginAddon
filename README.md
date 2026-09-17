# OverlayPluginAddon

一款 [OverlayPlugin](https://github.com/OverlayPlugin/OverlayPlugin) Addon 插件，在本機即時算出每個玩家的 **rDPS 家族**（rDPS / aDPS / nDPS / cDPS）與 **GCD 運轉率**，把結果注入 ACT 的 CombatData，讓 mopimopi 等既有懸浮窗直接多出欄位。

不需要 FFLogs 帳號、不需要上傳器、不需要網路。

**所有數字都在這裡算完。** 懸浮窗只負責顯示 —— 這是刻意的：一份計算、一個來源，開幾個懸浮窗都不會各算各的，重整頁面也不會把一整場資料弄丟。

rDPS 用的是 **FFLogs 自己的解析器**（`data/parser-ff.js`），跑在插件內嵌的 V8 上。不重寫它是因為它是 RPGLogs 的專有程式碼、每次改版都會重發；照抄一份等於每個版本都要重新追。除了它以外，其餘每一段 —— 折疊 actor 表、跟 ACT 的列比對、兩個時鐘、GCD 運轉率 —— 都是 .NET。

## 運作原理

```
ACT log 讀取迴圈 (BeforeLogLineRead, originalLogLine)
        │
        ├─→ StatusTracker  誰身上掛著什麼狀態（加速狀態與玩家判定；03 / 26 / 30 / 25 / 04 / 01 / 02 行）
        ├─→ GcdTracker     20 行（詠唱開始）與 21/22 行（技能落地）記一次 GCD；23 行撤銷
        │
        └─→ 行佇列 ──→ 專屬執行緒 ──→ V8：parser-ff.js
                                         │  prepareToParseLines + parseLine，每 100ms 一批
                                         │  collectMeters()，每 500ms 一次
                                         ▼
                                     ParserOutput      讀出 fight / actors / 治療 / 死亡
                                     DowntimeWindows   三種 zone handler 欄位形狀
                                     FightMatch        fight ↔ ACT encounter、兩個時鐘
                                     MeterSnapshot     折疊比對完的每一列
                                         │
                                         └─→ GcdTracker.SetDowntimeWindows
                                  ↓
        CombatantData / EncounterData.ExportVariables
                                  ↓
                 OverlayPlugin 的 CombatData 事件
                                  ↓
                    mopimopi / 任何懸浮窗
```

log hook 掛在 ACT 的讀取迴圈上（而不是 `FFXIVRepository.RegisterLogLineHandler`），因為那是同一條執行緒、嚴格照 log 順序，能保證「按鍵入帳時，它之前的加速狀態行都已經套用完畢」。

解析器跑在自己的執行緒上：ACT 的 log 執行緒只把字串丟進佇列，其餘都在別處做，解析器再慢也拖不到 ACT。實測 V8 每秒可吃 68,000 行，一場副本大約每秒數百行。

## 匯出的欄位

rDPS 家族沿用既有名稱 —— ACT 沒有任何欄位叫 `rdps`，所以可以直接用：

| key | 說明 |
|---|---|
| `rdps` | 把團輔還給施放者之後的每秒傷害。除以「戰鬥時長 − downtime」 |
| `adps` | 扣掉收到的單體增益，但給出去的仍算給施放者 |
| `ndps` | 扣掉收到的所有增益，且不計給出去的 |
| `cdps` | 保留收到的團輔，並計入給出去的 |
| `rdpsDelta` | rDPS 減去自己的每秒傷害：撐團拿到的，扣掉別人給的 |
| `rdpsPct` | 這一列佔全隊 rDPS 的百分比 |

GCD 欄位：

| key | 說明 |
|---|---|
| `gcdUptime` | GCD 運轉率（%）：第一次按鍵到最新一次按鍵之間，GCD 在轉的比例。**已經除好的值，懸浮窗直接顯示、不要自己再除** |
| `gcdCount` | GCD 次數（只計戰技與魔法） |
| `gcdClip` | GCD 之間空窗損失的總秒數 |
| `gcdOccupied` | 已結束的 GCD 佔用的總秒數（`gcdUptime` 的分子） |
| `gcdRecast` | 該玩家 2.5 秒基準的 GCD，由推估出的速度屬性算出 |

其餘欄位一律加 `fflogs` 前綴，因為 `damage`、`healed`、`maxhit` 這些名字是 ACT 的，而 ACT 的 export variable **只能新增、不能覆蓋**。所以同一列上兩邊的數字並排存在，由懸浮窗決定要顯示哪一個：

| key | 說明 |
|---|---|
| `fflogsDps` | 每秒傷害，**在這裡除好**，除數跟 rDPS 家族是同一個（戰鬥時長 − downtime）。**不含**獨立成列的寵物 —— 那幾列各自帶自己的一份，懸浮窗把寵物併進主人時加起來就是 rDPS 量的那個折疊總量 |
| `fflogsHps` | 每秒治療，同樣除好，除數是整場 |
| `fflogsDamage` | FFLogs 記的傷害。**不含**它另外獨立成列的寵物（巴哈姆特等） |
| `fflogsHits` / `fflogsCrithits` / `fflogsDirectHitCount` / `fflogsCritDirectHitCount` | 命中／暴擊／直擊／暴直次數 |
| `fflogsMaxhit` / `fflogsMAXHIT` | 最大單次傷害，`技能名-數字` 與純數字兩種寫法（照 ACT 的格式） |
| `fflogsHealed` / `fflogsOverHeal` / `fflogsHeals` / `fflogsCritheals` | 治療量（含溢出，照 ACT 的算法）、溢出量、次數。**這一版解析器對玩家不記治療**（它只把 NPC 與寵物標成友方），那時整組送空字串，不送 0 |
| `fflogsMaxheal` / `fflogsMAXHEAL` | 最大單次治療。同上 |
| `fflogsDeaths` | 死亡次數 |
| `fflogsDuration` | **傷害**欄位的除數：戰鬥時長減去 downtime。**帶小數，不是整數秒** |
| `fflogsHealDuration` | **治療**欄位的除數：整場，不扣 downtime。同樣帶小數 |

整場那一層（`EncounterData.ExportVariables`）：`fflogsDamage`、`fflogsHealed`、`fflogsRdps`、`fflogsEncdps`、`fflogsEnchps`、`fflogsDuration`、`fflogsHealDuration`、`fflogsDurationText`、`fflogsFightId`、`fflogsDowntime`、`fflogsApplied`、`fflogsParserVersion`。`fflogsDurationText` 是給標題列印的 `mm:ss`：ACT 自己的時間在過場時會從頭算起（它把戰鬥判定為結束又重開），底下每個數字卻還是整場的。`fflogsFightId` 是這一場的編號，過場時不變 —— 懸浮窗靠它分辨「ACT 暫時忘掉的那一列」與「已經離隊的人」。`fflogsEncdps` / `fflogsEnchps` 是懸浮窗標題列印的全隊總計 —— 在這裡除好，用的是每一列用的同兩個時鐘，標題才不會描述一場跟底下表格不同的戰鬥。

**每秒的欄位一律在這裡除。** 單人的時候 rDPS 照定義就等於 DPS（沒人給你團輔、你也沒給別人），所以只要這兩欄不是同一個地方除出來的，它們就會對不上：先是懸浮窗把時鐘捨成整數，一個除 30.4、一個除 30，差 1～2%；把時鐘改成帶小數送過去之後，換成懸浮窗把 30.456 讀成 30.45。所以 `fflogsDps` / `fflogsHps` 跟 rDPS 家族一樣除好再送，懸浮窗原樣顯示。

時鐘還是照送，而且**不要四捨五入到整秒**：舊版懸浮窗仍然自己除，`fflogsDuration` 帶小數才不會又差一次。

**空字串代表 FFLogs 沒有量到這一格**：這一列它不認得（NPC，或兩邊名字拼法不同）、這一整組它填不了（治療），或這一場它根本不在報（`fflogsApplied` 是 0）。量到的話一定是數字，**包含 0** —— 已經被解析器折進主人的寵物就是 0，那是「算在主人身上了」，不是「沒量」。

懸浮窗看到空字串就該保留 ACT 的數字，而且要**整組一起**：混的是同一場戰鬥的兩種量法並排在相鄰欄位（FFLogs 的 rDPS 配 ACT 的傷害），但一整組沒人量過的欄位不是混 —— 那組數字只有 ACT 有。治療就是這一組：把它的 0 蓋上去之後，治療欄變 0、旁邊的盾與溢療還是 ACT 的，有效治療就成了負數。mopimopi 的 `preferFflogs()` 照這個規則分成時鐘、傷害、治療三組。

除錯用的自訂事件 `onGcdUpdate` 每秒推一次；用 `getGcdData` 也可以主動拉。

同一個 ACT 裡若同時載入舊的 RdpsOverlay，兩邊註冊的是同一組 key，先註冊的那一方負責，後者靜默跳過。

## 一場戰鬥從哪裡到哪裡

ACT 只要脫戰超過閒置時限就會結束一個 encounter 再開一個，而**劇情換場正好就是那樣**：M8S 前半身死亡、一分鐘後第二隻出現，ACT 在那裡切了一刀，FFLogs 卻把整場當一場。

所以這裡的「一場」是**解析器的 fight，不是 ACT 的 encounter**（[PullBoundary.cs](OverlayPluginAddon/Fflogs/PullBoundary.cs)）。GCD 紀錄跟著 fight 走：換場不重置，重新開一場（滅團重來）才重置。照 ACT 的 encounter 重置會把前半場的 GCD 全丟掉，變成同一張表裡傷害欄位講整場、GCD 欄位只講後半場。

只有在解析器沒有 fight 可依據時才回頭聽 ACT：它沒有 handler 的副本，或解析器根本沒起來。換區會把 fight 忘掉，好讓 ACT 在下一個地方重新接手。

## 兩個時鐘

FFLogs 用兩個不同的除數，這不是我們的慣例，是它上傳器自己那一行：

```
((fight.endTime - fight.startTime) - (type === 'friendlyDamage' ? fight.downtime : 0)) / 1000
```

**傷害除以「戰鬥時長 − downtime」，治療除以整場。** downtime 是 boss 完全打不到的那段（M8S 兩個本體之間的一分鐘、絕歐米茄的每次換場）。用整場去除傷害，在有一分鐘 downtime 的戰鬥會低估大約 8%，欄位就對不上它要鏡像的那份 FFLogs 報告。

同一組 downtime 也要從 GCD 運轉率兩邊扣掉：坐在 downtime 裡的空窗不是玩家能填的空窗，而它也不該留在分母裡。`fight.downtime` 只有總量，區間本身在 zone handler 上，而 handler 是一場一場手寫的，**分成三種欄位形狀**（見 [DowntimeWindows.cs](OverlayPluginAddon/Fflogs/DowntimeWindows.cs)）。三種都要讀 —— 讀錯一種比完全不讀更糟：被誤判成「永不關閉」的視窗會把它之後所有真實空轉都吃掉。

**視窗要記在這一場上，不能每次 collect 重讀就算。** 解析器在戰鬥一結束就把 `meterFight` 丟掉，連帶 handler 也沒了，於是這一拉的最後一次 collect —— 也就是大家真正在看的那一份數字 —— 讀到零個視窗，每個 GCD 數字當場用「沒有 downtime」重算一次，整場一直正確忽略掉的過場又變回一分多鐘的空窗。[MeterPipeline](OverlayPluginAddon/Fflogs/MeterPipeline.cs) 因此用 fight id 把視窗留著，換場（換 fight id）才清掉；視窗以 start 為鍵，還開著的那個每次 collect 會用更晚的 end 覆蓋自己。

## FFLogs 解析器

`data/parser-ff.js` 是 FFLogs 自家的用戶端解析器，也就是它 Archon 上傳器驅動的那個 `window.LogParser`。版本、來源與更新方式記在 [data/PARSER-VERSION.md](data/PARSER-VERSION.md)。

它是 RPGLogs 的專有、混淆程式碼。放進這個 repo 與發布壓縮檔是維護者的決定。

執行環境用 [ClearScript](https://github.com/microsoft/ClearScript) 的 V8。選 V8 而不是 Jint 這類純 managed 引擎，是因為量過：同一份 parser-ff.js，V8 每秒 68,000 行，Jint 每秒 1,637 行，差 42 倍，而這是掛在 ACT 的 log 執行緒旁邊跑的。代價是壓縮檔多出約 39MB 的原生 DLL（`ClearScriptV8.win-x64.dll` 28.5MB、`ClearScript.V8.ICUData.dll` 10.3MB），而且限定 x64。

解析器是頁面腳本，所以 [ParserHost.cs](OverlayPluginAddon/Fflogs/ParserHost.cs) 給它一個最小的 `window` 外殼。那段 JavaScript 只有環境，沒有任何計算邏輯 —— 計算全部在 .NET 這邊。

### 相依組件要自己交出來

**ACT 是用自己讀進來的位元組載入外掛的**，所以 CLR 根本不知道這個 dll 是從哪個資料夾來的，也就不會去那裡找它需要的東西。別的 addon 沒踩到是因為它們用的（ACT 自己的型別、OverlayPlugin 的、Newtonsoft）在它們執行時早就載好了；ClearScript 是我們自己帶的。

[PrivateAssemblies.cs](OverlayPluginAddon/PrivateAssemblies.cs) 掛一個 `AssemblyResolve`，把 dll 旁邊的檔案按名字交出去。它刻意不引用 ACT 或 OverlayPlugin 的任何型別，這樣才載得起來、也才測得到。

`build.ps1` 裡那份清單少一個檔案不會降級，而是在解析器自己的執行緒上丟例外 —— 背景執行緒上沒人接的例外會**直接帶走整個 ACT**。所以那份清單是實測出來的：把 dll 從位元組載入、probing path 上什麼都沒有，一個一個補到能開為止（後面四個是傳遞相依，不試不會知道）。改動相依時照同樣方式驗一次。

兩個細節值得記住：

- **`disableLineSigning` 傳 `true`。** 每一行 network log 尾端都有簽章，解析器驗不過就直接 `throw`。我們不上傳，簽章沒有意義，而關掉它也就不必指望 ACT 一個位元組不差地把行交出來。
- **`metersEnabled` 傳 `true`。** 這才是讓解析器維護那組即時 rDPS 帳（每個 actor 的 `amount` / `amountTaken` / `singleTargetAmountTaken` / `amountGiven`）的開關。

### 區域

解析器讀行的方式跟區域有關。這個值不做成設定 —— ACT 本來就知道自己在看哪一個客戶端，再叫使用者選一次只是多一個可以選錯的地方。插件從 OverlayPlugin 的 `FFXIVRepository.GetMachinaRegion()` 讀出來再對應到 FFLogs 自己的區域編號（國際服 1、韓服 4、國服 5）。認不出來的一律當國際服。

## GCD 運轉率

演算法沿用 [xivanalysis](https://github.com/xivanalysis/xivanalysis) 的模型（`SpeedStatsAdapterStep` + `AlwaysBeCasting` + `speedStatMapper`）。

```
單次施放佔用 = max(recast, castTime + (castTime ≥ GCD ? 100ms 詠唱稅 : 0))
運轉率       = 1 − Σ 空窗 / (最新一次按鍵 − 第一次按鍵)
空窗         = Σ max(0, 間隔 − downtime − 佔用)   （超過 slack 才算）

以上整組在「打出 GCD 的當下」量一次，之後定住；分母是自己的按鍵區間再扣掉 downtime，不是戰鬥時長
```

**每次施放記它完整的 GCD 佔用，不看到下一次的間隔**：log 時間戳有 ~45ms 的批次抖動，一個乾淨的 2.5 秒輪替會量出 2.46～2.54 的間隔；如果拿 `min(recast, 間隔)`，每個「看起來早了 40ms」的按鍵都會被削掉，永遠到不了 100%。

**進行中的那個 GCD 兩邊都不算**：它的 recast 還在轉、後面的間隔還沒關上，所以它在 `gcdCount` 裡，但不在分子也不在分母。

**boss 打不到的時間兩邊都扣掉**：那段不是玩家能填的空窗，也不該留在分母裡。視窗從 FFLogs 解析器的 zone handler 拿（見〈兩個時鐘〉）；沒有解析器就每個空窗都算，也就是加這條之前的行為。

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

速度公式（`attributeMultiplier = 1000 − floor(130 × (stat − 420) / 2780)`）與其反函數都在 [ActionData.cs](OverlayPluginAddon/Gcd/ActionData.cs)，出處是 Allagan Studies，經 xivanalysis 的 `speedStatMapper` 轉錄。

### 資料來源

```powershell
node tools/Build-ActionData.js [xivanalysis 路徑]
```

抽出 recast 覆蓋（500ms 到 6000ms）與加速狀態（神速咏唱 0.80、內丹 0.85、風雅 0.87、疾走 0.85、靈感 0.75，靈感只作用於指定技能）。超過 10 秒的 recast 一律當成預設值。

### 還沒做到的

- **MNK / NIN 的職業基礎加成沒有單獨套用**（資料檔裡有，但沒接上）。
- **第一次按鍵之前、最後一次按鍵之後都不算**（見上）。

## 安裝

1. 解壓 `OverlayPluginAddon-X.Y.Z.zip` 到任意資料夾
2. ACT → Plugins → Plugin Listing → Browse → 選 `OverlayPluginAddon.dll` → Add/Enable
3. **載入順序必須是**：`FFXIV_ACT_Plugin.dll` → `OverlayPlugin.dll` → `OverlayPluginAddon.dll`
4. **整包解開，不要只拿 dll。** `data/` 與十幾個 DLL 必須跟 `OverlayPluginAddon.dll` 放在同一層

mopimopi 端（[Unnbird/mopimopi](https://github.com/Unnbird/mopimopi)）在設定畫面把要的欄位勾起來即可。外掛啟動時會註冊 preset **MopiMopiCustom**，在 OverlayPlugin 的「新增懸浮窗」直接選就好。

### 自動更新

每次 ACT 啟動時查一次 `Unnbird/OverlayPluginAddon` 的最新 release tag，比目前版本新才會詢問；按下同意才會下載 `OverlayPluginAddon-<version>.zip`，覆蓋 dll 與 `data/` 之後重啟 ACT。

## 驗證流程

不用進遊戲的三個，改動之後都該重跑：

```powershell
.\build.ps1 -SkipDeps
.\tools\Test-Gcd.ps1              # GCD 模型，含 downtime
.\tools\Test-Downtime.ps1         # zone handler 的三種 downtime 欄位形狀
.\tools\Test-Fflogs.ps1           # 解析器輸出 → ACT 表格的比對
.\tools\Test-ActionCategories.ps1 # 技能分類表
.\tools\Test-Loading.ps1          # 照 ACT 的方式載入打包好的 addon
```

`Test-Loading.ps1` 測的是**打包結果**而不是建置輸出：在子行程裡把 dll 從位元組載入、probing path 上什麼都沒有，確認解析器起得來；再確認少了 ClearScript 時它只是不啟動、不會把行程帶走。改動相依或動到 `build.ps1` 的檔案清單之後一定要跑。

`Test-Downtime.ps1` 與 `Test-Fflogs.ps1` 會開一個真的 V8，把假的 handler 與假的 fight 當成真的 script object 餵進去，所以連 interop 一起測到，不只是規則。

### 拿真實 log 對帳

單元測試證不了數字對不對 —— 解析器是 FFLogs 的，只有它自己的報告能說什麼才是對的。

```powershell
.\tools\Replay-Log.ps1 <Network_*.log> -From 2026-09-13T13:39 -To 2026-09-13T13:58
```

整條鏈路照跑一遍（同一個 ParserHost、同一份 parser-ff.js、同樣的 downtime 與比對），最後印出每個人的傷害、rDPS、aDPS、rDPS%、治療、死亡數。開同一場的 fflogs.com 報告對一遍就知道。

**改動任何 `Fflogs/` 底下的東西之後，都應該至少跑過一場真實 log。**

### 全部是 0？先跑診斷

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
| `running : no` | `data/parser-ff.js` 或 ClearScript 的 DLL 不在 dll 旁邊 |
| `lines parsed : 0` 但 log 有行 | 解析器執行緒起來了卻沒收到東西 |
| **`applied : False` 但 `fight` 有值** | **rDPS 欄位空白最常見的原因**：解析器有一場戰鬥，只是它不是 ACT 正在報的那一場。後面的括號會說是哪一條規則擋下來的 |
| `downtime windows : 0` 但這場應該有 | 這張圖的 zone handler 用了沒認得的欄位形狀 |

檔案最後會附上幾條原始 log line 並逐欄編號，直接跟 [StatusTracker.cs](OverlayPluginAddon/Gcd/StatusTracker.cs) 和 [EventSource.cs](OverlayPluginAddon/EventSource.cs) 最上面的欄位常數對照即可。

### 逐 GCD 對帳

每場戰鬥結束會在 dll 旁邊的 `OverlayPluginAddon.status.log` 追加一段：每人一行統計，底下每個 GCD 一行（時間、技能 id、硬詠唱與否、記入的 recast、到下一次的間隔、坐在 downtime 裡的部分、佔用、損失）。「GCD 邏輯錯了」的爭論在這裡收尾。

## 原始碼佈局

```
OverlayPluginAddon/
  Addon.cs                  ACT 外殼：找到 OverlayPlugin、起事件來源、註冊 preset 與更新器
  EventSource.cs            ACT hook、行佇列、export 欄位註冊
  Fflogs/
    ParserHost.cs           V8 生命週期、window 外殼、餵行、collectMeters
    ParserOutput.cs         把解析器輸出讀成 .NET 物件（寵物折疊、死亡、最大單次）
    DowntimeWindows.cs      zone handler 的三種 downtime 欄位形狀
    FightMatch.cs           fight ↔ ACT encounter 比對，以及兩個時鐘
    MeterSnapshot.cs        折疊比對完的每一列，export formatter 只讀這個
    MeterPipeline.cs        「收集完」到「欄位有數字」之間的全部，Replay-Log 也用同一份
    PullBoundary.cs         什麼算是新的一場（是 fight，不是 ACT 的 encounter）
  Gcd/
    GcdTracker.cs           xivanalysis 的 GCD 模型，含 downtime 視窗
    StatusTracker.cs        加速狀態與玩家判定
    ActionCategories.cs     從 FFXIV_ACT_Plugin.Resource 讀即時技能分類表
    ActionData.cs           每個技能的 recast / 詠唱時間 / 速度屬性
  PrivateAssemblies.cs      把自己帶的組件交給 CLR（ACT 用位元組載入，不會去 dll 旁邊找）
  Instrumentation.cs        診斷檔
data/
  actions.json              recast 資料（tools/Build-ActionData.js 產生）
  parser-ff.js              FFLogs 的解析器（見 PARSER-VERSION.md）
```

## 建置

```powershell
.\build.ps1
```

輸出在 `..\out\OverlayPluginAddon-<version>.zip`；中間產物在 `..\out\Release\OverlayPluginAddon\`。

專案假設的 workspace 佈局：

```
<workspace>/OverlayPlugin       <- OverlayPlugin 原始碼，build.ps1 會自動 clone
<workspace>/OverlayPluginAddon  <- 本專案
<workspace>/out                 <- 建置輸出
```

`build.ps1` 會在跑 `fetch_deps.ps1` 之前把 OverlayPlugin `DEPS.json` 裡失效的 `FFXIV_ACT_Plugin_SDK_2.0.6.1.zip` 改成 `3.0.2.8`（詳見腳本內註解），所以直接 `.\build.ps1` 就好。
