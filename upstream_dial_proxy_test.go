package main

import (
	"net/url"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
	"time"
)

func TestDialProxyForBindsOnlyAtLoad(t *testing.T) {
	input := []ServerConfigUpStream{
		{Name: "target", ProxyUrl: "https://127.0.0.1:8443", Credit: -100, Sleep: 80, DnsResolve: true, Whitelist: []ServerConfigDomainList{{Path: "sites.txt", Type: "suffix"}}},
		{Name: "sub_mysub_a", ProxyUrl: "socks5://127.0.0.1:1080", DialProxyFor: "target"},
	}
	original := append([]ServerConfigUpStream(nil), input...)
	resolved, err := resolveUpstreamProxyReferences(input)
	if err != nil {
		t.Fatal(err)
	}
	if len(resolved) != 2 || resolved[0].Name != "target" || !reflect.DeepEqual(resolved[1], input[1]) {
		t.Fatalf("prefix lost its own routing configuration: %+v", resolved)
	}
	parsed, _ := url.Parse(resolved[0].ProxyUrl)
	if parsed.Query().Get("upProxy") != input[1].ProxyUrl {
		t.Fatal("prefix was not applied to receiver")
	}
	if resolved[0].Credit != -100 || resolved[0].Sleep != 80 || !resolved[0].DnsResolve || !reflect.DeepEqual(resolved[0].Whitelist, input[0].Whitelist) {
		t.Fatal("receiver policy was replaced by prefix policy")
	}
	if !reflect.DeepEqual(input, original) {
		t.Fatal("load changed saved configuration")
	}
	withoutPrefix, err := resolveUpstreamProxyReferences(input[:1])
	if err != nil {
		t.Fatal(err)
	}
	if len(withoutPrefix) != 1 || withoutPrefix[0].ProxyUrl != input[0].ProxyUrl {
		t.Fatal("deleting prefix left a dangling receiver reference")
	}
}

func TestDialProxyForMultipleParentsAndNestedBindings(t *testing.T) {
	resolved, err := resolveUpstreamProxyReferences([]ServerConfigUpStream{
		{Name: "final", ProxyUrl: "https://127.0.0.1:8443"},
		{Name: "middle", ProxyUrl: "http://127.0.0.1:8080", DialProxyFor: "final"},
		{Name: "first-a", ProxyUrl: "socks5://127.0.0.1:1080", DialProxyFor: "middle"},
		{Name: "first-b", ProxyUrl: "socks5://127.0.0.1:1081", DialProxyFor: "middle"},
	})
	if err != nil {
		t.Fatal(err)
	}
	if len(resolved) != 6 || resolved[0].Name == resolved[1].Name || resolved[4].Name != "first-a" || resolved[5].Name != "first-b" {
		t.Fatalf("expected both full chains and standalone prefix routes: %+v", resolved)
	}
	for index, config := range resolved[:2] {
		outer, _ := url.Parse(config.ProxyUrl)
		middle, _ := url.Parse(outer.Query().Get("upProxy"))
		want := "socks5://127.0.0.1:1080"
		if index == 1 {
			want = "socks5://127.0.0.1:1081"
		}
		if middle.Host != "127.0.0.1:8080" || middle.Query().Get("upProxy") != want {
			t.Fatalf("bad chain: %s", config.ProxyUrl)
		}
	}
}

func TestDialProxyForPrefixAcceleratesEveryMatchedTarget(t *testing.T) {
	for _, selectors := range []string{"tw_0", "tw_0*", "tw_0111,tw_0da,tw_0232_11aa", "tw_0,tw_0111"} {
		t.Run(selectors, func(t *testing.T) {
			input := []ServerConfigUpStream{
				{Name: "A", ProxyUrl: "socks5://front.test:1080", DialProxyFor: selectors, Sleep: 15},
				{Name: "tw_0111", ProxyUrl: "https://one.test:443", Sleep: 80, Credit: -100},
				{Name: "tw_0da", ProxyUrl: "http://two.test:8080", DnsResolve: true},
				{Name: "tw_0232_11aa", ProxyUrl: "socks5://three.test:1080", CorrectDelay: 30},
				{Name: "hk_01", Alias: "tw_0 display only", ProxyUrl: "https://other.test:443"},
			}
			original := append([]ServerConfigUpStream(nil), input...)
			resolved, err := resolveUpstreamProxyReferences(input)
			if err != nil {
				t.Fatal(err)
			}
			if len(resolved) != len(input) || !reflect.DeepEqual(resolved[0], input[0]) || !reflect.DeepEqual(resolved[4], input[4]) || !reflect.DeepEqual(input, original) {
				t.Fatalf("binding changed source routes or duplicated matches: %+v", resolved)
			}
			for index := 1; index <= 3; index++ {
				parsed, _ := url.Parse(resolved[index].ProxyUrl)
				if parsed.Query().Get("upProxy") != input[0].ProxyUrl {
					t.Fatalf("A was not the matched node's front: %s", resolved[index].ProxyUrl)
				}
				resolved[index].ProxyUrl = input[index].ProxyUrl
				if !reflect.DeepEqual(resolved[index], input[index]) {
					t.Fatal("target routing policy was not preserved")
				}
			}
		})
	}
}

