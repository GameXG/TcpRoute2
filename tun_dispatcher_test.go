package main

import (
	"context"
	"encoding/binary"
	"fmt"
	"io"
	"net"
	"strings"
	"testing"
	"time"

	"github.com/miekg/dns"
	"github.com/xtls/xray-core/common/buf"
	xnet "github.com/xtls/xray-core/common/net"
	"github.com/xtls/xray-core/transport"
)

type recordingTunUpstream struct{ calls chan string }

func (u *recordingTunUpstream) DialTimeout(network, address string, _ time.Duration) (net.Conn, UpStreamErrorReporting, error) {
	u.calls <- network + " " + address
	client, server := net.Pipe()
	go func() { defer server.Close(); _, _ = io.Copy(server, server) }()
	return client, nil, nil
}

func TestTunTCPUsesTcpRouteRouting(t *testing.T) {
	client, server := net.Pipe()
	defer client.Close()
	defer server.Close()
	_ = client.SetDeadline(time.Now().Add(3 * time.Second))
	pool := &recordingTunUpstream{calls: make(chan string, 1)}
	dispatcher := &tunDispatcher{upstream: pool}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	result := make(chan error, 1)
	go func() {
		result <- dispatcher.DispatchLink(ctx, xnet.TCPDestination(xnet.ParseAddress("192.0.2.1"), xnet.Port(22)), &transport.Link{Reader: buf.NewReader(server), Writer: buf.NewWriter(server)})
	}()
	if _, err := client.Write([]byte("hello")); err != nil {
		t.Fatal(err)
	}
	var reply [5]byte
	if _, err := io.ReadFull(client, reply[:]); err != nil || string(reply[:]) != "hello" {
		t.Fatalf("reply=%q error=%v", reply, err)
	}
	if got := <-pool.calls; got != "tcp 192.0.2.1:22" {
		t.Fatalf("route=%s", got)
	}
	cancel()
	select {
	case <-result:
	case <-time.After(3 * time.Second):
		t.Fatal("TUN forwarding did not stop on cancellation")
	}
}

func TestTunIPv6AndUDPPolicy(t *testing.T) {
	t.Setenv("TCPROUTE_ENABLE_IPV6", "false")
	t.Setenv("TCPROUTE_ALLOW_QUIC", "false")
	t.Setenv("TCPROUTE_ALLOW_STUN", "false")
	pool := &recordingTunUpstream{calls: make(chan string, 1)}
	dispatcher := &tunDispatcher{upstream: pool}
	for _, destination := range []xnet.Destination{
		xnet.TCPDestination(xnet.ParseAddress("2001:db8::1"), xnet.Port(443)),
		xnet.UDPDestination(xnet.ParseAddress("192.0.2.1"), xnet.Port(443)),
		xnet.UDPDestination(xnet.ParseAddress("192.0.2.1"), xnet.Port(3478)),
	} {
		link := &transport.Link{Reader: buf.NewPacketReader(strings.NewReader("blocked")), Writer: buf.NewWriter(io.Discard)}
		if err := dispatcher.DispatchLink(context.Background(), destination, link); err == nil {
			t.Fatalf("allowed %s", destination)
		}
	}
	if len(pool.calls) != 0 {
		t.Fatal("disabled traffic reached the route pool")
	}
}

