# TcpRoute2 Windows Client

Windows distribution is a portable x64/ARM64 ZIP with a tray-only client.
There is no main or configuration window. Left-clicking the tray icon opens
only the activity log; closing that window hides it without stopping TcpRoute2.
The log wraps to the window width, including long unbroken messages. Export
retains the original log lines.

## Tray Menu

Right-click the tray icon for status, Start/Stop, Restart, connectivity checks,
network mode, outbound interface, runtime settings, configuration files, Geo
data updates, log export, and Quit. Commands and selected settings live in this
menu. Upstreams, routing rules, Hosts, and DNS are edited in TOML files.

**Copy proxy** offers the HTTP proxy URL or environment variable commands for
CMD and PowerShell. The commands set `HTTP_PROXY` and `HTTPS_PROXY` to the HTTP
listener and `ALL_PROXY` to its `socks5h://` address, using the running listener
port (or the configured port when stopped). Paste and execute the commands in
the matching terminal; they affect only that terminal and its child processes.

Proxy mode exposes SOCKS5, HTTP, and CONNECT on one listener. Optional Windows
system proxy settings are restored on stop. TUN / VPN (Wintun) mode uses the
pinned xray-core TUN stack and requests administrator privileges through UAC.
Modes are mutually exclusive; system proxy and LAN controls are unavailable in
TUN mode. Stop before switching modes, interfaces, STUN, QUIC, or IPv6.

Outbound interface defaults to **Automatic**. The menu lists interfaces whose
status is **UP**, without requiring a gateway for inclusion. Manual selection
is persisted by interface GUID. GUID comparison accepts case and braces. Startup
uses the same UP interface inventory as the menu and maps the GUID to its IPv4
interface index, including logical/hidden interfaces. A selected interface must have a connected IPv4 default route and usable
DNS. An unavailable manual selection produces a readable error; choose Automatic
to allow default-route selection.

Wintun requires the bundled architecture-matched `wintun.dll`. If Windows has
disabled its IPv6 binding and IPv6 is off in TcpRoute2, startup skips IPv6
interface configuration. Enabling IPv6 requires that binding. When IPv6 is
available, TUN captures it so the core can enforce the IPv6 policy.
PowerShell progress is suppressed and failures are shown as readable text.

TUN address and route setup reuses existing entries on the owned adapter and
corrects their prefix/metric when needed. Newly added entries use ActiveStore,
so repeated starts and partially completed setup do not fail on duplicate IPs.
Listener readiness uses a complete local SOCKS5 UDP ASSOCIATE exchange without
contacting an upstream or sending UDP traffic.

Wintun provides process bypass for upstream proxy engines as described below,
but no general per-application routing editor, comprehensive leak blocking, ICMP/ping,
or automatic network handover. The DNS gateway supports A/AAAA; other record
types receive NOTIMP. Interface loss stops the session; restart to select again.

## Configuration Files

The portable directory contains:

```text
TcpRoute2_Windows_x64.exe or TcpRoute2_Windows_arm64.exe
TcpRoute2.exe
TcpRoute2Client.json
config.toml
config.toml.example
geosite.dat
geoip.dat
wintun.dll
```

Use **Configuration > Edit file** to open a TOML profile in Notepad.
Use **Runtime settings > Edit client configuration** for client preferences.
TOML changes are applied automatically after two stable checks, about 1.5 seconds.
External edits to `TcpRoute2Client.json` require **Reload now** by default;
`AutoReloadClientSettings=true` opts into automatic client JSON reload.
A running session restarts to apply changed configuration. If startup fails,
the client tries to restore the previous running configuration, retains the
user's edited file, and reports the error in the activity log. Stopped sessions
remain stopped. **Reload now** is also available.

Client preferences are stored in `TcpRoute2Client.json`:

```json
{
  "SelectedConfigId": "config.toml",
  "ExecutablePath": null,
  "AllowLan": false,
  "SystemProxyEnabled": false,
  "AllowStun": false,
  "AllowQuic": false,
  "EnableIpv6": false,
  "UseWintun": false,
  "AutoReloadClientSettings": false,
  "OutboundInterfaceId": "",
  "TunAutoBypassLocalProxies": true,
  "TunBypassProcesses": [],
  "TunBypassRules": [],
  "Subscriptions": [],
  "Appearance": "system",
  "Language": "system"
}
```

