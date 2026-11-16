# Changelog

All notable changes to ParsGate will be documented in this file.

## [0.8.0] - 2025-08-31

### Fixed
- **TASK 1**: Pinned core versions (Xray-core v25.8.31, sing-box v1.12.4) with SHA256 verification in CI. Added config validation step using `sing-box check` and `xray run -test` for all supported protocols (VLESS+Reality, VLESS+TLS+WS, VMess+WS, Trojan, Shadowsocks) with IranDirect and Split Tunnel combinations.
- **TASK 2**: Fixed routing loop by reordering Sing-box route rules: (1) xray.exe process → direct, (2) VPN server IPs → direct, (3) loopback/private ranges → direct, then QUIC block, DNS hijack, Iran rules, split tunnel. Added domain resolution for VPN server with direct DNS rule. Unified Xray SOCKS port constant (10808).
- **TASK 3**: Rewrote ConfigParser with full Reality/TLS/Transport support. Reality now emits `realitySettings` (publicKey, shortId, fingerprint, spiderX) not `tlsSettings`. VLESS carries `flow` (e.g., xtls-rprx-vision) and encryption=none. Parse all standard share-link parameters (type, security, sni, fp, pbk, sid, spx, flow, alpn, path, host, serviceName, mode, headerType). Percent-decode query values and #name fragment (including Persian). Reject malformed URIs with Persian error. Added xUnit tests with 3+ example links per protocol.
- **TASK 4**: Fixed Iran rule-sets. Removed fake geoip.dat/geosite.dat copy step. Using sing-box rule_set (local binary .srs) for geoip-ir and geosite-ir from Chocolate4U releases. Replaced deprecated geoip/geosite rule types with rule_set references. Removed invalid geosite-ir domain_suffix from DNS rules. Graceful degradation if .srs files missing (skip rules, Persian warning via LiveRegion). Rules path absolute based on app base directory.
- **TASK 5**: Fixed DNS and UDP. `dns.final` now uses TAG "remote-dns" (not "dns-out"). Route DNS queries with proper hijack/DNS action so no DNS leaks outside tunnel. Removed `udp_over_tcp` from SOCKS outbound (Xray inbound has udp:true). Validate CustomRemoteDns/CustomDirectDns with fallback to defaults.
- **TASK 6**: Redesigned Kill Switch. No global block rule. Instead: save current default outbound policy, set default to block, add explicit ALLOW rules for xray.exe, sing-box.exe, loopback, DHCP, VPN server IPs (IPv4/IPv6), TUN subnet. Prefix rules with `ParsGate_KS_`. State file in %ProgramData%\ParsGate\. RestoreNetwork() restores saved policy and deletes rules. Called on disconnect, app exit, SessionEnding, crash handler, startup (anti-lockout), and via `ParsGate.exe --restore-network`. Inno Setup uninstall runs `--restore-network`. All netsh errors logged. IPv6 supported. Persian error if enable fails.
- **TASK 7**: Fixed Split Tunneling logic. Replaced magic strings with enum `SplitTunnelMode` (ExcludeSelected, OnlySelected). Migrated legacy values. ExcludeSelected: selected → direct, route.final = proxy. OnlySelected: selected → proxy, route.final = direct. Always-direct rules and QUIC block stay first. Iran-direct precedence preserved. Added unit tests for both modes.
- **TASK 8**: Core process lifecycle. Replaced fixed 800ms delay with real readiness check (poll Xray SOCKS port up to 10s). Streaming log output to %ProgramData%\ParsGate\logs\ (5MB cap). Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. Exited event handler: on unexpected core death → set IsConnected=false, stop other core, restore network, announce Persian message via LiveRegion. SemaphoreSlim for async safety. DisconnectAsync safe against double-clicks. App.OnExit and SessionEnding handle Windows shutdown without cancelling. Config files written to %ProgramData%\ParsGate\run\ and cleaned up.
- **TASK 9**: Settings location moved to %ProgramData%\ParsGate\settings.json. Migrates from old install directory on first start. Atomic write (temp file → replace). Corrupt file backed up as .bad and defaults loaded. Secrets not logged.
- **TASK 10**: Version bumped to 0.8 consistently (csproj, AppSettings.Version, setup.iss, build.yml). Fixed AppId GUID in setup.iss for upgrades. CloseApplications for ParsGate.exe, xray.exe, sing-box.exe before install/upgrade. Output: ParsGate-Setup-v0.8.exe.

### Added
- xUnit test project (tests/ParsGate.Tests) with ConfigParser and SingBoxGenerator tests.
- CI config validation step for all protocol combinations.

### Changed
- AppSettings: added ShadowsocksMethod, ShadowsocksPassword to VpnConfigItem. SplitTunnelMode enum. Settings path changed to %ProgramData%\ParsGate\.
- Build.yml: pinned Xray-core v25.8.31, sing-box v1.12.4 with SHA256. Iran rule-set download from Chocolate4U. Config validation with pinned binaries.

### Notes
- Kill Switch design: needs manual verification that TUN traffic is not blocked by firewall rules while allowing app traffic to enter tunnel.
- Iran rule-set source: Chocolate4U prebuilt .srs files; if unavailable, CI generates from .dat sources using pinned sing-box.
- No visual design, NVDA/AutomationProperties, RTL layout, or Vazirmatn font changes.

## [0.7.0] - Previous release

Initial dual-core (Xray + Sing-box) VPN client for Windows 11 with NVDA accessibility support.