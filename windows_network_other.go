//go:build !windows

package main

import (
	"fmt"
	"os"

	proxyclient "github.com/GameXG/ProxyClient"
)

func initializeWindowsNetwork() error {
	if os.Getenv("TCPROUTE_WINTUN_NAME") != "" {
		return fmt.Errorf("Wintun is only supported on Windows")
	}
	return nil
}

func bindUpstream(pc proxyclient.ProxyClient) (proxyclient.ProxyClient, error)    { return pc, nil }
func startWindowsTun(upstream UpStreamDial, configs []ServerConfigUpStream) error { return nil }
