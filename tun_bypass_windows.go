//go:build windows

package main

import (
	"encoding/json"
	"fmt"
	"log"
	"net"
	"net/netip"
	"os"
	"strings"
	"syscall"
	"unsafe"

	"golang.org/x/sys/windows"
)

var ipHelper = windows.NewLazySystemDLL("iphlpapi.dll")
var getExtendedTCPTable = ipHelper.NewProc("GetExtendedTcpTable")
var getExtendedUDPTable = ipHelper.NewProc("GetExtendedUdpTable")

func windowsProcessSockets(network string, ipv6 bool) ([]processSocket, error) {
	proc, tableClass := getExtendedTCPTable, uintptr(5) // TCP_TABLE_OWNER_PID_ALL
	if network == "udp" {
		proc, tableClass = getExtendedUDPTable, 1
	} // UDP_TABLE_OWNER_PID
	family := uintptr(windows.AF_INET)
	if ipv6 {
		family = windows.AF_INET6
	}
	size := uint32(16384)
	for attempts := 0; attempts < 5; attempts++ {
		if size > 64*1024*1024 {
			return nil, fmt.Errorf("owner table exceeds size limit")
		}
		data := make([]byte, size)
		status, _, _ := proc.Call(uintptr(unsafe.Pointer(&data[0])), uintptr(unsafe.Pointer(&size)), 0, family, tableClass, 0)
		if syscall.Errno(status) == windows.ERROR_INSUFFICIENT_BUFFER {
			continue
		}
		if status != 0 {
			return nil, syscall.Errno(status)
		}
		return parseProcessSocketTable(data, network == "udp", ipv6)
	}
	return nil, fmt.Errorf("owner table kept changing")
}

func windowsProcessPath(pid uint32) (string, error) {
	handle, err := windows.OpenProcess(windows.PROCESS_QUERY_LIMITED_INFORMATION, false, pid)
	if err != nil {
		return "", err
	}
	defer windows.CloseHandle(handle)
	buffer := make([]uint16, 32768)
	size := uint32(len(buffer))
	if err := windows.QueryFullProcessImageName(handle, 0, &buffer[0], &size); err != nil {
		return "", err
	}
	return windows.UTF16ToString(buffer[:size]), nil
}

func newWindowsTunBypass(configs []ServerConfigUpStream) (*tunBypassPolicy, error) {
	policy := &tunBypassPolicy{selfPID: uint32(os.Getpid()), table: windowsProcessSockets, processPath: windowsProcessPath}
	if raw := os.Getenv("TCPROUTE_TUN_BYPASS_RULES"); raw != "" {
		var err error
		policy.rules, err = parseTunBypassRules(raw)
		if err != nil {
			return nil, err
		}
	}
	if raw := os.Getenv("TCPROUTE_TUN_BYPASS_PROCESSES"); raw != "" {
		if err := json.Unmarshal([]byte(raw), &policy.processes); err != nil {
			return nil, fmt.Errorf("invalid TUN bypass process list: %w", err)
		}
		for _, name := range policy.processes {
			if strings.TrimSpace(name) != name || name == "" || strings.ContainsAny(name, "*?\r\n") {
				return nil, fmt.Errorf("invalid TUN bypass process name")
			}
		}
	}
	if !strings.EqualFold(os.Getenv("TCPROUTE_TUN_AUTO_BYPASS"), "false") {
		addresses, err := net.InterfaceAddrs()
		if err != nil {
			return nil, fmt.Errorf("enumerate local proxy addresses: %w", err)
		}
		local := map[netip.Addr]bool{}
		for _, address := range addresses {
			prefix, err := netip.ParsePrefix(address.String())
			if err == nil {
				local[prefix.Addr().Unmap()] = true
			}
		}
		policy.localProxies = localProxyEndpoints(configs, local)
	}
	log.Printf("TUN bypass: %d local proxy endpoints, %d explicit process entries, %d destination rules", len(policy.localProxies), len(policy.processes), len(policy.rules))
	return policy, nil
}