func TestDialProxyForPrefixMultipleAccelerators(t *testing.T) {
	resolved, err := resolveUpstreamProxyReferences([]ServerConfigUpStream{
		{Name: "A", ProxyUrl: "socks5://front-a.test:1080", DialProxyFor: "tw_0"},
		{Name: "B", ProxyUrl: "socks5://front-b.test:1080", DialProxyFor: "tw_0*"},
		{Name: "tw_0111", ProxyUrl: "https://one.test:443"},
		{Name: "tw_0da", ProxyUrl: "https://two.test:443"},
	})
	if err != nil {
		t.Fatal(err)
	}
	if len(resolved) != 6 {
		t.Fatalf("expected two standalone fronts and four chains: %+v", resolved)
	}
	for index, config := range resolved[2:] {
		parsed, _ := url.Parse(config.ProxyUrl)
		want := "socks5://front-a.test:1080"
		if index%2 == 1 {
			want = "socks5://front-b.test:1080"
		}
		if parsed.Query().Get("upProxy") != want {
			t.Fatal("front candidate order or direction changed")
		}
	}
}

func TestDialProxyForPrefixRejectsInvalidGroups(t *testing.T) {
	for _, input := range [][]ServerConfigUpStream{
		{{Name: "tw_0front", ProxyUrl: "socks5://a.test:1", DialProxyFor: "tw_0"}},
		{{Name: "A", ProxyUrl: "socks5://a.test:1", DialProxyFor: "A,tw_0"}, {Name: "tw_0111", ProxyUrl: "http://b.test:1"}},
		{{Name: "A", ProxyUrl: "socks5://a.test:1", DialProxyFor: "A*"}},
		{{Name: "A", ProxyUrl: "socks5://a.test:1", DialProxyFor: "*"}},
		{{Name: "A", Remove: true, ProxyUrl: "socks5://a.test:1", DialProxyFor: "*"}},
		{{Name: "A", Remove: true, ProxyUrl: "socks5://a.test:1", DialProxyFor: "A"}},
		{{Name: "A", Remove: true, ProxyUrl: "socks5://a.test:1", DialProxyFor: "A*"}},
		{{Name: "A", ProxyUrl: "socks5://a.test:1", DialProxyFor: "tw_0,*"}, {Name: "tw_0111", ProxyUrl: "http://b.test:1"}},
		{{Name: "a_one", ProxyUrl: "http://a.test:1", DialProxyFor: "b_"}, {Name: "b_one", ProxyUrl: "http://b.test:1", DialProxyFor: "a_"}},
		{{Name: "A", ProxyUrl: "socks5://a.test:1", DialProxyFor: "tw_0"}, {Name: "tw_0111", ProxyUrl: "vless://id@b.test:1"}},
		{{Name: "A", ProxyUrl: "socks5://a.test:1", DialProxyFor: "tw_0,missing"}, {Name: "tw_0111", ProxyUrl: "http://b.test:1"}},
	} {
		if _, err := resolveUpstreamProxyReferences(input); err == nil {
			t.Fatalf("invalid prefix binding was accepted: %+v", input)
		}
	}
	resolved, err := resolveUpstreamProxyReferences([]ServerConfigUpStream{{Name: "A", ProxyUrl: "socks5://a.test:1", DialProxyFor: "tw_0"}, {Name: "tw_0111", Remove: true, ProxyUrl: "https://b.test:1"}})
	if err != nil || len(resolved) != 1 || resolved[0].ProxyUrl != "socks5://a.test:1" {
		t.Fatalf("disabled match did not retain standalone A: %+v %v", resolved, err)
	}
}