`UseWintun=true` selects TUN. An empty `OutboundInterfaceId` selects Automatic;
the tray menu can select an UP interface and write its GUID. Appearance accepts
`system|light|dark`, language accepts `system|en|zh-Hans|zh-Hant`. STUN, QUIC, and
IPv6 default off.
STUN permits recognized discovery traffic (common STUN ports or STUN packet
signatures); QUIC permits UDP port 443, rather than recognizing QUIC on every
possible port; IPv6
permits locally known IPv6 destinations and DNS candidates. A remote upstream
controls the address family when it resolves the destination itself.

### TUN Destination Bypass

`TunBypassRules` in `TcpRoute2Client.json` adds destination IP/CIDR and port
exceptions independently of process bypass. It defaults to an empty array.
For example:

```json
"TunBypassRules": [
  { "Destinations": ["192.168.0.0/16", "10.0.0.0/8"] },
  { "Network": "udp", "Ports": ["123"] },
  {
    "Network": "tcp",
    "Destinations": ["203.0.113.10", "2001:db8::/32"],
    "Ports": ["443", "8000-8100"]
  }
]
```

The first rule bypasses the listed IPv4 networks on all TCP/UDP ports. The
second bypasses UDP destination port 123 on any IP. The third requires TCP,
one of the listed destination addresses/networks, and one of the listed ports;
replace its documentation addresses with your upstream server addresses.

`Network` accepts `any` (default), `tcp`, or `udp`. A rule must supply at least
one destination or port. Omitted or empty `Destinations` means any destination;
omitted or empty `Ports` means any port. Values within each list are alternatives;
the protocol, address, and port conditions in the same rule must all match.
Rules are alternatives and evaluated in order. Ports are strings from `1` to
`65535`, with inclusive ranges such as `8000-8100`. Destinations are IPv4/IPv6
addresses or CIDRs, not domains, source addresses, or local listening ports.
Host bits in a CIDR are masked. IPv4-mapped literals are accepted as IPv4; use
ordinary IPv4 CIDRs rather than IPv4-mapped IPv6 CIDRs. Zone IDs are unsupported.

Matches are checked before process lookup and forwarded directly through the
selected physical interface. Explicit bypass rules override routing rules and
the application STUN/QUIC/IPv6 restrictions, just like upstream-process bypass;
unmatched flows retain existing policy. The synthetic TUN DNS gateway still
uses the core resolver. Activity output identifies the matched rule number.
These rules affect only captured TUN TCP/UDP connections and do not change
SOCKS/HTTP proxy-mode routing or install persistent Windows routes.

Invalid rules reject the configuration. Apply saved client JSON changes with
**Reload now**, or enable `AutoReloadClientSettings`; failed startup retains the
previous running configuration.

### TUN Upstream Process Bypass

