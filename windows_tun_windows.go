//go:build windows

package main

import (
	"fmt"
	"os"
	"strings"

	"github.com/xtls/xray-core/core"
	"github.com/xtls/xray-core/proxy/tun"
)

var activeWindowsTun interface{}

func startWindowsTun(upstream UpStreamDial, configs []ServerConfigUpStream) error {
	name := os.Getenv("TCPROUTE_WINTUN_NAME")
	if name == "" {
		return nil
	}
	if len(name) > 64 || strings.ContainsAny(name, "\r\n\\/") {
		return fmt.Errorf("invalid Wintun adapter name")
	}
	bypass, err := newWindowsTunBypass(configs)
	if err != nil {
		return err
	}
	instance, err := core.New(&core.Config{})
	if err != nil {
		return err
	}
	if err := instance.AddFeature(&tunDispatcher{upstream: upstream, bypass: bypass.match}); err != nil {
		return err
	}
	handler, err := core.CreateObject(instance, &tun.Config{Name: name, MTU: 1500})
	if err != nil {
		return fmt.Errorf("start Wintun: %w", err)
	}
	activeWindowsTun = handler
	fmt.Println("TCPROUTE_WINTUN_READY")
	return nil
}
