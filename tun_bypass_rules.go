package main

import (
	"encoding/json"
	"fmt"
	"io"
	"net/netip"
	"strconv"
	"strings"

	xnet "github.com/xtls/xray-core/common/net"
)

type tunBypassRuleConfig struct {
	Network      string   `json:"Network"`
	Destinations []string `json:"Destinations"`
	Ports        []string `json:"Ports"`
}

type tunBypassPortRange struct{ first, last uint16 }

type tunBypassRule struct {
	network      string
	destinations []netip.Prefix
	ports        []tunBypassPortRange
}

func parseTunBypassRules(raw string) ([]tunBypassRule, error) {
	var configs []tunBypassRuleConfig
	decoder := json.NewDecoder(strings.NewReader(raw))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&configs); err != nil {
		return nil, fmt.Errorf("invalid TUN bypass rules: %w", err)
	}
	if configs == nil {
		return nil, fmt.Errorf("TUN bypass rules must be an array")
	}
	if err := decoder.Decode(new(any)); err != io.EOF {
		return nil, fmt.Errorf("unexpected data after TUN bypass rules")
	}
	rules := make([]tunBypassRule, 0, len(configs))
	for index, config := range configs {
		rule, err := compileTunBypassRule(config)
		if err != nil {
			return nil, fmt.Errorf("TUN bypass rule %d: %w", index+1, err)
		}
		rules = append(rules, rule)
	}
	return rules, nil
}

func compileTunBypassRule(config tunBypassRuleConfig) (tunBypassRule, error) {
	rule := tunBypassRule{network: config.Network}
	switch rule.network {
	case "", "any":
		rule.network = "any"
	case "tcp", "udp":
	default:
		return rule, fmt.Errorf("Network must be any, tcp, or udp")
	}
	if len(config.Destinations) == 0 && len(config.Ports) == 0 {
		return rule, fmt.Errorf("at least one destination or port is required")
	}
	for _, value := range config.Destinations {
		prefix, err := netip.ParsePrefix(value)
		if err != nil {
			address, parseErr := netip.ParseAddr(value)
			if parseErr != nil || address.Zone() != "" {
				return rule, fmt.Errorf("invalid destination %q: use an IP address or CIDR", value)
			}
			address = address.Unmap()
			prefix = netip.PrefixFrom(address, address.BitLen())
		} else if prefix.Addr().Is4In6() {
			return rule, fmt.Errorf("IPv4-mapped CIDRs are unsupported; use an IPv4 CIDR")
		}
		rule.destinations = append(rule.destinations, prefix.Masked())
	}
	for _, value := range config.Ports {
		bounds := strings.Split(value, "-")
		if len(bounds) > 2 {
			return rule, fmt.Errorf("invalid port range %q", value)
		}
		parsePort := func(text string) (uint16, error) {
			if text == "" || strings.IndexFunc(text, func(r rune) bool { return r < '0' || r > '9' }) >= 0 {
				return 0, fmt.Errorf("invalid port %q", value)
			}
			port, err := strconv.ParseUint(text, 10, 16)
			if err != nil || port == 0 {
				return 0, fmt.Errorf("port %q must be between 1 and 65535", value)
			}
			return uint16(port), nil
		}
		first, err := parsePort(bounds[0])
		if err != nil {
			return rule, err
		}
		last := first
		if len(bounds) == 2 {
			last, err = parsePort(bounds[1])
			if err != nil {
				return rule, err
			}
		}
		if first > last {
			return rule, fmt.Errorf("reversed port range %q", value)
		}
		rule.ports = append(rule.ports, tunBypassPortRange{first, last})
	}
	return rule, nil
}

func (r *tunBypassRule) matches(destination xnet.Destination) bool {
	network := ""
	switch destination.Network {
	case xnet.Network_TCP:
		network = "tcp"
	case xnet.Network_UDP:
		network = "udp"
	default:
		return false
	}
	if r.network != "any" && r.network != network {
		return false
	}
	if len(r.destinations) != 0 {
		if !destination.Address.Family().IsIP() {
			return false
		}
		address, ok := netip.AddrFromSlice(destination.Address.IP())
		if !ok {
			return false
		}
		address = address.WithZone("").Unmap()
		matched := false
		for _, prefix := range r.destinations {
			if prefix.Contains(address) {
				matched = true
				break
			}
		}
		if !matched {
			return false
		}
	}
	if len(r.ports) != 0 {
		port := destination.Port.Value()
		for _, ports := range r.ports {
			if port >= ports.first && port <= ports.last {
				return true
			}
		}
		return false
	}
	return true
}
