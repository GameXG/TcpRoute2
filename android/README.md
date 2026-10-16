# TcpRoute2 Android Client

This directory contains the supported Android phone and tablet client for TcpRoute2. Phone and tablet form factors use the same ARMv8 (`arm64-v8a`) APK and configuration contract; 32-bit ARM and x86 Android devices are not supported.

Android has two practical modes:

- Proxifier mode: run TcpRoute2 as a local SOCKS5 listener, then point Proxifier for Android at `127.0.0.1:7070`.
- Built-in VPN mode: run TcpRoute2 with the bundled `tun2proxy` bridge so the app owns VPN permission, TUN lifecycle, and TUN-to-SOCKS routing.

The Android entry point is a native Kotlin app with:

- a main activity for Proxifier SOCKS and built-in VPN controls,
- English, Simplified Chinese, and Traditional Chinese UI with system-language matching and an in-app language selector,
- an always-visible visual config editor with explicit save actions,
- a top-right menu for Advanced TOML, local import, online import, export, and language selection,
- local files, remote files, `geosite:gfw` domain references, and `geoip:cn` IP references,
- a `VpnService` that owns Android VPN permission and TUN lifecycle,
- a foreground service for the local SOCKS mode,
- config storage under the app files directory,
- subscription management from the top-right menu, with persistent sources, cached nodes and TOML imports,
- per-ABI TcpRoute2 core binaries plus the pinned `tun2proxy` Android library, isolated runner, and JNI bridge.

## Subscriptions

Open **Subscriptions** in the top-right menu. Add a named HTTP(S) source, set an
optional User-Agent and save. **Refresh** reads URI lists, Base64 lists, JSON node
arrays, complete Clash YAML profiles or Surge profiles. **Nodes** selects the
imported routes; select/clear all is available, and long-pressing a node edits its
override JSON. The source editor also accepts an override map keyed by node ID.

Source defaults include `DnsResolve`, `Credit`, `Sleep` (milliseconds),
`CorrectDelay`, `UpProxy` and `DialProxyFor`. Checked locks preserve existing TOML
values during refresh without requiring a matching default; unlocked fields may be overwritten. New nodes use defaults,
node options and overrides. `UpProxy` and `DialProxyFor` cannot be combined.

`DialProxyFor="tw_0,hk_*"` makes the declaring node the front proxy for every
matched target `Name`. Commas separate selectors; an exact name wins, otherwise
the selector matches a prefix. A trailing `*` forces prefix matching. Matching
is case-sensitive and does not use `Alias`; overlapping matches are deduplicated.
The direction is `TcpRoute2 -> declaring node -> matched node -> destination`.
Standalone `*` selectors and any selection of the declaring node itself are
rejected; nonempty prefixes such as `tw_0*` remain valid.

Imported nodes are real `[[UpStreams]]` entries named
`sub_<subscription-name>_<stable-node-id>` in the saved TOML. `Alias` holds the
real node name, including Chinese, and is retained through visual edits. The
visual editor displays aliases while routing references keep using `Name`.
Selections and exclusions both store stable IDs; old name-based exclusions
migrate using cached nodes. Source-prefix history stays in client subscription
storage. Refresh updates unlocked fields and connection details,
adds new nodes and preserves locked values, routing rules and comments. Old
readable upstream names and encoded header comments migrate on update, with chain references
updated together. Deselection deletes the known node's TOML table and nested rules;
reselecting imports current values and defaults. Refresh retains deselection by stable ID.
Disappeared nodes and removed sources retain their tables with `Remove=true` for
recovery. Startup protects nodes absent from a verified pool; an unavailable cache
retains saved TOML. Explicit `upProxy` references must remain valid when deleting.
A front node races independently with its own `Sleep`; when accelerating another
route, only the receiving route's `Sleep` controls the attempt.

Updates default to manual (`0`). An interval of at least 15 minutes opts into
Android's periodic background updates; Android controls their exact execution
time. Failed downloads, unsupported conversions and invalid edits retain the
previous configuration and cache. Saved changes apply on the next session start;
restart an existing VPN/SOCKS session to use the new routes.

**Enable system proxy** advertises the local HTTP proxy on the VPN network on
Android 10+. It does not grant privileged global proxy control.

## Layout

```text
android/
  settings.gradle.kts
  build.gradle.kts
  app/
    build.gradle.kts
    src/main/jniLibs/<abi>/
    src/main/assets/bin/<abi>/
    src/main/AndroidManifest.xml
    src/main/java/io/tcproute/android/
```

