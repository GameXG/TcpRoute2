package main

import (
	"context"
	"testing"

	xnet "github.com/xtls/xray-core/common/net"
)

func TestTunDestinationBypassRules(t *testing.T) {
	for _, test := range []struct {
		name, config, host string
		port               xnet.Port
		udp, want          bool
	}{
		{"subnet", `[{"Destinations":["192.0.2.99/24"]}]`, "192.0.2.20", 80, false, true},
		{"outside subnet", `[{"Destinations":["192.0.2.0/24"]}]`, "192.0.3.1", 80, false, false},
		{"literal", `[{"Destinations":["192.0.2.1"]}]`, "192.0.2.1", 53, true, true},
		{"other literal", `[{"Destinations":["192.0.2.1"]}]`, "192.0.2.2", 53, true, false},
		{"ipv6", `[{"Destinations":["2001:db8::/32"]}]`, "2001:db8:1::1", 123, true, true},
		{"ipv4 mapped literal", `[{"Destinations":["::ffff:192.0.2.1"]}]`, "192.0.2.1", 443, false, true},
		{"different family", `[{"Destinations":["0.0.0.0/0"]}]`, "2001:db8::1", 80, false, false},
		{"tcp port", `[{"Network":"tcp","Ports":["443"]}]`, "192.0.2.1", 443, false, true},
		{"udp mismatch", `[{"Network":"tcp","Ports":["443"]}]`, "192.0.2.1", 443, true, false},
		{"range first", `[{"Network":"udp","Ports":["8000-9000"]}]`, "192.0.2.1", 8000, true, true},
		{"range last", `[{"Ports":["8000-9000"]}]`, "192.0.2.1", 9000, true, true},
		{"outside range", `[{"Ports":["8000-9000"]}]`, "192.0.2.1", 9001, true, false},
		{"combined", `[{"Network":"tcp","Destinations":["192.0.2.0/24"],"Ports":["443","8000-9000"]}]`, "192.0.2.1", 8080, false, true},
		{"combined wrong port", `[{"Destinations":["192.0.2.0/24"],"Ports":["443"]}]`, "192.0.2.1", 80, false, false},
		{"combined wrong ip", `[{"Destinations":["192.0.2.0/24"],"Ports":["443"]}]`, "192.0.3.1", 443, false, false},
		{"multiple rules", `[{"Ports":["80"]},{"Network":"udp","Destinations":["2001:db8::1"],"Ports":["443"]}]`, "2001:db8::1", 443, true, true},
		{"multiple addresses", `[{"Destinations":["192.0.2.1","2001:db8::1"]}]`, "2001:db8::1", 80, false, true},
		{"no rules", `[]`, "192.0.2.1", 443, false, false},
	} {
		t.Run(test.name, func(t *testing.T) {
			rules, err := parseTunBypassRules(test.config)
			if err != nil {
				t.Fatal(err)
			}
			destination := xnet.TCPDestination(xnet.ParseAddress(test.host), test.port)
			if test.udp {
				destination.Network = xnet.Network_UDP
			}
			policy := &tunBypassPolicy{rules: rules}
			bypass, err := policy.match(context.Background(), destination)
			if err != nil || bypass != test.want {
				t.Fatalf("bypass=%v want=%v err=%v", bypass, test.want, err)
			}
		})
	}
}

func TestTunDestinationBypassRejectsInvalidConfiguration(t *testing.T) {
	for _, raw := range []string{
		`null`, `{}`, `[null]`, `[{}]`, `[{"Network":"udp"}]`,
		`[{"Ports":["0"]}]`, `[{"Ports":["65536"]}]`, `[{"Ports":["9000-8000"]}]`,
		`[{"Ports":["80-90-100"]}]`, `[{"Ports":[" 80"]}]`, `[{"Ports":["+80"]}]`,
		`[{"Ports":["80-"]}]`, `[{"Ports":[""]}]`, `[{"Ports":[80]}]`,
		`[{"Network":"icmp","Ports":["80"]}]`, `[{"Network":"TCP","Ports":["80"]}]`,
		`[{"Destinations":["example.com"]}]`, `[{"Destinations":["192.0.2.1/33"]}]`,
		`[{"Destinations":["2001:db8::/129"]}]`, `[{"Destinations":["fe80::1%7"]}]`,
		`[{"Destinations":["::ffff:192.0.2.0/120"]}]`, `[{"Destinations":["127.1"]}]`,
		`[{"Destinations":["192.0.2.1"],"Port":["443"]}]`, `[] []`,
	} {
		t.Run(raw, func(t *testing.T) {
			if _, err := parseTunBypassRules(raw); err == nil {
				t.Fatal("invalid configuration accepted")
			}
		})
	}
}

func TestTunDestinationBypassPrecedesProcessLookup(t *testing.T) {
	rules, err := parseTunBypassRules(`[{"Network":"udp","Destinations":["192.0.2.0/24"],"Ports":["443"]}]`)
	if err != nil {
		t.Fatal(err)
	}
	policy := &tunBypassPolicy{rules: rules, processes: []string{"engine.exe"}}
	destination := xnet.UDPDestination(xnet.ParseAddress("192.0.2.1"), 443)
	if bypass, err := policy.match(context.Background(), destination); err != nil || !bypass {
		t.Fatalf("explicit bypass requires process lookup: %v", err)
	}
	destination.Port = 80
	if _, err := policy.match(context.Background(), destination); err == nil {
		t.Fatal("unmatched destination skipped process checks")
	}
}