`TunAutoBypassLocalProxies` defaults to `true`. The core finds the Windows
process listening on a configured local HTTP/HTTPS/SOCKS upstream endpoint,
including nested `upProxy` URLs and name references. It recognizes `localhost`,
loopback IPs, and literal IPs assigned to this computer. Custom local hostnames
and other upstream URL types require an explicit process entry. Listener
ownership is refreshed for each new flow, so a restarted engine is recognized
without retaining stale PIDs.
TCP ownership uses the full local/remote connection tuple; UDP ownership uses
the bound endpoint and rejects ambiguous owners, following the Windows
[TCP](https://learn.microsoft.com/en-us/windows/win32/api/tcpmib/ns-tcpmib-mib_tcprow_owner_pid)
and [UDP](https://learn.microsoft.com/en-us/windows/win32/api/udpmib/ns-udpmib-mib_udprow_owner_pid)
owner tables.

When a captured TCP/UDP connection belongs to that process, the core forwards it
directly through the selected physical interface, without upstream selection,
HTTP/SNI inspection, or application STUN/QUIC/IPv6 restrictions. This exception
keeps an upstream engine's own transport working; flows without an explicit
destination or process bypass still follow the switches and routing rules.
Bypassed flows appear in the activity log.
The synthetic TUN DNS gateway continues to use the core's physical DNS resolver.
The mechanism forwards captured connections; it does not install WFP rules.

If the listener and network engine are different processes, add the actual
engine executable to `TunBypassProcesses` in `TcpRoute2Client.json`, for example:

```json
"TunBypassProcesses": ["C:\\Proxy\\engine.exe", "another-engine.exe"]
```

A full executable path matches that path, ignoring case; a filename matches all
processes with that filename. Wildcards and automatic child-process inheritance
are not supported. A matched process's captured network traffic bypasses proxy
rules, so use the engine executable rather than a general-purpose launcher.
Apply saved client JSON changes with **Reload now**, or enable
`AutoReloadClientSettings` for automatic reload and session restart.
Connections whose owner cannot be identified, including UDP endpoints shared by
different PIDs, are rejected while process bypass is active unless an explicit
destination rule matches, to prevent recapture
loops. This is a proxy-loop safeguard, not a system-wide firewall or leak blocker.

Network recovery and Geo update metadata are stored separately in the client
runtime directory under the system temporary directory. Existing recovery
metadata in older `TcpRoute2Client.json` files is retained during migration.

Upgrades also import the former `TcpRouteClient.json` when the new preferences
file is absent. The old Wintun/runtime identifiers remain stable for recovery.

All core settings, including listener address, upstream URLs, nested per-route
whitelists/blacklists, Hosts, and optional DoH/system DNS, remain in TOML.
The file is passed to the core without conversion to a visual model.
See [the configuration reference](../help.md).

The client preferences file `TcpRoute2Client.json` is not automatically
reloaded by default. This avoids applying a partial external JSON edit while
TUN is active. Enable `AutoReloadClientSettings` from the tray runtime settings
only when external preference edits should reload and restart the session;
TOML configuration reload remains a separate path.

### Subscriptions

Subscription sources are configured in `TcpRoute2Client.json`. Selecting a
cached node in the tray writes a real `[[UpStreams]]` entry into the currently
selected TOML profile and reloads it. That TOML is the routing source of truth:
users can edit the imported upstream's parameters and rules, and other routes
can reference its `Name` through `upProxy`. A successful subscription update
automatically imports new nodes, updates same-name nodes, and deletes nodes no
longer present. It synchronizes the active profile and other profiles already
containing that subscription, without adding it to unrelated profiles.
Switching profiles shows the nodes
actually imported into that profile. The old `SelectedNodes` client JSON field
is only a cache-selection hint; it does not inject nodes into the running core.

`Url` may also be a local relative file such as `sub_clash.txt`. Local files are
resolved beside the TcpRoute2 client executable and never leave that directory;
they support the same URI, Base64, JSON, Clash and Surge formats.

The response may be a newline-delimited proxy URI list, the same list encoded as
Base64, a JSON node array, a complete Clash YAML profile, or a complete Surge
configuration. Clash `proxies:` entries and Surge `[Proxy]` entries are extracted
into subscription nodes. Common HTTP/HTTPS/SOCKS5, Shadowsocks, Trojan,
and VMess entries are supported; unsupported proxy types are rejected while the
previous cache remains available. The subscription downloader follows the Windows
system proxy configuration by default; set `UseProxy` to `false` for a direct
connection. `UserAgent` is optional and is sent exactly as the HTTP header value
when the provider requires a specific client header, for example `clash`:

```json
"Subscriptions": [
  {
    "Id": "mysub",
    "Name": "mysub",
    "Url": "https://example.invalid/proxies.txt",
    "UseProxy": true,
    "UserAgent": "Mihomo/Clash Verge",
    "SelectedNodesAll": false,
    "UpdateIntervalMinutes": 0,
    "Defaults": {
      "DnsResolve": false,
      "Credit": 0,
      "Sleep": 80,
      "CorrectDelay": 0
    },
    "LockedFields": ["Sleep", "Credit"],
    "Overrides": {}
  }
]
```

JSON nodes use `Id`, `Name`, `ProxyUrl`, and optional `Options` with
`DnsResolve`, `Credit`, `Sleep`, `CorrectDelay`, and `UpProxy`. `Overrides` is
the local per-node override map. Values resolve in this order: source defaults,
node options, then local override. A field in `LockedFields` retains its existing
TOML value on refresh; no matching `Defaults` entry is required. New or missing
fields use the normal defaults, node options and overrides. `UpProxy` may be a complete proxy URL or an existing
upstream name, subject to the normal `upProxy` validation.

Subscription defaults, node options, and overrides also accept `DialProxyFor`,
and it can be locked with `LockedFields`. To make selected subscription nodes
front the named upstream `exit`, configure:

```json
"Defaults": { "DialProxyFor": "exit", "Sleep": 80 },
"LockedFields": ["DialProxyFor"]
```

`DialProxyFor` also accepts comma-separated `Name` selectors, such as
`"tw_0,hk_*"`. Each selector uses an exact name first, then a prefix when no
exact name exists; a trailing `*` explicitly selects a prefix group. Matches
are case-sensitive, exclude disabled nodes from chains, and never use `Alias`.
Setting this on A makes A the front proxy for every matched node, so the path
is `TcpRoute2 -> A -> matched node -> destination`. Overlapping matches are
deduplicated; a selector with no match rejects the configuration.
The standalone `*` selector is forbidden, including inside a list. Exact,
prefix, or list selection of the declaring node itself is also rejected.

The imported node then contains `DialProxyFor="exit"` in the TOML; `exit` keeps
its own `ProxyUrl` without an `upProxy` reference to the subscription node.
Deleting the front node can therefore leave `exit` intact and loadable. Do not
combine `DialProxyFor` and `UpProxy` options or a URL `upProxy`; the receiver
cannot have an explicit `upProxy` either. Several front nodes create competing
chain candidates. Each front node also retains ordinary routing under its own
rules, credit, and delay; the composed chain uses the receiver's policies and
delays. The receiving protocol must be
HTTP/HTTPS/SOCKS with the current ProxyClient. See [the core reference](../help.md#22-反向声明前置代理-dialproxyfor).

The program manages `Name` as `sub_<subscription-name>_<stable-node-id>` and
stores the real node name in `Alias`, for example:

```toml
[[UpStreams]]
Name="sub_mysub_9824086d034c33939dc44b77"
Alias="Hong Kong 01"
ProxyUrl="socks5://proxy.example:1080"
```

`upProxy` and `DialProxyFor` reference `Name`; `Alias` is a readable label and
does not change routing identity. When the source `Name` is empty, its `Id` is
used; spaces and punctuation in the source prefix become underscores. These
prefixes are reserved for managed nodes. Selecting the same node again does not
duplicate it. Client selections and profile exclusions both store stable IDs.
Legacy name-based exclusions migrate using cached node names. Source-prefix
history remains in the client cache. Existing readable upstream names migrate
on refresh with chain references updated. Legacy encoded comments are read for
migration and removed on update; no new metadata comments are generated.

Matching uses the case-sensitive node name within a subscription. Changes to
the server address, authentication, or provider ID do not replace a same-name
node's stable upstream name. Duplicate node names and ambiguous source prefixes
reject the update. `Alias` retains the node name even when the cache is missing.

On refresh, connection addresses, credentials, unlocked URL options and unlocked
parameters follow the subscription and client options. `LockedFields` preserves
existing TOML values; it is the only mechanism for preventing those fields from
being overwritten. Unknown fields, comments, and nested whitelist/blacklist tables
are preserved. New nodes receive defaults, node options and overrides. The upstream name is always managed by
the program; changing a subscription name also updates matching `upProxy`
references, including nested proxy URLs. Deselection deletes that profile's
owned TOML table and its nested rules and records its stable ID in `ExcludedNodesByProfile`, so refresh
will not re-add it. Exclusions apply only to that profile; selecting it again
clears the exclusion and imports current subscription values and defaults. Explicit
`upProxy` references must remain valid; deleting a referenced parent is rejected
without changing the file. `DialProxyFor` leaves receivers available through their
original URLs when their front node is deleted. A front node uses its own `Sleep`
when racing independently; its use inside another route's chain adds no front-node
delay, and that route races using its own `Sleep`.
Removing a subscription source from the client configuration while the client
is running cleans up its owned entries across profiles, retaining other
subscriptions and manual entries.

An imported entry and a chain can be edited in the TOML as follows:

```toml
[[UpStreams]]
Name="sub_mysub_node1"
ProxyUrl="socks5://127.0.0.1:1080"
DnsResolve=false
Sleep=80

[[UpStreams]]
Name="chain"
ProxyUrl="https://proxy.example.com:443?upProxy=sub_mysub_node1"
```

The response is limited to 4 MiB and must contain supported proxy URLs; Direct
nodes are rejected. Each source allows up to 2048 nodes and a profile up to
4096 imported nodes; proxy-chain expansion limits still apply. TOML edits use
the bundled core's TOML parser, preserve unrelated text,
and refuse to overwrite a file changed concurrently during the operation.

Updates are cached under the client runtime directory. `UpdateIntervalMinutes`
defaults to `0`, which keeps subscription updates manual. Setting a positive
value enables background updates at that interval; the tray menu always also
offers an immediate manual update. Network, HTTP status, parsing, validation,
and startup failures keep the last valid cache. Nodes missing from the subscription pool remain as complete
TOML entries with `Remove=true`; their rules, parameters, comments, and chain
fields remain available for manual recovery while the core skips them. An
explicit JSON `[]` is a valid empty subscription and marks its nodes removed;
an empty or malformed response
is an update failure and cannot erase the current configuration.
Startup protects subscription entries missing from a verified pool or configured
source with `Remove=true`. An unavailable cache retains saved TOML without a network
request. An update that cannot start restores the prior
running configuration and the pre-update TOML unless the user edited the file
again. Multi-profile changes are prepared and validated before writing, and
failed writes roll back without overwriting concurrent user edits. There is no
temporary subscription-upstream file or runtime injection.

For `DnsResolve=true`, matching Hosts entries override DNS; only misses query
DNS/DoH. Multiple matching hosts addresses race with each other, retaining
source Credit. Filtering or connection failure does not fall back to DNS.
`DnsResolve=false` keeps remote upstream resolution.

Geosite/GeoIP files stay beside the core. Update frequency is controlled only
by each TOML `[[GeoData]].UpdateInterval`. The default is manual (`0`); a
positive duration such as `24h` enables background updates for that source.
The former client JSON `GeoDataInterval` field is ignored and no longer saved.
Failed downloads or verification retain the previously installed files.

The core TOML can define separate Geo data sources. When no `[[GeoData]]` is
present, the bundled verified pair and built-in release addresses are used. A
source uses `Type="geosite"` or `Type="geoip"`, `URL`, optional `ChecksumURL`,
and `UpdateInterval`; `0` means manual and is the default. If `URL` is omitted,
the built-in address for that type is used. If `ChecksumURL` is omitted or
empty, no checksum file is fetched or verified for that source. An explicit
`ChecksumURL` enables SHA-256 verification. With no `[[GeoData]]` entries,
manual updates use the built-in addresses and their built-in checksum URLs.

```toml
[[GeoData]]
Type="geosite"
URL="https://example.invalid/geosite.dat"
ChecksumURL="https://example.invalid/geosite.dat.sha256sum"
UpdateInterval="0"

[[GeoData]]
Type="geoip"
URL="https://example.invalid/geoip.dat"
ChecksumURL="https://example.invalid/geoip.dat.sha256sum"
UpdateInterval="24h"
```

The TOML block is the source of truth for data addresses and update frequency;
client JSON stores only cache and status metadata.

Activity reads UTF-8 stdout/stderr into bounded memory: at most 1,000 lines,
2,048 characters each. Only explicit exports create log files; normal operation
creates no `logs/` directory.

## Build And Package

```powershell
cd windows
dotnet run --project .\TcpRouteTray\TcpRouteTray.csproj
powershell -ExecutionPolicy Bypass -File .\package-tray.ps1 -Runtime win-x64
```

On macOS, cross-build or package with:

```bash
dotnet build windows/TcpRouteTray/TcpRouteTray.csproj -p:EnableWindowsTargeting=true
bash windows/package-tray.sh win-x64
bash windows/package-tray.sh win-arm64
```

On Windows, run the native menu regression tests with:

```powershell
dotnet run --project windows/TcpRouteMenuSelfTest/TcpRouteMenuSelfTest.csproj
```

The Windows Tray Menu workflow runs these tests on a Windows runner. They
exercise the WinForms dropdown click path, repeated and asynchronous node
selection, paginated menus, normal commands, and keyboard/focus/outside-click
close reasons. Configuration-only self-tests and macOS cross-builds cannot
exercise these native menu events.

The core must first exist under `../.build/release-core`, or be selected with
`TCPROUTE_EXE` for the shell packaging script. Packaging verifies downloaded
Geo data and Wintun. Versioned ZIPs and `SHA256SUMS.txt` belong under root
`dist/`; expanded packaging trees use temporary directories.

`TcpRoute2Client.ps1` remains a debugging/automation fallback. The old
`MainWindow`, `AdvancedTomlWindow`, and visual editor sources are historical
and excluded from the tray client's build.

Cross-builds and portable self-tests do not verify Windows UAC, drivers,
registry changes, or live routing. These require a Windows machine.