## Binary Placement

Place Android binaries in `jniLibs` before packaging. Android reliably allows execution from the native library directory:

The Android package includes checksum-pinned `assets/geosite.dat` and `assets/geoip.dat`. The overflow menu's Geo data manager can update the verified pair daily, weekly, manually, or not at all; any download or checksum failure keeps the previous pair. Quick UI hides Geo data paths and per-rule update intervals to prevent accidental changes; Advanced TOML preserves access to custom paths.

```text
app/src/main/jniLibs/arm64-v8a/libtcproute.so
app/src/main/jniLibs/arm64-v8a/libtun2proxy.so
app/src/main/jniLibs/arm64-v8a/libtun2proxy_runner.so
app/src/main/jniLibs/arm64-v8a/libtcproute_bridge.so
```

The old asset layout is still supported as a development fallback, but modern Android releases may prevent executing files copied from app data:

```text
app/src/main/assets/bin/arm64-v8a/TcpRoute2
```

With `jniLibs`, the app executes the packaged native-library files directly. With the asset fallback, it copies matching binaries to internal storage, marks them executable, writes a runtime TOML config, and starts:

```text
TcpRoute2 -config <runtime.toml>
```

VPN mode passes the `VpnService` TUN file descriptor through JNI to an isolated native runner process. Keeping `tun2proxy` outside the Android app process prevents its forced-exit fallback from terminating the UI after a normal VPN stop. The runner uses the equivalent of:

```text
tun2proxy-bin --tun-fd <fd> --close-fd-on-drop true \
  --proxy socks5://127.0.0.1:<port> --dns over-tcp --exit-on-fatal-error
```

The service tracks the runner PID, terminates and reaps it before closing the TUN descriptor, and invalidates callbacks from older VPN sessions. Stop and destroy paths share the same idempotent resource cleanup.

## Modes

The optional **VPN network proxy** control advertises the local HTTP/CONNECT
proxy on the same port as SOCKS5 using the public VPN network proxy API on
Android 10+. It requires no ADB or Root and applies only while VPN is active.
Standalone mode provides a local SOCKS/HTTP listener. The overflow menu's
**Wi-Fi proxy settings** action shows the saved listener port, copies the local
host, and opens system Wi-Fi settings for manual configuration. Users must also
remove that manual Wi-Fi proxy when they stop the listener. The app does not
write global proxy settings or request privileged proxy permissions. Apps can
ignore network proxy settings; VPN mode remains the traffic-capture option.
Normal core output is kept in bounded session memory, not written to log files.

VPN DNS-over-TCP uses the synthetic 198.18.0.1 gateway and TcpRoute2's configured
DNS/DoH scheduler. The core is excluded from VPN capture so bootstrap/fallback
DNS uses the underlying network. The gateway currently supports A/AAAA records.

- `Start/Stop SOCKS`: one dynamic button controls TcpRoute2 as a foreground SOCKS service on the configured endpoint.
- `Request log`: opens a live, auto-refreshing view of the current background TcpRoute2 SOCKS process output. A fresh session log starts with each SOCKS launch; the page shows up to the most recent 256 KB and closes with the system Back action.
- `Start/Stop VPN`: one dynamic button controls VPN permission, TUN, TcpRoute2, and the `tun2proxy` bridge.

VPN and standalone SOCKS are mutually exclusive because both use the configured local port. Starting one mode stops the other first.

## Connection / Routing Config

The main screen edits the app's `default.toml` without requiring an external editor. Configuration is organized into three sections:

- `Connection configuration`: the built-in direct route and user-added proxy upstreams. Direct has no editable connection row, and the visual editor has no dedicated SOCKS listen-port field.
- `Rule configuration`: select an upstream, then edit that upstream's multiple whitelist and blacklist sources, followed by Android VPN application configuration.
- `Host configuration`: load and edit local or remote standard hosts sources.

The visual editor provides:

