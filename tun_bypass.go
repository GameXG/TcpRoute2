package main

import (
	"context"
	"encoding/binary"
	"fmt"
	"log"
	"net"
	"net/netip"
	"net/url"
	"path"
	"strings"

	xnet "github.com/xtls/xray-core/common/net"
	"github.com/xtls/xray-core/common/session"
)

type processSocket struct {
	local, remote netip.AddrPort
	pid           uint32
	listener      bool
}

type tunBypassPolicy struct {
	localProxies []netip.AddrPort
	processes    []string
	rules        []tunBypassRule
	selfPID      uint32
	table        func(string, bool) ([]processSocket, error)
	processPath  func(uint32) (string, error)
}

func localProxyEndpoints(configs []ServerConfigUpStream, local map[netip.Addr]bool) []netip.AddrPort {
	var endpoints []netip.AddrPort
	seen := map[netip.AddrPort]bool{}
	var collect func(string, int)
	collect = func(raw string, depth int) {
		if depth > 64 {
			return
		}
		parsed, err := url.Parse(raw)
		if err != nil {
			return
		}
		for key, values := range parsed.Query() {
			if strings.EqualFold(key, "upProxy") && len(values) == 1 {
				collect(values[0], depth+1)
			}
		}
		switch strings.ToLower(parsed.Scheme) {
		case "http", "https", "socks5":
		default:
			return
		}
		hosts := []string{parsed.Hostname()}
		if strings.EqualFold(parsed.Hostname(), "localhost") {
			hosts = []string{"127.0.0.1", "::1"}
		}
		for _, host := range hosts {
			endpoint, err := netip.ParseAddrPort(net.JoinHostPort(host, parsed.Port()))
			if err != nil || (!endpoint.Addr().IsLoopback() && !local[endpoint.Addr().WithZone("").Unmap()]) {
				continue
			}
			if !seen[endpoint] {
				endpoints = append(endpoints, endpoint)
				seen[endpoint] = true
			}
		}
	}
	for _, config := range configs {
		collect(config.ProxyUrl, 0)
	}
	return endpoints
}

func (p *tunBypassPolicy) match(ctx context.Context, destination xnet.Destination) (bool, error) {
	for index := range p.rules {
		if p.rules[index].matches(destination) {
			log.Printf("TUN bypass: destination rule %d -> %s", index+1, destination)
			return true, nil
		}
	}
	if len(p.localProxies) == 0 && len(p.processes) == 0 {
		return false, nil
	}
	inbound := session.InboundFromContext(ctx)
	if inbound == nil {
		return false, fmt.Errorf("TUN bypass: missing source endpoint")
	}
	source, err := netip.ParseAddrPort(inbound.Source.NetAddr())
	if err != nil {
		return false, fmt.Errorf("TUN bypass: invalid source endpoint")
	}
	remote, err := netip.ParseAddrPort(destination.NetAddr())
	if err != nil {
		return false, fmt.Errorf("TUN bypass: invalid destination endpoint")
	}
	network := "tcp"
	if destination.Network == xnet.Network_UDP {
		network = "udp"
	}
	// Read live tables for each new flow. Retaining PID decisions across flows can
	// bypass an unrelated process after a proxy exits and Windows reuses its PID.
	rows, err := p.table(network, source.Addr().Is6())
	if err != nil {
		return false, fmt.Errorf("TUN bypass owner lookup: %w", err)
	}
	pid, err := socketOwner(rows, source, remote, network == "udp")
	if err != nil && source.Addr().Is4() {
		// A dual-stack socket may be listed only in the IPv6 owner table.
		dual, lookupErr := p.table(network, true)
		if lookupErr != nil {
			return false, fmt.Errorf("TUN bypass dual-stack owner lookup: %w", lookupErr)
		}
		pid, err = socketOwner(append(rows, dual...), source, remote, network == "udp")
	}
	if err != nil {
		return false, err
	}
	if pid == p.selfPID {
		return true, nil
	}
	for _, ipv6 := range []bool{false, true} {
		needed := false
		for _, endpoint := range p.localProxies {
			if endpoint.Addr().Is6() == ipv6 {
				needed = true
			}
		}
		if !needed {
			continue
		}
		listeners := rows
		if network != "tcp" || source.Addr().Is6() != ipv6 {
			listeners, err = p.table("tcp", ipv6)
			if err != nil {
				return false, fmt.Errorf("TUN bypass listener lookup: %w", err)
			}
		}
		for _, row := range listeners {
			if !row.listener || row.pid != pid {
				continue
			}
			for _, endpoint := range p.localProxies {
				if socketEndpointMatches(row.local, endpoint) {
					log.Printf("TUN bypass: local upstream process PID %d -> %s %s", pid, network, destination.NetAddr())
					return true, nil
				}
			}
		}
	}
	if len(p.processes) == 0 {
		return false, nil
	}
	image, err := p.processPath(pid)
	if err != nil {
		return false, fmt.Errorf("TUN bypass process lookup: %w", err)
	}
	matched := matchesBypassProcess(image, p.processes)
	if matched {
		log.Printf("TUN bypass: configured process PID %d -> %s %s", pid, network, destination.NetAddr())
	}
	return matched, nil
}

