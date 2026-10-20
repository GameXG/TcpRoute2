package main

import (
	"io"
	"net"
	"net/url"
	"reflect"
	"strings"
	"testing"
	"time"

	proxyclient "github.com/GameXG/ProxyClient"
)

func TestResolveUpstreamProxyNameReferences(t *testing.T) {
	configs, err := resolveUpstreamProxyReferences([]ServerConfigUpStream{
		{Name: "entry", ProxyUrl: "socks5://127.0.0.1:1080"},
		{Name: "middle", ProxyUrl: "http://127.0.0.1:8080?upProxy=entry"},
		{Name: "final", ProxyUrl: "https://127.0.0.1:8443?upProxy=middle"},
	})
	if err != nil {
		t.Fatal(err)
	}
	if configs[1].ProxyUrl != "http://127.0.0.1:8080?upProxy=socks5%3A%2F%2F127.0.0.1%3A1080" {
		t.Fatalf("middle URL = %q", configs[1].ProxyUrl)
	}
	if configs[2].ProxyUrl != "https://127.0.0.1:8443?upProxy=http%3A%2F%2F127.0.0.1%3A8080%3FupProxy%3Dsocks5%253A%252F%252F127.0.0.1%253A1080" {
		t.Fatalf("final URL = %q", configs[2].ProxyUrl)
	}
}

func TestRemovedUpstreamDisablesDependentChains(t *testing.T) {
	configs, err := resolveUpstreamProxyReferences([]ServerConfigUpStream{
		{Name: "direct", ProxyUrl: "direct://0.0.0.0:0"},
		{Name: "removed", ProxyUrl: "invalid", Remove: true, DialProxyFor: "exit"},
		{Name: "chain", ProxyUrl: "http://127.0.0.1:80?upProxy=removed"},
		{Name: "nested", ProxyUrl: "http://127.0.0.1:81?upProxy=chain"},
		{Name: "exit", ProxyUrl: "socks5://127.0.0.1:1080"},
	})
	if err != nil || len(configs) != 2 || configs[0].Name != "direct" || configs[1].Name != "exit" || configs[1].ProxyUrl != "socks5://127.0.0.1:1080" {
		t.Fatalf("removed node or dependent route remained active: %#v, %v", configs, err)
	}
}

func TestResolveUpstreamProxyReferenceErrors(t *testing.T) {
	for name, configs := range map[string][]ServerConfigUpStream{
		"unknown":   {{Name: "a", ProxyUrl: "socks5://127.0.0.1:1?upProxy=missing"}},
		"cycle":     {{Name: "a", ProxyUrl: "socks5://127.0.0.1:1?upProxy=b"}, {Name: "b", ProxyUrl: "socks5://127.0.0.1:2?upProxy=a"}},
		"duplicate": {{Name: "a", ProxyUrl: "socks5://127.0.0.1:1"}, {Name: "a", ProxyUrl: "socks5://127.0.0.1:2"}},
	} {
		t.Run(name, func(t *testing.T) {
			if _, err := resolveUpstreamProxyReferences(configs); err == nil {
				t.Fatalf("expected %s to fail", name)
			}
		})
	}
}

func TestResolveUpstreamProxyKeepsFullURL(t *testing.T) {
	input := []ServerConfigUpStream{{Name: "a", ProxyUrl: "socks5://127.0.0.1:1?upProxy=http://127.0.0.1:2"}}
	output, err := resolveUpstreamProxyReferences(input)
	if err != nil || output[0].ProxyUrl != input[0].ProxyUrl {
		t.Fatalf("full upProxy URL changed: %#v, %v", output, err)
	}
}

func TestResolveUpstreamProxyKeepsUnnamedUpstream(t *testing.T) {
	input := []ServerConfigUpStream{{ProxyUrl: "socks5://127.0.0.1:1080"}}
	output, err := resolveUpstreamProxyReferences(input)
	if err != nil || !reflect.DeepEqual(input, output) {
		t.Fatalf("unnamed upstream changed: %#v, %v", output, err)
	}
}

func TestResolveUpstreamProxyForwardCaseAndNestedURL(t *testing.T) {
	for _, key := range []string{"upProxy", "upproxy", "UPPROXY"} {
		t.Run(key, func(t *testing.T) {
			input := []ServerConfigUpStream{
				{Name: "final", ProxyUrl: "http://127.0.0.1:3?" + key + "=" + url.QueryEscape("http://127.0.0.1:2?upProxy=entry")},
				{Name: "entry", ProxyUrl: "socks5://127.0.0.1:1"},
			}
			original := append([]ServerConfigUpStream(nil), input...)
			resolved, err := resolveUpstreamProxyReferences(input)
			if err != nil {
				t.Fatal(err)
			}
			outer, _ := url.Parse(resolved[0].ProxyUrl)
			inner, err := url.Parse(outer.Query().Get(key))
			if err != nil || inner.Query().Get("upProxy") != input[1].ProxyUrl {
				t.Fatalf("nested named URL was not resolved: %v, %v", inner, err)
			}
			if !reflect.DeepEqual(input, original) {
				t.Fatal("resolution modified caller configuration")
			}
		})
	}
}

