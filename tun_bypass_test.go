package main

import (
	"context"
	"encoding/binary"
	"errors"
	"net/netip"
	"reflect"
	"testing"

	xnet "github.com/xtls/xray-core/common/net"
	"github.com/xtls/xray-core/common/session"
)

func TestLocalProxyEndpointDiscovery(t *testing.T) {
	configs, err := resolveUpstreamProxyReferences([]ServerConfigUpStream{
		{Name: "remote", ProxyUrl: "https://proxy.example.com:443?upProxy=entry"},
		{Name: "entry", ProxyUrl: "socks5://localhost:1080"},
		{Name: "local", ProxyUrl: "http://192.0.2.10:8080"},
		{Name: "notlocal", ProxyUrl: "http://192.0.2.11:8080"},
		{Name: "direct", ProxyUrl: "direct://0.0.0.0:0"},
	})
	if err != nil {
		t.Fatal(err)
	}
	got := localProxyEndpoints(configs, map[netip.Addr]bool{netip.MustParseAddr("192.0.2.10"): true})
	want := []netip.AddrPort{netip.MustParseAddrPort("127.0.0.1:1080"), netip.MustParseAddrPort("[::1]:1080"), netip.MustParseAddrPort("192.0.2.10:8080")}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("local proxy endpoints=%v want=%v", got, want)
	}
}

func TestProcessSocketTableLayouts(t *testing.T) {
	for _, test := range []struct {
		name                                              string
		udp, ipv6                                         bool
		size, local, port, remote, remotePort, pid, state int
	}{
		{"tcp4", false, false, 24, 4, 8, 12, 16, 20, 0},
		{"udp4", true, false, 12, 0, 4, 0, 0, 8, 0},
		{"tcp6", false, true, 56, 0, 20, 24, 44, 52, 48},
		{"udp6", true, true, 28, 0, 20, 0, 0, 24, 0},
	} {
		t.Run(test.name, func(t *testing.T) {
			data := make([]byte, 4+2*test.size)
			binary.LittleEndian.PutUint32(data, 2)
			local, remote := netip.MustParseAddr("198.18.0.2"), netip.MustParseAddr("203.0.113.9")
			if test.ipv6 {
				local, remote = netip.MustParseAddr("fd73:7463:7072::2"), netip.MustParseAddr("2001:db8::9")
			}
			for i := 0; i < 2; i++ {
				row := data[4+i*test.size:]
				copy(row[test.local:], local.AsSlice())
				binary.BigEndian.PutUint16(row[test.port:], uint16(5000+i))
				if !test.udp {
					copy(row[test.remote:], remote.AsSlice())
					binary.BigEndian.PutUint16(row[test.remotePort:], 443)
					binary.LittleEndian.PutUint32(row[test.state:], uint32(2+i))
				}
				binary.LittleEndian.PutUint32(row[test.pid:], uint32(123+i))
			}
			rows, err := parseProcessSocketTable(data, test.udp, test.ipv6)
			if err != nil || len(rows) != 2 {
				t.Fatalf("rows=%v err=%v", rows, err)
			}
			for i, row := range rows {
				if row.local != netip.AddrPortFrom(local, uint16(5000+i)) || row.pid != uint32(123+i) {
					t.Fatalf("row=%v", row)
				}
				if !test.udp && (row.remote != netip.AddrPortFrom(remote, 443) || row.listener != (i == 0)) {
					t.Fatalf("TCP row=%v", row)
				}
			}
			if _, err := parseProcessSocketTable(data[:len(data)-1], test.udp, test.ipv6); err == nil {
				t.Fatal("truncated row accepted")
			}
		})
	}
	if _, err := parseProcessSocketTable([]byte{0}, false, false); err == nil {
		t.Fatal("truncated header accepted")
	}
}

func TestSocketOwnerUsesWholeTCPConnectionAndRejectsAmbiguousUDP(t *testing.T) {
	source, remote := netip.MustParseAddrPort("198.18.0.2:5000"), netip.MustParseAddrPort("203.0.113.9:443")
	rows := []processSocket{
		{local: source, remote: netip.MustParseAddrPort("203.0.113.8:443"), pid: 10},
		{local: source, remote: remote, pid: 20},
	}
	if pid, err := socketOwner(rows, source, remote, false); err != nil || pid != 20 {
		t.Fatalf("owner=%d err=%v", pid, err)
	}
	if _, err := socketOwner(rows, source, remote, true); err == nil {
		t.Fatal("ambiguous UDP owners accepted")
	}
	rows = []processSocket{{local: netip.MustParseAddrPort("0.0.0.0:5000"), pid: 20}}
	if pid, err := socketOwner(rows, source, remote, true); err != nil || pid != 20 {
		t.Fatalf("wildcard owner=%d err=%v", pid, err)
	}
	if _, err := socketOwner(nil, source, remote, true); err == nil {
		t.Fatal("unidentified flow accepted")
	}
}