func TestDialProxyForProviderKeepsOwnRoutePolicy(t *testing.T) {
	directory := t.TempDir()
	if err := os.WriteFile(filepath.Join(directory, "blocked.txt"), []byte("blocked.test\n"), 0600); err != nil {
		t.Fatal(err)
	}
	input := []ServerConfigUpStream{
		{Name: "front", ProxyUrl: "http://127.0.0.1:1080", DialProxyFor: "target", Credit: -100, Sleep: 35, CorrectDelay: 20, DnsResolve: true, Blacklist: []ServerConfigDomainList{{Path: "blocked.txt", Type: "base"}}},
		{Name: "target", ProxyUrl: "https://127.0.0.1:8443", Credit: 0, Sleep: 80},
	}
	resolved, err := resolveUpstreamProxyReferences(input)
	if err != nil {
		t.Fatal(err)
	}
	pool := NewTcppingUpStream()
	for _, config := range resolved {
		if err := pool.AddConfiguredUpStream(config, directory); err != nil {
			t.Fatal(err)
		}
	}
	if len(pool.dc) != 2 {
		t.Fatal("front is absent from the ordinary route pool")
	}
	front := pool.dc[0]
	if front.name != "front" || front.dialCredit != -100 || front.sleep != 35*time.Millisecond || front.correctDelay != 20*time.Millisecond || !front.dnsResolve {
		t.Fatal("front's ordinary-route policy was changed")
	}
	allowed, _ := pool.selectDialClients("allowed.test:443")
	blocked, _ := pool.selectDialClients("blocked.test:443")
	if len(allowed) != 2 || len(blocked) != 1 || blocked[0].name != "target" {
		t.Fatalf("ordinary route rules were ignored: allowed=%d blocked=%+v", len(allowed), blocked)
	}
	parsed, _ := url.Parse(resolved[1].ProxyUrl)
	if parsed.Query().Get("upProxy") != input[0].ProxyUrl {
		t.Fatal("front was not also used for its target")
	}
}

func TestDialProxyForRejectsConflictsAndCycles(t *testing.T) {
	for name, configs := range map[string][]ServerConfigUpStream{
		"own upProxy":        {{Name: "front", ProxyUrl: "http://127.0.0.1:1?upProxy=direct://0.0.0.0:0", DialProxyFor: "target"}, {Name: "target", ProxyUrl: "http://127.0.0.1:2"}},
		"receiver upProxy":   {{Name: "front", ProxyUrl: "http://127.0.0.1:1", DialProxyFor: "target"}, {Name: "target", ProxyUrl: "http://127.0.0.1:2?UPPROXY=direct://0.0.0.0:0"}},
		"unknown target":     {{Name: "front", ProxyUrl: "http://127.0.0.1:1", DialProxyFor: "missing"}},
		"self":               {{Name: "front", ProxyUrl: "http://127.0.0.1:1", DialProxyFor: "front"}},
		"cycle":              {{Name: "a", ProxyUrl: "http://127.0.0.1:1", DialProxyFor: "b"}, {Name: "b", ProxyUrl: "http://127.0.0.1:2", DialProxyFor: "a"}},
		"unsupported target": {{Name: "front", ProxyUrl: "http://127.0.0.1:1", DialProxyFor: "target"}, {Name: "target", ProxyUrl: "vless://id@127.0.0.1:2"}},
	} {
		t.Run(name, func(t *testing.T) {
			if _, err := resolveUpstreamProxyReferences(configs); err == nil {
				t.Fatal("invalid DialProxyFor was accepted")
			}
		})
	}
}

func TestSubscriptionDialProxyForCanBeDeselected(t *testing.T) {
	imported := sampleSubscriptionImport()
	imported.Upstream.DialProxyFor = "target"
	base := "addr='127.0.0.1:7070'\n[[UpStreams]]\nName='target'\nProxyUrl='https://127.0.0.1:8443'\n"
	result, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: base, Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(result.Content, `DialProxyFor="target"`) {
		t.Fatal("subscription import did not persist DialProxyFor")
	}
	removed, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: result.Content, Sources: []subscriptionSource{{SourceID: "mysub", Prefix: "sub_mysub_", Nodes: map[string]string{"a": "a"}}}, Removals: []subscriptionIdentity{{SourceID: "mysub", NodeID: "a"}}})
	if err != nil {
		t.Fatal(err)
	}
	if len(removed.Selections) != 0 || !strings.Contains(removed.Content, "Remove=true") || !strings.Contains(removed.Content, "DialProxyFor=\"target\"") {
		t.Fatal("subscription prefix was not soft-removed")
	}
}