func TestTunFeatureSwitchesAndProcessBypass(t *testing.T) {
	stun := make([]byte, 20)
	binary.BigEndian.PutUint32(stun[4:8], 0x2112a442)
	for _, feature := range []struct {
		name        string
		destination xnet.Destination
		payload     []byte
	}{
		{"TCPROUTE_ALLOW_STUN", xnet.UDPDestination(xnet.ParseAddress("192.0.2.1"), 9999), stun},
		{"TCPROUTE_ALLOW_QUIC", xnet.UDPDestination(xnet.ParseAddress("192.0.2.1"), 443), []byte("quic")},
		{"TCPROUTE_ENABLE_IPV6", xnet.TCPDestination(xnet.ParseAddress("2001:db8::1"), 22), []byte("ipv6-tcp")},
		{"TCPROUTE_ENABLE_IPV6", xnet.UDPDestination(xnet.ParseAddress("2001:db8::1"), 123), []byte("ipv6-udp")},
	} {
		for _, enabled := range []bool{false, true} {
			for _, mode := range []string{"none", "process", "destination"} {
				bypass := mode != "none"
				t.Run(fmt.Sprintf("%s/%s/enabled=%t/bypass=%s", feature.name, feature.destination.Network, enabled, mode), func(t *testing.T) {
					t.Setenv("TCPROUTE_ALLOW_STUN", "false")
					t.Setenv("TCPROUTE_ALLOW_QUIC", "false")
					t.Setenv("TCPROUTE_ENABLE_IPV6", "false")
					t.Setenv(feature.name, fmt.Sprint(enabled))
					pool := &recordingTunUpstream{calls: make(chan string, 1)}
					direct := &recordingTunUpstream{calls: make(chan string, 1)}
					d := &tunDispatcher{upstream: pool,
						bypass: func(context.Context, xnet.Destination) (bool, error) { return bypass, nil },
						directDial: func(_ context.Context, network, address string) (net.Conn, error) {
							conn, _, err := direct.DialTimeout(network, address, 0)
							return conn, err
						},
					}
					if mode == "destination" {
						rule, err := compileTunBypassRule(tunBypassRuleConfig{
							Network:      strings.ToLower(feature.destination.Network.String()),
							Destinations: []string{feature.destination.Address.IP().String()},
							Ports:        []string{fmt.Sprint(feature.destination.Port.Value())},
						})
						if err != nil {
							t.Fatal(err)
						}
						policy := &tunBypassPolicy{rules: []tunBypassRule{rule}}
						d.bypass = policy.match
					}
					client, server := net.Pipe()
					defer client.Close()
					defer server.Close()
					_ = client.SetDeadline(time.Now().Add(3 * time.Second))
					ctx, cancel := context.WithCancel(context.Background())
					defer cancel()
					done := make(chan error, 1)
					go func() {
						done <- d.DispatchLink(ctx, feature.destination, &transport.Link{Reader: buf.NewReader(server), Writer: buf.NewWriter(server)})
					}()
					go func() { _, _ = client.Write(feature.payload) }()
					if enabled || bypass {
						reply := make([]byte, len(feature.payload))
						if _, err := io.ReadFull(client, reply); err != nil || string(reply) != string(feature.payload) {
							t.Fatalf("forwarded reply=%x err=%v", reply, err)
						}
						cancel()
					}
					select {
					case err := <-done:
						if !enabled && !bypass && err == nil {
							t.Fatal("disabled traffic accepted")
						}
					case <-time.After(3 * time.Second):
						t.Fatal("forwarding did not finish")
					}
					wantDirect, wantPool := 0, 0
					if bypass {
						wantDirect = 1
					} else if enabled {
						wantPool = 1
					}
					if len(direct.calls) != wantDirect || len(pool.calls) != wantPool {
						t.Fatalf("direct=%d pool=%d", len(direct.calls), len(pool.calls))
					}
				})
			}
		}
	}
}

func TestTunDNSRejectsMalformedAndUnsupportedQueries(t *testing.T) {
	if tunDNSResponse(context.Background(), []byte("invalid")) != nil {
		t.Fatal("malformed DNS query was accepted")
	}
	t.Setenv("TCPROUTE_ENABLE_IPV6", "false")
	for _, kind := range []uint16{dns.TypeAAAA, dns.TypeTXT} {
		request := new(dns.Msg)
		request.SetQuestion("example.test.", kind)
		wire, _ := request.Pack()
		answer := new(dns.Msg)
		if err := answer.Unpack(tunDNSResponse(context.Background(), wire)); err != nil {
			t.Fatal(err)
		}
		if answer.Id != request.Id || !answer.Response || len(answer.Answer) != 0 {
			t.Fatal("invalid DNS response")
		}
		if kind == dns.TypeTXT && answer.Rcode != dns.RcodeNotImplemented {
			t.Fatal("unsupported DNS type was not rejected")
		}
	}
}

func TestAndroidTunDNSGatewayOverSocks(t *testing.T) {
	t.Setenv("TCPROUTE_TUN_DNS", "true")
	pool := &recordingTunUpstream{calls: make(chan string, 1)}
	conn, err := net.Dial("tcp", startMixedProxyTest(t, pool))
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(3 * time.Second))
	_, _ = conn.Write([]byte{5, 1, 0})
	var auth [2]byte
	if _, err := io.ReadFull(conn, auth[:]); err != nil || auth != [2]byte{5, 0} {
		t.Fatalf("auth=%v error=%v", auth, err)
	}
	_, _ = conn.Write([]byte{5, 1, 0, 1, 198, 18, 0, 1, 0, 53})
	var reply [10]byte
	if _, err := io.ReadFull(conn, reply[:]); err != nil || reply[1] != 0 {
		t.Fatalf("CONNECT=%v error=%v", reply, err)
	}
	query := new(dns.Msg)
	query.SetQuestion("example.test.", dns.TypeTXT)
	wire, _ := query.Pack()
	var size [2]byte
	binary.BigEndian.PutUint16(size[:], uint16(len(wire)))
	_, _ = conn.Write(append(size[:], wire...))
	if _, err := io.ReadFull(conn, size[:]); err != nil {
		t.Fatal(err)
	}
	answerWire := make([]byte, binary.BigEndian.Uint16(size[:]))
	if _, err := io.ReadFull(conn, answerWire); err != nil {
		t.Fatal(err)
	}
	answer := new(dns.Msg)
	if err := answer.Unpack(answerWire); err != nil || answer.Id != query.Id || answer.Rcode != dns.RcodeNotImplemented {
		t.Fatalf("DNS response=%v error=%v", answer, err)
	}
	if len(pool.calls) != 0 {
		t.Fatal("synthetic DNS gateway reached an external upstream")
	}
}