func TestTunBypassMatchesProxyProcessNotDestinationPort(t *testing.T) {
	source, remote := netip.MustParseAddrPort("198.18.0.2:5000"), netip.MustParseAddrPort("203.0.113.9:443")
	flowPID, listenerPID := uint32(10), uint32(10)
	policy := &tunBypassPolicy{
		localProxies: []netip.AddrPort{netip.MustParseAddrPort("127.0.0.1:1080")},
		table: func(network string, ipv6 bool) ([]processSocket, error) {
			return []processSocket{
				{local: source, remote: remote, pid: flowPID},
				{local: netip.MustParseAddrPort("0.0.0.0:1080"), pid: listenerPID, listener: true},
			}, nil
		},
	}
	ctx := session.ContextWithInbound(context.Background(), &session.Inbound{Source: xnet.TCPDestination(xnet.ParseAddress(source.Addr().String()), xnet.Port(source.Port()))})
	destination := xnet.TCPDestination(xnet.ParseAddress(remote.Addr().String()), xnet.Port(remote.Port()))
	if bypass, err := policy.match(ctx, destination); err != nil || !bypass {
		t.Fatalf("proxy bypass=%v err=%v", bypass, err)
	}
	flowPID = 11
	if bypass, err := policy.match(ctx, destination); err != nil || bypass {
		t.Fatalf("ordinary application bypass=%v err=%v", bypass, err)
	}
	listenerPID = 11
	if bypass, err := policy.match(ctx, destination); err != nil || !bypass {
		t.Fatalf("restarted proxy bypass=%v err=%v", bypass, err)
	}
	listenerPID = 12
	if bypass, err := policy.match(ctx, destination); err != nil || bypass {
		t.Fatalf("stale PID bypass=%v err=%v", bypass, err)
	}
	policy.table = func(string, bool) ([]processSocket, error) { return nil, errors.New("lookup failed") }
	if _, err := policy.match(ctx, destination); err == nil {
		t.Fatal("lookup failure silently fell back into proxy pool")
	}
}

func TestBypassProcessPaths(t *testing.T) {
	for _, test := range []struct {
		configured string
		want       bool
	}{
		{`engine.exe`, true}, {`ENGINE.EXE`, true}, {`C:\Proxy\engine.exe`, true},
		{`D:\Other\engine.exe`, false}, {`other.exe`, false},
	} {
		if got := matchesBypassProcess(`C:\Proxy\Engine.exe`, []string{test.configured}); got != test.want {
			t.Fatalf("%s: %v", test.configured, got)
		}
	}
}

func TestTunBypassExplicitEngineAndLookupFailure(t *testing.T) {
	policy := &tunBypassPolicy{
		processes: []string{`C:\Proxy\engine.exe`},
		table: func(string, bool) ([]processSocket, error) {
			return []processSocket{{local: netip.MustParseAddrPort("0.0.0.0:5000"), pid: 20}}, nil
		},
		processPath: func(uint32) (string, error) { return `C:\Proxy\Engine.exe`, nil },
	}
	ctx := session.ContextWithInbound(context.Background(), &session.Inbound{Source: xnet.UDPDestination(xnet.ParseAddress("198.18.0.2"), 5000)})
	destination := xnet.UDPDestination(xnet.ParseAddress("203.0.113.9"), 443)
	if bypass, err := policy.match(ctx, destination); err != nil || !bypass {
		t.Fatalf("explicit engine bypass=%v err=%v", bypass, err)
	}
	policy.processPath = func(uint32) (string, error) { return `C:\Other\engine.exe`, nil }
	if bypass, err := policy.match(ctx, destination); err != nil || bypass {
		t.Fatalf("unrelated executable bypass=%v err=%v", bypass, err)
	}
	policy.processPath = func(uint32) (string, error) { return "", errors.New("access denied") }
	if _, err := policy.match(ctx, destination); err == nil {
		t.Fatal("process lookup failure silently proxied")
	}
	policy.table = func(network string, ipv6 bool) ([]processSocket, error) {
		if ipv6 {
			return []processSocket{{local: netip.MustParseAddrPort("[::]:5000"), pid: 20}}, nil
		}
		return nil, nil
	}
	policy.processPath = func(uint32) (string, error) { return `C:\Proxy\Engine.exe`, nil }
	if bypass, err := policy.match(ctx, destination); err != nil || !bypass {
		t.Fatalf("dual-stack UDP engine bypass=%v err=%v", bypass, err)
	}
}