func matchesBypassProcess(image string, configured []string) bool {
	image = strings.ReplaceAll(image, "\\", "/")
	for _, candidate := range configured {
		candidate = strings.ReplaceAll(candidate, "\\", "/")
		if strings.Contains(candidate, "/") {
			if strings.EqualFold(candidate, image) {
				return true
			}
		} else if strings.EqualFold(candidate, path.Base(image)) {
			return true
		}
	}
	return false
}

func socketEndpointMatches(bound, actual netip.AddrPort) bool {
	boundAddr, actualAddr := bound.Addr().WithZone("").Unmap(), actual.Addr().WithZone("").Unmap()
	return bound.Port() == actual.Port() && boundAddr.Is6() == actualAddr.Is6() &&
		(boundAddr.IsUnspecified() || boundAddr == actualAddr)
}

func socketOwner(rows []processSocket, source, remote netip.AddrPort, udp bool) (uint32, error) {
	var pid uint32
	for _, row := range rows {
		matches := socketEndpointMatches(row.local, source)
		if udp && source.Addr().Is4() && row.local.Addr().Is6() && row.local.Addr().IsUnspecified() && row.local.Port() == source.Port() {
			matches = true
		}
		if row.listener || row.pid == 0 || !matches {
			continue
		}
		if !udp && (row.remote.Port() != remote.Port() || row.remote.Addr().WithZone("").Unmap() != remote.Addr().WithZone("").Unmap()) {
			continue
		}
		if pid != 0 && pid != row.pid {
			return 0, fmt.Errorf("TUN bypass: ambiguous socket owner")
		}
		pid = row.pid
	}
	if pid == 0 {
		return 0, fmt.Errorf("TUN bypass: socket owner unavailable; flow rejected to prevent proxy loops")
	}
	return pid, nil
}

// OWNER_PID table layouts are fixed-width DWORD fields, independent of host
// pointer size. Addresses/ports are network order; counts/PIDs are little endian.
func parseProcessSocketTable(data []byte, udp, ipv6 bool) ([]processSocket, error) {
	size := 24
	if udp {
		size = 12
	}
	if ipv6 {
		if udp {
			size = 28
		} else {
			size = 56
		}
	}
	if len(data) < 4 {
		return nil, fmt.Errorf("truncated owner table")
	}
	count := uint64(binary.LittleEndian.Uint32(data))
	if count > uint64((len(data)-4)/size) {
		return nil, fmt.Errorf("truncated owner table rows")
	}
	rows := make([]processSocket, 0, int(count))
	for i := 0; i < int(count); i++ {
		row := data[4+i*size : 4+(i+1)*size]
		localOffset, localPort, remoteOffset, remotePort, pidOffset, stateOffset := 4, 8, 12, 16, 20, 0
		if udp {
			localOffset, localPort, pidOffset = 0, 4, 8
		}
		if ipv6 {
			localOffset, localPort = 0, 20
			if udp {
				pidOffset = 24
			} else {
				remoteOffset, remotePort, stateOffset, pidOffset = 24, 44, 48, 52
			}
		}
		addr := func(offset int) netip.Addr {
			if ipv6 {
				return netip.AddrFrom16([16]byte(row[offset : offset+16]))
			}
			return netip.AddrFrom4([4]byte(row[offset : offset+4]))
		}
		entry := processSocket{local: netip.AddrPortFrom(addr(localOffset), binary.BigEndian.Uint16(row[localPort:])), pid: binary.LittleEndian.Uint32(row[pidOffset:])}
		if !udp {
			entry.remote = netip.AddrPortFrom(addr(remoteOffset), binary.BigEndian.Uint16(row[remotePort:]))
			entry.listener = binary.LittleEndian.Uint32(row[stateOffset:]) == 2
		}
		rows = append(rows, entry)
	}
	return rows, nil
}
