# TcpRoute2 配置手册

## 1. 配置文件结构

TcpRoute2 默认读取当前目录 `config.toml`，命令行可执行文件名为 `TcpRoute2`。

```toml
addr = "127.0.0.1:7070"

[[Hosts]]
Path = "hosts/local.txt"
Type = "base"
Credit = 0
UpdateInterval = "24h"

[[UpStreams]]
Name = "direct"
ProxyUrl = "direct://0.0.0.0:0000"
DnsResolve = true
Credit = 0
Sleep = 0
CorrectDelay = 0

[[UpStreams.Whitelist]]
Path = "rules/local.txt"
Type = "suffix"
UpdateInterval = "24h"

[[UpStreams.Blacklist]]
Path = "https://example.com/excluded.txt"
Type = "base"
UpdateInterval = "6h"

[[UpStreams.Blacklist]]
Type = "geosite"
Path = "geosite:gfw"
```

字段说明：

- `addr`：TcpRoute2 对外监听地址（SOCKS5、HTTP 与 CONNECT 共用端口）。
- `Hosts[]`：标准 hosts 规则源；`Path` 支持本地文件或 HTTP(S) URL，`Type` 支持 `base`、`suffix`、`pan`、`regex`，`Credit` 会随每个 DNS 候选参与端口安全过滤。
- `UpStreams[].Name`：线路唯一标识，也用于 `upProxy`、`DialProxyFor` 引用。
- `UpStreams[].Alias`：可选的可读别名；订阅节点在此保存真实节点名，不参与线路引用。
- `UpStreams[].ProxyUrl`：代理线路 URL（连接格式见 ProxyClient 文档）。
- `UpStreams[].DnsResolve`：是否本地先解析域名再连接。

- `UpStreams[].Credit`：线路信誉分。
- `UpStreams[].Sleep`：该线路发起连接前延迟（毫秒）。
- `UpStreams[].CorrectDelay`：该线路延迟修正值（毫秒）。
- `UpStreams[].Whitelist[]`：该线路的白名单来源，可以配置多个。
- `UpStreams[].Blacklist[]`：该线路的黑名单来源，可以配置多个。

启用 `DnsResolve` 的线路先检查 Hosts：命中时只尝试匹配的 hosts 地址，同一域名的多个地址可以竞速；未命中才查询 DNS/DoH。hosts 地址被信誉度或 IPv6 策略过滤、或者连接失败时，不会回退到 DNS。关闭 `DnsResolve` 时仍将域名交给支持远端解析的上游。

### 1.1 路由规则

每个白名单或黑名单项包含规则类型和数据来源：

- `Path`：本地文件或 HTTP(S) 远程文件；Geosite 数据同样支持本地路径和远程 URL。
- `Type="base"`：每行精确匹配一个域名。
- `Type="suffix"`：每行匹配一个域名及其所有子域。
- `Type="pan"`：每行使用 `*`、`?` 通配符。
- `Type="regex"`：每行是一个正则表达式。
- Geosite：使用 `Type="geosite"`、`Path="geosite:apple,geosite:google"`；`geosite.dat` 固定取 TcpRoute2 程序目录，缺少时报错。Quick UI 编辑紧凑集合名称。旧的自定义数据文件格式保留读取和往返兼容。
- GeoIP：使用 `Type="geoip"`、`Path="geoip:cn,geoip:private"`；`geoip.dat` 固定取 TcpRoute2 程序目录，缺少时报错。匹配目标 IPv4/IPv6 地址，旧的自定义数据文件格式保留兼容。
- `UpdateInterval`：远程来源的更新间隔，使用 Go duration 格式，例如 `1.5h`、`2h45m`；最小为 `1m`，下载失败后 1 分钟重试。

白名单命中时只在所有命中白名单的上游之间竞速；同一上游同时命中白名单和黑名单时，白名单优先。没有白名单命中时，所有未被黑名单排除的上游继续自动竞速。显式规则命中后不再应用该上游的 `Sleep` 延迟。

## 2. ProxyUrl 通用规则

- URL 查询参数大小写不敏感（内部会转小写）。
- 多条线路并行竞争，系统自动选最快可用线路。
- `#备注` 可写在线路 URL 末尾（部分协议会读取）。
- URL 中的路径、特殊字符建议做百分号编码（例如 `/` -> `%2F`）。

### 2.1 级联代理 upProxy

对 `socks/http/https` 可用：

```text
socks5://a.b.c.d:1080?upProxy=http://u:p@1.2.3.4:8080
```

`upProxy` 也可以直接填写已定义的上游 `Name`，支持前向引用和多级级联：

```toml
[[UpStreams]]
Name="entry"
ProxyUrl="socks5://127.0.0.1:1080"

[[UpStreams]]
Name="final"
ProxyUrl="https://proxy.example.com:443?upProxy=entry"
```

以上连接顺序为 TcpRoute2 → entry → final → 目标地址。启动时会校验名称、重复定义和循环引用；名称不存在时配置不会启动。完整代理 URL 的旧写法继续兼容。

说明：

- 名称区分大小写；包含空格、`&`、`+` 等特殊字符时，按 URL 查询参数规则编码。
- 引用只复用该上游的 `ProxyUrl`，不继承它的黑白名单、`DnsResolve`、`Credit`、`Sleep` 或 `CorrectDelay`。
- 可在 `http/https/socks5` URL 上使用名称引用。当前 ProxyClient 不支持在直连或 Xray 扩展协议 URL 上使用名称引用，此时会明确报错。

### 2.2 反向声明前置代理 DialProxyFor

在前置线路上指定目标上游 `Name`，载入时组成代理链，不改写目标线路的 TOML：

