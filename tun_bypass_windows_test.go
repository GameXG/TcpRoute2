//go:build windows

package main

import (
	"net"
	"net/netip"
	"os"
	"strings"
	"testing"
)

func TestWindowsProcessOwnerTables(t *testing.T) {
	for _, ipv6 := range []bool{false, true} {
		host, tcp, udp := "127.0.0.1", "tcp4", "udp4"
		if ipv6 {
			host, tcp, udp = "::1", "tcp6", "udp6"
		}
		t.Run(tcp, func(t *testing.T) {
			listener, err := net.Listen(tcp, net.JoinHostPort(host, "0"))
			if err != nil {
				if ipv6 {
					t.Skip(err)
				}
				t.Fatal(err)
			}
			defer listener.Close()
			client, err := net.Dial(tcp, listener.Addr().String())
			if err != nil {
				t.Fatal(err)
			}
			defer client.Close()
			server, err := listener.Accept()
			if err != nil {
				t.Fatal(err)
			}
			defer server.Close()
			rows, err := windowsProcessSockets("tcp", ipv6)
			if err != nil {
				t.Fatal(err)
			}
			pid, err := socketOwner(rows, netip.MustParseAddrPort(client.LocalAddr().String()), netip.MustParseAddrPort(client.RemoteAddr().String()), false)
			if err != nil || pid != uint32(os.Getpid()) {
				t.Fatalf("TCP PID=%d err=%v", pid, err)
			}
			found := false
			for _, row := range rows {
				if row.listener && row.pid == pid && row.local == netip.MustParseAddrPort(listener.Addr().String()) {
					found = true
				}
			}
			if !found {
				t.Fatal("listener owner was not found")
			}
		})
		t.Run(udp, func(t *testing.T) {
			socket, err := net.ListenPacket(udp, net.JoinHostPort(host, "0"))
			if err != nil {
				if ipv6 {
					t.Skip(err)
				}
				t.Fatal(err)
			}
			defer socket.Close()
			rows, err := windowsProcessSockets("udp", ipv6)
			if err != nil {
				t.Fatal(err)
			}
			pid, err := socketOwner(rows, netip.MustParseAddrPort(socket.LocalAddr().String()), netip.MustParseAddrPort(net.JoinHostPort(host, "53")), true)
			if err != nil || pid != uint32(os.Getpid()) {
				t.Fatalf("UDP PID=%d err=%v", pid, err)
			}
		})
	}
	image, err := windowsProcessPath(uint32(os.Getpid()))
	if err != nil {
		t.Fatal(err)
	}
	executable, err := os.Executable()
	if err != nil || !strings.EqualFold(image, executable) {
		t.Fatalf("process image=%q executable=%q err=%v", image, executable, err)
	}
}
