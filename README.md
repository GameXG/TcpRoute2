# TcpRoute2

English | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md)

[![Android Build](https://github.com/GameXG/TcpRoute2/actions/workflows/android-build.yml/badge.svg)](https://github.com/GameXG/TcpRoute2/actions/workflows/android-build.yml)
[![Release](https://img.shields.io/github/v/release/GameXG/TcpRoute2)](https://github.com/GameXG/TcpRoute2/releases)
[![License: GPLv3](https://img.shields.io/badge/license-GPLv3-blue.svg)](LICENSE)

TcpRoute2 is a TCP router that selects the fastest successful connection across multiple routes, nested upstreams, and DNS address candidates. It races eligible Direct and proxy routes and, when local resolution is enabled, multiple destination IPs. Route selection measures connection establishment, not download throughput.

It supports per-upstream whitelists and blacklists, independent hosts files, route and Host candidate trust levels, and recovery of destination domains from HTTP Host or TLS SNI. These preserve the original TcpRoute2 routing principles while adding DoH, Geosite/GeoIP, and maintained Windows and Android clients.

SOCKS5, HTTP proxy, and HTTPS CONNECT share one listener port.

Upstream proxy connections are provided by [ProxyClient](https://github.com/GameXG/ProxyClient); see its documentation for supported protocols and connection parameters. TcpRoute2 owns route selection, rule matching, DNS scheduling, and client system integration, and maintains a required built-in Direct route.

## Installation

Download the package for your platform from [Releases](https://github.com/GameXG/TcpRoute2/releases). Client releases include an Android `arm64-v8a` APK and self-contained portable Windows x64/ARM64 ZIP packages. Separate core packages are available as Windows x64/ARM64 ZIPs and macOS ARM64 / Linux amd64/arm64 command-line archives. The macOS GUI is no longer distributed. Packages include both `geosite.dat` and `geoip.dat`.

Android offers standalone proxy and VPN modes. Windows offers mutually exclusive proxy and Wintun modes. Windows integration reuses the xray-core Wintun/gVisor stack; TcpRoute2 TOML still controls routing, DNS, and upstream selection. See the [Windows client guide](windows/README.md) for requirements and limitations.

Windows optionally sets the current user's HTTP/HTTPS/SOCKS system proxy and restores its previous values on stop. Android's optional **VPN network proxy** uses the public Android 10+ API without ADB or Root. Standalone Android mode provides a local SOCKS/HTTP listener; **Wi-Fi proxy settings** in the menu displays its host and port and opens system settings for manual Wi-Fi configuration. Proxy settings apply to apps that honor them and are distinct from VPN/Wintun traffic capture. Manually configured Wi-Fi proxies must also be disabled manually. Normal operation creates no log files or `logs/` directory; client output stays in bounded memory.

Versioned distributable packages are published through GitHub Releases. Local packaging writes the current release and `SHA256SUMS.txt` under `dist/`; these outputs are not tracked in Git. Core intermediates and debug artifacts go under `.build/`. Android and Windows directories contain platform-specific source and build scripts. Run `bash scripts/clean-release-artifacts.sh` to remove obsolete packaging outputs.

Direct GitHub distribution is separate from app-store signing. The Android packaging script defaults to the maintainer's local debug certificate unless a release keystore is supplied. Windows applications have no Authenticode signature. Platforms may request confirmation on first installation or launch.

For command-line use, extract the core package, copy [config.toml.example](config.toml.example) to `config.toml`, and replace its sample proxy URL with your upstream. By default, the core reads `config.toml` from the current working directory; `-config <path>` selects another file. For example, from the extracted directory on macOS/Linux:

```bash
cp config.toml.example config.toml
# Edit config.toml before starting.
./TcpRoute2 -config ./config.toml
```

On Windows, copy the file in Explorer and run `.\TcpRoute2.exe -config .\config.toml`, or use the portable GUI. Configure proxy-aware applications to use SOCKS5 `127.0.0.1:7070`; HTTP proxy settings use the same address and port. For SOCKS clients that support it, enable remote DNS so TcpRoute2 receives the domain name.

With `Type="geosite"` or `Type="geoip"`, `Path` contains list selectors and the corresponding data file must be beside the core executable. Missing data causes a startup error.

Geo data comes from [Loyalsoldier/v2ray-rules-dat](https://github.com/Loyalsoldier/v2ray-rules-dat) and is available from its [Releases](https://github.com/Loyalsoldier/v2ray-rules-dat/releases):

- [geosite.dat](https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat): domain lists for Geosite rules.
- [geoip.dat](https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat): IP address lists for GeoIP rules.

For manual downloads, also obtain `geosite.dat.sha256sum` and `geoip.dat.sha256sum` from the same release and verify both files. Place the data beside `TcpRoute2` / `TcpRoute2.exe`; no `data/` directory is needed. Release packages already include both files. Android manages its copies in the application's private directory.

Android and Windows expose persisted STUN, QUIC, and IPv6 controls. All three default off and stay locked during an active session. Command-line deployments can enable them by setting `TCPROUTE_ALLOW_STUN`, `TCPROUTE_ALLOW_QUIC`, and `TCPROUTE_ENABLE_IPV6` to `true`.
Source builds need the dependencies of `github.com/GameXG/ProxyClient`.

macOS, Windows, and Linux release packaging downloads the latest Geosite/GeoIP pair, verifies SHA256, and bundles it. Both files come from the same release; downloads use temporary directories. A failed download or checksum stops packaging instead of substituting old data. Ordinary source and Android builds can use pinned resources in `assets/`:

```bash
bash scripts/fetch-geosite.sh
```

## Platform Support

- `windows/`: Tray-only client for x64 and ARM64, with commands and status in the context menu and a separate activity-log window. Configuration is edited in files and reloaded automatically; the client manages Windows system proxy and Wintun. PowerShell remains available for debugging and automation. External tools such as Proxifier can also provide global or per-application capture.
- macOS: ARM64 core command-line package only; no GUI client.
- `android/`: Kotlin client for ARMv8 `arm64-v8a` phones and tablets, with an Activity, visual configuration editor, foreground service, standalone local proxy, and `VpnService`/TUN integration. The local SOCKS listener can be used with external proxy managers. Built-in VPN bridges TUN traffic to SOCKS and supports mutually exclusive application allowlists/denylists. TcpRoute2 itself always bypasses its own VPN.
- Linux: amd64/arm64 core command-line packages using `config.toml` or `-config <path>`. No separate client or GUI.

There is no iOS or Intel macOS client.

Run the Windows tray client locally:

```powershell
cd windows
dotnet run --project .\TcpRouteTray\TcpRouteTray.csproj
```

Package the Windows tray client:

```powershell
powershell -ExecutionPolicy Bypass -File windows\package-tray.ps1 -Runtime win-x64
```

## Configuration

Windows is portable: the core, original `config.toml`, `TcpRoute2Client.json`, and Geo files live in the application directory. The core accepts `-config <path>`. Android and Windows share the same core TOML. Windows edits TOML files directly from the tray menu and reloads stable saved changes automatically. Android provides Advanced TOML and a visual editor; its local/URL imports and exports operate on the full TOML. Automatic core download and manual installation prompts are not yet implemented.

A typical portable directory is:

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

macOS/Linux core packages contain `TcpRoute2`, the configuration example, and Geo data, without a separate client. A log directory is not required.

Android's visual editor has connection, routing, and Host sections:

- Connections manage upstreams. Direct is the required first route and has no editable connection row. User-added upstreams start at number 2.
- Routing manages multiple whitelist and blacklist sources per upstream: local files, remote HTTP(S) files, Geosite, and GeoIP.
- Hosts load local or HTTP(S) standard hosts files rather than manually mapping individual domains to addresses.
- The visual editor does not expose the SOCKS listener port. It preserves the default or imported `addr`; use Advanced TOML for full editing.

On Android, visual changes remain drafts until **Save configuration** is pressed in the connection, routing, Host, or Advanced TOML editor. Starting VPN/SOCKS, viewing request output, and exporting do not implicitly save drafts. Full configuration import explicitly replaces the configuration. Generated TOML starts with `addr` and adds no `TcpRoute config generated ...` comment.

### Complete Configuration Example

```toml
addr = "127.0.0.1:7070"

# Recover a domain from HTTP Host / TLS SNI when SOCKS supplies an IP.
PreHttpPorts = [80]
PreHttpsPorts = [443]

# A local hosts source.
[[Hosts]]
Path = "hosts/local.txt"
Type = "base"
Credit = 0

# A remote hosts source with periodic refresh.
[[Hosts]]
Path = "https://example.com/hosts.txt"
Type = "suffix"
Credit = 0
UpdateInterval = "24h"

# Preserve Direct in TOML; the visual client hides its editing row.
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

# Ordinary rule files contain one matching entry per line.
[[UpStreams.Whitelist]]
Path = "https://example.com/proxy.list"
Type = "suffix"
UpdateInterval = "24h"

# Geosite: Type names the kind; Path selects lists in local Geo data.
[[UpStreams.Whitelist]]
Type = "geosite"
Path = "geosite:apple,geosite:google"

# GeoIP matches literal destinations when no domain is available.
[[UpStreams.Whitelist]]
Type = "geoip"
Path = "geoip:cn,geoip:private"

[[UpStreams.Blacklist]]
Path = "rules/blocked.list"
Type = "base"
```

### Listener and Upstream Fields

- `addr`: TCP listener shared by SOCKS5, HTTP, and CONNECT; defaults to `127.0.0.1:7070`.
- `PreHttpPorts` / `PreHttpsPorts`: ports where TcpRoute2 reads HTTP Host or TLS SNI to recover domains already resolved to IPs by SOCKS clients. If both fields are omitted, defaults are `[80]` and `[443]`. Use `[0]` to explicitly disable the corresponding type.
- `UpStreams[].Name`: unique route name used in output and the UI.
- `UpStreams[].ProxyUrl`: complete proxy URL parsed by ProxyClient.
- `UpStreams[].DnsResolve`: whether TcpRoute2 resolves destination domains locally. When enabled, matching Hosts entries override DNS; only a miss queries DNS/DoH.
- `UpStreams[].Credit`: route trust level. Low-trust routes are excluded from plaintext ports requiring a higher security level.
- `UpStreams[].Sleep`: delay in milliseconds before attempting this route when no explicit rule matches. This can defer expensive or slow upstreams.
- `UpStreams[].CorrectDelay`: route latency correction in milliseconds.

Direct uses `direct://0.0.0.0:0000`. Unlike the original release, the current core adds built-in Direct when it is absent from TOML. Visual clients preserve an explicit Direct entry without offering an add-direct action or editable Direct parameters.

### Client DNS Correction

Applications using redsocks, Proxifier, or similar tools may pass an already resolved IP to SOCKS. On `PreHttpPorts` / `PreHttpsPorts`, TcpRoute2 reads HTTP Host / TLS SNI and uses the recovered domain for routing and resolution. `DnsResolve=true` makes TcpRoute2 resolve it locally; `false` passes the domain to a remote-DNS-capable upstream. This can avoid relying on the application's original DNS answer, but requires a recoverable domain: traffic without HTTP Host or usable TLS SNI cannot be corrected this way. Use `[0]` to disable either port list.

### DNS Scheduling

With no `[[Dns]]` entries, TcpRoute2 uses system DNS. Standard DoH can be configured in Advanced TOML:

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

Lower priority numbers are queried first; equal priorities preserve TOML order. Each source defaults to a three-second timeout. Timeout, HTTP/TLS failure, unusable addresses, or invalid DNS responses advance to the next source. If all fail, system DNS is the final fallback. A valid NXDOMAIN is final. `Type="system"` can also have an explicit priority; system DNS is not retried if already attempted.

DoH sends DNS wire-format HTTPS POST requests with certificate verification and connects directly to the endpoint without inheriting system proxy environment variables. Endpoint names bootstrap through system DNS, so not all DNS queries are necessarily encrypted. Cancellation stops scheduling. With IPv6 off, only A records are queried; with IPv6 on, A/AAAA candidates are retained. Hosts matches bypass this DNS schedule and use only their configured addresses. Local resolution only applies to upstreams with `DnsResolve=true`. GUIs provide no separate DNS service, and visual saves preserve DNS tables.

### Host Sources

Each `[[Hosts]]` entry identifies a standard hosts file; multiple sources are supported:

- `Path`: local path or HTTP(S) URL. Relative paths resolve against the configuration directory.
- `Type`: domain-key matching mode: `base`, `suffix`, `pan`, or `regex`; defaults to `base`.
- `Credit`: trust level of each address candidate from this source, used in port security filtering.
- `UpdateInterval`: remote refresh interval in Go duration format, such as `30m`, `1.5h`, or `24h`; defaults to `24h`, with a minimum of `1m`.

Each valid hosts line begins with a candidate IP followed by a matching key. Repeated keys retain multiple IPv4/IPv6 candidates:

```text
# hosts/local.txt
192.0.2.10 example.com
192.0.2.11 example.com
2001:db8::10 example.com
```

`base` matches only `example.com`; `suffix` also matches subdomains; `pan` uses `*` and `?` wildcards; `regex` treats the key as a regular expression. Hosts do not support Geosite and do not preselect one IP. For upstreams with `DnsResolve=true`, a hosts match uses only its matching addresses; multiple hosts addresses race with each other. Only a miss queries DNS/DoH. Filtered or unreachable hosts addresses fail without DNS fallback. Upstreams with `DnsResolve=false` retain remote resolution. Local routes with hosts sources recheck resolution instead of using the cached-address shortcut, so hosts edits and removals take effect.

Local hosts changes reload automatically. Remote sources refresh according to `UpdateInterval`; a failed download retains cached data and retries after one minute.

### Whitelists, Blacklists, and Geo Data

`[[UpStreams.Whitelist]]` and `[[UpStreams.Blacklist]]` belong to their containing `[[UpStreams]]`. Each upstream can have multiple sources.

Ordinary rule files use local paths or HTTP(S) URLs in `Path`, with one matching entry per line. Their `Type` supports the same `base`, `suffix`, `pan`, and `regex` modes as Hosts. Remote rule files also support `UpdateInterval`.

The current Geosite format selects lists in `Path`:

```toml
[[UpStreams.Whitelist]]
Type = "geosite"
Path = "geosite:apple,geosite:google"
```

With `Type="geosite"`, each comma-separated selector has a `geosite:` prefix. The core loads `geosite.dat` only from its executable directory and fails at startup if missing, without falling back to the configuration, working, or other asset directories. Android's installation directory is read-only, so the app uses its private Geo directory. Android's visual editor accepts `apple,google` and saves the format above.

GeoIP uses `Type="geoip"` and selectors such as `Path="geoip:cn,geoip:private"`. It matches literal destination IPs when no domain is available and requires `geoip.dat` beside the executable. Visual editors expose list names for both Geo kinds and preserve hidden legacy data paths and refresh intervals; the Geo data manager controls the built-in files.

If any whitelist matches, only upstreams with matching whitelists participate in the race. When both a whitelist and blacklist match the same upstream, the whitelist wins. If no whitelist matches, blacklists exclude their respective upstreams and the remaining routes race. Explicit rule matches bypass `Sleep`.

### Upstream Connections

Named `upProxy` references are case-sensitive and reuse only the referenced `ProxyUrl`, without inheriting routing rules, DNS resolution, credit, or delays. Set this parameter on HTTP/HTTPS/SOCKS URLs; Direct and Xray extension URLs cannot use named `upProxy` with the current ProxyClient. See [the chain example](help.md#21-级联代理-upproxy).

`ProxyUrl` accepts a complete proxy URL supported by [ProxyClient](https://github.com/GameXG/ProxyClient). Android's visual editor selects the proxy type first and validate the complete URL before saving. Direct is maintained automatically; legacy connection types in existing v1.x TOML remain compatible. See [help.md](help.md) for TcpRoute2 configuration and ProxyClient documentation for protocol and TLS parameters.

Proxy URLs can specify an upper proxy with `upProxy`, either as a complete proxy URL or another upstream `Name`; forward and multi-level name references are supported, while unknown names, duplicate names, and cycles fail validation.

For Direct, enable `DnsResolve` to resolve locally, with hosts taking precedence over DNS. With remote-DNS-capable proxies, leave it off to send the domain upstream. `Sleep=80` delays an unruled proxy attempt by 80 milliseconds, giving Direct a head start; explicit whitelist/blacklist routing bypasses this delay. `CorrectDelay` adjusts cached connection timing and is useful when an upstream cannot report the actual destination connection time, as with Shadowsocks.

Android subscription management is available from the top-right **Subscriptions** menu: add a source, refresh and select nodes. See the [Android subscription guide](android/README.md#subscriptions). Windows subscription sources are configured in `TcpRoute2Client.json`, while selected nodes are written into the active TOML as ordinary `[[UpStreams]]` entries. A minimal Windows source is:

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

The response may be a newline-delimited proxy URL list, Base64-encoded list, JSON node array, complete Clash YAML profile, or complete Surge configuration. Open the tray menu, refresh the subscription, and select nodes; generated `Name` values use `sub_<subscription>_<node-id>`, while `Alias` holds the real node name. Client selections and exclusions use stable IDs. See the [Windows subscription reference](windows/README.md#subscriptions) for extracted proxy types, JSON node fields, defaults, locked fields, `UserAgent`, exclusions, and refresh behavior.

An upstream may set `DialProxyFor="target-name"` to also act as that upstream's
front proxy, bound at load time, while retaining its own ordinary routing rules,
credit, and delay. Comma-separated selectors such as `DialProxyFor="tw_0,hk_*"` apply that front
to multiple target names or prefixes. Exact names have priority; a trailing `*`
forces prefix matching. Matching uses `Name`, not `Alias`; standalone `*`
selectors and self-selection are rejected. It is mutually exclusive with URL `upProxy`;
the receiver also cannot specify `upProxy`. Several fronts produce competing
chain candidates. Removing a subscribed front leaves no reference in the
receiver's TOML. See [DialProxyFor](help.md#22-反向声明前置代理-dialproxyfor).

## Trust Levels

`UpStreams[].Credit` and `Hosts[].Credit` filter connections by destination-port security requirements. The lower of route and address credit applies. Plaintext and unknown ports normally require `0`; ports such as HTTPS `443` and SMTP over TLS `465` accept values down to `-500`. For example, `Credit=-100` excludes HTTP port `80` while permitting HTTPS port `443`. See the [port table](netchan/dialchan_filter.go). This is a port-based policy; applications must still verify server certificates.

## Routing Details

- Try multiple DNS address candidates concurrently and use the fastest successful connection.
- Race Direct and proxy routes, with configurable delays, and use the fastest successful connection.
- Cache route/address connection timings for fifteen minutes; try an eligible cached route first and race again if it fails.
- Detect DNS hijacking addresses returned for nonexistent domains and add them to the IP blacklist.
