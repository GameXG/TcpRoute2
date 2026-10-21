# TcpRoute2

[English](README.md) | [简体中文](README.zh-CN.md) | 繁體中文

[![Android Build](https://github.com/GameXG/TcpRoute2/actions/workflows/android-build.yml/badge.svg)](https://github.com/GameXG/TcpRoute2/actions/workflows/android-build.yml)
[![Release](https://img.shields.io/github/v/release/GameXG/TcpRoute2)](https://github.com/GameXG/TcpRoute2/releases)
[![License: GPLv3](https://img.shields.io/badge/license-GPLv3-blue.svg)](LICENSE)


TcpRoute2 是一個 TCP 層路由器，會為 TCP 連接從多條線路（允許任意嵌套）和多個域名解析結果中自動選擇最優線路。它同時嘗試符合規則的直連和代理線路；啟用本地解析時，還會同時嘗試多個目標 IP，最終使用最快成功建立的連接。選路依據是連接建立耗時，而不是下載吞吐量。

支援線路級黑白名單、獨立於作業系統的 hosts 檔案、線路和 Host 候選位址的信譽度，以及透過 HTTP Host / TLS SNI 恢復目標域名。這些能力延續了原版 TcpRoute2 的路由原則，並增加了 DoH、Geosite/GeoIP，以及持續維護的 Windows 和 Android 客戶端。

通過同一監聽端口提供 SOCKS4、SOCKS4a、SOCKS5、HTTP 代理和 HTTPS CONNECT 服務。

上游代理連接由 [ProxyClient](https://github.com/GameXG/ProxyClient) 提供，具體協議和連接參數見其文檔。TcpRoute2 負責線路選擇、規則匹配、DNS 調度及客戶端系統集成，並維護必需的內置直連線路。

## 安裝

從 [Releases](https://github.com/GameXG/TcpRoute2/releases) 下載對應平台的包。客戶端發佈形式為 Android `arm64-v8a` APK，以及 Windows x64/ARM64 self-contained ZIP 便攜包。核心另提供 Windows x64/ARM64 ZIP、macOS ARM64 和 Linux amd64/arm64 命令行壓縮包，不再發佈 macOS GUI 包。包內附帶 `geosite.dat` 和 `geoip.dat`。

Android 提供獨立代理和 VPN 兩種模式；Windows 提供代理和 Wintun 兩種互斥模式。Windows 接入復用 xray-core 的 Wintun/gVisor 棧，路由、DNS 和上游選擇仍由 TcpRoute2 TOML 控制。要求及限制見 [Windows 客戶端說明](windows/README.md)。

Windows 可選“設為系統代理”，設置當前用戶的 HTTP/HTTPS/SOCKS 代理並在停止時恢復原值。Android VPN 模式可選“VPN 網絡代理”，使用 Android 10+ 的公開 API，無須 ADB 或 Root；普通代理模式提供本地 SOCKS/HTTP 服務，菜單中的“Wi-Fi 代理設置”顯示主機和端口並打開系統設置，由用戶手動配置當前 Wi-Fi。代理設置供遵循系統代理的應用使用，不等同於 VPN/Wintun 接管。手動設置的 Wi-Fi 代理也需要手動關閉。正常啓動不生成日誌文件或 `logs/`，界面輸出保存在有界內存中。

版本化發佈包通過 GitHub Releases 分發。本地打包將當前版本安裝包和 `SHA256SUMS.txt` 寫入項目根目錄 `dist/`，這些產物不納入 Git。核心中間二進制與調試產物寫入根 `.build/`；`android/` 和 `windows/` 保存各平台專屬源碼與構建腳本。執行 `bash scripts/clean-release-artifacts.sh` 可清理過期的打包產物。

GitHub 直分發包不等同於應用商店簽名：未指定發佈密鑰時，Android 打包腳本默認使用維護機的本地調試證書簽署；Windows 應用未進行 Authenticode 簽名。各平台可能在首次安裝或啓動時要求用戶確認。

命令列模式下，解壓核心包，將 [config.toml.example](config.toml.example) 複製為 `config.toml`，並把範例代理 URL 替換為自己的上游。核心預設讀取目前工作目錄的 `config.toml`，也可以用 `-config <path>` 指定檔案。macOS/Linux 在解壓目錄中執行：

```bash
cp config.toml.example config.toml
# 啟動前先編輯 config.toml。
./TcpRoute2 -config ./config.toml
```

Windows 可在檔案總管中複製檔案，然後執行 `.\TcpRoute2.exe -config .\config.toml`，也可以使用便攜 GUI。應用代理設定填寫 SOCKS5 `127.0.0.1:7070`；HTTP 代理使用相同位址和連接埠。SOCKS 客戶端支援遠端 DNS 時，建議開啟，讓 TcpRoute2 收到域名。

使用 `Type="geosite"` 或 `Type="geoip"` 時，`Path` 填寫集合選擇器，對應 `.dat` 檔案須放在 TcpRoute2 核心程式所在目錄，缺少檔案會啟動報錯。

Geo 數據來源為 [Loyalsoldier/v2ray-rules-dat](https://github.com/Loyalsoldier/v2ray-rules-dat)，可從其 [Releases](https://github.com/Loyalsoldier/v2ray-rules-dat/releases) 下載：

- [geosite.dat](https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat)：域名集合，供 Geosite 規則使用。
- [geoip.dat](https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat)：IP 地址集合，供 GeoIP 規則使用。

手動下載時，請同時下載同一髮布版本的 `geosite.dat.sha256sum` 和 `geoip.dat.sha256sum` 並校驗文件，再將兩個 `.dat` 文件放到 `TcpRoute2` / `TcpRoute2.exe` 旁邊，無須創建 `data/` 目錄。發佈包已附帶兩份數據；Android 由應用管理其私有目錄中的副本。

Android 和 Windows 客戶端均提供 STUN、QUIC、IPv6 運行控制，三項默認關閉並在運行期間鎖定。命令行部署可分別通過 `TCPROUTE_ALLOW_STUN`、`TCPROUTE_ALLOW_QUIC`、`TCPROUTE_ENABLE_IPV6` 設置為 `true` 啓用。
源碼編譯時需要包含 `github.com/GameXG/ProxyClient` 的依賴組件。

macOS、Windows 和 Linux 發佈打包時會下載最新發佈版本的 Geosite/GeoIP 數據，校驗 SHA256 後內置到包中。兩個文件使用同一髮布版本，下載只使用臨時目錄；任一下載或校驗失敗都會停止打包，不使用舊文件代替。普通源碼/Android 構建仍可使用固定版本資源，下載到源碼樹的 `assets/`：

```bash
bash scripts/fetch-geosite.sh
```

## 平台支持

當前正式支持的平台為：

- `windows/`：支援 x64 和 ARM64 的純系統匣客戶端，右鍵選單提供全部操作及狀態，點擊圖示只開啟活動日誌。配置直接在檔案中編輯，儲存後自動重新載入；支援系統代理及 Wintun 接管。PowerShell 腳本保留為調試/自動化入口。桌面端也可以搭配 Proxifier 這類工具做全局或按應用流量接管。
- macOS：僅提供 ARM64 核心命令行包，不提供 GUI 客戶端。
- `android/`：僅支持 ARMv8 `arm64-v8a` 手機和平板的 Kotlin 客戶端，包含 Activity、內置可視化配置、Foreground Service、本地 SOCKS for Proxifier 模式和 `VpnService`/TUN 接入。Android 可搭配 Proxifier for Android 使用本地 SOCKS 監聽；若希望不依賴外部代理管理器，則使用內建 VPN/TUN 到 SOCKS 的橋接，並支持互斥的應用白名單/黑名單分流，TcpRoute2 自身永遠靜默繞過。
- Linux：提供 amd64/arm64 TcpRoute2 核心命令行包，使用 `config.toml` 或 `-config <path>`；不提供獨立客戶端或 GUI。

本工程不提供 iOS 或 Intel macOS 客戶端。

Windows 托盤客戶端本地運行：

```powershell
cd windows
dotnet run --project .\TcpRouteTray\TcpRouteTray.csproj
```

Windows 托盤客戶端打包：

```powershell
powershell -ExecutionPolicy Bypass -File windows\package-tray.ps1 -Runtime win-x64
```

## 配置

Windows 客戶端是 ZIP 便攜式程序：核心、原始 TcpRoute2 配置 `config.toml`、`TcpRoute2Client.json` 和 Geo 數據文件均位於程序目錄。核心支持 `-config <path>` 指定配置。Android 和 Windows 共享同一套核心 TOML；Windows 從系統匣選單開啟 TOML 檔案編輯，儲存穩定後自動重新載入；Android 提供 Advanced TOML 和視覺化編輯，本機/線上匯入和匯出操作完整 TOML。缺少核心時的自動下載與手動安裝流程尚待實現。

便攜式目錄的基本結構為：

```text
TcpRoute2/
├─ TcpRoute2_Windows_x64.exe / TcpRoute2_Windows_arm64.exe
├─ TcpRoute2(.exe)                 # bundled core
├─ TcpRoute2Client.json
├─ wintun.dll
├─ config.toml
├─ config.toml.example
├─ geosite.dat
└─ geoip.dat
```

macOS/Linux 核心包只有 `TcpRoute2`、配置示例和 Geo 數據；不包含獨立客戶端。日誌目錄不是便攜目錄的必需組成部分。

Android 視覺化配置分為連接、規則和 Host 三部分：

- 連接配置維護代理上游。直連是必需的內置第一條線路，不顯示可編輯連接行；用戶添加的上游從編號 2 開始。
- 規則配置為每條上游維護多個白名單和黑名單來源，支持本地文件、HTTP(S) 遠程文件、Geosite 和 GeoIP。
- Host 配置加載本地或 HTTP(S) 標準 hosts 文件，不是手工填寫“域名 → 地址”的表單。
- 可視化界面不開放 SOCKS 監聽端口，默認或導入的 `addr` 會被保留；完整編輯使用 Advanced TOML。

Android 視覺化修改只保存在頁面草稿中。只有點擊連接、規則或 Host 板塊的“保存配置”，或者 Advanced TOML 中的“保存配置”，才會校驗並寫入 TOML。啓動 VPN/SOCKS、查看請求日誌和導出配置都不會順帶保存草稿；完整配置導入屬於顯式替換操作。客戶端生成的 TOML 從 `addr` 開始，不添加 `TcpRoute config generated ...` 注釋。

### 完整配置示例

```toml
addr = "127.0.0.1:7070"

# SOCKS 客戶端傳入 IP 時，可從這些端口的 HTTP Host / TLS SNI 恢復域名。
PreHttpPorts = [80]
PreHttpsPorts = [443]

# Host 來源可以是本地文件。
[[Hosts]]
Path = "hosts/local.txt"
Type = "base"
Credit = 0

# 也可以是定時刷新的遠程 hosts 文件。
[[Hosts]]
Path = "https://example.com/hosts.txt"
Type = "suffix"
Credit = 0
UpdateInterval = "24h"

# 明確保留內建 direct；視覺化客戶端不顯示它的編輯行。
[[UpStreams]]
Name = "direct"
ProxyUrl = "direct://0.0.0.0:0000"
DnsResolve = true
Credit = 0
Sleep = 0
CorrectDelay = 0

[[UpStreams]]
Name = "proxy"
ProxyUrl = "socks5://127.0.0.1:1080"
DnsResolve = false
Credit = 0
Sleep = 80
CorrectDelay = 0

# 普通規則文件：每行一個匹配項。
[[UpStreams.Whitelist]]
Path = "https://example.com/proxy.list"
Type = "suffix"
UpdateInterval = "24h"

# Geosite：Type 是類型，Path 是集合選擇器；數據文件位於程序目錄。
[[UpStreams.Whitelist]]
Type = "geosite"
Path = "geosite:apple,geosite:google"

# GeoIP：用於沒有域名的目標 IP。
[[UpStreams.Whitelist]]
Type = "geoip"
Path = "geoip:cn,geoip:private"

[[UpStreams.Blacklist]]
Path = "rules/blocked.list"
Type = "base"
```

### 頂層和上游字段

- `addr`：SOCKS4、SOCKS4a、SOCKS5、HTTP 和 CONNECT 共用的 TCP 監聽地址，默認 `127.0.0.1:7070`。
- `PreHttpPorts` / `PreHttpsPorts`：當 SOCKS 客戶端已經把域名解析為 IP 時，TcpRoute2 在指定端口預讀 HTTP Host 或 TLS SNI 來恢復域名。兩項都省略時默認分別為 `[80]` 和 `[443]`；可使用 `[0]` 顯式關閉對應類型。
- `UpStreams[].Name`：線路名稱，用於日誌和界面顯示；名稱不能重復。
- `UpStreams[].ProxyUrl`：由 ProxyClient 解析的完整代理 URL。
- `UpStreams[].DnsResolve`：是否由 TcpRoute2 解析目標域名。開啓後，命中 Hosts 就只使用其位址，未命中才查詢 DNS/DoH。
- `UpStreams[].Credit`：線路信譽度。低信譽線路不會用於需要更高安全級別的明文端口。
- `UpStreams[].Sleep`：沒有顯式規則命中時，開始嘗試該線路前等待的毫秒數；用於降低高延遲或高成本上游的搶跑概率。
- `UpStreams[].CorrectDelay`：線路延遲修正值，單位毫秒。

直連使用 `direct://0.0.0.0:0000`。與原版不同，目前核心在 TOML 未配置直連時會自動補入內建 Direct。視覺化客戶端保留明確的直連項，但不會提供「新增直連」或編輯直連參數的入口。

### 客戶端 DNS 解析糾正

使用 redsocks、Proxifier 等工具的應用可能向 SOCKS 傳入已經解析好的 IP。TcpRoute2 會在 `PreHttpPorts` / `PreHttpsPorts` 指定的連接埠讀取 HTTP Host / TLS SNI，以恢復的域名執行路由和解析。`DnsResolve=true` 時由 TcpRoute2 本地解析；為 `false` 時把域名交給支援遠端 DNS 的上游。這樣可以避免依賴應用最初的 DNS 結果，但前提是能夠恢復域名：沒有 HTTP Host 或可用 TLS SNI 的流量無法透過此方式糾正。對應連接埠清單設為 `[0]` 可關閉。

### DNS 調度

不配置 `[[Dns]]` 時使用系統 DNS；可以通過 Advanced TOML 配置標準 DoH：

```toml
[[Dns]]
Type="doh"
URL="https://cloudflare-dns.com/dns-query"
Priority=10
Timeout="3s"

[[Dns]]
Type="doh"
URL="https://dns.google/dns-query"
Priority=20
Timeout="3s"
```

優先級數值越小越先查詢，相同優先級按 TOML 順序。每個來源默認超時 3 秒。超時、HTTP/TLS 錯誤、無可用地址或無效 DNS 響應時嘗試下一來源，全部失敗後使用系統 DNS；有效 NXDOMAIN 不回退。也可以顯式配置 `Type="system"` 和優先級，已嘗試過系統 DNS 時不會重復回退。

DoH 使用 DNS wire format 的 HTTPS POST，校驗證書並直接連接指定端點，不繼承系統代理環境變量；端點域名由系統 DNS 引導解析，不能承諾所有 DNS 查詢都加密。請求取消會停止調度。IPv6 關閉時只查詢 A，開啓時同時保留 A/AAAA 候選。命中 Hosts 時跳過上述 DNS 調度，只使用其配置位址；解析結果交給現有連接競速邏輯，僅在 `DnsResolve=true` 的上游進行本地解析。GUI 不提供獨立 DNS 服務，Quick 保存會保留 DNS 配置表。

### Host 配置

每個 `[[Hosts]]` 表示一個標準 hosts 文件來源，可以配置多個：

- `Path`：本地路徑或 HTTP(S) URL。相對路徑以 `config.toml` 所在目錄為基準。
- `Type`：對 hosts 文件中域名鍵的匹配方式，支持 `base`、`suffix`、`pan`、`regex`，默認 `base`。
- `Credit`：該來源產生的每個候選地址的信譽度，參與端口安全過濾。
- `UpdateInterval`：遠程來源刷新間隔，採用 Go duration 格式，例如 `30m`、`1.5h`、`24h`；默認 `24h`，最小 `1m`。

hosts 文件每個有效行以候選 IP 開頭，後面跟一個匹配鍵。重復同一個鍵可以保留多個 IPv4/IPv6 候選：

```text
# hosts/local.txt
192.0.2.10 example.com
192.0.2.11 example.com
2001:db8::10 example.com
```

`Type="base"` 只匹配 `example.com`；`suffix` 還會匹配其子域；`pan` 使用 `*`、`?` 通配符；`regex` 把匹配鍵作為正則表達式。Host 不支持 Geosite，且不會提前把域名固定替換成一個 IP。上游設定 `DnsResolve=true` 時，命中 hosts 就只嘗試其匹配位址；同一域名的多個 hosts 位址可競速。只有未命中時才查詢 DNS/DoH。位址被過濾或連接失敗時直接失敗，不回退到 DNS。`DnsResolve=false` 的上游仍執行遠端解析。配置 hosts 的本機解析線路會重新檢查位址來源，不走快取位址捷徑，確保 hosts 修改或移除後生效。

本地 hosts 文件發生變化後會自動重新加載。遠程來源按 `UpdateInterval` 刷新；下載失敗會繼續使用緩存，並在 1 分鐘後重試。

### 白名單、黑名單和 Geo 資料

`[[UpStreams.Whitelist]]` 與 `[[UpStreams.Blacklist]]` 屬於它們前面的那條 `[[UpStreams]]`，每條上游都可以配置多個來源。

普通規則文件的 `Path` 是本地路徑或 HTTP(S) URL，每行一個匹配項；`Type` 使用與 Host 相同的 `base`、`suffix`、`pan`、`regex`。遠程普通規則同樣支持 `UpdateInterval`。

Geosite 新格式直接在 Path 中指定集合：

```toml
[[UpStreams.Whitelist]]
Type = "geosite"
Path = "geosite:apple,geosite:google"
```

`Type="geosite"` 時，`Path` 是逗號分隔的集合選擇器，每項都帶 `geosite:` 前綴。核心只從 TcpRoute2 可執行文件所在目錄加載 `geosite.dat`，缺少文件時啓動報錯，不回退到配置目錄、工作目錄或其他資產目錄。Android 安裝目錄只讀，使用應用私有 Geo 目錄。Android 視覺化界面編輯 `apple,google`，保存時生成上面的格式。

GeoIP 使用 `Type="geoip"`，選擇器例如 `Path="geoip:cn,geoip:private"`；在沒有域名時匹配目標 IP，並要求程式旁有 `geoip.dat`。兩種 Geo 來源的視覺化編輯均只開放集合名稱，保留隱藏的舊資料路徑和更新間隔；內建檔案由 Geo 資料管理器控制。

只要任一白名單命中，就只在所有命中白名單的上游之間競速；同一上游同時命中白名單和黑名單時，白名單優先。沒有白名單命中時，黑名單會排除對應上游，其餘上游繼續競速。顯式規則命中後不再應用 `Sleep` 延遲。

### 上游連接

`ProxyUrl` 填寫由 [ProxyClient](https://github.com/GameXG/ProxyClient) 支持的完整代理 URL。Android 視覺化客戶端先選擇代理類型，再校驗完整 URL 後保存。直連由客戶端自動維護；已有 v1.x TOML 的舊連接類型仍保留兼容能力。TcpRoute2 配置說明見 [`help.md`](help.md)，具體協議和 TLS 參數見 ProxyClient 文檔。

代理 URL 可用 `upProxy` 指定上層代理，值可以是完整代理 URL，也可以是另一條上游的 `Name`；支援前向和多級名稱引用，未知名稱、重複名稱和循環會在啟動前報錯。

直連建議開啟 `DnsResolve`，執行本機解析，優先使用 hosts，未命中才查詢 DNS；支援遠端 DNS 的代理通常關閉它，將域名交給上游。`Sleep=80` 會在沒有明確規則命中時延遲 80 毫秒再嘗試該代理，為直連留出先行時間；黑白名單明確分流時不套用此延遲。`CorrectDelay` 用於修正快取中的連接耗時，適用於 Shadowsocks 等無法回報真實目標連接時間的上游。

Android 從右上角「訂閱」選單新增訂閱、更新和選取節點，詳見 [Android 訂閱說明](android/README.md#subscriptions)。Windows 訂閱來源配置在 `TcpRoute2Client.json`，選取的節點會寫入目前的 TOML，成為一般的 `[[UpStreams]]` 條目。Windows 最小配置範例：

```json
{
  "Subscriptions": [
    {
      "Id": "mysub",
      "Name": "mysub",
      "Url": "https://example.com/proxies.txt",
      "UserAgent": "Mihomo/Clash Verge",
      "SelectedNodesAll": false,
      "UpdateIntervalMinutes": 0,
      "Defaults": { "DnsResolve": false, "Credit": 0, "Sleep": 80, "CorrectDelay": 0 },
      "LockedFields": [],
      "Overrides": {}
    }
  ]
}
```

訂閱回應可以是每行一個代理 URL 的清單、Base64 編碼清單、JSON 節點陣列、完整 Clash YAML 設定或完整 Surge 設定。程式會提取 Clash 的 `proxies:` 和 Surge 的 `[Proxy]` 條目。在系統匣選單中更新訂閱並選取節點；生成的 `Name` 使用 `sub_<訂閱名>_<節點ID>`，`Alias` 儲存節點真實名稱。客戶端選取項目和排除項目統一使用穩定 ID。支援的代理類型、JSON 節點欄位、預設值、鎖定欄位、`UserAgent`、排除項和更新行為見 [Windows 訂閱說明](windows/README.md#subscriptions)。

`upProxy` 名稱區分大小寫，只複用被引用的 `ProxyUrl`，不繼承規則、DNS 解析、信譽度或延遲。請在 HTTP/HTTPS/SOCKS 或 Xray 擴充協定 URL 上設定；直連不能接收 `upProxy`。範例見 [級聯代理說明](help.md#21-级联代理-upproxy)。

上游可設定 `DialProxyFor="目標Name"`，載入時增加作為目標上游前置的用途，同時仍按自己的規則、信譽度和延遲參與一般請求選路。支援逗號分隔多個名稱或前綴，例如 `DialProxyFor="tw_0,hk_*"`；優先精確匹配，找不到同名節點時按前綴匹配，末尾 `*` 明確按前綴匹配。匹配使用 `Name`，不使用 `Alias`；禁止單獨的 `*` 和匹配自身。它與 URL 的 `upProxy` 互斥；目標也不能同時指定 `upProxy`。多個前置組成不同鏈路競速，刪除訂閱前置後不會在目標 TOML 中留下引用。詳見 [DialProxyFor](help.md#22-反向声明前置代理-dialproxyfor)。

## 信譽度功能

`UpStreams[].Credit` 和 `Hosts[].Credit` 按目標連接埠的安全等級過濾連接，取線路與位址信譽度的較低值。明文及未知連接埠通常要求 `0`；HTTPS `443`、SMTP over TLS `465` 等連接埠允許低至 `-500`。例如 `Credit=-100` 會排除 HTTP `80`，仍允許 HTTPS `443`。具體見[連接埠表](netchan/dialchan_filter.go)。此策略按連接埠判斷，應用仍須驗證伺服器憑證。

## 具體細節

* 對 DNS 解析獲得的多個IP同時嘗試連接，最終使用最快建立的連接。
* 同時使用直連及代理建立連接(可設置延遲)，最終使用最快建立的連接。
* 快取線路及位址的連接耗時 15 分鐘；優先嘗試符合目前規則的快取線路，失敗後重新競速。
* 解析不存在域名獲得域名糾錯IP，並添加到 IP黑名單