func TestResolveUpstreamProxyRejectsAmbiguousQueries(t *testing.T) {
	for _, raw := range []string{
		"http://127.0.0.1:1?upProxy=entry&upproxy=entry",
		"http://127.0.0.1:1?upProxy=entry&upProxy=entry",
		"http://127.0.0.1:1?upProxy=",
		"http://127.0.0.1:1?upProxy=%zz",
		"direct://0.0.0.0:0?upProxy=entry",
	} {
		_, err := resolveUpstreamProxyReferences([]ServerConfigUpStream{
			{Name: "final", ProxyUrl: raw}, {Name: "entry", ProxyUrl: "http://127.0.0.1:2"},
		})
		if err == nil {
			t.Fatalf("invalid or unsupported chain accepted: %s", raw)
		}
	}
	resolved, err := resolveUpstreamProxyReferences([]ServerConfigUpStream{
		{Name: "front", ProxyUrl: "http://127.0.0.1:1", DialProxyFor: "target"},
		{Name: "target", ProxyUrl: "ss://aes-256-gcm:password@127.0.0.1:2"},
	})
	if err != nil || !strings.Contains(resolved[1].ProxyUrl, "upProxy=http") {
		t.Fatalf("extension target did not accept a front proxy: %#v, %v", resolved, err)
	}
	_, err = resolveUpstreamProxyReferences([]ServerConfigUpStream{
		{Name: "self", ProxyUrl: "http://127.0.0.1:1?upProxy=" + url.QueryEscape("http://127.0.0.1:2?upProxy=self")},
	})
	if err == nil {
		t.Fatal("cycle through an inline proxy URL was accepted")
	}
}

func TestNamedProxyChainConnectsThroughBothHops(t *testing.T) {
	t.Run("upProxy", func(t *testing.T) { testProxyChainConnectsThroughBothHops(t, false, false) })
	t.Run("DialProxyFor", func(t *testing.T) { testProxyChainConnectsThroughBothHops(t, true, false) })
	t.Run("DialProxyFor receiver sleep only", func(t *testing.T) { testProxyChainConnectsThroughBothHops(t, true, true) })
}

func testProxyChainConnectsThroughBothHops(t *testing.T, reverse, race bool) {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	go func() {
		conn, err := listener.Accept()
		if err == nil {
			defer conn.Close()
			_, _ = io.Copy(conn, conn)
		}
	}()
	last := &httpTestPool{target: listener.Addr().String(), calls: make(chan string, 2)}
	lastAddress := startMixedProxyTest(t, last)
	first := &httpTestPool{target: lastAddress, calls: make(chan string, 2)}
	firstAddress := startMixedProxyTest(t, first)
	configs := []ServerConfigUpStream{
		{Name: "final", ProxyUrl: "http://" + lastAddress + "?upProxy=entry"},
		{Name: "entry", ProxyUrl: "http://" + firstAddress},
	}
	if reverse {
		configs[0].ProxyUrl = "http://" + lastAddress
		configs[1].DialProxyFor = "final"
	}
	if race {
		configs[0].Sleep, configs[1].Sleep = 25, 3000
	}
	resolved, err := resolveUpstreamProxyReferences(configs)
	if err != nil {
		t.Fatal(err)
	}
	var conn net.Conn
	if race {
		pool := NewTcppingUpStream()
		for _, config := range resolved {
			if err := pool.AddConfiguredUpStream(config, t.TempDir()); err != nil {
				t.Fatal(err)
			}
		}
		if pool.dc[0].sleep != 25*time.Millisecond || pool.dc[1].sleep != 3*time.Second {
			t.Fatal("standalone and receiver sleep policies were mixed")
		}
		started := time.Now()
		conn, _, err = pool.DialTimeout("tcp", listener.Addr().String(), 3*time.Second)
		if time.Since(started) > 2*time.Second {
			t.Fatal("accelerated receiver waited for the front's standalone Sleep")
		}
	} else {
		client, clientErr := proxyclient.NewProxyClient(resolved[0].ProxyUrl)
		if clientErr != nil {
			t.Fatal(clientErr)
		}
		conn, err = client.DialTimeout("tcp", listener.Addr().String(), 3*time.Second)
	}
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(3 * time.Second))
	if _, err := io.WriteString(conn, "named-chain"); err != nil {
		t.Fatal(err)
	}
	reply := make([]byte, len("named-chain"))
	if _, err := io.ReadFull(conn, reply); err != nil || string(reply) != "named-chain" {
		t.Fatalf("proxy chain reply = %q, %v", reply, err)
	}
	if address := <-first.calls; address != lastAddress {
		t.Fatalf("entry connected to %q, want final proxy %q", address, lastAddress)
	}
	if address := <-last.calls; address != listener.Addr().String() {
		t.Fatalf("final connected to %q, want echo destination", address)
	}
}
