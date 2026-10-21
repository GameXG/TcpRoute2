# TcpRoute2

[English](README.md) | 简体中文 | [繁體中文](README.zh-TW.md)

[![Android Build](https://github.com/GameXG/TcpRoute2/actions/workflows/android-build.yml/badge.svg)](https://github.com/GameXG/TcpRoute2/actions/workflows/android-build.yml)
[![Release](https://img.shields.io/github/v/release/GameXG/TcpRoute2)](https://github.com/GameXG/TcpRoute2/releases)
[![License: GPLv3](https://img.shields.io/badge/license-GPLv3-blue.svg)](LICENSE)


TcpRoute2 是一个 TCP 层路由器，会为 TCP 连接从多条线路（允许任意嵌套）和多个域名解析结果中自动选择最优线路。它同时尝试符合规则的直连和代理线路；启用本地解析时，还会同时尝试多个目标 IP，最终使用最快成功建立的连接。选路依据是连接建立耗时，而不是下载吞吐量。

支持线路级黑白名单、独立于操作系统的 hosts 文件、线路和 Host 候选地址的信誉度，以及通过 HTTP Host / TLS SNI 恢复目标域名。这些能力延续了原版 TcpRoute2 的路由原则，并增加了 DoH、Geosite/GeoIP，以及持续维护的 Windows 和 Android 客户端。

通过同一监听端口提供 SOCKS4、SOCKS4a、SOCKS5、HTTP 代理和 HTTPS CONNECT 服务。

上游代理连接由 [ProxyClient](https://github.com/GameXG/ProxyClient) 提供，具体协议和连接参数见其文档。TcpRoute2 负责线路选择、规则匹配、DNS 调度及客户端系统集成，并维护必需的内置直连线路。

## 安装

从 [Releases](https://github.com/GameXG/TcpRoute2/releases) 下载对应平台的包。客户端发布形式为 Android `arm64-v8a` APK，以及 Windows x64/ARM64 self-contained ZIP 便携包。核心另提供 Windows x64/ARM64 ZIP、macOS ARM64 和 Linux amd64/arm64 命令行压缩包，不再发布 macOS GUI 包。包内附带 `geosite.dat` 和 `geoip.dat`。

Android 提供独立代理和 VPN 两种模式；Windows 提供代理和 Wintun 两种互斥模式。Windows 接入复用 xray-core 的 Wintun/gVisor 栈，路由、DNS 和上游选择仍由 TcpRoute2 TOML 控制。要求及限制见 [Windows 客户端说明](windows/README.md)。

Windows 可选“设为系统代理”，设置当前用户的 HTTP/HTTPS/SOCKS 代理并在停止时恢复原值。Android VPN 模式可选“VPN 网络代理”，使用 Android 10+ 的公开 API，无须 ADB 或 Root；普通代理模式提供本地 SOCKS/HTTP 服务，菜单中的“Wi-Fi 代理设置”显示主机和端口并打开系统设置，由用户手动配置当前 Wi-Fi。代理设置供遵循系统代理的应用使用，不等同于 VPN/Wintun 接管。手动设置的 Wi-Fi 代理也需要手动关闭。正常启动不生成日志文件或 `logs/`，界面输出保存在有界内存中。

版本化发布包通过 GitHub Releases 分发。本地打包将当前版本安装包和 `SHA256SUMS.txt` 写入项目根目录 `dist/`，这些产物不纳入 Git。核心中间二进制与调试产物写入根 `.build/`；`android/` 和 `windows/` 保存各平台专属源码与构建脚本。执行 `bash scripts/clean-release-artifacts.sh` 可清理过期的打包产物。

GitHub 直分发包不等同于应用商店签名：未指定发布密钥时，Android 打包脚本默认使用维护机的本地调试证书签署；Windows 应用未进行 Authenticode 签名。各平台可能在首次安装或启动时要求用户确认。

命令行模式下，解压核心包，将 [config.toml.example](config.toml.example) 复制为 `config.toml`，并把示例代理 URL 替换为自己的上游。核心默认读取当前工作目录的 `config.toml`，也可以用 `-config <path>` 指定文件。macOS/Linux 在解压目录中执行：

```bash
cp config.toml.example config.toml
# 启动前先编辑 config.toml。
./TcpRoute2 -config ./config.toml
```

Windows 可在资源管理器中复制文件，然后执行 `.\TcpRoute2.exe -config .\config.toml`，也可以使用便携 GUI。应用代理设置填写 SOCKS5 `127.0.0.1:7070`；HTTP 代理使用相同地址和端口。SOCKS 客户端支持远端 DNS 时，建议开启，让 TcpRoute2 收到域名。

使用 `Type="geosite"` 或 `Type="geoip"` 时，`Path` 填写集合选择器，对应 `.dat` 文件须放在 TcpRoute2 核心程序所在目录，缺少文件会启动报错。

Geo 数据来源为 [Loyalsoldier/v2ray-rules-dat](https://github.com/Loyalsoldier/v2ray-rules-dat)，可从其 [Releases](https://github.com/Loyalsoldier/v2ray-rules-dat/releases) 下载：

- [geosite.dat](https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat)：域名集合，供 Geosite 规则使用。
- [geoip.dat](https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat)：IP 地址集合，供 GeoIP 规则使用。

手动下载时，请同时下载同一发布版本的 `geosite.dat.sha256sum` 和 `geoip.dat.sha256sum` 并校验文件，再将两个 `.dat` 文件放到 `TcpRoute2` / `TcpRoute2.exe` 旁边，无须创建 `data/` 目录。发布包已附带两份数据；Android 由应用管理其私有目录中的副本。

Android 和 Windows 客户端均提供 STUN、QUIC、IPv6 运行控制，三项默认关闭并在运行期间锁定。命令行部署可分别通过 `TCPROUTE_ALLOW_STUN`、`TCPROUTE_ALLOW_QUIC`、`TCPROUTE_ENABLE_IPV6` 设置为 `true` 启用。
源码编译时需要包含 `github.com/GameXG/ProxyClient` 的依赖组件。

macOS、Windows 和 Linux 发布打包时会下载最新发布版本的 Geosite/GeoIP 数据，校验 SHA256 后内置到包中。两个文件使用同一发布版本，下载只使用临时目录；任一下载或校验失败都会停止打包，不使用旧文件代替。普通源码/Android 构建仍可使用固定版本资源，下载到源码树的 `assets/`：

```bash
bash scripts/fetch-geosite.sh
```

## 平台支持

当前正式支持的平台为：

- `windows/`：支持 x64 和 ARM64 的纯托盘客户端，右键菜单提供全部操作及状态，点击图标只打开活动日志。配置直接在文件中编辑，保存后自动重载；支持系统代理及 Wintun 接管。PowerShell 脚本保留为调试/自动化入口。桌面端也可以搭配 Proxifier 这类工具做全局或按应用流量接管。
- macOS：仅提供 ARM64 核心命令行包，不提供 GUI 客户端。
- `android/`：仅支持 ARMv8 `arm64-v8a` 手机和平板的 Kotlin 客户端，包含 Activity、内置可视化配置、Foreground Service、本地 SOCKS for Proxifier 模式和 `VpnService`/TUN 接入。Android 可搭配 Proxifier for Android 使用本地 SOCKS 监听；若希望不依赖外部代理管理器，则使用内建 VPN/TUN 到 SOCKS 的桥接，并支持互斥的应用白名单/黑名单分流，TcpRoute2 自身永远静默绕过。
- Linux：提供 amd64/arm64 TcpRoute2 核心命令行包，使用 `config.toml` 或 `-config <path>`；不提供独立客户端或 GUI。

本工程不提供 iOS 或 Intel macOS 客户端。

Windows 托盘客户端本地运行：

```powershell
cd windows
dotnet run --project .\TcpRouteTray\TcpRouteTray.csproj
```

Windows 托盘客户端打包：

```powershell
powershell -ExecutionPolicy Bypass -File windows\package-tray.ps1 -Runtime win-x64
```

## 配置

Windows 客户端是 ZIP 便携式程序：核心、原始 TcpRoute2 配置 `config.toml`、`TcpRoute2Client.json` 和 Geo 数据文件均位于程序目录。核心支持 `-config <path>` 指定配置。Android 和 Windows 共享同一套核心 TOML；Windows 从托盘菜单打开 TOML 文件编辑，保存稳定后自动重载；Android 提供 Advanced TOML 和可视化编辑，本地/在线导入和导出操作完整 TOML。缺少核心时的自动下载与手动安装流程尚待实现。

便携式目录的基本结构为：

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

macOS/Linux 核心包只有 `TcpRoute2`、配置示例和 Geo 数据；不包含独立客户端。日志目录不是便携目录的必需组成部分。

Android 可视化配置分为连接、规则和 Host 三部分：

- 连接配置维护代理上游。直连是必需的内置第一条线路，不显示可编辑连接行；用户添加的上游从编号 2 开始。
- 规则配置为每条上游维护多个白名单和黑名单来源，支持本地文件、HTTP(S) 远程文件、Geosite 和 GeoIP。
- Host 配置加载本地或 HTTP(S) 标准 hosts 文件，不是手工填写“域名 → 地址”的表单。
- 可视化界面不开放 SOCKS 监听端口，默认或导入的 `addr` 会被保留；完整编辑使用 Advanced TOML。

Android 可视化修改只保存在页面草稿中。只有点击连接、规则或 Host 板块的“保存配置”，或者 Advanced TOML 中的“保存配置”，才会校验并写入 TOML。启动 VPN/SOCKS、查看请求日志和导出配置都不会顺带保存草稿；完整配置导入属于显式替换操作。客户端生成的 TOML 从 `addr` 开始，不添加 `TcpRoute config generated ...` 注释。

### 完整配置示例

```toml
addr = "127.0.0.1:7070"

# SOCKS 客户端传入 IP 时，可从这些端口的 HTTP Host / TLS SNI 恢复域名。
PreHttpPorts = [80]
PreHttpsPorts = [443]

# Host 来源可以是本地文件。
[[Hosts]]
Path = "hosts/local.txt"
Type = "base"
Credit = 0

# 也可以是定时刷新的远程 hosts 文件。
[[Hosts]]
Path = "https://example.com/hosts.txt"
Type = "suffix"
Credit = 0
UpdateInterval = "24h"

# 显式保留内置 direct；可视化客户端不显示它的编辑行。
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

# 普通规则文件：每行一个匹配项。
[[UpStreams.Whitelist]]
Path = "https://example.com/proxy.list"
Type = "suffix"
UpdateInterval = "24h"

# Geosite：Type 是类型，Path 是集合选择器；数据文件位于程序目录。
[[UpStreams.Whitelist]]
Type = "geosite"
Path = "geosite:apple,geosite:google"

# GeoIP：用于没有域名的目标 IP。
[[UpStreams.Whitelist]]
Type = "geoip"
Path = "geoip:cn,geoip:private"

[[UpStreams.Blacklist]]
Path = "rules/blocked.list"
Type = "base"
```

### 顶层和上游字段

- `addr`：SOCKS4、SOCKS4a、SOCKS5、HTTP 和 CONNECT 共用的 TCP 监听地址，默认 `127.0.0.1:7070`。
- `PreHttpPorts` / `PreHttpsPorts`：当 SOCKS 客户端已经把域名解析为 IP 时，TcpRoute2 在指定端口预读 HTTP Host 或 TLS SNI 来恢复域名。两项都省略时默认分别为 `[80]` 和 `[443]`；可使用 `[0]` 显式关闭对应类型。
- `UpStreams[].Name`：线路名称，用于日志和界面显示；名称不能重复。
- `UpStreams[].ProxyUrl`：由 ProxyClient 解析的完整代理 URL。
- `UpStreams[].DnsResolve`：是否由 TcpRoute2 解析目标域名。开启后，命中 Hosts 就只使用其地址，未命中才查询 DNS/DoH。
- `UpStreams[].Credit`：线路信誉度。低信誉线路不会用于需要更高安全级别的明文端口。
- `UpStreams[].Sleep`：没有显式规则命中时，开始尝试该线路前等待的毫秒数；用于降低高延迟或高成本上游的抢跑概率。
- `UpStreams[].CorrectDelay`：线路延迟修正值，单位毫秒。

直连使用 `direct://0.0.0.0:0000`。与原版不同，当前核心在 TOML 未配置直连时会自动补入内置 Direct。可视化客户端保留显式直连项，但不会提供“添加直连”或编辑直连参数的入口。

### 客户端 DNS 解析纠正

使用 redsocks、Proxifier 等工具的应用可能向 SOCKS 传入已经解析好的 IP。TcpRoute2 会在 `PreHttpPorts` / `PreHttpsPorts` 指定的端口读取 HTTP Host / TLS SNI，以恢复的域名执行路由和解析。`DnsResolve=true` 时由 TcpRoute2 本地解析；为 `false` 时把域名交给支持远端 DNS 的上游。这样可以避免依赖应用最初的 DNS 结果，但前提是能够恢复域名：没有 HTTP Host 或可用 TLS SNI 的流量无法通过此方式纠正。对应端口列表设为 `[0]` 可关闭。

### DNS 调度

不配置 `[[Dns]]` 时使用系统 DNS；可以通过 Advanced TOML 配置标准 DoH：

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

优先级数值越小越先查询，相同优先级按 TOML 顺序。每个来源默认超时 3 秒。超时、HTTP/TLS 错误、无可用地址或无效 DNS 响应时尝试下一来源，全部失败后使用系统 DNS；有效 NXDOMAIN 不回退。也可以显式配置 `Type="system"` 和优先级，已尝试过系统 DNS 时不会重复回退。

DoH 使用 DNS wire format 的 HTTPS POST，校验证书并直接连接指定端点，不继承系统代理环境变量；端点域名由系统 DNS 引导解析，不能承诺所有 DNS 查询都加密。请求取消会停止调度。IPv6 关闭时只查询 A，开启时同时保留 A/AAAA 候选。命中 Hosts 时跳过上述 DNS 调度，只使用其配置地址；解析结果交给现有连接竞速逻辑，仅在 `DnsResolve=true` 的上游进行本地解析。GUI 不提供独立 DNS 服务，Quick 保存会保留 DNS 配置表。

### Host 配置

每个 `[[Hosts]]` 表示一个标准 hosts 文件来源，可以配置多个：

- `Path`：本地路径或 HTTP(S) URL。相对路径以 `config.toml` 所在目录为基准。
- `Type`：对 hosts 文件中域名键的匹配方式，支持 `base`、`suffix`、`pan`、`regex`，默认 `base`。
- `Credit`：该来源产生的每个候选地址的信誉度，参与端口安全过滤。
- `UpdateInterval`：远程来源刷新间隔，采用 Go duration 格式，例如 `30m`、`1.5h`、`24h`；默认 `24h`，最小 `1m`。

hosts 文件每个有效行以候选 IP 开头，后面跟一个匹配键。重复同一个键可以保留多个 IPv4/IPv6 候选：

```text
# hosts/local.txt
192.0.2.10 example.com
192.0.2.11 example.com
2001:db8::10 example.com
```

`Type="base"` 只匹配 `example.com`；`suffix` 还会匹配其子域；`pan` 使用 `*`、`?` 通配符；`regex` 把匹配键作为正则表达式。Host 不支持 Geosite，且不会提前把域名固定替换成一个 IP。上游设置 `DnsResolve=true` 时，命中 hosts 就只尝试其匹配地址；同一域名的多个 hosts 地址可竞速。只有未命中时才查询 DNS/DoH。地址被过滤或连接失败时直接失败，不回退到 DNS。`DnsResolve=false` 的上游仍执行远端解析。配置 hosts 的本地解析线路会重新检查地址来源，不走缓存地址捷径，确保 hosts 修改或移除后生效。

本地 hosts 文件发生变化后会自动重新加载。远程来源按 `UpdateInterval` 刷新；下载失败会继续使用缓存，并在 1 分钟后重试。

### 白名单、黑名单和 Geo 数据

`[[UpStreams.Whitelist]]` 与 `[[UpStreams.Blacklist]]` 属于它们前面的那条 `[[UpStreams]]`，每条上游都可以配置多个来源。

普通规则文件的 `Path` 是本地路径或 HTTP(S) URL，每行一个匹配项；`Type` 使用与 Host 相同的 `base`、`suffix`、`pan`、`regex`。远程普通规则同样支持 `UpdateInterval`。

Geosite 新格式直接在 Path 中指定集合：

```toml
[[UpStreams.Whitelist]]
Type = "geosite"
Path = "geosite:apple,geosite:google"
```

`Type="geosite"` 时，`Path` 是逗号分隔的集合选择器，每项都带 `geosite:` 前缀。核心只从 TcpRoute2 可执行文件所在目录加载 `geosite.dat`，缺少文件时启动报错，不回退到配置目录、工作目录或其他资产目录。Android 安装目录只读，使用应用私有 Geo 目录。Android 可视化界面编辑 `apple,google`，保存时生成上面的格式。

GeoIP 使用 `Type="geoip"`，选择器例如 `Path="geoip:cn,geoip:private"`；在没有域名时匹配目标 IP，并要求程序旁有 `geoip.dat`。两种 Geo 来源的可视化编辑均只开放集合名称，保留隐藏的旧数据路径和刷新间隔；内置文件由 Geo 数据管理器控制。

只要任一白名单命中，就只在所有命中白名单的上游之间竞速；同一上游同时命中白名单和黑名单时，白名单优先。没有白名单命中时，黑名单会排除对应上游，其余上游继续竞速。显式规则命中后不再应用 `Sleep` 延迟。

### 上游连接

`ProxyUrl` 填写由 [ProxyClient](https://github.com/GameXG/ProxyClient) 支持的完整代理 URL。Android 可视化客户端先选择代理类型，再校验完整 URL 后保存。直连由客户端自动维护；已有 v1.x TOML 的旧连接类型仍保留兼容能力。TcpRoute2 配置说明见 [`help.md`](help.md)，具体协议和 TLS 参数见 ProxyClient 文档。

代理 URL 可用 `upProxy` 指定上层代理，值可以是完整代理 URL，也可以是另一条上游的 `Name`；支持前向和多级名称引用，未知名称、重复名称和循环会在启动前报错。

直连建议开启 `DnsResolve`，执行本地解析，优先使用 hosts，未命中才查询 DNS；支持远端 DNS 的代理通常关闭它，将域名交给上游。`Sleep=80` 会在没有显式规则命中时延迟 80 毫秒再尝试该代理，为直连留出先行时间；黑白名单显式分流时不应用此延迟。`CorrectDelay` 用于修正缓存中的连接耗时，适用于 Shadowsocks 等无法反馈真实目标连接时间的上游。

Android 从右上角“订阅”菜单添加订阅、刷新和选择节点，详见 [Android 订阅说明](android/README.md#subscriptions)。Windows 订阅源配置在 `TcpRoute2Client.json`，选中的节点会写入当前 TOML，成为普通的 `[[UpStreams]]` 条目。Windows 最小配置示例：

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

订阅响应可以是每行一个代理 URL 的列表、Base64 编码列表、JSON 节点数组、完整 Clash YAML 配置或完整 Surge 配置。程序会提取 Clash 的 `proxies:` 和 Surge 的 `[Proxy]` 条目。在托盘菜单中刷新订阅并选择节点；生成的 `Name` 使用 `sub_<订阅名>_<节点ID>`，`Alias` 保存节点真实名字。客户端选择项和排除项统一使用稳定 ID。支持的代理类型、JSON 节点字段、默认值、锁定字段、`UserAgent`、排除项和刷新行为见 [Windows 订阅说明](windows/README.md#subscriptions)。

`upProxy` 名称区分大小写，只复用被引用的 `ProxyUrl`，不继承规则、DNS 解析、信誉度或延迟。请在 HTTP/HTTPS/SOCKS 或 Xray 扩展协议 URL 上设置；直连不能接收 `upProxy`。示例见 [级联代理说明](help.md#21-级联代理-upproxy)。

上游可设置 `DialProxyFor="目标Name"`，载入时增加作为目标上游前置的用途，同时仍按自己的规则、信誉度和延迟参与普通请求选路。支持逗号分隔多个名称或前缀，例如 `DialProxyFor="tw_0,hk_*"`；优先精确匹配，找不到同名节点时按前缀匹配，末尾 `*` 明确按前缀匹配。匹配使用 `Name`，不使用 `Alias`；禁止单独的 `*` 和匹配自身。它与 URL 的 `upProxy` 互斥；目标也不能同时指定 `upProxy`。多个前置组成不同链路竞速，删除订阅前置后不会在目标 TOML 中留下引用。详见 [DialProxyFor](help.md#22-反向声明前置代理-dialproxyfor)。

## 信誉度功能

`UpStreams[].Credit` 和 `Hosts[].Credit` 按目标端口的安全等级过滤连接，取线路与地址信誉度的较低值。明文及未知端口通常要求 `0`；HTTPS `443`、SMTP over TLS `465` 等端口允许低至 `-500`。例如 `Credit=-100` 会排除 HTTP `80`，仍允许 HTTPS `443`。具体见[端口表](netchan/dialchan_filter.go)。此策略按端口判断，应用仍须验证服务端证书。

## 具体细节

* 对 DNS 解析获得的多个IP同时尝试连接，最终使用最快建立的连接。
* 同时使用直连及代理建立连接(可设置延迟)，最终使用最快建立的连接。
* 缓存线路及地址的连接耗时 15 分钟；优先尝试符合当前规则的缓存线路，失败后重新竞速。
* 解析不存在域名获得域名纠错IP，并添加到 IP黑名单