- `Add Proxy`: adds a proxy upstream template such as `socks5://127.0.0.1:1080`.
- `Whitelist / Blacklist`: adds a local file, remote file, Geosite, or GeoIP rule below the selected upstream. Geosite accepts compact names such as `apple,google` and writes `Type="geosite"`, `Path="geosite:apple,geosite:google"`. Android uses the app-private Geo directory because the native-library installation directory is read-only. Legacy custom local/remote Geosite data paths remain compatible.
- `Host configuration`: loads a local or HTTP(S) hosts source with `Type=base|suffix|pan|regex`, `Credit`, multiple address candidates, and remote refresh settings. For `DnsResolve=true` upstreams, matching hosts addresses override DNS; only misses query DNS/DoH. Multiple matching hosts addresses race with each other, and filtering or connection failure does not fall back to DNS. `DnsResolve=false` leaves resolution to the upstream.
- `Application configuration`: opens application selection in a dialog for allowlist or bypass policies instead of permanently expanding every installed app in the main page.
- `Advanced TOML`: the primary configuration surface for experienced users. It opens from the top-right menu and edits the same complete config text directly; the visual editor is an optional helper for common fields.
- `Save configuration`: appears in the connection, rule, and Host sections. Visual changes remain drafts until one of these buttons is pressed; Advanced TOML has its own Save configuration action. Starting SOCKS/VPN and exporting do not write unsaved drafts.

Generated TOML begins directly with `addr`; the clients no longer add a `TcpRoute config generated ...` comment. Existing copies of that historical marker are hidden in Advanced TOML and disappear from the stored file on the next explicit save without removing user-authored comments.

## Built-in VPN App Routing

The built-in VPN mode supports three mutually exclusive app routing policies:

- `VPN all apps`: route every app through the VPN except TcpRoute2 itself.
- `VPN only selected apps`: allowlist mode; only selected apps enter the VPN. The allowlist must contain at least one app.
- `Bypass selected apps`: blocklist mode; selected apps bypass the VPN.

TcpRoute2's own package is always removed from saved selections and silently bypassed. In allowlist mode this is enforced by never adding TcpRoute2 to the allowlist; in all-app and blocklist modes it is enforced with `addDisallowedApplication`.

These app selections apply only to the built-in VPN tunnel. While standalone SOCKS mode is active, the app hides these controls and shows that application selection is owned by the SOCKS client or Proxifier instead.

## Language

The language menu in the top-right corner supports:

- Follow system (default),
- English,
- Simplified Chinese,
- Traditional Chinese.

System matching maps Chinese locales using script and region: `zh-Hant`, Taiwan, Hong Kong, and Macau use Traditional Chinese; other Chinese locales use Simplified Chinese. Unsupported system languages fall back to English. A manual selection is stored locally and applied to both the main UI and foreground-service notifications.

## Build

### Local prerequisites

- JDK 17+
- Gradle once, only to generate `gradle-wrapper.jar`
- Go 1.25+
- Android NDK 28.2+
- `curl` and `unzip` for the pinned `tun2proxy` release

Generate the Gradle wrapper once:

```bash
bash android/generate-gradle-wrapper.sh
```

Build the TcpRoute2 Android core binaries:

```bash
bash scripts/build-android-tcproute.sh
```

Fetch the checksum-pinned official `tun2proxy v0.8.2` Android libraries and build the JNI bridge:

```bash
bash android/fetch-tun2proxy-android.sh
bash android/build-jni-bridge.sh
```

Package a debug APK:

```bash
bash android/package-debug.sh
```

The collected debug APK is written to `../.build/android/TcpRoute2-debug-android-arm64.apk`. Release packaging writes only the versioned distributable APK to the repository-root `dist/`; this platform directory does not retain packaged artifacts.

If you want to run Gradle manually after staging binaries:

```bash
cd android
./gradlew assembleDebug
```

## CI Build

The repository also includes `.github/workflows/android-build.yml`. It installs Go and JDK, builds the Android TcpRoute2 core binary, generates the Gradle wrapper, assembles the debug APK, collects it under the repository-root `.build/android/`, and uploads that debug artifact.

## Current Limits

TcpRoute2's SOCKS5 server implements both TCP `CONNECT` and UDP `ASSOCIATE`. Ordinary UDP is routed through the selected upstream; DNS remains configured over TCP in the built-in VPN. The connection panel keeps STUN and QUIC disabled by default to reduce address disclosure and UDP fallback. Enabling **Allow STUN** permits STUN payloads and common STUN ports, while **Allow QUIC** permits UDP port 443. These settings apply to VPN and standalone SOCKS sessions and are fixed for the lifetime of a running session.

IPv6 is also disabled by default. Enabling **Enable IPv6** allows locally known IPv6 targets and DNS candidates; in VPN mode it additionally installs an IPv6 address and default route on the Android VPN interface. All three controls persist locally and must be selected before starting either VPN or standalone SOCKS.

`tun2proxy v0.8.2` is distributed under the MIT license. Its notice is packaged at `app/src/main/assets/THIRD_PARTY_NOTICES.txt`.