```toml
[[UpStreams]]
Name="sub_mysub_node1"
ProxyUrl="socks5://127.0.0.1:1080"
DialProxyFor="exit"

[[UpStreams]]
Name="exit"
ProxyUrl="https://proxy.example.com:443"
```

该链路连接顺序为 TcpRoute2 → sub_mysub_node1 → exit → 目标。
设置 `DialProxyFor` 只增加前置用途，线路仍按自己的黑白名单、信誉度和延迟参与普通请求竞速。目标线路无需引用订阅节点；取消前置节点后，目标按自己的原始 `ProxyUrl` 连接，不残留对已删除节点的引用。

- `DialProxyFor` 支持用逗号分隔多个 `Name` 选择器，区分大小写；允许前向引用和多级链。只匹配 `Name`，不匹配显示用的 `Alias`。
- 每个选择器优先精确匹配；没有同名节点时按前缀匹配。末尾加 `*` 可明确按前缀匹配，即使存在同名前缀节点。重叠选择器会去重，不会重复生成同一前置关系。
- 禁止单独的 `*`，包括多目标列表中的 `*`；也禁止精确、前缀或列表匹配到声明节点自身。`tw_0*` 这类非空前缀允许使用。
- 同一条线路的 `DialProxyFor` 与 URL `upProxy` 互斥；被 `DialProxyFor` 指向的目标也不能同时自行配置 `upProxy`。
- 多条前置指向同一目标时，生成独立链路候选并按目标线路的策略竞速。各链路有独立缓存身份；每个目标最多 256 个候选。
- 每个选择器都必须匹配至少一个目标；自指、循环、冲突和不支持的协议会在载入时报错。匹配到的 `Remove=true` 节点不生成链路。
- 继承的是前置的代理 URL；目标的 `DnsResolve`、`Credit`、`Sleep`、`CorrectDelay` 和黑白名单仍属于目标线路，不继承前置线路的这些策略参数。
  例如 A 的 `Sleep=3000`、B 的 `Sleep=25`：A 单独竞速时延迟 3000 毫秒；经 A 加速的 B 只按 25 毫秒参与竞速。
  订阅池中已知节点取消选中时删除其 TOML 表及附属规则，重选时使用当前订阅值和默认值。仅对订阅或已验证节点池中找不到的节点保留 `Remove=true` 保护；缓存暂不可用时保留现有配置。显式 `upProxy` 引用必须保持有效，否则取消操作报错并保留原文件。
- 当前 ProxyClient 的 HTTP/HTTPS/SOCKS 和 Xray 扩展协议目标均可接收前置；TcpRoute2 会在创建扩展协议本地客户端后显式绑定前置。直连目标不能配置此项。
- 原有显式 `upProxy=节点名` 继续兼容，也仍要求被引用节点存在。需自由取消订阅前置时，使用上面的反向声明方式。

例如让 A 作为三个台湾节点的前置，加速这些节点的访问：

```toml
[[UpStreams]]
Name="A"
ProxyUrl="socks5://127.0.0.1:1080"
DialProxyFor="tw_0"
```

当配置中有 `tw_0111`、`tw_0da`、`tw_0232_11aa` 且没有精确名称 `tw_0` 时，分别生成 `TcpRoute2 → A → 匹配节点 → 目标`，不是把匹配节点当作 A 的前置。`DialProxyFor="tw_0*,hk_*"` 可同时加速两个前缀组。

### 2.3 URL 内覆盖信用分

可以在 URL 里写 `credit` 覆盖 `UpStreams[].Credit`：

```text
socks5://1.2.3.4:1080?credit=10
```

## 3. 原生协议

## 3.1 direct

格式：

```text
direct://0.0.0.0:0000?localAddr=192.168.1.10:0
```

参数：

- `localAddr`：本地出口地址（可选），默认 `:0`。

## 3.2 socks5

格式：

```text
socks5://host:port
```

说明：

- 可视化客户端提供 `socks5` 的新建入口。
- 当前 `socks5` 按无认证方式使用（不配置用户名密码）。

## 3.3 http / https

格式：

```text
http://host:port
http://user:pass@host:port
https://host:port
https://user:pass@host:port
```

说明：

- 支持 Basic 认证（`user:pass@`）。

## 4. 其他上游连接

TcpRoute2 通过 [ProxyClient](https://github.com/GameXG/ProxyClient) 建立上游连接。其他协议的 URL 格式、传输及 TLS 参数由 ProxyClient 定义，请查阅其文档；TcpRoute2 只在 `ProxyUrl` 中保存完整连接 URL，不重复维护协议参数手册。

## 5. 推荐配置模板

```toml
addr = "127.0.0.1:7070"

[[UpStreams]]
Name = "direct"
ProxyUrl = "direct://0.0.0.0:0000"
DnsResolve = true
Credit = 0
Sleep = 30
CorrectDelay = 0

[[UpStreams]]
Name = "socks5-home"
ProxyUrl = "socks5://192.168.1.2:1080"
DnsResolve = false
Credit = 10
Sleep = 0
CorrectDelay = 0

[[UpStreams]]
Name = "http-main"
ProxyUrl = "http://127.0.0.1:8080"
DnsResolve = false
Credit = 20
Sleep = 0
CorrectDelay = 0
```

## 6. 快速排错

- 检查配置是否可读：

```bash
./TcpRoute2 -config config.toml
```

- 检查版本：

```bash
./TcpRoute2 -version
```

- 常见错误：

- `addr 错误的格式`：`ProxyUrl` 格式非法。
- `invalid host:port`：主机端口写法错误。
- `missing ...`：连接 URL 缺少必填参数，参照 ProxyClient 文档检查。
- 路径参数未编码：`path=/ws` 建议写成 `path=%2Fws`。
